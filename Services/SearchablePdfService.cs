using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace HoloPDFCreator.Services;

/// <summary>
/// Per-page OCR data needed to build the invisible text layer. Regions are in pixels of
/// an ImgWidth × ImgHeight image of the page. <paramref name="Geometry"/> is the page's
/// rotation and visible box when that image was rendered; when set, the text layer is
/// placed by it, so rotating or cropping the page afterwards keeps the text in place.
/// Null means "the page as it is now" (e.g. a page built from the image itself).
/// </summary>
public record OcrPageData(List<OcrRegion> Regions, int ImgWidth, int ImgHeight,
                          PageGeometry? Geometry = null);

/// <summary>Visible page area in unrotated user space (points).</summary>
public readonly record struct PdfPageBox(double X, double Y, double Width, double Height);

/// <summary>How a page is displayed: its /Rotate and its visible box.</summary>
public readonly record struct PageGeometry(int Rotation, PdfPageBox Box);

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

    // Text objects: BT … ET as whole tokens.
    private static readonly System.Text.RegularExpressions.Regex TextObject = new(
        @"(?<![^\s\[\]()<>/{}])BT(?![^\s\[\]()<>/{}]).*?(?<![^\s\[\]()<>/{}])ET(?![^\s\[\]()<>/{}])",
        System.Text.RegularExpressions.RegexOptions.Singleline);

    // What may stand outside the text objects of an invisible text layer.
    private static readonly System.Text.RegularExpressions.Regex LayerFiller = new(
        @"^(?:\s|q|Q|cm|[-+]?\d*\.?\d+|%[^\n]*)*$");

    /// <summary>
    /// True for a content stream that is only an invisible text layer and can be replaced
    /// by new OCR results: ours (font /HoloPdfOcrF), or one whose every text object uses
    /// render mode 3 (invisible) with nothing but q/Q/cm around it. Anything that draws —
    /// visible text, paths, images — is kept; when unsure the stream is kept.
    /// </summary>
    private static bool IsInvisibleTextLayer(byte[] data)
    {
        var s = Encoding.Latin1.GetString(data);
        if (s.Contains(FontKey + " ")) return true;

        var objects = TextObject.Matches(s);
        if (objects.Count == 0) return false;
        foreach (System.Text.RegularExpressions.Match m in objects)
            if (!System.Text.RegularExpressions.Regex.IsMatch(m.Value, @"(?<![\d.])3\s+Tr\b") ||
                System.Text.RegularExpressions.Regex.IsMatch(m.Value, @"(?<![\d.])[0-24-7]\s+Tr\b"))
                return false;
        return LayerFiller.IsMatch(TextObject.Replace(s, " "));
    }

    /// <summary>
    /// Removes the page's invisible text-layer streams (see <see cref="IsInvisibleTextLayer"/>).
    /// Called before embedding new OCR results when replacing an existing text layer.
    /// </summary>
    private static void StripTextLayerStreams(PdfPage page)
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
                    IsInvisibleTextLayer(d.Stream.UnfilteredValue))
                {
                    arr.Elements.RemoveAt(i);
                }
            }
        }
        else if (resolved is PdfDictionary dict && dict.Stream != null)
        {
            if (IsInvisibleTextLayer(dict.Stream.UnfilteredValue))
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
            var supp = new Dictionary<int, int>();
            byte[] bytes = BuildContentStream(data, data.Geometry?.Box ?? VisibleBox(page),
                                              data.Geometry?.Rotation ?? page.Rotate, supp);
            EnsureFontResource(doc, page, supp);
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

        foreach (var (pageIdx, data) in pageData.OrderBy(kv => kv.Key))
        {
            if (pageIdx >= doc.PageCount || data.Regions.Count == 0) continue;

            var page = doc.Pages[pageIdx];
            // Only pages that get new OCR text lose their previous invisible layer.
            if (replaceExistingText) StripTextLayerStreams(page);
            var supp = new Dictionary<int, int>();
            byte[] bytes = BuildContentStream(data, data.Geometry?.Box ?? VisibleBox(page),
                                              data.Geometry?.Rotation ?? page.Rotate, supp);
            EnsureFontResource(doc, page, supp);
            AppendContentStream(doc, page, bytes);
        }

        doc.Save(outputPath);
    }

    // ── Font resource ─────────────────────────────────────────────────────────

    private const string FontKey = "/HoloPdfOcrF";

    // CIDs D800–DFFF never stand for a BMP character (that range is UTF-16
    // surrogates), so characters outside the BMP borrow them, one CID each.
    private const int SuppCidFirst = 0xD800;
    private const int SuppCidCount = 0x800;

    /// <summary>
    /// ToUnicode CMap: every BMP CID maps to the same code point; borrowed surrogate
    /// CIDs map to the supplementary character in <paramref name="supp"/> (code point → CID).
    /// This lets the PDF viewer copy/search the correct Unicode text for any script.
    /// </summary>
    private static string BuildToUnicodeCMap(IReadOnlyDictionary<int, int> supp)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n");
        sb.Append("12 dict begin\n");
        sb.Append("begincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n");
        sb.Append("/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        sb.Append("2 beginbfrange\n<0000> <D7FF> <0000>\n<E000> <FFFF> <E000>\nendbfrange\n");

        // At most 100 entries per bfchar block (PDF spec limit).
        var pairs = supp.OrderBy(kv => kv.Value).ToList();
        for (int i = 0; i < pairs.Count; i += 100)
        {
            var chunk = pairs.Skip(i).Take(100).ToList();
            sb.Append($"{chunk.Count} beginbfchar\n");
            foreach (var (cp, cid) in chunk)
            {
                string utf16 = char.ConvertFromUtf32(cp);
                sb.Append($"<{cid:X4}> <{(int)utf16[0]:X4}{(int)utf16[1]:X4}>\n");
            }
            sb.Append("endbfchar\n");
        }

        sb.Append("endcmap\n");
        sb.Append("CMapName currentdict /CMap defineresource pop\n");
        sb.Append("end\n");
        sb.Append("end\n");
        return sb.ToString();
    }

    private static void EnsureFontResource(PdfDocument doc, PdfPage page, IReadOnlyDictionary<int, int> supp)
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

        // Always (re)built: the ToUnicode map depends on this page's supplementary characters.
        var toUnicode = new PdfDictionary(doc);
        toUnicode.CreateStream(Encoding.ASCII.GetBytes(BuildToUnicodeCMap(supp)));
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
        OcrPageData data, PdfPageBox box, int rotation, Dictionary<int, int> supp)
    {
        var sb = new StringBuilder();
        sb.Append("q\nBT\n3 Tr\n");   // save state, begin text, invisible rendering mode

        foreach (var r in data.Regions)
        {
            if (string.IsNullOrWhiteSpace(r.Text) || r.Points.Length == 0) continue;

            var (textHex, glyphs) = EncodeTextHex(r.Text, supp);
            if (glyphs == 0) continue; // no printable characters

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
                data.ImgWidth, data.ImgHeight, box.Width, box.Height, rotation);
            x += box.X;
            y += box.Y;

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
            double nomW = glyphs * h;
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
    /// The area a viewer shows (and the OCR image covers): CropBox clipped to
    /// MediaBox, or MediaBox when there is no CropBox. Never rotation-adjusted.
    /// </summary>
    public static PdfPageBox VisibleBox(PdfPage page)
    {
        var mb = page.MediaBox;
        double x1 = Math.Min(mb.X1, mb.X2), y1 = Math.Min(mb.Y1, mb.Y2);
        double x2 = Math.Max(mb.X1, mb.X2), y2 = Math.Max(mb.Y1, mb.Y2);
        if (page.Elements.ContainsKey("/CropBox"))
        {
            var cb = page.CropBox;
            double cx1 = Math.Max(x1, Math.Min(cb.X1, cb.X2)), cy1 = Math.Max(y1, Math.Min(cb.Y1, cb.Y2));
            double cx2 = Math.Min(x2, Math.Max(cb.X1, cb.X2)), cy2 = Math.Min(y2, Math.Max(cb.Y1, cb.Y2));
            if (cx2 > cx1 && cy2 > cy1) { x1 = cx1; y1 = cy1; x2 = cx2; y2 = cy2; }
        }
        return new PdfPageBox(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>Rotation and visible box of every page of <paramref name="pdfPath"/>.</summary>
    public static PageGeometry[] ReadPageGeometry(string pdfPath)
    {
        using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        var result = new PageGeometry[doc.PageCount];
        for (int i = 0; i < doc.PageCount; i++)
            result[i] = new PageGeometry(Normalize(doc.Pages[i].Rotate), VisibleBox(doc.Pages[i]));
        return result;
    }

    /// <summary>
    /// Image pixel (x, y) of an imgW × imgH rendering of a page shown as <paramref name="g"/>
    /// → unrotated user space (u, v). Same transform as <see cref="MapToPage"/>.
    /// </summary>
    public static (double U, double V) ImageToUser(double x, double y, int imgW, int imgH, PageGeometry g)
    {
        double nx = x / imgW, ny = y / imgH, W = g.Box.Width, H = g.Box.Height;
        var (u, v) = Normalize(g.Rotation) switch
        {
            90  => (ny * W, nx * H),
            180 => ((1 - nx) * W, ny * H),
            270 => ((1 - ny) * W, (1 - nx) * H),
            _   => (nx * W, (1 - ny) * H),
        };
        return (u + g.Box.X, v + g.Box.Y);
    }

    /// <summary>Inverse of <see cref="ImageToUser"/>.</summary>
    public static (double X, double Y) UserToImage(double u, double v, int imgW, int imgH, PageGeometry g)
    {
        double W = g.Box.Width, H = g.Box.Height;
        double ru = (u - g.Box.X) / W, rv = (v - g.Box.Y) / H;
        var (nx, ny) = Normalize(g.Rotation) switch
        {
            90  => (rv, ru),
            180 => (1 - ru, rv),
            270 => (1 - rv, 1 - ru),
            _   => (ru, 1 - rv),
        };
        return (nx * imgW, ny * imgH);
    }

    private static int Normalize(int rotation) => ((rotation % 360) + 360) % 360;

    /// <summary>
    /// Maps a region's bottom-left pixel + size (in the rendered, rotated image) to
    /// unrotated user space relative to the visible box of size <paramref name="bW"/> × <paramref name="bH"/>.
    /// Returns the baseline origin (x, y), the run length w and glyph height h in points,
    /// and the text direction (ta, tb) and ascent direction (tc, td).
    /// /Rotate turns the page clockwise for display, so with u/v the unrotated
    /// coordinates and nx/ny the normalized image coordinates (Y-down):
    ///   0°: u = nx·W,      v = (1−ny)·H
    ///  90°: u = ny·W,      v = nx·H
    /// 180°: u = (1−nx)·W,  v = ny·H
    /// 270°: u = (1−ny)·W,  v = (1−nx)·H
    /// The text baseline is placed at the bottom of the OCR bounding box.
    /// With Ascent=1000 and Descent=0, the text fills exactly the OCR box height.
    /// </summary>
    private static (double x, double y, double w, double h,
                    double ta, double tb, double tc, double td)
        MapToPage(double blXPx, double blYPx, double vwPx, double vhPx,
                  int imgW, int imgH, double bW, double bH, int rotation)
    {
        double nx  = blXPx / imgW;   // left edge of the box
        double ny  = blYPx / imgH;   // bottom edge of the box (largest Y)
        double nvw = vwPx  / imgW;
        double nvh = vhPx  / imgH;

        return (((rotation % 360) + 360) % 360) switch
        {
            // visual right = +v, visual up = −u
            90  => (ny * bW, nx * bH, nvw * bH, nvh * bW, 0, 1, -1, 0),
            // visual right = −u, visual up = −v
            180 => ((1 - nx) * bW, ny * bH, nvw * bW, nvh * bH, -1, 0, 0, -1),
            // visual right = −v, visual up = +u
            270 => ((1 - ny) * bW, (1 - nx) * bH, nvw * bH, nvh * bW, 0, -1, 1, 0),
            // visual right = +u, visual up = +v
            _   => (nx * bW, (1 - ny) * bH, nvw * bW, nvh * bH, 1, 0, 0, 1),
        };
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
    /// Encodes text as a PDF hex string of 2-byte CIDs for the Identity-H Type0 font.
    /// BMP characters use their code point as CID; characters outside the BMP get a
    /// CID from the surrogate range, recorded in <paramref name="supp"/> for the ToUnicode map.
    /// Returns the hex string and the number of glyphs (CIDs) written.
    /// </summary>
    private static (string Hex, int Glyphs) EncodeTextHex(string text, Dictionary<int, int> supp)
    {
        var sb = new StringBuilder("<");
        int glyphs = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            if (cp < 0x20 && cp != '\t') continue;  // skip control characters

            int cid;
            if (rune.IsBmp)
                cid = cp;
            else if (!supp.TryGetValue(cp, out cid))
            {
                cid = supp.Count < SuppCidCount ? SuppCidFirst + supp.Count : 0xFFFD;
                if (cid != 0xFFFD) supp[cp] = cid;
            }

            sb.Append(cid.ToString("X4"));
            glyphs++;
        }
        sb.Append(">");
        return (sb.ToString(), glyphs);
    }
}
