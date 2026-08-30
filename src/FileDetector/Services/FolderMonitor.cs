using FileDetector.Core;

namespace FileDetector.Services;

public enum MonitorStatus
{
    Stopped,
    Watching,
    Unavailable,
}

/// <summary>
/// Watches the top level of one folder. Combines three sources of truth, because none of them
/// is sufficient alone:
///   1. FileSystemWatcher for immediate events,
///   2. a stability gate so half-copied files are not announced,
///   3. a periodic full rescan against the stored snapshot, which catches everything the
///      watcher misses (buffer overflows, network shares, sleep/resume).
/// </summary>
public sealed class FolderMonitor : IDisposable
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StabilityTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StabilityRecheck = TimeSpan.FromMilliseconds(500);

    private readonly SnapshotStore _snapshots;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingItem> _pendingStability = new(StringComparer.OrdinalIgnoreCase);

    private AppSettings _settings;
    private IgnoreMatcher _ignore;
    private ChangeAggregator _aggregator;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _pump;
    private DateTime _nextRescanUtc;
    private DateTime _nextRetryUtc;
    private bool _disposed;

    public FolderMonitor(string folderPath, AppSettings settings, SnapshotStore snapshots)
    {
        FolderPath = AppPaths.NormalizeFolder(folderPath);
        _settings = settings;
        _snapshots = snapshots;
        _ignore = new IgnoreMatcher(settings.IgnorePatterns);
        _aggregator = new ChangeAggregator(FolderPath, settings.Debounce, settings.MaxBatchWait);
    }

    public string FolderPath { get; }

    public MonitorStatus Status { get; private set; } = MonitorStatus.Stopped;

    /// <summary>Raised off the UI thread; the owner is responsible for marshalling.</summary>
    public event Action<ChangeBatch>? BatchReady;

    public event Action<FolderMonitor>? StatusChanged;

    /// <summary>
    /// Starts watching. When a snapshot already exists, any difference found now is reported as a
    /// catch-up batch ("while you were away"). A folder with no snapshot is baselined silently so
    /// adding a populated folder does not fire a notification for every existing item.
    /// </summary>
    public void Start()
    {
        var existing = _snapshots.Load(FolderPath);

        if (!Directory.Exists(FolderPath))
        {
            SetStatus(MonitorStatus.Unavailable);
            _nextRetryUtc = DateTime.UtcNow + RetryInterval;
            Log.Warn($"watched folder is unavailable: {FolderPath}");
            StartPump();
            return;
        }

        if (existing is null)
        {
            var baseline = FolderScanner.Scan(FolderPath, _ignore);
            _snapshots.Save(baseline);
            Log.Info($"baselined '{FolderPath}' with {baseline.Entries.Count} item(s)");
        }
        else
        {
            EmitDiffAgainstDisk(existing, isCatchUp: true);
        }

        StartWatcher();
        _nextRescanUtc = DateTime.UtcNow + _settings.RescanInterval;
        StartPump();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _pump?.Dispose();
            _pump = null;
            DisposeWatcher();
            _aggregator.Discard();
            _pendingStability.Clear();
        }
        SetStatus(MonitorStatus.Stopped);
    }

    /// <summary>Re-reads settings without losing the snapshot or restarting the watch.</summary>
    public void ApplySettings(AppSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            _ignore = new IgnoreMatcher(settings.IgnorePatterns);
            _aggregator = new ChangeAggregator(FolderPath, settings.Debounce, settings.MaxBatchWait);
            _nextRescanUtc = DateTime.UtcNow + settings.RescanInterval;
        }
    }

    /// <summary>Full scan against the stored snapshot. Used by the timer and by "Rescan now".</summary>
    public void Rescan(bool report = true)
    {
        if (!Directory.Exists(FolderPath))
        {
            if (Status != MonitorStatus.Unavailable)
            {
                Log.Warn($"watched folder disappeared: {FolderPath}");
                DisposeWatcher();
                SetStatus(MonitorStatus.Unavailable);
                _nextRetryUtc = DateTime.UtcNow + RetryInterval;
            }
            return;
        }

        var previous = _snapshots.Load(FolderPath) ?? FolderSnapshot.Empty(FolderPath);
        EmitDiffAgainstDisk(previous, isCatchUp: false, report: report);
    }

    /// <summary>Records current contents as the new baseline without reporting anything.</summary>
    public void Rebaseline()
    {
        if (!Directory.Exists(FolderPath)) return;
        _snapshots.Save(FolderScanner.Scan(FolderPath, _ignore));
        lock (_gate)
        {
            _aggregator.Discard();
            _pendingStability.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ---- internals -------------------------------------------------------

    private void StartPump()
    {
        lock (_gate)
        {
            _pump?.Dispose();
            _pump = new System.Threading.Timer(_ => Pump(), null, PumpInterval, PumpInterval);
        }
    }

    private void StartWatcher()
    {
        DisposeWatcher();
        try
        {
            var watcher = new FileSystemWatcher(FolderPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += OnCreated;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            watcher.Changed += OnChanged;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            SetStatus(MonitorStatus.Watching);
            Log.Info($"watching '{FolderPath}'");
        }
        catch (Exception ex)
        {
            Log.Error($"could not watch '{FolderPath}'", ex);
            SetStatus(MonitorStatus.Unavailable);
            _nextRetryUtc = DateTime.UtcNow + RetryInterval;
        }
    }

    private void DisposeWatcher()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCreated;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Changed -= OnChanged;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"error disposing watcher for '{FolderPath}': {ex.Message}");
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Usually an overflowed internal buffer: events were dropped, so trust the disk instead.
        Log.Error($"watcher error on '{FolderPath}', falling back to a full rescan", e.GetException());
        try
        {
            StartWatcher();
            Rescan();
        }
        catch (Exception ex)
        {
            Log.Error($"recovery rescan failed for '{FolderPath}'", ex);
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (_ignore.IsIgnored(e.Name ?? string.Empty)) return;
        QueueArrival(e.Name!, renamedFrom: null);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        var name = e.Name;
        if (string.IsNullOrEmpty(name) || _ignore.IsIgnored(name)) return;

        lock (_gate)
        {
            // Never reported in the first place, so nothing to retract.
            if (_pendingStability.Remove(name)) return;
        }

        _aggregator.Add(new FileChange
        {
            Kind = ChangeKind.Deleted,
            FolderPath = FolderPath,
            Name = name,
            IsDirectory = false,
            TimestampUtc = DateTime.UtcNow,
        }, DateTime.UtcNow);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        var newName = e.Name;
        var oldName = e.OldName;
        if (string.IsNullOrEmpty(newName)) return;

        var newIgnored = _ignore.IsIgnored(newName);
        var oldIgnored = !string.IsNullOrEmpty(oldName) && _ignore.IsIgnored(oldName);

        lock (_gate)
        {
            if (!string.IsNullOrEmpty(oldName) && _pendingStability.Remove(oldName!, out var pending))
            {
                // Still settling under its old name — keep gating it under the new one.
                if (!newIgnored)
                {
                    _pendingStability[newName] = pending with { Name = newName, RenamedFrom = pending.RenamedFrom };
                }
                return;
            }
        }

        if (newIgnored) return;   // e.g. renamed to something.tmp

        if (oldIgnored || string.IsNullOrEmpty(oldName))
        {
            // The classic download pattern: "file.crdownload" → "file.zip". The item is new to us.
            QueueArrival(newName, renamedFrom: null);
            return;
        }

        _aggregator.Add(new FileChange
        {
            Kind = ChangeKind.Renamed,
            FolderPath = FolderPath,
            Name = newName,
            OldName = oldName,
            IsDirectory = Directory.Exists(Path.Combine(FolderPath, newName)),
            TimestampUtc = DateTime.UtcNow,
        }, DateTime.UtcNow);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var name = e.Name;
        if (string.IsNullOrEmpty(name) || _ignore.IsIgnored(name)) return;

        lock (_gate)
        {
            // A file still being written keeps its stability window alive.
            if (_pendingStability.TryGetValue(name, out var pending))
            {
                _pendingStability[name] = pending with { LastCheckUtc = DateTime.MinValue };
                return;
            }
        }

        if (!_settings.NotifyOnModified) return;
        if (Directory.Exists(Path.Combine(FolderPath, name))) return;

        var entry = FolderScanner.Describe(FolderPath, name);
        if (entry is null) return;

        _aggregator.Add(new FileChange
        {
            Kind = ChangeKind.Modified,
            FolderPath = FolderPath,
            Name = name,
            IsDirectory = false,
            Size = entry.Size,
            TimestampUtc = DateTime.UtcNow,
        }, DateTime.UtcNow);
    }

    /// <summary>
    /// A new item appeared. Directories are reported immediately; files are held until they stop
    /// growing and can be opened exclusively, so a 2 GB copy notifies once, at the end.
    /// </summary>
    private void QueueArrival(string name, string? renamedFrom)
    {
        var full = Path.Combine(FolderPath, name);

        if (Directory.Exists(full))
        {
            _aggregator.Add(new FileChange
            {
                Kind = ChangeKind.Added,
                FolderPath = FolderPath,
                Name = name,
                IsDirectory = true,
                TimestampUtc = DateTime.UtcNow,
            }, DateTime.UtcNow);
            return;
        }

        lock (_gate)
        {
            _pendingStability[name] = new PendingItem(name, renamedFrom, DateTime.UtcNow, DateTime.MinValue, -1);
        }
    }

    private void Pump()
    {
        if (_disposed) return;

        try
        {
            var now = DateTime.UtcNow;

            if (Status == MonitorStatus.Unavailable)
            {
                if (now < _nextRetryUtc) return;
                _nextRetryUtc = now + RetryInterval;
                if (!Directory.Exists(FolderPath)) return;

                Log.Info($"'{FolderPath}' is back; resuming");
                StartWatcher();
                if (Status == MonitorStatus.Watching)
                {
                    Rescan();
                    _nextRescanUtc = DateTime.UtcNow + _settings.RescanInterval;
                }
                return;
            }

            PromoteStableItems(now);

            if (_aggregator.TryFlush(now, out var batch))
            {
                Publish(batch);
            }

            if (now >= _nextRescanUtc)
            {
                _nextRescanUtc = now + _settings.RescanInterval;
                Rescan();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"pump failure for '{FolderPath}'", ex);
        }
    }

    private void PromoteStableItems(DateTime now)
    {
        List<PendingItem>? ready = null;

        lock (_gate)
        {
            if (_pendingStability.Count == 0) return;

            foreach (var name in _pendingStability.Keys.ToList())
            {
                var item = _pendingStability[name];
                if (now - item.LastCheckUtc < StabilityRecheck) continue;

                var full = Path.Combine(FolderPath, item.Name);
                if (!File.Exists(full))
                {
                    if (Directory.Exists(full))
                    {
                        _pendingStability.Remove(name);
                        (ready ??= new List<PendingItem>()).Add(item with { IsDirectory = true });
                    }
                    else if (now - item.FirstSeenUtc > StabilityTimeout)
                    {
                        _pendingStability.Remove(name);   // vanished before it ever settled
                    }
                    continue;
                }

                long size;
                var unlocked = false;
                try
                {
                    var info = new FileInfo(full);
                    size = info.Length;
                    using (info.Open(FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        unlocked = true;
                    }
                }
                catch (IOException)
                {
                    size = -1;   // still being written to
                }
                catch (UnauthorizedAccessException)
                {
                    // Cannot open it, but it exists and we should still report it.
                    size = SafeLength(full);
                    unlocked = true;
                }

                var settled = unlocked && size >= 0 && size == item.LastSize;
                var timedOut = now - item.FirstSeenUtc > StabilityTimeout;

                if (settled || timedOut)
                {
                    _pendingStability.Remove(name);
                    (ready ??= new List<PendingItem>()).Add(item with { LastSize = size < 0 ? SafeLength(full) : size });
                }
                else
                {
                    _pendingStability[name] = item with { LastCheckUtc = now, LastSize = size };
                }
            }
        }

        if (ready is null) return;

        foreach (var item in ready)
        {
            _aggregator.Add(new FileChange
            {
                Kind = ChangeKind.Added,
                FolderPath = FolderPath,
                Name = item.Name,
                IsDirectory = item.IsDirectory,
                Size = item.LastSize < 0 ? 0 : item.LastSize,
                TimestampUtc = DateTime.UtcNow,
            }, DateTime.UtcNow);
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private void EmitDiffAgainstDisk(FolderSnapshot previous, bool isCatchUp, bool report = true)
    {
        FolderSnapshot current;
        try
        {
            current = FolderScanner.Scan(FolderPath, _ignore);
        }
        catch (Exception ex)
        {
            Log.Error($"scan failed for '{FolderPath}'", ex);
            return;
        }

        _snapshots.Save(current);

        if (!report) return;

        var changes = FolderDiffer.Diff(previous, current, includeModified: _settings.NotifyOnModified);
        if (changes.Count == 0) return;

        Publish(new ChangeBatch
        {
            FolderPath = FolderPath,
            Changes = changes,
            IsCatchUp = isCatchUp,
            CreatedUtc = DateTime.UtcNow,
        }, rebaseline: false);
    }

    private void Publish(ChangeBatch batch, bool rebaseline = true)
    {
        if (batch.IsEmpty) return;

        if (rebaseline)
        {
            // Fold the reported state back into the baseline by re-reading the folder, so the next
            // rescan does not report the same items again.
            try
            {
                if (Directory.Exists(FolderPath)) _snapshots.Save(FolderScanner.Scan(FolderPath, _ignore));
            }
            catch (Exception ex)
            {
                Log.Warn($"could not refresh snapshot for '{FolderPath}': {ex.Message}");
            }
        }

        BatchReady?.Invoke(batch);
    }

    private void SetStatus(MonitorStatus status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke(this);
    }

    private readonly record struct PendingItem(
        string Name,
        string? RenamedFrom,
        DateTime FirstSeenUtc,
        DateTime LastCheckUtc,
        long LastSize,
        bool IsDirectory = false);
}
