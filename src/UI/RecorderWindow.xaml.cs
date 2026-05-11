using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Labs626.UrTask.UI;

public partial class RecorderWindow : Window
{
    public RecorderWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is RecorderViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
                ApplyCompactState(vm.IsCompact);
            }
        };
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is RecorderViewModel vm && e.PropertyName == nameof(vm.IsCompact))
        {
            ApplyCompactState(vm.IsCompact);
        }
    }

    private void ApplyCompactState(bool compact)
    {
        if (compact)
        {
            MinWidth = 320;
            MinHeight = 80;
            Width = 380;
            Height = 110;
            SizeToContent = SizeToContent.Manual;
        }
        else
        {
            MinWidth = 440;
            MinHeight = 520;
            Width = 520;
            Height = 640;
            SizeToContent = SizeToContent.Manual;
        }
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
