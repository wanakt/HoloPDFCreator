using System.Windows.Media.Imaging;

namespace HoloPDFCreator.Models;

/// <summary>
/// Shared store of adjusted page bitmaps produced by PDFReaderPage's adjust panel.
/// OcrPage reads from this so OCR runs on the adjusted images.
/// Keys are 0-based page indices.
/// </summary>
public class AdjustedImageStore
{
    private readonly Dictionary<int, BitmapImage> _images = new();

    public BitmapImage? Get(int pageIndex)
        => _images.TryGetValue(pageIndex, out var img) ? img : null;

    public void Set(int pageIndex, BitmapImage img) => _images[pageIndex] = img;

    public void Remove(int pageIndex) => _images.Remove(pageIndex);

    public void Clear() => _images.Clear();

    /// <summary>
    /// Follows a page edit: newToOld[i] is the old index of the page now at i (-1 = new
    /// page; null = pages did not move). Images of <paramref name="drop"/> pages (new
    /// indices, e.g. rotated or cropped) are discarded and get rendered again.
    /// </summary>
    public void Remap(int[]? newToOld, ISet<int>? drop = null)
    {
        var moved = new Dictionary<int, BitmapImage>();
        if (newToOld == null)
        {
            foreach (var (k, v) in _images) moved[k] = v;
        }
        else
        {
            for (int i = 0; i < newToOld.Length; i++)
                if (newToOld[i] >= 0 && _images.TryGetValue(newToOld[i], out var img))
                    moved[i] = img;
        }
        if (drop != null) foreach (int i in drop) moved.Remove(i);
        _images.Clear();
        foreach (var (k, v) in moved) _images[k] = v;
    }

    public bool HasAny => _images.Count > 0;

    public IReadOnlyList<(int PageIndex, BitmapImage Image)> GetAll()
        => _images.OrderBy(kv => kv.Key)
                  .Select(kv => (kv.Key, kv.Value))
                  .ToList();
}
