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
    public string    Text   { get; init; } = "";
    public float     Score  { get; init; }
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

    public int          PoolSize          { get; private set; } = 1;
    public int          RequestedWorkers  { get; private set; } = 1;
    public OcrLanguage  CurrentLanguage   { get; private set; } = OcrLanguage.Latin;
    public OcrModelSize CurrentModelSize  { get; private set; } = OcrModelSize.Mobile;
    public bool         UseGpu            { get; private set; } = false;
    public string?      GpuFallbackReason { get; private set; }
    public bool         IsReady           => !_pool.IsEmpty;

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
        bool useGpu = false,
        OcrModelSize modelSize = OcrModelSize.Mobile)
    {
        DisposePool();

        CurrentLanguage   = language;
        CurrentModelSize  = modelSize;
        UseGpu            = useGpu;
        RequestedWorkers  = Math.Max(1, parallelism);
        GpuFallbackReason = null;
        PoolSize          = RequestedWorkers;

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

        SessionOptions? gpuOpts = null;
        if (useGpu)
        {
            try
            {
                gpuOpts = BuildGpuOptions();
                progress?.Report((1, steps, "GPU (DirectML) 초기화 성공"));
            }
            catch (Exception ex)
            {
                UseGpu            = false;
                GpuFallbackReason = ex.Message;
                progress?.Report((1, steps, "GPU 초기화 실패 — CPU로 전환"));
            }
        }

        SessionOptions? cpuOpts = UseGpu ? null : BuildCpuOptions();

        for (int i = 0; i < PoolSize; i++)
        {
            ct.ThrowIfCancellationRequested();
            SessionOptions? opts = gpuOpts ?? cpuOpts;
            var ocr = await Task.Run(() =>
            {
                var instance = new RapidOcr();
                if (useCustomPaths)
                {
                    if (opts != null) instance.InitModels(detPath, clsPath, recPath, keysPath, opts);
                    else              instance.InitModels(detPath, clsPath, recPath, keysPath);
                }
                else
                {
                    if (opts != null) instance.InitModels(opts);
                    else              instance.InitModels();
                }
                return instance;
            }, ct).ConfigureAwait(false);
            _pool.Enqueue(ocr);
            progress?.Report((i + 2, steps, $"워커 {i + 1}/{PoolSize} 초기화 완료"));
        }

        _sem = new SemaphoreSlim(PoolSize, PoolSize);
        gpuOpts?.Dispose();
        cpuOpts?.Dispose();
        progress?.Report((steps, steps, $"준비 완료 (워커 {PoolSize}개, {(UseGpu ? "GPU" : "CPU")})"));
    }

    private static SessionOptions BuildGpuOptions()
    {
        var opts = new SessionOptions();
        opts.EnableMemoryPattern = false;
        opts.IntraOpNumThreads   = 1;
        opts.AppendExecutionProvider_DML(0);
        return opts;
    }

    private static SessionOptions BuildCpuOptions()
    {
        var opts = new SessionOptions();
        opts.IntraOpNumThreads = 1;
        opts.InterOpNumThreads = 1;
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

    public async Task<List<OcrRegion>> RunOcrRawAsync(
        SKBitmap skBmp, int imgW, int imgH, CancellationToken ct = default)
    {
        if (_sem == null || _pool.IsEmpty)
            throw new InvalidOperationException("OCR engine not initialized.");

        bool insertSpaces = CurrentLanguage == OcrLanguage.Latin;

        await _sem.WaitAsync(ct).ConfigureAwait(false);
        _pool.TryDequeue(out var ocr);
        try
        {
            return await Task.Run(() =>
            {
                var result = ocr!.Detect(skBmp, _ocrOptions);
                return MapResult(result, imgW, imgH, insertSpaces);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
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
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        return SKBitmap.Decode(ms)
               ?? throw new InvalidOperationException("SKBitmap.Decode returned null.");
    }

    private static SKBitmap ToSKBitmap(System.Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
        ms.Position = 0;
        return SKBitmap.Decode(ms)
               ?? throw new InvalidOperationException("SKBitmap.Decode returned null.");
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void DisposePool()
    {
        _sem?.Dispose();
        _sem = null;
        while (_pool.TryDequeue(out var ocr)) ocr.Dispose();
    }

    public void Dispose() => DisposePool();
}
