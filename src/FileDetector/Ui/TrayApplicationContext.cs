using FileDetector.Core;
using FileDetector.Services;

namespace FileDetector.Ui;

/// <summary>
/// The application itself: a tray icon, no main window. Owns the watch service, the notifier
/// and the three windows, and is the only place that touches the UI thread.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly HistoryStore _history = new();
    private readonly SynchronizationContext _ui;

    private AppSettings _settings;
    private WatchService _watchService;
    private NotificationService _notifier;
    private ChangesModalForm? _modal;
    private SettingsForm? _settingsForm;
    private HistoryForm? _historyForm;

    public TrayApplicationContext(bool silent)
    {
        AppPaths.EnsureCreated();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _settings = SettingsStore.Load();
        StartupRegistration.RefreshIfEnabled();

        _pauseItem = new ToolStripMenuItem("Pause watching") { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) => TogglePause();

        _trayIcon = new NotifyIcon
        {
            Icon = IconProvider.App,
            Visible = true,
            Text = "FileDetector",
            ContextMenuStrip = BuildMenu(),
        };
        _trayIcon.DoubleClick += (_, _) => ShowSettings();

        _notifier = new NotificationService(_trayIcon, _ui);
        _notifier.Activated += OnNotificationActivated;

        _watchService = new WatchService(_settings, _ui);
        _watchService.BatchReady += OnBatchReady;
        _watchService.StatusChanged += UpdateTrayState;
        _watchService.Start();

        UpdateTrayState();

        if (_settings.WatchedFolders.Count == 0)
        {
            // Nothing to watch yet — the app is useless until a folder is chosen, so ask up front.
            if (silent)
            {
                Log.Info("started silently with no folders configured");
                _notifier.ShowSimple("FileDetector is running", "No folders are being watched yet. Open Settings from the tray icon to add one.");
            }
            else
            {
                _ui.Post(_ => ShowSettings(), null);
            }
        }
        else if (!silent)
        {
            _notifier.ShowSimple("FileDetector is running", $"Watching {_settings.EnabledFolderPaths.Count()} folder(s). Right-click the tray icon for options.");
        }
    }

    /// <summary>Called when a second copy of the app is launched; surfaces this one instead.</summary>
    public void ShowSettingsFromAnotherInstance() => _ui.Post(_ => ShowSettings(), null);

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var settings = new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()) { Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripMenuItem("History…", null, (_, _) => ShowHistory()));
        menu.Items.Add(new ToolStripMenuItem("Rescan now", null, (_, _) => RescanNow()));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open log folder", null, (_, _) => ShellHelper.OpenFolder(AppPaths.Root)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        return menu;
    }

    private void OnBatchReady(ChangeBatch batch)
    {
        Log.Info($"batch: {batch.Summarize()}{(batch.IsCatchUp ? " (catch-up)" : string.Empty)}");
        _history.Append(batch);

        if (_settings.ShowToast) _notifier.Notify(batch);
        if (_settings.ShowModal) ShowModal(batch);

        _historyForm?.Reload();
    }

    private void ShowModal(ChangeBatch batch)
    {
        if (_modal is null || _modal.IsDisposed)
        {
            _modal = new ChangesModalForm();
        }
        _modal.AppendBatch(batch);
    }

    private void OnNotificationActivated(string folderPath, NotificationAction action)
    {
        if (action == NotificationAction.OpenFolder)
        {
            ShellHelper.OpenFolder(folderPath);
            return;
        }

        if (_modal is { IsDisposed: false })
        {
            _modal.ShowAndFocus();
            return;
        }
        ShellHelper.OpenFolder(folderPath);
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        using var form = new SettingsForm(_settings);
        _settingsForm = form;
        try
        {
            if (form.ShowDialog() != DialogResult.OK) return;

            _settings = form.Result;
            SettingsStore.Save(_settings);
            _watchService.ApplySettings(_settings);
            UpdateTrayState();
            Log.Info($"settings saved; watching {_settings.EnabledFolderPaths.Count()} folder(s)");
        }
        finally
        {
            _settingsForm = null;
        }
    }

    private void ShowHistory()
    {
        if (_historyForm is { IsDisposed: false })
        {
            _historyForm.Reload();
            _historyForm.Activate();
            return;
        }

        _historyForm = new HistoryForm(_history);
        _historyForm.FormClosed += (_, _) => _historyForm = null;
        _historyForm.Show();
    }

    private void RescanNow()
    {
        if (_settings.WatchedFolders.Count == 0)
        {
            _notifier.ShowSimple("Nothing to rescan", "No folders are being watched yet.");
            return;
        }

        // Off the UI thread: a rescan enumerates the disk and can block on a slow share.
        var service = _watchService;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            service.RescanAll();
            _ui.Post(__ => _notifier.ShowSimple("Rescan complete", "All watched folders were compared against their saved snapshot."), null);
        });
    }

    private void TogglePause()
    {
        if (_pauseItem.Checked) _watchService.Pause();
        else _watchService.Resume();

        UpdateTrayState();
    }

    private void UpdateTrayState()
    {
        var paused = _watchService.IsPaused;
        _trayIcon.Icon = paused ? IconProvider.Paused : IconProvider.App;

        string text;
        if (paused)
        {
            text = "FileDetector — paused";
        }
        else
        {
            var watching = _watchService.WatchingCount;
            var unavailable = _watchService.UnavailableCount;
            text = watching == 0 && unavailable == 0
                ? "FileDetector — no folders watched"
                : $"FileDetector — watching {watching} folder(s)" + (unavailable > 0 ? $", {unavailable} unavailable" : string.Empty);
        }

        // NotifyIcon.Text is capped at 63 characters by the shell.
        _trayIcon.Text = text.Length > 63 ? text[..62] + "…" : text;
    }

    private void ExitApplication()
    {
        Log.Info("exiting");
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _watchService.BatchReady -= OnBatchReady;
                _watchService.StatusChanged -= UpdateTrayState;
                _watchService.Dispose();
                _notifier.Dispose();

                _trayIcon.Visible = false;
                _trayIcon.Dispose();

                _modal?.Dispose();
                _historyForm?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error("error during shutdown", ex);
            }
        }
        base.Dispose(disposing);
    }
}
