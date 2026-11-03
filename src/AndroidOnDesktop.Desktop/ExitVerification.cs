using System.IO;
using System.Text.Json;
using AndroidOnDesktop.Core;

namespace AndroidOnDesktop.Desktop;

internal static class ExitVerification
{
    internal static async Task<int> RunAsync(MainWindow window, HostSettings settings, string mode, string reportPath)
    {
        var started = DateTimeOffset.Now;
        ExitProbeResult? probe = null;
        Exception? failure = null;
        var deviceRemoved = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            probe = await window.VerifyExitForSmokeAsync(mode, timeout.Token);
            await using var observer = new RuntimeService(settings);
            using var removalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!removalTimeout.IsCancellationRequested)
            {
                if ((await observer.GetDevicesAsync(removalTimeout.Token)).All(device => device.Serial != settings.DeviceSerial))
                {
                    deviceRemoved = true;
                    break;
                }
                await Task.Delay(250, removalTimeout.Token);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (window.IsLoaded)
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Close();
                try { await closed.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (TimeoutException ex) { failure ??= ex; }
            }
        }
        var success = failure is null && deviceRemoved && probe is { CleanupCompletedBeforeClose: true, ManagementReleased: true };
        var fullReportPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullReportPath)!);
        await File.WriteAllTextAsync(fullReportPath, JsonSerializer.Serialize(new
        {
            success, started, finished = DateTimeOffset.Now, probe, deviceRemoved,
            selectedDeviceSerial = settings.DeviceSerial, logFile = window.SessionLogPath,
            error = failure?.ToString(),
            scope = "正常窗口关闭、重复关闭请求、启动取消和对应 Android 退出的必要检查；未验证强制结束、系统崩溃或全面业务。"
        }, new JsonSerializerOptions { WriteIndented = true }));
        return success ? 0 : 1;
    }
}
