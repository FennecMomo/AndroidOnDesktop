namespace AndroidOnDesktop.Core;

public sealed partial class RuntimeService
{
    // Connect to the configured AVD without claiming ownership of an existing process.
    public async Task<bool> ConnectRunningEmulatorAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Settings.ValidateEmulator();
        var serial = $"emulator-{Settings.EmulatorPort}";
        var device = (await GetDevicesAsync(cancellationToken)).FirstOrDefault(item => item.Serial == serial);
        if (device is null) return false;
        if (device.State != "device")
            throw new InvalidOperationException($"Android {serial} 尚未就绪（{device.State}），请等待启动完成。");
        var name = await AdbAsync(["-s", serial, "emu", "avd", "name"], cancellationToken, false);
        var actualName = name.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line != "OK");
        if (name.ExitCode != 0 || actualName != Settings.AvdName)
            throw new InvalidOperationException($"端口 {Settings.EmulatorPort} 上的 Android 与配置的虚拟设备不一致，请检查运行配置。");
        var boot = await AdbAsync(["-s", serial, "shell", "getprop", "sys.boot_completed"], cancellationToken, false);
        if (boot.ExitCode != 0 || boot.StandardOutput.Trim() != "1")
            throw new InvalidOperationException("Android 仍在启动，请稍后重试。");
        Settings.DeviceSerial = serial;
        WriteLog($"已连接正在运行的 Android：{Settings.AvdName}（{serial}）。");
        return true;
    }
}
