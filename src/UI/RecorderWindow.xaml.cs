using System.ComponentModel;
using System.Windows;

namespace Labs626.UrTask.UI;

public partial class RecorderWindow : Window
{
    public RecorderWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Close button minimizes to tray rather than exiting — App's ShutdownMode
    /// is OnExplicitShutdown, and the plugin stays running in the tray watching
    /// for hotkeys. App.xaml.cs handles real exit via the tray menu's Quit item.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private void OnTogglePinClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is RecorderViewModel vm)
            vm.IsTopmost = !vm.IsTopmost;
    }

    private void OnToggleCompactClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is RecorderViewModel vm)
            vm.IsCompact = !vm.IsCompact;
    }
}
