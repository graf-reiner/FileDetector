using Microsoft.Win32;

namespace FileDetector.Services;

/// <summary>
/// "Start with Windows" via the per-user Run key — no elevation, no scheduled task, and it is
/// obvious to the user where it lives.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FileDetector";

    /// <summary>
    /// The path to register. <see cref="Environment.ProcessPath"/> is correct for both normal and
    /// single-file publishes; <c>Assembly.Location</c> is empty in the latter, so it is not used.
    /// </summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FileDetector.exe");

    /// <summary>Reads the registry rather than trusting settings.json, which can drift.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read the Run key: {ex.Message}");
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                var exe = ExecutablePath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\" --silent", RegistryValueKind.String);
                Log.Info($"start-with-Windows enabled ({exe})");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("start-with-Windows disabled");
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"could not set start-with-Windows to {enabled}", ex);
            return false;
        }
    }

    /// <summary>Keeps the stored command line pointing at the current exe after a move or update.</summary>
    public static void RefreshIfEnabled()
    {
        if (!IsEnabled()) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            var current = key?.GetValue(ValueName) as string;
            var expected = $"\"{ExecutablePath}\" --silent";
            if (key is not null && current is not null && !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, expected, RegistryValueKind.String);
                Log.Info("refreshed the start-with-Windows command line");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not refresh the Run key: {ex.Message}");
        }
    }
}
