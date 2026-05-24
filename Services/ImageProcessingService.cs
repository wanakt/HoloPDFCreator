using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HoloPDFCreator.Services;

public static class ImageProcessingService
{
    // ─── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Adjusts brightness. brightness: -1.0 (black) to +1.0 (white).
    /// </summary>
    public static Bitmap AdjustBrightness(Bitmap source, float brightness)
    {
        int add = (int)(brightness * 255f);
        var data = GetPixels(source);
        int len = data.Length;

        for (int i = 0; i < len; i += 4)
        {
            data[i]     = Clamp(data[i]     + add); // B
            data[i + 1] = Clamp(data[i + 1] + add); // G
            data[i + 2] = Clamp(data[i + 2] + add); // R
        }

        return FromPixels(data, source.Width, source.Height);
    }

    /// <summary>
    /// Adjusts contrast. contrast: -1.0 (flat grey) to +1.0 (maximum contrast).
    /// </summary>
    public static Bitmap AdjustContrast(Bitmap source, float contrast)
    {
        float c = contrast * 255f;
        float factor = (259f * (c + 255f)) / (255f * (259f - c));

        var data = GetPixels(source);
        int len = data.Length;

        for (int i = 0; i < len; i += 4)
        {
            data[i]     = Clamp((int)(factor * (data[i]     - 128) + 128));
            data[i + 1] = Clamp((int)(factor * (data[i + 1] - 128) + 128));
            data[i + 2] = Clamp((int)(factor * (data[i + 2] - 128) + 128));
        }

        return FromPixels(data, source.Width, source.Height);
    }

    /// <summary>
    /// Thickens dark strokes using morphological erosion.
    /// radius supports fractions (e.g. 0.3, 1.5): blends adjacent integer results.
    /// </summary>
    public static Bitmap ThickenStrokes(Bitmap source, float radius)
    {
        if (radius <= 0f) return new Bitmap(source);

        int   r1   = (int)radius;
        float frac = radius - r1;

        if (frac < 0.01f)
            return r1 == 0 ? new Bitmap(source) : ErodeInt(source, r1);

        // Blend between erosion at r1 and r1+1 for smooth sub-integer steps.
        using var lo = r1 == 0 ? new Bitmap(source) : ErodeInt(source, r1);
        using var hi = ErodeInt(source, r1 + 1);
        return BlendBitmaps(lo, hi, frac);
    }

    private static Bitmap ErodeInt(Bitmap source, int radius)
    {
        int w = source.Width;
        int h = source.Height;
        int stride = w * 4;

        var src = GetPixels(source);
        var dst = new byte[src.Length];

        for (int y = 0; y < h; y++)
        {
            int yBase = y * stride;
            for (int x = 0; x < w; x++)
            {
                int idx = yBase + x * 4;
                int minB = 255, minG = 255, minR = 255;
                byte alpha = src[idx + 3];

                int yMin = Math.Max(0, y - radius);
                int yMax = Math.Min(h - 1, y + radius);
                int xMin = Math.Max(0, x - radius);
                int xMax = Math.Min(w - 1, x + radius);

                for (int ny = yMin; ny <= yMax; ny++)
                {
                    int nyBase = ny * stride;
                    for (int nx = xMin; nx <= xMax; nx++)
                    {
                        int ni = nyBase + nx * 4;
                        if (src[ni]     < minB) minB = src[ni];
                        if (src[ni + 1] < minG) minG = src[ni + 1];
                        if (src[ni + 2] < minR) minR = src[ni + 2];
                    }
                }

                dst[idx]     = (byte)minB;
                dst[idx + 1] = (byte)minG;
                dst[idx + 2] = (byte)minR;
                dst[idx + 3] = alpha;
            }
        }

        return FromPixels(dst, w, h);
    }

    private static Bitmap BlendBitmaps(Bitmap a, Bitmap b, float t)
    {
        var da = GetPixels(a);
        var db = GetPixels(b);
        var result = new byte[da.Length];
        float s = 1f - t;
        for (int i = 0; i < da.Length; i++)
            result[i] = (byte)(da[i] * s + db[i] * t);
        return FromPixels(result, a.Width, a.Height);
    }

