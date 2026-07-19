using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NotepadRedo;

/// <summary>
/// A drop-in, themed replacement for <see cref="MessageBox.Show(Window, string, string,
/// MessageBoxButton, MessageBoxImage)"/>. The native message box is drawn by the OS and can't be
/// restyled, so we build a small modal <see cref="Window"/> from the app's theme brushes and let
/// Controls.xaml's implicit Button style handle the look (it auto-accents the default button).
///
/// Note: the native Save-As "overwrite? Yes/No" prompt comes from the OS SaveFileDialog and is
/// separate — it still can't be themed. Only the app's own confirmation/error prompts route here.
/// </summary>
internal static class ThemedDialog
{
    public static MessageBoxResult Show(Window? owner, string message, string title,
                                        MessageBoxButton buttons,
                                        MessageBoxImage icon = MessageBoxImage.None)
    {
        var dlg = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 340,
            MaxWidth = 560,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = Brush("Theme.WindowBg", Brushes.White),
        };

        var root = new DockPanel();

        // ----- footer band with the buttons, docked bottom -----
        var footer = new Border
        {
            Background = Brush("Theme.PanelBg", Brushes.WhiteSmoke),
            BorderBrush = Brush("Theme.PanelBorder", Brushes.Gainsboro),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10, 14, 12),
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        footer.Child = btnRow;

        // ----- content: optional icon + wrapped message -----
        var content = new Grid { Margin = new Thickness(18, 18, 18, 16) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = MakeIcon(icon);
        if (glyph is not null)
        {
            glyph.Margin = new Thickness(0, 0, 16, 0);
            glyph.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(glyph, 0);
            content.Children.Add(glyph);
        }

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Theme.EditorFg", Brushes.Black),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            MaxWidth = 460,
        };
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        root.Children.Add(footer);
        root.Children.Add(content);
        dlg.Content = root;

        var result = DefaultResult(buttons);

        void Add(string label, MessageBoxResult r, bool isDefault = false)
        {
            var b = new Button
            {
                Content = label,
                MinWidth = 82,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault,
            };
            b.Click += (_, _) => { result = r; dlg.Close(); };
            btnRow.Children.Add(b);
        }

        switch (buttons)
        {
            case MessageBoxButton.OK:
                Add("OK", MessageBoxResult.OK, isDefault: true);
                break;
            case MessageBoxButton.OKCancel:
                Add("OK", MessageBoxResult.OK, isDefault: true);
                Add("Cancel", MessageBoxResult.Cancel);
                break;
            case MessageBoxButton.YesNo:
                Add("Yes", MessageBoxResult.Yes, isDefault: true);
                Add("No", MessageBoxResult.No);
                break;
            case MessageBoxButton.YesNoCancel:
                Add("Yes", MessageBoxResult.Yes, isDefault: true);
                Add("No", MessageBoxResult.No);
                Add("Cancel", MessageBoxResult.Cancel);
                break;
        }

