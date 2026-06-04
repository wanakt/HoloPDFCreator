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
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace HoloPDFCreator.Pages;

public partial class OcrPage : Page
{
    // ─── Data model ───────────────────────────────────────────────────────────

    private class ImageItem : IDisposable
    {
        public required string DisplayName  { get; init; }
        public Bitmap?         Original     { get; set; }
        public BitmapImage?    ThumbSource  { get; set; }
        public BitmapSource?   OcrSource    { get; set; }  // BitmapSource received from PDF Reader
        public bool            IsLoaded     => Original  != null;

        public void Dispose()
        {
            Original?.Dispose();
        }
    }

    private readonly List<ImageItem>  _items          = new();
    private readonly HashSet<int>     _markedIndices  = new();
    private int    _selectedIndex    = -1;
    private int    _lastMarkedIndex  = -1;
    private bool   _isProcessing;
    private string? _lastPdfPath;
    private CancellationTokenSource _previewCts  = new();
    private CancellationTokenSource _pdfThumbCts = new();

    // ─── Zoom ─────────────────────────────────────────────────────────────────
    private readonly ScaleTransform _zoomTransform = new(1.0, 1.0);
    private static readonly double[] ZoomSteps =
        [0.25, 0.33, 0.5, 0.67, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0];

    // ─── PDF source tracking ──────────────────────────────────────────────────
    private string? _sourcePdfPath;
    private int     _pdfPageCount;
    private uint    _pdfInitialPage;

    private static readonly string[] ImgExts =
        [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".gif"];

