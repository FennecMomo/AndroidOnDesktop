using System.Text.Json;

namespace AndroidOnDesktop.Core;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task<HostSettings> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return ToolLocator.Discover();
        await using var stream = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<HostSettings>(stream, Options, cancellationToken)
            ?? new HostSettings();
        // Tolerate nulls from hand-edited/older configuration files.
        settings.AdbPath ??= "";
        settings.EmulatorPath ??= "";
        settings.ScrcpyPath ??= "";
        settings.AvdName ??= "AndroidOnDesktop_API36";
        settings.DeviceSerial ??= "";
        settings.DisplaySize ??= "720x1280";
        settings.EmulatorGpuMode ??= "auto";
        return ToolLocator.Discover(settings);
    }

    public static async Task SaveAsync(string path, HostSettings settings, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
