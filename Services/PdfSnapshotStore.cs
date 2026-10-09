using System.Diagnostics;
using System.IO;

namespace HoloPDFCreator.Services;

/// <summary>
/// Keeps the PDF bytes of undo/redo snapshots in temp files instead of the managed heap.
/// Every structural edit used to pin a full copy of the document in memory (15 undo
/// levels plus the redo stack), so memory grew by the file size on each edit.
/// <para>
/// Handles are reference-counted: a snapshot may sit on both stacks, and consecutive
/// captures of the same byte array share one file. The file is deleted when the last
/// reference is released. If writing fails (disk full, no temp access) the bytes are
/// kept in memory instead, so undo still works.
/// </para>
/// </summary>
public sealed class PdfSnapshotStore : IDisposable
{
    public sealed class Handle
    {
        internal string? FilePath;
        internal byte[]? Bytes;      // in-memory fallback
        internal int     Refs;
        public   long    Length { get; internal set; }
        public   bool    OnDisk => FilePath != null;
    }

    private readonly string _dir;
    private int _seq;

    // The byte array most recently captured or loaded, and its handle. Lets the next
    // capture of that same array reuse the file instead of writing it again.
    private byte[]? _lastBytes;
    private Handle? _lastHandle;

    /// <summary>Total size of the snapshot files currently on disk.</summary>
    public long DiskBytes { get; private set; }

    /// <summary>Size of snapshots that fell back to memory.</summary>
    public long MemoryBytes { get; private set; }

    public PdfSnapshotStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "HoloPDFCreator");
        _dir = Path.Combine(root, $"undo-{Environment.ProcessId}");
        DeleteStaleFolders(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    /// <summary>Returns a handle for <paramref name="bytes"/> holding one reference.</summary>
    public Handle Capture(byte[] bytes)
    {
        if (ReferenceEquals(bytes, _lastBytes) && _lastHandle != null)
        {
            _lastHandle.Refs++;
            return _lastHandle;
        }

        var h = new Handle { Length = bytes.Length, Refs = 1 };
        try
        {
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, $"{Interlocked.Increment(ref _seq):D6}.pdf");
            File.WriteAllBytes(path, bytes);
            h.FilePath = path;
            DiskBytes += h.Length;
        }
        catch
        {
            h.Bytes = bytes;
            MemoryBytes += h.Length;
        }
        Remember(bytes, h);
        return h;
    }

    public void AddRef(Handle h) => h.Refs++;

    public void Release(Handle h)
    {
        if (--h.Refs > 0) return;
        if (h.FilePath != null)
        {
            try { File.Delete(h.FilePath); } catch { /* temp file, non-critical */ }
            DiskBytes -= h.Length;
            h.FilePath = null;
        }
        else if (h.Bytes != null)
        {
            MemoryBytes -= h.Length;
            h.Bytes = null;
        }
    }

    /// <summary>Reads the snapshot back. The returned array is linked to the handle for de-duplication.</summary>
    public byte[] Load(Handle h)
    {
        byte[] bytes = h.Bytes
            ?? (h.FilePath != null ? File.ReadAllBytes(h.FilePath)
                                   : throw new InvalidOperationException("Snapshot was already released."));
        Remember(bytes, h);
        return bytes;
    }

    private void Remember(byte[] bytes, Handle h)
    {
        if (ReferenceEquals(h, _lastHandle)) { _lastBytes = bytes; return; }
        h.Refs++;
        var old = _lastHandle;
        _lastBytes  = bytes;
        _lastHandle = h;
        if (old != null) Release(old);
    }

    public void Dispose()
    {
        _lastBytes  = null;
        _lastHandle = null;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        DiskBytes = MemoryBytes = 0;
    }

    // Folders left behind by instances that crashed or were killed.
    private static void DeleteStaleFolders(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root, "undo-*"))
            {
                if (!int.TryParse(Path.GetFileName(dir)["undo-".Length..], out int pid)) continue;
                if (pid == Environment.ProcessId || IsRunning(pid)) continue;
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
        catch { }
    }

    private static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}
