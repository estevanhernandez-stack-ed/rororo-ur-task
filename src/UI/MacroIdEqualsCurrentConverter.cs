using System.Globalization;
using System.Windows.Data;

namespace Labs626.UrTask.UI;

/// <summary>
/// IMultiValueConverter: values[0] = macro row Id (string?),
/// values[1] = RecorderViewModel.CurrentlyPlayingMacroId (string?).
/// Returns true when both are non-empty and equal — drives the card's
/// cyan outline and PLAY → PLAYING badge swap.
/// </summary>
public sealed class MacroIdEqualsCurrentConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        var rowId = values[0] as string;
        var currentId = values[1] as string;
        return !string.IsNullOrEmpty(rowId) && rowId == currentId;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
