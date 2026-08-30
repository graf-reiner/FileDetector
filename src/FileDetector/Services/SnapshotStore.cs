using System.Text.Json;
using FileDetector.Core;

namespace FileDetector.Services;

/// <summary>
/// Enumerates the top level of a watched folder. Subfolders are listed as items but never
/// descended into — the watch is deliberately one level deep.
/// </summary>
public static class FolderScanner
{
    public static FolderSnapshot Scan(string folderPath, IgnoreMatcher ignore)
    {
        var normalized = AppPaths.NormalizeFolder(folderPath);
        var snapshot = new FolderSnapshot
        {
            Path = normalized,
            CapturedUtc = DateTime.UtcNow,
            Entries = new List<FolderEntry>(),
        };

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            // Hidden items still count as "something appeared"; OS bookkeeping (System Volume
            // Information, pagefile.sys) does not.
            AttributesToSkip = FileAttributes.System,
        };

        var dir = new DirectoryInfo(normalized);
        foreach (var info in dir.EnumerateFileSystemInfos("*", options))
        {
            if (ignore.IsIgnored(info.Name)) continue;

            var isDir = (info.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
            long size = 0;
            var lastWrite = DateTime.MinValue;
            try
            {
                if (!isDir && info is FileInfo file) size = file.Length;
                lastWrite = info.LastWriteTimeUtc;
            }
            catch (IOException)
            {
                // Item vanished or is locked mid-scan; record what we have.
            }

            snapshot.Entries.Add(new FolderEntry(info.Name, isDir, size, lastWrite));
        }

        return snapshot;
    }

    /// <summary>Reads a single item as it would appear in a snapshot, or null if it is already gone.</summary>
    public static FolderEntry? Describe(string folderPath, string name)
    {
        var full = Path.Combine(folderPath, name);
        try
        {
            if (Directory.Exists(full))
            {
                return new FolderEntry(name, true, 0, Directory.GetLastWriteTimeUtc(full));
            }

            var file = new FileInfo(full);
            if (!file.Exists) return null;
            return new FolderEntry(name, false, file.Length, file.LastWriteTimeUtc);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not describe '{full}': {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Persists one snapshot per watched folder. This is what lets the app report changes that
/// happened while it was not running.
/// </summary>
public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _gate = new();

    public FolderSnapshot? Load(string folderPath)
    {
        var file = AppPaths.SnapshotFileFor(folderPath);
        lock (_gate)
        {
            if (!File.Exists(file)) return null;
            try
            {
                var snapshot = JsonSerializer.Deserialize<FolderSnapshot>(File.ReadAllText(file), JsonOptions);
                if (snapshot is null) return null;
                snapshot.Entries ??= new List<FolderEntry>();
                return snapshot;
            }
            catch (Exception ex)
            {
                // A corrupt snapshot would report the whole folder as new; treat it as "no baseline"
                // so the next scan silently re-baselines instead.
                Log.Error($"snapshot for '{folderPath}' unreadable, discarding", ex);
                TryDelete(file);
                return null;
            }
        }
    }

    public void Save(FolderSnapshot snapshot)
    {
        var file = AppPaths.SnapshotFileFor(snapshot.Path);
        lock (_gate)
        {
            try
            {
                AppPaths.EnsureCreated();
                AppPaths.WriteFileAtomic(file, JsonSerializer.Serialize(snapshot, JsonOptions));
            }
            catch (Exception ex)
            {
                Log.Error($"failed to save snapshot for '{snapshot.Path}'", ex);
            }
        }
    }

    public void Delete(string folderPath)
    {
        lock (_gate) TryDelete(AppPaths.SnapshotFileFor(folderPath));
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not delete snapshot '{file}': {ex.Message}");
        }
    }
}
