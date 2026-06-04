using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using PigPdf = UglyToad.PdfPig;

namespace HoloPDFCreator.Services;

/// <summary>
/// Copies searchable-text, bookmarks (outlines), and annotations
/// from a source PDF file into an already-open destination PdfDocument.
/// </summary>
public static class PdfMetaCopier
{
    /// <param name="includeText">
    /// When true (default), extracts the source PDF's text layer via PdfPig and re-applies it.
    /// Pass false when you intend to apply a different text layer (e.g. fresh OCR results) yourself.
    /// </param>
    public static void CopyMeta(string sourcePath, PdfDocument dstDoc, bool includeText = true)
    {
        // 1. Searchable text
        if (includeText)
        {
            var textData = ExtractTextData(sourcePath, dstDoc);
            if (textData.Count > 0)
                SearchablePdfService.ApplyTextLayer(dstDoc, textData);
        }

        // 2. Outlines and annotations via PdfSharp
        try
        {
            using var srcDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
            CopyOutlines(srcDoc, dstDoc);
            CopyAnnotations(srcDoc, dstDoc);
        }
        catch { }
    }

    // ── Text layer ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the existing text layer of <paramref name="sourcePath"/> and returns
    /// word-level <see cref="OcrRegion"/> lists per page, with coordinates scaled
    /// to a rendered image <paramref name="renderWidth"/> pixels wide.
    /// Returns an empty dictionary when there is no text or extraction fails.
    /// </summary>
    public static Dictionary<int, List<OcrRegion>> LoadTextRegions(
        string sourcePath, int renderWidth = 1200)
    {
        var result = new Dictionary<int, List<OcrRegion>>();
        try
        {
            using var pig = PigPdf.PdfDocument.Open(sourcePath);
            for (int i = 0; i < pig.NumberOfPages; i++)
            {
                var page  = pig.GetPage(i + 1);
                var words = NearestNeighbourWordExtractor.Instance
                                .GetWords(page.Letters).ToList();
                if (words.Count == 0) continue;

                double pW    = page.MediaBox.Bounds.Width;
                double pH    = page.MediaBox.Bounds.Height;
                double scale = renderWidth / pW;

                var regions = new List<OcrRegion>(words.Count);
                foreach (var w in words)
                {
                    if (string.IsNullOrWhiteSpace(w.Text)) continue;
                    var b = w.BoundingBox;
                    int left   = (int)Math.Round(b.Left          * scale);
                    int top    = (int)Math.Round((pH - b.Top)    * scale);
                    int right  = (int)Math.Round(b.Right         * scale);
                    int bottom = (int)Math.Round((pH - b.Bottom) * scale);
                    if (right <= left || bottom <= top) continue;
                    regions.Add(new OcrRegion
                    {
                        Text   = w.Text,
                        Score  = 1f,
                        Points = [
                            new OcrPoint(left,  top),
                            new OcrPoint(right, top),
                            new OcrPoint(right, bottom),
                            new OcrPoint(left,  bottom),
                        ]
                    });
                }
                if (regions.Count > 0)
                    result[i] = regions;
            }
        }
        catch { }
        return result;
    }

