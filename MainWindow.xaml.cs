using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HoloPDFCreator.Models;
using HoloPDFCreator.Pages;
using HoloPDFCreator.Services;

namespace HoloPDFCreator;

public partial class MainWindow : Window
{
    private readonly AdjustedImageStore _adjustedStore     = new();
    private readonly PDFReaderPage     _pdfReaderPage     = new();
    private readonly ImageAdjusterPage _imageAdjusterPage = new();

    // ─── Bookmark colors ──────────────────────────────────────────────────────
    private static readonly SolidColorBrush BmNormalBg   = new(Color.FromRgb(0x1E, 0x1E, 0x2E));
    private static readonly SolidColorBrush BmHoverBg    = new(Color.FromRgb(0x2A, 0x2A, 0x3E));
    private static readonly SolidColorBrush BmSelectedBg = new(Color.FromRgb(0x31, 0x32, 0x44));
    private static readonly SolidColorBrush BmDropIntoBg = new(Color.FromArgb(0xFF, 0x1D, 0x3A, 0x5F));

    // ─── Selection ────────────────────────────────────────────────────────────
    private int _selectedBookmarkId = -1;
    private readonly Dictionary<int, Border>              _bookmarkContainers = new();
    private readonly Dictionary<Border, (Bookmark Bm, int Depth)> _containerInfo = new();

    // ─── Drag state ───────────────────────────────────────────────────────────
    private Bookmark? _dragBm;
    private Border?   _dragContainer;
    private Point     _dragStartPt;
    private bool      _isDraggingBm;

    // ─── Drop state ───────────────────────────────────────────────────────────
    private enum DropPosition { Before, Into, After }
    private Bookmark?    _dropTargetBm;
    private DropPosition _dropPos;
    private int?         _dropNewParentId;
    private int          _dropNewSortOrder;
    private Rectangle?   _dragIndicator;
    private Border?      _dropHighlightBorder;

    public MainWindow()
    {
        InitializeComponent();
        _pdfReaderPage.AdjustedStore     = _adjustedStore;
        _imageAdjusterPage.AdjustedStore = _adjustedStore;
        ContentFrame.Navigate(_pdfReaderPage);
        BookmarkService.Instance.Changed += (_, _) => RefreshBookmarkPanel();
        RefreshBookmarkPanel();
    }

    // ─── Page navigation ─────────────────────────────────────────────────────

    private void SwitchToPdfReader()
    {
        ContentFrame.Navigate(_pdfReaderPage);
        BtnNavPdfReader.Style     = (Style)FindResource("NavButtonActive");
        BtnNavImageAdjuster.Style = (Style)FindResource("NavButton");
        _ = _pdfReaderPage.RefreshWithAdjustedImagesAsync();
    }

    private void BtnNavPdfReader_Click(object sender, RoutedEventArgs e) => SwitchToPdfReader();

    private async void BtnNavImageAdjuster_Click(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigate(_imageAdjusterPage);
        BtnNavImageAdjuster.Style = (Style)FindResource("NavButtonActive");
        BtnNavPdfReader.Style     = (Style)FindResource("NavButton");

        if (_pdfReaderPage.CurrentFilePath is string pdfPath)
            await _imageAdjusterPage.LoadFromPdfAsync(pdfPath, _pdfReaderPage.CurrentPage);
    }

    // ─── Bookmark panel ───────────────────────────────────────────────────────

    private void BtnClearBookmarks_Click(object sender, RoutedEventArgs e)
    {
        // Remove root nodes (descendants removed automatically)
        var rootIds = BookmarkService.Instance.GetRoots().Select(b => b.Id).ToList();
        foreach (var id in rootIds) BookmarkService.Instance.Remove(id);
    }

