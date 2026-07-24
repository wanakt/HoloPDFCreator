using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;

namespace HoloPDFCreator.Services;

public readonly record struct OcrPoint(int X, int Y);

// Word-level fragment used for positional interleaving in mixed Korean+Hanja lines.
// Score is the per-word recognition confidence from the OCR model (0–1).
public readonly record struct WordFragment(string Text, int CenterX, float Score = 1f);

public class OcrRegion
{
    public string         Text   { get; set; }  = "";
    public float          Score  { get; init; }
    public OcrPoint[]     Points { get; init; } = [];
    public WordFragment[] Words  { get; init; } = [];
}

public enum OcrLanguage
{
    Latin,
    Korean,
    Chinese,
    Japanese,
}

public enum OcrModelSize { Mobile, Full }

public class OcrService : IDisposable
{
    // Each RapidOcr instance wraps its own ONNX sessions and must never be
    // called concurrently. The queue acts as a slot pool: a caller dequeues
    // one instance, uses it exclusively, then enqueues it back.
    private readonly ConcurrentQueue<RapidOcr> _pool     = new();
    private readonly ConcurrentQueue<RapidOcr> _hanjaPool = new();
    private SemaphoreSlim? _sem;
    private SemaphoreSlim? _hanjaSem;
    private bool           _hanjaEnabled;

    public int          PoolSize         { get; private set; } = 1;
    public int          RequestedWorkers { get; private set; } = 1;
    public OcrLanguage  CurrentLanguage  { get; private set; } = OcrLanguage.Latin;
    public OcrModelSize CurrentModelSize { get; private set; } = OcrModelSize.Mobile;
    public bool         IsReady          => !_pool.IsEmpty;

    private record ModelConfig(string RecFile, string KeysFile, string ModelVer = "v5");

    private static readonly Dictionary<OcrLanguage, ModelConfig> LangModels = new()
    {
        [OcrLanguage.Latin]    = new("latin_PP-OCRv5_rec_mobile_infer.onnx", "ppocrv5_latin_dict.txt"),
        [OcrLanguage.Korean]   = new("korean_PP-OCRv5_rec_mobile.onnx",      "ppocrv5_korean_dict.txt"),
        [OcrLanguage.Chinese]  = new("ch_PP-OCRv5_rec_mobile.onnx",          "ppocrv5_dict.txt"),
        [OcrLanguage.Japanese] = new("japan_PP-OCRv4_rec_mobile.onnx",       "japan_dict.txt", "v4"),
    };

    // ── Initialization ────────────────────────────────────────────────────────

