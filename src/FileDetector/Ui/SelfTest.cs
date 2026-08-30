using FileDetector.Core;
using FileDetector.Services;

namespace FileDetector.Ui;

/// <summary>
/// <c>FileDetector.exe --selftest-ui</c>: builds every window, forces its handle to be created and
/// pushes a synthetic batch through the modal, then exits. Exit code 0 means the UI constructs
/// cleanly. Exists because the windows cannot otherwise be checked on a headless or automated
/// machine — a broken layout would otherwise only show up in front of the user.
/// </summary>
internal static class SelfTest
{
    public static int RunUi()
    {
        AppPaths.EnsureCreated();
        ApplicationConfiguration.Initialize();

        var failures = new List<string>();

        Check(failures, "icons", () =>
        {
            _ = IconProvider.App;
            _ = IconProvider.Paused;
        });

        Check(failures, "ChangesModalForm", () =>
        {
            using var form = new ChangesModalForm();
            form.CreateControl();
            _ = form.Handle;
            form.AppendBatch(SyntheticBatch());
            form.Hide();
        });

        Check(failures, "SettingsForm", () =>
        {
            using var form = new SettingsForm(new AppSettings
            {
                WatchedFolders = { new WatchedFolder { Path = AppPaths.Root, Enabled = true } },
            });
            form.CreateControl();
            _ = form.Handle;
        });

        Check(failures, "HistoryForm", () =>
        {
            using var form = new HistoryForm(new HistoryStore());
            form.CreateControl();
            _ = form.Handle;
        });

        Check(failures, "NotifyIcon + notifier", () =>
        {
            using var icon = new NotifyIcon { Icon = IconProvider.App, Visible = false, Text = "FileDetector self-test" };
            using var notifier = new NotificationService(icon, new WindowsFormsSynchronizationContext());
            Log.Info($"self-test: toasts available = {notifier.ToastsAvailable}");
        });

        if (failures.Count == 0)
        {
            Log.Info("UI self-test passed");
            return 0;
        }

        foreach (var failure in failures) Log.Error($"UI self-test: {failure}");
        return 1;
    }

    private static void Check(List<string> failures, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failures.Add($"{what} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ChangeBatch SyntheticBatch()
    {
        const string folder = @"C:\SelfTest";
        return new ChangeBatch
        {
            FolderPath = folder,
            IsCatchUp = true,
            Changes = new[]
            {
                new FileChange { Kind = ChangeKind.Added, FolderPath = folder, Name = "New Folder", IsDirectory = true },
                new FileChange { Kind = ChangeKind.Added, FolderPath = folder, Name = "invoice_001.pdf", Size = 12345 },
                new FileChange { Kind = ChangeKind.Renamed, FolderPath = folder, Name = "final.zip", OldName = "draft.zip", Size = 999 },
                new FileChange { Kind = ChangeKind.Deleted, FolderPath = folder, Name = "old.txt" },
            },
        };
    }
}
