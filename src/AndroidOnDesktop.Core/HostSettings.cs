using System.Text.RegularExpressions;

namespace AndroidOnDesktop.Core;

public sealed class HostSettings
{
    public string AdbPath { get; set; } = "";
    public string EmulatorPath { get; set; } = "";
    public string ScrcpyPath { get; set; } = "";
    public string AvdName { get; set; } = "AndroidOnDesktop_API36";
    public string DeviceSerial { get; set; } = "";
    public string DisplaySize { get; set; } = "720x1280";
    public int DisplayDpi { get; set; } = 240;
    public int EmulatorPort { get; set; } = 5584;
    public bool EnableAudio { get; set; } = true;
    public bool ResizeDisplayWithWindow { get; set; } = false;
    public int AppStartupTimeoutSeconds { get; set; } = 90;
    public string EmulatorGpuMode { get; set; } = "auto";
    public int EmulatorMemoryMb { get; set; } = 3072;
    public int VideoMaxFps { get; set; } = 30;
    public int VideoBitRateMbps { get; set; } = 4;

    public void ValidateAppStartup()
    {
        if (AppStartupTimeoutSeconds is < 15 or > 300)
            throw new InvalidOperationException("应用启动等待时间应介于 15 和 300 秒之间。");
    }

    public void ValidateDisplay()
    {
        var match = Regex.Match(DisplaySize.Trim(), @"^(\d{2,4})x(\d{2,4})$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var width) ||
            !int.TryParse(match.Groups[2].Value, out var height) ||
            width is < 200 or > 8192 || height is < 200 or > 8192)
            throw new InvalidOperationException("窗口分辨率请输入 200–8192 范围内的宽x高，例如 1000x1600。");
        if (DisplayDpi is < 72 or > 640)
            throw new InvalidOperationException("显示 DPI 应介于 72 和 640 之间。");
        if (VideoMaxFps is < 1 or > 120)
            throw new InvalidOperationException("视频帧率应介于 1 和 120 帧/秒之间。");
        if (VideoBitRateMbps is < 1 or > 100)
            throw new InvalidOperationException("视频码率应介于 1 和 100 Mbps 之间。");
    }

    public void ValidateEmulator()
    {
        if (string.IsNullOrWhiteSpace(AvdName))
            throw new InvalidOperationException("请先选择或填写 Android 虚拟设备（AVD）。");
        if (EmulatorPort is < 5554 or > 5682 || EmulatorPort % 2 != 0)
            throw new InvalidOperationException("模拟器端口应为 5554–5682 范围内的偶数。");
        if (EmulatorMemoryMb is < 2048 or > 8192)
            throw new InvalidOperationException("Android 内存应介于 2048 和 8192 MB 之间。");
        if (EmulatorGpuMode?.Trim().ToLowerInvariant() is not ("auto" or "host" or "software" or "swiftshader"))
            throw new InvalidOperationException("图形模式请选择 auto、host、software 或 swiftshader。");
    }
}

public sealed record AndroidDevice(string Serial, string State, string Description)
{
    public override string ToString() => $"{Serial} ({State})";
}

public sealed record AndroidApp(string PackageName)
{
    public override string ToString() => PackageName;
}

public sealed record RuntimeDiagnostics(
    bool AdbAvailable,
    bool EmulatorAvailable,
    bool ScrcpyAvailable,
    string ScrcpyVersion,
    IReadOnlyList<string> Messages);
