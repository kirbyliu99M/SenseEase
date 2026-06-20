using System;
using System.Globalization;
using System.Windows.Data;
using SenseEase.App.Localization;

namespace SenseEase.App.Converters;

/// <summary>Accessible name for a score button: the bare number for the middle scores, but the END
/// anchors carry their meaning ("1, poor" / "5, great") so a screen reader announces the scale direction,
/// not just a digit. Mirrors the small visual anchors shown above the number row.</summary>
public sealed class ScoreToAccessibleNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int s) return value?.ToString() ?? string.Empty;
        return s switch
        {
            1 => LocalizationManager.Current.Pick("1，不滿意", "1, poor"),
            5 => LocalizationManager.Current.Pick("5，非常滿意", "5, great"),
            _ => s.ToString(CultureInfo.InvariantCulture),
        };
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// UI-RESHAPE iter 3 — true when a score button's own value equals the survey's
/// <c>SelectedScore</c>, so the chosen tile can stay highlighted after the click (the
/// 5-point QA needs persistent selection feedback, not just hover). MultiBinding:
/// values[0] = the button's int score, values[1] = the VM's nullable SelectedScore.
/// </summary>
public sealed class ScoreIsSelectedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values is { Length: 2 }
           && values[0] is int score
           && values[1] is int selected
           && score == selected;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
