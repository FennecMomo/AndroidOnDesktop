using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AndroidOnDesktop.Core;

public sealed partial class RuntimeService
{
    private async Task LaunchMonitoredAppAsync(string packageName, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!IsPackageName(packageName)) throw new InvalidOperationException("应用包名格式不正确。");
        Settings.ValidateDisplay();
        Settings.ValidateAppStartup();
        var serial = await RequireDeviceAsync(cancellationToken);
        var adbPath = Settings.AdbPath;
        var version = await ProcessRunner.RunAsync(Settings.ScrcpyPath, ["--version"], TimeSpan.FromSeconds(15), cancellationToken);
        if (GetScrcpyMajor(version.Output) < 3) throw new InvalidOperationException("独立应用窗口需要 scrcpy 3.0 或更新版本。");
        var sdk = await AdbAsync(["-s", serial, "shell", "getprop", "ro.build.version.sdk"], cancellationToken);
        if (!int.TryParse(sdk.StandardOutput.Trim(), out var api) || api < 29)
            throw new InvalidOperationException("独立应用窗口需要 Android 10（API 29）或更新版本。");
        var help = await ProcessRunner.RunAsync(Settings.ScrcpyPath, ["--help"], TimeSpan.FromSeconds(15), cancellationToken);
        var arguments = new List<string>
        {
            "--serial=" + serial,
            "--new-display=" + Settings.DisplaySize.Trim() + "/" + Settings.DisplayDpi,
            "--start-app=" + packageName,
            "--window-title=" + packageName,
            "--video-codec=h264",
            "--video-bit-rate=" + Settings.VideoBitRateMbps + "M",
            "--max-fps=" + Settings.VideoMaxFps
        };
        bool audio;
        lock (_windowLock) audio = Settings.EnableAudio && api >= 30 && !IsAlive(_audioOwner);
        if (!audio) arguments.Add("--no-audio");
        if (help.Output.Contains("--display-ime-policy", StringComparison.Ordinal)) arguments.Add("--display-ime-policy=local");
        if (help.Output.Contains("--keep-active", StringComparison.Ordinal)) arguments.Add("--keep-active");
        if (Settings.ResizeDisplayWithWindow && GetScrcpyMajor(version.Output) >= 5 && help.Output.Contains("--flex-display", StringComparison.Ordinal)) arguments.Add("--flex-display");
        var info = ProcessRunner.CreateStartInfo(Settings.ScrcpyPath, arguments);
        info.Environment["ADB"] = adbPath;
        var session = new AppSession(packageName, serial, adbPath, new Process { StartInfo = info }, Settings.AppStartupTimeoutSeconds);
        session.Process.OutputDataReceived += (_, e) => ReceiveSessionOutput(session, e.Data);
        session.Process.ErrorDataReceived += (_, e) => ReceiveSessionOutput(session, e.Data);
        PublishSession(session, AppSessionState.Starting, $"正在启动应用并等待画面就绪（最多 {session.StartupTimeoutSeconds} 秒）：{packageName}");
        try
        {
            // Snapshot historical exits before --start-app. Old crashes must never
            // be mistaken for a failure of this new application window.
            if (api >= 30)
            {
                var baseline = await ReadExitHistoryAsync(session, cancellationToken);
                session.ExitInfoSupported = baseline.Supported;
                session.KnownCrashKeys.UnionWith(baseline.Crashes.Select(crash => crash.Key));
            }
            if (!session.ExitInfoSupported)
                session.KnownCrashKeys.UnionWith((await ReadCrashBufferAsync(session, cancellationToken)).Select(crash => crash.Key));
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            session.Process.Start();
            session.Process.BeginOutputReadLine();
            session.Process.BeginErrorReadLine();
            lock (_windowLock)
            {
                _sessions.Add(session);
                if (audio) _audioOwner = session.Process;
            }
            session.Monitor = MonitorSessionAsync(session);
            await session.Ready.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await CloseSessionAsync(session);
            throw;
        }
        catch (Exception ex)
        {
            if (!session.IsTerminal)
                FailSession(session, ex.Message, ex.ToString());
            if (session.Monitor is null) session.Process.Dispose();
            throw;
        }
    }

    private void ReceiveSessionOutput(AppSession session, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (session.Sync)
        {
            session.Output.Enqueue(line);
            while (session.Output.Count > 18) session.Output.Dequeue();
            var display = Regex.Match(line, @"New display:.*\(id=(\d+)\)", RegexOptions.CultureInvariant);
            if (display.Success && int.TryParse(display.Groups[1].Value, out var id)) session.DisplayId = id;
            if (line.Contains("ERROR:", StringComparison.OrdinalIgnoreCase) || line.Contains("Exception", StringComparison.Ordinal))
                session.LastWindowError = line;
        }
        WriteLog($"[窗口 {session.PackageName}] {line}");
    }

    private async Task MonitorSessionAsync(AppSession session)
    {
        var started = Stopwatch.StartNew();
        var connectionFailures = 0;
        try
        {
            while (!session.Stop.IsCancellationRequested)
            {
                if (!IsAlive(session.Process))
                {
                    var exitCode = session.Process.ExitCode;
                    if (exitCode != 0)
                        FailSession(session, $"应用窗口异常退出（{exitCode}）：{session.PackageName}", SessionOutput(session));
                    else
                    {
                        PublishSession(session, AppSessionState.Closed, "应用窗口已关闭：" + session.PackageName);
                        session.Ready.TrySetException(new InvalidOperationException("应用窗口在就绪前已关闭。"));
                    }
                    break;
                }
                try
                {
                    var crashes = session.ExitInfoSupported
                        ? (await ReadExitHistoryAsync(session, session.Stop.Token)).Crashes
                        : await ReadCrashBufferAsync(session, session.Stop.Token);
                    var newCrash = crashes.FirstOrDefault(crash => !session.KnownCrashKeys.Contains(crash.Key));
                    if (newCrash is not null)
                    {
                        var hint = newCrash.Native
                            ? "应用发生原生崩溃，可能与当前运行环境或 ARM 兼容有关。"
                            : "应用发生异常并已退出。";
                        var detail = newCrash.Detail;
                        if (newCrash.Native && session.ExitInfoSupported)
                            detail += await ReadNativeCrashSummaryAsync(session, newCrash);
                        FailSession(session, $"{session.PackageName}：{hint}", detail);
                        break;
                    }
                    if (!session.Ready.Task.IsCompleted)
                    {
                        var pid = await SessionAdbAsync(session, ["shell", "pidof", session.PackageName], session.Stop.Token);
                        if (pid.ExitCode == 0 && !string.IsNullOrWhiteSpace(pid.StandardOutput))
                        {
                            var activities = await SessionAdbAsync(session, ["shell", "dumpsys", "activity", "activities"], session.Stop.Token);
                            int? displayId;
                            lock (session.Sync) displayId = session.DisplayId;
                            if (displayId is not null && IsActivityReady(activities.StandardOutput, session.PackageName, displayId.Value))
                            {
                                PublishSession(session, AppSessionState.Running, "应用已启动：" + session.PackageName);
                                session.Ready.TrySetResult();
                            }
                        }
                        if (!session.Ready.Task.IsCompleted && started.Elapsed > TimeSpan.FromSeconds(session.StartupTimeoutSeconds))
                        {
                            FailSession(session, $"应用在 {session.StartupTimeoutSeconds} 秒内没有进入可见运行状态：{session.PackageName}",
                                "未同时检测到应用进程、目标显示器中的 RESUMED Activity 以及首次绘制。" + Environment.NewLine + SessionOutput(session));
                            break;
                        }
                    }
                    connectionFailures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    connectionFailures++;
                    WriteLog($"[监测 {session.PackageName}] {ex.Message}");
                    if (connectionFailures >= 3)
                    {
                        FailSession(session, "与 Android 的连接中断，无法继续确认应用状态：" + session.PackageName, ex.ToString());
                        break;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), session.Stop.Token);
            }
        }
        catch (OperationCanceledException) when (session.Stop.IsCancellationRequested) { }
        catch (Exception ex) { FailSession(session, "应用状态监测失败：" + session.PackageName, ex.ToString()); }
        finally
        {
            if (session.Stop.IsCancellationRequested && !session.IsTerminal)
                PublishSession(session, AppSessionState.Closed, "应用窗口已关闭：" + session.PackageName);
            if (!session.Ready.Task.IsCompleted)
                session.Ready.TrySetCanceled();
            lock (_windowLock)
                if (ReferenceEquals(_audioOwner, session.Process)) _audioOwner = null;
        }
    }

    private static bool IsActivityReady(string output, string packageName, int displayId)
    {
        var displays = Regex.Matches(output, @"(?m)^Display #(\d+)[^\r\n]*", RegexOptions.CultureInvariant);
        for (var i = 0; i < displays.Count; i++)
        {
            if (displays[i].Groups[1].Value != displayId.ToString()) continue;
            var start = displays[i].Index;
            var end = i + 1 < displays.Count ? displays[i + 1].Index : output.Length;
            var body = output[start..end];
            foreach (var activity in Regex.Split(body, @"(?m)(?=^[ \t]*\* Hist[ \t]+#\d+:)"))
            {
                var header = activity.Split('\n', 2)[0];
                if (!Regex.IsMatch(header, @"\b" + Regex.Escape(packageName) + @"/", RegexOptions.CultureInvariant)) continue;
                if (activity.Contains("state=RESUMED", StringComparison.Ordinal) &&
                    (activity.Contains("firstWindowDrawn=true", StringComparison.Ordinal) || activity.Contains("reportedDrawn=true", StringComparison.Ordinal)) &&
                    activity.Contains("mVisibleRequested=true", StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    private static async Task<ExitHistory> ReadExitHistoryAsync(AppSession session, CancellationToken ct)
    {
        var result = await SessionAdbAsync(session, ["shell", "dumpsys", "activity", "exit-info", session.PackageName], ct);
        var supported = result.ExitCode == 0 && result.StandardOutput.Contains("PROCESS EXIT INFO", StringComparison.OrdinalIgnoreCase);
        var crashes = new List<AppCrash>();
        if (supported)
        {
            foreach (var block in Regex.Split(result.StandardOutput, @"ApplicationExitInfo #\d+:"))
            {
                var identity = Regex.Match(block, @"timestamp=([^\r\n]+?)\s+pid=(\d+)", RegexOptions.CultureInvariant);
                var process = Regex.Match(block, @"process=(\S+)\s+reason=(\d+)\s+\(([^\r\n]+)\)", RegexOptions.CultureInvariant);
                if (!identity.Success || !process.Success) continue;
                var processName = process.Groups[1].Value;
                // Auxiliary service/sandbox crashes are not proof that the main
                // application window failed. Monitor the exact main process.
                if (processName != session.PackageName) continue;
                var reason = process.Groups[2].Value;
                if (reason is not ("4" or "5")) continue;
                var detail = $"应用主进程：{processName}；退出原因：{reason}（{(reason == "5" ? "APP CRASH(NATIVE)" : "APP CRASH(EXCEPTION)")}）；" +
                    $"时间：{identity.Groups[1].Value}；PID：{identity.Groups[2].Value}" + Environment.NewLine +
                    string.Join(Environment.NewLine, block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(16));
                int.TryParse(identity.Groups[2].Value, out var pid);
                var parsedTime = DateTime.TryParse(identity.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp);
                crashes.Add(new(identity.Groups[1].Value + "/" + identity.Groups[2].Value + "/" + reason,
                    reason == "5", detail, pid > 0 ? pid : null, parsedTime ? timestamp : null));
            }
        }
        return new(supported, crashes);
    }

    private static async Task<IReadOnlyList<AppCrash>> ReadCrashBufferAsync(AppSession session, CancellationToken ct)
    {
        var result = await SessionAdbAsync(session, ["logcat", "-b", "crash", "-d", "-v", "threadtime"], ct);
        var lines = result.StandardOutput.Split('\n');
        var crashes = new List<AppCrash>();
        for (var i = 0; i < lines.Length; i++)
        {
            var native = lines[i].Contains(">>> " + session.PackageName + " <<<", StringComparison.Ordinal) ||
                (lines[i].Contains("Fatal signal", StringComparison.Ordinal) && lines[i].Contains("(" + session.PackageName + ")", StringComparison.Ordinal));
            var javaProcess = Regex.Match(lines[i], @"Process:\s*([^,\s]+),\s*PID:", RegexOptions.CultureInvariant);
            var java = lines[i].Contains("AndroidRuntime", StringComparison.Ordinal) && javaProcess.Success &&
                javaProcess.Groups[1].Value == session.PackageName;
            if (!native && !java) continue;
            crashes.Add(new(lines[i].Trim(), native, string.Join(Environment.NewLine, lines.Skip(i).Take(20))));
        }
        return crashes;
    }

    private static async Task<string> ReadNativeCrashSummaryAsync(AppSession session, AppCrash crash)
    {
        // exit-info has already confirmed this new main-process crash. Fetch a
        // short diagnostic excerpt once, without clearing or polling logcat.
        if (crash.ProcessId is not int pid || crash.Timestamp is not DateTime timestamp)
            return Environment.NewLine + "退出记录没有可核对的 PID / 时间，未附加可能属于历史崩溃的日志。";
        try
        {
            var result = await ProcessRunner.RunAsync(session.AdbPath,
                ["-s", session.Serial, "logcat", "-b", "crash", "-d", "-v", "threadtime"],
                TimeSpan.FromSeconds(3), session.Stop.Token, requireSuccess: false);
            if (result.ExitCode != 0) return Environment.NewLine + "已确认原生崩溃；读取原生日志摘要未成功。";
            var lines = result.StandardOutput.Split('\n');
            var fullHeader = new Regex(@"\bpid:\s*" + pid + @"\b.*>>>\s*" + Regex.Escape(session.PackageName) + @"\s*<<<", RegexOptions.CultureInvariant);
            var signalHeader = new Regex(@"Fatal signal.*\bpid\s+" + pid + @"\s+\(" + Regex.Escape(session.PackageName) + @"\)", RegexOptions.CultureInvariant);
            bool MatchesCurrentCrash(string line, Regex marker)
            {
                if (!marker.IsMatch(line)) return false;
                var logTime = Regex.Match(line, @"^(\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d+)", RegexOptions.CultureInvariant);
                return logTime.Success && DateTime.TryParse(timestamp.Year + "-" + logTime.Groups[1].Value,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var loggedAt) &&
                    Math.Abs((loggedAt - timestamp).TotalSeconds) <= 30;
            }
            var start = Array.FindLastIndex(lines, line => MatchesCurrentCrash(line, fullHeader));
            if (start < 0) start = Array.FindLastIndex(lines, line => MatchesCurrentCrash(line, signalHeader));
            if (start < 0) return Environment.NewLine + "已确认原生崩溃；crash buffer 中尚无与本次 PID、包名和时间相符的原生堆栈。";
            var excerpt = new List<string>();
            for (var i = start; i < lines.Length && excerpt.Count < 24; i++)
            {
                if (i > start && Regex.IsMatch(lines[i], @"\bpid:\s*\d+\b.*>>>", RegexOptions.CultureInvariant)) break;
                if (!string.IsNullOrWhiteSpace(lines[i])) excerpt.Add(lines[i].TrimEnd());
            }
            return Environment.NewLine + "本次原生崩溃日志摘要：" + Environment.NewLine + string.Join(Environment.NewLine, excerpt);
        }
        catch (Exception ex)
        {
            // A diagnostic read timeout must never hide the confirmed crash.
            return Environment.NewLine + "已确认原生崩溃；原生日志摘要读取未完成：" + ex.Message;
        }
    }

    private static async Task<ProcessResult> SessionAdbAsync(AppSession session, IEnumerable<string> arguments, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(session.AdbPath, new[] { "-s", session.Serial }.Concat(arguments),
            TimeSpan.FromSeconds(12), ct, requireSuccess: false);
        if (result.ExitCode != 0 && Regex.IsMatch(result.Output,
                @"device.*(?:not found|offline|unauthorized)|no devices/emulators|cannot connect|protocol fault|connection (?:closed|reset)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Android 设备连接异常：" + result.Output);
        return result;
    }

    private void PublishSession(AppSession session, AppSessionState state, string message, string? detail = null)
    {
        lock (session.Sync)
        {
            if (session.IsTerminal) return;
            session.State = state;
            if (state is AppSessionState.Failed or AppSessionState.Closed) session.IsTerminal = true;
        }
        WriteLog(message + (string.IsNullOrWhiteSpace(detail) ? "" : Environment.NewLine + detail));
        AppSessionChanged?.Invoke(new(session.Id, session.PackageName, session.Serial, state, message, detail));
    }

    private void FailSession(AppSession session, string message, string? detail)
    {
        PublishSession(session, AppSessionState.Failed, message, detail);
        session.Ready.TrySetException(new InvalidOperationException(message));
        ProcessRunner.TryKill(session.Process);
    }

    private static string SessionOutput(AppSession session)
    {
        lock (session.Sync) return string.Join(Environment.NewLine, session.Output);
    }

    private async Task CloseSessionAsync(AppSession session)
    {
        session.Stop.Cancel();
        ProcessRunner.TryKill(session.Process);
        if (session.Monitor is not null)
            try { await session.Monitor; } catch (OperationCanceledException) { }
        else
            PublishSession(session, AppSessionState.Closed, "应用启动已取消：" + session.PackageName);
        session.Ready.TrySetCanceled();
    }

    private async Task CloseSessionsAsync(string? serial = null)
    {
        AppSession[] sessions;
        lock (_windowLock)
        {
            sessions = _sessions.Where(session => serial is null || session.Serial == serial).ToArray();
            foreach (var session in sessions) _sessions.Remove(session);
        }
        foreach (var session in sessions)
        {
            await CloseSessionAsync(session);
            session.Process.Dispose();
            session.Stop.Dispose();
        }
    }

    private sealed class AppSession(string packageName, string serial, string adbPath, Process process, int startupTimeoutSeconds)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string PackageName { get; } = packageName;
        public string Serial { get; } = serial;
        public string AdbPath { get; } = adbPath;
        public Process Process { get; } = process;
        public int StartupTimeoutSeconds { get; } = startupTimeoutSeconds;
        public object Sync { get; } = new();
        public Queue<string> Output { get; } = new();
        public HashSet<string> KnownCrashKeys { get; } = new(StringComparer.Ordinal);
        public CancellationTokenSource Stop { get; } = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Monitor { get; set; }
        public int? DisplayId { get; set; }
        public string? LastWindowError { get; set; }
        public bool ExitInfoSupported { get; set; }
        public AppSessionState State { get; set; }
        public bool IsTerminal { get; set; }
    }

    private sealed record AppCrash(string Key, bool Native, string Detail, int? ProcessId = null, DateTime? Timestamp = null);
    private sealed record ExitHistory(bool Supported, IReadOnlyList<AppCrash> Crashes);
}