    private void RefreshBookmarkPanel()
    {
        if (_isDraggingBm)
        {
            _dragContainer?.ReleaseMouseCapture();
            _dragBm = null; _dragContainer = null; _isDraggingBm = false;
        }
        ClearDropVisuals();

        _containerInfo.Clear();
        _bookmarkContainers.Clear();
        BookmarkListPanel.Children.Clear();

        if (!BookmarkService.Instance.GetRoots().Any())
        {
            _selectedBookmarkId = -1;
            BookmarkListPanel.Children.Add(new TextBlock
            {
                Text         = "No bookmarks yet.\nUse Ctrl+B or right-click to add one.",
                Foreground   = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
                FontSize     = 11,
                Margin       = new Thickness(8, 8, 8, 0),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        RenderSubtree(null, 0);

        if (_bookmarkContainers.TryGetValue(_selectedBookmarkId, out var sel))
            sel.Background = BmSelectedBg;
    }

    private void RenderSubtree(int? parentId, int depth)
    {
        foreach (var bm in BookmarkService.Instance.GetChildren(parentId))
        {
            BookmarkListPanel.Children.Add(CreateTreeItem(bm, depth));
            if (bm.IsExpanded)
                RenderSubtree(bm.Id, depth + 1);
        }
    }

    // ─── Tree item factory ────────────────────────────────────────────────────

    private UIElement CreateTreeItem(Bookmark bm, int depth)
    {
        bool hasKids = BookmarkService.Instance.HasChildren(bm.Id);

        // ── Expand / collapse ──
        var expandBtn = new Button
        {
            Width             = 16, Height = 16,
            Content           = hasKids ? (bm.IsExpanded ? "▼" : "▶") : "",
            FontSize          = 8,
            Padding           = new Thickness(0),
            Background        = Brushes.Transparent,
            BorderBrush       = Brushes.Transparent,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled         = hasKids,
            Cursor            = hasKids ? Cursors.Hand : Cursors.Arrow
        };
        if (hasKids) expandBtn.Click += (_, _) => BookmarkService.Instance.ToggleExpand(bm.Id);

        // ── Drag handle ──
        var dragHandle = new TextBlock
        {
            Text              = "⠿",
            FontSize          = 14,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(2, 0, 4, 0),
            Cursor            = Cursors.SizeNS,
            ToolTip           = "Drag to reorder"
        };

        // ── Page badge ──
        var badge = new Border
        {
            Background        = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            CornerRadius      = new CornerRadius(4),
            Padding           = new Thickness(5, 2, 5, 2),
            Margin            = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock
        {
            Text       = $"P{bm.PageNumber + 1}",
            FontSize   = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA))
        };

        // ── Title TextBlock ──
        var titleBlock = new TextBlock
        {
            Text              = bm.Title,
            FontSize          = 12,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor            = Cursors.Hand,
            ToolTip           = bm.Title
        };

        // ── Title TextBox (inline edit) ──
        var titleBox = new TextBox
        {
            Text              = bm.Title,
            FontSize          = 12,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            Background        = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            BorderBrush       = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
            BorderThickness   = new Thickness(1),
            Padding           = new Thickness(3, 1, 3, 1),
            Visibility        = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleArea = new Grid();
        titleArea.Children.Add(titleBlock);
        titleArea.Children.Add(titleBox);

        void StartEdit()
        {
            titleBox.Text         = bm.Title;
            titleBlock.Visibility = Visibility.Collapsed;
            titleBox.Visibility   = Visibility.Visible;
            titleBox.Focus(); titleBox.SelectAll();
        }
        void CommitEdit()
        {
            var t = titleBox.Text.Trim();
            if (!string.IsNullOrEmpty(t)) bm.Title = t;
            titleBlock.Text       = bm.Title;
            titleBlock.ToolTip    = bm.Title;
            titleBox.Visibility   = Visibility.Collapsed;
            titleBlock.Visibility = Visibility.Visible;
        }

        titleBox.LostFocus += (_, _) => CommitEdit();
        titleBox.KeyDown   += (_, e) =>
        {
            if      (e.Key == Key.Enter)  { CommitEdit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { titleBox.Text = bm.Title; CommitEdit(); e.Handled = true; }
        };
        titleBlock.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { StartEdit(); e.Handled = true; }
        };

        // ── Add child button ──
        var addChildBtn = new Button
        {
            Content           = "+",
            FontSize          = 12,
            Width = 18, Height = 18,
            Padding           = new Thickness(0),
            Background        = Brushes.Transparent,
            BorderBrush       = Brushes.Transparent,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            Cursor            = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 1, 0),
            ToolTip           = "Add child bookmark"
        };
        addChildBtn.Click += (_, _) =>
        {
            var path = _pdfReaderPage.CurrentFilePath;
            if (path == null) { SwitchToPdfReader(); return; }
            bm.IsExpanded = true; // ensure parent is expanded before Changed fires
            BookmarkService.Instance.Add(new Bookmark
            {
                FilePath   = path,
                PageNumber = _pdfReaderPage.CurrentPage,
                Title      = $"Page {_pdfReaderPage.CurrentPage + 1}",
                ParentId   = bm.Id
            });
        };

        // ── Edit / delete ──
        var editBtn = new Button
        {
            Content = "✎", FontSize = 13,
            Width = 18, Height = 18, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
            Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 1, 0), ToolTip = "Rename"
        };
        editBtn.Click += (_, _) => StartEdit();

        var deleteBtn = new Button
        {
            Content = "✕", FontSize = 10,
            Width = 18, Height = 18, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x58, 0x5B, 0x70)),
            Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Remove (and children)"
        };
        deleteBtn.Click += (_, _) => BookmarkService.Instance.Remove(bm.Id);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
        btnPanel.Children.Add(addChildBtn);
        btnPanel.Children.Add(editBtn);
        btnPanel.Children.Add(deleteBtn);

