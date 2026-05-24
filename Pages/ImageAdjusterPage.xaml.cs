using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using HoloPDFCreator.Dialogs;
using HoloPDFCreator.Models;
using HoloPDFCreator.Services;
using Microsoft.Win32;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using Windows.Storage;
using Windows.Storage.Streams;
using PigPdf = UglyToad.PdfPig;
using WinPdf = Windows.Data.Pdf;

namespace HoloPDFCreator.Pages;

public partial class ImageAdjusterPage : Page
{
    // ─── Data model ───────────────────────────────────────────────────────────

    private class ImageItem : IDisposable
    {
        public required string DisplayName  { get; init; }
        public Bitmap?         Original     { get; set; }  // null = placeholder (not yet loaded)
        public Bitmap?         Adjusted     { get; set; }
        public BitmapImage?    ThumbSource  { get; set; }  // cached thumbnail (frozen)
        public bool            IsAdjusted   => Adjusted != null;
        public bool            IsLoaded     => Original  != null;

        public void Dispose()
        {
            Original?.Dispose();
            Adjusted?.Dispose();
        }
    }

    private readonly List<ImageItem>  _items          = new();
    private readonly HashSet<int>     _markedIndices  = new();
    private int    _selectedIndex    = -1;
    private int    _lastMarkedIndex  = -1;
    private bool   _autoLevelApplied;
    private bool   _suppressSliderUpdate;
    private bool   _isProcessing;
    private string? _lastPdfPath;
    private CancellationTokenSource _previewCts  = new();
    private CancellationTokenSource _pdfThumbCts = new();

    // ─── Zoom ─────────────────────────────────────────────────────────────────
    private readonly ScaleTransform _zoomTransform = new(1.0, 1.0);
    private static readonly double[] ZoomSteps =
        [0.25, 0.33, 0.5, 0.67, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0];

    // ─── PDF source tracking (for "Save as PDF") ──────────────────────────────
    private string? _sourcePdfPath;   // original PDF path; null when source is not a PDF
    private int     _pdfPageCount;    // total page count of _sourcePdfPath
    private uint    _pdfInitialPage;  // PDF page index loaded initially (lazy-load scenario)

    // Metadata extracted from the original PDF for reconstruction
    private record WordInfo(string Text, double Left, double Bottom, double Right, double Top);
    private record PageDim(double WidthPt, double HeightPt);
    private record OutlineNode(string Title, int PageIdx, List<OutlineNode> Children);
    private record PdfMeta(
        List<List<WordInfo>> Words,
        List<PageDim>        Pages,
        List<OutlineNode>    Outlines);

    private static readonly string[] ImgExts =
        [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".gif"];

