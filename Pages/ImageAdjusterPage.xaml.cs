using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using HoloPDFCreator.Services;
using Microsoft.Win32;

namespace HoloPDFCreator.Pages;

public partial class ImageAdjusterPage : Page
{
    private Bitmap? _originalBitmap;
    private Bitmap? _adjustedBitmap;
    private bool _autoLevelApplied;
    private bool _suppressSliderUpdate;

    public ImageAdjusterPage()
    {
        InitializeComponent();
    }

    // ─── File Operations ───────────────────────────────────────────────────────

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif;*.gif|All Files|*.*",
            Title = "Open Image"
        };
        if (dlg.ShowDialog() != true) return;
        LoadImage(dlg.FileName);
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        string[] imageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".gif"];
        var img = Array.Find(files, f => imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (img is not null) LoadImage(img);
    }

    private void LoadImage(string path)
    {
        try
        {
            _originalBitmap?.Dispose();
            _adjustedBitmap?.Dispose();

            _originalBitmap = new Bitmap(path);
            _adjustedBitmap = null;
            _autoLevelApplied = false;

            ImgOriginal.Source = BitmapToWpf(_originalBitmap);
            ImgAdjusted.Source = null;

            OriginalDropZone.Visibility = Visibility.Collapsed;
            ImgOriginal.Visibility = Visibility.Visible;
            AdjustedPlaceholder.Visibility = Visibility.Visible;
            ImgAdjusted.Visibility = Visibility.Collapsed;

            BtnSaveImage.IsEnabled = false;
            BtnApply.IsEnabled = true;

            TxtImageInfo.Text = $"{_originalBitmap.Width} × {_originalBitmap.Height} px  ·  {Path.GetFileName(path)}";
            SetStatus("Image loaded. Adjust settings and click Apply.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open image:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        if (_adjustedBitmap is null) return;

        var dlg = new SaveFileDialog
        {
            Filter = "PNG Image|*.png|JPEG Image|*.jpg|BMP Image|*.bmp",
            Title = "Save Adjusted Image",
            FileName = "adjusted"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var format = dlg.FilterIndex switch
            {
                2 => System.Drawing.Imaging.ImageFormat.Jpeg,
                3 => System.Drawing.Imaging.ImageFormat.Bmp,
                _ => System.Drawing.Imaging.ImageFormat.Png
            };
            _adjustedBitmap.Save(dlg.FileName, format);
            SetStatus("Image saved.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ─── Controls ─────────────────────────────────────────────────────────────

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderUpdate) return;
        TxtBrightness.Text = ((int)SldBrightness.Value).ToString();
        TxtContrast.Text   = ((int)SldContrast.Value).ToString();
        TxtStroke.Text     = ((int)SldStroke.Value).ToString();
    }

    private void AutoLevel_Click(object sender, RoutedEventArgs e)
    {
        if (_originalBitmap is null) { SetStatus("Open an image first."); return; }
        _autoLevelApplied = true;
        SetStatus("Auto level will be applied on next Apply.");
        TxtStatus.Text = "Auto level queued. Click Apply Changes.";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _suppressSliderUpdate = true;
        SldBrightness.Value = 0;
        SldContrast.Value   = 0;
        SldStroke.Value     = 0;
        _suppressSliderUpdate = false;

        TxtBrightness.Text = "0";
        TxtContrast.Text   = "0";
        TxtStroke.Text     = "0";
        _autoLevelApplied = false;

        if (_originalBitmap is not null)
        {
            ImgAdjusted.Source = null;
            AdjustedPlaceholder.Visibility = Visibility.Visible;
            ImgAdjusted.Visibility = Visibility.Collapsed;
            BtnSaveImage.IsEnabled = false;
        }
        SetStatus("Settings reset.");
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_originalBitmap is null) { SetStatus("Open an image first."); return; }

        SetStatus("Processing…");
        try
        {
            var result = new Bitmap(_originalBitmap);

            float brightness = (float)SldBrightness.Value / 100f;
            float contrast   = (float)SldContrast.Value   / 100f;
            int   stroke     = (int)SldStroke.Value;

            if (brightness != 0)
                result = ImageProcessingService.AdjustBrightness(result, brightness);

            if (contrast != 0)
                result = ImageProcessingService.AdjustContrast(result, contrast);

            if (_autoLevelApplied)
                result = ImageProcessingService.AutoLevel(result);

            if (stroke > 0)
                result = ImageProcessingService.ThickenStrokes(result, stroke);

            _adjustedBitmap?.Dispose();
            _adjustedBitmap = result;

            ImgAdjusted.Source = BitmapToWpf(result);
            AdjustedPlaceholder.Visibility = Visibility.Collapsed;
            ImgAdjusted.Visibility = Visibility.Visible;
            BtnSaveImage.IsEnabled = true;

            var ops = new List<string>();
            if (brightness != 0) ops.Add($"Brightness {(int)SldBrightness.Value:+#;-#;0}");
            if (contrast   != 0) ops.Add($"Contrast {(int)SldContrast.Value:+#;-#;0}");
            if (_autoLevelApplied) ops.Add("Auto Level");
            if (stroke > 0) ops.Add($"Stroke ×{stroke}");

            SetStatus("Done: " + (ops.Count > 0 ? string.Join(", ", ops) : "no changes"));
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}");
            MessageBox.Show($"Processing failed:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static BitmapImage BitmapToWpf(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = ms;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void SetStatus(string msg) => TxtStatus.Text = msg;
}