        // Escape maps to the natural "cancel" choice (Cancel, else No, else OK) and closes.
        dlg.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                result = DefaultResult(buttons);
                dlg.Close();
            }
        };

        dlg.ShowDialog();
        return result;
    }

    /// <summary>
    /// A themed "save changes?" prompt offering four choices as a horizontal button row:
    /// Save / Save All / Don't Save / Cancel. Returns 0=Save, 1=Save All, 2=Don't Save, 3=Cancel
    /// (Esc also yields 3). Used when quitting/closing with several unsaved documents, so the user
    /// can answer each one — or hit "Save All" to save the rest without further prompts.
    /// </summary>
    public static int ShowSaveAll(Window? owner, string message, string title)
    {
        var dlg = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 380,
            MaxWidth = 560,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = Brush("Theme.WindowBg", Brushes.White),
        };

        var root = new DockPanel();

        var footer = new Border
        {
            Background = Brush("Theme.PanelBg", Brushes.WhiteSmoke),
            BorderBrush = Brush("Theme.PanelBorder", Brushes.Gainsboro),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10, 14, 12),
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        footer.Child = btnRow;

        var content = new Grid { Margin = new Thickness(18, 18, 18, 16) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var glyph = MakeIcon(MessageBoxImage.Warning);
        if (glyph is not null)
        {
            glyph.Margin = new Thickness(0, 0, 16, 0);
            glyph.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(glyph, 0);
            content.Children.Add(glyph);
        }
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Theme.EditorFg", Brushes.Black),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            MaxWidth = 460,
        };
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        root.Children.Add(footer);
        root.Children.Add(content);
        dlg.Content = root;

        int result = 3;   // default / Esc → Cancel

        void Add(string label, int r, bool isDefault = false)
        {
            var b = new Button
            {
                Content = label,
                MinWidth = 82,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = isDefault,
            };
            b.Click += (_, _) => { result = r; dlg.Close(); };
            btnRow.Children.Add(b);
        }
        Add("Save", 0, isDefault: true);
        Add("Save All", 1);
        Add("Don't Save", 2);
        Add("Cancel", 3);

        dlg.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { result = 3; dlg.Close(); }
        };
        dlg.ShowDialog();
        return result;
    }

    /// <summary>
    /// A themed modal offering an arbitrary vertical list of choices (each a full-width button).
    /// Returns the 0-based index of the chosen option, or -1 if the dialog was dismissed (Esc /
    /// window close). Used for the external-change and mid-merge conflict prompts, which need more
    /// than the fixed Yes/No/Cancel sets. <paramref name="defaultIndex"/> is the Enter default.
    /// </summary>
    public static int ShowChoices(Window? owner, string message, string title,
                                  IReadOnlyList<string> choices, MessageBoxImage icon = MessageBoxImage.Question,
                                  int defaultIndex = 0)
    {
        var dlg = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 420,
            MaxWidth = 620,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            Background = Brush("Theme.WindowBg", Brushes.White),
        };

        int result = -1;

        var outer = new StackPanel { Margin = new Thickness(18, 18, 18, 14) };

        // Header: optional icon + message.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var glyph = MakeIcon(icon);
        if (glyph is not null)
        {
            glyph.Margin = new Thickness(0, 0, 16, 0);
            glyph.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(glyph, 0);
            header.Children.Add(glyph);
        }
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Theme.EditorFg", Brushes.Black),
            FontSize = 13,
            MaxWidth = 520,
        };
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        outer.Children.Add(header);

        // One full-width button per choice, stacked vertically.
        for (int i = 0; i < choices.Count; i++)
        {
            int idx = i;
            var b = new Button
            {
                Content = choices[i],
                Margin = new Thickness(0, i == 0 ? 16 : 6, 0, 0),
                Padding = new Thickness(12, 7, 12, 7),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                IsDefault = i == defaultIndex,
            };
            b.Click += (_, _) => { result = idx; dlg.Close(); };
            outer.Children.Add(b);
        }

        dlg.Content = outer;
        dlg.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { result = -1; dlg.Close(); }
        };
        dlg.ShowDialog();
        return result;
    }

    private static Brush Brush(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static MessageBoxResult DefaultResult(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OK => MessageBoxResult.OK,
        MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
        MessageBoxButton.YesNo => MessageBoxResult.No,
        MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
        _ => MessageBoxResult.None,
    };

    /// <summary>A round, coloured badge with a single glyph — mirrors the four MessageBoxImage kinds.</summary>
    private static FrameworkElement? MakeIcon(MessageBoxImage icon)
    {
        (string glyph, Color color) = icon switch
        {
            MessageBoxImage.Error       => ("\u00D7", Color.FromRgb(0xE8, 0x11, 0x23)), // ×
            MessageBoxImage.Warning     => ("!",      Color.FromRgb(0xE0, 0xA9, 0x3B)),
            MessageBoxImage.Question    => ("?",      Color.FromRgb(0x0F, 0x6C, 0xBD)),
            MessageBoxImage.Information  => ("i",      Color.FromRgb(0x0F, 0x6C, 0xBD)),
            _ => ("", Colors.Transparent),
        };
        if (glyph.Length == 0)
            return null;

        var grid = new Grid { Width = 34, Height = 34 };
        grid.Children.Add(new Ellipse { Fill = new SolidColorBrush(color) });
        grid.Children.Add(new TextBlock
        {
            Text = glyph,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = glyph == "i" ? new Thickness(0, -1, 0, 0) : default,
        });
        return grid;
    }
}
