using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace HoloPDFCreator.Services;

/// <summary>Per-page OCR data needed to build the invisible text layer.</summary>
public record OcrPageData(List<OcrRegion> Regions, int ImgWidth, int ImgHeight);

public static class SearchablePdfService
{
    /// <summary>
    /// Returns true if the PDF contains a searchable text layer on any page.
    /// </summary>
    public static bool HasTextLayer(string pdfPath)
    {
        try
        {
            using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
            for (int i = 0; i < doc.PageCount; i++)
            {
                if (PageHasTextContent(doc.Pages[i])) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool PageHasTextContent(PdfPage page)
    {
        foreach (var data in EnumerateContentStreams(page))
        {
            if (StreamContainsBT(data)) return true;
        }
        return false;
    }

    private static IEnumerable<byte[]> EnumerateContentStreams(PdfPage page)
    {
        var contents = page.Elements["/Contents"];
        if (contents == null) yield break;

        PdfObject? resolved = contents is PdfReference r ? r.Value : contents as PdfObject;

        if (resolved is PdfArray arr)
        {
            foreach (var item in arr.Elements)
            {
                if (item is PdfReference refItem && refItem.Value is PdfDictionary d && d.Stream != null)
                    yield return d.Stream.UnfilteredValue;
            }
        }
        else if (resolved is PdfDictionary dict && dict.Stream != null)
        {
            yield return dict.Stream.UnfilteredValue;
        }
    }

    private static bool StreamContainsBT(byte[] data) =>
        Encoding.Latin1.GetString(data).Contains("BT");

    /// <summary>
    /// Returns true if the stream contains text operators (BT) but no image invocation (Do).
    /// Such streams are safe to strip when replacing the text layer.
    /// </summary>
    private static bool IsTextOnlyStream(byte[] data)
    {
        var s = Encoding.Latin1.GetString(data);
        if (!s.Contains("BT")) return false;
        for (int i = 1; i < s.Length - 1; i++)
        {
            if (s[i] == 'D' && s[i + 1] == 'o'
                && char.IsWhiteSpace(s[i - 1])
                && (i + 2 >= s.Length || char.IsWhiteSpace(s[i + 2])))
                return false; // has Do operator → image/XObject present, not text-only
        }
        return true;
    }

    /// <summary>
    /// Removes content streams that consist only of a text layer (BT…ET, no Do/image ops).
    /// Called before embedding new OCR results when replacing an existing text layer.
    /// </summary>
    private static void StripTextOnlyStreams(PdfPage page)
    {
        var contents = page.Elements["/Contents"];
        if (contents == null) return;

        PdfObject? resolved = contents is PdfReference r ? r.Value : contents as PdfObject;

        if (resolved is PdfArray arr)
        {
            for (int i = arr.Elements.Count - 1; i >= 0; i--)
            {
                if (arr.Elements[i] is PdfReference refItem &&
                    refItem.Value is PdfDictionary d &&
                    d.Stream != null &&
                    IsTextOnlyStream(d.Stream.UnfilteredValue))
                {
                    arr.Elements.RemoveAt(i);
                }
            }
        }
        else if (resolved is PdfDictionary dict && dict.Stream != null)
        {
            if (IsTextOnlyStream(dict.Stream.UnfilteredValue))
                page.Elements.Remove("/Contents");
        }
    }

    /// <summary>
    /// Appends an invisible text layer to each page of an already-open <see cref="PdfDocument"/>.
    /// </summary>
    public static void ApplyTextLayer(
        PdfDocument doc,
        IReadOnlyDictionary<int, OcrPageData> pageData)
    {
        foreach (var (pageIdx, data) in pageData.OrderBy(kv => kv.Key))
        {
            if (pageIdx >= doc.PageCount || data.Regions.Count == 0) continue;
            var page = doc.Pages[pageIdx];
            EnsureFontResource(doc, page);
            byte[] bytes = BuildContentStream(data, page.Width.Point, page.Height.Point, page.Rotate);
            AppendContentStream(doc, page, bytes);
        }
    }

    /// <summary>
    /// Opens <paramref name="inputPath"/>, appends an invisible text layer to each page
    /// that has OCR results, and saves to <paramref name="outputPath"/>.
    /// When <paramref name="replaceExistingText"/> is true, any existing text-only
    /// content streams are stripped before the new OCR layer is applied.
    /// </summary>
    public static void Save(
        string inputPath,
        string outputPath,
        IReadOnlyDictionary<int, OcrPageData> pageData,
        bool replaceExistingText = false)
    {
        using var doc = PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify);

        if (replaceExistingText)
        {
            for (int i = 0; i < doc.PageCount; i++)
                StripTextOnlyStreams(doc.Pages[i]);
        }

        foreach (var (pageIdx, data) in pageData.OrderBy(kv => kv.Key))
        {
            if (pageIdx >= doc.PageCount || data.Regions.Count == 0) continue;

            var page  = doc.Pages[pageIdx];
            int rot   = page.Rotate;            // 0 / 90 / 180 / 270
            double pW = page.Width.Point;
            double pH = page.Height.Point;

            EnsureFontResource(doc, page);

            byte[] bytes = BuildContentStream(data, pW, pH, rot);
            AppendContentStream(doc, page, bytes);
        }

        doc.Save(outputPath);
    }

    // ── Font resource ─────────────────────────────────────────────────────────

    private const string FontKey = "/HoloPdfOcrF";

    // Identity ToUnicode CMap: maps every CID directly to the same Unicode code point.
    // This lets the PDF viewer copy/search the correct Unicode text for any script.
    private const string ToUnicodeCMap =
        "/CIDInit /ProcSet findresource begin\n" +
        "12 dict begin\n" +
        "begincmap\n" +
        "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
        "/CMapName /Adobe-Identity-UCS def\n" +
        "/CMapType 2 def\n" +
        "1 begincodespacerange\n" +
        "<0000> <FFFF>\n" +
        "endcodespacerange\n" +
        "1 beginbfrange\n" +
        "<0000> <FFFF> <0000>\n" +
        "endbfrange\n" +
        "endcmap\n" +
        "CMapName currentdict /CMap defineresource pop\n" +
        "end\n" +
        "end\n";

    private static void EnsureFontResource(PdfDocument doc, PdfPage page)
    {
        var res = page.Elements.GetDictionary("/Resources");
        if (res == null)
        {
            res = new PdfDictionary(doc);
            page.Elements["/Resources"] = res;
        }

        var fonts = res.Elements.GetDictionary("/Font");
        if (fonts == null)
        {
            fonts = new PdfDictionary(doc);
            res.Elements["/Font"] = fonts;
        }

        if (fonts.Elements.ContainsKey(FontKey)) return;

        // ToUnicode CMap stream (identity mapping for entire Unicode BMP)
        var toUnicode = new PdfDictionary(doc);
        toUnicode.CreateStream(Encoding.ASCII.GetBytes(ToUnicodeCMap));
        doc.Internals.AddObject(toUnicode);

        // FontDescriptor — Ascent=1000, Descent=0 so text fills box from baseline to top
        var fontDesc = new PdfDictionary(doc);
        fontDesc.Elements.SetName("/Type",           "/FontDescriptor");
        fontDesc.Elements.SetName("/FontName",       "/HoloPdfOcrCID");
        fontDesc.Elements.SetInteger("/Flags",       4);
        var bboxArr = new PdfArray(doc);
        bboxArr.Elements.Add((PdfItem)new PdfInteger(-1000));
        bboxArr.Elements.Add((PdfItem)new PdfInteger(-500));
        bboxArr.Elements.Add((PdfItem)new PdfInteger(2000));
        bboxArr.Elements.Add((PdfItem)new PdfInteger(1500));
        fontDesc.Elements["/FontBBox"]               = bboxArr;
        fontDesc.Elements.SetInteger("/ItalicAngle", 0);
        fontDesc.Elements.SetInteger("/Ascent",      1000);
        fontDesc.Elements.SetInteger("/Descent",     0);
        fontDesc.Elements.SetInteger("/CapHeight",   1000);
        fontDesc.Elements.SetInteger("/StemV",       80);
        doc.Internals.AddObject(fontDesc);

        // CIDSystemInfo (inline)
        var cidSysInfo = new PdfDictionary(doc);
        cidSysInfo.Elements.SetString("/Registry",    "Adobe");
        cidSysInfo.Elements.SetString("/Ordering",    "Identity");
        cidSysInfo.Elements.SetInteger("/Supplement", 0);

        // CIDFont: DW=1000 means each character is exactly 1 em wide (monospace em-box)
        var cidFont = new PdfDictionary(doc);
        cidFont.Elements.SetName("/Type",            "/Font");
        cidFont.Elements.SetName("/Subtype",         "/CIDFontType2");
        cidFont.Elements.SetName("/BaseFont",        "/HoloPdfOcrCID");
        cidFont.Elements["/CIDSystemInfo"]           = cidSysInfo;
        cidFont.Elements.SetInteger("/DW",           1000);
        cidFont.Elements["/FontDescriptor"]          = fontDesc.Reference;
        doc.Internals.AddObject(cidFont);

        // DescendantFonts array (indirect ref to CIDFont)
        var descendants = new PdfArray(doc);
        descendants.Elements.Add(cidFont.Reference!);

        // Type0 composite font with Identity-H encoding (2-byte CID = Unicode code point)
        var fDef = new PdfDictionary(doc);
        fDef.Elements.SetName("/Type",     "/Font");
        fDef.Elements.SetName("/Subtype",  "/Type0");
        fDef.Elements.SetName("/BaseFont", "/HoloPdfOcrCID");
        fDef.Elements.SetName("/Encoding", "/Identity-H");
        fDef.Elements["/DescendantFonts"] = descendants;
        fDef.Elements["/ToUnicode"]       = toUnicode.Reference;

        fonts.Elements[FontKey] = fDef;
    }

    // ── Content stream ────────────────────────────────────────────────────────

    // OCR bounding boxes include vertical padding around the actual glyphs.
    // Reducing the font size to this fraction of the box height and centering it
    // produces a selection rectangle that matches the visible text more closely.
    private const double TextHeightScale = 0.43;

    private static byte[] BuildContentStream(
        OcrPageData data, double pW, double pH, int rotation)
    {
        var sb = new StringBuilder();
        sb.Append("q\nBT\n3 Tr\n");   // save state, begin text, invisible rendering mode

        foreach (var r in data.Regions)
        {
            if (string.IsNullOrWhiteSpace(r.Text) || r.Points.Length == 0) continue;

            string textHex = EncodeTextHex(r.Text);
            if (textHex.Length <= 2) continue; // "<>" = no printable characters

            double minXPx = r.Points.Min(p => p.X);
            double maxXPx = r.Points.Max(p => p.X);
            double minYPx = r.Points.Min(p => p.Y);
            double maxYPx = r.Points.Max(p => p.Y);

            double vwPx = maxXPx - minXPx;  // visual width in pixels
            double vhPx = maxYPx - minYPx;  // visual height in pixels
            if (vwPx <= 0 || vhPx <= 0) continue;

            double blXPx = minXPx;
            double blYPx = maxYPx;  // bottom of region in image coords (largest Y)

            var (x, y, w, h, ta, tb, tc, td) = MapToPage(
                blXPx, blYPx, vwPx, vhPx,
                data.ImgWidth, data.ImgHeight, pW, pH, rotation);

            if (h < 0.5) continue;

            // Scale font height down and center text within the OCR bounding box.
            // (tc, td) is the font's ascent direction in PDF user space; shifting the
            // baseline by half the height difference moves text to the vertical center.
            double hFull  = h;
            h             = hFull * TextHeightScale;
            double center = (hFull - h) / 2.0;
            x += tc * center;
            y += td * center;

            // With DW=1000, each character advances exactly 1 em = h points.
            // Tz scales the entire line to match the detected bounding-box width.
            double nomW = r.Text.Length * h;
            double hz = nomW > 0 ? Math.Clamp(w / nomW * 100.0, 10, 2000) : 100.0;

            sb.Append($"{FontKey} {h:F2} Tf\n");
            sb.Append($"{hz:F2} Tz\n");
            sb.Append($"{ta:F4} {tb:F4} {tc:F4} {td:F4} {x:F2} {y:F2} Tm\n");
            sb.Append(textHex);
            sb.Append(" Tj\n");
        }

        sb.Append("ET\nQ\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ── Coordinate transform ──────────────────────────────────────────────────

    /// <summary>
    /// Maps a region's bottom-left pixel + size to PDF page space.
    /// The text baseline is placed at the bottom of the OCR bounding box.
    /// With Ascent=1000 and Descent=0, the text fills exactly the OCR box height.
    /// </summary>
    private static (double x, double y, double w, double h,
                    double ta, double tb, double tc, double td)
        MapToPage(double blXPx, double blYPx, double vwPx, double vhPx,
                  int imgW, int imgH, double pW, double pH, int rotation)
    {
        double sX = (rotation is 90 or 270) ? pH / imgW : pW / imgW;
        double sY = (rotation is 90 or 270) ? pW / imgH : pH / imgH;

        double x, y, w, h;
        double ta, tb, tc, td;

        switch (rotation)
        {
            case 90:   // image vx→PDF -y, vy→PDF x
                x  = pW * (1.0 - blYPx / imgH);
                y  = pH * (1.0 - (blXPx + vwPx) / imgW);
                w  = vhPx * sY;
                h  = vwPx * sX;
                ta = 0; tb = 1; tc = -1; td = 0;
                break;

            case 180:  // both axes flipped
                x  = pW * (1.0 - (blXPx + vwPx) / imgW);
                y  = pH * blYPx / imgH;
                w  = vwPx * sX;
                h  = vhPx * sY;
                ta = -1; tb = 0; tc = 0; td = -1;
                break;

            case 270:  // image vx→PDF y, vy→PDF -x
                x  = pW * blYPx / imgH;
                y  = pH * blXPx / imgW;
                w  = vhPx * sY;
                h  = vwPx * sX;
                ta = 0; tb = -1; tc = 1; td = 0;
                break;

            default:   // 0° (standard orientation)
                // x = left edge of box; y = bottom of box in PDF coords (Y-up)
                // With Ascent=1000/Descent=0 the glyph box extends from y to y+h,
                // which exactly matches the OCR bounding box in PDF space.
                x  = blXPx * sX;
                y  = pH - blYPx * sY;
                w  = vwPx * sX;
                h  = vhPx * sY;
                ta = 1; tb = 0; tc = 0; td = 1;
                break;
        }

        return (x, y, w, h, ta, tb, tc, td);
    }

    // ── Append stream to page ─────────────────────────────────────────────────

    private static void AppendContentStream(PdfDocument doc, PdfPage page, byte[] bytes)
    {
        var streamObj = new PdfDictionary(doc);
        streamObj.CreateStream(bytes);
        doc.Internals.AddObject(streamObj);

        var contents = page.Elements["/Contents"];

        if (contents == null)
        {
            page.Elements["/Contents"] = streamObj.Reference;
            return;
        }

        PdfObject? resolved = contents is PdfReference r ? r.Value : contents as PdfObject;

        if (resolved is PdfArray arr)
        {
            arr.Elements.Add(streamObj.Reference!);
        }
        else
        {
            var array = new PdfArray(doc);
            if (contents is PdfReference existingRef)
                array.Elements.Add(existingRef);
            array.Elements.Add(streamObj.Reference!);
            page.Elements["/Contents"] = array;
        }
    }

    // ── String helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes text as a PDF hex string using UTF-16BE (2 bytes per BMP code point).
    /// Compatible with Identity-H Type0 fonts — each 2-byte value is the CID,
    /// which equals the Unicode code point via the identity ToUnicode CMap.
    /// </summary>
    private static string EncodeTextHex(string text)
    {
        var sb = new StringBuilder("<");
        foreach (char c in text)
        {
            if (c < 0x20 && c != '\t') continue;  // skip control characters
            sb.Append(((int)c).ToString("X4"));
        }
        sb.Append(">");
        return sb.ToString();
    }
}
