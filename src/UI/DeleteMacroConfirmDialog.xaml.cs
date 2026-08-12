using System.Windows;
using System.Windows.Input;

namespace Labs626.UrTask.UI;

public partial class DeleteMacroConfirmDialog : Window
{
    public DeleteMacroConfirmDialog(string macroName)
    {
        InitializeComponent();
        MessageText.Text = $"Delete macro \"{macroName}\"?\nThis can't be undone.";
        Loaded += (_, _) => { Activate(); Focus(); Keyboard.Focus(this); };
    }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void OnDelete(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        // Enter CANCELS. This handler is what actually decides — it runs before the
        // markup's IsDefault ever gets a say, which is why setting IsDefault on CANCEL
        // alone changed nothing and Enter still deleted (found in smoke, 2026-08-11).
        else if (e.Key == Key.Enter) { DialogResult = false; Close(); }
    }
}
