using System.Windows;
using System.Windows.Input;

namespace Labs626.UrTask.UI;

/// <summary>
/// Shown before archiving a macro that is paired with an alt or used in a recipe.
/// An unused macro archives without asking: archiving is a file move, and Restore
/// undoes it.
/// </summary>
public partial class ArchiveMacroConfirmDialog : Window
{
    public ArchiveMacroConfirmDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
        Loaded += (_, _) => { Activate(); Focus(); Keyboard.Focus(this); };
    }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void OnArchive(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        // Enter cancels, same as DeleteMacroConfirmDialog: this handler runs before IsDefault.
        else if (e.Key == Key.Enter) { DialogResult = false; Close(); }
    }
}
