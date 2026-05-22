namespace HoloPDFCreator.Models;

public class PdfMemo
{
    private static int _counter;
    public int      Id         { get; } = System.Threading.Interlocked.Increment(ref _counter);
    public string   FilePath   { get; set; } = "";
    public uint     PageNumber { get; set; }
    public double   X          { get; set; }   // PDF user-space (pts, origin BL)
    public double   Y          { get; set; }
    public string   Content    { get; set; } = "";
    public DateTime CreatedAt  { get; set; } = DateTime.Now;
}
