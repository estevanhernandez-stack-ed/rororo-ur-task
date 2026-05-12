using System.Windows;
using System.Windows.Input;

namespace Labs626.UrTask.UI;

public partial class RenameMacroDialog : Window
{
    public RenameMacroDialog(string currentName)
    {
        InitializeComponent();
        NameInput.Text = currentName;
        Loaded += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
    }

    public string NewName => NameInput.Text;

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void OnRename(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        else if (e.Key == Key.Enter) { DialogResult = true; Close(); }
    }
}
