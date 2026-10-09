using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HoloPDFCreator.Models;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace HoloPDFCreator.Services;

/// <summary>
/// The one place that turns a page of a <see cref="DocumentSession"/> into an image:
/// rendered from the session's current PDF, with the page's adjustment applied, cached by
/// page id. The reader (page, side pages, thumbnails) and the OCR view (display and OCR
/// input) all ask here, so a page is rendered once per size and an adjustment shows up
/// everywhere at once.
/// <para>
/// Cached images stay valid across page reorder/insert/delete (they are keyed by page id);
/// rotating or cropping a page (<see cref="DocumentSession.ContentChanged"/>) or changing its
/// adjustment drops that page's images and raises <see cref="PageImageChanged"/>.
/// </para>
/// </summary>
public sealed class PageImageProvider : IDisposable
{
    private const int MaxDocuments = 4;

    private readonly DocumentSession _session;
    private readonly object _lock = new();

    // Rendering: a small pool of documents opened from the same file, so pages can be
    // rendered in parallel (OCR) without sharing one WinRT document across threads.
    private string? _path;
    private int     _docVersion;
    private ConcurrentBag<WinPdf.PdfDocument> _docs = new();
    private SemaphoreSlim _docSlots = new(MaxDocuments, MaxDocuments);
    // Between a page-order change and the new rendering file the order and the file disagree.
    private bool _stale;

    // LRU cache of frozen images.
    private sealed record Entry((long Id, int Width, bool Adjusted) Key, BitmapSource Image, long Bytes);
    private readonly Dictionary<(long, int, bool), LinkedListNode<Entry>> _cache = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;
    private readonly Dictionary<long, int> _pageVersion = new();

    /// <summary>Memory the cached images may take.</summary>
    public long CacheBudget { get; set; } = 256L * 1024 * 1024;

    public long CachedBytes { get { lock (_lock) return _bytes; } }

    private int _renderCount;
    /// <summary>Pages actually rendered (cache misses), for diagnostics.</summary>
    public int RenderCount => _renderCount;

    /// <summary>Raised (on any thread) with the id of a page whose images changed; -1 = all pages.</summary>
    public event Action<long>? PageImageChanged;

    public PageImageProvider(DocumentSession session)
    {
        _session = session;
        _session.ContentChanged += OnContentChanged;
        _session.OrderChanged   += OnOrderChanged;
        _session.Reset          += OnReset;
        _session.Adjust.Changed += OnAdjustChanged;
    }

    public void Dispose()
    {
        _session.ContentChanged -= OnContentChanged;
        _session.OrderChanged   -= OnOrderChanged;
        _session.Reset          -= OnReset;
        _session.Adjust.Changed -= OnAdjustChanged;
    }

    public string? DocumentPath { get { lock (_lock) return _path; } }

    /// <summary>
    /// True between a page edit (order or content) and the new rendering file: page
    /// indices do not match that file then, so <see cref="GetAsync"/> returns null.
    /// </summary>
    public bool IsStale { get { lock (_lock) return _stale; } }

    /// <summary>
    /// The file the session's pages are rendered from now (it changes with every page edit).
    /// Images of pages whose content did not change stay cached.
    /// </summary>
    /// <param name="loaded">The caller's already loaded document of that file, reused for rendering.</param>
    public void SetDocument(string? path, WinPdf.PdfDocument? loaded = null)
    {
        lock (_lock)
        {
            _path = path;
            _docVersion++;
            _docs = new ConcurrentBag<WinPdf.PdfDocument>();
            if (path != null && loaded != null) _docs.Add(loaded);
            _docSlots = new SemaphoreSlim(MaxDocuments, MaxDocuments);
            _stale = false;
        }
    }

