using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace HoloPDFCreator.Services;

/// <summary>One outline (bookmark) entry read from a PDF; PageIndex is 0-based, -1 when unresolved.</summary>
public sealed record OutlineNode(
    string Title, int PageIndex, bool Open, PdfOutlineStyle Style, List<OutlineNode> Children);

/// <summary>
/// Reads the document outline straight from the /Outlines dictionaries.
/// PDFsharp's <c>PdfDocument.Outlines</c> throws ("Destination Array expected") as soon as
/// one entry uses a named destination, which loses the whole outline, so this walks the
/// tree itself and resolves explicit, named (/Dests, /Names) and GoTo-action destinations.
/// </summary>
public static class PdfOutlineReader
{
    public static List<OutlineNode> Read(PdfDocument doc)
    {
        var root = Resolve(doc.Internals.Catalog.Elements["/Outlines"]) as PdfDictionary;
        return root == null ? new() : ReadLevel(doc, root, new HashSet<PdfDictionary>(), 0);
    }

    private static List<OutlineNode> ReadLevel(
        PdfDocument doc, PdfDictionary parent, HashSet<PdfDictionary> seen, int depth)
    {
        var result = new List<OutlineNode>();
        if (depth > 64) return result;

        var item = Resolve(parent.Elements["/First"]) as PdfDictionary;
        while (item != null && seen.Add(item))   // guard against /Next cycles
        {
            var dest = item.Elements["/Dest"];
            if (dest == null && Resolve(item.Elements["/A"]) is PdfDictionary action)
                dest = action.Elements["/D"];

            string title = (Resolve(item.Elements["/Title"]) as PdfString)?.Value ?? "(untitled)";
            int    flags = item.Elements.GetInteger("/F");

            result.Add(new OutlineNode(
                title,
                ResolveDestinationPage(doc, dest, 0),
                item.Elements.GetInteger("/Count") > 0,
                (PdfOutlineStyle)(flags & 3),
                ReadLevel(doc, item, seen, depth + 1)));

            item = Resolve(item.Elements["/Next"]) as PdfDictionary;
        }
        return result;
    }

    /// <summary>Returns the 0-based page index a destination points to, or -1.</summary>
    public static int ResolveDestinationPage(PdfDocument doc, PdfItem? dest, int depth)
    {
        if (dest is null || depth > 4) return -1;
        dest = Resolve(dest);

        switch (dest)
        {
            case PdfArray arr when arr.Elements.Count > 0:
                var first = arr.Elements[0];
                if (first is PdfReference pageRef)
                {
                    for (int i = 0; i < doc.PageCount; i++)
                        if (doc.Pages[i].Reference?.ObjectID == pageRef.ObjectID) return i;
                }
                else if (first is PdfInteger idx)
                {
                    return idx.Value;
                }
                return -1;

            case PdfDictionary dict when dict.Elements.ContainsKey("/D"):
                return ResolveDestinationPage(doc, dict.Elements["/D"], depth + 1);

            case PdfName name:
                var dests = Resolve(doc.Internals.Catalog.Elements["/Dests"]) as PdfDictionary;
                return ResolveDestinationPage(doc, dests?.Elements[name.Value], depth + 1);

            case PdfString str:
                var names = Resolve(doc.Internals.Catalog.Elements["/Names"]) as PdfDictionary;
                var tree  = Resolve(names?.Elements["/Dests"]) as PdfDictionary;
                return ResolveDestinationPage(doc, FindInNameTree(tree, str.Value, 0), depth + 1);
        }
        return -1;
    }

    private static PdfItem? FindInNameTree(PdfDictionary? node, string key, int depth)
    {
        if (node is null || depth > 32) return null;
        if (Resolve(node.Elements["/Names"]) is PdfArray names)
        {
            for (int i = 0; i + 1 < names.Elements.Count; i += 2)
                if (Resolve(names.Elements[i]) is PdfString k && k.Value == key)
                    return names.Elements[i + 1];
        }
        if (Resolve(node.Elements["/Kids"]) is PdfArray kids)
        {
            foreach (var kid in kids.Elements)
            {
                var hit = FindInNameTree(Resolve(kid) as PdfDictionary, key, depth + 1);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    /// <summary>
    /// Empties the outline so it can be rebuilt through <c>doc.Outlines</c>. The raw
    /// /Outlines entry is dropped first, otherwise PDFsharp would try to parse it (and
    /// throw on named destinations) the first time <c>doc.Outlines</c> is touched.
    /// </summary>
    public static PdfOutlineCollection ResetOutlines(PdfDocument doc)
    {
        doc.Internals.Catalog.Elements.Remove("/Outlines");
        doc.Outlines.Clear();
        return doc.Outlines;
    }
}
