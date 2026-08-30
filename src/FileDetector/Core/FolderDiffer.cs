namespace FileDetector.Core;

/// <summary>
/// Compares two snapshots of a folder's top level. Drives both the startup catch-up
/// ("what changed while the app was closed") and the periodic safety rescan.
/// </summary>
public static class FolderDiffer
{
    /// <summary>
    /// Produces the changes needed to turn <paramref name="previous"/> into <paramref name="current"/>.
    /// A rename performed while the app was not watching is indistinguishable from a delete plus an
    /// add, so it is reported as exactly that — the snapshot holds no identity to match on.
    /// </summary>
    public static IReadOnlyList<FileChange> Diff(FolderSnapshot previous, FolderSnapshot current, bool includeModified = true)
    {
        var before = previous.ToMap();
        var after = current.ToMap();
        var changes = new List<FileChange>();

        foreach (var kv in after)
        {
            var entry = kv.Value;
            if (!before.TryGetValue(kv.Key, out var old))
            {
                changes.Add(new FileChange
                {
                    Kind = ChangeKind.Added,
                    FolderPath = current.Path,
                    Name = entry.Name,
                    IsDirectory = entry.IsDirectory,
                    Size = entry.Size,
                    TimestampUtc = entry.LastWriteUtc,
                });
                continue;
            }

            if (!includeModified) continue;

            // Directory timestamps churn whenever their contents change, and we deliberately do not
            // watch inside subfolders, so only files are eligible for "modified".
            if (entry.IsDirectory || old.IsDirectory) continue;

            if (entry.Size != old.Size || entry.LastWriteUtc != old.LastWriteUtc)
            {
                changes.Add(new FileChange
                {
                    Kind = ChangeKind.Modified,
                    FolderPath = current.Path,
                    Name = entry.Name,
                    IsDirectory = false,
                    Size = entry.Size,
                    TimestampUtc = entry.LastWriteUtc,
                });
            }
        }

        foreach (var kv in before)
        {
            if (after.ContainsKey(kv.Key)) continue;

            var entry = kv.Value;
            changes.Add(new FileChange
            {
                Kind = ChangeKind.Deleted,
                FolderPath = current.Path,
                Name = entry.Name,
                IsDirectory = entry.IsDirectory,
                Size = entry.Size,
                TimestampUtc = DateTime.UtcNow,
            });
        }

        return ChangeSorter.Sort(changes);
    }
}

/// <summary>Presentation order shared by batches, the modal and the history log.</summary>
public static class ChangeSorter
{
    private static int KindRank(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => 0,
        ChangeKind.Renamed => 1,
        ChangeKind.Modified => 2,
        ChangeKind.Deleted => 3,
        _ => 4,
    };

    public static IReadOnlyList<FileChange> Sort(IEnumerable<FileChange> changes) =>
        changes
            .OrderBy(c => KindRank(c.Kind))
            .ThenByDescending(c => c.IsDirectory)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
