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
    /// Thickens dark strokes by applying morphological erosion (minimum filter).
    /// radius: 1–5 pixels.
    /// </summary>
    public static Bitmap ThickenStrokes(Bitmap source, int radius)
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

    // ─── Private Helpers ──────────────────────────────────────────────────────

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
