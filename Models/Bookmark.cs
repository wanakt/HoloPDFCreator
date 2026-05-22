namespace HoloPDFCreator.Models;

public class Bookmark
{
    private static int _counter;

    public int      Id         { get; }      = System.Threading.Interlocked.Increment(ref _counter);
    public string   FilePath   { get; init; } = "";
    public uint     PageNumber { get; init; }
    public string   Title      { get; set;  } = "";
    public DateTime CreatedAt  { get; }      = DateTime.Now;
    public int?     ParentId   { get; set;  }
    public int      SortOrder  { get; set;  }
    public bool     IsExpanded { get; set;  } = true;

    public string FileName => System.IO.Path.GetFileNameWithoutExtension(FilePath);
}
