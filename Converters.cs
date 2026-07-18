using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NotepadRedo;

/// <summary>
/// Turns a history row's indent level into a left margin so the flattened list still shows fork
/// structure: linear runs sit flush-left, and each branch step nudges its rows further right.
/// </summary>
public sealed class IndentMarginConverter : IValueConverter
{
    public const double IndentPerLevel = 16;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int level = value is int i ? i : 0;
        return new Thickness(level * IndentPerLevel, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Computes the maximum width a history-node preview may occupy so it trims to the visible list
/// pane with an ellipsis. Inputs: [0] the list's ActualWidth, [1] the row's indent level. Each
/// branch level indents the row, and we leave room for borders and the vertical scrollbar.
/// </summary>
public sealed class PreviewWidthConverter : IMultiValueConverter
{
    private const double IndentPerLevel = 16;
    private const double ChromeAllowance = 30;   // borders + scrollbar
    private const double MinWidth = 24;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double treeWidth || treeWidth <= 0)
            return double.PositiveInfinity;

        int level = values[1] is int d ? d : 0;
        double avail = treeWidth - (level + 1) * IndentPerLevel - ChromeAllowance;
        return avail < MinWidth ? MinWidth : avail;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
