using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.UI;

/// <summary>Authoring state for one recipe: an ordered list of position steps plus
/// a terminal loop/keep-alive step, built from the macro library. All logic here;
/// the window is a thin view over it.</summary>
internal sealed class RecipeEditorViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, Macro> _byId;

    public RecipeEditorViewModel(IReadOnlyList<Macro> library)
    {
        _byId = library.ToDictionary(m => m.Id);
        Library = new ObservableCollection<Macro>(library);
    }

    public ObservableCollection<Macro> Library { get; }
    public ObservableCollection<RecipeStep> Steps { get; } = new();

    private string? _name;
    public string? Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Seed this VM's Name + Steps from an existing recipe — the Edit
    /// flow's entry point (RecipesWindow's per-row Edit action). Clears any
    /// current authoring state first. The loaded steps are already valid (they
    /// came from a saved recipe), so Recompute() flips CanSave true immediately
    /// — Save/Run are enabled without the user touching anything.</summary>
    public void LoadFrom(Recipe recipe)
    {
        if (recipe is null) throw new ArgumentNullException(nameof(recipe));
        Name = recipe.Name;
        Steps.Clear();
        foreach (var step in recipe.Steps)
            Steps.Add(step);
        Recompute();
    }

    public void AddPositionStep(string macroId)
    {
        // a new position step must land before the terminal step, if one exists
        var step = new RecipeStep(macroId, StepIteration.RunOnce);
        if (Steps.Count > 0 && Steps[^1].Iteration != StepIteration.RunOnce)
            Steps.Insert(Steps.Count - 1, step);
        else
            Steps.Add(step);
        Recompute();
    }

    public void SetTerminal(StepIteration mode, string? macroId)
    {
        if (mode is not (StepIteration.Loop or StepIteration.KeepAlive))
            throw new ArgumentException("Terminal must be Loop or KeepAlive.", nameof(mode));
        var terminal = new RecipeStep(mode == StepIteration.Loop ? macroId : null, mode);
        if (Steps.Count > 0 && Steps[^1].Iteration != StepIteration.RunOnce)
            Steps[^1] = terminal;   // replace existing terminal
        else
            Steps.Add(terminal);
        Recompute();
    }

    public void RemoveStep(int index)
    {
        if (index >= 0 && index < Steps.Count) { Steps.RemoveAt(index); Recompute(); }
    }

    /// <summary>Swap a position step up with its predecessor. Only RunOnce steps move,
    /// and only swap with an adjacent RunOnce — so the terminal Loop/KeepAlive step can
    /// never move and a position step can never end up past it.</summary>
    public void MoveStepUp(int index)
    {
        if (index <= 0 || index >= Steps.Count) return;
        if (Steps[index].Iteration != StepIteration.RunOnce || Steps[index - 1].Iteration != StepIteration.RunOnce) return;
        Steps.Move(index, index - 1);
        Recompute();
    }

    /// <summary>Swap a position step down with its successor. Only RunOnce steps move,
    /// and only swap with an adjacent RunOnce — so the terminal Loop/KeepAlive step can
    /// never move and a position step can never end up past it.</summary>
    public void MoveStepDown(int index)
    {
        if (index < 0 || index >= Steps.Count - 1) return;
        if (Steps[index].Iteration != StepIteration.RunOnce || Steps[index + 1].Iteration != StepIteration.RunOnce) return;
        Steps.Move(index, index + 1);
        Recompute();
    }

    public bool CanSave { get; private set; }
    public string? ValidationError { get; private set; }

    /// <summary>Macro display name for a step (game badge/mismatch reuse existing MacroGameFilter in the row template).</summary>
    public string StepMacroName(RecipeStep step)
        => step.Iteration == StepIteration.KeepAlive ? "Keep-alive (Space)"
         : (step.MacroId is not null && _byId.TryGetValue(step.MacroId, out var m) ? (m.Name ?? "(unnamed)") : "(missing macro)");

    public Recipe Build(string id, string? name, long nowUnixMs)
    {
        var placeStamp = Steps.Select(s => s.MacroId)
            .Where(mid => mid is not null && _byId.ContainsKey(mid))
            .Select(mid => _byId[mid!])
            .FirstOrDefault(m => m.IsGameScoped);
        return new Recipe(Recipe.CurrentSchemaVersion, id, name, Steps.ToList(), nowUnixMs,
            placeStamp?.RecordedPlaceId, placeStamp?.RecordedGameName);
    }

    private void Recompute()
    {
        var (ok, error) = Recipe.ValidateSteps(Steps.ToList());
        CanSave = ok;
        ValidationError = error;
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ValidationError));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
