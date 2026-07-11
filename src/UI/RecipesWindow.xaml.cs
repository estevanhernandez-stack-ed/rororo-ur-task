using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.UI;

/// <summary>Read-only projection of one saved <see cref="Recipe"/> for the
/// library list row — resolves a display name and a short summary so the
/// DataTemplate has no method calls to bind against (mirrors StepRowItem's
/// rationale in RecipeEditorWindow.xaml.cs).</summary>
internal sealed class RecipeRowItem
{
    public RecipeRowItem(Recipe recipe)
    {
        Recipe = recipe;
        DisplayName = string.IsNullOrWhiteSpace(recipe.Name) ? "(unnamed)" : recipe.Name!;
        Summary = BuildSummary(recipe);
    }

    public Recipe Recipe { get; }
    public string DisplayName { get; }
    public string Summary { get; }

    /// <summary>Defensive against a hand-edited/corrupted on-disk file with an
    /// empty Steps list — Recipe.Terminal indexes Steps[^1], which throws on
    /// empty. A recipe built through the editor always has ≥1 step (Save/Run
    /// are gated on CanSave), so this path is disk-corruption-only.</summary>
    private static string BuildSummary(Recipe recipe)
    {
        if (recipe.Steps.Count == 0) return "0 steps — invalid recipe";
        var positionCount = recipe.PositionSteps.Count();
        var terminalLabel = recipe.Terminal.Iteration switch
        {
            StepIteration.Loop => "LOOP",
            StepIteration.KeepAlive => "KEEP-ALIVE",
            _ => recipe.Terminal.Iteration.ToString().ToUpperInvariant(),
        };
        return $"{recipe.Steps.Count} steps · {positionCount} position → {terminalLabel}";
    }
}

/// <summary>
/// The saved-recipes library — the missing other half of the recipes feature.
/// Lists every recipe persisted in <see cref="RecipeStore"/> with per-row
/// Run/Edit/Delete, plus a NEW RECIPE entry point. No dedicated VM; the row
/// projection (<see cref="RecipeRowItem"/>) is enough, same shape as the
/// editor's StepRowItem sync layer — this window just re-reads
/// <see cref="RecipeStore.LoadAll"/> into <see cref="_rows"/>.
///
/// Refresh strategy: rather than threading a callback through
/// <see cref="PluginRuntime.OpenRecipeEditor"/> (which owns persistence and
/// doesn't expose the child window it creates), this window re-reads disk on
/// every <see cref="Window.Activated"/> — New (via OpenRecipeEditor) and Edit
/// (constructed directly below) both close their child editor and hand focus
/// back here, so both paths refresh the same way without extra plumbing.
/// </summary>
public partial class RecipesWindow : Window
{
    private readonly PluginRuntime _runtime;
    private readonly ObservableCollection<RecipeRowItem> _rows = new();

    internal RecipesWindow(PluginRuntime runtime)
    {
        InitializeComponent();

        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        RecipeList.ItemsSource = _rows;
        RefreshList();

        Activated += (_, _) => RefreshList();
        Loaded += (_, _) => { Activate(); Focus(); Keyboard.Focus(this); };
    }

    /// <summary>Re-read every recipe from disk and rebuild the row list.</summary>
    internal void RefreshList()
    {
        var recipes = _runtime.Recipes.LoadAll().Recipes
            .OrderBy(r => r.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        _rows.Clear();
        foreach (var recipe in recipes)
            _rows.Add(new RecipeRowItem(recipe));
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Header ───────────────────────────────────────────────────────────────

    private void OnNewRecipeClicked(object sender, RoutedEventArgs e) => _runtime.OpenRecipeEditor(this);

    // ── Per-row actions ─────────────────────────────────────────────────────

    /// <summary>
    /// RUN: reuse the existing multi-select alt picker (built for exactly this
    /// seam — see PlaybackTargetPickerWindow) instead of auto-grabbing every
    /// running alt. Explicit selection, no surprise fan-out. Cancel or a
    /// zero-alt confirm is a no-op — RunRecipe itself refuses an empty list,
    /// but checking here avoids even attempting the call.
    /// </summary>
    private void OnRunClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not RecipeRowItem row) return;

        var alts = _runtime.Accounts.Snapshot().OrderBy(a => a.DisplayName).ToList();
        var picker = new PlaybackTargetPickerWindow(row.Recipe.Name ?? "recipe", alts, preferredUserId: null, multiSelect: true)
        {
            Owner = this,
        };
        if (picker.ShowDialog() == true && picker.SelectedTargets is { Count: > 0 } chosen)
            _runtime.RunRecipe(row.Recipe, chosen);
    }

    private void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not RecipeRowItem row) return;
        OpenEditor(row.Recipe);
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not RecipeRowItem row) return;
        _runtime.Recipes.Delete(row.Recipe.Id);
        RefreshList();
    }

    /// <summary>
    /// Edit path: construct the editor directly (PluginRuntime.OpenRecipeEditor
    /// only supports New) seeded with the existing recipe via the 4th ctor
    /// param. Persistence mirrors OpenRecipeEditor's own Saved wiring — Save
    /// reuses the recipe's original Id (RecipeEditorWindow's _existingId), so
    /// editing overwrites the same file instead of cloning a new one.
    /// </summary>
    private void OpenEditor(Recipe existing)
    {
        try
        {
            var library = _runtime.Store.LoadAll().Macros;
            var alts = _runtime.Accounts.Snapshot().OrderBy(a => a.DisplayName).ToList();
            var editor = new RecipeEditorWindow(library, alts, _runtime, existing) { Owner = this };
            editor.Saved += (_, _) =>
            {
                if (editor.BuiltRecipe is { } recipe)
                    _runtime.Recipes.Save(recipe);
            };
            editor.Show();
        }
        catch (Exception ex)
        {
            // Row-button click — nowhere dedicated to report; leave a trace like OpenRecipeEditor does.
            Diagnostics.DiagLog.Write($"Recipe editor failed to open (edit): {ex.Message}");
        }
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
