using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.UI;

public partial class RecorderWindow : Window
{
    /// <summary>Wired by App.xaml.cs to <see cref="PluginRuntime.OpenRecipeEditor"/>
    /// so the RECIPES button doesn't need a runtime reference of its own.</summary>
    internal Action? RecipesRequested { get; set; }

    public RecorderWindow()
    {
        InitializeComponent();
        NativeResizeBehavior.Attach(this);
        Loaded += (_, _) =>
        {
            if (DataContext is RecorderViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
                ApplyCompactState(vm.IsCompact);
            }
        };

        // Re-read the saved-routines list every time this window regains focus —
        // mirrors RecipesWindow's Activated-refresh so a recipe/loadout saved,
        // edited, or deleted in the (non-modal) Recipes library or editor shows
        // up in the routine picker here without a restart.
        Activated += (_, _) =>
        {
            if (DataContext is RecorderViewModel vm) vm.RefreshRoutines();
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
            MinWidth = 720;
            MinHeight = 600;
            Width = 860;
            Height = 600;
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

    private void OnRecipesClicked(object sender, RoutedEventArgs e) => RecipesRequested?.Invoke();

    // ── Macro card handlers ────────────────────────────────────────────────

    /// <summary>
    /// Clicking anywhere on a macro card marks it as the active assignment macro
    /// (cyan outline, "ACTIVE" chip). The card click replaces the old per-card
    /// PLAY button — left-clicking = "I want to assign this macro."
    /// </summary>
    private void OnMacroCardClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is Labs626.UrTask.Macros.Macro macro
            && DataContext is RecorderViewModel vm)
        {
            vm.MarkMacroActiveCommand.Execute(macro);
        }
    }

    private void OnMacroOverflowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
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
            var dlg = new DeleteMacroConfirmDialog(macro.Name ?? "(unnamed)", DescribeUsage(vm.GetUsage(macro))) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                vm.DeleteMacro(macro);
            }
        }
    }

    /// <summary>
    /// Archive moves the file into macros\archive\. An unused macro goes straight
    /// there (Restore undoes it); one that is paired or used in a recipe asks first,
    /// naming where, like Delete does.
    /// </summary>
    private void OnMacroArchiveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not Macro macro
            || DataContext is not RecorderViewModel vm) return;

        var usage = vm.GetUsage(macro);
        if (usage.IsUsed)
        {
            var message = $"Archive macro \"{macro.Name ?? "(unnamed)"}\"? It's in use.\n\n"
                + DescribeUsage(usage)
                + "\n\nPaired alts keep playing it until you clear the pairing. "
                + "Recipes that use it stop at that step until you restore it.";
            var dlg = new ArchiveMacroConfirmDialog(message) { Owner = this };
            if (dlg.ShowDialog() != true) return;
        }
        vm.ArchiveMacro(macro);
    }

    private void OnMacroRestoreClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is Macro macro
            && DataContext is RecorderViewModel vm)
        {
            vm.RestoreMacro(macro);
        }
    }

    /// <summary>"Paired with: a, b." and "Used in recipes: x." lines, or null when unused.</summary>
    private static string? DescribeUsage(MacroUsageInfo usage)
    {
        if (!usage.IsUsed) return null;
        var lines = new List<string>();
        if (usage.PairedAlts.Count > 0) lines.Add($"Paired with: {string.Join(", ", usage.PairedAlts)}.");
        if (usage.Recipes.Count > 0) lines.Add($"Used in recipes: {string.Join(", ", usage.Recipes)}.");
        return string.Join("\n", lines);
    }

    // ── Library search + groups ────────────────────────────────────────────

    /// <summary>Ctrl+F (window-local CommandBinding): jump to the search box.</summary>
    private void OnFindExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (DataContext is RecorderViewModel { IsCompact: true }) return;
        MacroSearchBox.Focus();
        MacroSearchBox.SelectAll();
        e.Handled = true;
    }

    /// <summary>
    /// Esc inside the search box clears it. A local KeyDown on the TextBox only;
    /// a global bare-Esc hotkey hijacked Esc system-wide once (v0.3.1).
    /// </summary>
    private void OnMacroSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not RecorderViewModel vm) return;
        if (vm.SearchText.Length > 0)
        {
            vm.SearchText = string.Empty;
            e.Handled = true;
        }
    }

    private void OnGroupHeaderClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is MacroGroupHeader header
            && DataContext is RecorderViewModel vm)
        {
            vm.ToggleGroup(header);
            e.Handled = true;
        }
    }

    private void OnMacroAllGamesClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is Macro macro
            && DataContext is RecorderViewModel vm)
        {
            vm.ToggleAllGames(macro);
        }
    }

    // ── Macro import / export handlers ─────────────────────────────────────

    private void OnImportMacrosClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RecorderViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import macros",
            Multiselect = true,
            DefaultExt = ".json",
            Filter = "Ur Task macro bundle (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true)
        {
            vm.ImportMacros(dlg.FileNames);
        }
    }

    private void OnExportAllMacrosClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RecorderViewModel vm || vm.Macros.Count == 0) return;
        ExportWithDialog(vm, vm.Macros.ToList(), "ur-task-macros");
    }

    private void OnMacroExportClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is Macro macro
            && DataContext is RecorderViewModel vm)
        {
            ExportWithDialog(vm, new[] { macro }, SuggestExportFileName(macro.Name));
        }
    }

    private void ExportWithDialog(RecorderViewModel vm, IReadOnlyList<Macro> macros, string suggestedName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export macros",
            FileName = suggestedName,
            DefaultExt = ".json",
            Filter = "Ur Task macro bundle (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            vm.ExportMacros(macros, dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed: {ex.Message}", "RoRoRo Ur Task",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnMacroExportAhkV1Clicked(object sender, RoutedEventArgs e) => ExportAsAutoHotkey(sender, AhkVersion.V1);

    private void OnMacroExportAhkV2Clicked(object sender, RoutedEventArgs e) => ExportAsAutoHotkey(sender, AhkVersion.V2);

    /// <summary>Mirrors <see cref="ExportWithDialog"/>'s SaveFileDialog pattern for the
    /// AutoHotkey exporter — .ahk filter, macro-name-derived default filename.</summary>
    private void ExportAsAutoHotkey(object sender, AhkVersion version)
    {
        if (sender is not MenuItem mi || mi.Tag is not Macro macro
            || DataContext is not RecorderViewModel vm) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = version == AhkVersion.V1 ? "Export as AutoHotkey v1" : "Export as AutoHotkey v2",
            FileName = SuggestExportFileName(macro.Name) + ".ahk",
            DefaultExt = ".ahk",
            Filter = "AutoHotkey script (*.ahk)|*.ahk",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            vm.ExportMacroAsAutoHotkey(macro, version, dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed: {ex.Message}", "RoRoRo Ur Task",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Macro name → safe filename stem ("farm loop!" → "farm loop"); falls back to "macro".</summary>
    private static string SuggestExportFileName(string? macroName)
    {
        if (string.IsNullOrWhiteSpace(macroName)) return "macro";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var cleaned = new string(macroName.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "macro" : cleaned;
    }

    // ── Assignment row handler ─────────────────────────────────────────────

    /// <summary>
    /// Clicking an alt row in the assignment panel commits (or clears) the active
    /// macro to that alt. The toggle logic lives in ToggleAltAssignmentCommand.
    /// </summary>
    private void OnAssignmentRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is AssignmentRow row
            && DataContext is RecorderViewModel vm)
        {
            vm.ToggleAltAssignmentCommand.Execute(row);
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
