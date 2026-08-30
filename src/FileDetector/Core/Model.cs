using System.Text.Json.Serialization;

namespace FileDetector.Core;

/// <summary>What happened to an item in a watched folder.</summary>
public enum ChangeKind
{
    Added,
    Renamed,
    Modified,
    Deleted,
}

/// <summary>One direct child of a watched folder, as recorded in a snapshot.</summary>
public sealed record FolderEntry(
    string Name,
    bool IsDirectory,
    long Size,
    DateTime LastWriteUtc);

/// <summary>The persisted contents of a watched folder's top level at a point in time.</summary>
public sealed class FolderSnapshot
{
    public string Path { get; set; } = string.Empty;
    public DateTime CapturedUtc { get; set; }
    public List<FolderEntry> Entries { get; set; } = new();

    public static FolderSnapshot Empty(string path) =>
        new() { Path = path, CapturedUtc = DateTime.MinValue, Entries = new List<FolderEntry>() };

    public Dictionary<string, FolderEntry> ToMap()
    {
        var map = new Dictionary<string, FolderEntry>(Entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries)
        {
            map[e.Name] = e;
        }
        return map;
    }
}

/// <summary>A single detected change. Immutable so coalescing can rewrite copies with <c>with</c>.</summary>
public sealed record FileChange
{
    public required ChangeKind Kind { get; init; }
    public required string FolderPath { get; init; }
    public required string Name { get; init; }

    /// <summary>Previous name, set only when <see cref="Kind"/> is <see cref="ChangeKind.Renamed"/>.</summary>
    public string? OldName { get; init; }

    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    [JsonIgnore]
    public string FullPath => System.IO.Path.Combine(FolderPath, Name);

    public string Describe() => Kind switch
    {
        ChangeKind.Renamed => $"{OldName} → {Name}",
        _ => Name,
    };
}

/// <summary>A debounced group of changes belonging to one watched folder.</summary>
public sealed class ChangeBatch
{
    public required string FolderPath { get; init; }
    public required IReadOnlyList<FileChange> Changes { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>True when this batch was produced by the startup catch-up scan ("while you were away").</summary>
    public bool IsCatchUp { get; init; }

    public int Count => Changes.Count;
    public bool IsEmpty => Changes.Count == 0;

    public string FolderName
    {
        get
        {
            var trimmed = FolderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var leaf = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(leaf) ? FolderPath : leaf;
        }
    }

    public int CountOf(ChangeKind kind)
    {
        var n = 0;
        foreach (var c in Changes)
        {
            if (c.Kind == kind) n++;
        }
        return n;
    }

    /// <summary>Short one-line summary used for toast titles and history rows.</summary>
    public string Summarize()
    {
        var parts = new List<string>(4);
        var added = CountOf(ChangeKind.Added);
        var renamed = CountOf(ChangeKind.Renamed);
        var modified = CountOf(ChangeKind.Modified);
        var deleted = CountOf(ChangeKind.Deleted);

        if (added > 0) parts.Add($"{added} new");
        if (renamed > 0) parts.Add($"{renamed} renamed");
        if (modified > 0) parts.Add($"{modified} changed");
        if (deleted > 0) parts.Add($"{deleted} deleted");

        var what = parts.Count == 0 ? "no changes" : string.Join(", ", parts);
        return $"{what} in {FolderName}";
    }
}