    // Frozen brushes for item panel
    private static readonly SolidColorBrush BrushNormal     = Frozen(0x1E, 0x1E, 0x2E);
    private static readonly SolidColorBrush BrushHover      = Frozen(0x28, 0x28, 0x3C);
    private static readonly SolidColorBrush BrushSelected   = Frozen(0x31, 0x32, 0x44);
    private static readonly SolidColorBrush BrushDotDone    = Frozen(0xA6, 0xE3, 0xA1);
    private static readonly SolidColorBrush BrushDotPend    = Frozen(0x45, 0x47, 0x5A);
    private static readonly SolidColorBrush BrushMarkBorder = Frozen(0x89, 0xB4, 0xFA); // blue mark border

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(MediaColor.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public ImageAdjusterPage()
    {
        InitializeComponent();
        // Share the same ScaleTransform so both panels zoom in sync
        GridOriginalContent.LayoutTransform = _zoomTransform;
        GridAdjustedContent.LayoutTransform = _zoomTransform;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  File operations — Load
    // ═══════════════════════════════════════════════════════════════════════════

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var dropped = (string[])e.Data.GetData(DataFormats.FileDrop);

        var images  = dropped.Where(f => ImgExts.Contains(
                          Path.GetExtension(f).ToLowerInvariant())).ToArray();
        var pdfs    = dropped.Where(f =>
                          f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
        var folders = dropped.Where(Directory.Exists).ToArray();

        if (images.Length > 0) { LoadImageFiles(images); _lastPdfPath = null; }
        foreach (var pdf    in pdfs)    await LoadFromPdfAsync(pdf);
        foreach (var folder in folders) await LoadFolderAsync(folder, clearFirst: false);
    }

    // ── Load image files from PDF Reader (replace, no-op if same source) ────────

    private IReadOnlyList<string>? _lastImageSource;

    public void LoadImageFilesFromReader(IReadOnlyList<string> paths)
    {
        if (ReferenceEquals(paths, _lastImageSource) && _items.Count > 0) return;
        _lastImageSource = paths;
        _lastPdfPath     = null;
        ClearItems();
        _sourcePdfPath = null;
        _pdfPageCount  = 0;
        LoadImageFiles(paths);
    }

    // ── Load image files (append) ─────────────────────────────────────────────

    private void LoadImageFiles(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var path in paths)
        {
            try { AddItemInternal(new Bitmap(path), Path.GetFileName(path)); added++; }
            catch { /* skip unreadable */ }
        }
        if (added == 0) return;
        RefreshImageList();
        if (_selectedIndex < 0) SelectItem(0);
        UpdateButtonStates();
        SetStatus($"{_items.Count} image{(_items.Count == 1 ? "" : "s")} loaded.");
    }

    // ── Load single PDF page initially; remaining pages loaded lazily on Apply to All ──

    public AdjustedImageStore? AdjustedStore { get; set; }

    public async Task LoadFromPdfAsync(string filePath, uint initialPage = 0)
    {
        // Skip only if we already have this PDF's pages loaded (preserves user's work)
        if (filePath == _lastPdfPath && _items.Count > 0) return;
        _lastPdfPath = filePath;

        _pdfThumbCts.Cancel();
        _pdfThumbCts = new CancellationTokenSource();
        ClearItems();
        _sourcePdfPath  = null;
        _pdfPageCount   = 0;
        _pdfInitialPage = 0;

        try
        {
            SetStatus("Loading PDF…");
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
            var pdfDoc      = await WinPdf.PdfDocument.LoadFromFileAsync(storageFile);
            uint count      = pdfDoc.PageCount;
            string baseName = Path.GetFileNameWithoutExtension(filePath);

            initialPage = Math.Min(initialPage, count - 1);

            // Pre-populate all pages as placeholders so thumbnails show immediately.
            for (uint i = 0; i < count; i++)
                AddPlaceholderInternal($"{baseName} — Page {i + 1}");

            // Load only the initial page bitmap.
            SetStatus($"Loading page {initialPage + 1} / {count}…");

            using var pdfPage = pdfDoc.GetPage(initialPage);
            using var stream  = new InMemoryRandomAccessStream();
            await pdfPage.RenderToStreamAsync(stream,
                new WinPdf.PdfPageRenderOptions { DestinationWidth = 1200 });
            stream.Seek(0);

            var ms = new MemoryStream();
            await stream.AsStream().CopyToAsync(ms);
            ms.Position = 0;

            _items[(int)initialPage].Original = new Bitmap(ms);

            _sourcePdfPath  = filePath;
            _pdfPageCount   = (int)count;
            _pdfInitialPage = initialPage;

            RefreshImageList();
            SelectItem((int)initialPage);
            UpdateButtonStates();

            // Render PDF page thumbnails in background (all pages, low resolution).
            _ = RenderPdfThumbnailsAsync(filePath, _pdfThumbCts.Token);

            SetStatus(count > 1
                ? $"Loaded page {initialPage + 1} of {count}. Click any page thumbnail to load it, or press 'Apply to All' to process all pages."
                : $"Loaded 1 page from {Path.GetFileName(filePath)}.");
        }
        catch (Exception ex)
        {
            _lastPdfPath = null;
            SetStatus($"PDF load failed: {ex.Message}");
            MessageBox.Show($"Could not load PDF:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Load all remaining placeholder pages (called before Apply to All or Save as PDF) ──

    private async Task<bool> EnsureAllPdfPagesLoadedAsync(ProgressWindow? win = null)
    {
        if (_sourcePdfPath == null) return true;
        var unloaded = Enumerable.Range(0, _items.Count)
            .Where(i => _items[i].Original == null).ToList();
        if (unloaded.Count == 0) return true;

        string filePath = _sourcePdfPath;

        try
        {
            if (win != null) win.SetTitle("Loading PDF pages…");
            else SetStatus($"Loading {unloaded.Count} remaining page(s)…");

            var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
            var pdfDoc      = await WinPdf.PdfDocument.LoadFromFileAsync(storageFile);

            for (int n = 0; n < unloaded.Count; n++)
            {
                int idx = unloaded[n];
                string stepText = $"Loading page {idx + 1} / {_items.Count}…";
                if (win != null) win.Update(n + 1, unloaded.Count, stepText);
                else             SetStatus(stepText);

                using var pdfPage = pdfDoc.GetPage((uint)idx);
                using var stream  = new InMemoryRandomAccessStream();
                await pdfPage.RenderToStreamAsync(stream,
                    new WinPdf.PdfPageRenderOptions { DestinationWidth = 1200 });
                stream.Seek(0);

                var ms = new MemoryStream();
                await stream.AsStream().CopyToAsync(ms);
                ms.Position = 0;

                _items[idx].Original = new Bitmap(ms);
            }

            RefreshImageList();
            SelectItem(_selectedIndex);
            UpdateButtonStates();
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load all pages: {ex.Message}");
            MessageBox.Show($"Could not load PDF pages:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ── Load a single placeholder page on demand ──────────────────────────────

    private async Task LoadAndDisplayPageAsync(int index)
    {
        if (_sourcePdfPath == null || index < 0 || index >= _items.Count) return;
        if (_items[index].Original != null) { SelectItem(index); return; }

        try
        {
            SetStatus($"Loading page {index + 1} / {_items.Count}…");
            var storageFile = await StorageFile.GetFileFromPathAsync(_sourcePdfPath);
            var pdfDoc      = await WinPdf.PdfDocument.LoadFromFileAsync(storageFile);

            using var pdfPage = pdfDoc.GetPage((uint)index);
            using var stream  = new InMemoryRandomAccessStream();
            await pdfPage.RenderToStreamAsync(stream,
                new WinPdf.PdfPageRenderOptions { DestinationWidth = 1200 });
            stream.Seek(0);

            var ms = new MemoryStream();
            await stream.AsStream().CopyToAsync(ms);
            ms.Position = 0;

            _items[index].Original = new Bitmap(ms);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load page {index + 1}: {ex.Message}");
            return;
        }

        // Refresh thumbnail and display if still selected.
        if (_selectedIndex == index)
        {
            SelectItem(index);
            if (HasNonDefaultSettings()) TriggerLivePreview();
        }
        else
        {
            // Just update the single thumbnail in the list panel.
            if (index < ImageListPanel.Children.Count)
            {
                var child = ImageListPanel.Children[index];
                if (child is Border b)
                {
                    var item = _items[index];
                    if (item.ThumbSource == null && item.Original != null)
                    {
                        using var thumb = CreateThumbnail(item.Original, 150, 92);
                        item.ThumbSource = BitmapToWpf(thumb);
                    }
                    // Rebuild just this one item without full refresh.
                    int pos = ImageListPanel.Children.IndexOf(b);
                    if (pos >= 0)
                    {
                        ImageListPanel.Children.RemoveAt(pos);
                        ImageListPanel.Children.Insert(pos, CreateListItem(index));
                    }
                }
            }
            UpdateButtonStates();
        }
    }

    // ── Load all images in a folder ───────────────────────────────────────────

    private async Task LoadFolderAsync(string folderPath, bool clearFirst)
    {
        var files = Directory.EnumerateFiles(folderPath)
            .Where(f => ImgExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0) { SetStatus("No image files found in folder."); return; }

        if (clearFirst) ClearItems();

        var win = new ProgressWindow($"Loading {files.Count} image(s)…") { Owner = Window.GetWindow(this) };
        win.Show();

        var loaded = new List<(Bitmap bmp, string name)>(files.Count);

        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                if (win.IsCancelled) break;
                win.Update(i + 1, files.Count,
                    $"Loading {i + 1} / {files.Count}: {Path.GetFileName(files[i])}…");

                string f = files[i];
                var bmp = await Task.Run(() =>
                {
                    try { return (Bitmap?)new Bitmap(f); } catch { return null; }
                });

                if (bmp != null) loaded.Add((bmp, Path.GetFileName(f)));
            }
        }
        finally
        {
            win.Close();
        }

        if (loaded.Count == 0) { SetStatus("No images loaded."); return; }

        int startIdx = _items.Count;
        foreach (var (bmp, name) in loaded) AddItemInternal(bmp, name);

        RefreshImageList();
        if (_selectedIndex < 0 && _items.Count > 0) SelectItem(startIdx);
        UpdateButtonStates();
        SetStatus(win.IsCancelled
            ? $"Loaded {loaded.Count} of {files.Count} image(s). Cancelled."
            : $"Loaded {loaded.Count} image(s) from folder.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Image list management
    // ═══════════════════════════════════════════════════════════════════════════

    private void AddItemInternal(Bitmap bmp, string name) =>
        _items.Add(new ImageItem { DisplayName = name, Original = bmp });

    private void AddPlaceholderInternal(string name) =>
        _items.Add(new ImageItem { DisplayName = name });

    private void ClearItems()
    {
        foreach (var item in _items) item.Dispose();
        _items.Clear();
        _selectedIndex   = -1;
        _markedIndices.Clear();
        _lastMarkedIndex = -1;
        AdjustedStore?.Clear();
    }

    private void RefreshImageList()
    {
        ImageListPanel.Children.Clear();

        if (_items.Count == 0)
        {
            ImageListPanel.Children.Add(new TextBlock
            {
                Text        = "No images loaded.\nOpen files or drop here.",
                FontSize    = 11,
                Foreground  = new SolidColorBrush(MediaColor.FromRgb(0x45, 0x47, 0x5A)),
                TextWrapping = TextWrapping.Wrap,
                Margin      = new Thickness(6, 8, 6, 0)
            });
            return;
        }

        for (int i = 0; i < _items.Count; i++)
            ImageListPanel.Children.Add(CreateListItem(i));
    }

    private UIElement CreateListItem(int index)
    {
        var item     = _items[index];
        bool selected = index == _selectedIndex;

        // If a PDF thumbnail hasn't arrived yet but the bitmap is loaded, generate it now.
        if (item.ThumbSource == null && item.Original != null)
        {
            using var thumb = CreateThumbnail(item.Original, 150, 92);
            item.ThumbSource = BitmapToWpf(thumb);
        }

        UIElement topContent;
        if (item.ThumbSource != null)
        {
            // Use whatever thumbnail is available (PDF-rendered or bitmap-derived).
            var imgEl = new System.Windows.Controls.Image
            {
                Source    = item.ThumbSource,
                Stretch   = Stretch.Uniform,
                MaxHeight = 92,
                Margin    = new Thickness(0, 0, 0, 4)
            };
            RenderOptions.SetBitmapScalingMode(imgEl, BitmapScalingMode.HighQuality);
            topContent = imgEl;
        }
        else
        {
            // No thumbnail yet — grey placeholder while PDF thumb is rendering.
            topContent = new Border
            {
                Height     = 92,
                Background = BrushNormal,
                Child      = new TextBlock
                {
                    Text                = "—",
                    FontSize            = 22,
                    Foreground          = BrushDotPend,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center
                }
            };
        }

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width             = 7,
            Height            = 7,
            Fill              = item.IsAdjusted ? BrushDotDone : BrushDotPend,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
            ToolTip           = item.IsAdjusted ? "Adjusted" : "Pending"
        };

        var nameBlock = new TextBlock
        {
            Text              = item.DisplayName,
            FontSize          = 10,
            Foreground        = new SolidColorBrush(selected
                ? MediaColor.FromRgb(0xCD, 0xD6, 0xF4)
                : MediaColor.FromRgb(0x9A, 0x9D, 0xB2)),
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = item.DisplayName
        };

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(nameBlock, 0);
        Grid.SetColumn(dot, 1);
        footer.Children.Add(nameBlock);
        footer.Children.Add(dot);

        var stack = new StackPanel();
        stack.Children.Add(topContent);
        stack.Children.Add(footer);

        bool isMarked = _markedIndices.Contains(index);
        var container = new Border
        {
            Background      = selected ? BrushSelected : BrushNormal,
            BorderBrush     = isMarked ? BrushMarkBorder : System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(isMarked ? 2 : 0),
            CornerRadius    = new CornerRadius(5),
            Padding         = new Thickness(isMarked ? 3 : 5),
            Margin          = new Thickness(0, 0, 0, 4),
            Cursor          = Cursors.Hand,
            Child           = stack
        };

        int capturedIdx = index;
        container.MouseEnter += (_, _) =>
        {
            if (capturedIdx != _selectedIndex) container.Background = BrushHover;
        };
        container.MouseLeave += (_, _) =>
        {
            container.Background = capturedIdx == _selectedIndex ? BrushSelected : BrushNormal;
        };
        container.MouseLeftButtonUp += (_, _) =>
        {
            bool ctrl  = Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl);
            bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            if (ctrl)
            {
                if (_markedIndices.Contains(capturedIdx))
                    _markedIndices.Remove(capturedIdx);
                else
                {
                    _markedIndices.Add(capturedIdx);
                    _lastMarkedIndex = capturedIdx;
                }
                SelectItem(capturedIdx);
            }
            else if (shift && _lastMarkedIndex >= 0)
            {
                int from = Math.Min(_lastMarkedIndex, capturedIdx);
                int to   = Math.Max(_lastMarkedIndex, capturedIdx);
                for (int i = from; i <= to; i++)
                    _markedIndices.Add(i);
                SelectItem(capturedIdx);
            }
            else
            {
                SelectItem(capturedIdx);
                if (HasNonDefaultSettings()) TriggerLivePreview();
            }
        };

        return container;
    }

    private void SelectItem(int index)
    {
        _previewCts.Cancel(); // cancel any in-progress live preview for the previous page
        _selectedIndex = index;

        if (index < 0 || index >= _items.Count)
        {
            ImgOriginal.Source             = null;
            OriginalDropZone.Visibility    = Visibility.Visible;
            ImgOriginal.Visibility         = Visibility.Collapsed;
            ImgAdjusted.Source             = null;
            AdjustedPlaceholder.Visibility = Visibility.Visible;
            ImgAdjusted.Visibility         = Visibility.Collapsed;
            TxtImageInfo.Text              = "";
            UpdateButtonStates();
            UpdatePageNav();
            RefreshImageList();
            return;
        }

        var item = _items[index];

        // Trigger lazy load for unloaded placeholder pages.
        if (item.Original == null)
        {
            ImgOriginal.Source             = null;
            OriginalDropZone.Visibility    = Visibility.Collapsed;
            ImgOriginal.Visibility         = Visibility.Collapsed;
            ImgAdjusted.Source             = null;
            AdjustedPlaceholder.Visibility = Visibility.Visible;
            ImgAdjusted.Visibility         = Visibility.Collapsed;
            TxtImageInfo.Text              = $"Loading page {index + 1}…";
            UpdateButtonStates();
            UpdatePageNav();
            RefreshImageList();
            _ = LoadAndDisplayPageAsync(index);
            return;
        }

        ImgOriginal.Source          = BitmapToWpf(item.Original);
        OriginalDropZone.Visibility = Visibility.Collapsed;
        ImgOriginal.Visibility      = Visibility.Visible;

        if (item.Adjusted is not null)
        {
            ImgAdjusted.Source             = BitmapToWpf(item.Adjusted);
            AdjustedPlaceholder.Visibility = Visibility.Collapsed;
            ImgAdjusted.Visibility         = Visibility.Visible;
        }
        else
        {
            ImgAdjusted.Source             = null;
            AdjustedPlaceholder.Visibility = Visibility.Visible;
            ImgAdjusted.Visibility         = Visibility.Collapsed;
        }

        TxtImageInfo.Text = $"{item.Original.Width} × {item.Original.Height} px  ·  {item.DisplayName}";
        UpdateButtonStates();
        UpdatePageNav();
        RefreshImageList();
    }

    private void UpdateButtonStates()
    {
        bool hasItems    = _items.Count > 0;
        bool hasSelected = _selectedIndex >= 0 && _selectedIndex < _items.Count;
        bool selLoaded   = hasSelected && _items[_selectedIndex].IsLoaded;
        bool selAdjusted = hasSelected && _items[_selectedIndex].IsAdjusted;
        bool anyAdjusted = _items.Any(i => i.IsAdjusted);
        bool isFromPdf   = _sourcePdfPath != null;

        int  loadedCount     = _items.Count(i => i.IsLoaded);
        bool hasPendingPages = loadedCount < _items.Count;

        BtnApplyAll.IsEnabled    = hasItems && !_isProcessing;
        BtnApplyMarked.IsEnabled = _markedIndices.Count > 0 && !_isProcessing;
        BtnApplyRange.IsEnabled  = hasItems && !_isProcessing && TxtPageRange.Text.Trim().Length > 0;
        BtnSaveImage.IsEnabled = selAdjusted;
        BtnSaveAll.IsEnabled   = anyAdjusted;
        BtnSavePdf.IsEnabled   = isFromPdf   && !_isProcessing;
        BtnClearAll.IsEnabled  = hasItems    && !_isProcessing;

        // Disable list interaction while processing
        ImageListPanel.IsHitTestVisible = !_isProcessing;

        TxtImageCount.Text = hasPendingPages
            ? $"{loadedCount} / {_items.Count} pages loaded"
            : _items.Count switch
            {
                0 => "0 images",
                1 => "1 image",
                _ => $"{_items.Count} images"
            };
    }

    // ─── Page navigation ─────────────────────────────────────────────────────

    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex > 0)
        {
            SelectItem(_selectedIndex - 1);
            if (HasNonDefaultSettings()) TriggerLivePreview();
        }
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex < _items.Count - 1)
        {
            SelectItem(_selectedIndex + 1);
            if (HasNonDefaultSettings()) TriggerLivePreview();
        }
    }

    private void UpdatePageNav()
    {
        bool hasSel = _selectedIndex >= 0 && _selectedIndex < _items.Count;
        TxtPageNav.Text       = _items.Count > 0 ? $"{_selectedIndex + 1} / {_items.Count}" : "0 / 0";
        BtnPrev.IsEnabled     = hasSel && _selectedIndex > 0;
        BtnNext.IsEnabled     = hasSel && _selectedIndex < _items.Count - 1;
    }

    // ─── Zoom ─────────────────────────────────────────────────────────────────

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
    {
        double current = _zoomTransform.ScaleX;
        double next    = ZoomSteps.FirstOrDefault(z => z > current + 0.01);
        if (next > 0) ApplyZoom(next);
    }

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
    {
        double current = _zoomTransform.ScaleX;
        double prev    = ZoomSteps.LastOrDefault(z => z < current - 0.01);
        if (prev > 0) ApplyZoom(prev);
    }

    private void BtnZoomFit_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _items.Count) { ApplyZoom(1.0); return; }
        var bmp = _items[_selectedIndex].Original;
        if (bmp == null) { ApplyZoom(1.0); return; }
        double avW  = Math.Max(1, ScrollOriginal.ActualWidth  - 28);
        double avH  = Math.Max(1, ScrollOriginal.ActualHeight - 28);
        ApplyZoom(Math.Min(avW / bmp.Width, avH / bmp.Height));
    }

