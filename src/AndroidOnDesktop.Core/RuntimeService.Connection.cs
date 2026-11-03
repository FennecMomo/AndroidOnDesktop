namespace AndroidOnDesktop.Core;

public sealed partial class RuntimeService
{
    // Verification tools remain read-only by default. The desktop can explicitly manage
    // an existing instance only after its AVD directory matches this project's runtime.
    public async Task<bool> ConnectRunningEmulatorAsync(CancellationToken cancellationToken = default, bool manageLifetime = false)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            Settings.ValidateEmulator();
            var serial = $"emulator-{Settings.EmulatorPort}";
            var avdName = Settings.AvdName.Trim();
            var adbPath = Settings.AdbPath;
            if (_ownedSerial is not null && _ownedSerial != serial)
                throw new InvalidOperationException("请先停止当前由程序管理的 Android，再连接其他环境。");
            var device = (await GetDevicesAsync(cancellationToken)).FirstOrDefault(item => item.Serial == serial);
            if (device is null) return false;
            if (device.State != "device")
                throw new InvalidOperationException($"Android {serial} 尚未就绪（{device.State}），请等待启动完成。");
            var actualName = await ReadAvdValueAsync(adbPath, serial, "name", cancellationToken);
            if (actualName != avdName)
                throw new InvalidOperationException($"端口 {Settings.EmulatorPort} 上的 Android 与配置的虚拟设备不一致，请检查运行配置。");
            var boot = await ProcessRunner.RunAsync(adbPath, ["-s", serial, "shell", "getprop", "sys.boot_completed"],
                TimeSpan.FromSeconds(10), cancellationToken, false);
            if (boot.ExitCode != 0 || boot.StandardOutput.Trim() != "1")
                throw new InvalidOperationException("Android 仍在启动，请稍后重试。");

            if (manageLifetime && _ownedSerial is null)
            {
                var environment = ToolLocator.EmulatorEnvironment(Settings.EmulatorPath);
                if (environment.TryGetValue("ANDROID_AVD_HOME", out var avdHome) &&
                    Path.GetFileName(avdName) == avdName)
                {
                    var expectedPath = Path.Combine(avdHome, avdName + ".avd");
                    var identity = await ReadEmulatorIdentityAsync(adbPath, serial, avdName, cancellationToken);
                    if (!SameDirectory(identity.AvdPath, expectedPath))
                        throw new InvalidOperationException("当前 Android 的数据目录不属于本项目，未接管其退出生命周期。");
                    _ownedSerial = serial;
                    _ownedIdentity = identity;
                    WriteLog($"已管理本项目的 Android：{avdName}（{serial}），关闭主程序时会一并停止。");
                }
                else WriteLog("当前 Android 使用外部运行组件，仅连接；关闭程序时保留外部环境。");
            }
            Settings.DeviceSerial = serial;
            WriteLog($"已连接正在运行的 Android：{avdName}（{serial}）。");
            return true;
        }
        finally { _lifecycle.Release(); }
    }

    private sealed record EmulatorIdentity(string AdbPath, string Serial, string AvdName, string AvdPath, string BootId);

    private static async Task<string> ReadAvdValueAsync(string adbPath, string serial, string field, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(adbPath, ["-s", serial, "emu", "avd", field],
            TimeSpan.FromSeconds(5), cancellationToken);
        var value = result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line != "OK");
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("KO:", StringComparison.Ordinal))
            throw new InvalidOperationException($"无法确认 Android 的 AVD {field}，未执行环境接管或停止。");
        return value;
    }

    private static async Task<EmulatorIdentity> ReadEmulatorIdentityAsync(string adbPath, string serial, string avdName, CancellationToken cancellationToken)
    {
        var actualName = await ReadAvdValueAsync(adbPath, serial, "name", cancellationToken);
        if (actualName != avdName) throw new InvalidOperationException("Android 实例名称已变化，未执行环境接管或停止。");
        var avdPath = await ReadAvdValueAsync(adbPath, serial, "path", cancellationToken);
        if (!Path.IsPathFullyQualified(avdPath)) throw new InvalidOperationException("无法确认 Android 数据目录的绝对路径。");
        var boot = await ProcessRunner.RunAsync(adbPath, ["-s", serial, "shell", "cat", "/proc/sys/kernel/random/boot_id"],
            TimeSpan.FromSeconds(5), cancellationToken);
        var bootId = boot.StandardOutput.Trim();
        if (!Guid.TryParse(bootId, out _)) throw new InvalidOperationException("无法确认 Android 本次启动标识，未执行环境接管或停止。");
        return new(adbPath, serial, avdName, Path.GetFullPath(avdPath), bootId);
    }

    private static bool SameDirectory(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
