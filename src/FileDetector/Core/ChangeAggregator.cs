namespace FileDetector.Core;

/// <summary>
/// Debounce engine for one watched folder. Raw filesystem events go in; one coalesced
/// <see cref="ChangeBatch"/> comes out once the folder has been quiet for
/// <see cref="QuietPeriod"/> — or immediately at <see cref="MaxWait"/> so a long continuous
/// copy still reports instead of being starved.
///
/// Deliberately clock-driven rather than timer-owning: the caller supplies "now" on every
/// call, which keeps burst behaviour fully testable without sleeping.
/// </summary>
public sealed class ChangeAggregator
{
    private readonly List<FileChange> _pending = new();
    private readonly object _gate = new();
    private DateTime _firstEventUtc;
    private DateTime _lastEventUtc;

    public ChangeAggregator(string folderPath, TimeSpan quietPeriod, TimeSpan maxWait)
    {
        FolderPath = folderPath;
        QuietPeriod = quietPeriod;
        MaxWait = maxWait;
    }

    public string FolderPath { get; }
    public TimeSpan QuietPeriod { get; }
    public TimeSpan MaxWait { get; }

    public bool HasPending
    {
        get { lock (_gate) return _pending.Count > 0; }
    }

    public void Add(FileChange change, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_pending.Count == 0) _firstEventUtc = nowUtc;
            _lastEventUtc = nowUtc;
            _pending.Add(change);
        }
    }

    /// <summary>
    /// Emits a batch when the quiet period has elapsed since the last event, or when the
    /// maximum wait since the first event has been exceeded. Returns false while still collecting.
    /// </summary>
    public bool TryFlush(DateTime nowUtc, out ChangeBatch batch)
    {
        lock (_gate)
        {
            batch = null!;
            if (_pending.Count == 0) return false;

            var quietElapsed = nowUtc - _lastEventUtc >= QuietPeriod;
            var maxElapsed = nowUtc - _firstEventUtc >= MaxWait;
            if (!quietElapsed && !maxElapsed) return false;

            var produced = BuildBatch(nowUtc);
            _pending.Clear();

            if (produced.IsEmpty) return false;   // everything cancelled out (e.g. created then deleted)
            batch = produced;
            return true;
        }
    }

    /// <summary>Flushes whatever is pending regardless of timing. Returns null if nothing survives coalescing.</summary>
    public ChangeBatch? ForceFlush(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return null;
            var produced = BuildBatch(nowUtc);
            _pending.Clear();
            return produced.IsEmpty ? null : produced;
        }
    }

    public void Discard()
    {
        lock (_gate) _pending.Clear();
    }

    private ChangeBatch BuildBatch(DateTime nowUtc) => new()
    {
        FolderPath = FolderPath,
        Changes = Coalesce(_pending, FolderPath),
        CreatedUtc = nowUtc,
    };

    /// <summary>
    /// Collapses a raw event stream into the net effect per item.
    ///
    /// created → deleted        = nothing happened
    /// created → renamed        = one Added under the final name
    /// renamed → deleted        = one Deleted under the original name
    /// renamed (unknown origin) = one Renamed
    /// repeated events per name = one entry carrying the newest size/timestamp
    /// </summary>
    public static IReadOnlyList<FileChange> Coalesce(IEnumerable<FileChange> changes, string folderPath)
    {
        // Keyed by the item's *current* name; rename moves the entry to its new key.
        var effective = new Dictionary<string, FileChange>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case ChangeKind.Added:
                    if (effective.TryGetValue(change.Name, out var existingAdd))
                    {
                        effective[change.Name] = existingAdd.Kind == ChangeKind.Deleted
                            ? change                                   // deleted then re-created: it exists now
                            : Freshen(existingAdd, change);            // keep Added/Renamed, take newest metadata
                    }
                    else
                    {
                        effective[change.Name] = change;
                    }
                    break;

                case ChangeKind.Modified:
                    if (effective.TryGetValue(change.Name, out var existingMod))
                    {
                        effective[change.Name] = Freshen(existingMod, change);
                    }
                    else
                    {
                        effective[change.Name] = change;
                    }
                    break;

                case ChangeKind.Renamed:
                    var from = change.OldName ?? string.Empty;
                    if (from.Length > 0 && effective.TryGetValue(from, out var existingRen))
                    {
                        effective.Remove(from);
                        effective[change.Name] = existingRen.Kind switch
                        {
                            // It was created in this same window, so the rename is just its final name.
                            ChangeKind.Added => existingRen with { Name = change.Name, Size = change.Size, TimestampUtc = change.TimestampUtc },
                            // Already a rename: chain it back to the original name.
                            ChangeKind.Renamed => change with { OldName = existingRen.OldName ?? from },
                            _ => change,
                        };
                    }
                    else
                    {
                        effective[change.Name] = change;
                    }
                    break;

                case ChangeKind.Deleted:
                    if (effective.TryGetValue(change.Name, out var existingDel))
                    {
                        effective.Remove(change.Name);
                        if (existingDel.Kind == ChangeKind.Added)
                        {
                            break;   // created and gone again inside the window: report nothing
                        }
                        if (existingDel.Kind == ChangeKind.Renamed && !string.IsNullOrEmpty(existingDel.OldName))
                        {
                            // Renamed then deleted: from the outside world's view the original is gone.
                            effective[existingDel.OldName!] = change with { Name = existingDel.OldName!, OldName = null };
                            break;
                        }
                        effective[change.Name] = change;
                    }
                    else
                    {
                        effective[change.Name] = change;
                    }
                    break;
            }
        }

        return ChangeSorter.Sort(effective.Values);
    }

    private static FileChange Freshen(FileChange keep, FileChange newer) => keep with
    {
        Size = newer.Size,
        IsDirectory = keep.IsDirectory || newer.IsDirectory,
        TimestampUtc = newer.TimestampUtc,
    };
}
