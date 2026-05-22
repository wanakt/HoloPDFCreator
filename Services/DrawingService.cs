using HoloPDFCreator.Models;

namespace HoloPDFCreator.Services;

public sealed class DrawingService
{
    public static readonly DrawingService Instance = new();
    private DrawingService() {}

    private readonly List<DrawingShape> _items = new();

    public void Add(DrawingShape s)    => _items.Add(s);
    public void Remove(int id)         => _items.RemoveAll(s => s.Id == id);
    public void ClearForFile(string f) =>
        _items.RemoveAll(s => s.FilePath.Equals(f, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<DrawingShape> ForPage(string filePath, uint page) =>
        _items.Where(s =>
            s.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) &&
            s.PageNumber == page);

    public IEnumerable<DrawingShape> ForFile(string filePath) =>
        _items.Where(s => s.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

    public void RenameFile(string oldPath, string newPath)
    {
        foreach (var s in _items.Where(s => s.FilePath.Equals(oldPath, StringComparison.OrdinalIgnoreCase)))
            s.FilePath = newPath;
    }
}
