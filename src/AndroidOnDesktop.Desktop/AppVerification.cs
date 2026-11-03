using System.IO;
using System.Text.Json;
using AndroidOnDesktop.Core;

namespace AndroidOnDesktop.Desktop;

internal static class AppVerification
{
    internal static async Task<int> RunAsync(HostSettings settings, string package, int seconds, string expected, string reportPath)
    {
        var log = new List<string>();
        var updates = new List<AppSessionUpdate>();
        var gate = new object();
        var started = DateTimeOffset.Now;
        Exception? failure = null;
        var ready = false;
        var crash = new TaskCompletionSource<AppSessionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var runtime = new RuntimeService(settings))
        {
            runtime.Log += line => { lock (gate) log.Add($"[{DateTimeOffset.Now:O}] {line}"); };
            runtime.AppSessionChanged += update =>
            {
                lock (gate) updates.Add(update);
                if (update.State == AppSessionState.Failed) crash.TrySetResult(update);
            };
            try
            {
                await runtime.LaunchAppAsync(package);
                ready = true;
                await Task.WhenAny(crash.Task, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120))));
            }
            catch (Exception ex) { failure = ex; }
        }
        var passed = expected == "crash" ? crash.Task.IsCompletedSuccessfully : ready && !crash.Task.IsCompletedSuccessfully && failure is null;
        var report = new
        {
            verificationSucceeded = passed,
            expected,
            package,
            device = settings.DeviceSerial,
            started,
            finished = DateTimeOffset.Now,
            requestedObservationSeconds = seconds,
            activityReady = ready,
            applicationFailureDetected = crash.Task.IsCompletedSuccessfully,
            updates,
            error = failure?.ToString(),
            scope = "真实应用启动及崩溃监测必要检查；未执行登录、阅读业务或全面兼容性测试。"
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllLinesAsync(Path.ChangeExtension(reportPath, ".log"), log);
        return passed ? 0 : 1;
    }
}