    public async Task InitializeAsync(
        OcrLanguage language = OcrLanguage.Latin,
        IProgress<(int current, int total, string message)>? progress = null,
        CancellationToken ct = default,
        int parallelism = 2,
        OcrModelSize modelSize = OcrModelSize.Mobile)
    {
        DisposePool();

        CurrentLanguage  = language;
        CurrentModelSize = modelSize;
        RequestedWorkers = Math.Max(1, parallelism);
        PoolSize         = RequestedWorkers;

        // Korean mode loads a secondary Chinese model pool for Hanja recognition.
        bool needHanja = (language == OcrLanguage.Korean);
        int steps = (needHanja ? 2 : 1) * PoolSize + 1;
        progress?.Report((1, steps, "OCR 엔진 초기화 중…"));

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var cfg = LangModels[language];

        string detPath, clsPath, recPath, keysPath;
        bool useCustomPaths;

        clsPath = Path.Combine(baseDir, "models", "v5", "ch_ppocr_mobile_v2.0_cls_infer.onnx");

        if (modelSize == OcrModelSize.Full)
        {
            detPath        = Path.Combine(baseDir, "models", "v5", "full", "PP-OCRv5_server_det_infer.onnx");
            useCustomPaths = true;
            if (language == OcrLanguage.Chinese || language == OcrLanguage.Latin)
            {
                recPath  = Path.Combine(baseDir, "models", "v5", "full", "PP-OCRv5_server_rec_infer.onnx");
                keysPath = Path.Combine(baseDir, "models", "v5", "ppocrv5_dict.txt");
            }
            else
            {
                recPath  = Path.Combine(baseDir, "models", cfg.ModelVer, cfg.RecFile);
                keysPath = Path.Combine(baseDir, "models", cfg.ModelVer, cfg.KeysFile);
            }
        }
        else
        {
            detPath        = Path.Combine(baseDir, "models", "v5", "ch_PP-OCRv5_mobile_det.onnx");
            recPath        = Path.Combine(baseDir, "models", cfg.ModelVer, cfg.RecFile);
            keysPath       = Path.Combine(baseDir, "models", cfg.ModelVer, cfg.KeysFile);
            useCustomPaths = language != OcrLanguage.Latin;
        }

        if (useCustomPaths && !File.Exists(recPath))
            throw new FileNotFoundException($"OCR model not found: {recPath}");

        using var cpuOpts = BuildCpuOptions(PoolSize);
        for (int i = 0; i < PoolSize; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ocr = await Task.Run(() =>
            {
                var inst = new RapidOcr();
                if (useCustomPaths) inst.InitModels(detPath, clsPath, recPath, keysPath, cpuOpts);
                else                inst.InitModels(cpuOpts);
                return inst;
            }, ct).ConfigureAwait(false);
            _pool.Enqueue(ocr);
            progress?.Report((i + 2, steps, $"워커 {i + 1}/{PoolSize} 완료"));
        }

        _sem = new SemaphoreSlim(PoolSize, PoolSize);

        // Secondary Hanja pool: recognises traditional Chinese characters (正字/번체자)
        // that the Korean dict cannot produce.
        // Uses chinese_cht_PP-OCRv3 (Traditional Chinese, 8,077 CJK characters) which
        // outputs full-form characters matching Korean Hanja (國, 經, 學 …).
        // The Simplified Chinese model (ch_PP-OCRv5) must NOT be used here because it
        // maps traditional forms to simplified output (國→国, 經→经, etc.).
        _hanjaEnabled = needHanja;
        if (needHanja)
        {
            string hanjaDetPath  = detPath;   // same detection model as primary pass
            string hanjaRecPath  = Path.Combine(baseDir, "models", "v3", "chinese_cht_PP-OCRv3_rec_mobile.onnx");
            string hanjaKeysPath = Path.Combine(baseDir, "models", "v3", "chinese_cht_dict.txt");

            if (!File.Exists(hanjaRecPath))
                throw new FileNotFoundException($"Traditional Chinese model not found: {hanjaRecPath}");

            for (int i = 0; i < PoolSize; i++)
            {
                ct.ThrowIfCancellationRequested();
                var hanjaOcr = await Task.Run(() =>
                {
                    var inst = new RapidOcr();
                    inst.InitModels(hanjaDetPath, clsPath, hanjaRecPath, hanjaKeysPath, cpuOpts);
                    return inst;
                }, ct).ConfigureAwait(false);
                _hanjaPool.Enqueue(hanjaOcr);
                progress?.Report((PoolSize + i + 2, steps, $"한자 모델 {i + 1}/{PoolSize} 로드"));
            }
            _hanjaSem = new SemaphoreSlim(PoolSize, PoolSize);
        }

        progress?.Report((steps, steps, $"준비 완료 (워커 {PoolSize}개)"));
    }

