using System.Windows;
using System.Windows.Controls;
using HoloPDFCreator.Pages;

namespace HoloPDFCreator;

public partial class MainWindow : Window
{
    private readonly PDFReaderPage     _pdfReaderPage = new();
    private readonly OcrPage           _ocrPage       = new();

    public MainWindow()
    {
        InitializeComponent();
        ContentFrame.Navigate(_pdfReaderPage);

        // The OCR view works on the reader's document: PDFs dropped there open in the
        // reader, and its searchable-PDF save is the reader's save.
        _ocrPage.OpenPdfInReader = async path =>
        {
            await _pdfReaderPage.LoadPdfAsync(path);
            await ShowReaderDocumentInOcrAsync();
        };
        // A copy: the reader keeps working on its document.
        _ocrPage.SaveReaderDocument = (path, includeOcr) => _pdfReaderPage.SaveCopyAsync(path, includeOcr);

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

        await ShowReaderDocumentInOcrAsync();
    }

    private async Task ShowReaderDocumentInOcrAsync()
    {
        if (_pdfReaderPage.EffectiveFilePath is not string pdfPath) return;
        // pdfPath may be the reader's temp file; the results belong to the document itself.
        await _ocrPage.LoadFromPdfAsync(pdfPath, _pdfReaderPage.CurrentPage,
                                        _pdfReaderPage.Session, _pdfReaderPage.Images);
    }
}
