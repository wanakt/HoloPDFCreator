using HoloPDFCreator.Models;

namespace HoloPDFCreator.Services;

public sealed class BookmarkService
{
    public static readonly BookmarkService Instance = new();
    private BookmarkService() { }

    private readonly List<Bookmark> _items = new();

    public event EventHandler? Changed;

    public IReadOnlyList<Bookmark> All => _items;

    public IEnumerable<Bookmark> GetRoots() => GetChildren(null);

    public IEnumerable<Bookmark> GetChildren(int? parentId) =>
        _items.Where(b => b.ParentId == parentId).OrderBy(b => b.SortOrder);

    public bool HasChildren(int id) => _items.Any(b => b.ParentId == id);

    public IEnumerable<Bookmark> ForFile(string filePath) =>
        _items.Where(b => b.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

    public void Add(Bookmark b)
    {
        b.SortOrder = _items.Count(x => x.ParentId == b.ParentId);
        _items.Add(b);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(int id)
    {
        RemoveDescendants(id);
        if (_items.RemoveAll(b => b.Id == id) > 0)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveDescendants(int parentId)
    {
        var childIds = _items.Where(b => b.ParentId == parentId).Select(b => b.Id).ToList();
        foreach (var cid in childIds) RemoveDescendants(cid);
        _items.RemoveAll(b => b.ParentId == parentId);
    }

    // Move item id to a new position: newParentId = parent (null = root), newSortOrder = sibling index
    public void Move(int id, int? newParentId, int newSortOrder)
    {
        var bm = _items.FirstOrDefault(b => b.Id == id);
        if (bm == null) return;
        if (newParentId.HasValue && IsDescendant(id, newParentId.Value)) return; // prevent cycles

        int? oldParentId = bm.ParentId;
        bm.ParentId = newParentId;

        // Recompact old siblings
        var oldSiblings = _items
            .Where(b => b.ParentId == oldParentId && b.Id != id)
            .OrderBy(b => b.SortOrder).ToList();
        for (int i = 0; i < oldSiblings.Count; i++) oldSiblings[i].SortOrder = i;

        // Insert among new siblings
        var newSiblings = _items
            .Where(b => b.ParentId == newParentId && b.Id != id)
            .OrderBy(b => b.SortOrder).ToList();
        newSiblings.Insert(Math.Clamp(newSortOrder, 0, newSiblings.Count), bm);
        for (int i = 0; i < newSiblings.Count; i++) newSiblings[i].SortOrder = i;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleExpand(int id)
    {
        var bm = _items.FirstOrDefault(b => b.Id == id);
        if (bm == null) return;
        bm.IsExpanded = !bm.IsExpanded;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Returns true if nodeId is a descendant of ancestorId
    private bool IsDescendant(int ancestorId, int nodeId)
    {
        var visited = new HashSet<int>();
        int? cur = nodeId;
        while (cur.HasValue)
        {
            if (!visited.Add(cur.Value)) break;
            if (cur.Value == ancestorId) return true;
            cur = _items.FirstOrDefault(b => b.Id == cur.Value)?.ParentId;
        }
        return false;
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
