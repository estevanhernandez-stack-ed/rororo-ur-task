using System.Globalization;
using System.Windows.Data;

namespace Labs626.UrTask.UI;

/// <summary>
/// IMultiValueConverter: values[0] = a row's int identifier (e.g. Alt.Pid),
/// values[1] = the currently-active identifier (e.g. CurrentRunnerAltPid).
/// Returns true when both are non-zero/non-negative-sentinel and equal —
/// drives the "currently playing this alt" row outline during round-robin
/// playback.
/// </summary>
public sealed class IntEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        if (values[0] is not int a || values[1] is not int b) return false;
        if (b < 0) return false; // sentinel for "no current alt"
        return a == b;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
