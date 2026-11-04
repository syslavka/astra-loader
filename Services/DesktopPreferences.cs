using System.IO;
using Microsoft.Win32;

namespace NLCleaner.Services;

public static class DesktopPreferences
{
    public const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupValue = "Astra Loader";
    private const string PreferencesKey = @"Software\NL-Cleaner\Preferences";
    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "AstraLoader.exe");

    public static string StartupCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['"', '\r', '\n', '\0']) >= 0 ||
            !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A full executable path is required.");
        var command = $"\"{Path.GetFullPath(executable)}\" --manual";
        if (command.Length > 260) throw new ArgumentException("Startup command exceeds the Windows Run-key length limit.");
        return command;
    }

    public static bool StartupEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
            return string.Equals(key?.GetValue(StartupValue) as string, StartupCommand(ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetStartup(bool enabled)
    {
        if (enabled)
        {
            if (!File.Exists(ExecutablePath)) throw new FileNotFoundException("Build/publish the application first.", ExecutablePath);
            using var key = Registry.CurrentUser.CreateSubKey(StartupKey, true);
            key.SetValue(StartupValue, StartupCommand(ExecutablePath), RegistryValueKind.String);
            RemoveLegacyStartup(key);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey, true);
            key?.DeleteValue(StartupValue, false);
            if (key is not null) RemoveLegacyStartup(key);
        }
        if (StartupEnabled != enabled) throw new IOException("Startup registration could not be verified.");
    }

    private static void RemoveLegacyStartup(RegistryKey key)
    {
        // Migrate only the known menu-only registration from earlier releases.
        if (key.GetValue("NL-Cleaner") is string legacy &&
            legacy.StartsWith('"') && legacy.EndsWith("\\NL-Cleaner.exe\" --manual", StringComparison.OrdinalIgnoreCase))
            key.DeleteValue("NL-Cleaner", false);
    }

    public static bool MotionEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(PreferencesKey); return key?.GetValue("MotionEnabled") is not int value || value != 0; }
        set { using var key = Registry.CurrentUser.CreateSubKey(PreferencesKey, true); key.SetValue("MotionEnabled", value ? 1 : 0, RegistryValueKind.DWord); }
    }
}
