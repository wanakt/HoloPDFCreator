using System.Windows;
using System.Windows.Input;

namespace HoloPDFCreator.Dialogs;

public enum RotateScope { Current, All, Range, Even, Odd }

public partial class RotateDialog : Window
{
    public int         Angle    { get; private set; }
    public RotateScope Scope    { get; private set; }
    public int         PageFrom { get; private set; }
    public int         PageTo   { get; private set; }

    private readonly int _totalPages;

    public RotateDialog(int totalPages, int currentPage)
    {
        InitializeComponent();
        _totalPages    = totalPages;
        RangeFrom.Text = (currentPage + 1).ToString();
        RangeTo.Text   = (currentPage + 1).ToString();
        TotalHint.Text = $"/ {totalPages}";
    }

    private void RbRange_Checked(object sender, RoutedEventArgs e)
        => RangePanel.IsEnabled = true;

    private void RbRange_Unchecked(object sender, RoutedEventArgs e)
        => RangePanel.IsEnabled = false;

    private void RangeBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Length == 0 || !char.IsDigit(e.Text[0]);

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Angle = Rb90CCW.IsChecked == true ? 270
              : Rb180.IsChecked   == true ? 180
              :                             90;   // Rb90CW (default)

        if (RbAll.IsChecked == true)
        {
            Scope = RotateScope.All;
        }
        else if (RbRange.IsChecked == true)
        {
            if (!int.TryParse(RangeFrom.Text, out int from) || from < 1 || from > _totalPages ||
                !int.TryParse(RangeTo.Text,   out int to)   || to   < 1 || to   > _totalPages ||
                from > to)
            {
                MessageBox.Show($"유효한 페이지 범위를 입력해주세요. (1 – {_totalPages})", "입력 오류",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Scope    = RotateScope.Range;
            PageFrom = from;
            PageTo   = to;
        }
        else if (RbEven.IsChecked == true)
        {
            Scope = RotateScope.Even;
        }
        else if (RbOdd.IsChecked == true)
        {
            Scope = RotateScope.Odd;
        }
        else
        {
            Scope = RotateScope.Current;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
