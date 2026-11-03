namespace AndroidOnDesktop.Core;

public static class ToolLocator
{
    public static HostSettings Discover(HostSettings? settings = null)
    {
        settings ??= new HostSettings();
        var roots = ProjectRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sdkRoots = new List<string>();
        foreach (var root in roots) sdkRoots.Add(Path.Combine(root, "runtime", "android-sdk"));
        foreach (var variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value) sdkRoots.Add(value);
        sdkRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"));
        sdkRoots.Add(@"C:\Program Files (x86)\Android\android-sdk");
        var unityEditors = @"C:\Program Files\Unity\Hub\Editor";
        if (Directory.Exists(unityEditors))
        {
            try
            {
                sdkRoots.AddRange(Directory.EnumerateDirectories(unityEditors).OrderDescending()
                    .Select(editor => Path.Combine(editor, "Editor", "Data", "PlaybackEngines", "AndroidPlayer", "SDK")));
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        settings.AdbPath = Fill(settings.AdbPath, sdkRoots.Select(root => Path.Combine(root, "platform-tools", "adb.exe")).Concat(OnPath("adb.exe")));
        settings.EmulatorPath = Fill(settings.EmulatorPath, sdkRoots.Select(root => Path.Combine(root, "emulator", "emulator.exe")).Concat(OnPath("emulator.exe")));
        var scrcpy = roots.Select(root => Path.Combine(root, "runtime", "scrcpy", "scrcpy.exe"))
            .Concat(OnPath("scrcpy.exe"));
        settings.ScrcpyPath = Fill(settings.ScrcpyPath, scrcpy);
        return settings;
    }

    private static string Fill(string? current, IEnumerable<string> candidates)
    {
        if (!string.IsNullOrWhiteSpace(current)) return current.Trim().Trim('"');
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static IEnumerable<string> OnPath(string file)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string candidate;
            try { candidate = Path.Combine(directory.Trim('"'), file); }
            catch (ArgumentException) { continue; }
            yield return candidate;
        }
    }

    private static IEnumerable<string> ProjectRoots()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            for (var i = 0; current is not null && i < 9; i++, current = current.Parent)
                yield return current.FullName;
        }
    }

    internal static IReadOnlyDictionary<string, string> EmulatorEnvironment(string emulatorPath)
    {
        foreach (var root in ProjectRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sdk = Path.Combine(root, "runtime", "android-sdk");
            var localEmulator = Path.Combine(sdk, "emulator", "emulator.exe");
            if (!File.Exists(localEmulator) ||
                !string.Equals(Path.GetFullPath(localEmulator), Path.GetFullPath(emulatorPath), StringComparison.OrdinalIgnoreCase)) continue;
            var values = new Dictionary<string, string>
            {
                ["ANDROID_HOME"] = sdk,
                ["ANDROID_SDK_ROOT"] = sdk
            };
            var avd = Path.Combine(root, "runtime", "avd");
            if (Directory.Exists(avd)) values["ANDROID_AVD_HOME"] = avd;
            return values;
        }
        return new Dictionary<string, string>();
    }
}
