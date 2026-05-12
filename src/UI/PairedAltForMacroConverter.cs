using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Labs626.UrTask.UI;

/// <summary>
/// IMultiValueConverter: values[0] = macro Id (string),
/// values[1] = PairedAltByMacroId (IReadOnlyDictionary&lt;string, string&gt;).
/// Returns the paired alt's DisplayName, or empty string if this macro isn't paired.
/// Used by per-card "→ AltName" chip to show all paired macros simultaneously
/// (the multi-pair visibility that rc9 was missing).
/// </summary>
public sealed class PairedAltForMacroConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return string.Empty;
        var macroId = values[0] as string;
        var map = values[1] as IReadOnlyDictionary<string, string>;
        if (string.IsNullOrEmpty(macroId) || map is null) return string.Empty;
        return map.TryGetValue(macroId, out var altName) ? altName : string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// IMultiValueConverter mirror of PairedAltForMacro: returns Visibility.Visible
/// when the macro has a paired alt, Collapsed otherwise. Drives the chip visibility.
/// </summary>
public sealed class PairedAltVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return Visibility.Collapsed;
        var macroId = values[0] as string;
        var map = values[1] as IReadOnlyDictionary<string, string>;
        if (string.IsNullOrEmpty(macroId) || map is null) return Visibility.Collapsed;
        return map.ContainsKey(macroId) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
