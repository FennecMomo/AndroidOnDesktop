using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AndroidOnDesktop.Core;

namespace AndroidOnDesktop.Desktop;

internal static class RuntimeSmoke
{
    private const string PackageName = "org.androidondesktop.smoke";

    internal static async Task<int> RunAsync(HostSettings settings, string apkPath, string reportPath)
    {
        var started = DateTimeOffset.Now;
        var log = new List<string>();
        var logLock = new object();
        var cleanupErrors = new List<string>();
        var observedWindowIds = new HashSet<int>();
        RuntimeDiagnostics? diagnostics = null;
        NativeWindow? nativeWindow = null;
        Exception? failure = null;
        var booted = false;
        var installed = false;
        var packageFound = false;
        var windowRequested = false;
        string? ownedStartedSerial = null;
        var stage = "detect";
        string[] appPackages = [];
        var runtime = new RuntimeService(settings);

        void Append(string message)
        {
            lock (logLock)
            {
                // Capture only a serial the current service actually claimed after starting a process.
                // This also covers a start that later fails and cleans itself up before returning.
                if (runtime.OwnsEmulator && !string.IsNullOrWhiteSpace(settings.DeviceSerial))
                    ownedStartedSerial ??= settings.DeviceSerial;
                log.Add($"[{DateTimeOffset.Now:O}] {message}");
            }
        }

        runtime.Log += Append;
        try
        {
            Append("开始实际 Android 运行链路冒烟检查。");
            diagnostics = await runtime.DetectAsync();
            foreach (var message in diagnostics.Messages) Append(message);
            if (!diagnostics.AdbAvailable || !diagnostics.EmulatorAvailable || !diagnostics.ScrcpyAvailable)
                throw new InvalidOperationException("运行组件不完整，不能执行真实 APK 冒烟检查。");

            stage = "boot";
            await runtime.StartEmulatorAsync();
            booted = true;
            ownedStartedSerial ??= settings.DeviceSerial;

            stage = "install";
            await runtime.InstallApkAsync(apkPath);
            installed = true;

            stage = "list-apps";
            var apps = await runtime.ListAppsAsync();
            appPackages = apps.Select(app => app.PackageName).ToArray();
            packageFound = appPackages.Contains(PackageName, StringComparer.Ordinal);
            if (!packageFound) throw new InvalidOperationException("已安装应用列表没有找到冒烟应用：" + PackageName);

            stage = "launch-window";
            var existingIds = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(settings.ScrcpyPath))
                .Select(process => { using (process) return process.Id; }).ToHashSet();
            await runtime.LaunchAppAsync(PackageName);
            windowRequested = true;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(settings.ScrcpyPath)))
                {
                    using (process)
                    {
                        if (existingIds.Contains(process.Id)) continue;
                        try
                        {
                            process.Refresh();
                            if (process.HasExited) continue;
                            var executable = process.MainModule?.FileName;
                            if (executable is null || !SamePath(executable, settings.ScrcpyPath)) continue;
                            observedWindowIds.Add(process.Id);
                            var handle = process.MainWindowHandle;
                            var title = process.MainWindowTitle;
                            if (handle == IntPtr.Zero || !title.Contains(PackageName, StringComparison.Ordinal)) continue;
                            nativeWindow = new(process.Id, executable, title, "0x" + handle.ToInt64().ToString("X"));
                            break;
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception ex)
                        {
                            Append($"读取新 scrcpy 进程 {process.Id} 的窗口信息失败：{ex.Message}");
                        }
                    }
                }
                if (nativeWindow is not null) break;
                await Task.Delay(250);
            }
            if (nativeWindow is null)
                throw new TimeoutException("20 秒内没有发现新 scrcpy 进程创建的预期应用原生窗口。");
            stage = "native-window-ready";
            Append($"已确认原生应用窗口：PID={nativeWindow.ProcessId} HWND={nativeWindow.Handle} Title={nativeWindow.Title}");
        }
        catch (Exception ex)
        {
            failure = ex;
            Append($"冒烟检查在 {stage} 阶段失败：{ex}");
        }
        finally
        {
            try
            {
                await runtime.DisposeAsync();
                Append("已完成自有窗口和 Android 运行环境清理。");
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex.ToString());
                Append("清理失败：" + ex);
            }
        }

        bool? androidDeviceRemoved = null;
        string? remainingOwnedSerial = null;
        if (!string.IsNullOrWhiteSpace(ownedStartedSerial))
        {
            androidDeviceRemoved = false;
            remainingOwnedSerial = ownedStartedSerial;
            try
            {
                // A separate service only observes ADB here; it never claims or stops a device.
                await using var observer = new RuntimeService(settings);
                using var removalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    var devices = await observer.GetDevicesAsync(removalTimeout.Token);
                    if (devices.All(device => device.Serial != ownedStartedSerial))
                    {
                        androidDeviceRemoved = true;
                        remainingOwnedSerial = null;
                        Append("已通过 ADB 确认本次启动的 Android 设备已移除：" + ownedStartedSerial);
                        break;
                    }
                    await Task.Delay(250, removalTimeout.Token);
                }
            }
            catch (OperationCanceledException)
            {
                cleanupErrors.Add("清理后 10 秒内未确认 Android 设备已移除：" + ownedStartedSerial);
                Append(cleanupErrors[^1]);
            }
            catch (Exception ex)
            {
                cleanupErrors.Add("无法通过 ADB 确认本次 Android 设备已移除：" + ex.Message);
                Append(cleanupErrors[^1]);
            }
        }

        var remainingWindowIds = new List<int>();
        foreach (var id in observedWindowIds)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                // Kill() is asynchronous; give only the known window process a short time to finish.
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(exitTimeout.Token); }
                catch (OperationCanceledException) { }
                process.Refresh();
                if (!process.HasExited && process.MainModule?.FileName is string executable && SamePath(executable, settings.ScrcpyPath))
                    remainingWindowIds.Add(id);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception ex) { cleanupErrors.Add($"无法确认窗口进程 {id} 已退出：{ex.Message}"); }
        }

        var success = failure is null && cleanupErrors.Count == 0 && remainingWindowIds.Count == 0 &&
            !runtime.OwnsEmulator && androidDeviceRemoved is not false;
        var report = new
        {
            success,
            started,
            finished = DateTimeOffset.Now,
            stage,
            apkPath = Path.GetFullPath(apkPath),
            expectedPackage = PackageName,
            selectedDeviceSerial = settings.DeviceSerial,
            stages = new { androidBooted = booted, apkInstalled = installed, packageFound, windowRequested, nativeWindowReady = nativeWindow is not null },
            diagnostics,
            installedAppPackages = appPackages,
            nativeWindow,
            cleanup = new { serviceOwnsEmulator = runtime.OwnsEmulator, ownedStartedSerial, androidDeviceRemoved, remainingOwnedSerial, observedWindowProcessIds = observedWindowIds.ToArray(), remainingWindowProcessIds = remainingWindowIds.ToArray(), errors = cleanupErrors.ToArray() },
            error = failure?.ToString(),
            scope = "实际 Android 启动、APK 安装、可启动应用枚举、原生窗口创建和自有进程清理。未执行点击、输入、画面效果、业务验收或全面测试。"
        };
        var fullReportPath = Path.GetFullPath(reportPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullReportPath)!);
            string[] logLines;
            lock (logLock) logLines = log.ToArray();
            await File.WriteAllLinesAsync(Path.ChangeExtension(fullReportPath, ".log"), logLines);
            await File.WriteAllTextAsync(fullReportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            return 1;
        }
        return success ? 0 : 1;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed record NativeWindow(int ProcessId, string ExecutablePath, string Title, string Handle);
}
