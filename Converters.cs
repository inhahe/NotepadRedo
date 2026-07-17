using System.Globalization;
using System.Windows.Data;

namespace TreeNotepad;

/// <summary>
/// Computes the maximum width a history-node preview may occupy so it trims to the visible tree
/// pane with an ellipsis. Inputs: [0] the TreeView's ActualWidth, [1] the node's Depth. Each tree
/// level indents the row by roughly 19px, and we leave room for the expander, borders and the
/// vertical scrollbar.
/// </summary>
public sealed class PreviewWidthConverter : IMultiValueConverter
{
    private const double IndentPerLevel = 19;
    private const double ChromeAllowance = 30;   // expander + borders + scrollbar
    private const double MinWidth = 24;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double treeWidth || treeWidth <= 0)
            return double.PositiveInfinity;

        int depth = values[1] is int d ? d : 0;
        double avail = treeWidth - (depth + 1) * IndentPerLevel - ChromeAllowance;
        return avail < MinWidth ? MinWidth : avail;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
