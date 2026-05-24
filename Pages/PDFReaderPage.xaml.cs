using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HoloPDFCreator.Dialogs;
using HoloPDFCreator.Models;
using HoloPDFCreator.Services;
using System.Linq;
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

public partial class PDFReaderPage : Page
{
    // ─── PDF state ────────────────────────────────────────────────────────────
    private PdfDocument? _editDocument;
    private byte[]?      _editPdfBytes;   // snapshot for PdfPig — avoids calling Save() again
    private string? _currentFilePath;
    private WinPdf.PdfDocument? _renderDocument;
    private uint _currentPage;
    private uint _totalPages;
    private double _zoomLevel = 1.0;
    private const double RenderBaseWidth = 1200;

    // ─── Text-overlay state ───────────────────────────────────────────────────
    private record WordBox(string Text, double Left, double Top, double Width, double Height)
    {
        public double Right   => Left + Width;
        public double Bottom  => Top  + Height;
        public double CenterX => Left + Width  / 2;
        public double CenterY => Top  + Height / 2;
    }

    private readonly List<WordBox>        _pageWords    = new();
    private readonly List<List<int>>      _pageLines    = new();
    private readonly List<int>            _readingOrder = new();
    private readonly Dictionary<int, int> _wordToOrder  = new();

    private readonly HashSet<int> _selectedIndices  = new();
    private int  _lastClickedOrder    = -1;
    private int  _lastHoverIndex      = -1;
    private int  _currentPageRotation = 0;
    private bool _isVerticalText      = false;

    // ─── Drag state ───────────────────────────────────────────────────────────
    private bool      _isDragging;
    private Point     _dragStart;
    private int       _dragAnchorOrder = -1;
    private List<int>? _dragAnchorLine;   // column/line the drag started in (vertical-lock)

    // ─── Annotation state ─────────────────────────────────────────────────────
    private TextAnnotation?              _selectedAnnotation;
    private Popup?                       _annotationPopup;
    private readonly Dictionary<int,int> _wordToLine = new();
    private double                       _currentPagePdfWidth;
    private double                       _currentPagePdfHeight;

    // ─── Memo state ───────────────────────────────────────────────────────────
    private double _memoInsertPdfX;
    private double _memoInsertPdfY;
    private Popup? _memoEditPopup;

    // ─── Continuous scroll ────────────────────────────────────────────────────
    private bool   _pageTransitioning;
    private bool   _updatingScrollBar;
    private bool   _scrollBarDragging;
    private double _scrollBarDragTarget;
    private const  double PageGap = 16; // px gap between pages in the StackPanel

    // ─── Thumbnail panel ──────────────────────────────────────────────────────
    private record ThumbEntry(System.Windows.Controls.Image Img, Border Container);
    private readonly List<ThumbEntry> _thumbItems = new();
    private CancellationTokenSource _thumbCts = new();
    private static readonly SolidColorBrush ThumbBrushNormal   = Frozen(0x1E, 0x1E, 0x2E);
    private static readonly SolidColorBrush ThumbBrushSelected = Frozen(0x31, 0x32, 0x44);
    private static readonly SolidColorBrush ThumbBrushHover    = Frozen(0x28, 0x28, 0x3C);
    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    private static readonly (Color Color, string Name)[] HighlightPresets =
    {
        (Color.FromRgb(0xFF, 0xEE, 0x00), "Yellow"),
        (Color.FromRgb(0x00, 0xFF, 0x66), "Green"),
        (Color.FromRgb(0x00, 0xCC, 0xFF), "Cyan"),
        (Color.FromRgb(0xFF, 0x88, 0x00), "Orange"),
        (Color.FromRgb(0xFF, 0x2D, 0x9E), "Pink"),
    };
    private static readonly (Color Color, string Name)[] LinePresets =
    {
        (Color.FromRgb(0xFF, 0x00, 0x00), "Red"),
        (Color.FromRgb(0x00, 0x66, 0xFF), "Blue"),
        (Color.FromRgb(0x00, 0xBB, 0x00), "Green"),
        (Color.FromRgb(0xFF, 0x66, 0x00), "Orange"),
        (Color.FromRgb(0x00, 0x00, 0x00), "Black"),
    };

    // ─── Colors ───────────────────────────────────────────────────────────────
    private static readonly Brush HoverBrush    = new SolidColorBrush(Color.FromArgb(70,  137, 180, 250));
    private static readonly Brush SelectedBrush = new SolidColorBrush(Color.FromArgb(160, 137, 180, 250));

    public PDFReaderPage()
    {
        InitializeComponent();
        PdfPageBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color       = System.Windows.Media.Colors.Black,
            BlurRadius  = 24,
            ShadowDepth = 4,
            Opacity     = 0.6
        };

