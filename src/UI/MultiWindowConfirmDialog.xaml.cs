using System.Windows;

namespace Labs626.UrTask.UI;

public partial class MultiWindowConfirmDialog : Window
{
    public MultiWindowConfirmDialog()
    {
        InitializeComponent();
    }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void OnPlay(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
}
