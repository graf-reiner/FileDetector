using FileDetector.Core;

namespace FileDetector.Services;

/// <summary>
/// Owns one <see cref="FolderMonitor"/> per configured folder and is the single place the UI
/// talks to. Batches are filtered by the user's notification preferences and marshalled onto
/// the UI thread before they are raised.
/// </summary>
public sealed class WatchService : IDisposable
{
    private readonly SnapshotStore _snapshots = new();
    private readonly Dictionary<string, FolderMonitor> _monitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizationContext _ui;
    private readonly object _gate = new();
    private AppSettings _settings;

    public WatchService(AppSettings settings, SynchronizationContext uiContext)
    {
        _settings = settings;
        _ui = uiContext;
    }

    /// <summary>Raised on the UI thread with only the change kinds the user asked to see.</summary>
    public event Action<ChangeBatch>? BatchReady;

    /// <summary>Raised on the UI thread whenever a folder's availability or the paused state changes.</summary>
    public event Action? StatusChanged;

    public bool IsPaused { get; private set; }

    public IReadOnlyList<FolderMonitor> Monitors
    {
        get { lock (_gate) return _monitors.Values.ToList(); }
    }

    public int WatchingCount
    {
        get { lock (_gate) return _monitors.Values.Count(m => m.Status == MonitorStatus.Watching); }
    }

    public int UnavailableCount
    {
        get { lock (_gate) return _monitors.Values.Count(m => m.Status == MonitorStatus.Unavailable); }
    }

    public void Start()
    {
        Sync(_settings);
    }

    /// <summary>
    /// Applies new settings live: folders that were removed stop watching, new ones start
    /// (silently baselining), and survivors keep their snapshot and pick up the new tuning.
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        Sync(settings);
    }

    public void Pause()
    {
        if (IsPaused) return;
        IsPaused = true;

        lock (_gate)
        {
            foreach (var monitor in _monitors.Values) monitor.Stop();
        }

        Log.Info("watching paused");
        RaiseStatusChanged();
    }

    /// <summary>
    /// Resumes and silently re-baselines. Pausing is how you say "I am about to churn this
    /// folder, leave me alone" — replaying that churn on resume would defeat the point.
    /// </summary>
    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;

        lock (_gate)
        {
            foreach (var monitor in _monitors.Values)
            {
                monitor.Rebaseline();
                monitor.Start();
            }
        }

        Log.Info("watching resumed (folders re-baselined)");
        RaiseStatusChanged();
    }

    /// <summary>User-initiated full scan of every folder; reports whatever it finds.</summary>
    public void RescanAll()
    {
        List<FolderMonitor> monitors;
        lock (_gate) monitors = _monitors.Values.ToList();

        foreach (var monitor in monitors)
        {
            try
            {
                monitor.Rescan();
            }
            catch (Exception ex)
            {
                Log.Error($"manual rescan failed for '{monitor.FolderPath}'", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var monitor in _monitors.Values) monitor.Dispose();
            _monitors.Clear();
        }
    }

    private void Sync(AppSettings settings)
    {
        var wanted = settings.EnabledFolderPaths.ToList();

        lock (_gate)
        {
            foreach (var path in _monitors.Keys.Except(wanted, StringComparer.OrdinalIgnoreCase).ToList())
            {
                _monitors[path].Dispose();
                _monitors.Remove(path);
                Log.Info($"stopped watching '{path}'");
            }

            foreach (var path in wanted)
            {
                if (_monitors.TryGetValue(path, out var existing))
                {
                    existing.ApplySettings(settings);
                    continue;
                }

                var monitor = new FolderMonitor(path, settings, _snapshots);
                monitor.BatchReady += OnBatchReady;
                monitor.StatusChanged += _ => RaiseStatusChanged();
                _monitors[path] = monitor;

                if (!IsPaused) monitor.Start();
            }
        }

        RaiseStatusChanged();
    }

    private void OnBatchReady(ChangeBatch batch)
    {
        if (IsPaused) return;

        var visible = batch.Changes.Where(c => _settings.ShouldNotify(c.Kind)).ToList();
        if (visible.Count == 0) return;

        var filtered = new ChangeBatch
        {
            FolderPath = batch.FolderPath,
            Changes = visible,
            IsCatchUp = batch.IsCatchUp,
            CreatedUtc = batch.CreatedUtc,
        };

        _ui.Post(_ => BatchReady?.Invoke(filtered), null);
    }

    private void RaiseStatusChanged() => _ui.Post(_ => StatusChanged?.Invoke(), null);
}
