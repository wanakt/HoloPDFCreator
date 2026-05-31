using System.Windows;
using System.Windows.Input;

namespace HoloPDFCreator.Dialogs;

public partial class PageRangeDialog : Window
{
    public int FromPage { get; private set; }
    public int ToPage   { get; private set; }

    public PageRangeDialog(int totalPages, int currentPage = 1)
    {
        InitializeComponent();
        TxtFrom.Text      = currentPage.ToString();
        TxtTo.Text        = totalPages.ToString();
        TxtTotalHint.Text = $"/ {totalPages}";
    }

    private void NumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtFrom.Text, out int from) || from < 1 ||
            !int.TryParse(TxtTo.Text,   out int to)   || to   < from)
        {
            MessageBox.Show("Enter a valid range (From ≤ To, both ≥ 1).",
                "Invalid Range", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        FromPage    = from;
        ToPage      = to;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