        // ── Layout grid ──
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(expandBtn,  0);
        Grid.SetColumn(dragHandle, 1);
        Grid.SetColumn(badge,      2);
        Grid.SetColumn(titleArea,  3);
        Grid.SetColumn(btnPanel,   4);

        mainGrid.Children.Add(expandBtn);
        mainGrid.Children.Add(dragHandle);
        mainGrid.Children.Add(badge);
        mainGrid.Children.Add(titleArea);
        mainGrid.Children.Add(btnPanel);

        // ── Container ──
        var container = new Border
        {
            Background   = BmNormalBg,
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(4, 4, 4, 4),
            Margin       = new Thickness(depth * 16, 1, 0, 1)
        };
        container.Child = mainGrid;

        _bookmarkContainers[bm.Id]   = container;
        _containerInfo[container]    = (bm, depth);

        // ── Hover ──
        container.MouseEnter += (_, _) =>
        {
            if (!_isDraggingBm && _selectedBookmarkId != bm.Id)
                container.Background = BmHoverBg;
        };
        container.MouseLeave += (_, _) =>
        {
            if (_dropHighlightBorder == container) return; // keep drop highlight
            container.Background = _selectedBookmarkId == bm.Id ? BmSelectedBg : BmNormalBg;
        };

        // ── Drag: begin ──
        container.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount > 1) return;
            // Don't start drag from Button or TextBox
            var src = e.OriginalSource as DependencyObject;
            while (src != null && src != container)
            {
                if (src is Button || src is TextBox) return;
                src = VisualTreeHelper.GetParent(src);
            }
            _dragBm        = bm;
            _dragContainer = container;
            _dragStartPt   = e.GetPosition(BookmarkListPanel);
            _isDraggingBm  = false;
            container.CaptureMouse();
            e.Handled = true;
        };

        // ── Drag: move ──
        container.MouseMove += (_, e) =>
        {
            if (_dragBm == null || _dragContainer != container) return;
            var pos = e.GetPosition(BookmarkListPanel);

            if (!_isDraggingBm && Math.Abs(pos.Y - _dragStartPt.Y) > 6)
            {
                _isDraggingBm     = true;
                container.Opacity = 0.45;
            }

            if (_isDraggingBm) { UpdateTreeDropIndicator(pos.Y, pos.X); e.Handled = true; }
        };

        // ── Drag: drop ──
        container.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragBm == null || _dragContainer != container) return;

            bool wasDragging = _isDraggingBm;
            _isDraggingBm    = false;            // disarm LostMouseCapture
            container.ReleaseMouseCapture();

            if (wasDragging)
            {
                container.Opacity = 1.0;
                var dragged      = _dragBm;
                var newParentId  = _dropNewParentId;
                var newSortOrder = _dropNewSortOrder;
                _dragBm = null; _dragContainer = null;
                ClearDropVisuals();
                CommitDrop(dragged, newParentId, newSortOrder);
            }
            else
            {
                _dragBm = null; _dragContainer = null;
                SelectBookmark(bm.Id);
                NavigateToPdfBookmark(bm);
            }
            e.Handled = true;
        };

        // ── Drag: cancel on lost capture (Alt+Tab, etc.) ──
        container.LostMouseCapture += (_, _) =>
        {
            if (!_isDraggingBm) return;
            container.Opacity = 1.0;
            ClearDropVisuals();
            _dragBm = null; _dragContainer = null; _isDraggingBm = false;
        };

        return container;
    }

    // ─── Selection ────────────────────────────────────────────────────────────

    private void SelectBookmark(int id)
    {
        if (_bookmarkContainers.TryGetValue(_selectedBookmarkId, out var prev))
            prev.Background = BmNormalBg;
        _selectedBookmarkId = id;
        if (_bookmarkContainers.TryGetValue(id, out var next))
            next.Background = BmSelectedBg;
    }

    // ─── Drop indicator ───────────────────────────────────────────────────────

    private void UpdateTreeDropIndicator(double mouseY, double mouseX)
    {
        ClearDropVisuals();

        var (targetBm, pos, targetDepth) = GetDropTarget(mouseY);
        _dropTargetBm = targetBm;
        _dropPos      = pos;

        if (targetBm == null)
        {
            // Below all items → append at root level
            _dropNewParentId  = null;
            _dropNewSortOrder = BookmarkService.Instance.GetRoots().Count();
            _dragIndicator    = MakeLine(0);
            BookmarkListPanel.Children.Add(_dragIndicator);
            return;
        }

        var targetContainer = _bookmarkContainers[targetBm.Id];

        if (pos == DropPosition.Into)
        {
            _dropNewParentId           = targetBm.Id;
            _dropNewSortOrder          = BookmarkService.Instance.GetChildren(targetBm.Id).Count();
            _dropHighlightBorder       = targetContainer;
            targetContainer.Background = BmDropIntoBg;
        }
        else
        {
            // Compute effective depth from horizontal mouse position.
            // Each indent level is 16px wide; After can go one level deeper than target.
            int mouseDepth    = Math.Max(0, (int)(mouseX / 16));
            int maxDepth      = pos == DropPosition.After ? targetDepth + 1 : targetDepth;
            int effectiveDepth = Math.Clamp(mouseDepth, 0, maxDepth);

            ComputeInsertionPoint(targetBm, targetDepth, effectiveDepth, pos,
                out int? newParentId, out int newSortOrder);
            _dropNewParentId  = newParentId;
            _dropNewSortOrder = newSortOrder;

            _dragIndicator = MakeLine(effectiveDepth);
            int ci = BookmarkListPanel.Children.IndexOf(targetContainer);
            if (ci >= 0)
                BookmarkListPanel.Children.Insert(
                    pos == DropPosition.Before ? ci : ci + 1, _dragIndicator);
        }
    }

    private void ComputeInsertionPoint(Bookmark target, int targetDepth, int effectiveDepth,
        DropPosition pos, out int? newParentId, out int newSortOrder)
    {
        // Dragging After and moving mouse right → make child of target
        if (pos == DropPosition.After && effectiveDepth > targetDepth)
        {
            newParentId  = target.Id;
            newSortOrder = BookmarkService.Instance.GetChildren(target.Id).Count();
            return;
        }

        // Walk up from target until we reach the ancestor at effectiveDepth
        var ancestor = target;
        int curDepth = targetDepth;
        while (curDepth > effectiveDepth && ancestor.ParentId.HasValue)
        {
            var parent = BookmarkService.Instance.All.FirstOrDefault(b => b.Id == ancestor.ParentId.Value);
            if (parent == null) break;
            ancestor = parent;
            curDepth--;
        }

        newParentId  = ancestor.ParentId;
        newSortOrder = pos == DropPosition.Before ? ancestor.SortOrder : ancestor.SortOrder + 1;
    }

    private (Bookmark? bm, DropPosition pos, int depth) GetDropTarget(double mouseY)
    {
        foreach (UIElement child in BookmarkListPanel.Children)
        {
            if (child is not Border b)        continue;
            if (b == _dragContainer)          continue; // skip the item being dragged
            if (!_containerInfo.TryGetValue(b, out var info)) continue;

            try
            {
                var tf   = b.TransformToAncestor(BookmarkListPanel);
                var topY = tf.Transform(new Point(0, 0)).Y;
                var h    = b.ActualHeight;
                if (mouseY < topY || mouseY > topY + h) continue;

                float rel = (float)((mouseY - topY) / h);
                var dpos = rel < 0.28f ? DropPosition.Before
                         : rel > 0.72f ? DropPosition.After
                         :               DropPosition.Into;
                return (info.Bm, dpos, info.Depth);
            }
            catch { continue; }
        }
        return (null, DropPosition.After, 0);
    }

    private Rectangle MakeLine(int depth) => new Rectangle
    {
        Height           = 2,
        Fill             = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
        Margin           = new Thickness(6 + depth * 16, 0, 6, 0),
        IsHitTestVisible = false
    };

    private void ClearDropVisuals()
    {
        if (_dragIndicator != null)
        {
            BookmarkListPanel.Children.Remove(_dragIndicator);
            _dragIndicator = null;
        }
        if (_dropHighlightBorder != null)
        {
            var prev = _dropTargetBm;
            _dropHighlightBorder.Background =
                prev != null && _selectedBookmarkId == prev.Id ? BmSelectedBg : BmNormalBg;
            _dropHighlightBorder = null;
        }
        _dropTargetBm = null;
    }

    // ─── Commit drop ─────────────────────────────────────────────────────────

    private void CommitDrop(Bookmark? dragged, int? newParentId, int newSortOrder)
    {
        if (dragged == null) return;

        // Expand the new parent so the moved item becomes visible
        if (newParentId.HasValue)
        {
            var parent = BookmarkService.Instance.All.FirstOrDefault(b => b.Id == newParentId.Value);
            if (parent != null && !parent.IsExpanded) parent.IsExpanded = true;
        }

        BookmarkService.Instance.Move(dragged.Id, newParentId, newSortOrder);
    }

    // ─── Navigate to bookmark ────────────────────────────────────────────────

    private void NavigateToPdfBookmark(Bookmark bm)
    {
        SwitchToPdfReader();
        _pdfReaderPage.NavigateToBookmark(bm);
    }
}