        // DrawingCanvas stays below TextOverlayCanvas; it only captures input during active draw mode.
    }

    // ─── Image mode (images loaded as a temp PDF) ────────────────────────────
    private List<string> _imageFiles     = new();
    private string?      _tempImagePdfPath;
    public bool IsImageMode => _imageFiles.Count > 0;
    public IReadOnlyList<string> ImageFiles => _imageFiles;

    public string?             CurrentFilePath => _currentFilePath;
    public uint               CurrentPage     => _currentPage;
    public AdjustedImageStore? AdjustedStore  { get; set; }

    // Re-render the current page and update thumbnails with any adjusted-image overrides.
    public async Task RefreshWithAdjustedImagesAsync()
    {
        if (_renderDocument == null) return;
        if (AdjustedStore == null || !AdjustedStore.HasAny) return;
        await RenderCurrentPageAsync();
        RefreshAdjustedThumbnails();
    }

    private void RefreshAdjustedThumbnails()
    {
        if (AdjustedStore == null) return;
        for (int i = 0; i < _thumbItems.Count; i++)
        {
            var overrideImg = AdjustedStore.Get(i);
            if (overrideImg != null)
                _thumbItems[i].Img.Source = overrideImg;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  File Operations
    // ═══════════════════════════════════════════════════════════════════════════

    private void BtnFile_Click(object sender, RoutedEventArgs e)
    {
        var cm = BtnFile.ContextMenu;
        cm.PlacementTarget = BtnFile;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        cm.IsOpen = true;
    }

    private void OpenImages_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter      = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif;*.gif|All Files|*.*",
            Title       = "Open Images",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0) return;

        var win = new ImageOrderWindow(dlg.FileNames) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true || win.OrderedPaths.Count == 0) return;

        _ = LoadImagesAsync(win.OrderedPaths);
    }

    private async Task LoadImagesAsync(IEnumerable<string> paths)
    {
        _imageFiles = paths.ToList();
        if (_imageFiles.Count == 0) return;

        CleanupTempImagePdf();
        AdjustedStore?.Clear();

        string tmpPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            System.IO.Path.GetRandomFileName() + ".pdf");
        _tempImagePdfPath = tmpPath;

        var win = new HoloPDFCreator.Dialogs.ProgressWindow("이미지 → PDF 변환 중")
        {
            Owner = Window.GetWindow(this)
        };
        win.Show();

        int total = _imageFiles.Count;
        bool succeeded = false;

        try
        {
            await Task.Run(() =>
            {
                var doc = new PdfDocument();
                for (int i = 0; i < total; i++)
                {
                    if (win.IsCancelled) return;

                    var imgPath = _imageFiles[i];
                    Dispatcher.Invoke(() =>
                        win.Update(i, total, $"({i + 1} / {total})  {System.IO.Path.GetFileName(imgPath)}"));

                    using var xImg = XImage.FromFile(imgPath);
                    double dpiX = xImg.HorizontalResolution > 0 ? xImg.HorizontalResolution : 96.0;
                    double dpiY = xImg.VerticalResolution   > 0 ? xImg.VerticalResolution   : 96.0;
                    double ptW  = xImg.PixelWidth  * 72.0 / dpiX;
                    double ptH  = xImg.PixelHeight * 72.0 / dpiY;
                    var page    = doc.AddPage();
                    page.Width  = XUnit.FromPoint(ptW);
                    page.Height = XUnit.FromPoint(ptH);
                    using var gfx = XGraphics.FromPdfPage(page);
                    gfx.DrawImage(xImg, 0, 0, ptW, ptH);
                }

                if (!win.IsCancelled)
                {
                    Dispatcher.Invoke(() => win.Update(total, total, "PDF 파일 저장 중…"));
                    doc.Save(tmpPath);
                    succeeded = true;
                }
            });
        }
        catch (Exception ex)
        {
            win.Close();
            _imageFiles.Clear();
            _tempImagePdfPath = null;
            MessageBox.Show($"이미지를 PDF로 변환하는 중 오류가 발생했습니다:\n{ex.Message}",
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("이미지 로드 실패.");
            return;
        }

        win.Close();

        if (!succeeded)
        {
            _imageFiles.Clear();
            _tempImagePdfPath = null;
            try { System.IO.File.Delete(tmpPath); } catch { }
            SetStatus("변환이 취소되었습니다.");
            return;
        }

        await LoadPdfAsync(tmpPath);
    }

    private void CleanupTempImagePdf()
    {
        if (_tempImagePdfPath != null)
        {
            try { File.Delete(_tempImagePdfPath); } catch { }
            _tempImagePdfPath = null;
        }
    }

    private async void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF Files (*.pdf)|*.pdf", Title = "Open PDF" };
        if (dlg.ShowDialog() != true) return;
        await LoadPdfAsync(dlg.FileName);
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var pdf = Array.Find(files, f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf is not null) await LoadPdfAsync(pdf);
    }

    private async Task LoadPdfAsync(string filePath)
    {
        if (filePath != _tempImagePdfPath)
        {
            _imageFiles.Clear();
            CleanupTempImagePdf();
        }
        SetStatus("Loading…");
        try
        {
            _editDocument?.Dispose();
            _editDocument = PdfReader.Open(filePath, PdfDocumentOpenMode.Modify);

            _currentFilePath = filePath;
            _currentPage = 0;

            // Read our annotations then strip them from _editDocument so the
            // rendered page image won't show them (prevents double-draw).
            LoadAnnotationsAndMemosFromPdf();

            string renderTmp = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                System.IO.Path.GetRandomFileName() + ".pdf");
            _editDocument.Save(renderTmp);

            // PDFsharp locks a document after Save(), so re-open from the
            // original file to keep _editDocument modifiable for future saves.
            _editDocument.Dispose();
            _editPdfBytes = File.ReadAllBytes(renderTmp);   // snapshot for PdfPig
            _editDocument = PdfReader.Open(filePath, PdfDocumentOpenMode.Modify);

            var cleanFile = await StorageFile.GetFileFromPathAsync(renderTmp);
            _renderDocument = await WinPdf.PdfDocument.LoadFromFileAsync(cleanFile);
            try { File.Delete(renderTmp); } catch { /* temp file, non-critical */ }

            _totalPages = _renderDocument.PageCount;

            DropZone.Visibility      = Visibility.Collapsed;
            PdfPageBorder.Visibility = Visibility.Visible;
            MenuItemSave.IsEnabled   = filePath != _tempImagePdfPath;
            MenuItemSaveAs.IsEnabled = true;
            BtnRotate.IsEnabled = true;
            UpdateNavButtons();

            await RenderCurrentPageAsync();
            SetStatus($"Opened: {System.IO.Path.GetFileName(filePath)}");
            StartThumbnailGeneration();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open PDF:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Error loading file.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PDF Creation
    // ═══════════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════════
    //  Save
    // ═══════════════════════════════════════════════════════════════════════════

    private async void MenuSave_Click(object sender, RoutedEventArgs e)
    {
        if (_editDocument is null || _currentFilePath is null) return;
        await SaveToPathAsync(_currentFilePath);
    }

    private async void MenuSaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_editDocument is null || _currentFilePath is null) return;

        var dlg = new SaveFileDialog
        {
            Filter   = "PDF Files (*.pdf)|*.pdf",
            Title    = "Save As",
            FileName = System.IO.Path.GetFileNameWithoutExtension(_currentFilePath)
        };
        if (dlg.ShowDialog() != true) return;

        await SaveToPathAsync(dlg.FileName);
    }

    private async Task SaveToPathAsync(string outputPath)
    {
        if (_editDocument is null || _currentFilePath is null) return;

        string? tmpPath = null;
        try
        {
            EmbedAnnotationsAndMemos();

            // Write to temp first to avoid locking the open file.
            tmpPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(outputPath)!,
                System.IO.Path.GetRandomFileName() + ".pdf");

            _editDocument.Save(tmpPath);

            _editDocument.Dispose();
            _editDocument   = null;
            _renderDocument = null;

            File.Move(tmpPath, outputPath, overwrite: true);
            tmpPath = null;

            if (!outputPath.Equals(_currentFilePath, StringComparison.OrdinalIgnoreCase))
            {
                AnnotationService.Instance.RenameFile(_currentFilePath, outputPath);
                DrawingService.Instance.RenameFile(_currentFilePath, outputPath);
            }

            await LoadPdfAsync(outputPath);
            SetStatus("Saved.");
        }
        catch (Exception ex)
        {
            if (tmpPath != null && File.Exists(tmpPath)) File.Delete(tmpPath);
            MessageBox.Show($"Save failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Bookmarks
    // ═══════════════════════════════════════════════════════════════════════════

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        if (ctrl && e.Key == Key.B) { AddBookmarkForCurrentPage(); e.Handled = true; }
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e) => AddBookmarkForCurrentPage();

    private void CopySelectedText_ContextMenu_Click(object sender, RoutedEventArgs e) =>
        CopySelectionToClipboard();

    private void AddBookmarkForCurrentPage()
    {
        if (_currentFilePath is null) { SetStatus("Open a PDF first."); return; }

        string title;
        if (_selectedIndices.Count > 0)
        {
            var raw = string.Join(" ",
                _readingOrder.Where(_selectedIndices.Contains).Select(i => _pageWords[i].Text));
            title = raw.Length > 60 ? raw[..57] + "…" : raw;
        }
        else
        {
            title = $"Page {_currentPage + 1}";
        }

        BookmarkService.Instance.Add(new Bookmark
        {
            FilePath   = _currentFilePath,
            PageNumber = _currentPage,
            Title      = title
        });
        SetStatus($"Bookmark added: \"{title}\"");
    }

    public async void NavigateToBookmark(Bookmark bm)
    {
        if (!bm.FilePath.Equals(_currentFilePath, StringComparison.OrdinalIgnoreCase))
            await LoadPdfAsync(bm.FilePath);

        if (_totalPages == 0) return;
        _currentPage = Math.Min(bm.PageNumber, _totalPages - 1);
        UpdateNavButtons();
        await RenderCurrentPageAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Page Navigation & Zoom
    // ═══════════════════════════════════════════════════════════════════════════

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage == 0) return;
        _currentPage--;
        UpdateNavButtons();
        await RenderCurrentPageAsync();
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage >= _totalPages - 1) return;
        _currentPage++;
        UpdateNavButtons();
        await RenderCurrentPageAsync();
    }

    private void UpdateNavButtons()
    {
        TxtPageInfo.Text  = $"Page {_currentPage + 1} / {_totalPages}";
        BtnPrev.IsEnabled = _currentPage > 0;
        BtnNext.IsEnabled = _currentPage < _totalPages - 1;
    }

    private async void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _zoomLevel = Math.Min(_zoomLevel + 0.25, 4.0);
        TxtZoom.Text = $"{(int)(_zoomLevel * 100)}%";
        await RenderCurrentPageAsync();
    }

    private async void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _zoomLevel = Math.Max(_zoomLevel - 0.25, 0.25);
        TxtZoom.Text = $"{(int)(_zoomLevel * 100)}%";
        await RenderCurrentPageAsync();
    }

    private async void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        _zoomLevel = 1.0;
        TxtZoom.Text = "100%";
        await RenderCurrentPageAsync();
    }

    // ─── Mouse-wheel: Ctrl+wheel → zoom, plain wheel → scroll + page-flip ────

    private async void PdfScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

        if (ctrl)
        {
            double step = e.Delta > 0 ? 0.25 : -0.25;
            _zoomLevel = Math.Clamp(_zoomLevel + step, 0.25, 4.0);
            TxtZoom.Text = $"{(int)(_zoomLevel * 100)}%";
            await RenderCurrentPageAsync();
            e.Handled = true;
            return;
        }

        // Structural edge case: when NextPagesPanel is empty (not yet rendered, or page is
        // short after rotation), the scrollable area ends before the normal transition boundary.
        // Detect "at scroll bottom while trying to go forward" and trigger transition directly.
        if (e.Delta < 0 && !_pageTransitioning && _currentPage < _totalPages - 1 &&
            NextPagesPanel.Children.Count == 0 &&
            PdfScrollViewer.ScrollableHeight - PdfScrollViewer.VerticalOffset < 2)
        {
            e.Handled = true;
            _pageTransitioning = true;
            _currentPage++;
            UpdateNavButtons();
            await RenderCurrentPageAsync();
            _pageTransitioning = false;
            return;
        }
    }

    // ─── Global scrollbar (whole-document position) ───────────────────────────

    private void UpdateGlobalScrollBar()
    {
        if (_renderDocument is null || _totalPages == 0 || _updatingScrollBar || _scrollBarDragging) return;

        double pageH = PdfPageBorder.ActualHeight;
        if (pageH <= 0) return;

        double prevH = PrevPagesPanel.ActualHeight;
        double localOffset  = Math.Max(0, PdfScrollViewer.VerticalOffset - prevH);
        double viewportH    = PdfScrollViewer.ViewportHeight;
        double viewportSize = viewportH / pageH;

        _updatingScrollBar       = true;
        GlobalScrollBar.IsEnabled    = true;
        GlobalScrollBar.Minimum      = 0;
        GlobalScrollBar.Maximum      = _totalPages;
        GlobalScrollBar.ViewportSize = viewportSize;
        GlobalScrollBar.LargeChange  = viewportSize;
        GlobalScrollBar.SmallChange  = viewportSize / 5.0;
        GlobalScrollBar.Value        = _currentPage + localOffset / pageH;
        _updatingScrollBar       = false;
    }

    private async void GlobalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (_renderDocument is null || _updatingScrollBar) return;

        switch (e.ScrollEventType)
        {
            case ScrollEventType.ThumbTrack:
                _scrollBarDragging = true;
                _scrollBarDragTarget = e.NewValue;
                if (!_pageTransitioning)
                    await NavigateGlobalScrollBar(_scrollBarDragTarget);
                return;

            case ScrollEventType.EndScroll:
                // Thumb released: navigate to wherever the user left it.
                if (!_scrollBarDragging) return;
                _scrollBarDragging = false;
                if (_pageTransitioning) return;
                await NavigateGlobalScrollBar(_scrollBarDragTarget);
                return;

            default:
                // Track clicks, arrow buttons, etc. — navigate immediately.
                if (_pageTransitioning) return;
                await NavigateGlobalScrollBar(e.NewValue);
                return;
        }
    }

    private async Task NavigateGlobalScrollBar(double targetValue)
    {
        double pageH = PdfPageBorder.ActualHeight;
        if (pageH <= 0) return;

        double clampedValue      = Math.Clamp(targetValue, 0, _totalPages - 1);
        uint   targetPage        = (uint)Math.Floor(clampedValue);
        if (targetPage >= _totalPages) targetPage = _totalPages - 1;
        double targetLocalOffset = (clampedValue - targetPage) * pageH;

        if (targetPage != _currentPage)
        {
            _pageTransitioning = true;
            _currentPage = targetPage;
            UpdateNavButtons();
            await RenderCurrentPageAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                double prevH = PrevPagesPanel.ActualHeight;
                PdfScrollViewer.ScrollToVerticalOffset(prevH + targetLocalOffset);
            }, System.Windows.Threading.DispatcherPriority.Render);
            _pageTransitioning = false;
        }
        else
        {
            double prevH = PrevPagesPanel.ActualHeight;
            PdfScrollViewer.ScrollToVerticalOffset(prevH + targetLocalOffset);
        }
    }

    private async void PdfScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 || _renderDocument is null) return;

        // Skip during transitions: layout rebuilds (collapsing/showing preview borders)
        // cause intermediate VerticalOffset values that would make the scrollbar jump.
        if (_pageTransitioning) return;

        UpdateGlobalScrollBar();

        double prevH = PrevPagesPanel.ActualHeight;

        // Forward page transition: user scrolled completely past current page into the next.
        if (e.VerticalChange > 0 &&
            _currentPage < _totalPages - 1 &&
            NextPagesPanel.Children.Count > 0)
        {
            double boundary = prevH + PdfPageBorder.ActualHeight + PageGap;
            if (PdfScrollViewer.VerticalOffset >= boundary)
            {
                double newOffset = PdfScrollViewer.VerticalOffset - boundary;
                _pageTransitioning = true;
                _currentPage++;
                UpdateNavButtons();
                await RenderCurrentPageAsync();
                await Dispatcher.InvokeAsync(() => { },
                    System.Windows.Threading.DispatcherPriority.Render);
                double prevHNew = PrevPagesPanel.ActualHeight;
                PdfScrollViewer.ScrollToVerticalOffset(prevHNew + Math.Max(0, newOffset));
                _pageTransitioning = false;
            }
        }

        // Backward page transition: user scrolled past the top of the prev-page previews.
        if (e.VerticalChange < 0 &&
            _currentPage > 0 &&
            PrevPagesPanel.Children.Count > 0 &&
            PdfScrollViewer.VerticalOffset <= 0)
        {
            _pageTransitioning = true;
            _currentPage--;
            UpdateNavButtons();
            await RenderCurrentPageAsync();
            await Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.Render);
            double prevHNew = PrevPagesPanel.ActualHeight;
            double bottomTarget = prevHNew + PdfPageBorder.ActualHeight - PdfScrollViewer.ViewportHeight;
            PdfScrollViewer.ScrollToVerticalOffset(Math.Max(prevHNew, bottomTarget));
            _pageTransitioning = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Rendering + Text Overlay
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task RenderCurrentPageAsync()
    {
        if (_renderDocument is null) return;
        SetStatus("Rendering…");

        try
        {
            BitmapSource displaySrc;

            // PDF mode (includes images-as-temp-PDF)
            using var pdfPage = _renderDocument!.GetPage(_currentPage);
            using var stream  = new InMemoryRandomAccessStream();
            var opts = new WinPdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)(RenderBaseWidth * _zoomLevel)
            };
            await pdfPage.RenderToStreamAsync(stream, opts);
            stream.Seek(0);
            var ms = new MemoryStream();
            await stream.AsStream().CopyToAsync(ms);
            ms.Position = 0;
            var pdfBmp = new BitmapImage();
            pdfBmp.BeginInit();
            pdfBmp.StreamSource = ms;
            pdfBmp.CacheOption  = BitmapCacheOption.OnLoad;
            pdfBmp.EndInit();
            pdfBmp.Freeze();
            displaySrc = pdfBmp;

            var overrideImg = AdjustedStore?.Get((int)_currentPage);
            var finalSrc    = overrideImg ?? displaySrc;

            PdfPageImage.Source  = finalSrc;
            PdfPageImage.Width   = double.NaN;
            PdfPageImage.Height  = double.NaN;
            PdfPageImage.Stretch = Stretch.None;

            // Clear stale previews while new page renders.
            PrevPagesPanel.Children.Clear();
            NextPagesPanel.Children.Clear();

            int dispW = displaySrc.PixelWidth;
            int dispH = displaySrc.PixelHeight;

            if (_currentFilePath is not null && _renderDocument is not null)
                await PopulateTextOverlayAsync(_currentFilePath, (int)_currentPage, dispW, dispH);

            // Pre-render prev pages (await so PrevPagesPanel.ActualHeight is ready for scroll offset).
            await RenderPrevPagesAsync(dispH);

            // Place scroll so current page top is at viewport top.
            await Dispatcher.InvokeAsync(() =>
            {
                double prevH = PrevPagesPanel.ActualHeight;
                PdfScrollViewer.ScrollToVerticalOffset(prevH);
                if (!_pageTransitioning)
                    UpdateGlobalScrollBar();
            }, System.Windows.Threading.DispatcherPriority.Render);

            // Pre-render next pages for smooth scrolling (intentional fire-and-forget).
#pragma warning disable CS4014
            RenderNextPagesAsync(dispH);
