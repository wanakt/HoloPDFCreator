using System.IO;
using System.Text.Json;
using System.Windows.Media;
using HoloPDFCreator.Models;

namespace HoloPDFCreator.Services;

internal sealed class AnnotationRecord
{
    public int              Type       { get; set; }
    public byte             A          { get; set; }
    public byte             R          { get; set; }
    public byte             G          { get; set; }
    public byte             B          { get; set; }
    public uint             PageNumber { get; set; }
    public List<SpanRecord> Spans      { get; set; } = new();
}

internal sealed class SpanRecord
{
    public double Left   { get; set; }
    public double Bottom { get; set; }
    public double Right  { get; set; }
    public double Top    { get; set; }
}

public sealed class AnnotationService
{
    public static readonly AnnotationService Instance = new();
    private AnnotationService() {}

    private readonly List<TextAnnotation> _items = new();
    public IReadOnlyList<TextAnnotation> All => _items;

    public void Add(TextAnnotation a) => _items.Add(a);
    public void Remove(int id)        => _items.RemoveAll(a => a.Id == id);
    public void ClearForFile(string filePath) =>
        _items.RemoveAll(a => a.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<TextAnnotation> ForPage(string filePath, uint pageNumber) =>
        _items.Where(a =>
            a.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) &&
            a.PageNumber == pageNumber);

    public IEnumerable<TextAnnotation> ForFile(string filePath) =>
        _items.Where(a => a.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

    public void RenameFile(string oldPath, string newPath)
    {
        foreach (var a in _items.Where(a => a.FilePath.Equals(oldPath, StringComparison.OrdinalIgnoreCase)))
            a.FilePath = newPath;
    }

    // ─── Persistence ──────────────────────────────────────────────────────────

    public void SaveForFile(string filePath)
    {
        var records = _items
            .Where(a => a.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            .Select(a => new AnnotationRecord
            {
                Type       = (int)a.Type,
                A          = a.Color.A,
                R          = a.Color.R,
                G          = a.Color.G,
                B          = a.Color.B,
                PageNumber = a.PageNumber,
                Spans      = a.Spans.Select(s => new SpanRecord
                {
                    Left = s.Left, Bottom = s.Bottom, Right = s.Right, Top = s.Top
                }).ToList()
            })
            .ToList();

        var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SidecarPath(filePath), json);
    }

    public int LoadForFile(string filePath)
    {
        _items.RemoveAll(a => a.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

        var path = SidecarPath(filePath);
        if (!File.Exists(path)) return 0;

        var json    = File.ReadAllText(path);
        var records = JsonSerializer.Deserialize<List<AnnotationRecord>>(json);
        if (records is null) return 0;

        foreach (var r in records)
        {
            _items.Add(new TextAnnotation
            {
                Type       = (AnnotationType)r.Type,
                Color      = Color.FromArgb(r.A, r.R, r.G, r.B),
                FilePath   = filePath,
                PageNumber = r.PageNumber,
                Spans      = r.Spans.Select(s => new AnnotationSpan
                {
                    Left = s.Left, Bottom = s.Bottom, Right = s.Right, Top = s.Top
                }).ToList()
            });
        }
        return records.Count;
    }

    private static string SidecarPath(string pdfPath) =>
        Path.Combine(
            Path.GetDirectoryName(pdfPath) ?? "",
            Path.GetFileNameWithoutExtension(pdfPath) + ".annot.json");
}
