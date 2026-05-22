using System.Windows.Media;

namespace HoloPDFCreator.Models;

public enum DrawingShapeType { Line, Box, Circle }

// Coordinates in PDF user-space (points, origin bottom-left, Y increases upward)
public class DrawingShape
{
    private static int _counter;
    public int              Id         { get; } = System.Threading.Interlocked.Increment(ref _counter);
    public DrawingShapeType ShapeType  { get; set; }
    public Color            Stroke     { get; set; } = Colors.Red;
    public Color            Fill       { get; set; } = Colors.Transparent;
    public bool             HasFill    { get; set; } = false;
    public string           FilePath   { get; set; } = "";
    public uint             PageNumber { get; set; }
    // PDF-space coords
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
}
