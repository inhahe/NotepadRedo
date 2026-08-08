using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NotepadRedo;

/// <summary>
/// Attached behaviour that shows a (long file) path in a <see cref="TextBlock"/> with a <b>leading</b>
/// ellipsis when it doesn't fit — dropping characters from the <i>front</i> so the informative tail
/// (the file name, its nearest folders, and any trailing dirty marker) stays visible. WPF's built-in
/// <see cref="TextTrimming.CharacterEllipsis"/> only trims the end, which would hide the file name; this
/// is the tab-header counterpart of MainWindow's window-title truncation.
///
/// Usage: set <c>local:LeadingEllipsisText.Path</c> (instead of <see cref="TextBlock.Text"/>) and leave
/// <see cref="TextBlock.TextTrimming"/> at <see cref="TextTrimming.None"/>. The display text is recomputed
/// whenever the path or the element's width changes.
/// </summary>
public static class LeadingEllipsisText
{
    public static readonly DependencyProperty PathProperty =
        DependencyProperty.RegisterAttached(
            "Path", typeof(string), typeof(LeadingEllipsisText),
            new PropertyMetadata(null, OnPathChanged));

    public static void SetPath(DependencyObject o, string value) => o.SetValue(PathProperty, value);
    public static string GetPath(DependencyObject o) => (string)o.GetValue(PathProperty);

    private static void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb)
            return;
        // Hook width changes exactly once, then (re)fit for the new path.
        tb.SizeChanged -= OnSizeChanged;
        tb.SizeChanged += OnSizeChanged;
        Apply(tb);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((TextBlock)sender);

    /// <summary>
    /// Re-fit the text after the caller changed the element's <see cref="FrameworkElement.MaxWidth"/>.
    /// Necessary because a *widened* cap produces no <see cref="FrameworkElement.SizeChanged"/> on its
    /// own — the already-elided text still measures at the old, narrower width, so nothing would ever
    /// prompt the element to grow back into the space it has just been given.
    /// </summary>
    public static void Refresh(TextBlock tb) => Apply(tb);

    /// <summary>
    /// Width in pixels the untruncated path would need. Used to divide the tab strip fairly: a tab
    /// whose whole path already fits in less than its equal share only takes what it needs, leaving
    /// the slack to the tabs that are actually being truncated.
    /// </summary>
    public static double MeasureFull(TextBlock tb)
    {
        string full = GetPath(tb) ?? "";
        return full.Length == 0 ? 0 : Measurer(tb)(full);
    }

    private static void Apply(TextBlock tb)
    {
        string full = GetPath(tb) ?? "";

        // Budget = the element's MaxWidth. A default TabControl sizes each tab to its content, so the
        // label's ActualWidth is driven by the text we put in it — fitting against ActualWidth would
        // feed back on itself and ratchet the tab narrower each pass. MaxWidth is set from *outside*
        // (MainWindow.UpdateTabWidths shares out the tab strip), so it's a stable, non-circular target:
        // a short path shows in full (the tab shrinks to it); a long one is fitted to the cap with a
        // leading ellipsis. Falls back to ActualWidth only if no finite cap is set.
        double budget = tb.MaxWidth;
        if (double.IsNaN(budget) || double.IsInfinity(budget) || budget <= 0)
            budget = tb.ActualWidth;

        string display = budget <= 0 ? full : Fit(tb, full, budget);
        if (!string.Equals(tb.Text, display, StringComparison.Ordinal))
            tb.Text = display;
    }

    /// <summary>A text-width function using the element's own typeface, size and DPI.</summary>
    private static Func<string, double> Measurer(TextBlock tb)
    {
        var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        double dpi;
        try { dpi = VisualTreeHelper.GetDpi(tb).PixelsPerDip; }
        catch { dpi = 1.0; }

        return s => new FormattedText(
            s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, tb.FontSize,
            Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
    }

    /// <summary>Longest suffix of <paramref name="full"/> that fits in <paramref name="avail"/> px,
    /// prefixed with a leading ellipsis when anything was dropped.</summary>
    private static string Fit(TextBlock tb, string full, double avail)
    {
        if (string.IsNullOrEmpty(full))
            return full;

        var Measure = Measurer(tb);

        if (Measure(full) <= avail)
            return full;

        // Binary-search the number of leading characters to drop.
        int lo = 0, hi = full.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            string candidate = "\u2026" + full.Substring(mid);
            if (Measure(candidate) <= avail) hi = mid;   // fits — try keeping more
            else lo = mid + 1;                            // too wide — drop more
        }
        return "\u2026" + full.Substring(Math.Min(lo, full.Length));
    }
}
