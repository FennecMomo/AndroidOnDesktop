using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AndroidOnDesktop.Core;
using Microsoft.Win32;

namespace AndroidOnDesktop.Desktop;

public partial class MainWindow : Window
{
    private readonly HostSettings _settings;
    private readonly RuntimeService _runtime;
    private readonly SessionLog _sessionLog;
    private readonly List<AndroidApp> _apps = [];
    private readonly Dictionary<string, AppSessionUpdate> _latestSessions = new(StringComparer.Ordinal);
    private CancellationTokenSource? _operation;
    private bool _closing;
    private bool _populating;
    private Process? _setupProcess;
    private string? _errorPackage;
    private long _sessionFailureSequence;
    private int _logFailureReported;
    private bool _initialConnectionAttempted;
    private bool _connectedToExistingAndroid;

    internal string SessionLogPath => _sessionLog.FilePath;
    internal bool IsErrorVisible => ErrorBanner.Visibility == Visibility.Visible;
    internal string ErrorMessage => ErrorText.Text;
    internal AppSessionUpdate? LastSessionUpdate { get; private set; }

    public MainWindow(HostSettings settings)
    {
        _sessionLog = new SessionLog(Path.Combine(App.Root, "data", "logs"));
        InitializeComponent();
        _settings = settings;
        _runtime = new RuntimeService(settings);
        _runtime.Log += AppendLog;
        _runtime.AppSessionChanged += HandleSessionUpdate;
        Closed += (_, _) => _sessionLog.Dispose();
        PopulateSettings();
        if (File.Exists(settings.AdbPath) && File.Exists(settings.EmulatorPath) && File.Exists(settings.ScrcpyPath))
            OperationText.Text = "运行组件已找到。点击“启动 Android”后即可安装 APK。";
        AppendLog("会话日志：" + SessionLogPath);
    }

