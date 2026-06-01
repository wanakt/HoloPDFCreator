using SkiaSharp;
using System.Windows.Media.Imaging;

namespace HoloPDFCreator.Services;

public static class DeskewService
{
    private const double MaxAngleDeg  = 10.0;
    private const double AngleStepDeg = 0.5;
    private const int    SampleStep   = 4;

    /// <summary>
    /// Pass 1 – projection-profile rough deskew.
    /// Returns the same instance if correction is negligible (&lt;0.1°).
    /// </summary>
    public static SKBitmap Deskew(SKBitmap src)
    {
        double angle = DetectSkewAngle(src);
        if (Math.Abs(angle) < 0.1) return src;
        return RotateBitmap(src, -angle);
    }

    /// <summary>
    /// Pass 2 – fine correction using the median angle of OCR text-box edges.
    /// Returns the same instance if residual is below 0.05°.
    /// </summary>
    public static SKBitmap RotateByAngle(SKBitmap src, double degrees)
        => Math.Abs(degrees) < 0.05 ? src : RotateBitmap(src, degrees);

    /// <summary>
    /// Computes the median skew angle (degrees) from the top edges of OCR text boxes.
    /// Returns 0 if fewer than 3 usable boxes are available.
    /// </summary>
    public static double ComputeAngleFromRegions(IReadOnlyList<OcrRegion> regions, int imageWidth)
    {
        double minLength = imageWidth * 0.4;
        var angles = new List<double>(regions.Count);
        foreach (var r in regions)
        {
            if (r.Points.Length < 2) continue;
            double dx = r.Points[1].X - r.Points[0].X;
            double dy = r.Points[1].Y - r.Points[0].Y;
            if (Math.Sqrt(dx * dx + dy * dy) < minLength) continue;  // too short
            if (Math.Abs(dx) < 2) continue;                           // near-vertical edge
            double a = Math.Atan2(dy, dx) * (180.0 / Math.PI);
            if (Math.Abs(a) < MaxAngleDeg) angles.Add(a);
        }
        if (angles.Count < 3) return 0;
        angles.Sort();
        return angles[angles.Count / 2];             // median
    }

    /// <summary>
    /// Converts a deskewed SKBitmap back to a frozen WPF BitmapSource (PNG-encoded).
    /// </summary>
    public static BitmapSource ToBitmapSource(SKBitmap skBmp)
    {
        using var img  = SKImage.FromBitmap(skBmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var ms   = new System.IO.MemoryStream(data.ToArray());
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = ms;
        bmp.CacheOption  = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static double DetectSkewAngle(SKBitmap src)
    {
        int w = src.Width, h = src.Height;
        int cx = w / 2, cy = h / 2;

        // Single-allocation pixel snapshot
        SKColor[] pixels = src.Pixels;

        // Collect subsampled dark-pixel offsets from image centre
        var dark = new List<(int dx, int dy)>(pixels.Length / (SampleStep * SampleStep));
        for (int y = 0; y < h; y += SampleStep)
        for (int x = 0; x < w; x += SampleStep)
        {
            var c    = pixels[y * w + x];
            int luma = (c.Red * 77 + c.Green * 150 + c.Blue * 29) >> 8;
            if (luma < 128) dark.Add((x - cx, y - cy));
        }

        // Guard: too few dark pixels (blank page) or mostly dark (inverted)
        int totalSampled = (w / SampleStep) * (h / SampleStep);
        if (dark.Count < 50 || dark.Count > totalSampled * 6 / 10) return 0;

        int histLen = w + h + 4;
        int histOff = histLen / 2;
        var hist    = new int[histLen];

        double bestAngle = 0, bestScore = -1;

        for (double deg = -MaxAngleDeg; deg <= MaxAngleDeg; deg += AngleStepDeg)
        {
            double rad  = deg * Math.PI / 180.0;
            double cosA = Math.Cos(rad);
            double sinA = Math.Sin(rad);

            Array.Clear(hist, 0, histLen);
            foreach (var (dx, dy) in dark)
            {
                int row = (int)(dy * cosA - dx * sinA) + histOff;
                if ((uint)row < (uint)histLen) hist[row]++;
            }

            // Sum of squares rewards sharp projection peaks (horizontally aligned text rows)
            double score = 0;
            foreach (int v in hist) score += (double)v * v;

            if (score > bestScore) { bestScore = score; bestAngle = deg; }
        }

        return bestAngle;
    }

    private static SKBitmap RotateBitmap(SKBitmap src, double degrees)
    {
        float rad  = (float)(degrees * Math.PI / 180.0);
        float cosA = Math.Abs(MathF.Cos(rad));
        float sinA = Math.Abs(MathF.Sin(rad));
        int newW   = (int)(src.Width * cosA + src.Height * sinA);
        int newH   = (int)(src.Width * sinA + src.Height * cosA);

        var dst = new SKBitmap(newW, newH, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        canvas.Translate(newW / 2f, newH / 2f);
        canvas.RotateDegrees((float)degrees);
        canvas.Translate(-src.Width / 2f, -src.Height / 2f);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }
}