#pragma warning restore CS4014

            SetStatus(_pageWords.Count > 0
                ? $"Page {_currentPage + 1} / {_totalPages}  ·  Drag or click to select"
                : $"Page {_currentPage + 1} / {_totalPages}");
            UpdateThumbnailHighlight();
        }
        catch (Exception ex)
        {
            SetStatus($"Render error: {ex.Message}");
        }
    }

    // How many side pages to pre-render so they fill the viewport.
    private int SidePageCount(double pageH)
    {
        if (pageH <= 0) return 1;
        double viewH = PdfScrollViewer.ViewportHeight > 0
            ? PdfScrollViewer.ViewportHeight
            : PdfScrollViewer.ActualHeight;
        if (viewH <= 0) return 1;
        return Math.Max(1, (int)Math.Ceiling(viewH / pageH));
    }

    // Render a single page from the document into an Image element.
    private async Task<System.Windows.Controls.Image> RenderPageImageAsync(uint pageIndex)
    {
        BitmapSource pageSrc;
        using var pdfPage = _renderDocument!.GetPage(pageIndex);
        using var stream  = new InMemoryRandomAccessStream();
        var opts = new WinPdf.PdfPageRenderOptions { DestinationWidth = (uint)(RenderBaseWidth * _zoomLevel) };
        await pdfPage.RenderToStreamAsync(stream, opts);
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
        pageSrc = bmp;

        var overrideImg2 = AdjustedStore?.Get((int)pageIndex);
        var finalSrc2    = overrideImg2 ?? pageSrc;

        var img = new System.Windows.Controls.Image
        {
            Source  = finalSrc2,
            Stretch = System.Windows.Media.Stretch.None
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    // Render N prev pages into PrevPagesPanel (oldest page first at top).
    private async Task RenderPrevPagesAsync(double pageH)
    {
        PrevPagesPanel.Children.Clear();
        if (_renderDocument is null) return;
        if (_currentPage == 0) return;

        int nPrev = Math.Min(SidePageCount(pageH), (int)_currentPage);
        try
        {
            // Render from furthest to nearest so children are in top→bottom reading order.
            for (int i = nPrev; i >= 1; i--)
            {
                var img = await RenderPageImageAsync(_currentPage - (uint)i);
                PrevPagesPanel.Children.Add(new Border
                {
                    Child      = img,
                    Background = Brushes.White,
                    Margin     = new Thickness(0, 0, 0, PageGap),  // gap below = gap to next sibling
                });
            }
        }
        catch { PrevPagesPanel.Children.Clear(); }
    }

    // Render N next pages into NextPagesPanel (nearest page first at top).
    private async Task RenderNextPagesAsync(double pageH)
    {
        NextPagesPanel.Children.Clear();
        if (_renderDocument is null) return;
        if (_currentPage + 1 >= _totalPages) return;

        int nNext = Math.Min(SidePageCount(pageH), (int)(_totalPages - 1 - _currentPage));
        try
        {
            for (int i = 1; i <= nNext; i++)
            {
                var img = await RenderPageImageAsync(_currentPage + (uint)i);
                NextPagesPanel.Children.Add(new Border
                {
                    Child      = img,
                    Background = Brushes.White,
                    Margin     = new Thickness(0, PageGap, 0, 0),  // gap above = gap from previous sibling
                });
            }
        }
        catch { NextPagesPanel.Children.Clear(); }
    }

    private async Task PopulateTextOverlayAsync(string pdfPath, int pageIndex,
                                                double imageWidth, double imageHeight)
    {
        _pageWords.Clear();
        _pageLines.Clear();
        _readingOrder.Clear();
        _wordToOrder.Clear();
        _selectedIndices.Clear();
        _lastClickedOrder = -1;
        _lastHoverIndex   = -1;
        _isDragging       = false;
        _dragAnchorOrder  = -1;
        _dragAnchorLine   = null;
        TextOverlayCanvas.Children.Clear();

        // Use the pre-captured bytes (set after each Save in LoadPdfAsync / ReloadRenderDocumentAsync).
        // PdfPig normalises word coordinates to the visual page space for any /Rotate value,
        // so opening the same rotated bytes that WinPdf rendered means the simple Y-flip formula
        // works correctly regardless of rotation.
        // IMPORTANT: never call _editDocument.Save() here — PDFsharp locks the document after Save().
        byte[]? editBytes = _editPdfBytes;

        var (words, pageRotation) = await Task.Run(() =>
        {
            var result = new List<WordBox>();
            int rotDeg = 0;
            try
            {
                PigPdf.PdfDocument doc = editBytes != null
                    ? PigPdf.PdfDocument.Open(editBytes)
                    : PigPdf.PdfDocument.Open(pdfPath);

                using (doc)
                {
                    var page = doc.GetPage(pageIndex + 1);

                    // PdfPig word coordinates are in the ORIGINAL (pre-rotation) PDF user space:
                    //   origin at bottom-left of the unrotated page, Y increases upward.
                    // W / H = raw MediaBox dimensions (never rotation-adjusted, always the same).
                    // The rotation-specific formulas below map these original coords to canvas
                    // (Y-down, origin at top-left of the rendered bitmap).
                    double W = page.MediaBox.Bounds.Width;   // original page width  (e.g. 612 for portrait)
                    double H = page.MediaBox.Bounds.Height;  // original page height (e.g. 792 for portrait)
                    rotDeg = page.Rotation.Value;            // 0 / 90 / 180 / 270
                    if (rotDeg != 0) return (result, rotDeg); // text selection disabled for rotated pages

                    foreach (var w in NearestNeighbourWordExtractor.Instance.GetWords(page.Letters))
                    {
                        var b = w.BoundingBox;
                        // Each case derives from: original (x,y) → display (Y-up) → canvas (Y-down).
                        // 90°  CW : display_x = y,     display_y = W-x  → imageWidth ∝ H, imageHeight ∝ W
                        // 270° CW : display_x = H-y,   display_y = x    → imageWidth ∝ H, imageHeight ∝ W
                        // 180°    : display_x = W-x,   display_y = H-y  → imageWidth ∝ W, imageHeight ∝ H
                        // PdfPig 0.1.9 normalizes word coords to the visual (post-rotation) page space.
                        // For 90°/270°: visual page is H×W (width=H, height=W).
                        // For 0°/180°: visual page is W×H (same original dims).
                        WordBox wb = rotDeg switch
                        {
                            90  => new WordBox(w.Text,
                                       b.Left        / H * imageWidth,
                                       (W - b.Top)   / W * imageHeight,
                                       b.Width       / H * imageWidth,
                                       b.Height      / W * imageHeight),
                            180 => new WordBox(w.Text,
                                       b.Left        / W * imageWidth,
                                       (H - b.Top)   / H * imageHeight,
                                       b.Width       / W * imageWidth,
                                       b.Height      / H * imageHeight),
                            270 => new WordBox(w.Text,
                                       b.Left        / H * imageWidth,
                                       (W - b.Top)   / W * imageHeight,
                                       b.Width       / H * imageWidth,
                                       b.Height      / W * imageHeight),
                            _   => new WordBox(w.Text,
                                       b.Left        / W * imageWidth,
                                       (H - b.Top)   / H * imageHeight,
                                       b.Width       / W * imageWidth,
                                       b.Height      / H * imageHeight),
                        };
                        result.Add(wb);
                    }
                }
            }
            catch { /* non-searchable PDF */ }
            return (result, rotDeg);
        });

        _currentPageRotation = pageRotation;
        _pageWords.AddRange(words);
        BuildLineStructure();

        TextOverlayCanvas.Width  = imageWidth;
        TextOverlayCanvas.Height = imageHeight;

        foreach (var w in _pageWords)
        {
            var border = new Border
            {
                Width            = Math.Max(w.Width,  2),
                Height           = Math.Max(w.Height, 2),
                Background       = Brushes.Transparent,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(border, w.Left);
            Canvas.SetTop(border,  w.Top);
            TextOverlayCanvas.Children.Add(border);
        }

        _selectedAnnotation = null;
        CloseAnnotationPopup();
        AnnotationCanvas.Width  = imageWidth;
        AnnotationCanvas.Height = imageHeight;
        MemoCanvas.Width  = imageWidth;
        MemoCanvas.Height = imageHeight;

        if (_editDocument != null && _currentPage < (uint)_editDocument.Pages.Count)
        {
            var p = _editDocument.Pages[(int)_currentPage];
            _currentPagePdfWidth  = p.Width.Point;
            _currentPagePdfHeight = p.Height.Point;
        }
        else
        {
            _currentPagePdfWidth  = imageWidth;
            _currentPagePdfHeight = imageHeight;
        }

        DrawAnnotations();
        DrawMemos();

        DrawingCanvas.Width  = imageWidth;
        DrawingCanvas.Height = imageHeight;
        RefreshDrawingCanvas();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Line Structure
    // ═══════════════════════════════════════════════════════════════════════════

    private void BuildLineStructure()
    {
        _pageLines.Clear();
        _readingOrder.Clear();
        _wordToOrder.Clear();
        _wordToLine.Clear();

        // 90°/270° rotated pages: original horizontal text lines become vertical columns.
        // Group by X overlap, sort within column by Top, order columns left-to-right.
        bool verticalText = _currentPageRotation == 90 || _currentPageRotation == 270;

        // Heuristic fallback: if most word boxes are taller than wide, text is vertical.
        // This handles cases where page.Rotation.Value does not reflect the visual orientation.
        if (!verticalText && _pageWords.Count > 3)
        {
            int tallerCount = _pageWords.Count(w => w.Height > w.Width * 1.5);
            if (tallerCount > _pageWords.Count / 2)
                verticalText = true;
        }

        _isVerticalText = verticalText;

        if (verticalText)
        {
            var byCenterX = Enumerable.Range(0, _pageWords.Count)
                                      .OrderBy(i => _pageWords[i].CenterX)
                                      .ToList();

            foreach (int wi in byCenterX)
            {
                var w = _pageWords[wi];
                bool placed = false;

                foreach (var col in _pageLines)
                {
                    double colLeft  = col.Min(i => _pageWords[i].Left);
                    double colRight = col.Max(i => _pageWords[i].Right);

                    if (w.Left < colRight && w.Right > colLeft)
                    {
                        col.Add(wi);
                        placed = true;
                        break;
                    }
                }

                if (!placed) _pageLines.Add(new List<int> { wi });
            }

            foreach (var col in _pageLines)
                col.Sort((a, b) => _pageWords[a].Top.CompareTo(_pageWords[b].Top));

            _pageLines.Sort((a, b) => _pageWords[a[0]].Left.CompareTo(_pageWords[b[0]].Left));
        }
        else
        {
            var byY = Enumerable.Range(0, _pageWords.Count)
                                .OrderBy(i => _pageWords[i].CenterY)
                                .ToList();

            foreach (int wi in byY)
            {
                var w = _pageWords[wi];
                bool placed = false;

                foreach (var line in _pageLines)
                {
                    double lineTop    = line.Min(i => _pageWords[i].Top);
                    double lineBottom = line.Max(i => _pageWords[i].Bottom);

                    if (w.Top < lineBottom && w.Bottom > lineTop)
                    {
                        line.Add(wi);
                        placed = true;
                        break;
                    }
                }

                if (!placed) _pageLines.Add(new List<int> { wi });
            }

            foreach (var line in _pageLines)
                line.Sort((a, b) => _pageWords[a].Left.CompareTo(_pageWords[b].Left));

            _pageLines.Sort((a, b) => _pageWords[a[0]].Top.CompareTo(_pageWords[b[0]].Top));
        }

        for (int li = 0; li < _pageLines.Count; li++)
        {
            foreach (int wi in _pageLines[li])
            {
                _wordToOrder[wi] = _readingOrder.Count;
                _wordToLine[wi]  = li;
                _readingOrder.Add(wi);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Canvas Mouse Handlers
    // ═══════════════════════════════════════════════════════════════════════════

    private void TextOverlayCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(TextOverlayCanvas);

        // Close any open memo popup on canvas click
        if (_memoEditPopup is { IsOpen: true })
        {
            _memoEditPopup.IsOpen = false;
            _memoEditPopup = null;
            e.Handled = true;
            return;
        }

        // Hit-test memo icons first
        var hitMemo = HitTestMemo(pos);
        if (hitMemo != null)
        {
            ShowMemoEditPopup(hitMemo);
            TextOverlayCanvas.Focus();
            e.Handled = true;
            return;
        }

        var hitAnn = HitTestAnnotation(pos);
        if (hitAnn != null)
        {
            _selectedAnnotation = hitAnn;
            DrawAnnotations();
            ShowAnnotationColorPicker(hitAnn);
            TextOverlayCanvas.Focus();
            e.Handled = true;
            return;
        }

        // Hit-test drawn shapes (Line/Box/Circle) — only in select mode
        if (_drawMode == DrawMode.None)
        {
            var hitShape = HitTestDrawingShape(pos);
            if (hitShape != null)
            {
                _selectedShape = hitShape;
                _selectedAnnotation = null;
                CloseAnnotationPopup();
                RefreshDrawingCanvas();
                ShowShapeColorPicker(hitShape);
                TextOverlayCanvas.Focus();
                e.Handled = true;
                return;
            }

            if (_selectedShape != null)
            {
                _selectedShape = null;
                CloseShapeColorPopup();
                RefreshDrawingCanvas();
            }
        }

        if (_selectedAnnotation != null)
        {
            _selectedAnnotation = null;
            CloseAnnotationPopup();
            DrawAnnotations();
        }

        if (_readingOrder.Count == 0) return;

        _dragStart       = pos;
        _isDragging      = false;
        _dragAnchorOrder = GetNearestReadingOrder(_dragStart);
        _dragAnchorLine  = GetNearestLine(_dragStart);

        TextOverlayCanvas.CaptureMouse();
        TextOverlayCanvas.Focus();
        e.Handled = true;
    }

    private void TextOverlayCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(TextOverlayCanvas);

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_isDragging)
            {
                var d = pos - _dragStart;
                if (Math.Abs(d.X) > 5 || Math.Abs(d.Y) > 5)
                    _isDragging = true;
            }

            if (_isDragging && _dragAnchorOrder >= 0)
            {
                // For vertical text (90°/270°), lock to anchor column when dragging primarily vertically.
                // This prevents minor X wobble from jumping across the narrow columns.
                bool vertDrag = _isVerticalText && _dragAnchorLine != null;
                if (vertDrag)
                {
                    var d = pos - _dragStart;
                    vertDrag = Math.Abs(d.Y) >= Math.Abs(d.X); // primarily vertical?
                }

                int currentOrder;
                if (vertDrag)
                    currentOrder = GetNearestReadingOrderInLine(_dragAnchorLine!, pos);
                else
                    currentOrder = GetNearestReadingOrder(pos);

                if (currentOrder < 0) return;

                int lo = Math.Min(_dragAnchorOrder, currentOrder);
                int hi = Math.Max(_dragAnchorOrder, currentOrder);

                var next = new HashSet<int>();
                for (int k = lo; k <= hi; k++) next.Add(_readingOrder[k]);

                if (next.Count != _selectedIndices.Count || !next.SetEquals(_selectedIndices))
                {
                    _selectedIndices.Clear();
                    foreach (var idx in next) _selectedIndices.Add(idx);
                    RefreshHighlights();
                    UpdateSelectionStatus();
                }
            }
        }
        else
        {
            int idx = GetWordIndexAt(pos);
            if (idx != _lastHoverIndex)
            {
                if (_lastHoverIndex >= 0 && !_selectedIndices.Contains(_lastHoverIndex))
                    SetWordBackground(_lastHoverIndex, Brushes.Transparent);
                if (idx >= 0 && !_selectedIndices.Contains(idx))
                    SetWordBackground(idx, HoverBrush);
                _lastHoverIndex = idx;
            }
            TextOverlayCanvas.Cursor = idx >= 0 ? Cursors.IBeam : Cursors.Arrow;
        }
    }

    private void TextOverlayCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        TextOverlayCanvas.ReleaseMouseCapture();
        var pos   = e.GetPosition(TextOverlayCanvas);
        bool ctrl  = Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl);
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

        if (_isDragging)
        {
            _isDragging = false;
            int endOrder = GetNearestReadingOrder(pos);
            if (endOrder >= 0) _lastClickedOrder = endOrder;
        }
        else
        {
            int clickedIdx = GetWordIndexAt(pos);

            if (clickedIdx < 0)
            {
                if (!ctrl && !shift) _selectedIndices.Clear();
            }
            else
            {
                int clickedOrder = _wordToOrder.GetValueOrDefault(clickedIdx, -1);

                if (ctrl)
                {
                    if (!_selectedIndices.Remove(clickedIdx)) _selectedIndices.Add(clickedIdx);
                }
                else if (shift && _lastClickedOrder >= 0)
                {
                    int lo = Math.Min(_lastClickedOrder, clickedOrder);
                    int hi = Math.Max(_lastClickedOrder, clickedOrder);
                    for (int k = lo; k <= hi; k++) _selectedIndices.Add(_readingOrder[k]);
                }
                else
                {
                    _selectedIndices.Clear();
                    _selectedIndices.Add(clickedIdx);
                }

                _lastClickedOrder = clickedOrder;
            }

            RefreshHighlights();
            UpdateSelectionStatus();
        }

        e.Handled = true;
    }

    private void TextOverlayCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_lastHoverIndex >= 0 && !_selectedIndices.Contains(_lastHoverIndex))
        {
            SetWordBackground(_lastHoverIndex, Brushes.Transparent);
            _lastHoverIndex = -1;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Keyboard
    // ═══════════════════════════════════════════════════════════════════════════

    private void TextOverlayCanvas_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

        if (ctrl && e.Key == Key.C)      { CopySelectionToClipboard(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A) { SelectAllWords();            e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            _selectedIndices.Clear();
            RefreshHighlights();
            UpdateSelectionStatus();
            e.Handled = true;
        }
    }

    private void SelectAllWords()
    {
        if (_pageWords.Count == 0) { SetStatus("No searchable text on this page."); return; }
        _selectedIndices.Clear();
        for (int i = 0; i < _pageWords.Count; i++) _selectedIndices.Add(i);
        RefreshHighlights();
        UpdateSelectionStatus();
    }

    private void CopySelectionToClipboard()
    {
        if (_selectedIndices.Count == 0) return;
        var text = string.Join(" ",
            _readingOrder.Where(_selectedIndices.Contains).Select(i => _pageWords[i].Text));
        Clipboard.SetText(text);
        SetStatus($"Copied {_selectedIndices.Count} word(s) to clipboard.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Hit-test Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private int GetWordIndexAt(Point pt)
    {
        for (int i = 0; i < _pageWords.Count; i++)
        {
            var w = _pageWords[i];
            if (pt.X >= w.Left && pt.X <= w.Right &&
                pt.Y >= w.Top  && pt.Y <= w.Bottom)
                return i;
        }
        return -1;
    }

    private int GetNearestReadingOrder(Point pt)
    {
        if (_pageLines.Count == 0) return -1;

        // For 90°/270° rotated pages, "lines" are vertical columns — nearest column by X,
        // nearest word within column by Y. For 0°/180°, nearest line by Y, nearest word by X.
        bool verticalText = _currentPageRotation == 90 || _currentPageRotation == 270;

        List<int>? nearestLine = null;
        double minLineDist = double.MaxValue;

        foreach (var line in _pageLines)
        {
            double dist;
            if (verticalText)
            {
                double colLeft  = line.Min(i => _pageWords[i].Left);
                double colRight = line.Max(i => _pageWords[i].Right);
                if      (pt.X < colLeft)  dist = colLeft  - pt.X;
                else if (pt.X > colRight) dist = pt.X - colRight;
                else                       dist = 0;
            }
            else
            {
                double lineTop    = line.Min(i => _pageWords[i].Top);
                double lineBottom = line.Max(i => _pageWords[i].Bottom);
                if      (pt.Y < lineTop)    dist = lineTop    - pt.Y;
                else if (pt.Y > lineBottom) dist = pt.Y - lineBottom;
                else                         dist = 0;
            }

            if (dist < minLineDist) { minLineDist = dist; nearestLine = line; }
        }

        if (nearestLine is null) return -1;

        int bestWordIdx = nearestLine[0];
        double bestDist = double.MaxValue;

        foreach (int wi in nearestLine)
        {
            var w = _pageWords[wi];
            double dist;
            if (verticalText)
            {
                if      (pt.Y < w.Top)    dist = w.Top    - pt.Y;
                else if (pt.Y > w.Bottom) dist = pt.Y - w.Bottom;
                else                       dist = 0;
            }
            else
            {
                if      (pt.X < w.Left)  dist = w.Left  - pt.X;
                else if (pt.X > w.Right) dist = pt.X - w.Right;
                else                      dist = 0;
            }

            if (dist < bestDist)
            {
                bestDist    = dist;
                bestWordIdx = wi;
                if (dist == 0) break;
            }
        }

        return _wordToOrder.GetValueOrDefault(bestWordIdx, -1);
    }

    private List<int>? GetNearestLine(Point pt)
    {
        if (_pageLines.Count == 0) return null;
        bool verticalText = _currentPageRotation == 90 || _currentPageRotation == 270;
        List<int>? nearest = null;
        double minDist = double.MaxValue;
        foreach (var line in _pageLines)
        {
            double dist;
            if (verticalText)
            {
                double colLeft  = line.Min(i => _pageWords[i].Left);
                double colRight = line.Max(i => _pageWords[i].Right);
                dist = pt.X < colLeft ? colLeft - pt.X : pt.X > colRight ? pt.X - colRight : 0;
            }
            else
            {
                double top    = line.Min(i => _pageWords[i].Top);
                double bottom = line.Max(i => _pageWords[i].Bottom);
                dist = pt.Y < top ? top - pt.Y : pt.Y > bottom ? pt.Y - bottom : 0;
            }
            if (dist < minDist) { minDist = dist; nearest = line; }
        }
        return nearest;
    }

    // Returns reading order of the word nearest to pt.Y within a fixed line/column.
    private int GetNearestReadingOrderInLine(List<int> line, Point pt)
    {
        if (line.Count == 0) return -1;
        bool verticalText = _currentPageRotation == 90 || _currentPageRotation == 270;
        int bestIdx = line[0];
        double bestDist = double.MaxValue;
        foreach (int wi in line)
        {
            var w = _pageWords[wi];
            double dist;
            if (verticalText)
                dist = pt.Y < w.Top ? w.Top - pt.Y : pt.Y > w.Bottom ? pt.Y - w.Bottom : 0;
            else
                dist = pt.X < w.Left ? w.Left - pt.X : pt.X > w.Right ? pt.X - w.Right : 0;
            if (dist < bestDist) { bestDist = dist; bestIdx = wi; if (dist == 0) break; }
        }
        return _wordToOrder.GetValueOrDefault(bestIdx, -1);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Visual Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private Border? GetWordBorder(int wordIndex)
    {
        if ((uint)wordIndex >= (uint)_pageWords.Count) return null;
        return TextOverlayCanvas.Children[wordIndex] as Border;
    }

    private void SetWordBackground(int wordIndex, Brush brush)
    {
        var b = GetWordBorder(wordIndex);
        if (b is not null) b.Background = brush;
    }

    private void RefreshHighlights()
    {
        for (int i = 0; i < _pageWords.Count; i++)
            SetWordBackground(i, _selectedIndices.Contains(i) ? SelectedBrush : Brushes.Transparent);
        _lastHoverIndex = -1;
    }

    private void UpdateSelectionStatus()
    {
        if (_selectedIndices.Count == 0)
        {
            SetStatus($"Page {_currentPage + 1} / {_totalPages}  ·  Drag or click to select");
            return;
        }

        var preview = string.Join(" ",
            _readingOrder.Where(_selectedIndices.Contains).Select(i => _pageWords[i].Text));
        if (preview.Length > 60) preview = preview[..57] + "…";
        SetStatus($"{_selectedIndices.Count} word(s) selected: \"{preview}\"");
    }

    private void SetStatus(string msg) => TxtStatus.Text = msg;

    // ═══════════════════════════════════════════════════════════════════════════
    //  Annotations – create
    // ═══════════════════════════════════════════════════════════════════════════

    private void TextOverlayCanvas_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var pos    = Mouse.GetPosition(TextOverlayCanvas);
        var hitAnn = HitTestAnnotation(pos);

        if (hitAnn != null)
        {
            e.Handled = true;
            _selectedAnnotation = hitAnn;
            DrawAnnotations();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                () => ShowAnnotationColorPicker(hitAnn));
            return;
        }

        // Capture PDF-space coordinates for "Add Memo"
        if (_currentPagePdfHeight > 0)
        {
            double toX = _currentPagePdfWidth  / TextOverlayCanvas.Width;
            double toY = _currentPagePdfHeight / TextOverlayCanvas.Height;
            _memoInsertPdfX = pos.X * toX;
            _memoInsertPdfY = _currentPagePdfHeight - pos.Y * toY;
        }

        var menu   = TextOverlayCanvas.ContextMenu;
        menu.Items.Clear();
        bool hasSel = _selectedIndices.Count > 0;

        if (hasSel)
        {
            menu.Items.Add(MakeAnnotationSubMenu("Highlight",     AnnotationType.Highlight,     HighlightPresets));
            menu.Items.Add(MakeAnnotationSubMenu("Underline",     AnnotationType.Underline,     LinePresets));
            menu.Items.Add(MakeAnnotationSubMenu("Strikethrough", AnnotationType.Strikethrough, LinePresets));
            menu.Items.Add(new Separator());
        }

        var copyItem = new MenuItem { Header = "Copy Selected Text", InputGestureText = "Ctrl+C", IsEnabled = hasSel };
        copyItem.Click += (_, _) => CopySelectionToClipboard();
        menu.Items.Add(copyItem);

        var bmItem = new MenuItem { Header = "Add Bookmark", InputGestureText = "Ctrl+B" };
        bmItem.Click += (_, _) => AddBookmarkForCurrentPage();
        menu.Items.Add(bmItem);

        menu.Items.Add(new Separator());

        var memoItem = new MenuItem { Header = "Add Memo" };
        memoItem.Click += (_, _) => ShowMemoEditPopup(null);
        menu.Items.Add(memoItem);
    }

    private MenuItem MakeAnnotationSubMenu(string header, AnnotationType type,
        (Color Color, string Name)[] presets)
    {
        var item = new MenuItem { Header = header };
        foreach (var (c, name) in presets)
        {
            var icon = new Border
            {
                Width = 14, Height = 14,
                Background   = new SolidColorBrush(c),
                CornerRadius = new CornerRadius(3)
            };
            var sub = new MenuItem { Header = name, Icon = icon };
            sub.Click += (_, _) => AddAnnotation(type, c);
            item.Items.Add(sub);
        }
        return item;
    }

    private void AddAnnotation(AnnotationType type, Color color)
    {
        if (_currentFilePath is null || _selectedIndices.Count == 0) return;
        if (_currentPagePdfWidth <= 0 || _currentPagePdfHeight <= 0) return;

        double toX = _currentPagePdfWidth  / AnnotationCanvas.Width;
        double toY = _currentPagePdfHeight / AnnotationCanvas.Height;

        var spans = _selectedIndices
            .GroupBy(i => _wordToLine.GetValueOrDefault(i, -1))
            .Where(g => g.Key >= 0)
            .Select(g =>
            {
                var ws = g.Select(i => _pageWords[i]).ToList();
                double cl = ws.Min(w => w.Left);
                double cr = ws.Max(w => w.Right);
                double ct = ws.Min(w => w.Top);
                double cb = ws.Max(w => w.Bottom);
                return new AnnotationSpan
                {
                    Left   = cl * toX,
                    Right  = cr * toX,
                    Top    = _currentPagePdfHeight - ct * toY,    // canvas top-down → PDF bottom-up
                    Bottom = _currentPagePdfHeight - cb * toY,
                };
            })
            .ToList();

        if (spans.Count == 0) return;

        AnnotationService.Instance.Add(new TextAnnotation
        {
            Type       = type,
            Color      = color,
            FilePath   = _currentFilePath,
            PageNumber = _currentPage,
            Spans      = spans
        });
        DrawAnnotations();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Annotations – draw
    // ═══════════════════════════════════════════════════════════════════════════

    private void DrawAnnotations()
    {
        AnnotationCanvas.Children.Clear();
        if (_currentFilePath is null || _currentPagePdfHeight <= 0) return;

        double sx = AnnotationCanvas.Width  / _currentPagePdfWidth;
        double sy = AnnotationCanvas.Height / _currentPagePdfHeight;

        foreach (var ann in AnnotationService.Instance.ForPage(_currentFilePath, _currentPage))
        {
            bool sel = _selectedAnnotation?.Id == ann.Id;

            foreach (var span in ann.Spans)
            {
                double cl  = span.Left   * sx;
                double cr  = span.Right  * sx;
                double ct  = (_currentPagePdfHeight - span.Top)    * sy;
                double cb  = (_currentPagePdfHeight - span.Bottom) * sy;
                double mid = (ct + cb) / 2;

                switch (ann.Type)
                {
                    case AnnotationType.Highlight:
                    {
                        var rect = new Rectangle
                        {
                            Width  = cr - cl, Height = cb - ct,
                            Fill   = new SolidColorBrush(Color.FromArgb(
                                         sel ? (byte)190 : (byte)110,
                                         ann.Color.R, ann.Color.G, ann.Color.B)),
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(rect, cl); Canvas.SetTop(rect, ct);
                        AnnotationCanvas.Children.Add(rect);
                        break;
                    }
                    case AnnotationType.Underline:
                    {
                        double h = sel ? 3 : 2;
                        var line = new Rectangle
                        {
                            Width = cr - cl, Height = h,
                            Fill  = new SolidColorBrush(ann.Color),
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(line, cl); Canvas.SetTop(line, cb + 1);
                        AnnotationCanvas.Children.Add(line);
                        break;
                    }
                    case AnnotationType.Strikethrough:
                    {
                        double h = sel ? 3 : 2;
                        var line = new Rectangle
                        {
                            Width = cr - cl, Height = h,
                            Fill  = new SolidColorBrush(ann.Color),
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(line, cl); Canvas.SetTop(line, mid - h / 2);
                        AnnotationCanvas.Children.Add(line);
                        break;
                    }
                }

                if (sel)
                {
                    var selBorder = new Border
                    {
                        Width  = cr - cl + 4, Height = cb - ct + 4,
                        BorderBrush     = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
                        BorderThickness = new Thickness(1),
                        CornerRadius    = new CornerRadius(2),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(selBorder, cl - 2); Canvas.SetTop(selBorder, ct - 2);
                    AnnotationCanvas.Children.Add(selBorder);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Annotations – hit-test
    // ═══════════════════════════════════════════════════════════════════════════

    private TextAnnotation? HitTestAnnotation(Point pt)
    {
        if (_currentFilePath is null || _currentPagePdfHeight <= 0) return null;
        double sx = AnnotationCanvas.Width  / _currentPagePdfWidth;
        double sy = AnnotationCanvas.Height / _currentPagePdfHeight;

        foreach (var ann in AnnotationService.Instance.ForPage(_currentFilePath, _currentPage))
        {
            foreach (var span in ann.Spans)
            {
                double cl = span.Left  * sx;
                double cr = span.Right * sx;
                double ct = (_currentPagePdfHeight - span.Top)    * sy;
                double cb = (_currentPagePdfHeight - span.Bottom) * sy;
                if (ann.Type != AnnotationType.Highlight) { ct -= 5; cb += 5; }
                if (pt.X >= cl && pt.X <= cr && pt.Y >= ct && pt.Y <= cb)
                    return ann;
            }
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Annotations – color picker popup
    // ═══════════════════════════════════════════════════════════════════════════

    private void ShowAnnotationColorPicker(TextAnnotation ann)
    {
        CloseAnnotationPopup();

        var presets = ann.Type == AnnotationType.Highlight ? HighlightPresets : LinePresets;
        var row     = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };

        foreach (var (c, name) in presets)
        {
            bool isCur = ann.Color == c;
            var swatch = new Border
            {
                Width  = 22, Height = 22, Margin = new Thickness(2),
                Background      = new SolidColorBrush(c),
                BorderBrush     = new SolidColorBrush(isCur ? Colors.White
                                      : Color.FromRgb(0x45, 0x47, 0x5A)),
                BorderThickness = new Thickness(isCur ? 2 : 1),
                CornerRadius    = new CornerRadius(11),
                Cursor          = Cursors.Hand,
                ToolTip         = name
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                ann.Color = c;
                CloseAnnotationPopup();
                DrawAnnotations();
            };
            row.Children.Add(swatch);
        }

        row.Children.Add(new Border
        {
            Width = 1, Height = 18, Margin = new Thickness(4, 0, 4, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            VerticalAlignment = VerticalAlignment.Center
        });

        var del = new Border
        {
            Width = 22, Height = 22, Margin = new Thickness(2),
            Background      = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(11),
            Cursor          = Cursors.Hand,
            ToolTip         = "Delete annotation"
        };
        del.Child = new TextBlock
        {
            Text = "✕", FontSize = 10,
            Foreground          = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        del.MouseLeftButtonUp += (_, _) =>
        {
            AnnotationService.Instance.Remove(ann.Id);
            _selectedAnnotation = null;
            CloseAnnotationPopup();
            DrawAnnotations();
        };
        row.Children.Add(del);

        var outer = new Border
        {
            Child           = row,
            Background      = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Effect          = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 12, ShadowDepth = 2, Opacity = 0.6
            }
        };

        var popup = new Popup
        {
            PlacementTarget    = TextOverlayCanvas,
            Placement          = PlacementMode.Mouse,
            StaysOpen          = false,
            AllowsTransparency = true,
            Child              = outer
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_annotationPopup, popup))
            {
                _annotationPopup    = null;
                _selectedAnnotation = null;
                DrawAnnotations();
            }
        };
        _annotationPopup = popup;
        popup.IsOpen = true;
    }

    private void CloseAnnotationPopup()
    {
        var p = _annotationPopup;
        _annotationPopup = null;          // null first so Closed handler skips deselect
        if (p is { IsOpen: true })
            p.IsOpen = false;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PDF persistence – embed (save) and load
    // ═══════════════════════════════════════════════════════════════════════════

    private void EmbedAnnotationsAndMemos()
    {
        if (_editDocument is null || _currentFilePath is null) return;

        var allAnns = AnnotationService.Instance.ForFile(_currentFilePath).ToList();

        for (int pi = 0; pi < _editDocument.Pages.Count; pi++)
        {
            var page = _editDocument.Pages[pi];

            // Collect items from the existing /Annots array that don't belong to us.
            var toKeep = new List<PdfItem>();
            var existingAnnots = page.Elements.GetArray("/Annots");
            if (existingAnnots != null)
            {
                for (int i = 0; i < existingAnnots.Elements.Count; i++)
                {
                    var item = existingAnnots.Elements[i];
                    if (!IsOurAnnotation(item))
                        toKeep.Add(item);
                }
            }

            // Build a fresh /Annots array = kept third-party items + our new items.
            var annots = new PdfArray(_editDocument);
            foreach (var kept in toKeep)
                annots.Elements.Add(kept);

            // Embed text annotations (highlight / underline / strikethrough).
            foreach (var ann in allAnns.Where(a => (int)a.PageNumber == pi && a.Spans.Count > 0))
                EmbedTextAnnotation(annots, ann);

            // Embed memos as PDF /Text (sticky-note) annotations.
            foreach (var memo in MemoService.Instance.ForPage(_currentFilePath, (uint)pi))
                EmbedMemo(annots, memo);

            // Embed drawn shapes (Line/Box/Circle).
            foreach (var shape in DrawingService.Instance.ForPage(_currentFilePath, (uint)pi))
                EmbedDrawingShape(annots, shape);

            page.Elements["/Annots"] = annots;
        }
    }

    private void EmbedDrawingShape(PdfArray annots, DrawingShape shape)
    {
        // Store as a /Square (box), /Circle, or /Line PDF annotation.
        string subtype = shape.ShapeType switch
        {
            DrawingShapeType.Box    => "/Square",
            DrawingShapeType.Circle => "/Circle",
            _                       => "/Line"
        };

        double minX = Math.Min(shape.X1, shape.X2);
        double minY = Math.Min(shape.Y1, shape.Y2);
        double maxX = Math.Max(shape.X1, shape.X2);
        double maxY = Math.Max(shape.Y1, shape.Y2);

        var d = new PdfDictionary(_editDocument);
        d.Elements["/Type"]    = new PdfName("/Annot");
        d.Elements["/Subtype"] = new PdfName(subtype);
        d.Elements["/T"]       = new PdfString("HoloPDF Drawing");
        d.Elements["/F"]       = new PdfInteger(4);
        d.Elements["/Rect"]    = MakePdfArray(minX, minY, maxX, maxY);
        d.Elements["/C"]       = MakePdfArray(
            shape.Stroke.R / 255.0, shape.Stroke.G / 255.0, shape.Stroke.B / 255.0);

        if (shape.ShapeType != DrawingShapeType.Line && shape.HasFill)
            d.Elements["/IC"] = MakePdfArray(
                shape.Fill.R / 255.0, shape.Fill.G / 255.0, shape.Fill.B / 255.0);

        if (shape.ShapeType == DrawingShapeType.Line)
            d.Elements["/L"] = MakePdfArray(shape.X1, shape.Y1, shape.X2, shape.Y2);

        _editDocument!.Internals.AddObject(d);
        annots.Elements.Add(d.Reference!);
    }

    private static bool IsOurAnnotation(PdfItem item)
    {
        PdfDictionary? d = item as PdfDictionary;
        if (d is null && item is PdfSharp.Pdf.Advanced.PdfReference r)
            d = r.Value as PdfDictionary;
        var t = d?.Elements.GetString("/T") ?? "";
        return t is "HoloPDF Creator" or "HoloPDF Memo" or "HoloPDF Drawing";
    }

    private void EmbedTextAnnotation(PdfArray annots, TextAnnotation ann)
    {
        string subtype = ann.Type switch
        {
            AnnotationType.Highlight     => "/Highlight",
            AnnotationType.Underline     => "/Underline",
            AnnotationType.Strikethrough => "/StrikeOut",
            _                            => "/Highlight"
        };

        var d = new PdfDictionary(_editDocument);
        d.Elements["/Type"]       = new PdfName("/Annot");
        d.Elements["/Subtype"]    = new PdfName(subtype);
        d.Elements["/T"]          = new PdfString("HoloPDF Creator");
        d.Elements["/F"]          = new PdfInteger(4);
        d.Elements["/Rect"]       = MakePdfArray(
            ann.Spans.Min(s => s.Left),   ann.Spans.Min(s => s.Bottom),
            ann.Spans.Max(s => s.Right),  ann.Spans.Max(s => s.Top));

        var qp = new PdfArray(_editDocument);
        foreach (var span in ann.Spans)
        {
            // UL, UR, LL, LR (PDF Y-up space)
            qp.Elements.Add(new PdfReal(span.Left));   qp.Elements.Add(new PdfReal(span.Top));
            qp.Elements.Add(new PdfReal(span.Right));  qp.Elements.Add(new PdfReal(span.Top));
            qp.Elements.Add(new PdfReal(span.Left));   qp.Elements.Add(new PdfReal(span.Bottom));
            qp.Elements.Add(new PdfReal(span.Right));  qp.Elements.Add(new PdfReal(span.Bottom));
        }
        d.Elements["/QuadPoints"] = qp;
        d.Elements["/C"]          = MakePdfArray(
            ann.Color.R / 255.0, ann.Color.G / 255.0, ann.Color.B / 255.0);
        if (ann.Type == AnnotationType.Highlight)
            d.Elements["/CA"] = new PdfReal(0.5);

        _editDocument.Internals.AddObject(d);
        annots.Elements.Add(d.Reference!);
    }

    private void EmbedMemo(PdfArray annots, PdfMemo memo)
    {
        double x = memo.X, y = memo.Y;
        var d = new PdfDictionary(_editDocument);
        d.Elements["/Type"]     = new PdfName("/Annot");
        d.Elements["/Subtype"]  = new PdfName("/Text");
        d.Elements["/T"]        = new PdfString("HoloPDF Memo");
        d.Elements["/Contents"] = new PdfString(memo.Content, PdfStringEncoding.Unicode);
        d.Elements["/Rect"]     = MakePdfArray(x - 11, y - 22, x + 11, y);
        d.Elements["/C"]        = MakePdfArray(1.0, 0.84, 0.0);
        d.Elements["/F"]        = new PdfInteger(4);
        d.Elements["/Name"]     = new PdfName("/Note");

        _editDocument.Internals.AddObject(d);
        annots.Elements.Add(d.Reference!);
    }

    private void LoadAnnotationsAndMemosFromPdf()
    {
        if (_editDocument is null || _currentFilePath is null) return;

        AnnotationService.Instance.ClearForFile(_currentFilePath);
        MemoService.Instance.ClearForFile(_currentFilePath);
        DrawingService.Instance.ClearForFile(_currentFilePath);

        for (int pi = 0; pi < _editDocument.Pages.Count; pi++)
        {
            var page   = _editDocument.Pages[pi];
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null) continue;

            var toKeep = new List<PdfItem>();

            for (int ai = 0; ai < annots.Elements.Count; ai++)
            {
                var item = annots.Elements[ai];
                PdfDictionary? ad = item as PdfDictionary;
                if (ad is null && item is PdfSharp.Pdf.Advanced.PdfReference pRef)
                    ad = pRef.Value as PdfDictionary;

                if (ad is null) { toKeep.Add(item); continue; }

                string title = ad.Elements.GetString("/T");

                if (title == "HoloPDF Creator")
                {
                    string sub = (ad.Elements["/Subtype"] as PdfName)?.Value ?? "";
                    AnnotationType type = sub switch
                    {
                        "/Highlight" => AnnotationType.Highlight,
                        "/Underline" => AnnotationType.Underline,
                        "/StrikeOut" => AnnotationType.Strikethrough,
                        _            => AnnotationType.Highlight
                    };

                    var cArr  = ad.Elements.GetArray("/C");
                    var color = cArr != null && cArr.Elements.Count >= 3
                        ? Color.FromRgb(
                            (byte)(GetPdfReal(cArr.Elements[0]) * 255),
                            (byte)(GetPdfReal(cArr.Elements[1]) * 255),
                            (byte)(GetPdfReal(cArr.Elements[2]) * 255))
                        : Colors.Yellow;

                    var qpArr = ad.Elements.GetArray("/QuadPoints");
                    var spans = new List<AnnotationSpan>();
                    if (qpArr != null)
                    {
                        for (int k = 0; k + 7 < qpArr.Elements.Count; k += 8)
                            spans.Add(new AnnotationSpan
                            {
                                Left   = GetPdfReal(qpArr.Elements[k]),
                                Top    = GetPdfReal(qpArr.Elements[k + 1]),
                                Right  = GetPdfReal(qpArr.Elements[k + 2]),
                                Bottom = GetPdfReal(qpArr.Elements[k + 5]),
                            });
                    }

                    if (spans.Count > 0)
                        AnnotationService.Instance.Add(new TextAnnotation
                        {
                            Type       = type,
                            Color      = color,
                            FilePath   = _currentFilePath,
                            PageNumber = (uint)pi,
                            Spans      = spans,
                        });
                    // not added to toKeep → stripped from _editDocument
                }
                else if (title == "HoloPDF Memo")
                {
                    string content = ad.Elements.GetString("/Contents");
                    double mx = 0, my = 0;
                    var rect = ad.Elements.GetArray("/Rect");
                    if (rect != null && rect.Elements.Count >= 4)
                    {
                        mx = (GetPdfReal(rect.Elements[0]) + GetPdfReal(rect.Elements[2])) / 2;
                        my = (GetPdfReal(rect.Elements[1]) + GetPdfReal(rect.Elements[3])) / 2;
                    }
                    if (!string.IsNullOrWhiteSpace(content))
                        MemoService.Instance.Add(new PdfMemo
                        {
                            FilePath   = _currentFilePath,
                            PageNumber = (uint)pi,
                            X = mx, Y = my,
                            Content    = content,
                        });
                    // not added to toKeep → stripped from _editDocument
                }
                else if (title == "HoloPDF Drawing")
                {
                    string sub = (ad.Elements["/Subtype"] as PdfName)?.Value ?? "";
                    DrawingShapeType stype = sub switch
                    {
                        "/Square" => DrawingShapeType.Box,
                        "/Circle" => DrawingShapeType.Circle,
                        _         => DrawingShapeType.Line
                    };

                    var cArr   = ad.Elements.GetArray("/C");
                    var stroke = cArr != null && cArr.Elements.Count >= 3
                        ? Color.FromRgb(
                            (byte)(GetPdfReal(cArr.Elements[0]) * 255),
                            (byte)(GetPdfReal(cArr.Elements[1]) * 255),
                            (byte)(GetPdfReal(cArr.Elements[2]) * 255))
                        : Colors.Red;

                    var icArr  = ad.Elements.GetArray("/IC");
                    bool hasFill = icArr != null && icArr.Elements.Count >= 3;
                    var fill   = hasFill
                        ? Color.FromRgb(
                            (byte)(GetPdfReal(icArr!.Elements[0]) * 255),
                            (byte)(GetPdfReal(icArr.Elements[1]) * 255),
                            (byte)(GetPdfReal(icArr.Elements[2]) * 255))
                        : Colors.Transparent;

                    double sx1 = 0, sy1 = 0, sx2 = 0, sy2 = 0;
                    if (stype == DrawingShapeType.Line)
                    {
                        var lArr = ad.Elements.GetArray("/L");
                        if (lArr != null && lArr.Elements.Count >= 4)
                        {
                            sx1 = GetPdfReal(lArr.Elements[0]); sy1 = GetPdfReal(lArr.Elements[1]);
                            sx2 = GetPdfReal(lArr.Elements[2]); sy2 = GetPdfReal(lArr.Elements[3]);
                        }
                    }
                    else
                    {
                        var rArr = ad.Elements.GetArray("/Rect");
                        if (rArr != null && rArr.Elements.Count >= 4)
                        {
                            sx1 = GetPdfReal(rArr.Elements[0]); sy1 = GetPdfReal(rArr.Elements[1]);
                            sx2 = GetPdfReal(rArr.Elements[2]); sy2 = GetPdfReal(rArr.Elements[3]);
                        }
                    }

                    DrawingService.Instance.Add(new DrawingShape
                    {
                        ShapeType  = stype,
                        Stroke     = stroke,
                        Fill       = fill,
                        HasFill    = hasFill,
                        FilePath   = _currentFilePath,
                        PageNumber = (uint)pi,
                        X1 = sx1, Y1 = sy1, X2 = sx2, Y2 = sy2,
                    });
                    // not added to toKeep → stripped from _editDocument
                }
                else
                {
                    toKeep.Add(item);  // preserve third-party annotations
                }
            }

            // Rebuild /Annots containing only third-party items.
            var newAnnots = new PdfArray(_editDocument);
            foreach (var kept in toKeep)
                newAnnots.Elements.Add(kept);
            page.Elements["/Annots"] = newAnnots;
        }
    }

    private static double GetPdfReal(PdfItem item) => item switch
    {
        PdfReal    r => r.Value,
        PdfInteger i => (double)i.Value,
        _            => 0.0
    };

    private static PdfArray MakePdfArray(params double[] values)
    {
        var arr = new PdfArray();
        foreach (var v in values)
            arr.Elements.Add(new PdfReal(v));
        return arr;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Memo – panel toggle
    // ═══════════════════════════════════════════════════════════════════════════

    private void ToggleMemos_Click(object sender, RoutedEventArgs e)
    {
        bool open = MemoPanelCol.Width.Value < 1;
        MemoPanelCol.Width = open ? new GridLength(280) : new GridLength(0);
        BtnToggleMemos.Style = (Style)FindResource(open ? "PrimaryButton" : "SecondaryButton");
        if (open) RefreshMemoList(MemoSearchBox.Text);
    }

    private void MemoSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshMemoList(MemoSearchBox.Text);

    private void RefreshMemoList(string query)
    {
        MemoListPanel.Children.Clear();
        if (_currentFilePath is null) return;

        var memos = (string.IsNullOrWhiteSpace(query)
            ? MemoService.Instance.ForFile(_currentFilePath)
            : MemoService.Instance.Search(_currentFilePath, query))
            .OrderBy(m => m.PageNumber)
            .ThenBy(m => m.CreatedAt)
            .ToList();

        if (memos.Count == 0)
        {
            MemoListPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(query) ? "No memos yet." : "No results.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x58, 0x5B, 0x70)),
                FontSize = 13, Margin = new Thickness(12, 20, 12, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return;
        }

        foreach (var memo in memos)
        {
            var preview = memo.Content.Length > 80 ? memo.Content[..80] + "…" : memo.Content;
            var card = new Border
            {
                Padding         = new Thickness(10, 8, 10, 8),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Background      = Brushes.Transparent,
                Cursor          = Cursors.Hand,
                Tag             = memo,
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = $"📝  Page {memo.PageNumber + 1}  ·  {memo.CreatedAt:MM/dd HH:mm}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
                Margin = new Thickness(0, 0, 0, 4),
            });
            sp.Children.Add(new TextBlock
            {
                Text = preview, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            });
            card.Child = sp;
            card.MouseEnter  += (s, _) => ((Border)s).Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
            card.MouseLeave  += (s, _) => ((Border)s).Background = Brushes.Transparent;
            card.MouseLeftButtonUp += (s, _) => NavigateToMemo((PdfMemo)((Border)s).Tag);
            MemoListPanel.Children.Add(card);
        }
    }

    private async void NavigateToMemo(PdfMemo memo)
    {
        if (!memo.FilePath.Equals(_currentFilePath, StringComparison.OrdinalIgnoreCase))
            await LoadPdfAsync(memo.FilePath);

        _currentPage = Math.Min(memo.PageNumber, _totalPages - 1);
        UpdateNavButtons();
        await RenderCurrentPageAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Memo – draw icons on canvas
    // ═══════════════════════════════════════════════════════════════════════════

    private void DrawMemos()
    {
        MemoCanvas.Children.Clear();
        if (_currentFilePath is null || _currentPagePdfHeight <= 0) return;

        double sx = MemoCanvas.Width  / _currentPagePdfWidth;
        double sy = MemoCanvas.Height / _currentPagePdfHeight;

        foreach (var memo in MemoService.Instance.ForPage(_currentFilePath, _currentPage))
        {
            double cx = memo.X * sx;
            double cy = (_currentPagePdfHeight - memo.Y) * sy;

            var icon = new Border
            {
                Width           = 22,
                Height          = 22,
                Background      = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0xCC, 0xAA, 0x00)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3, 3, 3, 0),
                IsHitTestVisible = false,
                ToolTip         = memo.Content.Length > 60 ? memo.Content[..60] + "…" : memo.Content,
                Child           = new TextBlock
                {
                    Text = "✎", FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Foreground          = new SolidColorBrush(Color.FromRgb(0x33, 0x22, 0x00)),
                }
            };
            Canvas.SetLeft(icon, cx - 11);
            Canvas.SetTop(icon,  cy - 22);
            MemoCanvas.Children.Add(icon);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Memo – hit-test
    // ═══════════════════════════════════════════════════════════════════════════

    private PdfMemo? HitTestMemo(Point pt)
    {
        if (_currentFilePath is null || _currentPagePdfHeight <= 0) return null;

        double sx = MemoCanvas.Width  / _currentPagePdfWidth;
        double sy = MemoCanvas.Height / _currentPagePdfHeight;

        foreach (var memo in MemoService.Instance.ForPage(_currentFilePath, _currentPage))
        {
            double cx = memo.X * sx;
            double cy = (_currentPagePdfHeight - memo.Y) * sy;
            // icon occupies (cx-11, cy-22) to (cx+11, cy)
            if (pt.X >= cx - 11 && pt.X <= cx + 11 && pt.Y >= cy - 22 && pt.Y <= cy)
                return memo;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Memo – edit popup
    // ═══════════════════════════════════════════════════════════════════════════

    private void ShowMemoEditPopup(PdfMemo? existing)
    {
        _memoEditPopup?.IsOpen.Equals(false);
        _memoEditPopup = null;

        var tb = new TextBox
        {
            Width  = 260,
            Height = 110,
            AcceptsReturn = true,
            TextWrapping  = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text    = existing?.Content ?? "",
            Padding = new Thickness(6),
            Margin  = new Thickness(8, 8, 8, 6),
        };

        tb.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Escape) { _memoEditPopup!.IsOpen = false; }
        };

        var saveBtn = new Button
        {
            Content  = existing is null ? "Add" : "Save",
            Style    = (Style)FindResource("PrimaryButton"),
            MinWidth = 56,
            Margin   = new Thickness(4, 0, 8, 0),
        };

        var cancelBtn = new Button
        {
            Content  = "Cancel",
            Style    = (Style)FindResource("SecondaryButton"),
            MinWidth = 56,
            Margin   = new Thickness(0, 0, 4, 0),
        };

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 8, 8),
        };

        if (existing is not null)
        {
            var delBtn = new Button
            {
                Content         = "Delete",
                Foreground      = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                Padding         = new Thickness(4, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            delBtn.Click += (_, _) =>
            {
                MemoService.Instance.Remove(existing.Id);
                _memoEditPopup!.IsOpen = false;
                DrawMemos();
                RefreshMemoList(MemoSearchBox.Text);
            };
            var spacer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Stretch };
            var outerRow = new Grid { Margin = new Thickness(8, 0, 8, 8) };
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(delBtn,    0);
            Grid.SetColumn(cancelBtn, 1);
            Grid.SetColumn(saveBtn,   2);
            outerRow.Children.Add(delBtn);
            outerRow.Children.Add(cancelBtn);
            outerRow.Children.Add(saveBtn);

            var panel = new StackPanel();
            panel.Children.Add(tb);
            panel.Children.Add(outerRow);

            var popup2 = BuildMemoPopup(panel);
            WireMemoBtns(popup2, tb, existing, saveBtn, cancelBtn);
            return;
        }

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(saveBtn);

        var content = new StackPanel();
        content.Children.Add(tb);
        content.Children.Add(btnRow);

        var popup = BuildMemoPopup(content);
        WireMemoBtns(popup, tb, null, saveBtn, cancelBtn);
    }

    private Popup BuildMemoPopup(UIElement content)
    {
        var outer = new Border
        {
            Child           = content,
            Background      = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Effect          = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Opacity = 0.7
            }
        };

        var popup = new Popup
        {
            PlacementTarget    = TextOverlayCanvas,
            Placement          = PlacementMode.Mouse,
            StaysOpen          = true,   // StaysOpen=true for Korean IME compatibility
            AllowsTransparency = true,
            Child              = outer,
            IsOpen             = true,
        };
        popup.Closed += (_, _) => { if (ReferenceEquals(_memoEditPopup, popup)) _memoEditPopup = null; };
        _memoEditPopup = popup;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (outer.Child is StackPanel sp)
            {
                var firstTb = sp.Children.OfType<TextBox>().FirstOrDefault();
                firstTb?.Focus();
                if (firstTb is not null) firstTb.CaretIndex = firstTb.Text.Length;
            }
        });

        return popup;
    }

    private void WireMemoBtns(Popup popup, TextBox tb, PdfMemo? existing,
                               Button saveBtn, Button cancelBtn)
    {
        saveBtn.Click += (_, _) =>
        {
            var text = tb.Text.Trim();
            if (text.Length == 0) return;

            if (existing is not null)
            {
                existing.Content = text;
            }
            else
            {
                if (_currentFilePath is null) return;
                MemoService.Instance.Add(new PdfMemo
                {
                    FilePath   = _currentFilePath,
                    PageNumber = _currentPage,
                    X          = _memoInsertPdfX,
                    Y          = _memoInsertPdfY,
                    Content    = text,
                });
            }
            popup.IsOpen = false;
            DrawMemos();
            RefreshMemoList(MemoSearchBox.Text);
        };

        cancelBtn.Click += (_, _) => popup.IsOpen = false;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Drawing Tools
    // ═══════════════════════════════════════════════════════════════════════════

    private enum DrawMode { None, Line, Box, Circle }

    private DrawMode         _drawMode        = DrawMode.None;
    private bool             _isDrawing;
    private Point            _drawStart;
    private Shape?           _rubberBand;
    private DrawingShape?    _selectedShape;
    private Popup?           _shapeColorPopup;

    // Last-used colors — new shapes inherit them for convenience.
    private Color  _lastStroke  = Colors.Red;
    private Color  _lastFill    = Colors.Transparent;
    private bool   _lastHasFill = false;

    private static readonly (Color Color, string Name)[] DrawColorPresets =
    {
        (Color.FromRgb(0xFF, 0x00, 0x00), "Red"),
        (Color.FromRgb(0x00, 0x66, 0xFF), "Blue"),
        (Color.FromRgb(0x00, 0xBB, 0x00), "Green"),
        (Color.FromRgb(0xFF, 0xAA, 0x00), "Orange"),
        (Color.FromRgb(0x88, 0x00, 0xFF), "Purple"),
        (Color.FromRgb(0x00, 0x00, 0x00), "Black"),
        (Color.FromRgb(0xFF, 0xFF, 0xFF), "White"),
    };

    private void DrawTool_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        var tag = btn.Tag as string;
        var newMode = tag switch
        {
            "Line"   => DrawMode.Line,
            "Box"    => DrawMode.Box,
            "Circle" => DrawMode.Circle,
            _        => DrawMode.None
        };

        _drawMode = _drawMode == newMode ? DrawMode.None : newMode;
        UpdateDrawToolButtons();
        ApplyDrawMode();

        _selectedShape = null;
        CloseShapeColorPopup();
        RefreshDrawingCanvas();
    }

    private void ApplyDrawMode()
    {
        // Always remove first to prevent duplicate registrations when switching modes.
        DrawingCanvas.MouseLeftButtonDown -= DrawingCanvas_MouseDown;
        DrawingCanvas.MouseMove           -= DrawingCanvas_MouseMove;
        DrawingCanvas.MouseLeftButtonUp   -= DrawingCanvas_MouseUp;

        bool drawing = _drawMode != DrawMode.None;
        DrawingCanvas.IsHitTestVisible     = drawing;
        TextOverlayCanvas.IsHitTestVisible = !drawing;
        DrawingCanvas.Cursor               = drawing ? Cursors.Cross : Cursors.Arrow;

        if (drawing)
        {
            DrawingCanvas.MouseLeftButtonDown += DrawingCanvas_MouseDown;
            DrawingCanvas.MouseMove           += DrawingCanvas_MouseMove;
            DrawingCanvas.MouseLeftButtonUp   += DrawingCanvas_MouseUp;
        }
    }

    private void UpdateDrawToolButtons()
    {
        BtnDrawLine.Style   = (Style)FindResource(_drawMode == DrawMode.Line   ? "PrimaryButton" : "SecondaryButton");
        BtnDrawBox.Style    = (Style)FindResource(_drawMode == DrawMode.Box    ? "PrimaryButton" : "SecondaryButton");
        BtnDrawCircle.Style = (Style)FindResource(_drawMode == DrawMode.Circle ? "PrimaryButton" : "SecondaryButton");
    }

    // ─── Rubber-band drawing ──────────────────────────────────────────────────

    private void DrawingCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _drawStart = e.GetPosition(DrawingCanvas);
        _isDrawing = true;
        DrawingCanvas.CaptureMouse();

        if (_drawMode == DrawMode.Line)
        {
            // Line uses canvas-absolute X1/Y1/X2/Y2 — do NOT set Canvas.Left/Top.
            var ln = new System.Windows.Shapes.Line
            {
                X1 = _drawStart.X, Y1 = _drawStart.Y,
                X2 = _drawStart.X, Y2 = _drawStart.Y,
                Stroke          = new SolidColorBrush(Color.FromArgb(200, 0x89, 0xB4, 0xFA)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 6, 3 },
                IsHitTestVisible = false,
            };
            _rubberBand = ln;
        }
        else
        {
            _rubberBand = CreateShapeVisual(_drawMode, Color.FromArgb(200, 0x89, 0xB4, 0xFA),
                                            Colors.Transparent, false);
            if (_rubberBand is null) return;
            Canvas.SetLeft(_rubberBand, _drawStart.X);
            Canvas.SetTop(_rubberBand,  _drawStart.Y);
            _rubberBand.Width  = 0;
            _rubberBand.Height = 0;
        }

        DrawingCanvas.Children.Add(_rubberBand);
        e.Handled = true;
    }

    private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDrawing || _rubberBand is null) return;

        var pos = e.GetPosition(DrawingCanvas);
        UpdateRubberBand(ApplyShiftConstraint(_drawStart, pos));
        e.Handled = true;
    }

    private void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawing) return;
        _isDrawing = false;
        DrawingCanvas.ReleaseMouseCapture();

        var rawPos = e.GetPosition(DrawingCanvas);
        if (_rubberBand != null)
            DrawingCanvas.Children.Remove(_rubberBand);
        _rubberBand = null;

        double rawDx = Math.Abs(rawPos.X - _drawStart.X);
        double rawDy = Math.Abs(rawPos.Y - _drawStart.Y);

        // Tiny click (no drag) → try to select an existing shape instead of drawing.
        if (rawDx < 4 && rawDy < 4)
        {
            var hit = HitTestDrawingShape(_drawStart);
            if (hit != null)
            {
                _selectedShape = hit;
                RefreshDrawingCanvas();
                ShowShapeColorPicker(hit);
            }
            e.Handled = true;
            return;
        }

        var pos = ApplyShiftConstraint(_drawStart, rawPos);

        if (_currentFilePath is null || _currentPagePdfHeight <= 0) { e.Handled = true; return; }

        double toX = _currentPagePdfWidth  / DrawingCanvas.Width;
        double toY = _currentPagePdfHeight / DrawingCanvas.Height;

        double px1 = _drawStart.X * toX;
        double py1 = _currentPagePdfHeight - _drawStart.Y * toY;
        double px2 = pos.X * toX;
        double py2 = _currentPagePdfHeight - pos.Y * toY;

        var shape = new DrawingShape
        {
            ShapeType  = _drawMode switch { DrawMode.Line => DrawingShapeType.Line, DrawMode.Box => DrawingShapeType.Box, _ => DrawingShapeType.Circle },
            Stroke     = _lastStroke,
            Fill       = _lastFill,
            HasFill    = _lastHasFill,
            FilePath   = _currentFilePath,
            PageNumber = _currentPage,
            X1 = px1, Y1 = py1, X2 = px2, Y2 = py2,
        };

        DrawingService.Instance.Add(shape);
        _selectedShape = shape;
        RefreshDrawingCanvas();
        ShowShapeColorPicker(shape);
        e.Handled = true;
    }

    private void UpdateRubberBand(Point pos)
    {
        if (_rubberBand is null) return;

        if (_rubberBand is System.Windows.Shapes.Line ln)
        {
            ln.X1 = _drawStart.X; ln.Y1 = _drawStart.Y;
            ln.X2 = pos.X;        ln.Y2 = pos.Y;
        }
        else
        {
            double x = Math.Min(_drawStart.X, pos.X);
            double y = Math.Min(_drawStart.Y, pos.Y);
            Canvas.SetLeft(_rubberBand, x);
            Canvas.SetTop(_rubberBand,  y);
            _rubberBand.Width  = Math.Max(Math.Abs(pos.X - _drawStart.X), 1);
            _rubberBand.Height = Math.Max(Math.Abs(pos.Y - _drawStart.Y), 1);
        }
    }

    private Point ApplyShiftConstraint(Point start, Point current)
    {
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        if (!shift) return current;

        double dx = current.X - start.X;
        double dy = current.Y - start.Y;

        if (_drawMode == DrawMode.Line)
        {
            // Snap to the dominant axis (horizontal or vertical).
            return Math.Abs(dx) >= Math.Abs(dy)
                ? new Point(current.X, start.Y)   // horizontal
                : new Point(start.X,  current.Y);  // vertical
        }
        else
        {
            // Force equal width and height so Box becomes square, Circle becomes circle.
            double size = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new Point(start.X + Math.Sign(dx) * size,
                             start.Y + Math.Sign(dy) * size);
        }
    }

    private static Shape? CreateShapeVisual(DrawMode mode, Color stroke, Color fill, bool hasFill)
    {
        var strokeBrush = new SolidColorBrush(stroke);
        var fillBrush   = hasFill ? (Brush)new SolidColorBrush(fill) : Brushes.Transparent;
        return mode switch
        {
            DrawMode.Line => new System.Windows.Shapes.Line
            {
                Stroke           = strokeBrush,
                StrokeThickness  = 2,
                IsHitTestVisible = false,
            },
            DrawMode.Box => new Rectangle
            {
                Stroke           = strokeBrush,
                StrokeThickness  = 2,
                Fill             = fillBrush,
                IsHitTestVisible = false,
            },
            DrawMode.Circle => new Ellipse
            {
                Stroke           = strokeBrush,
                StrokeThickness  = 2,
                Fill             = fillBrush,
                IsHitTestVisible = false,
            },
            _ => null
        };
    }

    // ─── Draw all shapes onto DrawingCanvas ───────────────────────────────────

    private void RefreshDrawingCanvas()
    {
        DrawingCanvas.Children.Clear();
        if (_currentFilePath is null || _currentPagePdfHeight <= 0) return;

        double sx = DrawingCanvas.Width  / _currentPagePdfWidth;
        double sy = DrawingCanvas.Height / _currentPagePdfHeight;

        foreach (var shape in DrawingService.Instance.ForPage(_currentFilePath, _currentPage))
        {
            bool sel = _selectedShape?.Id == shape.Id;

            double cx1 = shape.X1 * sx;
            double cy1 = (_currentPagePdfHeight - shape.Y1) * sy;
            double cx2 = shape.X2 * sx;
            double cy2 = (_currentPagePdfHeight - shape.Y2) * sy;

            var mode = shape.ShapeType switch
            {
                DrawingShapeType.Line   => DrawMode.Line,
                DrawingShapeType.Box    => DrawMode.Box,
                DrawingShapeType.Circle => DrawMode.Circle,
                _                       => DrawMode.Box
            };

            var vis = CreateShapeVisual(mode, shape.Stroke, shape.Fill, shape.HasFill);
            if (vis is null) continue;
            vis.IsHitTestVisible = false;

            if (shape.ShapeType == DrawingShapeType.Line)
            {
                if (vis is System.Windows.Shapes.Line ln)
                {
                    ln.X1 = cx1; ln.Y1 = cy1;
                    ln.X2 = cx2; ln.Y2 = cy2;
                }
                DrawingCanvas.Children.Add(vis);
            }
            else
            {
                double x = Math.Min(cx1, cx2);
                double y = Math.Min(cy1, cy2);
                Canvas.SetLeft(vis, x);
                Canvas.SetTop(vis,  y);
                vis.Width  = Math.Max(Math.Abs(cx2 - cx1), 1);
                vis.Height = Math.Max(Math.Abs(cy2 - cy1), 1);
                DrawingCanvas.Children.Add(vis);
            }

            if (sel) DrawSelectionHandles(cx1, cy1, cx2, cy2, shape.ShapeType);
        }
    }

    // Small square handles at corners/endpoints to indicate selection.
    private void DrawSelectionHandles(double cx1, double cy1, double cx2, double cy2,
                                      DrawingShapeType type)
    {
        const double S = 8;  // handle side length

        IEnumerable<(double x, double y)> pts = type == DrawingShapeType.Line
            ? [(cx1, cy1), (cx2, cy2)]
            : [(Math.Min(cx1, cx2), Math.Min(cy1, cy2)),
               (Math.Max(cx1, cx2), Math.Min(cy1, cy2)),
               (Math.Min(cx1, cx2), Math.Max(cy1, cy2)),
               (Math.Max(cx1, cx2), Math.Max(cy1, cy2))];

        foreach (var (x, y) in pts)
        {
            var h = new Rectangle
            {
                Width            = S,
                Height           = S,
                Fill             = Brushes.White,
                Stroke           = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
                StrokeThickness  = 1.5,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(h, x - S / 2);
            Canvas.SetTop(h,  y - S / 2);
            DrawingCanvas.Children.Add(h);
        }
    }

    // ─── Hit-test ─────────────────────────────────────────────────────────────

    private DrawingShape? HitTestDrawingShape(Point pt)
    {
        if (_currentFilePath is null || _currentPagePdfHeight <= 0) return null;

        double sx = DrawingCanvas.Width  / _currentPagePdfWidth;
        double sy = DrawingCanvas.Height / _currentPagePdfHeight;

        foreach (var shape in DrawingService.Instance.ForPage(_currentFilePath, _currentPage))
        {
            double cx1 = shape.X1 * sx;
            double cy1 = (_currentPagePdfHeight - shape.Y1) * sy;
            double cx2 = shape.X2 * sx;
            double cy2 = (_currentPagePdfHeight - shape.Y2) * sy;

            double minX = Math.Min(cx1, cx2);
            double maxX = Math.Max(cx1, cx2);
            double minY = Math.Min(cy1, cy2);
            double maxY = Math.Max(cy1, cy2);
            const double Tol = 6;

            if (shape.ShapeType == DrawingShapeType.Line)
            {
                // Distance from point to line segment
                double dist = PointToSegmentDist(pt, new Point(cx1, cy1), new Point(cx2, cy2));
                if (dist <= Tol) return shape;
            }
            else
            {
                // Hit border of box/ellipse
                bool inX = pt.X >= minX - Tol && pt.X <= maxX + Tol;
                bool inY = pt.Y >= minY - Tol && pt.Y <= maxY + Tol;
                bool onBorderX = pt.X <= minX + Tol || pt.X >= maxX - Tol;
                bool onBorderY = pt.Y <= minY + Tol || pt.Y >= maxY - Tol;
                if (inX && inY && (onBorderX || onBorderY)) return shape;
                if (!shape.HasFill && inX && inY) { /* inside unfilled — need border hit */ }
                if (shape.HasFill && inX && inY) return shape;
            }
        }
        return null;
    }

    private static double PointToSegmentDist(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq == 0) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
        double nx = a.X + t * dx - p.X;
        double ny = a.Y + t * dy - p.Y;
        return Math.Sqrt(nx * nx + ny * ny);
    }

    // ─── Shape color picker popup ─────────────────────────────────────────────

    private void ShowShapeColorPicker(DrawingShape shape)
    {
        CloseShapeColorPopup();

        bool isLine = shape.ShapeType == DrawingShapeType.Line;

        var panel = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };

        // Stroke row
        panel.Children.Add(new TextBlock
        {
            Text = "Stroke", FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8)),
            Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(BuildColorSwatchRow(DrawColorPresets, shape.Stroke, c =>
        {
            shape.Stroke = c;
            _lastStroke  = c;
            CloseShapeColorPopup();
            RefreshDrawingCanvas();
        }));

        // Fill row (Box and Circle only)
        if (!isLine)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Fill", FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8)),
                Margin = new Thickness(0, 8, 0, 4)
            });
            panel.Children.Add(BuildFillSwatchRow(shape, () =>
            {
                _lastFill    = shape.Fill;
                _lastHasFill = shape.HasFill;
                CloseShapeColorPopup();
                RefreshDrawingCanvas();
            }));
        }

        // Delete button
        var sep = new Border
        {
            Height = 1, Margin = new Thickness(0, 8, 0, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A))
        };
        panel.Children.Add(sep);

        var del = new Button
        {
            Content         = "Delete Shape",
            Style           = (Style)FindResource("SecondaryButton"),
            Foreground      = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        del.Click += (_, _) =>
        {
            DrawingService.Instance.Remove(shape.Id);
            _selectedShape = null;
            CloseShapeColorPopup();
            RefreshDrawingCanvas();
        };
        panel.Children.Add(del);

        var outer = new Border
        {
            Child           = panel,
            Width           = 220,
            Background      = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(8),
            Effect          = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 12, ShadowDepth = 2, Opacity = 0.6
            }
        };

        var popup = new Popup
        {
            PlacementTarget    = PdfPageBorder,
            Placement          = PlacementMode.Mouse,
            StaysOpen          = false,
            AllowsTransparency = true,
            Child              = outer
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_shapeColorPopup, popup))
            {
                _shapeColorPopup = null;
                _selectedShape   = null;
                RefreshDrawingCanvas();
            }
        };
        _shapeColorPopup = popup;
        popup.IsOpen = true;
    }

    private static StackPanel BuildColorSwatchRow(
        (Color Color, string Name)[] presets, Color current, Action<Color> onPick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (c, name) in presets)
        {
            bool isCur = current == c;
            var swatch = new Border
            {
                Width  = 22, Height = 22, Margin = new Thickness(2),
                Background      = new SolidColorBrush(c),
                BorderBrush     = new SolidColorBrush(isCur ? Colors.White
                                      : Color.FromRgb(0x45, 0x47, 0x5A)),
                BorderThickness = new Thickness(isCur ? 2 : 1),
                CornerRadius    = new CornerRadius(11),
                Cursor          = Cursors.Hand,
                ToolTip         = name
            };
            swatch.MouseLeftButtonUp += (_, _) => onPick(c);
            row.Children.Add(swatch);
        }
        return row;
    }

    private static StackPanel BuildFillSwatchRow(DrawingShape shape, Action onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        // Transparent swatch
        bool transSelected = !shape.HasFill;
        var transSwatch = new Border
        {
            Width  = 22, Height = 22, Margin = new Thickness(2),
            Background      = Brushes.Transparent,
            BorderBrush     = new SolidColorBrush(transSelected ? Colors.White
                                  : Color.FromRgb(0x45, 0x47, 0x5A)),
            BorderThickness = new Thickness(transSelected ? 2 : 1),
            CornerRadius    = new CornerRadius(11),
            Cursor          = Cursors.Hand,
            ToolTip         = "Transparent",
            Child           = new TextBlock
            {
                Text = "∅", FontSize = 12,
                Foreground          = new SolidColorBrush(Color.FromRgb(0x58, 0x5B, 0x70)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            }
        };
        transSwatch.MouseLeftButtonUp += (_, _) =>
        {
            shape.HasFill = false;
            onChange();
        };
        row.Children.Add(transSwatch);

        // Color swatches
        foreach (var (c, name) in DrawColorPresets)
        {
            bool isCur = shape.HasFill && shape.Fill == c;
            var swatch = new Border
            {
                Width  = 22, Height = 22, Margin = new Thickness(2),
                Background      = new SolidColorBrush(c),
                BorderBrush     = new SolidColorBrush(isCur ? Colors.White
                                      : Color.FromRgb(0x45, 0x47, 0x5A)),
                BorderThickness = new Thickness(isCur ? 2 : 1),
                CornerRadius    = new CornerRadius(11),
                Cursor          = Cursors.Hand,
                ToolTip         = name
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                shape.Fill    = c;
                shape.HasFill = true;
                onChange();
            };
            row.Children.Add(swatch);
        }
        return row;
    }

    private void CloseShapeColorPopup()
    {
        var p = _shapeColorPopup;
        _shapeColorPopup = null;
        if (p is { IsOpen: true }) p.IsOpen = false;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Page Rotation
    // ═══════════════════════════════════════════════════════════════════════════

    private async void RotatePage_Click(object sender, RoutedEventArgs e)
    {
        if (_totalPages == 0) return;

        var dlg = new RotateDialog((int)_totalPages, (int)_currentPage) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (dlg.Angle == 0) { SetStatus("회전 각도가 0°입니다. 적용하지 않습니다."); return; }

        IEnumerable<int> targets = dlg.Scope switch
        {
            RotateScope.All   => Enumerable.Range(0, (int)_totalPages),
            RotateScope.Range => Enumerable.Range(dlg.PageFrom - 1, dlg.PageTo - dlg.PageFrom + 1)
                                            .Where(i => i >= 0 && i < (int)_totalPages),
            RotateScope.Even  => Enumerable.Range(0, (int)_totalPages).Where(i => i % 2 == 1),
            RotateScope.Odd   => Enumerable.Range(0, (int)_totalPages).Where(i => i % 2 == 0),
            _                 => new[] { (int)_currentPage },
        };

        if (_editDocument is null) { SetStatus("Open a PDF first."); return; }

        foreach (int pi in targets)
        {
            var page = _editDocument.Pages[pi];
            page.Rotate = ((page.Rotate + dlg.Angle) % 360 + 360) % 360;
        }

        SetStatus("회전 적용 중…");
        await ReloadRenderDocumentAsync();
        SetStatus($"회전 완료 ({dlg.Angle}°).");
    }

    private async Task ReloadRenderDocumentAsync()
    {
        if (_editDocument is null) return;

        _pageTransitioning = true;   // suppress scroll events during reload
        string tmpPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            System.IO.Path.GetRandomFileName() + ".pdf");
        try
        {
            _editDocument.Save(tmpPath);
            _editDocument.Dispose();
            _editPdfBytes = System.IO.File.ReadAllBytes(tmpPath);  // snapshot for PdfPig
            _editDocument = PdfReader.Open(tmpPath, PdfDocumentOpenMode.Modify);

            var tmpFile = await StorageFile.GetFileFromPathAsync(tmpPath);
            _renderDocument = await WinPdf.PdfDocument.LoadFromFileAsync(tmpFile);
            try { System.IO.File.Delete(tmpPath); } catch { }

            _totalPages = _renderDocument.PageCount;
            UpdateNavButtons();
            await RenderCurrentPageAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"재로드 오류: {ex.Message}");
            try { System.IO.File.Delete(tmpPath); } catch { }
        }
        finally
        {
            _pageTransitioning = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Thumbnail panel
    // ═══════════════════════════════════════════════════════════════════════════

    private void StartThumbnailGeneration()
    {
        _thumbCts.Cancel();
        _thumbCts = new CancellationTokenSource();
        _thumbItems.Clear();
        ThumbPanel.Children.Clear();

        if (_renderDocument == null) return;
        if (_totalPages == 0) return;

        // Add placeholder containers immediately so the panel shows all pages at once.
        for (uint i = 0; i < _totalPages; i++)
        {
            uint capturedPage = i;

            var imgEl = new System.Windows.Controls.Image
            {
                Width   = 118,
                Height  = 90,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(imgEl, BitmapScalingMode.HighQuality);

            var numBlock = new TextBlock
            {
                Text              = $"{i + 1}",
                FontSize          = 10,
                Foreground        = new SolidColorBrush(Color.FromRgb(0x9A, 0x9D, 0xB2)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin            = new Thickness(0, 2, 0, 0)
            };

            var stack = new StackPanel { Margin = new Thickness(0) };
            stack.Children.Add(imgEl);
            stack.Children.Add(numBlock);

            bool isCurrent = (capturedPage == _currentPage);
            var container = new Border
            {
                Background   = isCurrent ? ThumbBrushSelected : ThumbBrushNormal,
                CornerRadius = new CornerRadius(4),
                Padding      = new Thickness(4),
                Margin       = new Thickness(0, 0, 0, 4),
                Cursor       = Cursors.Hand,
                Child        = stack
            };

            container.MouseEnter += (_, _) =>
            {
                if (capturedPage != _currentPage) container.Background = ThumbBrushHover;
            };
            container.MouseLeave += (_, _) =>
            {
                container.Background = capturedPage == _currentPage ? ThumbBrushSelected : ThumbBrushNormal;
            };
            container.MouseLeftButtonUp += async (_, _) =>
            {
                if (capturedPage == _currentPage) return;
                _currentPage = capturedPage;
                UpdateNavButtons();
                await RenderCurrentPageAsync();
            };

            _thumbItems.Add(new ThumbEntry(imgEl, container));
            ThumbPanel.Children.Add(container);
        }

        // Highlight initial page and render thumbnails in background.
        UpdateThumbnailHighlight();
        _ = RenderThumbnailsAsync(_thumbCts.Token);
    }

    private async Task RenderThumbnailsAsync(CancellationToken ct)
    {
        for (int i = 0; i < (int)_totalPages; i++)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                BitmapSource thumbSrc;

                if (_renderDocument == null) return;
                using var pdfPage = _renderDocument.GetPage((uint)i);
                using var stream  = new InMemoryRandomAccessStream();
                await pdfPage.RenderToStreamAsync(stream,
                    new WinPdf.PdfPageRenderOptions { DestinationWidth = 160 });
                stream.Seek(0);
                var ms = new MemoryStream();
                await stream.AsStream().CopyToAsync(ms);
                ms.Position = 0;
                var pdfThumb = new BitmapImage();
                pdfThumb.BeginInit();
                pdfThumb.StreamSource = ms;
                pdfThumb.CacheOption  = BitmapCacheOption.OnLoad;
                pdfThumb.EndInit();
                pdfThumb.Freeze();
                thumbSrc = pdfThumb;

                if (!ct.IsCancellationRequested && i < _thumbItems.Count)
                    _thumbItems[i].Img.Source = thumbSrc;
            }
            catch { /* skip on render error */ }

            // Yield between pages to keep UI responsive.
            await Task.Yield();
        }
    }

    private void UpdateThumbnailHighlight()
    {
        for (int i = 0; i < _thumbItems.Count; i++)
            _thumbItems[i].Container.Background =
                i == (int)_currentPage ? ThumbBrushSelected : ThumbBrushNormal;

        if (_currentPage < _thumbItems.Count)
            _thumbItems[(int)_currentPage].Container.BringIntoView();
    }
}
