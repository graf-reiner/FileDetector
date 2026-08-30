using FileDetector.Core;
using FileDetector.Services;

namespace FileDetector.Ui;

/// <summary>
/// The popup window shown when a batch lands. One window is reused for the whole session: a
/// second batch arriving while it is open appends to the list and re-activates it, so a busy
/// folder can never bury the screen in stacked dialogs.
/// </summary>
public sealed class ChangesModalForm : Form
{
    private const int MaxRows = 2000;

    private readonly Label _headerLabel = new();
    private readonly Label _subLabel = new();
    private readonly ListView _list = new();
    private readonly Button _openFolderButton = new();
    private readonly Button _openItemButton = new();
    private readonly Button _dismissButton = new();

    private readonly Dictionary<ChangeKind, ListViewGroup> _groups = new();
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private int _totalChanges;
    private int _shownRows;
    private bool _sawCatchUp;
    private string? _primaryFolder;

    public ChangesModalForm()
    {
        Text = "FileDetector — new items detected";
        Icon = IconProvider.App;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = true;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(660, 460);
        MinimumSize = new Size(520, 320);
        KeyPreview = true;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        BuildLayout();
    }

    /// <summary>Adds a batch to the window, creating group headers and refreshing the counts.</summary>
    public void AppendBatch(ChangeBatch batch)
    {
        if (batch.IsEmpty) return;

        _folders.Add(batch.FolderPath);
        _primaryFolder ??= batch.FolderPath;
        _sawCatchUp |= batch.IsCatchUp;
        _totalChanges += batch.Count;

        _list.BeginUpdate();
        try
        {
            foreach (var change in batch.Changes)
            {
                if (_shownRows >= MaxRows) break;

                var item = new ListViewItem(new[]
                {
                    change.Kind == ChangeKind.Renamed ? change.Describe() : change.Name,
                    ShellHelper.KindLabel(change.Kind),
                    ShellHelper.TypeLabel(change.IsDirectory, change.Name),
                    ShellHelper.FormatSize(change.Size, change.IsDirectory),
                    batch.CreatedUtc.ToLocalTime().ToString("HH:mm:ss"),
                    batch.FolderName,
                })
                {
                    Tag = change,
                    Group = GroupFor(change.Kind),
                    ForeColor = change.Kind == ChangeKind.Deleted ? Color.FromArgb(150, 60, 60) : SystemColors.WindowText,
                };

                _list.Items.Add(item);
                _shownRows++;
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        RefreshHeaders();
        ShowAndFocus();
    }

    public void ShowAndFocus()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;

        TopMost = true;
        BringToFront();
        Activate();
    }

    /// <summary>Clears the window so the next batch starts a fresh list.</summary>
    private void Reset()
    {
        _list.Items.Clear();
        _list.Groups.Clear();
        _groups.Clear();
        _folders.Clear();
        _totalChanges = 0;
        _shownRows = 0;
        _sawCatchUp = false;
        _primaryFolder = null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The tray app owns this window's lifetime; closing just hides and empties it.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            Reset();
            return;
        }
        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _headerLabel.AutoSize = true;
        _headerLabel.Font = new Font(Font.FontFamily, Font.Size + 3f, FontStyle.Bold);
        _headerLabel.Margin = new Padding(0, 0, 0, 2);
        _headerLabel.Text = "New items detected";

        _subLabel.AutoSize = true;
        _subLabel.ForeColor = SystemColors.GrayText;
        _subLabel.Margin = new Padding(0, 0, 0, 8);

        var headerPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };
        headerPanel.Controls.Add(_headerLabel);
        headerPanel.Controls.Add(_subLabel);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.ShowGroups = true;
        _list.Columns.Add("Name", 250);
        _list.Columns.Add("Change", 80);
        _list.Columns.Add("Type", 90);
        _list.Columns.Add("Size", 80);
        _list.Columns.Add("Time", 70);
        _list.Columns.Add("Folder", 120);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => OpenSelectedItem();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 10, 0, 0),
        };

        _dismissButton.Text = "Dismiss";
        _dismissButton.AutoSize = true;
        _dismissButton.Padding = new Padding(10, 3, 10, 3);
        _dismissButton.Click += (_, _) => Close();

        _openItemButton.Text = "Open Item";
        _openItemButton.AutoSize = true;
        _openItemButton.Padding = new Padding(10, 3, 10, 3);
        _openItemButton.Enabled = false;
        _openItemButton.Click += (_, _) => OpenSelectedItem();

        _openFolderButton.Text = "Open Folder";
        _openFolderButton.AutoSize = true;
        _openFolderButton.Padding = new Padding(10, 3, 10, 3);
        _openFolderButton.Click += (_, _) => OpenFolder();

        buttons.Controls.Add(_dismissButton);
        buttons.Controls.Add(_openItemButton);
        buttons.Controls.Add(_openFolderButton);

        root.Controls.Add(headerPanel, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        CancelButton = _dismissButton;
        AcceptButton = _openFolderButton;
    }

    private ListViewGroup GroupFor(ChangeKind kind)
    {
        if (_groups.TryGetValue(kind, out var group)) return group;

        group = new ListViewGroup(ShellHelper.KindLabel(kind));
        _groups[kind] = group;
        _list.Groups.Add(group);
        return group;
    }

    private void RefreshHeaders()
    {
        var counts = new Dictionary<ChangeKind, int>();
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is not FileChange change) continue;
            counts[change.Kind] = counts.GetValueOrDefault(change.Kind) + 1;
        }

        foreach (var (kind, group) in _groups)
        {
            group.Header = $"{ShellHelper.KindLabel(kind)} ({counts.GetValueOrDefault(kind)})";
        }

        var where = _folders.Count == 1
            ? Path.GetFileName(_primaryFolder!.TrimEnd(Path.DirectorySeparatorChar))
            : $"{_folders.Count} folders";
        if (string.IsNullOrEmpty(where)) where = _primaryFolder ?? "watched folder";

        var noun = _totalChanges == 1 ? "change" : "changes";
        _headerLabel.Text = _sawCatchUp
            ? $"While you were away: {_totalChanges} {noun} in {where}"
            : $"{_totalChanges} {noun} in {where}";

        _subLabel.Text = _folders.Count == 1
            ? _primaryFolder
            : string.Join("  •  ", _folders);

        if (_totalChanges > _shownRows)
        {
            _subLabel.Text += $"   (showing the first {_shownRows})";
        }

        _list.Columns[5].Width = _folders.Count > 1 ? 120 : 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var change = SelectedChange();
        _openItemButton.Enabled = change is not null && change.Kind != ChangeKind.Deleted;
        _openFolderButton.Enabled = _folders.Count > 0;
    }

    private FileChange? SelectedChange() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as FileChange : null;

    private void OpenSelectedItem()
    {
        var change = SelectedChange();
        if (change is null || change.Kind == ChangeKind.Deleted) return;
        ShellHelper.RevealItem(change.FullPath);
    }

    private void OpenFolder()
    {
        var change = SelectedChange();
        var folder = change?.FolderPath ?? _primaryFolder;
        if (!string.IsNullOrEmpty(folder)) ShellHelper.OpenFolder(folder!);
    }
}