    /// <summary>Drops every cached image (the PDF itself was replaced, e.g. reloaded or restored).</summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lru.Clear();
            _bytes = 0;
            _pageVersion.Clear();
        }
        PageImageChanged?.Invoke(-1);
    }

    private void Invalidate(long id, bool adjustedOnly)
    {
        lock (_lock)
        {
            _pageVersion[id] = _pageVersion.GetValueOrDefault(id) + 1;
            foreach (var key in _cache.Keys.Where(k => k.Item1 == id && (!adjustedOnly || k.Item3)).ToList())
                RemoveEntry(key);
        }
        PageImageChanged?.Invoke(id);
    }

    private void OnContentChanged(IReadOnlyCollection<long> ids)
    {
        // The rendering file still shows the old content until SetDocument.
        lock (_lock) _stale = true;
        foreach (var id in ids) Invalidate(id, adjustedOnly: false);
    }

    private void OnAdjustChanged(long id) => Invalidate(id, adjustedOnly: true);

    private void OnOrderChanged(long[] _, long[] __)
    {
        lock (_lock) _stale = true;
    }

    private void OnReset() => InvalidateAll();

    // ── Getting images ────────────────────────────────────────────────────────

    /// <summary>
    /// The image of page <paramref name="index"/>, <paramref name="width"/> pixels wide, with
    /// the page's adjustment applied when <paramref name="adjusted"/> (and it has one).
    /// <paramref name="cache"/>: false for one-off large renders (baking on save).
    /// </summary>
    public async Task<BitmapSource?> GetAsync(int index, int width, bool adjusted = true,
                                              bool cache = true, CancellationToken ct = default)
    {
        if (index < 0 || index >= _session.PageCount || width <= 0) return null;
        long id = _session.IdAt(index);
        AdjustSettings? adj = adjusted && _session.Adjust.TryGetValue(index, out var a) ? a : null;
        var key = (id, width, adj != null);

        string? path; int docVersion; bool stale; int pageVersion;
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }
            path = _path; docVersion = _docVersion; stale = _stale;
            pageVersion = _pageVersion.GetValueOrDefault(id);
        }
        // Mid page edit the index may name another page in the file: render nothing.
        if (path == null || stale) return null;

        var image = await RenderAsync(path, docVersion, index, width, adj, ct).ConfigureAwait(false);
        if (image == null) return null;

        lock (_lock)
        {
            // Store only what is still current: same file, same page order, same page version.
            if (cache && !stale && !_stale && docVersion == _docVersion &&
                pageVersion == _pageVersion.GetValueOrDefault(id) && !_cache.ContainsKey(key))
            {
                long bytes = (long)image.PixelWidth * image.PixelHeight * 4;
                _cache[key] = _lru.AddFirst(new Entry(key, image, bytes));
                _bytes += bytes;
                while (_bytes > CacheBudget && _lru.Count > 1)
                    RemoveEntry(_lru.Last!.Value.Key);
            }
        }
        return image;
    }

    private void RemoveEntry((long, int, bool) key)
    {
        if (!_cache.Remove(key, out var node)) return;
        _lru.Remove(node);
        _bytes -= node.Value.Bytes;
    }

    private async Task<BitmapSource?> RenderAsync(string path, int docVersion, int index, int width,
                                                  AdjustSettings? adj, CancellationToken ct)
    {
        SemaphoreSlim slots; ConcurrentBag<WinPdf.PdfDocument> docs;
        lock (_lock) { slots = _docSlots; docs = _docs; }

        await slots.WaitAsync(ct).ConfigureAwait(false);
        WinPdf.PdfDocument? doc = null;
        try
        {
            if (!docs.TryTake(out doc))
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct).ConfigureAwait(false);
                doc = await WinPdf.PdfDocument.LoadFromFileAsync(file).AsTask(ct).ConfigureAwait(false);
            }
            if (index >= doc.PageCount) return null;
            Interlocked.Increment(ref _renderCount);

            var ms = new MemoryStream();
            using (var page = doc.GetPage((uint)index))
            using (var stream = new InMemoryRandomAccessStream())
            {
                await page.RenderToStreamAsync(stream, new WinPdf.PdfPageRenderOptions { DestinationWidth = (uint)width })
                          .AsTask(ct).ConfigureAwait(false);
                stream.Seek(0);
                await stream.AsStream().CopyToAsync(ms, ct).ConfigureAwait(false);
            }
            ms.Position = 0;

            if (adj == null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = ms;
                bmp.CacheOption  = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }

            using var raw      = new System.Drawing.Bitmap(ms);
            using var adjusted = ImageProcessingService.ApplyAdjustments(raw, adj);
            return ToBitmapSource(adjusted);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally
        {
            // Return the document to its pool unless the file changed meanwhile.
            if (doc != null)
                lock (_lock) if (docVersion == _docVersion && ReferenceEquals(docs, _docs)) docs.Add(doc);
            slots.Release();
        }
    }

    /// <summary>Frozen BGRA copy of a System.Drawing bitmap (no PNG round trip).</summary>
    public static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var bs = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null,
                                         data.Scan0, data.Stride * bmp.Height, data.Stride);
            bs.Freeze();
            return bs;
        }
        finally { bmp.UnlockBits(data); }
    }
}
