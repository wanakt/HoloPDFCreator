using System.Windows;

namespace HoloPDFCreator.Dialogs;

public partial class ProgressWindow : Window
{
    private CancellationTokenSource _cts = new();
    public  CancellationToken Token => _cts.Token;
    public  bool IsCancelled => _cts.IsCancellationRequested;

    public ProgressWindow(string title)
    {
        InitializeComponent();
        TxtTitle.Text = title;
    }

    public void Update(int current, int total, string stepText)
    {
        PbProgress.Value = total > 0 ? (double)current / total * 100 : 0;
        TxtStep.Text     = stepText;
    }

    public void SetTitle(string title) => TxtTitle.Text = title;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        BtnCancel.IsEnabled = false;
        BtnCancel.Content   = "Cancelling…";
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // If closed by X button, treat as cancel (don't block close).
        if (!IsCancelled) _cts.Cancel();
    }
}