    private static Dictionary<int, OcrPageData> ExtractTextData(string sourcePath, PdfDocument dstDoc)
    {
        var result = new Dictionary<int, OcrPageData>();
        try
        {
            using var pig = PigPdf.PdfDocument.Open(sourcePath);
            int total = Math.Min(pig.NumberOfPages, dstDoc.PageCount);
            for (int i = 0; i < total; i++)
            {
                var page = pig.GetPage(i + 1);
                var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).ToList();
                if (words.Count == 0) continue;

                double pW = page.MediaBox.Bounds.Width;
                double pH = page.MediaBox.Bounds.Height;
                int imgW = (int)Math.Max(1, Math.Round(pW));
                int imgH = (int)Math.Max(1, Math.Round(pH));

                var regions = new List<OcrRegion>(words.Count);
                foreach (var w in words)
                {
                    if (string.IsNullOrWhiteSpace(w.Text)) continue;
                    var b = w.BoundingBox;
                    // PdfPig uses PDF space (Y-up, origin at bottom-left).
                    // OcrPageData uses pixel space (Y-down, origin at top-left).
                    // With ImgWidth=pW and ImgHeight=pH the scale factor is 1:1 pt→px,
                    // so we just Y-flip: pixelY = pH - pdfY.
                    int left   = (int)Math.Round(b.Left);
                    int top    = (int)Math.Round(pH - b.Top);     // top of box in px space
                    int right  = (int)Math.Round(b.Right);
                    int bottom = (int)Math.Round(pH - b.Bottom);  // bottom of box in px space
                    if (right <= left || bottom <= top) continue;
                    regions.Add(new OcrRegion
                    {
                        Text   = w.Text,
                        Score  = 1f,
                        Points = [
                            new OcrPoint(left,  top),
                            new OcrPoint(right, top),
                            new OcrPoint(right, bottom),
                            new OcrPoint(left,  bottom),
                        ]
                    });
                }

                if (regions.Count > 0)
                    result[i] = new OcrPageData(regions, imgW, imgH);
            }
        }
        catch { }
        return result;
    }

    // ── Outlines (bookmarks) ───────────────────────────────────────────────────

    private static void CopyOutlines(PdfDocument srcDoc, PdfDocument dstDoc)
    {
        try { CopyOutlineLevel(srcDoc.Outlines, dstDoc.Outlines, srcDoc, dstDoc); }
        catch { }
    }

    private static void CopyOutlineLevel(
        PdfOutlineCollection src,
        PdfOutlineCollection dst,
        PdfDocument srcDoc,
        PdfDocument dstDoc)
    {
        foreach (var o in src)
        {
            int pageIdx = FindPageIndex(o.DestinationPage, srcDoc);
            PdfPage? dstPage = (pageIdx >= 0 && pageIdx < dstDoc.PageCount)
                ? dstDoc.Pages[pageIdx]
                : (dstDoc.PageCount > 0 ? dstDoc.Pages[0] : null);

            if (dstPage == null) continue;

            try
            {
                var added = dst.Add(o.Title ?? "", dstPage, o.Opened, o.Style);
                if (o.Outlines.Count > 0)
                    CopyOutlineLevel(o.Outlines, added.Outlines, srcDoc, dstDoc);
            }
            catch { }
        }
    }

    private static int FindPageIndex(PdfPage? page, PdfDocument srcDoc)
    {
        if (page == null) return -1;
        for (int i = 0; i < srcDoc.PageCount; i++)
        {
            if (ReferenceEquals(srcDoc.Pages[i], page)) return i;
        }
        return -1;
    }

    // ── Annotations ────────────────────────────────────────────────────────────

    private static void CopyAnnotations(PdfDocument srcDoc, PdfDocument dstDoc)
    {
        int count = Math.Min(srcDoc.PageCount, dstDoc.PageCount);
        for (int i = 0; i < count; i++)
        {
            try { CopyPageAnnotations(srcDoc.Pages[i], dstDoc.Pages[i], dstDoc); }
            catch { }
        }
    }

    private static void CopyPageAnnotations(PdfPage srcPage, PdfPage dstPage, PdfDocument dstDoc)
    {
        var annots = srcPage.Elements["/Annots"];
        if (annots == null) return;

        PdfObject? resolved = annots is PdfReference r ? r.Value : annots as PdfObject;
        if (resolved is not PdfArray srcArr || srcArr.Elements.Count == 0) return;

        var dstArr = new PdfArray(dstDoc);
        dstPage.Elements["/Annots"] = dstArr;

        foreach (var item in srcArr.Elements)
        {
            PdfDictionary? srcDict = item is PdfReference pr
                ? pr.Value as PdfDictionary
                : item as PdfDictionary;
            if (srcDict == null) continue;

            // Copy only simple (non-reference) values to avoid cross-document reference issues.
            // This handles the common cases: /Subtype, /Rect, /Contents, /T, /M, /C, /Open.
            var copy = new PdfDictionary(dstDoc);
            bool hasRect = false;
            foreach (var key in srcDict.Elements.KeyNames)
            {
                if (key == "/P" || key == "/AP") continue; // skip page ref + appearance streams
                var val = srcDict.Elements[key];
                if (val is PdfReference) continue;         // skip indirect references
                if (val is PdfArray arr && ContainsReference(arr)) continue;
                copy.Elements[key] = val;
                if (key == "/Rect") hasRect = true;
            }
            if (!hasRect) continue; // annotations without /Rect are invalid

            dstDoc.Internals.AddObject(copy);
            dstArr.Elements.Add(copy.Reference!);
        }
    }

    private static bool ContainsReference(PdfArray arr)
    {
        foreach (var el in arr.Elements)
            if (el is PdfReference) return true;
        return false;
    }
}
