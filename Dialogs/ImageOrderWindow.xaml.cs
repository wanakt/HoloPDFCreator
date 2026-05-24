using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HoloPDFCreator.Dialogs;

public partial class ImageOrderWindow : Window
{
    public List<string> OrderedPaths { get; private set; } = new();

    private class FileEntry
    {
        public string   FilePath  { get; }
        public string   Name      => Path.GetFileName(FilePath);
        public DateTime Date      { get; }
        public long     SizeBytes { get; }
        public string   DateStr   => Date.ToString("yyyy-MM-dd HH:mm");
        public string   SizeStr   => SizeBytes switch
        {
            >= 1_000_000 => $"{SizeBytes / 1_000_000.0:F1} MB",
            >= 1_000     => $"{SizeBytes / 1_000.0:F0} KB",
            _            => $"{SizeBytes} B"
        };

        public FileEntry(string path)
        {
            FilePath  = path;
            var info  = new FileInfo(path);
            Date      = info.Exists ? info.LastWriteTime : DateTime.MinValue;
            SizeBytes = info.Exists ? info.Length : 0;
        }
    }

    private readonly ObservableCollection<FileEntry> _entries = new();

    // Sort state: null = unsorted, "name"/"date"/"size" = last sort field, bool = ascending
    private string? _lastSortField;
    private bool    _sortAscending = true;

    public ImageOrderWindow(IEnumerable<string> paths)
    {
        InitializeComponent();
        foreach (var p in paths)
            _entries.Add(new FileEntry(p));
        FileListBox.ItemsSource = _entries;
        UpdateCount();
    }

    private void UpdateCount() =>
        TxtCount.Text = $"{_entries.Count} file{(_entries.Count == 1 ? "" : "s")} selected";

    // ── Sort (toggle asc/desc on same field) ──────────────────────────────────

    private void SortName_Click(object sender, RoutedEventArgs e) => ApplySort("name");
    private void SortDate_Click(object sender, RoutedEventArgs e) => ApplySort("date");
    private void SortSize_Click(object sender, RoutedEventArgs e) => ApplySort("size");

    private void ApplySort(string field)
    {
        if (_lastSortField == field)
            _sortAscending = !_sortAscending;
        else
        {
            _lastSortField = field;
            _sortAscending = true;
        }

        IEnumerable<FileEntry> sorted = field switch
        {
            "name" => _sortAscending
                ? _entries.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                : _entries.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase),
            "date" => _sortAscending
                ? _entries.OrderBy(f => f.Date)
                : _entries.OrderByDescending(f => f.Date),
            _      => _sortAscending
                ? _entries.OrderBy(f => f.SizeBytes)
                : _entries.OrderByDescending(f => f.SizeBytes),
        };

        var list = sorted.ToList();
        _entries.Clear();
        foreach (var f in list) _entries.Add(f);

        UpdateSortButtonLabels();
    }

    private void UpdateSortButtonLabels()
    {
        string arrow = _sortAscending ? " ▲" : " ▼";
        BtnSortName.Content = "By Name" + (_lastSortField == "name" ? arrow : "");
        BtnSortDate.Content = "By Date" + (_lastSortField == "date" ? arrow : "");
        BtnSortSize.Content = "By Size" + (_lastSortField == "size" ? arrow : "");
    }

    // ── Move Up / Down ────────────────────────────────────────────────────────

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        int idx = FileListBox.SelectedIndex;
        if (idx <= 0) return;
        _entries.Move(idx, idx - 1);
        FileListBox.SelectedIndex = idx - 1;
        ((ListBoxItem?)FileListBox.ItemContainerGenerator.ContainerFromIndex(idx - 1))
            ?.BringIntoView();
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        int idx = FileListBox.SelectedIndex;
        if (idx < 0 || idx >= _entries.Count - 1) return;
        _entries.Move(idx, idx + 1);
        FileListBox.SelectedIndex = idx + 1;
        ((ListBoxItem?)FileListBox.ItemContainerGenerator.ContainerFromIndex(idx + 1))
            ?.BringIntoView();
    }

    // ── Confirm / Cancel ──────────────────────────────────────────────────────

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        OrderedPaths = _entries.Select(f => f.FilePath).ToList();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
