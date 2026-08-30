using FileDetector.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace FileDetector.Services;

public enum NotificationAction
{
    ViewDetails,
    OpenFolder,
}

/// <summary>
/// Raises the toast for a batch. Real Windows toasts are preferred (they persist in the Action
/// Center and carry buttons), but the notification platform is not guaranteed to be present —
/// notably on Server SKUs — so the tray icon's balloon is a first-class fallback, not a stub.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private const string ArgAction = "fdAction";
    private const string ArgFolder = "fdFolder";

    private readonly NotifyIcon _trayIcon;
    private readonly SynchronizationContext _ui;
    private string? _lastBalloonFolder;
    private bool _toastHandlerAttached;
    private bool _disposed;

    public NotificationService(NotifyIcon trayIcon, SynchronizationContext uiContext)
    {
        _trayIcon = trayIcon;
        _ui = uiContext;

        _trayIcon.BalloonTipClicked += OnBalloonClicked;
        ToastsAvailable = TryEnableToasts();

        Log.Info(ToastsAvailable
            ? "using Windows toast notifications"
            : "toast platform unavailable; using tray balloon notifications");
    }

    /// <summary>True when Windows toasts work on this machine; false means balloons are in use.</summary>
    public bool ToastsAvailable { get; private set; }

    /// <summary>Raised on the UI thread when the user clicks a notification or one of its buttons.</summary>
    public event Action<string, NotificationAction>? Activated;

    public void Notify(ChangeBatch batch)
    {
        var title = batch.IsCatchUp
            ? $"While you were away: {batch.Summarize()}"
            : batch.Summarize();

        var body = BuildBody(batch);

        if (ToastsAvailable && TryShowToast(title, body, batch.FolderPath)) return;

        ShowBalloon(title, body, batch.FolderPath);
    }

    /// <summary>Plain informational notification (startup, errors) with no batch behind it.</summary>
    public void ShowSimple(string title, string message)
    {
        if (ToastsAvailable && TryShowToast(title, message, folderPath: null)) return;
        ShowBalloon(title, message, folderPath: null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _trayIcon.BalloonTipClicked -= OnBalloonClicked;
        if (_toastHandlerAttached)
        {
            try
            {
                ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
            }
            catch (Exception ex)
            {
                Log.Warn($"could not detach the toast handler: {ex.Message}");
            }
        }
    }

    private static string BuildBody(ChangeBatch batch)
    {
        const int preview = 3;
        var names = batch.Changes.Take(preview).Select(c => Prefix(c) + c.Describe());
        var body = string.Join(Environment.NewLine, names);

        var remaining = batch.Count - preview;
        if (remaining > 0) body += $"{Environment.NewLine}…and {remaining} more";
        return body;
    }

    private static string Prefix(FileChange change) => change.Kind switch
    {
        ChangeKind.Added => change.IsDirectory ? "[+ folder] " : "[+] ",
        ChangeKind.Renamed => "[renamed] ",
        ChangeKind.Modified => "[changed] ",
        ChangeKind.Deleted => "[deleted] ",
        _ => string.Empty,
    };

    private bool TryEnableToasts()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
            _toastHandlerAttached = true;

            // Creating a notifier is the only reliable probe: registering the activator and reading
            // History both succeed on machines whose notification platform then refuses to show
            // anything (Windows Server, and any SKU with the platform disabled by policy). Doing it
            // here means the fallback is chosen at startup instead of losing the first toast.
            _ = ToastNotificationManagerCompat.CreateToastNotifier();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"toast notifications unavailable: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool TryShowToast(string title, string body, string? folderPath)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument(ArgAction, nameof(NotificationAction.ViewDetails))
                .AddText(title)
                .AddText(body);

            if (!string.IsNullOrEmpty(folderPath))
            {
                builder.AddArgument(ArgFolder, folderPath!);
                builder.AddButton(new ToastButton()
                    .SetContent("Open Folder")
                    .AddArgument(ArgAction, nameof(NotificationAction.OpenFolder))
                    .AddArgument(ArgFolder, folderPath!));
                builder.AddButton(new ToastButton()
                    .SetContent("View Details")
                    .AddArgument(ArgAction, nameof(NotificationAction.ViewDetails))
                    .AddArgument(ArgFolder, folderPath!));
            }

            builder.Show();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("toast failed; falling back to balloon for the rest of this session", ex);
            ToastsAvailable = false;
            return false;
        }
    }

    private void ShowBalloon(string title, string body, string? folderPath)
    {
        _lastBalloonFolder = folderPath;
        try
        {
            _trayIcon.ShowBalloonTip(10_000, Truncate(title, 60), Truncate(body, 240), ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("balloon notification failed", ex);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private void OnBalloonClicked(object? sender, EventArgs e)
    {
        var folder = _lastBalloonFolder;
        if (folder is null) return;
        Activated?.Invoke(folder, NotificationAction.ViewDetails);
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        // Activation arrives on a background thread.
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            var folder = args.Contains(ArgFolder) ? args[ArgFolder] : string.Empty;
            var action = args.Contains(ArgAction) && args[ArgAction] == nameof(NotificationAction.OpenFolder)
                ? NotificationAction.OpenFolder
                : NotificationAction.ViewDetails;

            if (string.IsNullOrEmpty(folder)) return;
            _ui.Post(_ => Activated?.Invoke(folder, action), null);
        }
        catch (Exception ex)
        {
            Log.Error("could not handle toast activation", ex);
        }
    }
}
