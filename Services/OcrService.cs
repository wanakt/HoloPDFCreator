using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;

namespace HoloPDFCreator.Services;

public readonly record struct OcrPoint(int X, int Y);

public class OcrRegion
{
    public string     Text   { get; set; }  = "";
    public float      Score  { get; init; }
    public OcrPoint[] Points { get; init; } = [];
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
    private readonly ConcurrentQueue<RapidOcr> _pool = new();
    private SemaphoreSlim? _sem;

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

        int steps = PoolSize + 1;
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
        progress?.Report((steps, steps, $"준비 완료 (워커 {PoolSize}개)"));
    }

    private static SessionOptions BuildCpuOptions(int poolSize)
    {
        var opts = new SessionOptions();
        opts.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / poolSize);
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
    // ImgResize is bumped to 2560 so the model-internal resize never shrinks the
    // upscaled input; MaxSideLen is set high so it doesn't act as a hard cap.
    private static readonly RapidOcrOptions _koreanOcrOptions =
        RapidOcrOptions.Default with
        {
            ReturnWordBox  = true,
            BoxScoreThresh = 0.2f,
            BoxThresh      = 0.1f,
            TextScore      = 0.3f,
            UnClipRatio    = 2.0f,
            MaxSideLen     = 4096,
            ImgResize      = 2560,
        };

    // Korean images are pre-upscaled so each Hangul syllable block reaches at
    // least ~20-24 px — the minimum the Chinese detector needs.  Coordinates are
    // scaled back to the original pixel space so overlays align with the display.
    public int KoreanUpscaleTarget { get; set; } = 2560;

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
            int longSide = Math.Max(skBmp.Width, skBmp.Height);
            if (longSide > 0 && longSide < KoreanUpscaleTarget)
            {
                float s = (float)KoreanUpscaleTarget / longSide;
                int nw = (int)(skBmp.Width  * s);
                int nh = (int)(skBmp.Height * s);
                input    = new SKBitmap(nw, nh, skBmp.ColorType, skBmp.AlphaType);
                invScale = 1f / s;
                using var c = new SKCanvas(input);
                c.DrawBitmap(skBmp, new SKRect(0, 0, nw, nh));
            }
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
                            .ToArray()
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
            regions.Add(new OcrRegion
            {
                Text   = text,
                Score  = block.BoxScore,
                Points = block.BoxPoints
                              .Select(p => new OcrPoint(p.X, p.Y))
                              .ToArray()
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

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void DisposePool()
    {
        _sem?.Dispose();
        _sem = null;
        while (_pool.TryDequeue(out var ocr)) ocr.Dispose();
    }

    public void InvalidateSessions() => DisposePool();

    public void Dispose() => DisposePool();
}
