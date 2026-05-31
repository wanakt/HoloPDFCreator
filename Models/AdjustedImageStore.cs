using System.Windows.Media.Imaging;

namespace HoloPDFCreator.Models;

/// <summary>
/// Shared store of adjusted page bitmaps produced by ImageAdjusterPage.
/// PDFReaderPage reads from this to display adjusted images in-place.
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

    public bool HasAny => _images.Count > 0;

    public IReadOnlyList<(int PageIndex, BitmapImage Image)> GetAll()
        => _images.OrderBy(kv => kv.Key)
                  .Select(kv => (kv.Key, kv.Value))
                  .ToList();
}
