using System.Windows;
using System.Windows.Controls;
using HoloPDFCreator.Models;
using HoloPDFCreator.Pages;

namespace HoloPDFCreator;

public partial class MainWindow : Window
{
    private readonly AdjustedImageStore _adjustedStore = new();
    private readonly PDFReaderPage     _pdfReaderPage = new();
    private readonly OcrPage           _ocrPage       = new();

    public MainWindow()
    {
        InitializeComponent();
        _pdfReaderPage.AdjustedStore = _adjustedStore;
        _ocrPage.AdjustedStore       = _adjustedStore;
        ContentFrame.Navigate(_pdfReaderPage);

        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && System.IO.File.Exists(args[1]))
                await _pdfReaderPage.LoadPdfAsync(args[1]);
        };
    }

    // ─── Page navigation ─────────────────────────────────────────────────────

    private void SetNavActive(Button active)
    {
        BtnNavPdfReader.Style     = (Style)FindResource("NavButton");
        BtnNavImageAdjuster.Style = (Style)FindResource("NavButton");
        active.Style              = (Style)FindResource("NavButtonActive");
    }

    private void SwitchToPdfReader()
    {
        ContentFrame.Navigate(_pdfReaderPage);
        SetNavActive(BtnNavPdfReader);
        _ = _pdfReaderPage.RefreshWithAdjustedImagesAsync();
    }

    private void BtnNavPdfReader_Click(object sender, RoutedEventArgs e) => SwitchToPdfReader();

    private async void BtnNavImageAdjuster_Click(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigate(_ocrPage);
        SetNavActive(BtnNavImageAdjuster);

        if (_pdfReaderPage.EffectiveFilePath is string pdfPath)
        {
            await _ocrPage.LoadFromPdfAsync(pdfPath, _pdfReaderPage.CurrentPage);
            _ocrPage.OverrideWithAdjustedStore();
        }
    }
}