    // Frozen brushes for item panel
    private static readonly SolidColorBrush BrushNormal     = Frozen(0x1E, 0x1E, 0x2E);
    private static readonly SolidColorBrush BrushHover      = Frozen(0x28, 0x28, 0x3C);
    private static readonly SolidColorBrush BrushSelected   = Frozen(0x31, 0x32, 0x44);
    private static readonly SolidColorBrush BrushDotDone    = Frozen(0xA6, 0xE3, 0xA1);
    private static readonly SolidColorBrush BrushDotPend    = Frozen(0x45, 0x47, 0x5A);
    private static readonly SolidColorBrush BrushMarkBorder = Frozen(0x89, 0xB4, 0xFA);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(MediaColor.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    // ─── OCR ─────────────────────────────────────────────────────────────────
    private readonly OcrService _ocrService = new();
    private readonly Dictionary<int, List<OcrRegion>> _pageRegions  = new();
    private readonly Dictionary<int, (int w, int h)>  _regionDims   = new();
    private List<OcrRegion>    _currentRegions = new();

    private CancellationTokenSource? _batchCts;
    private readonly List<Border>    _resultCards    = new();
    private Border?                  _highlightedCard;

    private static readonly MediaColor[] Palette =
    {
        MediaColor.FromRgb(0x89, 0xB4, 0xFA),
        MediaColor.FromRgb(0xA6, 0xE3, 0xA1),
        MediaColor.FromRgb(0xF9, 0xE2, 0xAF),
        MediaColor.FromRgb(0xCB, 0xA6, 0xF7),
        MediaColor.FromRgb(0xF3, 0x8B, 0xA8),
        MediaColor.FromRgb(0x89, 0xDC, 0xEB),
    };

    public OcrPage()
    {
        InitializeComponent();
        GridMainContent.LayoutTransform = _zoomTransform;
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns OCR results for <paramref name="forFilePath"/> as page-indexed
    /// <see cref="OcrPageData"/> ready for <see cref="SearchablePdfService"/>.
    /// Returns null when no results exist for that file or none have image dims
    /// (e.g. results loaded from an existing text layer rather than freshly run).
    /// </summary>
    // Called after PDF save with baked adjustments to dispose stale DML sessions.
    // The next OCR run will recreate fresh sessions; if DML is still broken it
    // will fall back to CPU automatically.
    public void InvalidateOcrSessions() => _ocrService.InvalidateSessions();

    public IReadOnlyDictionary<int, OcrPageData>? GetOcrData(string forFilePath)
    {
        if (_sourcePdfPath != forFilePath) return null;
        var result = new Dictionary<int, OcrPageData>();
        lock (_pageRegions)
        {
            foreach (var (idx, regions) in _pageRegions)
            {
                if (regions.Count == 0) continue;
                if (!_regionDims.TryGetValue(idx, out var d) || d.w <= 0 || d.h <= 0) continue;
                result[idx] = new OcrPageData(regions, d.w, d.h);
            }
        }
        return result.Count > 0 ? result : null;
    }

    public void ApplyAdjustedImages(AdjustedImageStore store)
    {
        if (!store.HasAny || _items.Count == 0) return;
        bool changed = false;
        for (int i = 0; i < _items.Count; i++)
        {
            var img = store.Get(i);
            if (img == null) continue;
            _items[i].OcrSource = img;
            changed = true;
        }
        if (!changed) return;
        RefreshImageList();
        if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
            UpdateMainImageDisplay(_items[_selectedIndex]);
        UpdateButtonStates();
    }

    // ── Streaming public API (used by MainWindow when sending pages from PDF Reader) ──

    public void PrepareForImages(int expectedCount)
    {
        _pageRegions.Clear();
        _regionDims.Clear();
        _previewCts.Cancel();
        _pdfThumbCts.Cancel();
        _pdfThumbCts = new CancellationTokenSource();
        ClearItems();

        ImageListPanel.Children.Clear();
        if (expectedCount > 0)
        {
            ImageListPanel.Children.Add(new TextBlock
            {
                Text         = $"Loading {expectedCount} page{(expectedCount == 1 ? "" : "s")}…",
                FontSize     = 10,
                Foreground   = new SolidColorBrush(MediaColor.FromRgb(0x45, 0x47, 0x5A)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(6, 8, 6, 0)
            });
        }

        ImgMain.Source             = null;
        OriginalDropZone.Visibility = Visibility.Visible;
        OcrContentGrid.Visibility   = Visibility.Collapsed;

        ClearOcrDisplay();
        UpdateButtonStates();
    }

    public void AddImage(string label, BitmapSource image)
    {
        int idx = _items.Count;

        if (idx == 0)
            ImageListPanel.Children.Clear();

        var bmp  = BitmapSourceToBitmap(image);
        var item = new ImageItem { DisplayName = label, Original = bmp, OcrSource = image };
        _items.Add(item);

        ImageListPanel.Children.Add(CreateListItem(idx));

        if (_selectedIndex < 0)
            SelectItem(0);

        UpdateButtonStates();
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

    public async Task LoadFromPdfAsync(string filePath, uint initialPage = 0)
    {
        if (filePath == _lastPdfPath && _items.Count > 0) return;
        _lastPdfPath = filePath;

        _pdfThumbCts.Cancel();
        _pdfThumbCts = new CancellationTokenSource();
        _pageRegions.Clear();
        _regionDims.Clear();
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

            for (uint i = 0; i < count; i++)
                AddPlaceholderInternal($"{baseName} — Page {i + 1}");

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

            _ = RenderPdfThumbnailsAsync(filePath, _pdfThumbCts.Token);
            _ = TryLoadExistingTextLayerAsync(filePath);

            SetStatus(count > 1
                ? $"Loaded page {initialPage + 1} of {count}. Click a thumbnail to navigate, or use 'Apply to All' to process all pages."
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

    private async Task<bool> EnsureAllPdfPagesLoadedAsync(ProgressWindow? win = null)
    {
        if (_sourcePdfPath == null) return true;
        var unloaded = Enumerable.Range(0, _items.Count)
            .Where(i => _items[i].Original == null && _items[i].OcrSource == null).ToList();
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

        if (_selectedIndex == index)
        {
            SelectItem(index);
        }
        else
        {
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
        // Do not clear AdjustedStore here — it is filled by PDFReaderPage and read via OverrideWithAdjustedStore()
    }

    private void RefreshImageList()
    {
        ImageListPanel.Children.Clear();

        if (_items.Count == 0)
        {
            ImageListPanel.Children.Add(new TextBlock
            {
                Text         = "No images loaded.\nOpen files or drop here.",
                FontSize     = 11,
                Foreground   = new SolidColorBrush(MediaColor.FromRgb(0x45, 0x47, 0x5A)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(6, 8, 6, 0)
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

        if (item.ThumbSource == null && item.Original != null)
        {
            using var thumb = CreateThumbnail(item.Original, 150, 92);
            item.ThumbSource = BitmapToWpf(thumb);
        }

        UIElement topContent;
        if (item.ThumbSource != null)
        {
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
            Fill              = (item.IsLoaded || item.OcrSource != null) ? BrushDotDone : BrushDotPend,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
            ToolTip           = (item.IsLoaded || item.OcrSource != null) ? "Ready" : "Pending"
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
            }
        };

        return container;
    }

    private void SelectItem(int index)
    {
        _previewCts.Cancel();
        _selectedIndex = index;

        if (index < 0 || index >= _items.Count)
        {
            ImgMain.Source             = null;
            OriginalDropZone.Visibility = Visibility.Visible;
            OcrContentGrid.Visibility   = Visibility.Collapsed;

            ClearOcrDisplay();
            UpdateButtonStates();
            UpdatePageNav();
            RefreshImageList();
            return;
        }

        var item = _items[index];

        if (item.Original == null && item.OcrSource == null)
        {
            ImgMain.Source             = null;
            OriginalDropZone.Visibility = Visibility.Collapsed;
            OcrContentGrid.Visibility   = Visibility.Collapsed;

            UpdateButtonStates();
            UpdatePageNav();
            RefreshImageList();
            _ = LoadAndDisplayPageAsync(index);
            return;
        }

        UpdateMainImageDisplay(item);
        int w = item.Original?.Width ?? (int)(item.OcrSource?.Width ?? 0);
        int h = item.Original?.Height ?? (int)(item.OcrSource?.Height ?? 0);


        if (_pageRegions.TryGetValue(index, out var regions))
        {
            _currentRegions = regions;
            DrawOverlays();
            ShowResults();
        }
        else
        {
            ClearOcrDisplay();
        }

        UpdateButtonStates();
        UpdatePageNav();
        RefreshImageList();
    }

    private void UpdateMainImageDisplay(ImageItem item)
    {
        BitmapSource src;
        if (item.OcrSource != null)
            src = item.OcrSource;
        else
            src = BitmapToWpf(item.Original!);

        ImgMain.Source      = src;
        ImgMain.Width       = src.PixelWidth;
        ImgMain.Height      = src.PixelHeight;
        OverlayCanvas.Width  = src.PixelWidth;
        OverlayCanvas.Height = src.PixelHeight;

        OriginalDropZone.Visibility = Visibility.Collapsed;
        OcrContentGrid.Visibility   = Visibility.Visible;
    }

    private void UpdateButtonStates()
    {
        bool hasItems    = _items.Count > 0;
        bool hasSelected = _selectedIndex >= 0 && _selectedIndex < _items.Count;

        int  loadedCount     = _items.Count(i => i.IsLoaded || i.OcrSource != null);
        bool hasPendingPages = loadedCount < _items.Count;

        BtnClearAll.IsEnabled    = hasItems && !_isProcessing;

        bool imageAvailable  = hasSelected && (_items[_selectedIndex].IsLoaded || _items[_selectedIndex].OcrSource != null);
        BtnRunOcr.IsEnabled       = imageAvailable && !_isProcessing;

        bool hasAnyOcr = _pageRegions.Values.Any(r => r.Count > 0);
        BtnSaveSearchablePdf.IsEnabled = hasAnyOcr && _sourcePdfPath != null && !_isProcessing;

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
        }
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex < _items.Count - 1)
        {
            SelectItem(_selectedIndex + 1);
        }
    }

    private void UpdatePageNav()
    {
        bool hasSel = _selectedIndex >= 0 && _selectedIndex < _items.Count;
        TxtPageNav.Text   = _items.Count > 0 ? $"{_selectedIndex + 1} / {_items.Count}" : "0 / 0";
        BtnPrev.IsEnabled = hasSel && _selectedIndex > 0;
        BtnNext.IsEnabled = hasSel && _selectedIndex < _items.Count - 1;
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
        var item = _items[_selectedIndex];
        var bmp  = item.Original;
        if (bmp == null) { ApplyZoom(1.0); return; }
        double avW = Math.Max(1, ScrollMain.ActualWidth  - 28);
        double avH = Math.Max(1, ScrollMain.ActualHeight - 28);
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
        _batchCts?.Cancel();
        _pdfThumbCts.Cancel();
        _pdfThumbCts = new CancellationTokenSource();
        _previewCts.Cancel();
        _pageRegions.Clear();
        _regionDims.Clear();
        ClearItems();
        _lastPdfPath   = null;
        _sourcePdfPath = null;
        _pdfPageCount  = 0;
        _pdfInitialPage = 0;

        ImgMain.Source             = null;
        OriginalDropZone.Visibility = Visibility.Visible;
        OcrContentGrid.Visibility   = Visibility.Collapsed;

        ClearOcrDisplay();
        RefreshImageList();
        UpdateButtonStates();
        SetStatus("Cleared.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  OCR
    // ═══════════════════════════════════════════════════════════════════════════

    private static void SetOcrStatus(string msg) { }

    private OcrModelSize _lastModelSize        = OcrModelSize.Mobile;
    private int          _lastWorkers          = 0;   // 0 = auto (cpu count)
    private int          _lastKoreanUpscale    = 2560;

    private void BtnRunOcr_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;

        var dlg = new OcrRunDialog(
            totalPages:        _items.Count,
            currentPage:       _selectedIndex + 1,
            lastModelSize:     _lastModelSize,
            lastWorkers:       _lastWorkers,
            lastKoreanUpscale: _lastKoreanUpscale)
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        var result = dlg.Result;
        _lastModelSize     = result.ModelSize;
        _lastWorkers       = result.Workers;
        _lastKoreanUpscale = result.KoreanUpscaleTarget;

        IEnumerable<int> indices = result.Scope switch
        {
            OcrRunScope.Current => _selectedIndex >= 0 ? new[] { _selectedIndex } : Array.Empty<int>(),
            OcrRunScope.All     => Enumerable.Range(0, _items.Count),
            OcrRunScope.Range   => Enumerable.Range(result.FromPage - 1,
                                       Math.Min(result.ToPage, _items.Count) - (result.FromPage - 1)),
            _                   => Array.Empty<int>()
        };

        _ = RunOcrOnPagesAsync(indices, result.ModelSize, result.Workers);
    }

    private void BtnCancelOcr_Click(object sender, RoutedEventArgs e) => _batchCts?.Cancel();

    private OcrLanguage SelectedOcrLanguage() => CmbLanguage.SelectedIndex switch
    {
        1 => OcrLanguage.Korean,
        2 => OcrLanguage.Chinese,
        3 => OcrLanguage.Japanese,
        _ => OcrLanguage.Latin,
    };

    private BitmapSource? GetOcrSourceForPage(int index)
    {
        var item = _items[index];
        if (item.OcrSource != null) return item.OcrSource;
        if (item.Original != null) return BitmapToWpf(item.Original);
        return null;
    }

    private async Task RunOcrOnPagesAsync(
        IEnumerable<int> indices,
        OcrModelSize modelSize,
        int workers)
    {
        var idxList = indices.Where(i => i >= 0 && i < _items.Count).ToList();
        if (idxList.Count == 0) return;

        _batchCts?.Cancel();
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;

        _isProcessing            = true;
        BtnRunOcr.IsEnabled      = false;
        BtnCancelOcr.Visibility  = Visibility.Visible;
        OcrProgress.Visibility   = Visibility.Visible;
        OcrProgress.Value        = 0;
        OcrProgress.Maximum      = idxList.Count;
        UpdateButtonStates();

        var win = new ProgressWindow($"OCR — 0 / {idxList.Count} 페이지")
                  { Owner = Window.GetWindow(this) };
        win.Token.Register(() => _batchCts?.Cancel());
        win.Show();
        // Yield to the render queue so the window actually paints before heavy work starts.
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        var sw        = System.Diagnostics.Stopwatch.StartNew();
        bool cancelled = false;

        try
        {
            var lang = SelectedOcrLanguage();

            if (!_ocrService.IsReady ||
                _ocrService.RequestedWorkers != workers   ||
                _ocrService.CurrentLanguage  != lang      ||
                _ocrService.CurrentModelSize != modelSize)
            {
                SetOcrStatus("OCR 엔진 초기화 중…");
                win.Update(0, idxList.Count, "OCR 엔진 초기화 중…");
                var initProgress = new Progress<(int, int, string)>(p =>
                {
                    SetOcrStatus($"[{p.Item1}/{p.Item2}] {p.Item3}");
                    win.Update(0, idxList.Count, $"초기화: {p.Item3}");
                });

                await _ocrService.InitializeAsync(
                    language:    lang,
                    progress:    initProgress,
                    ct:          ct,
                    parallelism: workers,
                    modelSize:   modelSize);
            }
            _ocrService.KoreanUpscaleTarget = _lastKoreanUpscale;

            // Build a pool of PDF document instances — one per worker — for fully parallel lazy rendering.
            // A single serialized document (pdfSem=1) becomes the throughput bottleneck once pre-loaded
            // pages are exhausted: serial render time >> GPU inference time → GPU starves.
            string? pdfPath = _sourcePdfPath;
            var pdfDocPool = new System.Collections.Concurrent.ConcurrentQueue<WinPdf.PdfDocument>();
            if (pdfPath != null)
            {
                for (int d = 0; d < workers; d++)
                {
                    var sf = await StorageFile.GetFileFromPathAsync(pdfPath);
                    pdfDocPool.Enqueue(await WinPdf.PdfDocument.LoadFromFileAsync(sf));
                }
            }
            // Semaphore counts available documents so WaitAsync blocks callers when the pool is empty.
            var pdfPoolSem = new SemaphoreSlim(pdfDocPool.Count, Math.Max(pdfDocPool.Count, 1));

            // Renders page idx from the pool; no-op if already loaded.
            // ConfigureAwait(false) keeps continuations on thread pool threads, not the UI thread.
            async Task LazyLoadPageAsync(int idx, CancellationToken localCt)
            {
                if (_items[idx].Original != null || _items[idx].OcrSource != null || pdfDocPool.IsEmpty)
                    return;
                await pdfPoolSem.WaitAsync(localCt).ConfigureAwait(false);
                pdfDocPool.TryDequeue(out var doc);
                try
                {
                    if (_items[idx].Original == null && _items[idx].OcrSource == null)
                    {
                        using var pdfPage = doc!.GetPage((uint)idx);
                        using var stream  = new InMemoryRandomAccessStream();
                        await pdfPage.RenderToStreamAsync(stream,
                            new WinPdf.PdfPageRenderOptions { DestinationWidth = 1200 })
                            .AsTask().ConfigureAwait(false);
                        stream.Seek(0);
                        var ms = new MemoryStream();
                        await stream.AsStream().CopyToAsync(ms).ConfigureAwait(false);
                        ms.Position = 0;
                        _items[idx].Original = new Bitmap(ms);
                    }
                }
                finally { pdfDocPool.Enqueue(doc!); pdfPoolSem.Release(); }
            }

            int done   = 0;
            bool deskew = ChkDeskew.IsChecked == true;
            SetOcrStatus($"OCR 실행 중… (0/{idxList.Count})");

            await Task.Run(() => Parallel.ForEachAsync(
                idxList,
                new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                async (idx, localCt) =>
                {
                    await LazyLoadPageAsync(idx, localCt).ConfigureAwait(false);

                    // OcrSource carries the adjusted image when the user applied Image Adjust;
                    // prefer it so OCR reflects the adjustments. Fall back to Original only
                    // when OcrSource is absent (direct-load path where only Original is set).
                    BitmapSource? wpfSrc = _items[idx].OcrSource;
                    Bitmap?       bmp    = wpfSrc == null ? _items[idx].Original : null;

                    SkiaSharp.SKBitmap skBmp;
                    if (wpfSrc != null)
                        skBmp = await Task.Run(() => OcrService.ConvertToSKBitmap(wpfSrc), localCt).ConfigureAwait(false);
                    else if (bmp != null)
                        skBmp = await Task.Run(() => OcrService.ConvertToSKBitmap(bmp), localCt).ConfigureAwait(false);
                    else return;

                    if (deskew)
                    {
                        var corrected = await Task.Run(() => DeskewService.Deskew(skBmp), localCt).ConfigureAwait(false);
                        if (!ReferenceEquals(corrected, skBmp)) skBmp.Dispose();
                        skBmp = corrected;
                    }

                    int w = skBmp.Width, h = skBmp.Height;
                    List<OcrRegion> regions;
                    try
                    {
                        regions = await _ocrService.RunOcrRawAsync(skBmp, w, h, localCt).ConfigureAwait(false);
                        if (deskew)
                        {
                            double residual = DeskewService.ComputeAngleFromRegions(regions, w);
                            if (Math.Abs(residual) >= 0.2)
                            {
                                var fine = await Task.Run(() => DeskewService.RotateByAngle(skBmp, -residual), localCt).ConfigureAwait(false);
                                if (!ReferenceEquals(fine, skBmp)) skBmp.Dispose();
                                skBmp = fine;
                                w = skBmp.Width; h = skBmp.Height;
                                regions = await _ocrService.RunOcrRawAsync(skBmp, w, h, localCt).ConfigureAwait(false);
                            }
                        }
                    }
                    finally { skBmp.Dispose(); }

                    lock (_pageRegions)
                    {
                        _pageRegions[idx] = regions;
                        _regionDims[idx]  = (w, h);
                    }
                    int current = System.Threading.Interlocked.Increment(ref done);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        OcrProgress.Value = current;
                        SetOcrStatus($"OCR: {current}/{idxList.Count}…");
                        win.SetTitle($"OCR — {current} / {idxList.Count} 페이지");
                        win.Update(current, idxList.Count, $"페이지 {idx + 1} 완료");
                        if (idx == _selectedIndex) { _currentRegions = regions; DrawOverlays(); ShowResults(); }
                    });
                }), ct);

            SetOcrStatus($"완료 — {idxList.Count}페이지 처리됨");
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            SetOcrStatus("취소됨.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"OCR failed:\n{ex.Message}", "OCR Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SetOcrStatus("OCR 실패.");
        }
        finally
        {
            sw.Stop();
            win.Close();
            _isProcessing            = false;
            BtnRunOcr.IsEnabled      = _selectedIndex >= 0;
            BtnCancelOcr.Visibility  = Visibility.Collapsed;
            OcrProgress.Visibility   = Visibility.Collapsed;
            UpdateButtonStates();
        }

        if (!cancelled)
        {
            int totalRegions = idxList.Sum(i =>
                _pageRegions.TryGetValue(i, out var r) ? r.Count : 0);

            string langStr = _ocrService.CurrentLanguage switch
            {
                OcrLanguage.Korean   => "한국어",
                OcrLanguage.Chinese  => "중국어",
                OcrLanguage.Japanese => "일본어",
                _                    => "라틴",
            };
            string modelStr = modelSize == OcrModelSize.Full ? "Full (서버)" : "Mobile";
            string elapsed  = sw.Elapsed.TotalSeconds < 60
                ? $"{sw.Elapsed.TotalSeconds:F1}초"
                : $"{(int)sw.Elapsed.TotalMinutes}분 {sw.Elapsed.Seconds}초";
            string perPage  = idxList.Count > 0
                ? $"{sw.Elapsed.TotalSeconds / idxList.Count:F1}초"
                : "-";
            int threadsUsed = Math.Max(1, Environment.ProcessorCount / workers);

            MessageBox.Show(
                $"처리 페이지:   {idxList.Count}페이지\n" +
                $"검출 텍스트:   {totalRegions}개 영역\n" +
                $"소요 시간:     {elapsed}\n" +
                $"페이지당 평균: {perPage}\n\n" +
                $"모델: {modelStr}  |  언어: {langStr}  |  워커: {workers} (스레드: {threadsUsed}×{workers})",
                "OCR 완료",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void BtnCopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRegions.Count == 0) return;
        Clipboard.SetText(string.Join("\n", _currentRegions.Select(r => r.Text)));
        SetOcrStatus("클립보드에 복사됨.");
    }

    private void BtnSaveTxt_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRegions.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Title = "Save OCR Text", Filter = "Text file|*.txt|All files|*.*", DefaultExt = ".txt"
        };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName,
            string.Join("\n", _currentRegions.Select(r => r.Text)),
            System.Text.Encoding.UTF8);
        SetOcrStatus($"저장됨: {Path.GetFileName(dlg.FileName)}");
    }

    private async void BtnSaveSearchablePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_pageRegions.Count == 0) return;

        string? sourcePath = _sourcePdfPath;

        string baseName = sourcePath != null
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : "ocr_result";

        var dlg = new SaveFileDialog
        {
            Title      = "Save Searchable PDF",
            Filter     = "PDF|*.pdf|All files|*.*",
            DefaultExt = ".pdf",
            FileName   = baseName + "_searchable.pdf"
        };
        if (dlg.ShowDialog() != true) return;
        string outputPath = dlg.FileName;

        // Decide whether to keep or replace the original text layer.
        // Ask only when the source PDF already has a searchable text layer.
        bool keepOriginalText = false;
        if (sourcePath != null && SearchablePdfService.HasTextLayer(sourcePath))
        {
            var answer = MessageBox.Show(
                "원본 PDF에 이미 검색 가능한 텍스트 레이어가 있습니다.\n\n" +
                "원본 텍스트를 유지하시겠습니까?\n\n" +
                "  [예]  원본 텍스트 유지\n" +
                "  [아니오]  OCR 결과로 대체",
                "텍스트 레이어 선택",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            keepOriginalText = answer == MessageBoxResult.Yes;
        }

        BtnSaveSearchablePdf.IsEnabled = false;

        // Open source PDF for lazy-rendering pages that have no loaded image
        WinPdf.PdfDocument? srcPdf = null;
        if (sourcePath != null)
        {
            var sf = await StorageFile.GetFileFromPathAsync(sourcePath);
            srcPdf = await WinPdf.PdfDocument.LoadFromFileAsync(sf);
        }

        // Step 1 (UI thread): collect all page images.
        // BitmapSource is frozen after GetPageBitmapSourceAsync, so it is safe to pass to Task.Run.
        int total = _items.Count;
        var images   = new BitmapSource?[total];
        var ocrDims  = new (int w, int h)[total];

        var winProg = new ProgressWindow("Searchable PDF 저장 중…") { Owner = Window.GetWindow(this) };
        winProg.Show();

        try
        {
            for (int i = 0; i < total; i++)
            {
                if (winProg.IsCancelled) break;
                winProg.Update(i + 1, total * 2, $"이미지 수집 중: {i + 1} / {total}…");
                images[i]  = await GetPageBitmapSourceAsync(i, srcPdf);
                if (images[i] != null)
                    ocrDims[i] = (images[i]!.PixelWidth, images[i]!.PixelHeight);
            }

            // Build a snapshot of OCR data (region lists are shared but read-only during save)
            var ocrData = new Dictionary<int, OcrPageData>();
            if (!keepOriginalText)
            {
                for (int i = 0; i < total; i++)
                {
                    if (_pageRegions.TryGetValue(i, out var regions) && regions.Count > 0)
                        ocrData[i] = new OcrPageData(regions, ocrDims[i].w, ocrDims[i].h);
                }
            }

            // Step 2 (background thread): encode PNGs, build PDF, copy meta, save.
            var dispatcher = Dispatcher;
            string? src = sourcePath;
            await Task.Run(() =>
            {
                void Report(int cur, int tot2, string msg) =>
                    dispatcher.Invoke(() => winProg.Update(cur, tot2, msg));

                var outDoc = new PdfDocument();
                for (int i = 0; i < total; i++)
                {
                    Report(total + i + 1, total * 2, $"페이지 빌드 중: {i + 1} / {total}…");
                    var imgSrc = images[i];
                    if (imgSrc == null) continue;

                    double dpiX = imgSrc.DpiX > 0 ? imgSrc.DpiX : 96.0;
                    double dpiY = imgSrc.DpiY > 0 ? imgSrc.DpiY : 96.0;
                    var page = outDoc.AddPage();
                    page.Width  = XUnit.FromPoint(imgSrc.PixelWidth  * 72.0 / dpiX);
                    page.Height = XUnit.FromPoint(imgSrc.PixelHeight * 72.0 / dpiY);

                    using var gfx   = XGraphics.FromPdfPage(page);
                    using var imgMs = new MemoryStream();
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(imgSrc));
                    enc.Save(imgMs);
                    imgMs.Position = 0;
                    var xImg = XImage.FromStream(imgMs);
                    gfx.DrawImage(xImg, 0, 0, page.Width.Point, page.Height.Point);
                }

                // Apply text layer
                if (keepOriginalText)
                {
                    // CopyMeta copies original text + bookmarks + annotations
                    if (src != null)
                    {
                        Report(total * 2, total * 2, "메타데이터 복사 중…");
                        PdfMetaCopier.CopyMeta(src, outDoc, includeText: true);
                    }
                }
                else
                {
                    // Apply fresh OCR text, then copy bookmarks + annotations (but not original text)
                    if (ocrData.Count > 0)
                        SearchablePdfService.ApplyTextLayer(outDoc, ocrData);
                    if (src != null)
                    {
                        Report(total * 2, total * 2, "메타데이터 복사 중…");
                        PdfMetaCopier.CopyMeta(src, outDoc, includeText: false);
                    }
                }

                outDoc.Save(outputPath);
            });

            MessageBox.Show($"저장 완료:\n{outputPath}", "완료",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"PDF 저장 실패:\n{ex.Message}", "오류",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            winProg.Close();
            UpdateButtonStates();
        }
    }

    // ─── Batch OCR & Save ────────────────────────────────────────────────────

    private async void BtnBatchOcrSave_Click(object sender, RoutedEventArgs e)
    {
        var fileDlg = new OpenFileDialog
        {
            Title       = "일괄 OCR할 PDF 파일 선택",
            Filter      = "PDF 파일|*.pdf|모든 파일|*.*",
            Multiselect = true,
        };
        if (fileDlg.ShowDialog() != true || fileDlg.FileNames.Length == 0) return;

        var settingsDlg = new OcrRunDialog(
            totalPages:    1,
            currentPage:   1,
            lastModelSize: _lastModelSize,
            lastWorkers:   _lastWorkers)
        { Owner = Window.GetWindow(this) };
        if (settingsDlg.ShowDialog() != true) return;

        _lastModelSize     = settingsDlg.Result!.ModelSize;
        _lastWorkers       = settingsDlg.Result!.Workers;
        _lastKoreanUpscale = settingsDlg.Result!.KoreanUpscaleTarget;

        await RunBatchOcrAndSaveAsync(
            fileDlg.FileNames,
            settingsDlg.Result!.ModelSize,
            settingsDlg.Result!.Workers,
            ChkDeskew.IsChecked == true);
    }

    private async Task RunBatchOcrAndSaveAsync(
        string[] pdfPaths,
        OcrModelSize modelSize,
        int workers,
        bool deskew = false)
    {
        _batchCts?.Cancel();
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;

        _isProcessing             = true;
        BtnRunOcr.IsEnabled       = false;
        BtnBatchOcrSave.IsEnabled = false;
        BtnCancelOcr.Visibility   = Visibility.Visible;
        OcrProgress.Visibility    = Visibility.Visible;
        OcrProgress.Value         = 0;
        OcrProgress.Maximum       = pdfPaths.Length;
        UpdateButtonStates();

        var progWin = new ProgressWindow($"일괄 OCR — 0 / {pdfPaths.Length} 파일")
                      { Owner = Window.GetWindow(this) };
        progWin.Token.Register(() => _batchCts?.Cancel());
        progWin.Show();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        int filesDone = 0;
        var errors    = new List<string>();

        try
        {
            var lang = SelectedOcrLanguage();

            // Initialize OCR engine if needed
            if (!_ocrService.IsReady          ||
                _ocrService.RequestedWorkers  != workers   ||
                _ocrService.CurrentLanguage   != lang      ||
                _ocrService.CurrentModelSize  != modelSize)
            {
                progWin.Update(0, pdfPaths.Length, "OCR 엔진 초기화 중…");
                var progress = new Progress<(int, int, string)>(p =>
                    progWin.Update(0, pdfPaths.Length, $"초기화: {p.Item3}"));
                await _ocrService.InitializeAsync(
                    language:    lang,
                    progress:    progress,
                    ct:          ct,
                    parallelism: workers,
                    modelSize:   modelSize);
            }
            _ocrService.KoreanUpscaleTarget = _lastKoreanUpscale;

            foreach (var pdfPath in pdfPaths)
            {
                if (ct.IsCancellationRequested) break;

                string baseName = Path.GetFileNameWithoutExtension(pdfPath);
                string dir      = Path.GetDirectoryName(pdfPath)!;
                string outPath  = Path.Combine(dir, baseName + "_ocr.pdf");

                progWin.SetTitle($"일괄 OCR — {filesDone + 1} / {pdfPaths.Length} 파일");
                progWin.Update(filesDone, pdfPaths.Length, $"{baseName} — 준비 중…");

                try
                {
                    // 1. Load all pages
                    var storageFile = await StorageFile.GetFileFromPathAsync(pdfPath);
                    var pdfDoc      = await WinPdf.PdfDocument.LoadFromFileAsync(storageFile);
                    int pageCount   = (int)pdfDoc.PageCount;

                    var images  = new BitmapSource[pageCount];
                    var ocrDims = new (int w, int h)[pageCount];

                    for (int i = 0; i < pageCount; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        progWin.Update(filesDone, pdfPaths.Length,
                            $"{baseName} — 페이지 로딩 {i + 1} / {pageCount}…");

                        using var pdfPage = pdfDoc.GetPage((uint)i);
                        using var stream  = new InMemoryRandomAccessStream();
                        await pdfPage.RenderToStreamAsync(stream,
                            new WinPdf.PdfPageRenderOptions { DestinationWidth = 1200 });
                        stream.Seek(0);
                        var ms = new MemoryStream();
                        await stream.AsStream().CopyToAsync(ms);
                        ms.Position = 0;
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = ms;
                        bmp.CacheOption  = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        images[i]  = bmp;
                        ocrDims[i] = (bmp.PixelWidth, bmp.PixelHeight);
                    }
                    if (ct.IsCancellationRequested) break;

                    // 2. OCR all pages in parallel
                    int pageDone   = 0;
                    var pageRegions = new Dictionary<int, List<OcrRegion>>();

                    await Task.Run(() => Parallel.ForEachAsync(
                        Enumerable.Range(0, pageCount),
                        new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                        async (idx, localCt) =>
                        {
                            var skBmp = OcrService.ConvertToSKBitmap(images[idx]);
                            if (deskew)
                            {
                                var corrected = DeskewService.Deskew(skBmp);
                                if (!ReferenceEquals(corrected, skBmp)) skBmp.Dispose();
                                skBmp = corrected;
                            }
                            int w = skBmp.Width, h = skBmp.Height;
                            List<OcrRegion> regions;
                            try
                            {
                                regions = await _ocrService.RunOcrRawAsync(skBmp, w, h, localCt).ConfigureAwait(false);
                                if (deskew)
                                {
                                    double residual = DeskewService.ComputeAngleFromRegions(regions, w);
                                    if (Math.Abs(residual) >= 0.2)
                                    {
                                        var fine = DeskewService.RotateByAngle(skBmp, -residual);
                                        if (!ReferenceEquals(fine, skBmp)) skBmp.Dispose();
                                        skBmp = fine;
                                        w = skBmp.Width; h = skBmp.Height;
                                        regions = await _ocrService.RunOcrRawAsync(skBmp, w, h, localCt).ConfigureAwait(false);
                                    }
                                }
                                // Update image and dims so PDF page matches final deskewed coordinates
                                if (deskew)
                                {
                                    images[idx]  = DeskewService.ToBitmapSource(skBmp);
                                    ocrDims[idx] = (w, h);
                                }
                            }
                            finally { skBmp.Dispose(); }
                            lock (pageRegions) pageRegions[idx] = regions;
                            int cur = System.Threading.Interlocked.Increment(ref pageDone);
                            await Dispatcher.InvokeAsync(() =>
                                progWin.Update(filesDone, pdfPaths.Length,
                                    $"{baseName} — OCR {cur} / {pageCount}…"));
                        }), ct);
                    if (ct.IsCancellationRequested) break;

                    // 3. Build and save searchable PDF
                    progWin.Update(filesDone, pdfPaths.Length, $"{baseName} — PDF 저장 중…");

                    var ocrData = new Dictionary<int, OcrPageData>();
                    for (int i = 0; i < pageCount; i++)
                        if (pageRegions.TryGetValue(i, out var regs) && regs.Count > 0)
                            ocrData[i] = new OcrPageData(regs, ocrDims[i].w, ocrDims[i].h);

                    var capturedImages  = images;
                    var capturedOcrData = ocrData;
                    string capturedSrc  = pdfPath;
                    string capturedOut  = outPath;

                    await Task.Run(() =>
                    {
                        var outDoc = new PdfDocument();
                        for (int i = 0; i < pageCount; i++)
                        {
                            var imgSrc = capturedImages[i];
                            double dpiX = imgSrc.DpiX > 0 ? imgSrc.DpiX : 96.0;
                            double dpiY = imgSrc.DpiY > 0 ? imgSrc.DpiY : 96.0;
                            var page    = outDoc.AddPage();
                            page.Width  = XUnit.FromPoint(imgSrc.PixelWidth  * 72.0 / dpiX);
                            page.Height = XUnit.FromPoint(imgSrc.PixelHeight * 72.0 / dpiY);
                            using var gfx   = XGraphics.FromPdfPage(page);
                            using var imgMs = new MemoryStream();
                            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(imgSrc));
                            enc.Save(imgMs);
                            imgMs.Position = 0;
                            using var xImg = XImage.FromStream(imgMs);
                            gfx.DrawImage(xImg, 0, 0, page.Width.Point, page.Height.Point);
                        }
                        if (capturedOcrData.Count > 0)
                            SearchablePdfService.ApplyTextLayer(outDoc, capturedOcrData);
                        PdfMetaCopier.CopyMeta(capturedSrc, outDoc, includeText: false);
                        outDoc.Save(capturedOut);
                    });

                    filesDone++;
                    OcrProgress.Value = filesDone;
                    progWin.SetTitle($"일괄 OCR — {filesDone} / {pdfPaths.Length} 파일");
                    progWin.Update(filesDone, pdfPaths.Length, $"{baseName} — 완료");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(pdfPath)}: {ex.Message}");
                }
            }

            if (!ct.IsCancellationRequested)
            {
                string msg = $"일괄 OCR 완료.\n{filesDone} / {pdfPaths.Length}개 파일 저장됨.\n\n" +
                             $"저장 위치: 원본 파일 폴더 (_ocr.pdf 접미사)";
                if (errors.Count > 0)
                    msg += "\n\n오류 발생 파일:\n" + string.Join("\n", errors);
                MessageBox.Show(msg, "완료", MessageBoxButton.OK,
                    errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            SetOcrStatus("일괄 OCR 취소됨.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"일괄 OCR 실패:\n{ex.Message}", "오류",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            progWin.Close();
            _isProcessing             = false;
            BtnRunOcr.IsEnabled       = _selectedIndex >= 0;
            BtnBatchOcrSave.IsEnabled = true;
            BtnCancelOcr.Visibility   = Visibility.Collapsed;
            OcrProgress.Visibility    = Visibility.Collapsed;
            UpdateButtonStates();
        }
    }

    private async Task<BitmapSource?> GetPageBitmapSourceAsync(int index, WinPdf.PdfDocument? srcPdf)
    {
        var item = _items[index];

        if (item.OcrSource != null) return item.OcrSource;
        if (item.Original  != null) return BitmapToWpf(item.Original);

        if (srcPdf == null) return null;
        try
        {
            using var pdfPage = srcPdf.GetPage((uint)index);
            using var stream  = new InMemoryRandomAccessStream();
            await pdfPage.RenderToStreamAsync(stream,
                new WinPdf.PdfPageRenderOptions { DestinationWidth = 1200 });
            stream.Seek(0);
            var ms = new MemoryStream();
            await stream.AsStream().CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }


    // ─── OCR overlay ─────────────────────────────────────────────────────────

    private void DrawOverlays()
    {
        OverlayCanvas.Children.Clear();
        for (int i = 0; i < _currentRegions.Count; i++)
        {
            var r         = _currentRegions[i];
            var color     = Palette[i % Palette.Length];
            int capturedI = i;
            if (r.Points.Length < 2) continue;

            var poly = new System.Windows.Shapes.Polygon
            {
                Stroke          = new SolidColorBrush(color),
                StrokeThickness = 2,
                Fill            = new SolidColorBrush(MediaColor.FromArgb(30, color.R, color.G, color.B)),
                Cursor          = Cursors.Hand,
            };
            foreach (var pt in r.Points)
                poly.Points.Add(new System.Windows.Point(pt.X, pt.Y));

            poly.MouseEnter          += (_, _) => { poly.StrokeThickness = 3; HighlightResultCard(capturedI); };
            poly.MouseLeave          += (_, _) => poly.StrokeThickness = 2;
            poly.MouseLeftButtonDown += (_, _) => ScrollToResultCard(capturedI);

            OverlayCanvas.Children.Add(poly);
        }
    }

    // ─── OCR results panel ────────────────────────────────────────────────────

    private void ShowResults()
    {
        _resultCards.Clear();
        ResultsPanel.Children.Clear();
        TxtNoResults.Visibility = Visibility.Collapsed;

        for (int i = 0; i < _currentRegions.Count; i++)
        {
            var r         = _currentRegions[i];
            var color     = Palette[i % Palette.Length];
            int capturedI = i;

            var scoreBadge = new Border
            {
                Background        = new SolidColorBrush(MediaColor.FromArgb(60, color.R, color.G, color.B)),
                CornerRadius      = new CornerRadius(4),
                Padding           = new Thickness(5, 2, 5, 2),
                VerticalAlignment = VerticalAlignment.Top
            };
            scoreBadge.Child = new TextBlock
            {
                Text       = $"{r.Score:P0}",
                FontSize   = 10,
                Foreground = new SolidColorBrush(color)
            };

            string committedText = r.Text;
            var tb = new TextBox
            {
                Text            = r.Text,
                FontSize        = 12,
                Foreground      = new SolidColorBrush(MediaColor.FromRgb(0xCD, 0xD6, 0xF4)),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsReadOnly      = false,
                TextWrapping    = TextWrapping.Wrap,
                AcceptsReturn   = true,
                Padding         = new Thickness(0),
                ToolTip         = "클릭하여 텍스트 수정 · Enter로 줄바꿈 · 다른 곳 클릭 시 저장"
            };

            // Visual feedback when editing
            tb.GotFocus  += (_, _) =>
                tb.Background = new SolidColorBrush(MediaColor.FromArgb(80, 0x31, 0x32, 0x44));
            tb.LostFocus += (_, _) =>
            {
                tb.Background = System.Windows.Media.Brushes.Transparent;
                string newText = tb.Text;
                if (newText != committedText && capturedI < _currentRegions.Count)
                {
                    _currentRegions[capturedI].Text = newText;
                    committedText = newText;
                }
            };
            // Ctrl+Enter also commits and moves focus away
            tb.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { tb.Text = committedText; ResultsPanel.Focus(); e.Handled = true; }
            };

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(scoreBadge, Dock.Right);
            header.Children.Add(scoreBadge);
            header.Children.Add(new TextBlock
            {
                Text              = $"#{i + 1}",
                FontSize          = 10,
                Foreground        = new SolidColorBrush(MediaColor.FromRgb(0x58, 0x5B, 0x70)),
                VerticalAlignment = VerticalAlignment.Center
            });

            var content = new StackPanel { Margin = new Thickness(10, 8, 8, 8) };
            content.Children.Add(header);
            content.Children.Add(tb);

            var stripe = new Border
            {
                Width        = 4,
                Background   = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(2, 0, 0, 2)
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(stripe,  0);
            Grid.SetColumn(content, 1);
            row.Children.Add(stripe);
            row.Children.Add(content);

            var card = new Border
            {
                Background      = new SolidColorBrush(MediaColor.FromRgb(0x1E, 0x1E, 0x2E)),
                BorderBrush     = new SolidColorBrush(MediaColor.FromRgb(0x31, 0x32, 0x44)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(0, 0, 0, 6),
                Cursor          = Cursors.Hand,
            };
            card.Child = row;

            card.MouseEnter += (_, _) => { card.BorderBrush = new SolidColorBrush(color); HighlightPolygon(capturedI); };
            card.MouseLeave += (_, _) => { card.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x31, 0x32, 0x44)); UnhighlightPolygon(capturedI); };

            ResultsPanel.Children.Add(card);
            _resultCards.Add(card);
        }

        ResultCountBadge.Visibility = _currentRegions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtResultCount.Text         = _currentRegions.Count.ToString();
        BtnCopyAll.IsEnabled        = _currentRegions.Count > 0;
        BtnSaveTxt.IsEnabled        = _currentRegions.Count > 0;

        if (_currentRegions.Count == 0)
        {
            TxtNoResults.Visibility = Visibility.Visible;
            TxtNoResults.Text       = "No text detected.";
        }
    }

    private void ClearOcrDisplay()
    {
        _currentRegions = new();
        _resultCards.Clear();
        OverlayCanvas.Children.Clear();
        ResultsPanel.Children.Clear();
        TxtNoResults.Visibility     = Visibility.Visible;
        TxtNoResults.Text           = "OCR results will appear here.";
        ResultCountBadge.Visibility = Visibility.Collapsed;
        BtnCopyAll.IsEnabled        = false;
        BtnSaveTxt.IsEnabled        = false;
        _highlightedCard            = null;
    }

    private void HighlightResultCard(int idx)
    {
        if (_highlightedCard != null)
            _highlightedCard.Background = new SolidColorBrush(MediaColor.FromRgb(0x1E, 0x1E, 0x2E));
        if (idx < _resultCards.Count)
        {
            _resultCards[idx].Background = new SolidColorBrush(MediaColor.FromRgb(0x2A, 0x2A, 0x3E));
            _highlightedCard = _resultCards[idx];
        }
    }

    private void ScrollToResultCard(int idx)
    {
        if (idx < _resultCards.Count) _resultCards[idx].BringIntoView();
        HighlightResultCard(idx);
    }

    private void HighlightPolygon(int idx)
    {
        if (idx < OverlayCanvas.Children.Count && OverlayCanvas.Children[idx] is System.Windows.Shapes.Polygon p)
            p.StrokeThickness = 3;
    }

    private void UnhighlightPolygon(int idx)
    {
        if (idx < OverlayCanvas.Children.Count && OverlayCanvas.Children[idx] is System.Windows.Shapes.Polygon p)
            p.StrokeThickness = 2;
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

    private static Bitmap BitmapSourceToBitmap(BitmapSource source)
    {
        using var ms = new MemoryStream();
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(ms);
        ms.Position = 0;
        using var tmp = new Bitmap(ms);
        return new Bitmap(tmp); // clone so the Bitmap is independent of the disposed stream
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static void SetStatus(string msg) { }

    // ─── Existing text layer auto-load ────────────────────────────────────────

    private async Task TryLoadExistingTextLayerAsync(string filePath)
    {
        // Run PdfPig extraction off the UI thread; it can be slow on large files.
        var regions = await Task.Run(() => PdfMetaCopier.LoadTextRegions(filePath, 1200))
                                .ConfigureAwait(true); // resume on UI thread

        // Abort if the user navigated away or opened a different file.
        if (_sourcePdfPath != filePath || regions.Count == 0) return;

        foreach (var (idx, r) in regions)
        {
            // Don't overwrite pages the user has already OCR'd in this session.
            if (!_pageRegions.ContainsKey(idx))
                _pageRegions[idx] = r;
        }

        // Refresh the currently displayed page if it now has regions.
        if (_selectedIndex >= 0 &&
            _pageRegions.TryGetValue(_selectedIndex, out var cur) &&
            _currentRegions.Count == 0)
        {
            _currentRegions = cur;
            DrawOverlays();
            ShowResults();
        }
    }

    // ─── PDF thumbnail rendering ──────────────────────────────────────────────

    private async Task RenderPdfThumbnailsAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
            var pdfDoc      = await WinPdf.PdfDocument.LoadFromFileAsync(storageFile);

            for (int i = 0; i < _items.Count; i++)
            {
                if (ct.IsCancellationRequested) return;
                if (_items[i].ThumbSource != null) continue;

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
                catch { }

                await Task.Yield();
            }
        }
        catch { }
    }

    private void UpdateListItemAt(int index)
    {
        if (index < 0 || index >= ImageListPanel.Children.Count) return;
        var child = ImageListPanel.Children[index];
        if (child is not Border) return;
        ImageListPanel.Children.RemoveAt(index);
        ImageListPanel.Children.Insert(index, CreateListItem(index));
    }

    // ─── Mouse-wheel handler ─────────────────────────────────────────────────

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

        if (e.Delta < 0 && sv.ScrollableHeight - sv.VerticalOffset <= 2
                        && _selectedIndex < _items.Count - 1)
        {
            SelectItem(_selectedIndex + 1);
            e.Handled = true;
        }
        else if (e.Delta > 0 && sv.VerticalOffset <= 2
                             && _selectedIndex > 0)
        {
            SelectItem(_selectedIndex - 1);
            e.Handled = true;
        }
    }
}
