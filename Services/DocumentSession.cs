using System.Collections;
using HoloPDFCreator.Models;

namespace HoloPDFCreator.Services;

/// <summary>
/// The open document, shared by the PDF reader and the OCR view.
/// <para>
/// Every page has an id that survives reordering, inserting and deleting pages. Per-page
/// data — adjustments, the image a page was made from, OCR results — is stored by that
/// id, so a page edit only changes <see cref="Order"/> and the data follows its page.
/// Callers still use page indices through <see cref="PageIndexed{T}"/>.
/// </para>
/// </summary>
public sealed class DocumentSession
{
    private readonly List<long>           _order    = new();
    private readonly Dictionary<long, int> _indexOf = new();
    private long _nextId = 1;
    private readonly bool _canGrow;

    /// <param name="canGrow">
    /// True for a session whose pages are created by adding images (the OCR view's own
    /// workspace): writing past the last page then adds pages. A PDF's session has a
    /// fixed page list that only page edits change.
    /// </param>
    public DocumentSession(bool canGrow = false)
    {
        _canGrow     = canGrow;
        Adjust       = new PageIndexed<AdjustSettings>(this);
        SourceImages = new PageIndexed<string>(this);
        OcrRegions   = new PageIndexed<List<OcrRegion>>(this);
        OcrSize      = new PageIndexed<(int w, int h)>(this);
        OcrGeometry  = new PageIndexed<PageGeometry>(this);
    }

    /// <summary>The document's own path (the reader may render from a temp copy).</summary>
    public string? FilePath { get; private set; }

    public int                PageCount => _order.Count;
    public IReadOnlyList<long> Order    => _order;

    /// <summary>Adjustment settings per page (undoable: the reader snapshots them).</summary>
    public PageIndexed<AdjustSettings> Adjust { get; }

    /// <summary>Image file a page was created from (for pages made from images).</summary>
    public PageIndexed<string> SourceImages { get; }

    /// <summary>OCR results: regions, the image size they refer to, the page geometry at OCR time.</summary>
    public PageIndexed<List<OcrRegion>> OcrRegions  { get; }
    public PageIndexed<(int w, int h)>  OcrSize     { get; }
    public PageIndexed<PageGeometry>    OcrGeometry { get; }

    /// <summary>Raised with the page order before and after any change of it.</summary>
    public event Action<long[], long[]>? OrderChanged;

    /// <summary>Raised when pages now show something else (rotated, cropped).</summary>
    public event Action<IReadOnlyCollection<long>>? ContentChanged;

    /// <summary>Raised when another document (or a new copy of it) was opened: all ids are new.</summary>
    public event Action? Reset;

    // ── Opening ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a document of <paramref name="pageCount"/> pages. Reopening the same file with
    /// the same page count (e.g. after saving it) keeps the ids and all per-page data.
    /// Returns true when the session was reset.
    /// </summary>
    public bool Open(string? filePath, int pageCount)
    {
        if (filePath != null && string.Equals(filePath, FilePath, StringComparison.OrdinalIgnoreCase) &&
            pageCount == _order.Count)
            return false;

        FilePath = filePath;
        _order.Clear();
        foreach (var store in Stores) store.ClearAll();
        for (int i = 0; i < pageCount; i++) _order.Add(_nextId++);
        Reindex();
        Reset?.Invoke();
        return true;
    }

    /// <summary>The document was saved under another name; its pages and data stay.</summary>
    public void Rename(string filePath) => FilePath = filePath;

    // ── Page edits ────────────────────────────────────────────────────────────

    public long IdAt(int index) => _order[index];

    public int IndexOf(long id) => _indexOf.TryGetValue(id, out int i) ? i : -1;

    /// <summary>newToOld[i] = current index of the page to put at position i.</summary>
    public void Reorder(IReadOnlyList<int> newToOld) =>
        SetOrder(newToOld.Select(i => _order[i]).ToArray());