    /// <summary>
    /// Stretches histogram per channel so the darkest pixel becomes 0
    /// and the brightest becomes 255. Improves washed-out or low-contrast scans.
    /// </summary>
    public static Bitmap AutoLevel(Bitmap source)
    {
        var data = GetPixels(source);
        int len = data.Length;

        int minB = 255, maxB = 0;
        int minG = 255, maxG = 0;
        int minR = 255, maxR = 0;

        for (int i = 0; i < len; i += 4)
        {
            byte b = data[i], g = data[i + 1], r = data[i + 2];
            if (b < minB) minB = b; if (b > maxB) maxB = b;
            if (g < minG) minG = g; if (g > maxG) maxG = g;
            if (r < minR) minR = r; if (r > maxR) maxR = r;
        }

        float rangeB = maxB > minB ? maxB - minB : 1f;
        float rangeG = maxG > minG ? maxG - minG : 1f;
        float rangeR = maxR > minR ? maxR - minR : 1f;

        for (int i = 0; i < len; i += 4)
        {
            data[i]     = Clamp((int)((data[i]     - minB) * 255f / rangeB));
            data[i + 1] = Clamp((int)((data[i + 1] - minG) * 255f / rangeG));
            data[i + 2] = Clamp((int)((data[i + 2] - minR) * 255f / rangeR));
        }

        return FromPixels(data, source.Width, source.Height);
    }

    /// <summary>
    /// Sharpens the image using unsharp masking.
    /// amount 0 = no change; 1 = moderate; 3+ = strong.
    /// </summary>
    public static Bitmap Sharpen(Bitmap source, float amount)
    {
        if (amount <= 0f) return new Bitmap(source);

        var srcPixels = GetPixels(source);
        using var blurred = BoxBlur(source, 1);
        var blrPixels = GetPixels(blurred);

        var dst = new byte[srcPixels.Length];
        for (int i = 0; i < srcPixels.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                float orig  = srcPixels[i + c];
                float blur  = blrPixels[i + c];
                dst[i + c]  = Clamp((int)(orig + amount * (orig - blur)));
            }
            dst[i + 3] = srcPixels[i + 3];
        }

        return FromPixels(dst, source.Width, source.Height);
    }

    // ─── Private Helpers ──────────────────────────────────────────────────────

    private static Bitmap BoxBlur(Bitmap source, int radius)
    {
        int w = source.Width, h = source.Height, stride = w * 4;
        var src = GetPixels(source);
        var tmp = new byte[src.Length];
        var dst = new byte[src.Length];

        // Horizontal pass
        for (int y = 0; y < h; y++)
        {
            int yOff = y * stride;
            for (int x = 0; x < w; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0, count = 0;
                for (int kx = Math.Max(0, x - radius); kx <= Math.Min(w - 1, x + radius); kx++)
                {
                    int ni = yOff + kx * 4;
                    sumB += src[ni]; sumG += src[ni + 1]; sumR += src[ni + 2]; count++;
                }
                int idx = yOff + x * 4;
                tmp[idx] = (byte)(sumB / count); tmp[idx + 1] = (byte)(sumG / count);
                tmp[idx + 2] = (byte)(sumR / count); tmp[idx + 3] = src[idx + 3];
            }
        }

        // Vertical pass
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0, count = 0;
                for (int ky = Math.Max(0, y - radius); ky <= Math.Min(h - 1, y + radius); ky++)
                {
                    int ni = ky * stride + x * 4;
                    sumB += tmp[ni]; sumG += tmp[ni + 1]; sumR += tmp[ni + 2]; count++;
                }
                int idx = y * stride + x * 4;
                dst[idx] = (byte)(sumB / count); dst[idx + 1] = (byte)(sumG / count);
                dst[idx + 2] = (byte)(sumR / count); dst[idx + 3] = tmp[idx + 3];
            }
        }

        return FromPixels(dst, w, h);
    }

    private static byte[] GetPixels(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[Math.Abs(bmpData.Stride) * bitmap.Height];
        Marshal.Copy(bmpData.Scan0, bytes, 0, bytes.Length);
        bitmap.UnlockBits(bmpData);
        return bytes;
    }

    private static Bitmap FromPixels(byte[] data, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var bmpData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(data, 0, bmpData.Scan0, data.Length);
        bitmap.UnlockBits(bmpData);
        return bitmap;
    }

    private static byte Clamp(int value) => value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;
}
