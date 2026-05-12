using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Labs626.UrTask.Macros;

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

    private void OnMacroOverflowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void OnMacroPlayOnMultipleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi
            && DataContext is RecorderViewModel vm
            && (mi.Tag as Labs626.UrTask.Macros.Macro
                ?? ((mi.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.Tag as Labs626.UrTask.Macros.Macro) is Labs626.UrTask.Macros.Macro macro)
        {
            vm.PlayMacroOnMultipleCommand.Execute(macro);
        }
    }

    private void OnMacroRenameClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is Macro macro
            && DataContext is RecorderViewModel vm)
        {
            var dlg = new RenameMacroDialog(macro.Name ?? "")
            {
                Owner = this,
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.NewName))
            {
                vm.RenameMacro(macro, dlg.NewName.Trim());
            }
        }
    }

    private void OnMacroDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is Macro macro
            && DataContext is RecorderViewModel vm)
        {
            var result = MessageBox.Show(this,
                $"Delete macro \"{macro.Name ?? "(unnamed)"}\"?\nThis can't be undone.",
                "Delete macro",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (result == MessageBoxResult.OK)
            {
                vm.DeleteMacro(macro);
            }
        }
    }

    // ── Custom title bar handlers ───────────────────────────────────────────

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch (InvalidOperationException) { /* ignore if not left-button or wrong state */ }
        }
    }

    private void OnTitleBarMinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnTitleBarMaximizeClicked(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Close button hides to tray — the OnClosing override below handles the
    /// cancel-and-hide logic, so calling Close() here is correct. The tray
    /// icon's Quit item calls Application.Current.Shutdown for real exit.
    /// </summary>
    private void OnTitleBarCloseClicked(object sender, RoutedEventArgs e) => Close();
}