    private static SessionOptions BuildCpuOptions(int poolSize)
    {
        var opts = new SessionOptions();
        // Spread the physical cores evenly across the worker pool so the whole CPU
        // is used in every scenario:
        //   • 1 page  → poolSize 1 → all cores on that single image (fast interactive).
        //   • N pages → poolSize N → one core-share each, run in parallel.
        // Either way total threads ≈ ProcessorCount, maximising throughput. (This
        // trades away the old "1 thread per session" CPU-throttle behaviour, which
        // left most cores idle for single-page / low-worker Korean OCR.)
        opts.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / Math.Max(1, poolSize));
        return opts;
    }

    // ── Inference ─────────────────────────────────────────────────────────────

    public async Task<List<OcrRegion>> RunOcrAsync(
        BitmapSource source, CancellationToken ct = default)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        using var skBmp = await Task.Run(() => ToSKBitmap(source), ct).ConfigureAwait(false);
        return await RunOcrRawAsync(skBmp, w, h, ct).ConfigureAwait(false);
    }

    public async Task<List<OcrRegion>> RunOcrAsync(
        System.Drawing.Bitmap bitmap, CancellationToken ct = default)
    {
        int w = bitmap.Width, h = bitmap.Height;
        using var skBmp = await Task.Run(() => ToSKBitmap(bitmap), ct).ConfigureAwait(false);
        return await RunOcrRawAsync(skBmp, w, h, ct).ConfigureAwait(false);
    }

    // ReturnWordBox splits each recognized line into word-level results using
    // CTC time-column positions, which is the only reliable way to recover
    // spaces for Latin models whose character dictionary omits the space glyph.
    private static readonly RapidOcrOptions _ocrOptions =
        RapidOcrOptions.Default with { ReturnWordBox = true };

    // The detection model (ch_PP-OCRv5_mobile_det) is Chinese-trained, so it
    // assigns lower confidence scores to Korean (Hangul) regions.
    // Thresholds are pushed lower than the Chinese/Latin defaults so the model
    // can find Hangul regions whose probability map scores are inherently weaker.
    // ImgResize is bumped to 2560 so the model-internal resize never shrinks the
    // upscaled input; MaxSideLen is set high so it doesn't act as a hard cap.
    private static readonly RapidOcrOptions _koreanOcrOptions =
        RapidOcrOptions.Default with
        {
            ReturnWordBox  = true,
            BoxScoreThresh = 0.1f,
            BoxThresh      = 0.05f,
            TextScore      = 0.2f,
            UnClipRatio    = 2.0f,
            MaxSideLen     = 4096,
            ImgResize      = 2560,
        };

    // Korean images are pre-upscaled so each Hangul syllable block reaches at
    // least ~20-24 px — the minimum the Chinese detector needs.  Coordinates are
    // scaled back to the original pixel space so overlays align with the display.
    public int KoreanUpscaleTarget { get; set; } = 2560;

    // Chinese model options for the Hanja secondary pass.  Same relaxed thresholds
    // as Korean so the det model has equal sensitivity when run on the same image.
    // ReturnWordBox=true is required so MergeHanjaRegions can access per-word X
    // positions for correct positional interleaving with Korean words.
    private static readonly RapidOcrOptions _hanjaOcrOptions =
        RapidOcrOptions.Default with
        {
            ReturnWordBox  = true,
            BoxScoreThresh = 0.1f,
            BoxThresh      = 0.05f,
            TextScore      = 0.2f,
            UnClipRatio    = 2.0f,
            MaxSideLen     = 4096,
            ImgResize      = 2560,
        };

    private RapidOcrOptions ActiveOptions =>
        CurrentLanguage == OcrLanguage.Korean ? _koreanOcrOptions : _ocrOptions;

    public async Task<List<OcrRegion>> RunOcrRawAsync(
        SKBitmap skBmp, int imgW, int imgH, CancellationToken ct = default)
    {
        if (_sem == null || _pool.IsEmpty)
            throw new InvalidOperationException("OCR engine not initialized.");

        bool insertSpaces = CurrentLanguage == OcrLanguage.Latin;
        var options = ActiveOptions;

        SKBitmap input = skBmp;
        float invScale = 1f;
        if (CurrentLanguage == OcrLanguage.Korean)
        {
            // Step 1: upscale so each Hangul syllable block reaches ≥20 px for the detector.
            SKBitmap upscaled = skBmp;
            int longSide = Math.Max(skBmp.Width, skBmp.Height);
            if (longSide > 0 && longSide < KoreanUpscaleTarget)
            {
                float s = (float)KoreanUpscaleTarget / longSide;
                int nw = (int)(skBmp.Width  * s);
                int nh = (int)(skBmp.Height * s);
                upscaled = new SKBitmap(nw, nh, skBmp.ColorType, skBmp.AlphaType);
                invScale = 1f / s;
                using var c = new SKCanvas(upscaled);
                c.DrawBitmap(skBmp, new SKRect(0, 0, nw, nh));
            }
            // Step 2: boost contrast so the Chinese det model scores Hangul regions higher.
            input = EnhanceContrast(upscaled);
            if (!ReferenceEquals(upscaled, skBmp)) upscaled.Dispose();
        }

        await _sem.WaitAsync(ct).ConfigureAwait(false);
        _pool.TryDequeue(out var ocr);
        try
        {
            var regions = await Task.Run(() =>
            {
                var result = ocr!.Detect(input, options);
                // If Detect returns null instead of throwing (some DML failures are silent),
                // throw explicitly so the GPU-fallback catch in the caller can handle it.
                if (result is null || result.TextBlocks is null)
                    throw new InvalidOperationException("Detect returned null result.");
                return MapResult(result, input.Width, input.Height, insertSpaces);
            }, ct).ConfigureAwait(false);

            // Korean gap-fill: the Chinese det model misses some Hangul rows even at
            // very low thresholds.  Re-run detection on narrow crops for each uncovered
            // text band so that Hangul becomes the dominant content of the crop and
            // the model scores it high enough to detect.
            if (CurrentLanguage == OcrLanguage.Korean)
            {
                var extra = await Task.Run(
                    () => FillKoreanGaps(input, regions, ocr!, options), ct)
                    .ConfigureAwait(false);
                regions.AddRange(extra);
            }

            // Hanja secondary pass: run the Hanja model ONLY on image bands not already
            // covered by the Korean OCR results.  Running it on the full image caused
            // the model to mis-read Hangul syllables as CJK glyphs and inject spurious
            // characters into Korean text.  By restricting it to uncovered rows the
            // Hanja model only ever sees actual Hanja content.
            if (_hanjaEnabled && _hanjaSem != null)
            {
                await _hanjaSem.WaitAsync(ct).ConfigureAwait(false);
                _hanjaPool.TryDequeue(out var hanjaOcr);
                try
                {
                    var hanjaRegions = await Task.Run(
                        () => FillHanjaGaps(input, regions, hanjaOcr!, _hanjaOcrOptions), ct)
                        .ConfigureAwait(false);
                    MergeHanjaIntoRegions(regions, hanjaRegions);
                }
                finally
                {
                    _hanjaPool.Enqueue(hanjaOcr!);
                    _hanjaSem.Release();
                }
            }

            // Map coordinates back to the original (pre-upscale) pixel space.
            if (invScale != 1f)
            {
                for (int i = 0; i < regions.Count; i++)
                {
                    var r = regions[i];
                    regions[i] = new OcrRegion
                    {
                        Text   = r.Text,
                        Score  = r.Score,
                        Points = r.Points
                            .Select(p => new OcrPoint(
                                (int)MathF.Round(p.X * invScale),
                                (int)MathF.Round(p.Y * invScale)))
                            .ToArray(),
                        Words  = r.Words
                            .Select(w => new WordFragment(
                                w.Text,
                                (int)MathF.Round(w.CenterX * invScale),
                                w.Score))
                            .ToArray(),
                    };
                }
            }
            return regions;
        }
        finally
        {
            if (!ReferenceEquals(input, skBmp)) input.Dispose();
            _pool.Enqueue(ocr!);
            _sem.Release();
        }
    }

    private static List<OcrRegion> MapResult(
        OcrResult result, int imgW, int imgH, bool insertSpaces)
    {
        var regions = new List<OcrRegion>(result.TextBlocks.Length);
        foreach (var block in result.TextBlocks)
        {
            // For Latin: reconstruct text from word-level results so that spaces
            // between words are preserved even when the dict has no space glyph.
            string text;
            if (insertSpaces && block.WordResults is { Length: > 0 } words)
                text = string.Join(" ", words.Select(w => w.Text));
            else
                text = block.Text;

            if (string.IsNullOrWhiteSpace(text)) continue;

            // Capture per-word X-centre positions and recognition scores for
            // Korean↔Hanja interleaving and confidence-based false-positive filtering.
            WordFragment[] wordFragments = block.WordResults is { Length: > 0 } wb
                ? wb.Select(w => new WordFragment(
                      w.Text,
                      w.BoxPoints.Length > 0
                          ? (int)w.BoxPoints.Average(p => (double)p.X)
                          : block.BoxPoints.Length > 0
                              ? (int)block.BoxPoints.Average(p => (double)p.X)
                              : 0,
                      w.Score))
                  .ToArray()
                : [];

            regions.Add(new OcrRegion
            {
                Text   = text,
                Score  = block.BoxScore,
                Points = block.BoxPoints.Select(p => new OcrPoint(p.X, p.Y)).ToArray(),
                Words  = wordFragments,
            });
        }
        return regions;
    }

    // ── Image conversion ──────────────────────────────────────────────────────

    public static SKBitmap ConvertToSKBitmap(System.Drawing.Bitmap bmp) => ToSKBitmap(bmp);
    public static SKBitmap ConvertToSKBitmap(BitmapSource src)           => ToSKBitmap(src);

    private static SKBitmap ToSKBitmap(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;

        // Fast path: Bgra32 / Pbgra32 can be copied directly into SKBitmap pixel buffer.
        if (src.Format == System.Windows.Media.PixelFormats.Bgra32 ||
            src.Format == System.Windows.Media.PixelFormats.Pbgra32)
        {
            var alphaType = src.Format == System.Windows.Media.PixelFormats.Pbgra32
                ? SKAlphaType.Premul : SKAlphaType.Unpremul;
            var skBmp = new SKBitmap(w, h, SKColorType.Bgra8888, alphaType);
            src.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h),
                           skBmp.GetPixels(), skBmp.ByteCount, w * 4);
            return EnsureOpaqueBgra(skBmp);
        }

        // Fallback: BMP encode + decode for other formats.
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        var decoded = SKBitmap.Decode(ms)
                      ?? throw new InvalidOperationException("SKBitmap.Decode returned null.");
        return EnsureOpaqueBgra(decoded);
    }

    private static unsafe SKBitmap ToSKBitmap(System.Drawing.Bitmap bmp)
    {
        var skBmp = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var data  = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bmp.Width * 4;
            byte* src = (byte*)data.Scan0;
            byte* dst = (byte*)skBmp.GetPixels().ToPointer();
            if (data.Stride == rowBytes)
                Buffer.MemoryCopy(src, dst, skBmp.ByteCount, skBmp.ByteCount);
            else
                for (int y = 0; y < bmp.Height; y++)
                    Buffer.MemoryCopy(src + y * data.Stride, dst + y * rowBytes, rowBytes, rowBytes);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return skBmp;
    }

    // Validates dimensions and composites any semi-transparent pixels onto white.
    // SkiaSharp always decodes to Bgra8888 internally; when the source has real
    // alpha the premultiplied values would corrupt the ONNX float normalisation.
    private static SKBitmap EnsureOpaqueBgra(SKBitmap src)
    {
        if (src.Width <= 0 || src.Height <= 0)
            throw new InvalidOperationException(
                $"Decoded image has invalid dimensions {src.Width}×{src.Height}.");

        if (src.AlphaType == SKAlphaType.Opaque)
            return src;

        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(src, 0, 0);
        src.Dispose();
        return dst;
    }

    // ── Hanja gap-fill ────────────────────────────────────────────────────────────────────────────

    // Runs the Traditional Chinese (Hanja) model on the full image and keeps only
    // results that represent genuine Hanja, not Korean syllables mis-read as CJK.
    //
    // Strategy: word-level confidence score.
    //   Genuine Hanja recognition  ─── score near 1.0, typically > 0.80
    //   Korean syllable mis-read   ─── model is guessing; score usually < 0.70
    //
    // Running on the full image (rather than narrow crops) avoids the problem of
    // the detection model being unable to locate text in small sub-crops whose
    // long side is only a fraction of ImgResize.
    private static List<OcrRegion> FillHanjaGaps(
        SKBitmap img, List<OcrRegion> existing, RapidOcr ocr, RapidOcrOptions options)
    {
        var result = ocr.Detect(img, options);
        if (result?.TextBlocks is null) return [];

        // ── Per-word score threshold ──────────────────────────────────────────
        // Genuine Hanja : score ≥ 0.80  (Traditional Chinese model is confident)
        // Korean misread: score < 0.70  (model is guessing at unfamiliar glyphs)
        const float MinWordScore = 0.80f;

        // ── Korean-region index for overlap checking ──────────────────────────
        // A Hanja block that overlaps a Korean region where the Korean recogniser
        // produced "enough" text is skipped (the Korean model already did the job).
        //
        // Fill-ratio = (recognisedChars × charHeight) / bboxWidth.
        //   ≈ 1.0  → recogniser read roughly one character per pixel-column: good Korean.
        //   ≪ 0.5  → recogniser produced far fewer chars than the width implies:
        //             it was probably garbling Hanja it doesn't know.
        //
        // hangulRatio is NOT used because the Korean dictionary contains only Hangul,
        // so every Korean-OCR result is 100% Hangul regardless of the input script.
        const float MinFillRatio = 0.50f;
        var korBoxes = existing
            .Where(r => r.Points.Length > 0 && r.Text.Length > 0)
            .Select(r =>
            {
                int ry0 = r.Points.Min(p => p.Y), ry1 = r.Points.Max(p => p.Y);
                int rx0 = r.Points.Min(p => p.X), rx1 = r.Points.Max(p => p.X);
                int charH    = Math.Max(8, ry1 - ry0);
                int textLen  = r.Text.Count(c => c != ' ');
                int bboxW    = Math.Max(1, rx1 - rx0);
                float fill   = (float)(textLen * charH) / bboxW;
                return (y0: ry0, y1: ry1, x0: rx0, x1: rx1, fill);
            })
            .ToList();

        var extra = new List<OcrRegion>();

        foreach (var block in result.TextBlocks)
        {
            if (block.BoxPoints.Length == 0) continue;

            int bX0 = block.BoxPoints.Min(p => p.X), bX1 = block.BoxPoints.Max(p => p.X);
            int bY0 = block.BoxPoints.Min(p => p.Y), bY1 = block.BoxPoints.Max(p => p.Y);
            int bW  = Math.Max(1, bX1 - bX0);

            // Skip if a Korean region with sufficient Hangul content overlaps this block.
            // This prevents the Hanja model from shadowing Korean OCR output.
            bool coveredByKorean = korBoxes.Any(k =>
                k.fill >= MinFillRatio &&
                Math.Max(bY0, k.y0) <= Math.Min(bY1, k.y1) &&
                (float)Math.Max(0, Math.Min(bX1, k.x1) - Math.Max(bX0, k.x0)) / bW >= 0.3f);

            if (coveredByKorean) continue;

            if (block.WordResults is { Length: > 0 } words)
            {
                var kept = words
                    .Where(w => w.Score >= MinWordScore && !string.IsNullOrEmpty(w.Text))
                    .ToList();
                if (kept.Count == 0) continue;

                extra.Add(new OcrRegion
                {
                    Text   = string.Join("", kept.Select(w => w.Text)),
                    Score  = (float)kept.Average(w => (double)w.Score),
                    Points = block.BoxPoints.Select(p => new OcrPoint(p.X, p.Y)).ToArray()
                });
            }
            else
            {
                if (block.BoxScore < MinWordScore || string.IsNullOrWhiteSpace(block.Text)) continue;
                extra.Add(new OcrRegion
                {
                    Text   = block.Text,
                    Score  = block.BoxScore,
                    Points = block.BoxPoints.Select(p => new OcrPoint(p.X, p.Y)).ToArray()
                });
            }
        }

        return extra;
    }

    // Merges Hanja results into the Korean regions list.
    //
    // When a Hanja block's bounding box overlaps a Korean region (≥ 30 % in X),
    // the Hanja text is appended to that Korean region's text instead of being
    // added as a separate region.  Without this, both "라." and "政府" would
    // occupy the exact same bounding box in the PDF and text selection would
    // always return whichever one the hit-test found first.
    //
    // Hanja blocks with no overlapping Korean region are added as new regions
    // (pure-Hanja lines that the Korean model never detected).
    private static void MergeHanjaIntoRegions(List<OcrRegion> regions, List<OcrRegion> hanja)
    {
        foreach (var h in hanja)
        {
            if (h.Points.Length == 0 || string.IsNullOrWhiteSpace(h.Text)) continue;

            int hX0 = h.Points.Min(p => p.X), hX1 = h.Points.Max(p => p.X);
            int hY0 = h.Points.Min(p => p.Y), hY1 = h.Points.Max(p => p.Y);
            int hW  = Math.Max(1, hX1 - hX0);

            int bestIdx     = -1;
            float bestOvlap = 0f;
            for (int i = 0; i < regions.Count; i++)
            {
                var k = regions[i];
                if (k.Points.Length == 0) continue;
                int kY0 = k.Points.Min(p => p.Y), kY1 = k.Points.Max(p => p.Y);
                if (Math.Max(hY0, kY0) > Math.Min(hY1, kY1)) continue;  // no Y overlap

                int kX0 = k.Points.Min(p => p.X), kX1 = k.Points.Max(p => p.X);
                float xOvlap = (float)Math.Max(0, Math.Min(hX1, kX1) - Math.Max(hX0, kX0)) / hW;
                if (xOvlap > bestOvlap) { bestOvlap = xOvlap; bestIdx = i; }
            }

            if (bestIdx >= 0 && bestOvlap >= 0.3f)
                regions[bestIdx].Text += " " + h.Text;
            else
                regions.Add(h);
        }
    }

    // ── Korean gap-fill ───────────────────────────────────────────────────────

    // After the full-image Korean detection pass, some Hangul rows score below
    // the detector threshold because the Chinese-trained model treats them as
    // low-confidence.  This method:
    //  1. Marks every row already covered by an existing detected region.
    //  2. Uses horizontal projection to find uncovered rows that contain dark pixels.
    //  3. Groups adjacent such rows into bands (tolerating small intra-glyph gaps).
    //  4. Crops each band (+ padding) and re-runs full OCR.
    //     When Hangul fills the entire crop the model scores it reliably.
    //  5. Returns the additional OcrRegions in `input`-image coordinate space.
    private static List<OcrRegion> FillKoreanGaps(
        SKBitmap img, List<OcrRegion> existing, RapidOcr ocr, RapidOcrOptions options)
    {
        int h = img.Height, w = img.Width;

        // ── 1. Build per-row coverage mask ──────────────────────────────────
        var covered = new bool[h];
        foreach (var r in existing)
        {
            if (r.Points.Length == 0) continue;
            int y0 = Math.Max(0, r.Points.Min(p => p.Y));
            int y1 = Math.Min(h - 1, r.Points.Max(p => p.Y));
            for (int y = y0; y <= y1; y++) covered[y] = true;
        }

        // ── 2. Horizontal projection on uncovered rows ──────────────────────
        var pixels  = img.Pixels;
        var hasDark = new bool[h];
        int minDark = Math.Max(3, w / 400);   // ≈0.25 % of row width
        for (int y = 0; y < h; y++)
        {
            if (covered[y]) continue;
            int dark = 0;
            for (int x = 0; x < w; x += 4)
            {
                var c = pixels[y * w + x];
                if ((c.Red * 77 + c.Green * 150 + c.Blue * 29) >> 8 < 128) dark++;
            }
            hasDark[y] = dark >= minDark;
        }

        // ── 3. Group dark rows into bands ───────────────────────────────────
        const int GapTol   = 6;   // intra-band gap tolerance (inter-stroke pixels)
        const int MinBandH = 8;   // drop bands thinner than this (border artefacts)
        var bands   = new List<(int top, int bottom)>();
        int? start  = null;
        int lastDark = -99;
        for (int y = 0; y < h; y++)
        {
            if (hasDark[y])
            {
                start    ??= y;
                lastDark   = y;
            }
            else if (start.HasValue && y - lastDark > GapTol)
            {
                if (lastDark - start.Value + 1 >= MinBandH)
                    bands.Add((start.Value, lastDark + 1));
                start = null;
            }
        }
        if (start.HasValue && lastDark - start.Value + 1 >= MinBandH)
            bands.Add((start.Value, lastDark + 1));

        if (bands.Count == 0) return [];

        // ── 4. Run OCR on each crop ─────────────────────────────────────────
        const int Pad = 10;
        var extra = new List<OcrRegion>();
        foreach (var (bandTop, bandBottom) in bands)
        {
            int cropTop = Math.Max(0, bandTop - Pad);
            int cropBot = Math.Min(h, bandBottom + Pad);
            int cropH   = cropBot - cropTop;

            using var crop = new SKBitmap(w, cropH, img.ColorType, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(crop))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(img, new SKRect(0, cropTop, w, cropBot),
                                  new SKRect(0, 0, w, cropH));
            }

            var result = ocr.Detect(crop, options);
            if (result?.TextBlocks is null) continue;

            foreach (var block in result.TextBlocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text)) continue;
                extra.Add(new OcrRegion
                {
                    Text   = block.Text,
                    Score  = block.BoxScore,
                    Points = block.BoxPoints
                                 .Select(p => new OcrPoint(p.X, p.Y + cropTop))
                                 .ToArray()
                });
            }
        }
        return extra;
    }

    // ── Image preprocessing ───────────────────────────────────────────────────

    // Applies a simple contrast stretch (pivot = mid-gray) to make text darker
    // and background whiter.  factor=1.35 is mild enough not to saturate thin
    // strokes, but strong enough to push borderline Hangul regions above the
    // detection model's probability threshold.
    private static SKBitmap EnhanceContrast(SKBitmap src, float factor = 1.35f)
    {
        float offset = (1f - factor) / 2f;   // pivot at 0.5 → keeps mid-gray stable
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint
        {
            ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
            {
                factor, 0,      0,      0, offset,
                0,      factor, 0,      0, offset,
                0,      0,      factor, 0, offset,
                0,      0,      0,      1, 0
            })
        };
        canvas.DrawBitmap(src, 0, 0, paint);
        return dst;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void DisposePool()
    {
        _sem?.Dispose();
        _sem = null;
        while (_pool.TryDequeue(out var ocr)) ocr.Dispose();

        _hanjaSem?.Dispose();
        _hanjaSem    = null;
        _hanjaEnabled = false;
        while (_hanjaPool.TryDequeue(out var ocr)) ocr.Dispose();
    }

    public void InvalidateSessions() => DisposePool();

    public void Dispose() => DisposePool();
}