    private void ApplyZoom(double scale)
    {
        scale = Math.Clamp(scale, 0.1, 8.0);
        _zoomTransform.ScaleX = scale;
        _zoomTransform.ScaleY = scale;
        TxtZoom.Text = $"{(int)Math.Round(scale * 100)}%";
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _pdfThumbCts.Cancel();
        _pdfThumbCts = new CancellationTokenSource();
        _previewCts.Cancel();
        ClearItems();
        _lastPdfPath                   = null;
        _sourcePdfPath                 = null;
        _pdfPageCount                  = 0;
        _pdfInitialPage                = 0;
        ImgOriginal.Source             = null;
        ImgAdjusted.Source             = null;
        OriginalDropZone.Visibility    = Visibility.Visible;
        ImgOriginal.Visibility         = Visibility.Collapsed;
        AdjustedPlaceholder.Visibility = Visibility.Visible;
        ImgAdjusted.Visibility         = Visibility.Collapsed;
        TxtImageInfo.Text              = "";
        RefreshImageList();
        UpdateButtonStates();
        SetStatus("Cleared.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Save
    // ═══════════════════════════════════════════════════════════════════════════

    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _items.Count) return;
        var adjusted = _items[_selectedIndex].Adjusted;
        if (adjusted is null) return;

        string baseName = Path.GetFileNameWithoutExtension(_items[_selectedIndex].DisplayName);
        var dlg = new SaveFileDialog
        {
            Filter   = "PNG Image|*.png|JPEG Image|*.jpg|BMP Image|*.bmp",
            Title    = "Save Adjusted Image",
            FileName = SanitizeFileName(baseName) + "_adjusted"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            adjusted.Save(dlg.FileName, dlg.FilterIndex switch
            {
                2 => System.Drawing.Imaging.ImageFormat.Jpeg,
                3 => System.Drawing.Imaging.ImageFormat.Bmp,
                _ => System.Drawing.Imaging.ImageFormat.Png
            });
            SetStatus("Saved.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        var toSave = _items.Where(i => i.IsAdjusted).ToList();
        if (toSave.Count == 0) { SetStatus("Apply adjustments first."); return; }

        var dlg = new OpenFolderDialog { Title = "Select Output Folder" };
        if (dlg.ShowDialog() != true) return;

        string outDir = dlg.FolderName;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int saved = 0;

        try
        {
            foreach (var item in toSave)
            {
                string safeBase = SanitizeFileName(
                    Path.GetFileNameWithoutExtension(item.DisplayName));
                string candidate = safeBase + "_adjusted.png";
                int n = 1;
                while (!seen.Add(candidate))
                    candidate = $"{safeBase}_adjusted_{++n}.png";

                item.Adjusted!.Save(Path.Combine(outDir, candidate),
                    System.Drawing.Imaging.ImageFormat.Png);
                saved++;
            }
            SetStatus($"Saved {saved} image(s) to folder.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ─── Save as PDF ─────────────────────────────────────────────────────────────
    //
    // Strategy: build a brand-new PDF instead of modifying the original.
    //   1. Extract text positions (PdfPig) and outlines+page sizes (PdfSharp)
    //      from the original file.
    //   2. For each page, draw the adjusted image with JPEG compression
    //      (≈10–20× smaller than PNG), then draw the original words in white
    //      on top. PDF viewers extract text regardless of colour, so the
    //      document remains searchable while the image is the visible layer.
    //   3. Reconstruct the original bookmark/outline tree in the new document.
    // Result: file size comparable to a normal image-based PDF — no bloat from
    // keeping a hidden original content stream.

    private async void SaveAsPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePdfPath == null) return;

        var dlg = new SaveFileDialog
        {
            Filter   = "PDF Files|*.pdf",
            Title    = "Save as PDF",
            FileName = Path.GetFileNameWithoutExtension(_sourcePdfPath) + "_adjusted"
        };
        if (dlg.ShowDialog() != true) return;

        string outPath   = dlg.FileName;
        int    pageCount = Math.Min(_pdfPageCount, _items.Count);

        _isProcessing = true;
        UpdateButtonStates();

        var win = new ProgressWindow("Saving PDF…") { Owner = Window.GetWindow(this) };
        win.Show();

        try
        {
            if (_items.Count < _pdfPageCount)
            {
                if (!await EnsureAllPdfPagesLoadedAsync(win))
                {
                    win.Close();
                    _isProcessing = false;
                    UpdateButtonStates();
                    return;
                }
            }

            win.Update(0, pageCount + 1, "Extracting text and bookmarks…");
            var meta = await Task.Run(() => ExtractPdfMeta(_sourcePdfPath, pageCount));

            var newDoc = new PdfDocument();

            for (int i = 0; i < pageCount; i++)
            {
                if (win.IsCancelled) break;
                win.Update(i + 1, pageCount + 1, $"Building page {i + 1} / {pageCount}…");
                await Task.Yield();

                var dim  = i < meta.Pages.Count ? meta.Pages[i] : new PageDim(595, 842);
                var page = newDoc.AddPage();
                page.Width  = XUnit.FromPoint(dim.WidthPt);
                page.Height = XUnit.FromPoint(dim.HeightPt);

                var bmp = _items[i].Adjusted ?? _items[i].Original;
                if (bmp == null) continue;

                using var jpegMs = new MemoryStream();
                SaveJpeg(bmp, jpegMs, 85);
                jpegMs.Position = 0;

                using var gfx    = XGraphics.FromPdfPage(page);
                using var xImage = XImage.FromStream(jpegMs);

                if (i < meta.Words.Count)
                    DrawSearchableText(gfx, meta.Words[i], dim.HeightPt);

                gfx.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);
            }

            if (!win.IsCancelled)
            {
                CopyOutlines(meta.Outlines, newDoc.Outlines, newDoc);
                win.Update(pageCount + 1, pageCount + 1, "Writing PDF…");
                await Task.Run(() => newDoc.Save(outPath));
                newDoc.Dispose();
                SetStatus($"Saved: {Path.GetFileName(outPath)}");
            }
            else
            {
                newDoc.Dispose();
                SetStatus("PDF save cancelled.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"PDF save failed: {ex.Message}");
            MessageBox.Show($"Could not save PDF:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            win.Close();
            _isProcessing = false;
            UpdateButtonStates();
        }
    }

    // ── Metadata extraction (runs on background thread) ───────────────────────

    private static PdfMeta ExtractPdfMeta(string pdfPath, int maxPages)
    {
        var wordPages = new List<List<WordInfo>>();
        var pageDims  = new List<PageDim>();
        var outlines  = new List<OutlineNode>();

        // Text via PdfPig (pure .NET, background-thread safe)
        try
        {
            byte[] bytes = File.ReadAllBytes(pdfPath);
            using var pig = PigPdf.PdfDocument.Open(bytes);
            int n = Math.Min(pig.NumberOfPages, maxPages);
            for (int i = 0; i < n; i++)
            {
                var pg    = pig.GetPage(i + 1);
                var words = NearestNeighbourWordExtractor.Instance
                    .GetWords(pg.Letters)
                    .Select(w => new WordInfo(
                        w.Text,
                        w.BoundingBox.Left,
                        w.BoundingBox.Bottom,
                        w.BoundingBox.Right,
                        w.BoundingBox.Top))
                    .ToList();
                wordPages.Add(words);
            }
        }
        catch { /* image-only PDF — no text layer */ }

        // Page sizes and outlines via PdfSharp
        try
        {
            using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.ReadOnly);
            int n = Math.Min(doc.PageCount, maxPages);
            for (int i = 0; i < n; i++)
            {
                var p = doc.Pages[i];
                pageDims.Add(new PageDim(p.Width.Point, p.Height.Point));
            }
            outlines = ReadOutlines(doc.Outlines, doc);
        }
        catch { /* use A4 defaults */ }

        return new PdfMeta(wordPages, pageDims, outlines);
    }

    private static List<OutlineNode> ReadOutlines(PdfOutlineCollection col, PdfDocument doc)
    {
        var result = new List<OutlineNode>();
        try
        {
            foreach (var o in col)
            {
                int pageIdx = -1;
                try
                {
                    // DestinationPage is the referenced PdfPage object
                    if (o.DestinationPage != null)
                        for (int i = 0; i < doc.PageCount; i++)
                            if (ReferenceEquals(doc.Pages[i], o.DestinationPage))
                            { pageIdx = i; break; }
                }
                catch { }

                var children = o.Outlines?.Count > 0
                    ? ReadOutlines(o.Outlines, doc)
                    : new List<OutlineNode>();

                result.Add(new OutlineNode(o.Title ?? "", pageIdx, children));
            }
        }
        catch { }
        return result;
    }

    // ── Text layer (white → invisible on paper, still searchable) ─────────────

    private static void DrawSearchableText(XGraphics gfx, List<WordInfo> words, double pagePdfHeight)
    {
        // Cache fonts by rounded size to avoid creating thousands of XFont objects
        var fontCache = new Dictionary<int, XFont?>();

        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w.Text)) continue;

            double h = w.Top - w.Bottom;
            if (h <= 0) continue;

            // Scale font size up by 12 % to compensate for font-metric differences
            // between the original PDF typeface and the substitute (Malgun Gothic).
            int sizeKey = Math.Clamp((int)Math.Round(h * 1.12), 4, 144);
            if (!fontCache.TryGetValue(sizeKey, out var font))
            {
                try
                {
                    font = new XFont("Malgun Gothic", sizeKey, XFontStyleEx.Regular,
                        new XPdfFontOptions(PdfFontEncoding.Unicode));
                }
                catch { font = null; }
                fontCache[sizeKey] = font;
            }
            if (font == null) continue;

            // Expand the hit-test rect by 6 % on each side so selection covers
            // the visible glyph even when metrics differ slightly.
            double padX = Math.Max(0.5, (w.Right - w.Left) * 0.06);
            double padY = Math.Max(0.5, h * 0.06);

            // Convert PDF coords (origin bottom-left, Y up) → XGraphics (top-left, Y down)
            double x   = w.Left  - padX;
            double y   = pagePdfHeight - w.Top - padY;
            double wid = Math.Max(1, w.Right - w.Left + padX * 2);
            double ht  = Math.Max(1, h + padY * 2);

            try
            {
                gfx.DrawString(w.Text, font, XBrushes.White,
                    new XRect(x, y, wid, ht), XStringFormats.TopLeft);
            }
            catch { /* skip words that fail (e.g. unsupported chars) */ }
        }
    }

    // ── Bookmark tree copy ────────────────────────────────────────────────────

    private static void CopyOutlines(List<OutlineNode> nodes,
        PdfOutlineCollection target, PdfDocument doc)
    {
        foreach (var node in nodes)
        {
            try
            {
                PdfOutline? outline = null;
                if (node.PageIdx >= 0 && node.PageIdx < doc.PageCount)
                    outline = target.Add(node.Title, doc.Pages[node.PageIdx], true);
                else if (doc.PageCount > 0)
                    outline = target.Add(node.Title, doc.Pages[0], true);

                if (outline != null && node.Children.Count > 0)
                    CopyOutlines(node.Children, outline.Outlines, doc);
            }
            catch { /* skip individual broken outlines */ }
        }
    }

    // ── JPEG helper ───────────────────────────────────────────────────────────

    private static void SaveJpeg(Bitmap bmp, Stream dest, long quality)
    {
        var codec = System.Drawing.Imaging.ImageCodecInfo
            .GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        var ep = new System.Drawing.Imaging.EncoderParameters(1);
        ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, quality);
        bmp.Save(dest, codec, ep);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Adjustment controls
    // ═══════════════════════════════════════════════════════════════════════════

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderUpdate) return;
        TxtBrightness.Text = ((int)SldBrightness.Value).ToString();
        TxtContrast.Text   = ((int)SldContrast.Value).ToString();
        double sv = Math.Round(SldStroke.Value, 1);
        TxtStroke.Text     = sv.ToString("0.#");
        double shv = Math.Round(SldSharpen.Value, 1);
        TxtSharpen.Text    = shv.ToString("0.#");
        TriggerLivePreview();
    }

    private void AutoLevel_CheckChanged(object sender, RoutedEventArgs e)
    {
        _autoLevelApplied = ChkAutoLevel.IsChecked == true;

        if (_autoLevelApplied)
        {
            // Reset and disable brightness/contrast when Auto Level is on.
            _suppressSliderUpdate = true;
            SldBrightness.Value = 0;
            SldContrast.Value   = 0;
            _suppressSliderUpdate = false;
            TxtBrightness.Text    = "0";
            TxtContrast.Text      = "0";
            SldBrightness.IsEnabled = false;
            SldContrast.IsEnabled   = false;
        }
        else
        {
            SldBrightness.IsEnabled = true;
            SldContrast.IsEnabled   = true;
        }

        TriggerLivePreview();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _suppressSliderUpdate = true;
        SldBrightness.Value = 0;
        SldContrast.Value   = 0;
        SldStroke.Value     = 0;
        SldSharpen.Value    = 0;
        _suppressSliderUpdate = false;

        TxtBrightness.Text = "0";
        TxtContrast.Text   = "0";
        TxtStroke.Text     = "0";
        TxtSharpen.Text    = "0";
        _autoLevelApplied       = false;
        ChkAutoLevel.IsChecked  = false;
        SldBrightness.IsEnabled = true;
        SldContrast.IsEnabled   = true;
        SetStatus("Settings reset.");
        TriggerLivePreview();
    }

    // ── Live preview (debounced) ──────────────────────────────────────────────

    private void TriggerLivePreview()
    {
        _previewCts.Cancel();
        _previewCts = new CancellationTokenSource();
        _ = RunLivePreviewAsync(_previewCts.Token);
    }

    private async Task RunLivePreviewAsync(CancellationToken token)
    {
        // Debounce: wait 120 ms so rapid slider drags coalesce into one render.
        try   { await Task.Delay(120, token); }
        catch (OperationCanceledException) { return; }

        if (_selectedIndex < 0 || _selectedIndex >= _items.Count) return;
        var item = _items[_selectedIndex];
        if (item.Original == null) return;

        // Snapshot settings before yielding to the thread pool.
        float brightness = (float)SldBrightness.Value / 100f;
        float contrast   = (float)SldContrast.Value   / 100f;
        float stroke     = (float)Math.Round(SldStroke.Value, 1);
        float sharpen    = (float)Math.Round(SldSharpen.Value, 1);
        bool  autoLevel  = _autoLevelApplied;

        Bitmap result;
        try
        {
            var src = new Bitmap(item.Original);
            result  = await Task.Run(() => ApplySettings(src, brightness, contrast, autoLevel, stroke, sharpen), token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { SetStatus($"Preview error: {ex.Message}"); return; }

        if (token.IsCancellationRequested) { result.Dispose(); return; }

        item.Adjusted?.Dispose();
        item.Adjusted = result;

        var wpfBmp = BitmapToWpf(item.Adjusted);
        ImgAdjusted.Source             = wpfBmp;
        AdjustedPlaceholder.Visibility = Visibility.Collapsed;
        ImgAdjusted.Visibility         = Visibility.Visible;

        if (_sourcePdfPath != null) AdjustedStore?.Set(_selectedIndex, wpfBmp);

        UpdateButtonStates();
        // Refresh only the current item's dot in the list (avoid full rebuild on every keystroke).
        if (_selectedIndex < ImageListPanel.Children.Count)
        {
            var child = ImageListPanel.Children[_selectedIndex];
            if (child is Border b)
            {
                int pos = ImageListPanel.Children.IndexOf(b);
                if (pos >= 0)
                {
                    ImageListPanel.Children.RemoveAt(pos);
                    ImageListPanel.Children.Insert(pos, CreateListItem(_selectedIndex));
                }
            }
        }
    }

    // ── Apply to all (async, one item at a time) ──────────────────────────────

    private async void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        if (_items.Count == 0 && _sourcePdfPath == null) return;

        _isProcessing = true;
        UpdateButtonStates();

        var win = new ProgressWindow("Processing…") { Owner = Window.GetWindow(this) };
        win.Show();
        int processed = 0;

        // Phase 1: load any unloaded pages, reporting into the progress window
        if (_items.Any(i => i.Original == null))
        {
            if (!await EnsureAllPdfPagesLoadedAsync(win))
            {
                win.Close();
                _isProcessing = false;
                UpdateButtonStates();
                return;
            }
        }

        if (_items.Count == 0)
        {
            win.Close();
            _isProcessing = false;
            UpdateButtonStates();
            return;
        }

        // Phase 2: apply adjustments
        win.SetTitle("Applying adjustments…");

        float brightness = (float)SldBrightness.Value / 100f;
        float contrast   = (float)SldContrast.Value   / 100f;
        float stroke     = (float)Math.Round(SldStroke.Value, 1);
        float sharpen    = (float)Math.Round(SldSharpen.Value, 1);
        bool  autoLevel  = _autoLevelApplied;
        int   total      = _items.Count;

        try
        {
            for (int i = 0; i < total; i++)
            {
                if (win.IsCancelled) break;
                win.Update(i + 1, total, $"Processing page {i + 1} / {total}…");

                var item = _items[i];
                if (item.Original == null) continue;

                var src    = new Bitmap(item.Original);
                var result = await Task.Run(
                    () => ApplySettings(src, brightness, contrast, autoLevel, stroke, sharpen),
                    win.Token);

                item.Adjusted?.Dispose();
                item.Adjusted = result;
                if (_sourcePdfPath != null) AdjustedStore?.Set(i, BitmapToWpf(item.Adjusted));
                processed++;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            win.Close();
            _isProcessing = false;
            SelectItem(_selectedIndex);
            UpdateButtonStates();
            SetStatus(win.IsCancelled
                ? $"Cancelled after {processed} page(s)."
                : $"Applied to {processed} page(s). {BuildOpsText()}");
        }
    }

    // ── Apply to marked pages ────────────────────────────────────────────────

    private async void ApplyMarked_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || _markedIndices.Count == 0) return;
        await ApplyToIndicesAsync(_markedIndices.OrderBy(i => i).ToList());
    }

    // ── Apply to page range ──────────────────────────────────────────────────

    private async void ApplyRange_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || _items.Count == 0) return;
        var indices = ParsePageRange(TxtPageRange.Text, _items.Count).OrderBy(i => i).ToList();
        if (indices.Count == 0) { SetStatus("No valid pages in range."); return; }
        await ApplyToIndicesAsync(indices);
    }

    private void TxtPageRange_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateButtonStates();

    // ── Common apply for a specific set of indices ───────────────────────────

    private async Task ApplyToIndicesAsync(IList<int> indices)
    {
        if (indices.Count == 0) return;
        _isProcessing = true;
        UpdateButtonStates();

        var unloaded = indices.Where(i => i >= 0 && i < _items.Count && _items[i].Original == null).ToList();
        if (unloaded.Count > 0 && !await LoadSpecificPagesAsync(unloaded))
        {
            _isProcessing = false;
            UpdateButtonStates();
            return;
        }

        float brightness = (float)SldBrightness.Value / 100f;
        float contrast   = (float)SldContrast.Value   / 100f;
        float stroke     = (float)Math.Round(SldStroke.Value, 1);
        float sharpen    = (float)Math.Round(SldSharpen.Value, 1);
        bool  autoLevel  = _autoLevelApplied;
        int   total      = indices.Count;

        for (int n = 0; n < total; n++)
        {
            int i = indices[n];
            if (i < 0 || i >= _items.Count) continue;
            var item = _items[i];
            if (item.Original == null) continue;

            SetStatus($"Processing {n + 1} / {total}…");
            var src    = new Bitmap(item.Original);
            var result = await Task.Run(() => ApplySettings(src, brightness, contrast, autoLevel, stroke, sharpen));
            item.Adjusted?.Dispose();
            item.Adjusted = result;
            if (_sourcePdfPath != null) AdjustedStore?.Set(i, BitmapToWpf(item.Adjusted));
        }

        _isProcessing = false;
        SelectItem(_selectedIndex);
        UpdateButtonStates();
        SetStatus($"Applied to {total} page(s). {BuildOpsText()}");
    }

    // ── Load a specific set of placeholder pages ─────────────────────────────

    private async Task<bool> LoadSpecificPagesAsync(IList<int> indices)
    {
        if (_sourcePdfPath == null) return true;
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(_sourcePdfPath);
            var pdfDoc      = await WinPdf.PdfDocument.LoadFromFileAsync(storageFile);

            foreach (int idx in indices)
            {
                if (idx < 0 || idx >= _items.Count || _items[idx].Original != null) continue;
                SetStatus($"Loading page {idx + 1} / {_items.Count}…");

                using var pdfPage = pdfDoc.GetPage((uint)idx);
                using var stream  = new InMemoryRandomAccessStream();
                await pdfPage.RenderToStreamAsync(stream,
                    new WinPdf.PdfPageRenderOptions { DestinationWidth = 1200 });
                stream.Seek(0);

                var ms = new MemoryStream();
                await stream.AsStream().CopyToAsync(ms);
                ms.Position = 0;
                _items[idx].Original = new Bitmap(ms);
            }
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load pages: {ex.Message}");
            MessageBox.Show($"Could not load PDF pages:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ── Parse "1-5, 7, 10-15" → 0-based index set ───────────────────────────

    private static HashSet<int> ParsePageRange(string input, int totalPages)
    {
        var result = new HashSet<int>();
        foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = part.Trim().Replace('–', '-').Replace('—', '-');
            var dash = seg.IndexOf('-');
            if (dash > 0 &&
                int.TryParse(seg[..dash].Trim(), out int from) &&
                int.TryParse(seg[(dash + 1)..].Trim(), out int to))
            {
                from = Math.Clamp(from, 1, totalPages);
                to   = Math.Clamp(to,   1, totalPages);
                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                    result.Add(i - 1);
            }
            else if (int.TryParse(seg, out int page))
            {
                result.Add(Math.Clamp(page, 1, totalPages) - 1);
            }
        }
        return result;
    }

    // ── Returns true if any adjustment is non-default ─────────────────────────

    private bool HasNonDefaultSettings() =>
        SldBrightness.Value != 0 || SldContrast.Value != 0 ||
        SldStroke.Value != 0 || SldSharpen.Value != 0 || _autoLevelApplied;

    // ── Processing pipeline ───────────────────────────────────────────────────

    private static Bitmap ApplySettings(Bitmap bmp,
        float brightness, float contrast, bool autoLevel, float stroke, float sharpen)
    {
        if (brightness != 0)
        {
            var next = ImageProcessingService.AdjustBrightness(bmp, brightness);
            bmp.Dispose(); bmp = next;
        }
        if (contrast != 0)
        {
            var next = ImageProcessingService.AdjustContrast(bmp, contrast);
            bmp.Dispose(); bmp = next;
        }
        if (autoLevel)
        {
            var next = ImageProcessingService.AutoLevel(bmp);
            bmp.Dispose(); bmp = next;
        }
        if (stroke > 0)
        {
            var next = ImageProcessingService.ThickenStrokes(bmp, stroke);
            bmp.Dispose(); bmp = next;
        }
        if (sharpen > 0)
        {
            var next = ImageProcessingService.Sharpen(bmp, sharpen);
            bmp.Dispose(); bmp = next;
        }
        return bmp;
    }

    private string BuildOpsText()
    {
        var ops = new List<string>();
        int    b  = (int)SldBrightness.Value;
        int    c  = (int)SldContrast.Value;
        double s  = Math.Round(SldStroke.Value,  1);
        double sh = Math.Round(SldSharpen.Value, 1);
        if (b != 0)            ops.Add($"Brightness {b:+#;-#;0}");
        if (c != 0)            ops.Add($"Contrast {c:+#;-#;0}");
        if (_autoLevelApplied) ops.Add("Auto Level");
        if (s  > 0)            ops.Add($"Stroke ×{s:0.#}");
        if (sh > 0)            ops.Add($"Sharpen ×{sh:0.#}");
        return ops.Count > 0 ? string.Join(", ", ops) : "no changes";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static Bitmap CreateThumbnail(Bitmap source, int maxW, int maxH)
    {
        float scale = Math.Min((float)maxW / source.Width, (float)maxH / source.Height);
        int w = Math.Max(1, (int)(source.Width  * scale));
        int h = Math.Max(1, (int)(source.Height * scale));
        var thumb = new Bitmap(w, h);
        using var g = Graphics.FromImage(thumb);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, w, h);
        return thumb;
    }

    private static BitmapImage BitmapToWpf(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = ms;
        bmp.CacheOption  = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private void SetStatus(string msg) => TxtStatus.Text = msg;

    // ─── PDF thumbnail rendering (runs in background after PDF load) ──────────

    private async Task RenderPdfThumbnailsAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
            var pdfDoc      = await WinPdf.PdfDocument.LoadFromFileAsync(storageFile);

            for (int i = 0; i < _items.Count; i++)
            {
                if (ct.IsCancellationRequested) return;
                if (_items[i].ThumbSource != null) continue; // already have a thumbnail

                try
                {
                    using var pdfPage = pdfDoc.GetPage((uint)i);
                    using var stream  = new InMemoryRandomAccessStream();
                    await pdfPage.RenderToStreamAsync(stream,
                        new WinPdf.PdfPageRenderOptions { DestinationWidth = 160 });
                    stream.Seek(0);

                    var ms = new MemoryStream();
                    await stream.AsStream().CopyToAsync(ms);
                    ms.Position = 0;

                    var bmpImage = new BitmapImage();
                    bmpImage.BeginInit();
                    bmpImage.StreamSource = ms;
                    bmpImage.CacheOption  = BitmapCacheOption.OnLoad;
                    bmpImage.EndInit();
                    bmpImage.Freeze();

                    if (ct.IsCancellationRequested) return;
                    if (i < _items.Count)
                    {
                        _items[i].ThumbSource = bmpImage;
                        UpdateListItemAt(i);
                    }
                }
                catch { /* skip individual page errors */ }

                await Task.Yield();
            }
        }
        catch { /* skip if PDF cannot be re-opened */ }
    }

    private void UpdateListItemAt(int index)
    {
        if (index < 0 || index >= ImageListPanel.Children.Count) return;
        var child = ImageListPanel.Children[index];
        if (child is not Border) return;
        ImageListPanel.Children.RemoveAt(index);
        ImageListPanel.Children.Insert(index, CreateListItem(index));
    }

    // ─── Mouse-wheel page navigation (boundary scroll → prev/next page) ──────

    private void PreviewArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv   = (ScrollViewer)sender;
        bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

        if (ctrl)
        {
            if (e.Delta > 0) BtnZoomIn_Click(sender,  new RoutedEventArgs());
            else             BtnZoomOut_Click(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Scroll down past bottom → next page
        if (e.Delta < 0 && sv.VerticalOffset >= sv.ScrollableHeight - 1
                        && _selectedIndex < _items.Count - 1)
        {
            SelectItem(_selectedIndex + 1);
            if (HasNonDefaultSettings()) TriggerLivePreview();
            e.Handled = true;
        }
        // Scroll up past top → previous page
        else if (e.Delta > 0 && sv.VerticalOffset <= 0
                             && _selectedIndex > 0)
        {
            SelectItem(_selectedIndex - 1);
            if (HasNonDefaultSettings()) TriggerLivePreview();
            e.Handled = true;
        }
    }
}
