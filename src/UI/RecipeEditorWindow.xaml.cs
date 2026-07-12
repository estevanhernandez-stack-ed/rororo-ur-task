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

    /// <summary>True for position (RunOnce) rows — drives the up/down reorder
    /// buttons' Visibility so the pinned terminal row never shows them.</summary>
    public bool IsPosition => Step.Iteration == StepIteration.RunOnce;

    public string IterationLabel => Step.Iteration switch
    {
        StepIteration.RunOnce => "POSITION",
        StepIteration.Loop => "LOOP",
        StepIteration.KeepAlive => "KEEP-ALIVE",
        StepIteration.Done => "RUN ONCE",
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
/// Save/Run seam: Save builds the <see cref="Recipe"/> via <c>_vm.Build(...)</c>,
/// stores it on <see cref="BuiltRecipe"/>, and raises <see cref="Saved"/> —
/// callers wire persistence (RecipeStore) off that event without this window
/// needing to know about it. Run does the same build + <see cref="Saved"/>
/// (so a run is always persisted) and additionally hands the recipe to
/// <see cref="PluginRuntime.RunRecipe"/>, which composes RecipeRunner over the
/// live SequencePlayer/AssignmentRunner and kicks it off.
/// </summary>
public partial class RecipeEditorWindow : Window
{
    private readonly RecipeEditorViewModel _vm;
    private readonly PlaybackTargetPickerViewModel _altPicker;
    private readonly ObservableCollection<AltRowItem> _altRows;
    private readonly ObservableCollection<StepRowItem> _stepRows = new();
    private readonly PluginRuntime _runtime;
    private readonly string? _existingId;

    /// <summary>
    /// Task 7's live-runner seam: <paramref name="runtime"/> supplies the composed
    /// RecipeRunner (SequencePlayer/AssignmentRunner-backed) via
    /// <see cref="PluginRuntime.RunRecipe"/> for the Run button. Persistence stays
    /// decoupled — callers wire it off <see cref="Saved"/>, same as before.
    ///
    /// <paramref name="existing"/> is the RecipesWindow Edit seam: when supplied,
    /// the VM is seeded from it (<see cref="RecipeEditorViewModel.LoadFrom"/>) and
    /// its Id is reused on Save/Run so editing overwrites the same on-disk file
    /// instead of cloning a new recipe alongside the original. Null (the default)
    /// preserves the New-recipe behavior every existing 3-arg call site expects.
    /// </summary>
    internal RecipeEditorWindow(
        IReadOnlyList<Macro> library,
        IReadOnlyList<AccountRegistry.AccountInfo> alts,
        PluginRuntime runtime,
        Recipe? existing = null)
    {
        InitializeComponent();
        NativeResizeBehavior.Attach(this);

        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _existingId = existing?.Id;
        _vm = new RecipeEditorViewModel(library);
        DataContext = _vm;
        if (existing is not null)
            _vm.LoadFrom(existing); // sets Name (PropertyChanged updates the bound TextBox) + Steps before the sync layers below read them

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
        EmptyStepsHint.Visibility = _stepRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnSetDoneClicked(object sender, RoutedEventArgs e)
        => _vm.SetTerminal(StepIteration.Done, null);

    private void OnRemoveStepClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is StepRowItem row)
            _vm.RemoveStep(row.Index);
    }

    private void OnMoveStepUpClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is StepRowItem row)
            _vm.MoveStepUp(row.Index);
    }

    private void OnMoveStepDownClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is StepRowItem row)
            _vm.MoveStepDown(row.Index);
    }

    // ── Save (persistence is wired off the Saved event by the caller) ──────

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanSave) return;
        var id = _existingId ?? Guid.NewGuid().ToString();
        BuiltRecipe = _vm.Build(id, _vm.Name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Saved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    // ── Run (build + persist same as Save, then hand off to the live runner) ──

    private void OnRunClicked(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanSave || SelectedAlts.Count == 0) return;
        var id = _existingId ?? Guid.NewGuid().ToString();
        BuiltRecipe = _vm.Build(id, _vm.Name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Saved?.Invoke(this, EventArgs.Empty); // same persistence path as Save
        _runtime.RunRecipe(BuiltRecipe, SelectedAlts);
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
