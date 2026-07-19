using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NotepadRedo;

/// <summary>
/// A live font picker. Unlike the WinForms FontDialog (which only fires on an explicit "Apply"),
/// this raises <see cref="SelectionChanged"/> on every family / size / style change so the caller
/// can preview the choice in the real editor as the user browses. The caller restores the original
/// font if the dialog is cancelled.
/// </summary>
public partial class FontPickerWindow : Window
{
    /// <summary>Points-to-WPF-pixels factor for the in-dialog preview.</summary>
    private const double PointsToPixels = 96.0 / 72.0;

    private static readonly double[] PresetSizes =
        { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48, 72 };

    private readonly List<string> _allFamilies;
    private bool _ready;

    public string SelectedFamily { get; private set; }
    public double SelectedSize { get; private set; }
    public bool Bold { get; private set; }
    public bool Italic { get; private set; }

    /// <summary>Raised whenever the family, size, or style changes (for live preview).</summary>
    public event Action? SelectionChanged;

    public FontPickerWindow(string family, double sizePt, bool bold, bool italic)
    {
        InitializeComponent();

        SelectedFamily = family;
        SelectedSize = sizePt;
        Bold = bold;
        Italic = italic;

        _allFamilies = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        FamilyList.ItemsSource = _allFamilies;
        SizeList.ItemsSource = PresetSizes.Select(FormatSize).ToList();

        SelectFamily(family);
        SizeBox.Text = FormatSize(sizePt);
        BoldCheck.IsChecked = bold;
        ItalicCheck.IsChecked = italic;

        _ready = true;
        FamilyList.ScrollIntoView(FamilyList.SelectedItem);
        UpdatePreview();

        // Land the caret in the filter box so the user can type a font name immediately.
        Loaded += (_, _) => { FilterBox.Focus(); FilterBox.SelectAll(); };
    }

    private static string FormatSize(double pt) =>
        pt == Math.Floor(pt) ? ((int)pt).ToString() : pt.ToString("0.#", CultureInfo.CurrentCulture);

    private void SelectFamily(string family)
    {
        var match = _allFamilies.FirstOrDefault(n => string.Equals(n, family, StringComparison.OrdinalIgnoreCase))
                    ?? _allFamilies.FirstOrDefault();
        FamilyList.SelectedItem = match;
    }

    private void Raise()
    {
        if (!_ready)
            return;
        UpdatePreview();
        SelectionChanged?.Invoke();
    }

    private void UpdatePreview()
    {
        try
        {
            Preview.FontFamily = new FontFamily(SelectedFamily);
            Preview.FontSize = SelectedSize * PointsToPixels;
            Preview.FontWeight = Bold ? FontWeights.Bold : FontWeights.Normal;
            Preview.FontStyle = Italic ? FontStyles.Italic : FontStyles.Normal;
        }
        catch { /* a transiently invalid family/size must never crash the picker */ }
    }

    // ---- family ----

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
            return;
        string q = FilterBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allFamilies
            : _allFamilies.Where(n => n.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        FamilyList.ItemsSource = filtered;

        // Keep the current family selected if it's still in the filtered view; otherwise
        // auto-select the top match so simply typing a name (and hitting OK/Enter) commits it.
        string? pick = filtered.FirstOrDefault(n => string.Equals(n, SelectedFamily, StringComparison.OrdinalIgnoreCase))
                       ?? filtered.FirstOrDefault();
        FamilyList.SelectedItem = pick;
        if (pick is not null)
            FamilyList.ScrollIntoView(pick);
    }

    private void Family_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FamilyList.SelectedItem is string fam)
        {
            SelectedFamily = fam;
            Raise();
        }
    }

    // ---- size ----

    private void Size_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
            return;
        if (double.TryParse(SizeBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double pt)
            && pt >= 1 && pt <= 1000)
        {
            SelectedSize = pt;
            Raise();
        }
    }

    private void SizeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && SizeList.SelectedItem is string s)
            SizeBox.Text = s;   // drives Size_TextChanged, which raises the change
    }

    // ---- style ----

    private void Style_Changed(object sender, RoutedEventArgs e)
    {
        Bold = BoldCheck.IsChecked == true;
        Italic = ItalicCheck.IsChecked == true;
        Raise();
    }

    // ---- buttons ----

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
