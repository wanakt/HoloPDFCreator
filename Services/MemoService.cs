using System.IO;
using System.Text.Json;
using HoloPDFCreator.Models;

namespace HoloPDFCreator.Services;

internal sealed class MemoRecord
{
    public double   X          { get; set; }
    public double   Y          { get; set; }
    public uint     PageNumber { get; set; }
    public string   Content    { get; set; } = "";
    public DateTime CreatedAt  { get; set; }
}

public sealed class MemoService
{
    public static readonly MemoService Instance = new();
    private MemoService() {}

    private readonly List<PdfMemo> _items = new();
    public IReadOnlyList<PdfMemo> All => _items;

    public void Add(PdfMemo m)    => _items.Add(m);
    public void Remove(int id)    => _items.RemoveAll(m => m.Id == id);
    public void ClearForFile(string filePath) =>
        _items.RemoveAll(m => m.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<PdfMemo> ForPage(string filePath, uint pageNumber) =>
        _items.Where(m =>
            m.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) &&
            m.PageNumber == pageNumber);

    public IEnumerable<PdfMemo> ForFile(string filePath) =>
        _items.Where(m => m.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<PdfMemo> Search(string filePath, string query) =>
        ForFile(filePath).Where(m =>
            m.Content.Contains(query, StringComparison.OrdinalIgnoreCase));

    public void RenameFile(string oldPath, string newPath)
    {
        foreach (var m in _items.Where(m =>
            m.FilePath.Equals(oldPath, StringComparison.OrdinalIgnoreCase)))
            m.FilePath = newPath;
    }

    public void SaveForFile(string filePath)
    {
        var records = _items
            .Where(m => m.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            .Select(m => new MemoRecord
            {
                X          = m.X,
                Y          = m.Y,
                PageNumber = m.PageNumber,
                Content    = m.Content,
                CreatedAt  = m.CreatedAt,
            })
            .ToList();

        var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SidecarPath(filePath), json);
    }

    public int LoadForFile(string filePath)
    {
        _items.RemoveAll(m => m.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

        var path = SidecarPath(filePath);
        if (!File.Exists(path)) return 0;

        var json    = File.ReadAllText(path);
        var records = JsonSerializer.Deserialize<List<MemoRecord>>(json);
        if (records is null) return 0;

        foreach (var r in records)
        {
            _items.Add(new PdfMemo
            {
                FilePath   = filePath,
                PageNumber = r.PageNumber,
                X          = r.X,
                Y          = r.Y,
                Content    = r.Content,
                CreatedAt  = r.CreatedAt,
            });
        }
        return records.Count;
    }

    private static string SidecarPath(string pdfPath) =>
        Path.Combine(
            Path.GetDirectoryName(pdfPath) ?? "",
            Path.GetFileNameWithoutExtension(pdfPath) + ".memo.json");
}
