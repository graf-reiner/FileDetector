using FileDetector.Core;
using FileDetector.Services;

namespace FileDetector.Ui;

/// <summary>Configuration window: which folders to watch, what to ignore, and how to notify.</summary>
public sealed class SettingsForm : Form
{
    private readonly ListView _folderList = new();
    private readonly Button _addButton = new();
    private readonly Button _removeButton = new();

    private readonly CheckBox _startWithWindows = new();
    private readonly CheckBox _showToast = new();
    private readonly CheckBox _showModal = new();
    private readonly CheckBox _notifyRenamed = new();
    private readonly CheckBox _notifyDeleted = new();
    private readonly CheckBox _notifyModified = new();

    private readonly NumericUpDown _debounce = new();
    private readonly NumericUpDown _rescan = new();
    private readonly TextBox _ignorePatterns = new();

    private readonly AppSettings _working;

    public SettingsForm(AppSettings settings)
    {
        _working = settings.Clone();

        Text = "FileDetector — Settings";
        Icon = IconProvider.App;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ClientSize = new Size(680, 730);
        MinimumSize = new Size(620, 690);
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        BuildLayout();
        LoadFrom(_working);
    }

    /// <summary>Populated with the edited settings when the dialog returns <see cref="DialogResult.OK"/>.</summary>
    public AppSettings Result => _working;

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12),
        };
        // Without an explicit column style the single column auto-sizes to its widest child and
        // pushes content past the form's edge instead of wrapping inside it.
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // folders — absorbs slack
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // notifications
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // timing
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));  // ignore patterns
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));   // buttons

        root.Controls.Add(BuildFoldersGroup(), 0, 0);
        root.Controls.Add(BuildNotificationsGroup(), 0, 1);
        root.Controls.Add(BuildTimingGroup(), 0, 2);
        root.Controls.Add(BuildIgnoreGroup(), 0, 3);
        root.Controls.Add(BuildButtons(), 0, 4);

        Controls.Add(root);
    }

    private Control BuildFoldersGroup()
    {
        var group = new GroupBox
        {
            Text = "Watched folders",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            MinimumSize = new Size(0, 160),
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _folderList.Dock = DockStyle.Fill;
        _folderList.View = View.Details;
        _folderList.CheckBoxes = true;
        _folderList.FullRowSelect = true;
        _folderList.HideSelection = false;
        _folderList.Columns.Add("Folder", 330);
        _folderList.Columns.Add("Status", 80);
        _folderList.ItemChecked += (_, _) => _removeButton.Enabled = _folderList.SelectedItems.Count > 0;
        _folderList.SelectedIndexChanged += (_, _) => _removeButton.Enabled = _folderList.SelectedItems.Count > 0;
        _folderList.DoubleClick += (_, _) =>
        {
            if (_folderList.SelectedItems.Count > 0) ShellHelper.OpenFolder(_folderList.SelectedItems[0].Text);
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(8, 0, 0, 0),
        };

        _addButton.Text = "Add…";
        _addButton.AutoSize = true;
        _addButton.Padding = new Padding(8, 2, 8, 2);
        _addButton.Click += (_, _) => AddFolder();

        _removeButton.Text = "Remove";
        _removeButton.AutoSize = true;
        _removeButton.Padding = new Padding(8, 2, 8, 2);
        _removeButton.Enabled = false;
        _removeButton.Click += (_, _) => RemoveSelectedFolder();

        var hint = new Label
        {
            Text = "Only items created directly\nin these folders are reported —\nsubfolder contents are not.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 12, 0, 0),
        };

        buttons.Controls.Add(_addButton);
        buttons.Controls.Add(_removeButton);
        buttons.Controls.Add(hint);

        layout.Controls.Add(_folderList, 0, 0);
        layout.Controls.Add(buttons, 1, 0);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildNotificationsGroup()
    {
        var group = new GroupBox
        {
            Text = "Notifications",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10),
            Margin = new Padding(0, 8, 0, 0),
        };

        // A fixed grid rather than a wrapping FlowLayoutPanel: an auto-sizing flow panel reports
        // its unwrapped width as its preferred size and stretches the whole dialog.
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        for (var i = 0; i < 3; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Configure(_showToast, "Show toast notification");
        Configure(_showModal, "Show popup window");
        Configure(_notifyRenamed, "Report renames");
        Configure(_notifyDeleted, "Report deletions");
        Configure(_notifyModified, "Report modified files");
        Configure(_startWithWindows, "Start with Windows");

        grid.Controls.Add(_showToast, 0, 0);
        grid.Controls.Add(_showModal, 1, 0);
        grid.Controls.Add(_startWithWindows, 2, 0);
        grid.Controls.Add(_notifyRenamed, 0, 1);
        grid.Controls.Add(_notifyDeleted, 1, 1);
        grid.Controls.Add(_notifyModified, 2, 1);

        group.Controls.Add(grid);
        return group;

        static void Configure(CheckBox box, string text)
        {
            box.Text = text;
            box.AutoSize = true;
            box.Margin = new Padding(0, 4, 12, 4);
        }
    }

    private Control BuildTimingGroup()
    {
        var group = new GroupBox
        {
            Text = "Timing",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10),
            Margin = new Padding(0, 8, 0, 0),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _debounce.DecimalPlaces = 1;
        _debounce.Increment = 0.5m;
        _debounce.Minimum = 0.5m;
        _debounce.Maximum = 60m;
        _debounce.Width = 70;

        _rescan.Minimum = 1;
        _rescan.Maximum = 1440;
        _rescan.Width = 70;

        grid.Controls.Add(new Label { Text = "Group changes for", AutoSize = true, Margin = new Padding(0, 6, 6, 4), Anchor = AnchorStyles.Left }, 0, 0);
        grid.Controls.Add(_debounce, 1, 0);
        grid.Controls.Add(new Label { Text = "seconds before notifying", AutoSize = true, Margin = new Padding(6, 6, 0, 4), Anchor = AnchorStyles.Left }, 2, 0);

        grid.Controls.Add(new Label { Text = "Full rescan every", AutoSize = true, Margin = new Padding(0, 6, 6, 4), Anchor = AnchorStyles.Left }, 0, 1);
        grid.Controls.Add(_rescan, 1, 1);
        grid.Controls.Add(new Label { Text = "minutes", AutoSize = true, Margin = new Padding(6, 6, 0, 4), Anchor = AnchorStyles.Left }, 2, 1);

        group.Controls.Add(grid);
        return group;
    }

    private Control BuildIgnoreGroup()
    {
        var group = new GroupBox
        {
            Text = "Ignore patterns (one per line, * and ? wildcards)",
            Dock = DockStyle.Fill,
            Height = 110,
            Padding = new Padding(10),
            Margin = new Padding(0, 8, 0, 0),
        };

        _ignorePatterns.Dock = DockStyle.Fill;
        _ignorePatterns.Multiline = true;
        _ignorePatterns.ScrollBars = ScrollBars.Vertical;
        _ignorePatterns.WordWrap = false;

        group.Controls.Add(_ignorePatterns);
        return group;
    }

    private Control BuildButtons()
    {
        // Fills a fixed-height row with AutoSize off: an auto-sizing flow panel reports a preferred
        // size larger than its cell and pushes the buttons past the form's padding.
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = false,
            Margin = new Padding(0, 8, 0, 0),
        };

        var cancel = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(10, 3, 10, 3), DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "OK", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
        ok.Click += (_, _) => Commit();

        flow.Controls.Add(cancel);
        flow.Controls.Add(ok);

        AcceptButton = ok;
        CancelButton = cancel;
        return flow;
    }

    private void LoadFrom(AppSettings settings)
    {
        _folderList.Items.Clear();
        foreach (var folder in settings.WatchedFolders)
        {
            AddFolderRow(folder.Path, folder.Enabled);
        }

        _showToast.Checked = settings.ShowToast;
        _showModal.Checked = settings.ShowModal;
        _notifyRenamed.Checked = settings.NotifyOnRenamed;
        _notifyDeleted.Checked = settings.NotifyOnDeleted;
        _notifyModified.Checked = settings.NotifyOnModified;

        // Read the registry, not the settings file — the two can drift.
        _startWithWindows.Checked = StartupRegistration.IsEnabled();

        _debounce.Value = (decimal)Math.Clamp(settings.DebounceSeconds, 0.5, 60);
        _rescan.Value = Math.Clamp(settings.RescanMinutes, 1, 1440);
        _ignorePatterns.Text = string.Join(Environment.NewLine, settings.IgnorePatterns);
    }

    private void AddFolderRow(string path, bool enabled)
    {
        var status = Directory.Exists(path) ? "OK" : "Missing";
        var item = new ListViewItem(new[] { path, status }) { Checked = enabled };
        if (status == "Missing") item.ForeColor = Color.FromArgb(170, 90, 30);
        _folderList.Items.Add(item);
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder to watch",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var path = AppPaths.NormalizeFolder(dialog.SelectedPath);
        // Nested folders are allowed on purpose: watching C:\Intake and C:\Intake\Archive are
        // different watches, because neither one looks inside its subfolders.
        foreach (ListViewItem existing in _folderList.Items)
        {
            if (string.Equals(existing.Text, path, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "That folder is already being watched.", "FileDetector",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                existing.Selected = true;
                return;
            }
        }

        AddFolderRow(path, enabled: true);
    }

    private void RemoveSelectedFolder()
    {
        foreach (ListViewItem item in _folderList.SelectedItems)
        {
            _folderList.Items.Remove(item);
        }
        _removeButton.Enabled = _folderList.SelectedItems.Count > 0;
    }

    private void Commit()
    {
        _working.WatchedFolders = _folderList.Items
            .Cast<ListViewItem>()
            .Select(i => new WatchedFolder { Path = i.Text, Enabled = i.Checked })
            .ToList();

        _working.ShowToast = _showToast.Checked;
        _working.ShowModal = _showModal.Checked;
        _working.NotifyOnRenamed = _notifyRenamed.Checked;
        _working.NotifyOnDeleted = _notifyDeleted.Checked;
        _working.NotifyOnModified = _notifyModified.Checked;
        _working.DebounceSeconds = (double)_debounce.Value;
        _working.RescanMinutes = (int)_rescan.Value;

        var patterns = _ignorePatterns.Lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _working.IgnorePatterns = patterns;

        if (!_showToast.Checked && !_showModal.Checked)
        {
            var proceed = MessageBox.Show(this,
                "Both the toast and the popup window are switched off, so detected changes will only appear in the history window.\n\nSave anyway?",
                "FileDetector", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (proceed != DialogResult.Yes) return;
        }

        if (_startWithWindows.Checked != StartupRegistration.IsEnabled())
        {
            if (!StartupRegistration.SetEnabled(_startWithWindows.Checked))
            {
                MessageBox.Show(this, "Could not update the 'Start with Windows' setting. See the log for details.",
                    "FileDetector", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        _working.StartWithWindows = StartupRegistration.IsEnabled();

        DialogResult = DialogResult.OK;
        Close();
    }
}
