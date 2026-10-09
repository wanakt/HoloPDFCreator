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
        _pdfReaderPage.AdjustedStore      = _adjustedStore;
        _pdfReaderPage.OcrStore           = _ocrPage;
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
        BtnNavOcr.Style = (Style)FindResource("NavButton");
        active.Style              = (Style)FindResource("NavButtonActive");
    }

    private void SwitchToPdfReader()
    {
        ContentFrame.Navigate(_pdfReaderPage);
        SetNavActive(BtnNavPdfReader);
        _ = _pdfReaderPage.RefreshWithAdjustedImagesAsync();
    }

    private void BtnNavPdfReader_Click(object sender, RoutedEventArgs e) => SwitchToPdfReader();

    private async void BtnNavOcr_Click(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigate(_ocrPage);
        SetNavActive(BtnNavOcr);

        if (_pdfReaderPage.EffectiveFilePath is string pdfPath)
        {
            // pdfPath may be the reader's temp file; the results belong to the document itself.
            await _ocrPage.LoadFromPdfAsync(pdfPath, _pdfReaderPage.CurrentPage,
                                            _pdfReaderPage.CurrentFilePath);
            _ocrPage.ApplyAdjustedImages(_adjustedStore);
        }
    }
}
