using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.UI;

/// <summary>Read-only projection of one <see cref="RecipeStep"/> for the ordered
/// steps list — resolves the macro name and a short iteration label so the
/// DataTemplate has no method calls to bind against. Recreated on every VM
/// change (mirrors <see cref="AltRowItem"/>'s sync pattern), so
/// <see cref="Index"/> is always the step's live position — safe to feed
/// straight into <see cref="RecipeEditorViewModel.RemoveStep"/> even when two
/// steps are value-equal (e.g. the same macro used twice in a row).</summary>
internal sealed class StepRowItem
{
    public StepRowItem(RecipeStep step, int index, string displayName)
    {
        Step = step;
        Index = index;
        DisplayName = displayName;
    }

    public RecipeStep Step { get; }
    public int Index { get; }
    public string DisplayName { get; }

    public string IterationLabel => Step.Iteration switch
    {
        StepIteration.RunOnce => "POSITION",
        StepIteration.Loop => "LOOP",
        StepIteration.KeepAlive => "KEEP-ALIVE",
        _ => Step.Iteration.ToString().ToUpperInvariant(),
    };
}

/// <summary>
/// The recipe authoring shell: name, alt-set selection (reusing
/// <see cref="PlaybackTargetPickerViewModel"/> for multi-select + select-all/none),
/// an ordered position/terminal steps list, and Save/Run actions. All authoring
/// logic lives in <see cref="RecipeEditorViewModel"/>; this window is a thin view
/// plus the two small sync layers (alt rows, step rows) that keep DataTemplates
/// free of method-call bindings.
///
/// Save/Run seam for Task 7: Save builds the <see cref="Recipe"/> via
/// <c>_vm.Build(...)</c>, stores it on <see cref="BuiltRecipe"/>, and raises
/// <see cref="Saved"/> — Task 7 wires persistence (RecipeStore) off that event
/// without this window needing to know about it. Run is deliberately left
/// unwired (no Click handler) for Task 7 to attach RecipeRunner.
/// </summary>
public partial class RecipeEditorWindow : Window
{
    private readonly RecipeEditorViewModel _vm;
    private readonly PlaybackTargetPickerViewModel _altPicker;
    private readonly ObservableCollection<AltRowItem> _altRows;
    private readonly ObservableCollection<StepRowItem> _stepRows = new();

    internal RecipeEditorWindow(IReadOnlyList<Macro> library, IReadOnlyList<AccountRegistry.AccountInfo> alts)
    {
        InitializeComponent();

        _vm = new RecipeEditorViewModel(library);
        DataContext = _vm;

        _altPicker = new PlaybackTargetPickerViewModel(alts, preferredUserId: null, multiSelect: true);
        AltPanel.DataContext = _altPicker; // enables the Select all/none row's Visibility={Binding MultiSelect}

        _altRows = new ObservableCollection<AltRowItem>(alts.Select(a => new AltRowItem(a)));
        AltList.ItemsSource = _altRows;
        _altPicker.PropertyChanged += (_, _) => SyncAltRowsFromVm();
        SyncAltRowsFromVm();

        StepsList.ItemsSource = _stepRows;
        _vm.Steps.CollectionChanged += OnStepsChanged;
        SyncStepRowsFromVm();

        PositionMacroCombo.ItemsSource = _vm.Library;
        TerminalMacroCombo.ItemsSource = _vm.Library;

        Loaded += (_, _) => { Activate(); Focus(); Keyboard.Focus(this); };
    }

    /// <summary>Set by <see cref="OnSaveClicked"/> once the current state validates.
    /// Task 7 reads this after <see cref="Saved"/> fires to hand the recipe to RecipeStore.</summary>
    internal Recipe? BuiltRecipe { get; private set; }

    /// <summary>Currently-checked alts in the alt-set sub-panel — Task 7's Run wiring
    /// target for RecipeRunner.RunAsync's selected-alts argument.</summary>
    internal IReadOnlyList<AccountRegistry.AccountInfo> SelectedAlts => _altPicker.SelectedTargets;

    /// <summary>Fires after a successful Save; <see cref="BuiltRecipe"/> is populated by then.</summary>
    internal event EventHandler? Saved;

    // ── Alt-set sync (mirrors PlaybackTargetPickerWindow's row-sync pattern) ──

    private void SyncAltRowsFromVm()
    {
        foreach (var row in _altRows)
            row.IsSelected = _altPicker.IsSelected(row.Info);
    }

    private void OnAltRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is AltRowItem row)
            _altPicker.Toggle(row.Info);
    }

    private void SelectAllAlts_Click(object sender, RoutedEventArgs e) => _altPicker.SelectAll();

    private void SelectNoneAlts_Click(object sender, RoutedEventArgs e) => _altPicker.SelectNone();

    // ── Steps sync ──────────────────────────────────────────────────────────

    private void OnStepsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncStepRowsFromVm();

    private void SyncStepRowsFromVm()
    {
        _stepRows.Clear();
        for (int i = 0; i < _vm.Steps.Count; i++)
        {
            var step = _vm.Steps[i];
            _stepRows.Add(new StepRowItem(step, i, _vm.StepMacroName(step)));
        }
    }

    // ── Step authoring ──────────────────────────────────────────────────────

    private void OnAddPositionStepClicked(object sender, RoutedEventArgs e)
    {
        if (PositionMacroCombo.SelectedItem is Macro macro)
            _vm.AddPositionStep(macro.Id);
    }

    private void OnSetLoopClicked(object sender, RoutedEventArgs e)
    {
        var macro = TerminalMacroCombo.SelectedItem as Macro;
        _vm.SetTerminal(StepIteration.Loop, macro?.Id);
    }

    private void OnSetKeepAliveClicked(object sender, RoutedEventArgs e)
        => _vm.SetTerminal(StepIteration.KeepAlive, null);

    private void OnRemoveStepClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is StepRowItem row)
            _vm.RemoveStep(row.Index);
    }

    // ── Save (Task 7 wires persistence off the Saved event) ────────────────

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanSave) return;
        BuiltRecipe = _vm.Build(Guid.NewGuid().ToString(), _vm.Name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Saved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    // ── Custom title bar ─────────────────────────────────────────────────────

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch (InvalidOperationException) { /* ignore if not left-button or wrong state */ }
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
