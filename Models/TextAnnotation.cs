using System.Windows.Media;

namespace HoloPDFCreator.Models;

public enum AnnotationType { Highlight, Underline, Strikethrough }

// All coordinates in PDF user-space units (points, origin bottom-left, Y increases upward)
public class AnnotationSpan
{
    public double Left   { get; set; }
    public double Bottom { get; set; }
    public double Right  { get; set; }
    public double Top    { get; set; }   // Top > Bottom in PDF space
}

public class TextAnnotation
{
    private static int _counter;
    public int                  Id         { get; } = System.Threading.Interlocked.Increment(ref _counter);
    public AnnotationType       Type       { get; set; }
    public Color                Color      { get; set; }
    public string               FilePath   { get; set; } = "";
    public uint                 PageNumber { get; set; }
    public List<AnnotationSpan> Spans      { get; set; } = new();
}
