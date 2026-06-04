using System.Windows;
using System.Windows.Input;
using HoloPDFCreator.Services;

namespace HoloPDFCreator.Dialogs;

public enum OcrRunScope { Current, All, Range }

public class OcrRunResult
{
    public OcrRunScope  Scope               { get; init; }
    public int          FromPage            { get; init; }
    public int          ToPage              { get; init; }
    public OcrModelSize ModelSize           { get; init; }
    public int          Workers             { get; init; }
    public int          KoreanUpscaleTarget { get; init; }
}

public partial class OcrRunDialog : Window
{
    private readonly int _totalPages;
    private readonly int _currentPage;

    public OcrRunResult? Result { get; private set; }

    public OcrRunDialog(int totalPages, int currentPage,
                        OcrModelSize lastModelSize = OcrModelSize.Mobile,
                        int lastWorkers = 0,
                        int lastKoreanUpscale = 2560)
    {
        InitializeComponent();
        _totalPages  = totalPages;
        _currentPage = currentPage;

        TxtFrom.Text      = currentPage.ToString();
        TxtTo.Text        = totalPages.ToString();
        TxtTotalHint.Text = $"/ {totalPages}";

        RadioMobile.IsChecked = lastModelSize == OcrModelSize.Mobile;
        RadioFull.IsChecked   = lastModelSize == OcrModelSize.Full;

        TxtKoreanUpscale.Text = lastKoreanUpscale.ToString();

        // Default workers: nearest option to cpu count (first run), or last-used value.
        int workers = lastWorkers > 0 ? lastWorkers : Math.Min(Environment.ProcessorCount, 8);
        CmbWorkers.SelectedIndex = workers <= 1 ? 0 :
                                   workers <= 2 ? 1 :
                                   workers <= 4 ? 2 : 3;
    }

    private void RadioScope_Checked(object sender, RoutedEventArgs e)
    {
        if (RangePanel == null) return;
        RangePanel.Visibility = RadioRange.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void NumericOnly(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsDigit);

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        OcrRunScope scope;
        int from = _currentPage, to = _currentPage;

        if (RadioAll.IsChecked == true)
        {
            scope = OcrRunScope.All;
            from  = 1;
            to    = _totalPages;
        }
        else if (RadioRange.IsChecked == true)
        {
            scope = OcrRunScope.Range;
            if (!int.TryParse(TxtFrom.Text, out from) || from < 1 ||
                !int.TryParse(TxtTo.Text,   out to)   || to < from)
            {
                MessageBox.Show("올바른 범위를 입력하세요 (From ≤ To, 둘 다 ≥ 1).",
                    "범위 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            to = Math.Min(to, _totalPages);
        }
        else
        {
            scope = OcrRunScope.Current;
        }

        int workers = CmbWorkers.SelectedIndex switch
        {
            0 => 1,
            2 => 4,
            3 => 8,
            _ => 2,
        };

        int upscale = int.TryParse(TxtKoreanUpscale.Text, out int u) && u >= 128 ? u : 2560;

        Result = new OcrRunResult
        {
            Scope               = scope,
            FromPage            = from,
            ToPage              = to,
            ModelSize           = RadioFull.IsChecked == true ? OcrModelSize.Full : OcrModelSize.Mobile,
            Workers             = workers,
            KoreanUpscaleTarget = upscale,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
        if (e.Key == Key.Enter)  Run_Click(sender, e);
    }
}
