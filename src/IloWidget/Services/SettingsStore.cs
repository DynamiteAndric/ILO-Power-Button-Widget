using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IloWidget.Models;

namespace IloWidget.Services;

/// <summary>Loads and saves the widget settings as JSON. The password is stored DPAPI-encrypted.</summary>
public sealed class SettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("IloWidget.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IloWidget", "settings.json");

    public string Path { get; }

    public SettingsStore(string? path = null)
    {
        Path = System.IO.Path.GetFullPath(path ?? DefaultPath);
    }

    /// <summary>
    /// True when the settings file exists but could not be read or parsed at the last <see cref="Load"/>.
    /// Saving is then refused unless forced, so a transient lock or a damaged file cannot wipe the real settings.
    /// </summary>
    public bool LoadFailed { get; private set; }

    private const int ReadAttempts = 3;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(200);

    public WidgetSettings Load()
    {
        LoadFailed = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(Path))
                    return new WidgetSettings();
                var s = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(Path)) ?? new WidgetSettings();
                // "Host": null in the file would otherwise leave a null in a non-nullable property
                s.Host ??= "";
                s.User ??= "";
                s.ProtectedPassword ??= "";
                return s;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return new WidgetSettings(); // removed since the existence check: same as missing
            }
            catch (JsonException)
            {
                break; // corrupt: reading again will not help
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // possibly a transient lock (e.g. at logon): try again shortly
                if (attempt >= ReadAttempts)
                    break;
                Thread.Sleep(ReadRetryDelay);
            }
        }
        LoadFailed = true;
        return new WidgetSettings();
    }

    /// <summary>
    /// Writes the settings. While <see cref="LoadFailed"/> is set the existing file is left alone and an
    /// <see cref="IOException"/> is thrown, unless <paramref name="force"/> (the user explicitly entered
    /// settings); a forced save first keeps a copy of the unreadable file as <c>settings.json.bad</c>.
    /// </summary>
    public void Save(WidgetSettings settings, bool force = false)
    {
        if (LoadFailed && !force)
            throw new IOException("The settings file could not be read; it is not overwritten.");
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (LoadFailed)
        {
            try
            {
                if (File.Exists(Path))
                    File.Copy(Path, Path + ".bad", overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a backup is best effort
            }
        }
        // write next to the target and move it over, so a crash mid-write cannot leave a truncated file
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, Path, overwrite: true);
        LoadFailed = false;
    }

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain))
            return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>True for a non-empty value that decrypts for the current user.</summary>
    public static bool CanUnprotect(string protectedValue) =>
        !string.IsNullOrEmpty(protectedValue) && TryUnprotect(protectedValue, out _);

    /// <summary>Returns "" when the value is empty or cannot be decrypted (e.g. file copied from another account).</summary>
    public static string Unprotect(string protectedValue) =>
        TryUnprotect(protectedValue, out var plain) ? plain : "";

    private static bool TryUnprotect(string protectedValue, out string plain)
    {
        plain = "";
        if (string.IsNullOrEmpty(protectedValue))
            return false;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            plain = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
