using System;
using System.Globalization;
using System.Windows.Data;

namespace SenseEase.App.Converters;

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
