using System.IO;
using System.Text.Json;
using System.Windows;
using AndroidOnDesktop.Core;

namespace AndroidOnDesktop.Desktop;

public partial class App : Application
{
    public static string Root { get; } = FindRoot();
    public static string SettingsPath => Path.Combine(Root, "data", "settings.json");
    public static string? StartupReportPath { get; private set; }
    public static bool IsVerificationMode { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsVerificationMode = e.Args.Length > 0 && e.Args[0].StartsWith("--verify-", StringComparison.Ordinal);
        try
        {
            Directory.CreateDirectory(Path.Combine(Root, "data"));
            Environment.CurrentDirectory = Root;
            var settings = ToolLocator.Discover(await SettingsStore.LoadAsync(SettingsPath));
            if (e.Args.Length == 4 && e.Args[0] == "--verify-error-ui") settings.DeviceSerial = e.Args[3];
            if (e.Args.Length is 5 or 6 && e.Args[0] == "--verify-app")
            {
                if (e.Args.Length == 6) settings.DeviceSerial = e.Args[5];
                Shutdown(await AppVerification.RunAsync(settings, e.Args[1], int.Parse(e.Args[2]), e.Args[3], e.Args[4]));
                return;
            }
            if (e.Args.Length == 3 && e.Args[0] == "--verify-runtime")
            {
                Shutdown(await RuntimeSmoke.RunAsync(settings, e.Args[1], e.Args[2]));
                return;
            }
            if (e.Args.Length == 2 && e.Args[0] == "--diagnose")
            {
                await using var service = new RuntimeService(settings);
                var report = await service.DetectAsync();
                await File.WriteAllTextAsync(e.Args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown();
                return;
            }
            if (e.Args.Length == 2 && e.Args[0] == "--verify-startup") StartupReportPath = e.Args[1];
            var verifyExit = e.Args.Length == 3 && e.Args[0] == "--verify-exit";
            MainWindow = new MainWindow(settings, manageExistingLifetime: verifyExit || !IsVerificationMode);
            MainWindow.Show();
            if (verifyExit)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Shutdown(await ExitVerification.RunAsync((MainWindow)MainWindow, settings, e.Args[1], e.Args[2]));
                return;
            }
            if (e.Args.Length is 3 or 4 && e.Args[0] == "--verify-error-ui")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var window = (MainWindow)MainWindow;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.AppStartupTimeoutSeconds + 30));
                await window.VerifyLaunchForSmokeAsync(e.Args[1], timeout.Token);
                for (var i = 0; i < 100 && window.LastSessionUpdate?.State != AppSessionState.Failed; i++)
                    await Task.Delay(200, timeout.Token);
                var verified = window.IsErrorVisible && window.LastSessionUpdate?.State == AppSessionState.Failed &&
                    File.Exists(window.SessionLogPath) && new FileInfo(window.SessionLogPath).Length > 0;
                await File.WriteAllTextAsync(e.Args[2], JsonSerializer.Serialize(new
                {
                    success = verified,
                    errorVisible = window.IsErrorVisible,
                    message = window.ErrorMessage,
                    session = window.LastSessionUpdate,
                    logFile = window.SessionLogPath,
                    testedAt = DateTimeOffset.Now,
                    scope = "通过真实 APK 的实际崩溃事件确认界面错误提示和日志落盘。"
                }, new JsonSerializerOptions { WriteIndented = true }));
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Close();
                await closed.Task;
                Shutdown(verified ? 0 : 1);
            }
        }
        catch (Exception ex)
        {
            if (e.Args.Length == 2 && e.Args[0].StartsWith("--"))
                await File.WriteAllTextAsync(e.Args[1], JsonSerializer.Serialize(new { success = false, error = ex.ToString() }));
            else MessageBox.Show(ex.Message, "Android 窗口启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AndroidOnDesktop.slnx"))) return directory.FullName;
        return AppContext.BaseDirectory;
    }
}