    private void PopulateSettings()
    {
        _populating = true;
        AdbPathBox.Text = _settings.AdbPath;
        EmulatorPathBox.Text = _settings.EmulatorPath;
        ScrcpyPathBox.Text = _settings.ScrcpyPath;
        AvdBox.Text = _settings.AvdName;
        SerialBox.Text = _settings.DeviceSerial;
        SizeBox.Text = _settings.DisplaySize;
        DpiBox.Text = _settings.DisplayDpi.ToString();
        PortBox.Text = _settings.EmulatorPort.ToString();
        var gpuMode = (_settings.EmulatorGpuMode ?? "auto").Trim().ToLowerInvariant();
        if (!GpuModeBox.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, gpuMode)))
        {
            // Preserve a valid advanced mode loaded from an existing configuration.
            GpuModeBox.Items.Add(new ComboBoxItem { Content = "其他兼容模式（当前配置）", Tag = gpuMode });
        }
        GpuModeBox.SelectedValue = gpuMode;
        MemoryBox.Text = _settings.EmulatorMemoryMb.ToString();
        FrameRateBox.Text = _settings.VideoMaxFps.ToString();
        _populating = false;
    }

    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(DpiBox.Text, out var dpi)) throw new InvalidOperationException("显示 DPI 需要填写整数。");
        if (!int.TryParse(PortBox.Text, out var port)) throw new InvalidOperationException("模拟器端口需要填写整数。");
        if (!int.TryParse(MemoryBox.Text, out var memoryMb)) throw new InvalidOperationException("安卓内存需要填写整数（MB）。");
        if (!int.TryParse(FrameRateBox.Text, out var maxFps)) throw new InvalidOperationException("帧率上限需要填写整数。");
        // Validate a candidate before changing the active runtime configuration.
        var candidate = JsonSerializer.Deserialize<HostSettings>(JsonSerializer.Serialize(_settings)) ?? new HostSettings();
        candidate.AdbPath = AdbPathBox.Text.Trim().Trim('"');
        candidate.EmulatorPath = EmulatorPathBox.Text.Trim().Trim('"');
        candidate.ScrcpyPath = ScrcpyPathBox.Text.Trim().Trim('"');
        candidate.AvdName = AvdBox.Text.Trim();
        candidate.DeviceSerial = SerialBox.Text.Trim();
        candidate.DisplaySize = SizeBox.Text.Trim();
        candidate.DisplayDpi = dpi;
        candidate.EmulatorPort = port;
        candidate.EmulatorGpuMode = GpuModeBox.SelectedValue as string ?? "auto";
        candidate.EmulatorMemoryMb = memoryMb;
        candidate.VideoMaxFps = maxFps;
        candidate.ValidateDisplay();
        candidate.ValidateEmulator();
        if (_runtime.OwnsEmulator &&
            (candidate.EmulatorPort != _settings.EmulatorPort || candidate.AvdName != _settings.AvdName || candidate.EmulatorPath != _settings.EmulatorPath ||
             !string.Equals(candidate.EmulatorGpuMode, _settings.EmulatorGpuMode?.Trim(), StringComparison.OrdinalIgnoreCase) ||
             candidate.EmulatorMemoryMb != _settings.EmulatorMemoryMb))
            throw new InvalidOperationException("请先停止本程序启动的 Android，再修改模拟器路径、AVD、端口、图形加速或安卓内存。");
        await SettingsStore.SaveAsync(App.SettingsPath, candidate);
        _settings.AdbPath = candidate.AdbPath;
        _settings.EmulatorPath = candidate.EmulatorPath;
        _settings.ScrcpyPath = candidate.ScrcpyPath;
        _settings.AvdName = candidate.AvdName;
        _settings.DeviceSerial = candidate.DeviceSerial;
        _settings.DisplaySize = candidate.DisplaySize;
        _settings.DisplayDpi = candidate.DisplayDpi;
        _settings.EmulatorPort = candidate.EmulatorPort;
        _settings.EmulatorGpuMode = candidate.EmulatorGpuMode;
        _settings.EmulatorMemoryMb = candidate.EmulatorMemoryMb;
        _settings.VideoMaxFps = candidate.VideoMaxFps;
        _settings.VideoBitRateMbps = candidate.VideoBitRateMbps;
    }

    private void AppendLog(string text)
    {
        _sessionLog.Write(text);
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => AppendLogToView(text)); return; }
        AppendLogToView(text);
    }

    private void AppendLogToView(string text)
    {
        if (_closing || LogBox is null) return;
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        if (LogBox.Text.Length > 70000) LogBox.Text = LogBox.Text[^50000..];
        LogBox.ScrollToEnd();
        if (_sessionLog.LastWriteError is string writeError && Interlocked.Exchange(ref _logFailureReported, 1) == 0)
            LogBox.AppendText("日志保存失败：" + writeError + Environment.NewLine);
    }

    private async Task RunAsync(string operation, Func<CancellationToken, Task> action, bool save = true,
        string? launchPackage = null, CancellationToken cancellationToken = default)
    {
        if (_operation is not null || _closing) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = cancellation;
        var failureSequence = _sessionFailureSequence;
        if (launchPackage is not null && _errorPackage == launchPackage) ClearApplicationError();
        SetBusy(true);
        OperationText.Text = operation;
        try
        {
            if (save) await SaveSettingsAsync();
            await action(cancellation.Token);
            if (_sessionFailureSequence != failureSequence && IsErrorVisible) OperationText.Text = ErrorMessage;
            else if (launchPackage is not null && _latestSessions.TryGetValue(launchPackage, out var session))
                OperationText.Text = session.State == AppSessionState.Running ? "应用已运行：" + launchPackage : session.Message;
            else OperationText.Text = operation + "：已完成";
        }
        catch (OperationCanceledException)
        {
            OperationText.Text = "操作已取消。";
            AppendLog("操作已取消。");
        }
        catch (Exception ex)
        {
            OperationText.Text = ex.Message;
            AppendLog(ex.ToString());
            if (launchPackage is not null) ShowApplicationError(launchPackage, ex.Message);
        }
        finally
        {
            _operation = null;
            SetBusy(false);
        }
    }

    private void HandleSessionUpdate(AppSessionUpdate update)
    {
        AppendLog($"[应用会话 {update.SessionId}] {update.PackageName} {update.State}：{update.Message}" +
            (string.IsNullOrWhiteSpace(update.TechnicalDetail) ? "" : Environment.NewLine + update.TechnicalDetail));
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => ApplySessionUpdate(update)); return; }
        ApplySessionUpdate(update);
    }

    private void ApplySessionUpdate(AppSessionUpdate update)
    {
        if (_closing) return;
        LastSessionUpdate = update;
        var isCurrent = !_latestSessions.TryGetValue(update.PackageName, out var latest) || latest.SessionId == update.SessionId;
        if (update.State == AppSessionState.Starting || isCurrent) _latestSessions[update.PackageName] = update;
        switch (update.State)
        {
            case AppSessionState.Starting:
                if (_errorPackage == update.PackageName) ClearApplicationError();
                OperationText.Text = update.Message;
                break;
            case AppSessionState.Running:
                if (!isCurrent) break;
                if (_errorPackage == update.PackageName) ClearApplicationError();
                if (_operation is null) OperationText.Text = "应用已运行：" + update.PackageName;
                break;
            case AppSessionState.Failed:
                _sessionFailureSequence++;
                ShowApplicationError(update.PackageName, update.Message);
                OperationText.Text = ErrorMessage;
                break;
            case AppSessionState.Closed:
                if (isCurrent && _operation is null && !IsErrorVisible) OperationText.Text = update.Message;
                break;
        }
    }

    private void ShowApplicationError(string packageName, string message)
    {
        _errorPackage = packageName;
        var conciseMessage = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "应用运行失败，请查看日志。";
        if (conciseMessage.Length > 360) conciseMessage = conciseMessage[..360] + "…";
        ErrorText.Text = conciseMessage.Contains(packageName, StringComparison.Ordinal) ? conciseMessage : packageName + "：" + conciseMessage;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void ClearApplicationError()
    {
        _errorPackage = null;
        ErrorText.Text = "";
        ErrorBanner.Visibility = Visibility.Collapsed;
    }

    private void DismissError_Click(object sender, RoutedEventArgs e) => ClearApplicationError();

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(SessionLogPath))
            {
                var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
                start.ArgumentList.Add(SessionLogPath);
                Process.Start(start);
            }
            else throw new IOException(_sessionLog.LastWriteError ?? "当前会话日志文件尚未创建。");
        }
        catch (Exception ex) { AppendLog(ex.ToString()); OperationText.Text = "无法打开日志：" + ex.Message; }
    }

    internal Task VerifyLaunchForSmokeAsync(string packageName, CancellationToken cancellationToken) =>
        RunAsync("打开应用窗口", ct => _runtime.LaunchAppAsync(packageName, ct), save: false, launchPackage: packageName, cancellationToken: cancellationToken);

    private void SetBusy(bool busy)
    {
        RuntimeActions.IsEnabled = !busy;
        AppActions.IsEnabled = !busy;
        LaunchActions.IsEnabled = !busy;
        SettingsFields.IsEnabled = !busy;
        StopAndroidButton.IsEnabled = !busy && _runtime.OwnsEmulator;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshDevicesAsync(CancellationToken ct)
    {
        var devices = await _runtime.GetDevicesAsync(ct);
        _populating = true;
        DevicesBox.ItemsSource = devices;
        DevicesBox.SelectedItem = devices.FirstOrDefault(x => x.Serial == _settings.DeviceSerial);
        _populating = false;
        AppendLog(devices.Count == 0 ? "没有发现 Android 设备。请先启动 Android。" : string.Join(Environment.NewLine, devices.Select(x => $"{x.Serial}  {x.State}  {x.Description}")));
    }

    private async Task RefreshAppsAsync(CancellationToken ct)
    {
        var apps = await _runtime.ListAppsAsync(ct);
        _apps.Clear();
        _apps.AddRange(apps);
        FilterApps();
        AppendLog($"已读取 {_apps.Count} 个可启动的已安装应用。");
    }

    private void FilterApps()
    {
        if (AppsList is null) return;
        var filter = SearchBox.Text.Trim();
        var visible = _apps.Where(x => x.PackageName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        AppsList.ItemsSource = visible;
        EmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _apps.Count == 0 ? "安装 APK 后，应用会显示在这里。\n也可以将 APK 拖入此窗口。" : "没有匹配的应用。";
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunAsync("启动 Android", async ct =>
    {
        if (!await _runtime.ConnectRunningEmulatorAsync(ct)) await _runtime.StartEmulatorAsync(ct);
        PopulateSettings();
        await SettingsStore.SaveAsync(App.SettingsPath, _settings);
        await RefreshDevicesAsync(ct);
        await RefreshAppsAsync(ct);
    });

    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunAsync("停止 Android", async ct =>
    {
        await _runtime.StopOwnedEmulatorAsync(ct);
        _apps.Clear();
        FilterApps();
        await RefreshDevicesAsync(ct);
    }, save: false);

    private async void Detect_Click(object sender, RoutedEventArgs e) => await RunAsync("检测运行环境", async ct =>
    {
        var report = await _runtime.DetectAsync(ct);
        PopulateSettings();
        foreach (var message in report.Messages) AppendLog(message);
        if (report.EmulatorAvailable)
        {
            var avds = await _runtime.ListAvdsAsync(ct);
            var current = AvdBox.Text;
            AvdBox.ItemsSource = avds;
            AvdBox.Text = current;
            AppendLog($"可用 AVD：{(avds.Count == 0 ? "无" : string.Join(", ", avds))}");
        }
    });

    private async void Devices_Click(object sender, RoutedEventArgs e) => await RunAsync("刷新设备", RefreshDevicesAsync);
    private async void RefreshApps_Click(object sender, RoutedEventArgs e) => await RunAsync("刷新应用", RefreshAppsAsync);
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunAsync("保存运行配置", _ => Task.CompletedTask);
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void Search_TextChanged(object sender, TextChangedEventArgs e) => FilterApps();
    private void ToggleSettings_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void Devices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_populating && DevicesBox.SelectedItem is AndroidDevice device) SerialBox.Text = device.Serial;
    }

    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        // Clearing paths allows automatic detection to replace an obsolete installation.
        var located = ToolLocator.Discover(new HostSettings());
        AdbPathBox.Text = located.AdbPath;
        EmulatorPathBox.Text = located.EmulatorPath;
        ScrcpyPathBox.Text = located.ScrcpyPath;
        AppendLog("已重新查找本地运行组件；点击保存配置后生效。");
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Android 安装包 (*.apk)|*.apk", Multiselect = false, Title = "选择 APK" };
        if (picker.ShowDialog(this) == true) await InstallAsync(picker.FileName);
    }

    private Task InstallAsync(string path) => RunAsync("安装 APK", async ct =>
    {
        await _runtime.InstallApkAsync(path, ct);
        await RefreshAppsAsync(ct);
    });

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_operation is not null || _closing) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var apks = paths.Where(x => Path.GetExtension(x).Equals(".apk", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (apks.Length != 1) { OperationText.Text = "每次请拖入一个 APK 安装包。"; return; }
            await InstallAsync(apks[0]);
        }
    }

    private async void Launch_Click(object sender, RoutedEventArgs e) => await LaunchSelectedAsync();
    private async void Apps_DoubleClick(object sender, MouseButtonEventArgs e) => await LaunchSelectedAsync();
    private async Task LaunchSelectedAsync()
    {
        if (AppsList.SelectedItem is not AndroidApp app) { OperationText.Text = "请先选择一个应用。"; return; }
        await RunAsync("打开应用窗口", ct => _runtime.LaunchAppAsync(app.PackageName, ct), launchPackage: app.PackageName);
    }
    private async void AndroidSettings_Click(object sender, RoutedEventArgs e) => await RunAsync("打开 Android 设置", ct => _runtime.LaunchAppAsync("com.android.settings", ct), launchPackage: "com.android.settings");

    private void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        if (_setupProcess is { HasExited: false }) { OperationText.Text = "运行组件安装窗口已打开，请在该窗口继续。"; return; }
        try
        {
            var script = Path.Combine(App.Root, "tools", "Install-Runtime.ps1");
            if (!File.Exists(script)) script = Path.Combine(AppContext.BaseDirectory, "tools", "Install-Runtime.ps1");
            if (!File.Exists(script)) throw new FileNotFoundException("找不到运行组件安装脚本。", script);
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = App.Root };
            foreach (var arg in new[] { "-NoProfile", "-NoExit", "-ExecutionPolicy", "Bypass", "-File", script, "-RuntimeRoot", Path.Combine(App.Root, "runtime") }) start.ArgumentList.Add(arg);
            _setupProcess = Process.Start(start);
            OperationText.Text = "已打开安装窗口。完成后点击“运行配置 → 自动查找 → 保存配置”。";
            AppendLog("安装窗口会下载 Android 运行组件；如需接受 Android SDK 条款，会在安装窗口中显示。");
        }
        catch (Exception ex) { OperationText.Text = ex.Message; AppendLog(ex.ToString()); }
    }

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (App.IsVerificationMode && App.StartupReportPath is null) return;
        if (_initialConnectionAttempted) return;
        _initialConnectionAttempted = true;
        await ConnectOnStartupAsync();
        if (App.StartupReportPath is string path)
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                success = true, title = Title, loaded = IsLoaded, width = ActualWidth, height = ActualHeight,
                avdName = _settings.AvdName, emulatorPort = _settings.EmulatorPort,
                deviceSerial = _settings.DeviceSerial, appStartupTimeoutSeconds = _settings.AppStartupTimeoutSeconds,
                displaySize = _settings.DisplaySize, videoMaxFps = _settings.VideoMaxFps,
                videoBitRateMbps = _settings.VideoBitRateMbps, emulatorGpuMode = _settings.EmulatorGpuMode,
                emulatorMemoryMb = _settings.EmulatorMemoryMb,
                connectedToExistingAndroid = _connectedToExistingAndroid, applicationCount = _apps.Count,
                ownsEmulator = _runtime.OwnsEmulator, time = DateTimeOffset.Now
            }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }

    private async Task ConnectOnStartupAsync()
    {
        if (!File.Exists(_settings.AdbPath)) return;
        bool? found = null;
        await RunAsync("检查 Android 状态", async ct =>
        {
            var configuredSerial = $"emulator-{_settings.EmulatorPort}";
            if (string.IsNullOrWhiteSpace(_settings.DeviceSerial) || _settings.DeviceSerial == configuredSerial)
                found = await _runtime.ConnectRunningEmulatorAsync(ct);
            else
                found = (await _runtime.GetDevicesAsync(ct)).Any(device => device.Serial == _settings.DeviceSerial && device.State == "device");
            await RefreshDevicesAsync(ct);
            if (found == true)
            {
                PopulateSettings();
                await RefreshAppsAsync(ct);
                _connectedToExistingAndroid = true;
            }
        }, save: false);
        if (found == true && _connectedToExistingAndroid && !IsErrorVisible)
            OperationText.Text = "已连接正在运行的 Android，可直接打开应用或安装 APK。";
        else if (found == false && !IsErrorVisible)
            OperationText.Text = "点击“启动 Android”，等待就绪后即可打开应用。";
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _operation?.Cancel();
        if (_operation is not null) { OperationText.Text = "正在取消操作，请稍后再关闭窗口。"; return; }
        _closing = true;
        IsEnabled = false;
        try { await _runtime.DisposeAsync(); }
        catch (Exception ex) { AppendLog(ex.ToString()); }
        _runtime.AppSessionChanged -= HandleSessionUpdate;
        _runtime.Log -= AppendLog;
        // Closing may finish synchronously; re-enter Close only after this event returns.
        _ = Dispatcher.BeginInvoke(Close);
    }
}
