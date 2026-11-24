using Microsoft.Win32;

namespace IloWidget.Services;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultValueName = "IloWidget";

    /// <summary>Run value of the instance that uses this settings file. The default file keeps the plain name.</summary>
    public static string ValueName(string settingsPath) =>
        IsDefault(settingsPath) ? DefaultValueName : DefaultValueName + "." + Hashing.Stable(Normalize(settingsPath));

    /// <summary>Command line that starts this instance with the same settings file.</summary>
    public static string Command(string exePath, string settingsPath) =>
        IsDefault(settingsPath) ? $"\"{exePath}\"" : $"\"{exePath}\" --settings \"{System.IO.Path.GetFullPath(settingsPath)}\"";

    public static bool IsEnabled(string settingsPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName(settingsPath)) is string;
    }

    public static void Set(string settingsPath, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                key.SetValue(ValueName(settingsPath), Command(exe, settingsPath));
        }
        else
        {
            key.DeleteValue(ValueName(settingsPath), throwOnMissingValue: false);
        }
    }

    private static bool IsDefault(string settingsPath) => Normalize(settingsPath) == Normalize(SettingsStore.DefaultPath);

    private static string Normalize(string path) => System.IO.Path.GetFullPath(path).ToLowerInvariant();
}