    /// <summary>Inserts <paramref name="count"/> new pages at <paramref name="at"/>; returns their ids.</summary>
    public long[] Insert(int at, int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => _nextId++).ToArray();
        var order = _order.ToList();
        order.InsertRange(Math.Clamp(at, 0, order.Count), ids);
        SetOrder(order.ToArray());
        return ids;
    }

    /// <summary>
    /// Removes pages from the order. Their data is kept, so restoring an earlier
    /// order (undo) brings the pages back with it.
    /// </summary>
    public void Delete(IEnumerable<int> indices)
    {
        var drop = new HashSet<int>(indices);
        SetOrder(_order.Where((_, i) => !drop.Contains(i)).ToArray());
    }

    public void MarkContentChanged(IEnumerable<int> indices) =>
        ContentChanged?.Invoke(indices.Where(i => i >= 0 && i < _order.Count).Select(i => _order[i]).ToArray());

    /// <summary>The page order, for undo.</summary>
    public long[] CaptureOrder() => _order.ToArray();

    public void RestoreOrder(long[] order)
    {
        if (order.SequenceEqual(_order)) return;
        SetOrder(order);
    }

    private void SetOrder(long[] order)
    {
        var old = _order.ToArray();
        _order.Clear();
        _order.AddRange(order);
        Reindex();
        OrderChanged?.Invoke(old, order);
    }

    // Pages created by writing past the end (OCR workspace only).
    internal bool EnsureCount(int count)
    {
        if (count <= _order.Count) return true;
        if (!_canGrow) return false;
        while (_order.Count < count) _order.Add(_nextId++);
        Reindex();
        return true;
    }

    private void Reindex()
    {
        _indexOf.Clear();
        for (int i = 0; i < _order.Count; i++) _indexOf[_order[i]] = i;
    }

    private IEnumerable<IPageStore> Stores =>
        new IPageStore[] { Adjust, SourceImages, OcrRegions, OcrSize, OcrGeometry };

    // ── OCR ───────────────────────────────────────────────────────────────────

    /// <summary>OCR results ready for the text layer, by current page index (pages with an image size only).</summary>
    public Dictionary<int, OcrPageData> OcrForSave()
    {
        var result = new Dictionary<int, OcrPageData>();
        lock (OcrRegions)
        {
            foreach (var (idx, regions) in OcrRegions)
            {
                if (regions.Count == 0) continue;
                if (!OcrSize.TryGetValue(idx, out var d) || d.w <= 0 || d.h <= 0) continue;
                result[idx] = new OcrPageData(regions, d.w, d.h,
                    OcrGeometry.TryGetValue(idx, out var g) ? g : null);
            }
        }
        return result;
    }

    internal interface IPageStore { void ClearAll(); }
}

/// <summary>
/// Per-page data addressed by page index but stored by page id, so it follows its page
/// through reorder, insert and delete. Enumeration, Count and Keys cover the pages in the
/// current order only; data of deleted pages is kept for undo.
/// </summary>
public sealed class PageIndexed<T> : IDictionary<int, T>, DocumentSession.IPageStore
{
    private readonly DocumentSession   _session;
    private readonly Dictionary<long, T> _byId = new();

    internal PageIndexed(DocumentSession session) => _session = session;

    public T this[int index]
    {
        get => TryGetValue(index, out var v) ? v : throw new KeyNotFoundException($"No data for page {index}.");
        set
        {
            if (index < 0 || !_session.EnsureCount(index + 1)) return;
            _byId[_session.IdAt(index)] = value;
        }
    }

    public bool TryGetValue(int index, out T value)
    {
        if (index >= 0 && index < _session.PageCount && _byId.TryGetValue(_session.IdAt(index), out value!))
            return true;
        value = default!;
        return false;
    }

    public bool ContainsKey(int index) => TryGetValue(index, out _);

    public bool Remove(int index) =>
        index >= 0 && index < _session.PageCount && _byId.Remove(_session.IdAt(index));

    /// <summary>Removes the data of every page, including deleted ones.</summary>
    public void Clear() => _byId.Clear();
    void DocumentSession.IPageStore.ClearAll() => _byId.Clear();

    public ICollection<int> Keys =>
        Enumerable.Range(0, _session.PageCount).Where(i => _byId.ContainsKey(_session.IdAt(i))).ToList();

    public ICollection<T> Values => this.Select(kv => kv.Value).ToList();

    public int Count => Enumerable.Range(0, _session.PageCount).Count(i => _byId.ContainsKey(_session.IdAt(i)));

    public bool IsReadOnly => false;

    public IEnumerator<KeyValuePair<int, T>> GetEnumerator()
    {
        for (int i = 0; i < _session.PageCount; i++)
            if (_byId.TryGetValue(_session.IdAt(i), out var v))
                yield return new KeyValuePair<int, T>(i, v);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Add(int key, T value) => this[key] = value;
    public void Add(KeyValuePair<int, T> item) => this[item.Key] = item.Value;
    public bool Contains(KeyValuePair<int, T> item) =>
        TryGetValue(item.Key, out var v) && EqualityComparer<T>.Default.Equals(v, item.Value);
    public bool Remove(KeyValuePair<int, T> item) => Contains(item) && Remove(item.Key);
    public void CopyTo(KeyValuePair<int, T>[] array, int arrayIndex)
    {
        foreach (var kv in this) array[arrayIndex++] = kv;
    }

    /// <summary>All data by page id (deleted pages included), for undo.</summary>
    public Dictionary<long, T> SnapshotById() => new(_byId);

    public void RestoreById(IReadOnlyDictionary<long, T> data)
    {
        _byId.Clear();
        foreach (var (k, v) in data) _byId[k] = v;
    }
}
