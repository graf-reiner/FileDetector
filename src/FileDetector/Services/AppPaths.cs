using System.Security.Cryptography;
using System.Text;

namespace FileDetector.Services;

/// <summary>Every file the app owns lives under %APPDATA%\FileDetector.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FileDetector");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string BadSettingsFile => Path.Combine(Root, "settings.bad.json");
    public static string SnapshotsDir => Path.Combine(Root, "snapshots");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string LogFile => Path.Combine(LogsDir, "app.log");
    public static string HistoryFile => Path.Combine(Root, "history.jsonl");
    public static string HistoryArchiveFile => Path.Combine(Root, "history.1.jsonl");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SnapshotsDir);
        Directory.CreateDirectory(LogsDir);
    }

    /// <summary>Stable per-folder snapshot filename: a readable leaf plus a hash of the full path.</summary>
    public static string SnapshotFileFor(string folderPath)
    {
        var normalized = NormalizeFolder(folderPath);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant())));
        var leaf = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(leaf)) leaf = normalized.Replace(":", string.Empty).Replace("\\", string.Empty);
        foreach (var c in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(c, '_');
        if (leaf.Length > 40) leaf = leaf[..40];

        return Path.Combine(SnapshotsDir, $"{leaf}-{hash[..12]}.json");
    }

    /// <summary>Trailing separators removed so the same folder always hashes the same way.</summary>
    public static string NormalizeFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return string.Empty;
        var trimmed = folderPath.Trim();
        // A drive root ("C:\") must keep its separator to stay a valid path.
        if (trimmed.Length > 3) trimmed = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    /// <summary>Writes via a temp file so a crash mid-write can never leave truncated state behind.</summary>
    public static void WriteFileAtomic(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(false));

        if (File.Exists(path))
        {
            File.Replace(temp, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
