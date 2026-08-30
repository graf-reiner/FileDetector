using System.Text.Json;
using System.Text.Json.Serialization;
using FileDetector.Core;

namespace FileDetector.Services;

public sealed class WatchedFolder
{
    public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

/// <summary>User-facing configuration, persisted to %APPDATA%\FileDetector\settings.json.</summary>
public sealed class AppSettings
{
    public List<WatchedFolder> WatchedFolders { get; set; } = new();
    public List<string> IgnorePatterns { get; set; } = new(IgnoreMatcher.DefaultPatterns);

    /// <summary>Quiet period before a burst of events is reported as one batch.</summary>
    public double DebounceSeconds { get; set; } = 2;

    /// <summary>Hard cap so a long continuous copy still reports instead of being starved.</summary>
    public double MaxBatchWaitSeconds { get; set; } = 10;

    /// <summary>Safety rescan interval — FileSystemWatcher is unreliable on network shares.</summary>
    public int RescanMinutes { get; set; } = 5;

    public bool StartWithWindows { get; set; }
    public bool ShowToast { get; set; } = true;
    public bool ShowModal { get; set; } = true;

    public bool NotifyOnRenamed { get; set; } = true;
    public bool NotifyOnDeleted { get; set; } = true;
    public bool NotifyOnModified { get; set; }

    [JsonIgnore]
    public TimeSpan Debounce => TimeSpan.FromSeconds(Math.Clamp(DebounceSeconds, 0.25, 60));

    [JsonIgnore]
    public TimeSpan MaxBatchWait => TimeSpan.FromSeconds(Math.Clamp(MaxBatchWaitSeconds, Debounce.TotalSeconds, 300));

    [JsonIgnore]
    public TimeSpan RescanInterval => TimeSpan.FromMinutes(Math.Clamp(RescanMinutes, 1, 1440));

    public bool ShouldNotify(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => true,
        ChangeKind.Renamed => NotifyOnRenamed,
        ChangeKind.Deleted => NotifyOnDeleted,
        ChangeKind.Modified => NotifyOnModified,
        _ => false,
    };

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;

    public IEnumerable<string> EnabledFolderPaths =>
        WatchedFolders
            .Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Path))
            .Select(f => AppPaths.NormalizeFolder(f.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Loads and saves <see cref="AppSettings"/>, surviving a corrupt or half-written file.</summary>
public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        AppPaths.EnsureCreated();

        if (!File.Exists(AppPaths.SettingsFile))
        {
            var fresh = new AppSettings();
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(AppPaths.SettingsFile);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is null) throw new InvalidDataException("settings.json deserialized to null");

            loaded.IgnorePatterns ??= new List<string>(IgnoreMatcher.DefaultPatterns);
            loaded.WatchedFolders ??= new List<WatchedFolder>();
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Error("settings.json unreadable, replacing with defaults", ex);
            try
            {
                File.Copy(AppPaths.SettingsFile, AppPaths.BadSettingsFile, overwrite: true);
            }
            catch (Exception copyEx)
            {
                Log.Warn($"could not preserve the bad settings file: {copyEx.Message}");
            }

            var fresh = new AppSettings();
            Save(fresh);
            return fresh;
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureCreated();
            AppPaths.WriteFileAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("failed to save settings", ex);
        }
    }
}
