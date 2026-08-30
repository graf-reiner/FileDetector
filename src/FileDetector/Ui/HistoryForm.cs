using FileDetector.Core;
using FileDetector.Services;

namespace FileDetector.Ui;

/// <summary>Scrollable log of everything the app has detected, read back from history.jsonl.</summary>
public sealed class HistoryForm : Form
{
    private readonly HistoryStore _history;
    private readonly ListView _list = new();
    private readonly ComboBox _folderFilter = new();
    private readonly ComboBox _kindFilter = new();
    private readonly Label _countLabel = new();

    private IReadOnlyList<HistoryEntry> _entries = Array.Empty<HistoryEntry>();

    public HistoryForm(HistoryStore history)
    {
        _history = history;

        Text = "FileDetector — History";
        Icon = IconProvider.App;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 500);
        MinimumSize = new Size(600, 360);
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        BuildLayout();
        Reload();
    }

    public void Reload()
    {
        _entries = _history.ReadRecent();

        var previous = _folderFilter.SelectedItem as string;
        _folderFilter.Items.Clear();
        _folderFilter.Items.Add("All folders");
        foreach (var folder in _entries.Select(e => e.FolderPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f))
        {
            _folderFilter.Items.Add(folder);
        }
        _folderFilter.SelectedItem = previous is not null && _folderFilter.Items.Contains(previous) ? previous : "All folders";

        ApplyFilter();
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

        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 8),
        };

        _folderFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _folderFilter.Width = 320;
        _folderFilter.SelectedIndexChanged += (_, _) => ApplyFilter();

        _kindFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _kindFilter.Width = 130;
        _kindFilter.Items.AddRange(new object[] { "All changes", "Added", "Renamed", "Changed", "Deleted" });
        _kindFilter.SelectedIndex = 0;
        _kindFilter.SelectedIndexChanged += (_, _) => ApplyFilter();

        _countLabel.AutoSize = true;
        _countLabel.ForeColor = SystemColors.GrayText;
        _countLabel.Margin = new Padding(12, 6, 0, 0);

        filters.Controls.Add(_folderFilter);
        filters.Controls.Add(_kindFilter);
        filters.Controls.Add(_countLabel);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.Columns.Add("When", 140);
        _list.Columns.Add("Change", 80);
        _list.Columns.Add("Name", 240);
        _list.Columns.Add("Type", 80);
        _list.Columns.Add("Size", 80);
        _list.Columns.Add("Folder", 200);
        _list.DoubleClick += (_, _) => OpenSelected();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 10, 0, 0),
        };

        var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(10, 3, 10, 3), DialogResult = DialogResult.Cancel };
        var clear = new Button { Text = "Clear history", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
        clear.Click += (_, _) => ClearHistory();
        var refresh = new Button { Text = "Refresh", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
        refresh.Click += (_, _) => Reload();
        var open = new Button { Text = "Open folder", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
        open.Click += (_, _) => OpenSelected();

        buttons.Controls.Add(close);
        buttons.Controls.Add(clear);
        buttons.Controls.Add(refresh);
        buttons.Controls.Add(open);

        root.Controls.Add(filters, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        CancelButton = close;
    }

    private void ApplyFilter()
    {
        var folder = _folderFilter.SelectedItem as string;
        var kind = _kindFilter.SelectedItem as string;

        var filtered = _entries.Where(e =>
            (folder is null or "All folders" || string.Equals(e.FolderPath, folder, StringComparison.OrdinalIgnoreCase)) &&
            (kind is null or "All changes" || ShellHelper.KindLabel(e.Kind) == kind));

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            var shown = 0;
            foreach (var entry in filtered)
            {
                var name = entry.Kind == ChangeKind.Renamed && !string.IsNullOrEmpty(entry.OldName)
                    ? $"{entry.OldName} → {entry.Name}"
                    : entry.Name;

                var item = new ListViewItem(new[]
                {
                    entry.WhenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    ShellHelper.KindLabel(entry.Kind),
                    name,
                    ShellHelper.TypeLabel(entry.IsDirectory, entry.Name),
                    ShellHelper.FormatSize(entry.Size, entry.IsDirectory),
                    entry.FolderPath,
                })
                {
                    Tag = entry,
                    ForeColor = entry.Kind == ChangeKind.Deleted ? Color.FromArgb(150, 60, 60) : SystemColors.WindowText,
                };
                _list.Items.Add(item);
                shown++;
            }
            _countLabel.Text = $"{shown} of {_entries.Count} events";
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    private void OpenSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not HistoryEntry entry) return;

        var full = Path.Combine(entry.FolderPath, entry.Name);
        if (entry.Kind == ChangeKind.Deleted || (!File.Exists(full) && !Directory.Exists(full)))
        {
            ShellHelper.OpenFolder(entry.FolderPath);
            return;
        }
        ShellHelper.RevealItem(full);
    }

    private void ClearHistory()
    {
        var confirm = MessageBox.Show(this, "Delete all recorded history? This cannot be undone.",
            "FileDetector", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _history.Clear();
        Reload();
    }
}
