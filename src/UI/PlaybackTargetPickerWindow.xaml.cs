using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.UI;

/// <summary>
/// Per-row binding wrapper so IsSelected and OrderTag can push updates to the
/// DataTemplate without a converter reaching into the window's VM.
/// </summary>
internal sealed class AltRowItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _orderTag = string.Empty;

    public AltRowItem(AccountRegistry.AccountInfo info)
    {
        Info = info;
    }

    public AccountRegistry.AccountInfo Info { get; }

    // Forwarded from AccountInfo for direct binding in DataTemplate.
    public string DisplayName => Info.DisplayName;
    public long RobloxUserId => Info.RobloxUserId;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string OrderTag
    {
        get => _orderTag;
        set { _orderTag = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}

public partial class PlaybackTargetPickerWindow : Window
{
    private readonly PlaybackTargetPickerViewModel _vm;
    private readonly ObservableCollection<AltRowItem> _rows;
    private int _focusedRowIndex;

    internal PlaybackTargetPickerWindow(
        string macroName,
        IReadOnlyList<AccountRegistry.AccountInfo> alts,
        long? preferredUserId,
        bool multiSelect)
    {
        InitializeComponent();
        _vm = new PlaybackTargetPickerViewModel(alts, preferredUserId, multiSelect);

        _rows = new ObservableCollection<AltRowItem>(
            alts.Select(a => new AltRowItem(a)));
        AltList.ItemsSource = _rows;

        TitleText.Text = $"Play {macroName ?? "macro"} on which alt{(multiSelect ? "s" : "")}?";
        SubtitleText.Text = multiSelect
            ? "Pick one or more. Playback runs in sequence — alt 1 → alt 2 → alt 3."
            : "No Roblox window is in foreground. Pick a target.";

        _vm.PropertyChanged += (_, _) => SyncRowsFromVm();
        SyncRowsFromVm();

        // Pre-focus first row so ↑↓ works immediately.
        _focusedRowIndex = _vm.Alts.Count > 0 ? 0 : -1;
        SyncFocusHighlight();
    }

    internal IReadOnlyList<AccountRegistry.AccountInfo>? SelectedTargets { get; private set; }

    // ── Row-click handler ────────────────────────────────────────────────────

    private void OnAltRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is AltRowItem row)
        {
            var rowIdx = _rows.IndexOf(row);
            if (rowIdx >= 0) _focusedRowIndex = rowIdx;

            _vm.Toggle(row.Info);
            // Single-select: click plays immediately.
            if (!_vm.MultiSelect && _vm.CanPlay) Confirm();
        }
    }

    // ── Footer button ────────────────────────────────────────────────────────

    private void OnPlayClicked(object sender, RoutedEventArgs e) => Confirm();

    // ── Keyboard ─────────────────────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                SelectedTargets = null;
                DialogResult = false;
                Close();
                break;

            case Key.Enter:
                if (_vm.CanPlay) Confirm();
                break;

            case Key.Down:
                MoveFocus(+1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveFocus(-1);
                e.Handled = true;
                break;

            case Key.Space:
                if (_focusedRowIndex >= 0 && _focusedRowIndex < _rows.Count)
                {
                    _vm.Toggle(_rows[_focusedRowIndex].Info);
                    e.Handled = true;
                }
                break;
        }
    }

    // ── Focus movement ───────────────────────────────────────────────────────

    private void MoveFocus(int delta)
    {
        if (_rows.Count == 0) return;
        _focusedRowIndex = Math.Clamp(_focusedRowIndex + delta, 0, _rows.Count - 1);
        // Single-select: ↑↓ pre-selects the focused row.
        if (!_vm.MultiSelect)
            _vm.Toggle(_rows[_focusedRowIndex].Info);
        SyncFocusHighlight();
    }

    /// <summary>
    /// Highlights the focused row in single-select by ensuring its IsSelected
    /// state is reflected. In multi-select the cyan border on selected rows
    /// already provides enough visual distinction; focus highlight is skipped.
    /// </summary>
    private void SyncFocusHighlight()
    {
        // No-op here; visual focus is handled via IsSelected in the DataTemplate
        // triggers. The VM's Toggle keeps single-select in sync with _focusedRowIndex.
    }

    // ── VM → row sync ────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes VM selection state into each <see cref="AltRowItem"/> so DataTemplate
    /// triggers (IsSelected, OrderTag) update without a converter.
    /// Also refreshes the Play button label and enabled state.
    /// </summary>
    private void SyncRowsFromVm()
    {
        foreach (var row in _rows)
        {
            row.IsSelected = _vm.IsSelected(row.Info);
            row.OrderTag = _vm.OrderTagFor(row.Info) is int n ? n.ToString() : string.Empty;
        }
        PlayButton.Content = _vm.PlayButtonLabel;
        PlayButton.IsEnabled = _vm.CanPlay;
    }

    // ── Confirm ──────────────────────────────────────────────────────────────

    private void Confirm()
    {
        SelectedTargets = _vm.SelectedTargets.ToList();
        DialogResult = true;
        Close();
    }
}
