using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AndroidOnDesktop.Core;

public sealed partial class RuntimeService : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _windowLock = new();
    private readonly List<AppSession> _sessions = [];
    private Process? _audioOwner;
    private Process? _emulator;
    private string? _ownedSerial;
    private EmulatorIdentity? _ownedIdentity;
    private bool _disposed;

    public RuntimeService(HostSettings settings) => Settings = settings;
    public HostSettings Settings { get; }
    public event Action<string>? Log;
    public event Action<AppSessionUpdate>? AppSessionChanged;
    public bool OwnsEmulator => _ownedSerial is not null;

    private void WriteLog(string text) => Log?.Invoke(text);

    public async Task<RuntimeDiagnostics> DetectAsync(CancellationToken cancellationToken = default)
    {
        ToolLocator.Discover(Settings);
        var messages = new List<string>();
        var adb = File.Exists(Settings.AdbPath);
        var emulator = File.Exists(Settings.EmulatorPath);
        var scrcpy = File.Exists(Settings.ScrcpyPath);
        var version = "";
        messages.Add(adb ? $"ADB：{Settings.AdbPath}" : "缺少 ADB，请配置 Android SDK platform-tools。");
        messages.Add(emulator ? $"Android Emulator：{Settings.EmulatorPath}" : "缺少 Android Emulator；本机运行需要另行安装模拟器和系统镜像。");
        if (emulator)
        {
            try
            {
                var acceleration = await ProcessRunner.RunAsync(Settings.EmulatorPath, ["-accel-check"], TimeSpan.FromSeconds(25),
                    cancellationToken, requireSuccess: false, environment: ToolLocator.EmulatorEnvironment(Settings.EmulatorPath));
                messages.Add("虚拟化加速检测：" + acceleration.Output);
                if (acceleration.ExitCode != 0) messages.Add("Android Emulator 尚未确认加速可用，请查看上述检测结果。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { messages.Add("虚拟化加速检测失败：" + ex.Message); }
        }
        if (scrcpy)
        {
            try
            {
                var result = await ProcessRunner.RunAsync(Settings.ScrcpyPath, ["--version"], TimeSpan.FromSeconds(15), cancellationToken);
                version = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                messages.Add(version);
                if (GetScrcpyMajor(result.Output) < 3) messages.Add("应用独立窗口需要 scrcpy 3.0 或更新版本。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { messages.Add(ex.Message); scrcpy = false; }
        }
        else messages.Add("缺少 scrcpy，暂时无法打开应用窗口。");
        return new(adb, emulator, scrcpy, version, messages);
    }

    public async Task<IReadOnlyList<AndroidDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await AdbAsync(["devices", "-l"], cancellationToken);
        var devices = new List<AndroidDevice>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*')) continue;
            var fields = Regex.Split(line, @"\s+", RegexOptions.CultureInvariant);
            if (fields.Length >= 2) devices.Add(new(fields[0], fields[1], string.Join(' ', fields.Skip(2))));
        }
        return devices;
    }

    public async Task<IReadOnlyList<string>> ListAvdsAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(Settings.EmulatorPath, ["-list-avds"], TimeSpan.FromSeconds(20), cancellationToken,
            environment: ToolLocator.EmulatorEnvironment(Settings.EmulatorPath));
        return result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("INFO", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public async Task StartEmulatorAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_ownedSerial is not null) throw new InvalidOperationException("本程序管理的 Android 运行环境已存在，请先停止后再启动。");
            Settings.ValidateEmulator();
            var serial = $"emulator-{Settings.EmulatorPort}";
            var adbPath = Settings.AdbPath;
            var avdName = Settings.AvdName.Trim();
            var devices = await GetDevicesAsync(cancellationToken);
            if (devices.Any(device => device.Serial == serial))
                throw new InvalidOperationException($"端口 {Settings.EmulatorPort} 已有模拟器。可在设备列表选择它，或更换端口；本程序不会接管已有模拟器。");
            EnsurePortAvailable(Settings.EmulatorPort);
            EnsurePortAvailable(Settings.EmulatorPort + 1);
            var avds = await ListAvdsAsync(cancellationToken);
            if (!avds.Contains(Settings.AvdName.Trim(), StringComparer.Ordinal))
                throw new InvalidOperationException($"未找到虚拟设备 {Settings.AvdName}。请先创建 AVD，再刷新列表。");
            var info = ProcessRunner.CreateStartInfo(Settings.EmulatorPath,
                ["-avd", Settings.AvdName.Trim(), "-port", Settings.EmulatorPort.ToString(), "-no-window", "-no-boot-anim", "-no-snapshot-save",
                    "-gpu", Settings.EmulatorGpuMode.Trim().ToLowerInvariant(), "-memory", Settings.EmulatorMemoryMb.ToString()]);
            foreach (var pair in ToolLocator.EmulatorEnvironment(Settings.EmulatorPath)) info.Environment[pair.Key] = pair.Value;
            _emulator = new Process { StartInfo = info };
            _emulator.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) WriteLog("[Android] " + e.Data); };
            _emulator.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) WriteLog("[Android] " + e.Data); };
            try { _emulator.Start(); }
            catch
            {
                _emulator.Dispose();
                _emulator = null;
                throw;
            }
            _emulator.BeginOutputReadLine();
            _emulator.BeginErrorReadLine();
            _ownedSerial = serial;
            Settings.DeviceSerial = serial;
            WriteLog($"正在启动 {Settings.AvdName}（{serial}），首次启动可能需要数分钟。");
            using var bootTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, bootTimeout.Token);
            try
            {
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (_emulator.HasExited && _emulator.ExitCode != 0)
                        throw new InvalidOperationException($"Android Emulator 已退出（{_emulator.ExitCode}），请查看日志并检查虚拟化支持。");
                    var boot = await AdbAsync(["-s", serial, "shell", "getprop", "sys.boot_completed"], linked.Token, requireSuccess: false);
                    if (boot.ExitCode == 0 && boot.StandardOutput.Trim() == "1") break;
                    await Task.Delay(1500, linked.Token);
                }
                _ownedIdentity = await ReadEmulatorIdentityAsync(adbPath, serial, avdName, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await StopOwnedCoreAsync();
                throw new TimeoutException("Android 启动超过 4 分钟。请查看运行日志、加速支持和系统镜像配置。");
            }
            catch
            {
                await StopOwnedCoreAsync();
                throw;
            }
            WriteLog($"Android 已启动：{serial}");
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopOwnedEmulatorAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try { await StopOwnedCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopOwnedCoreAsync()
    {
        if (_ownedSerial is null) return;
        var serial = _ownedSerial;
        await CloseSessionsAsync(serial);
        try
        {
            if (_ownedIdentity is { } expected)
            {
                // Use the captured connection identity, never mutable UI settings. A reused
                // ADB serial or a restarted AVD must not receive the old session's stop command.
                var devices = await ProcessRunner.RunAsync(expected.AdbPath, ["devices"], TimeSpan.FromSeconds(5));
                var connected = devices.StandardOutput.Split('\n', StringSplitOptions.TrimEntries)
                    .Any(line => Regex.IsMatch(line, "^" + Regex.Escape(serial) + @"\s+device(?:\s|$)", RegexOptions.CultureInvariant));
                if (connected)
                {
                    var current = await ReadEmulatorIdentityAsync(expected.AdbPath, expected.Serial, expected.AvdName, CancellationToken.None);
                    if (!SameDirectory(current.AvdPath, expected.AvdPath) || current.BootId != expected.BootId)
                        throw new InvalidOperationException("Android 实例已被替换或重新启动，未停止其他实例。");
                    await ProcessRunner.RunAsync(expected.AdbPath, ["-s", expected.Serial, "emu", "kill"], TimeSpan.FromSeconds(10));
                }
            }
            else if (_emulator is not null)
            {
                // Startup was cancelled before Android exposed an identity. Only the exact
                // process tree launched by this service can be cleaned up in that case.
                ProcessRunner.TryKill(_emulator);
            }

            if (_emulator is not null)
            {
                try
                {
                    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _emulator.WaitForExitAsync(exitTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    ProcessRunner.TryKill(_emulator);
                }
            }
            if (!await WaitForEmulatorPortClosedAsync(serial, TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Android 停止请求已发送，但运行环境尚未退出，请查看日志后重试停止。");
        }
        catch (Exception ex)
        {
            WriteLog("停止运行环境失败：" + ex.Message);
            throw;
        }
        _emulator?.Dispose();
        _emulator = null;
        _ownedSerial = null;
        _ownedIdentity = null;
        WriteLog("已停止本程序管理的 Android 运行环境：" + serial);
    }

    private static async Task<bool> WaitForEmulatorPortClosedAsync(string serial, TimeSpan timeout)
    {
        var port = int.Parse(serial.AsSpan("emulator-".Length));
        using var deadline = new CancellationTokenSource(timeout);
        while (!deadline.IsCancellationRequested)
        {
            // Inspect listeners only: probing an unused port may time out before Windows
            // reports refusal, while closing connections can remain in TIME_WAIT.
            if (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port))
                return true;
            try { await Task.Delay(200, deadline.Token); }
            catch (OperationCanceledException) { }
        }
        return false;
    }

    public async Task<IReadOnlyList<AndroidApp>> ListAppsAsync(CancellationToken cancellationToken = default)
    {
        var serial = await RequireDeviceAsync(cancellationToken);
        var result = await AdbAsync(["-s", serial, "shell", "pm", "list", "packages", "-3"], cancellationToken);
        var packages = result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("package:", StringComparison.Ordinal)).Select(line => line[8..].Trim())
            .Where(IsPackageName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var apps = new List<AndroidApp>();
        foreach (var package in packages)
        {
            var resolve = await AdbAsync(["-s", serial, "shell", "cmd", "package", "resolve-activity", "--brief", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", package], cancellationToken, false);
            if (resolve.ExitCode == 0 && resolve.StandardOutput.Split('\n').Any(line => line.Trim().StartsWith(package + "/", StringComparison.Ordinal)))
                apps.Add(new(package));
        }
        WriteLog($"找到 {apps.Count} 个可启动的已安装应用。");
        return apps;
    }

    public async Task InstallApkAsync(string apkPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(apkPath);
        if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".apk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择一个存在的 .apk 文件。");
        var serial = await RequireDeviceAsync(cancellationToken);
        WriteLog("正在安装 APK：" + Path.GetFileName(fullPath));
        var result = await AdbAsync(["-s", serial, "install", "-r", fullPath], cancellationToken, true, TimeSpan.FromMinutes(5));
        if (!result.StandardOutput.Split('\n').Any(line => line.Trim() == "Success"))
            throw new InvalidOperationException("APK 安装未返回成功结果：" + result.Output);
        WriteLog("APK 安装完成：" + Path.GetFileName(fullPath));
    }

    public Task LaunchAppAsync(string packageName, CancellationToken cancellationToken = default) =>
        LaunchMonitoredAppAsync(packageName, cancellationToken);

    private async Task<string> RequireDeviceAsync(CancellationToken cancellationToken)
    {
        var serial = Settings.DeviceSerial.Trim();
        if (string.IsNullOrWhiteSpace(serial))
            throw new InvalidOperationException("请先启动本机 Android，或从设备列表明确选择一个设备。");
        var device = (await GetDevicesAsync(cancellationToken)).FirstOrDefault(device => device.Serial == serial);
        if (device is null || device.State != "device")
            throw new InvalidOperationException($"设备 {serial} 尚未就绪（{device?.State ?? "未连接"}）。请刷新设备并等待系统启动。");
        return serial;
    }

    private Task<ProcessResult> AdbAsync(IEnumerable<string> arguments, CancellationToken cancellationToken,
        bool requireSuccess = true, TimeSpan? timeout = null) =>
        ProcessRunner.RunAsync(Settings.AdbPath, arguments, timeout ?? TimeSpan.FromSeconds(20), cancellationToken, requireSuccess);

    private static int GetScrcpyMajor(string output)
    {
        var match = Regex.Match(output, @"scrcpy\s+(\d+)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var major) ? major : 0;
    }

    private static bool IsAlive(Process? process)
    {
        if (process is null) return false;
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsPackageName(string value) => Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant);

    private static void EnsurePortAvailable(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        catch (SocketException) { throw new InvalidOperationException($"模拟器端口 {port} 已被占用，请更换端口。"); }
        finally { listener.Stop(); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await CloseSessionsAsync();
            await StopOwnedEmulatorAsync();
            _lifecycle.Dispose();
        }
        catch
        {
            // A failed stop keeps ownership so the desktop can report it and retry.
            _disposed = false;
            throw;
        }
    }
}
