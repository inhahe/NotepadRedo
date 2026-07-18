using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace TreeNotepad;

/// <summary>
/// Shell window: hosts a TabControl of <see cref="EditorView"/> documents, the menu, and a
/// shared status bar reflecting the active tab. Tabs can be torn off into new windows and
/// dragged back between windows.
/// </summary>
public partial class MainWindow : Window
{
    private const string TabDragFormat = "TreeNotepadTab";     // marker: this drag is a tab
    private const string DocFormat = "TreeNotepadDoc";         // JSON DocDto for cross-process moves
    private const string PidFormat = "TreeNotepadPid";         // origin process id
    private const string TokenFormat = "TreeNotepadToken";     // origin document RecoveryId

    /// <summary>In-process handoff state for a tab drag (never serialised across processes).</summary>
    private static class TabDrag
    {
        public static TabItem? Item;
        public static MainWindow? Source;
    }

    private Point _dragStart;
    private bool _dragArmed;

    public MainWindow()
    {
        InitializeComponent();
        SetUpKeyBindings();
        SyncOptionMenus();

        Loaded += (_, _) => UpdateChrome();
    }

    /// <summary>Seed a freshly-shown window: open the given files, or offer recovery when there are none.</summary>
    public void Initialize(IEnumerable<string> files, bool blankRequested)
    {
        var list = files.ToList();
        foreach (var f in list)
            RequestOpenFile(f);   // focuses instead of duplicating if already open here

        if (Tabs.Items.Count == 0 && !blankRequested && list.Count == 0)
            OfferRecovery();

        if (Tabs.Items.Count == 0)
            AddView(CreateBlankView(), select: true);

        ActiveView?.FocusEditor();
    }

    /// <summary>Prompt to restore snapshots left behind by a crashed session. Returns true if any restored.</summary>
    private bool OfferRecovery()
    {
        var snaps = EditorView.ScanRecoveries().ToList();
        if (snaps.Count == 0)
            return false;

        var result = MessageBox.Show(this,
            $"{snaps.Count} unsaved document(s) from a previous session were found.\n\nRecover them?",
            "TreeNotepad \u2014 Recover unsaved work",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            EditorView.ClearAllRecoveries();
            return false;
        }

        foreach (var snap in snaps)
        {
            var view = CreateBlankView();
            view.LoadRecovered(snap);
            AddView(view, select: true);
        }
        return true;
    }

    // ===================== Active tab helpers =====================

    private EditorView? ActiveView => (Tabs.SelectedItem as TabItem)?.Content as EditorView;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs))
            return;
        UpdateChrome();
        ActiveView?.FocusEditor();
    }

    private void UpdateChrome()
    {
        var view = ActiveView;
        if (view is null)
        {
            Title = "TreeNotepad";
            return;
        }
        Title = view.TabTitle + " - TreeNotepad";
        CaretStatus.Text = view.CaretText;
        CountStatus.Text = view.CountText;
        NodeStatus.Text = view.NodeText;
        SaveStatus.Text = view.SaveText;
        SaveStatus.Foreground = view.IsDirty ? Brushes.Firebrick : Brushes.ForestGreen;
    }

    private void View_Changed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, ActiveView))
            UpdateChrome();
    }

    // ===================== Tab lifecycle =====================

    private EditorView CreateBlankView() => new();

    private void OpenFileInTab(string path)
    {
        var view = CreateBlankView();
        try
        {
            view.LoadFile(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        AddView(view, select: true);
    }

    /// <summary>
    /// Open a file, but if it is already open — in this process or another instance — just
    /// focus the existing tab instead of creating a duplicate. Returns true if it was focused
    /// (rather than newly opened here).
    /// </summary>
    private bool RequestOpenFile(string path)
    {
        if (TryFocusDocument(path) || IpcServer.TryFocusInSibling(path))
            return true;
        OpenFileInTab(path);
        return false;
    }

    /// <summary>Canonical form for comparing file paths (case-insensitive on Windows).</summary>
    public static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return path; }
    }

    /// <summary>
    /// If any window in this process has the file open, select that tab and bring the window
    /// forward. Returns true when the document was found and focused.
    /// </summary>
    public static bool TryFocusDocument(string path)
    {
        string norm = NormalizePath(path);
        foreach (Window w in Application.Current.Windows)
        {
            if (w is not MainWindow mw)
                continue;
            var ti = mw.Tabs.Items.OfType<TabItem>().FirstOrDefault(t =>
                t.Content is EditorView v && v.FilePath is string p &&
                string.Equals(NormalizePath(p), norm, StringComparison.OrdinalIgnoreCase));
            if (ti is null)
                continue;

            mw.Tabs.SelectedItem = ti;
            mw.ForceForeground();
            (ti.Content as EditorView)?.FocusEditor();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Open a file as a new tab in an existing window of this process (tab-mode consolidation
    /// from another launch). Focuses it if it happens to be open already. Always succeeds when
    /// there is a window to host it.
    /// </summary>
    public static bool OpenDocument(string path)
    {
        if (TryFocusDocument(path))
            return true;
        var mw = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (mw is null)
            return false;
        mw.OpenFileInTab(path);
        mw.ForceForeground();
        return true;
    }

    /// <summary>
    /// Remove (and dispose) the tab whose document has the given RecoveryId, wherever it lives
    /// in this process. Used after a tab is torn off into another process. Returns true if found.
    /// </summary>
    public static bool CloseTabByToken(string token)
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is not MainWindow mw)
                continue;
            var ti = mw.Tabs.Items.OfType<TabItem>().FirstOrDefault(t =>
                t.Content is EditorView v && v.RecoveryId == token);
            if (ti is null)
                continue;
            mw.RemoveTab(ti, dispose: true);
            mw.CloseIfEmpty();
            return true;
        }
        return false;
    }

    /// <summary>Restore (if minimised) and force this window to the foreground.</summary>
    public void ForceForeground()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SetForegroundWindow(handle);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Wrap a live EditorView in a tab (with a full-path header + close button) and add it.</summary>
    private void AddView(EditorView view, bool select)
    {
        view.StatusChanged += View_Changed;
        view.TitleChanged += View_Changed;

        var ti = new TabItem { Content = view, Header = BuildHeader(view) };
        Tabs.Items.Add(ti);
        if (select)
            Tabs.SelectedItem = ti;
        UpdateChrome();
    }

    private FrameworkElement BuildHeader(EditorView view)
    {
        var panel = new DockPanel { DataContext = view, LastChildFill = true };

        var close = new Button
        {
            Style = (Style)Application.Current.FindResource("TabCloseButton"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Close tab (Ctrl+W)"
        };
        close.Click += (_, _) => CloseTab(FindTab(view));
        DockPanel.SetDock(close, Dock.Right);

        var label = new TextBlock
        {
            MaxWidth = 260,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(EditorView.TabTitle)));
        label.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(EditorView.TabTitle)));

        panel.Children.Add(close);
        panel.Children.Add(label);
        return panel;
    }

    private TabItem? FindTab(EditorView view) =>
        Tabs.Items.OfType<TabItem>().FirstOrDefault(t => ReferenceEquals(t.Content, view));

    /// <summary>Close a tab, prompting to save first. Closes the window when the last tab goes.</summary>
    private bool CloseTab(TabItem? ti)
    {
        if (ti?.Content is not EditorView view)
            return true;
        Tabs.SelectedItem = ti;
        if (!view.ConfirmDiscardIfDirty())
            return false;
        RemoveTab(ti, dispose: true);
        if (Tabs.Items.Count == 0)
        {
            _realClose = true;   // closing the final tab really closes the window
            Close();
        }
        return true;
    }

    /// <summary>Detach a tab from the tab strip without disposing its document.</summary>
    private void RemoveTab(TabItem ti, bool dispose)
    {
        if (ti.Content is EditorView view)
        {
            view.StatusChanged -= View_Changed;
            view.TitleChanged -= View_Changed;
            ti.Content = null;          // release so the control can be re-parented
            if (dispose)
                view.Dispose();
        }
        Tabs.Items.Remove(ti);
        UpdateChrome();
    }

    /// <summary>Re-host a live EditorView (moved from another window) in a fresh tab here.</summary>
    private void AdoptView(EditorView view, bool select)
    {
        AddView(view, select);
        Activate();
    }

    // ===================== Menu: File =====================

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (AppSettings.Current.OpenInNewInstance)
            LaunchInstance("--new");
        else
            AddView(CreateBlankView(), select: true);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != true)
            return;

        OpenPaths(dlg.FileNames);
    }

    /// <summary>
    /// Open each path, honouring the tab-vs-instance preference and de-duplicating: a file that is
    /// already open anywhere just gets focused. Used by File &gt; Open and by file drag-and-drop.
    /// </summary>
    private void OpenPaths(IEnumerable<string> paths)
    {
        foreach (var f in paths)
        {
            // Already open somewhere? Just focus it, regardless of the tab/instance setting.
            if (TryFocusDocument(f) || IpcServer.TryFocusInSibling(f))
                continue;
            if (AppSettings.Current.OpenInNewInstance)
                LaunchInstance(f);
            else
                OpenFileInTab(f);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => ActiveView?.Save(false);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => ActiveView?.Save(true);
    private void CloseTab_Click(object sender, RoutedEventArgs e) => CloseTab(Tabs.SelectedItem as TabItem);

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _realClose = true;   // File > Exit always really closes, ignoring the X-button behaviour
        Close();
    }

    private static void LaunchInstance(string arg)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false };
            psi.ArgumentList.Add(arg);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Launch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ===================== Menu: Edit / View =====================

    private void Undo_Click(object sender, RoutedEventArgs e) => ActiveView?.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => ActiveView?.Redo();

    private void WordWrap_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.WordWrap = WordWrapItem.IsChecked;
        AppSettings.Current.Save();
        foreach (var v in AllViews())
            v.ApplyWordWrap(AppSettings.Current.WordWrap);
    }

    // ===================== Menu: Format (editor font) =====================

    /// <summary>Open the live font picker: the editor previews the selection as the user browses,
    /// and the choice is committed on OK or reverted on Cancel.</summary>
    private void Font_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettings.Current;
        // Remember the current font so Cancel can restore it after live previewing.
        string origFamily = s.FontFamily;
        double origSize = s.FontSize;
        bool origBold = s.FontBold, origItalic = s.FontItalic;

        var dlg = new FontPickerWindow(origFamily, origSize, origBold, origItalic) { Owner = this };
        dlg.SelectionChanged += () =>
            PreviewFont(dlg.SelectedFamily, dlg.SelectedSize, dlg.Bold, dlg.Italic);

        if (dlg.ShowDialog() == true)
        {
            s.FontFamily = dlg.SelectedFamily;
            s.FontSize   = dlg.SelectedSize;
            s.FontBold   = dlg.Bold;
            s.FontItalic = dlg.Italic;
            ApplyFontEverywhere();
        }
        else
        {
            // Cancelled — undo the live preview without persisting anything.
            PreviewFont(origFamily, origSize, origBold, origItalic);
        }
    }

    /// <summary>Apply a font to every open editor for preview only (no persistence).</summary>
    private static void PreviewFont(string family, double sizePt, bool bold, bool italic)
    {
        foreach (var v in AllOpenViews())
            v.ApplyFont(family, sizePt, bold, italic);
    }

    /// <summary>Every open document across every window.</summary>
    private static IEnumerable<EditorView> AllOpenViews()
    {
        foreach (Window w in Application.Current.Windows)
            if (w is MainWindow mw)
                foreach (var v in mw.AllViews())
                    yield return v;
    }

    private void Bold_Click(object sender, RoutedEventArgs e)   => ToggleBold();
    private void Italic_Click(object sender, RoutedEventArgs e) => ToggleItalic();

    private void ToggleBold()
    {
        AppSettings.Current.FontBold = !AppSettings.Current.FontBold;
        ApplyFontEverywhere();
    }

    private void ToggleItalic()
    {
        AppSettings.Current.FontItalic = !AppSettings.Current.FontItalic;
        ApplyFontEverywhere();
    }

    private void FontSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag || !double.TryParse(tag, out double pt))
            return;
        AppSettings.Current.FontSize = pt;
        ApplyFontEverywhere();
    }

    /// <summary>Persist the shared editor font and apply it to every open document in every window.</summary>
    private static void ApplyFontEverywhere()
    {
        var s = AppSettings.Current;
        s.Save();
        foreach (Window w in Application.Current.Windows)
        {
            if (w is not MainWindow mw)
                continue;
            mw.SyncOptionMenus();
            foreach (var v in mw.AllViews())
                v.ApplyFont(s.FontFamily, s.FontSize, s.FontBold, s.FontItalic);
        }
    }

    private void ShowTree_Click(object sender, RoutedEventArgs e) => SetTreePreference(ShowTreeItem.IsChecked);

    private void ToggleTree_Click(object sender, RoutedEventArgs e) => SetTreePreference(TreeToggle.IsChecked == true);

    /// <summary>Persist the history-tree preference and apply it to every open document.</summary>
    private static void SetTreePreference(bool show)
    {
        AppSettings.Current.ShowTree = show;
        AppSettings.Current.Save();
        foreach (Window w in Application.Current.Windows)
        {
            if (w is not MainWindow mw)
                continue;
            mw.SyncOptionMenus();
            foreach (var v in mw.AllViews())
                v.ApplyTreeVisible(show);
        }
    }

    // ===================== Menu: Options =====================

    private void OpenTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag)
            return;
        AppSettings.Current.OpenInNewInstance = tag == "instance";
        AppSettings.Current.Save();
        SyncOptionMenus();
    }

    private void AutosaveInterval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag || !int.TryParse(tag, out int seconds))
            return;
        AppSettings.Current.AutosaveSeconds = seconds;
        AppSettings.Current.Save();
        SyncOptionMenus();

        // Apply to every open document across every window.
        foreach (Window w in Application.Current.Windows)
            if (w is MainWindow mw)
                foreach (var v in mw.AllViews())
                    v.ApplyAutosaveInterval(seconds);
    }

    // ----- Undo grouping -----

    private void UndoBreakEnter_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.UndoBreakOnEnter = UndoBreakEnter.IsChecked;
        SaveAndSyncOptions();
    }

    private void UndoBreakPaste_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.UndoBreakOnPaste = UndoBreakPaste.IsChecked;
        SaveAndSyncOptions();
    }

    private void UndoPerChar_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.UndoPerCharacter = UndoPerChar.IsChecked;
        SaveAndSyncOptions();
    }

    private void UndoPause_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag ||
            !double.TryParse(tag, out double seconds))
            return;
        AppSettings.Current.UndoCoalesceSeconds = seconds;
        SaveAndSyncOptions();
    }

    private void UndoPauseCustom_Click(object sender, RoutedEventArgs e)
    {
        double current = AppSettings.Current.UndoCoalesceSeconds;
        if (PromptForSeconds(current, out double seconds))
            AppSettings.Current.UndoCoalesceSeconds = seconds;
        SaveAndSyncOptions();   // re-sync either way so the checkmarks reflect the real value
    }

    /// <summary>Persist settings and refresh every window's Options-menu checkmarks.</summary>
    private static void SaveAndSyncOptions()
    {
        AppSettings.Current.Save();
        foreach (Window w in Application.Current.Windows)
            if (w is MainWindow mw)
                mw.SyncOptionMenus();
    }

    /// <summary>Modal prompt for a positive number of seconds. Returns false if cancelled or invalid.</summary>
    private bool PromptForSeconds(double current, out double seconds)
    {
        seconds = current;
        var dlg = new Window
        {
            Title = "Typing-pause length",
            Width = 300, Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "New undo step after a pause of (seconds):",
            Margin = new Thickness(0, 0, 0, 6)
        });
        var box = new TextBox { Text = current.ToString(CultureInfo.CurrentCulture) };
        box.SelectAll();
        panel.Children.Add(box);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var ok = new Button { Content = "OK", Width = 74, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 74, IsCancel = true };
        ok.Click += (_, _) => dlg.DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dlg.Content = panel;
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        if (dlg.ShowDialog() != true)
            return false;
        if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double v)
            && v >= 0 && v <= 3600)
        {
            seconds = v;
            return true;
        }
        return false;
    }

    private void CloseBehavior_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag ||
            !Enum.TryParse<CloseButtonBehavior>(tag, out var behavior))
            return;
        AppSettings.Current.CloseButton = behavior;
        AppSettings.Current.Save();
        // Keep every window's Options menu in sync with the shared preference.
        foreach (Window w in Application.Current.Windows)
            if (w is MainWindow mw)
                mw.SyncOptionMenus();
    }

    private void SyncOptionMenus()
    {
        var s = AppSettings.Current;
        WordWrapItem.IsChecked = s.WordWrap;
        ShowTreeItem.IsChecked = s.ShowTree;
        TreeToggle.IsChecked = s.ShowTree;
        OpenInTabItem.IsChecked = !s.OpenInNewInstance;
        OpenInInstanceItem.IsChecked = s.OpenInNewInstance;

        AutoOff.IsChecked = s.AutosaveSeconds == 0;
        Auto15.IsChecked = s.AutosaveSeconds == 15;
        Auto30.IsChecked = s.AutosaveSeconds == 30;
        Auto60.IsChecked = s.AutosaveSeconds == 60;
        Auto300.IsChecked = s.AutosaveSeconds == 300;

        CloseCloses.IsChecked    = s.CloseButton == CloseButtonBehavior.Close;
        CloseToTray.IsChecked    = s.CloseButton == CloseButtonBehavior.MinimizeToTray;
        CloseToTaskbar.IsChecked = s.CloseButton == CloseButtonBehavior.MinimizeToTaskbar;

        BoldItem.IsChecked   = s.FontBold;
        ItalicItem.IsChecked = s.FontItalic;
        foreach (var item in FontSizeMenu.Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is string t && double.TryParse(t, out double pt) && pt == s.FontSize;

        UndoBreakEnter.IsChecked = s.UndoBreakOnEnter;
        UndoBreakPaste.IsChecked = s.UndoBreakOnPaste;
        UndoPerChar.IsChecked    = s.UndoPerCharacter;
        UndoPause1.IsChecked = s.UndoCoalesceSeconds == 1;
        UndoPause2.IsChecked = s.UndoCoalesceSeconds == 2;
        UndoPause4.IsChecked = s.UndoCoalesceSeconds == 4;
        UndoPause8.IsChecked = s.UndoCoalesceSeconds == 8;
        UndoPauseCustom.IsChecked = s.UndoCoalesceSeconds is not (1 or 2 or 4 or 8);
    }

    private IEnumerable<EditorView> AllViews() =>
        Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<EditorView>();

    /// <summary>Public view over this window's open documents (used for process-wide setting fan-out).</summary>
    public IEnumerable<EditorView> AllEditorViews() => AllViews();

    private void SetUpKeyBindings()
    {
        void Bind(Key key, ModifierKeys mods, Action action) =>
            InputBindings.Add(new KeyBinding(new RelayCommand(action), new KeyGesture(key, mods)));

        Bind(Key.Z, ModifierKeys.Control, () => ActiveView?.Undo());
        Bind(Key.Y, ModifierKeys.Control, () => ActiveView?.Redo());
        Bind(Key.Z, ModifierKeys.Control | ModifierKeys.Shift, () => ActiveView?.Redo());
        Bind(Key.N, ModifierKeys.Control, () => New_Click(this, new RoutedEventArgs()));
        Bind(Key.O, ModifierKeys.Control, () => Open_Click(this, new RoutedEventArgs()));
        Bind(Key.S, ModifierKeys.Control, () => ActiveView?.Save(false));
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Shift, () => ActiveView?.Save(true));
        Bind(Key.W, ModifierKeys.Control, () => CloseTab(Tabs.SelectedItem as TabItem));
        Bind(Key.F4, ModifierKeys.Control, () => CloseTab(Tabs.SelectedItem as TabItem));
        Bind(Key.B, ModifierKeys.Control, ToggleBold);
        Bind(Key.I, ModifierKeys.Control, ToggleItalic);
    }

    // ===================== Tab drag: tear-off & reattach =====================

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // These are Preview (tunneling) handlers on the TabItem, but the selected tab's *content*
        // (editor, history tree, divider, scrollbars) routes its mouse events through the TabItem
        // too. Only arm a tab drag when the press is genuinely on the tab HEADER — otherwise any
        // press-drag inside the editor area would launch a phantom DoDragDrop that steals the mouse
        // capture and breaks every slider/scrollbar/divider drag (and text selection).
        if (sender is not TabItem ti || !IsOnTabHeader(ti, e.OriginalSource as DependencyObject))
        {
            _dragArmed = false;
            return;
        }
        _dragStart = e.GetPosition(null);
        _dragArmed = true;
    }

    /// <summary>True when <paramref name="source"/> lies within the tab's header chrome (a visual
    /// descendant of the TabItem) rather than in its hosted content. The content reaches the
    /// TabItem only through logical/routed links, never as a visual descendant, so a pure visual
    /// walk cleanly tells the two apart.</summary>
    private static bool IsOnTabHeader(TabItem ti, DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ti))
                return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.MiddleButton == MouseButtonState.Pressed && sender is TabItem ti)
            CloseTab(ti);   // middle-click closes a tab
    }

    private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || sender is not TabItem ti)
            return;
        // Belt-and-braces: never start a drag from a press that wandered in from the content.
        if (!IsOnTabHeader(ti, e.OriginalSource as DependencyObject))
        {
            _dragArmed = false;
            return;
        }

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArmed = false;
        BeginTabDrag(ti);
    }

    private void BeginTabDrag(TabItem ti)
    {
        if (ti.Content is not EditorView view)
            return;

        TabDrag.Item = ti;
        TabDrag.Source = this;

        // Carry both an in-process handle (via the static) and a fully serialised copy so the
        // tab can be reconstructed in another process. The pid lets a drop target tell the two
        // paths apart; the token lets the origin be asked to drop its copy after a cross-process move.
        var data = new DataObject();
        data.SetData(TabDragFormat, "1");
        data.SetData(PidFormat, Environment.ProcessId.ToString());
        data.SetData(TokenFormat, view.RecoveryId);
        try { data.SetData(DocFormat, JsonSerializer.Serialize(view.SerializeDocument())); }
        catch { /* worst case: cross-process drop is a no-op, local move still works */ }

        // WPF's DoDragDrop draws no drag image, so a torn-off tab used to give no visual feedback.
        // Float a small click-through label under the cursor for the duration of the drag. The
        // ghost is best-effort: any failure here must never disturb the actual drag/drop.
        Window? ghost = null;
        QueryContinueDragEventHandler? onQuery = null;
        try { ghost = CreateDragGhost(view.TabTitle); PositionGhost(ghost); } catch { ghost = null; }
        if (ghost is not null)
        {
            onQuery = (_, _) => PositionGhost(ghost);
            ti.QueryContinueDrag += onQuery;
        }

        try
        {
            var effect = DragDrop.DoDragDrop(ti, data, DragDropEffects.Move);

            // No window accepted the drop (and the tab is still ours) → tear off at the cursor.
            if (effect != DragDropEffects.Move && ReferenceEquals(TabDrag.Item, ti) && FindTab(view) is not null)
                DetachToNewWindow(ti);
        }
        finally
        {
            if (onQuery is not null) ti.QueryContinueDrag -= onQuery;
            try { ghost?.Close(); } catch { /* already gone */ }
            TabDrag.Item = null;
            TabDrag.Source = null;
        }
    }

    /// <summary>A small translucent label that trails the cursor while a tab is being dragged.</summary>
    private Window CreateDragGhost(string title)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,          // never steal focus / capture from the drag
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            IsHitTestVisible = false,
            Focusable = false,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 45, 45, 48)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 110, 110, 115)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10, 5, 10, 5),
                Child = new TextBlock
                {
                    Text = string.IsNullOrEmpty(title) ? "Untitled" : title,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 360,
                }
            }
        };
        // Make it click-through at the Win32 level so it never intercepts OLE drop hit-testing.
        w.SourceInitialized += (_, _) => MakeClickThrough(w);
        w.Show();
        return w;
    }

    private void PositionGhost(Window ghost)
    {
        try
        {
            var p = CursorPositionDip();
            ghost.Left = p.X + 14;   // offset so the cursor hotspot stays clear of the label
            ghost.Top = p.Y + 8;
        }
        catch { /* best-effort */ }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private static void MakeClickThrough(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        }
        catch { /* click-through is a nicety, not required */ }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (!e.Data.GetDataPresent(TabDragFormat))
            return;   // leave ordinary text drops for the editor to handle
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    // File drops from Explorer are intercepted at the tunnelling (Preview) stage so they open as
    // tabs instead of falling through to the editor TextBox, which would otherwise insert their
    // contents/paths into — effectively replacing — the current document.
    protected override void OnPreviewDragOver(DragEventArgs e)
    {
        base.OnPreviewDragOver(e);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    protected override void OnPreviewDrop(DragEventArgs e)
    {
        base.OnPreviewDrop(e);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;
        e.Handled = true;
        OpenPaths(files);
        ForceForeground();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (!e.Data.GetDataPresent(TabDragFormat))
            return;

        e.Handled = true;
        e.Effects = DragDropEffects.Move;   // accepted → origin's DoDragDrop returns Move (no tear-off)

        int srcPid = (e.Data.GetData(PidFormat) as string) is string ps && int.TryParse(ps, out int p) ? p : -1;

        if (srcPid == Environment.ProcessId)
        {
            // Same process: move the live control, preserving all in-memory state and UI.
            if (TabDrag.Item is not TabItem ti || ti.Content is not EditorView view)
                return;
            if (ReferenceEquals(TabDrag.Source, this))
                return;   // dropped back on its own window: keep it where it is
            TabDrag.Source?.RemoveTab(ti, dispose: false);
            AdoptView(view, select: true);
            TabDrag.Source?.CloseIfEmpty();
            return;
        }

        // Cross process: reconstruct the document from its serialised form, then ask the origin
        // process to drop its now-moved tab.
        if (e.Data.GetData(DocFormat) is not string json || string.IsNullOrEmpty(json))
            return;
        EditorView.DocDto? dto = null;
        try { dto = JsonSerializer.Deserialize<EditorView.DocDto>(json); }
        catch { }
        if (dto is null)
            return;

        var adopted = CreateBlankView();
        adopted.LoadTransferred(dto);
        AdoptView(adopted, select: true);
        ForceForeground();

        if (e.Data.GetData(TokenFormat) is string token && !string.IsNullOrEmpty(token))
        {
            // Fire-and-forget on a background thread — do NOT block here. We are running inside the
            // OLE drop callback while the source process is still blocked in DoDragDrop; it can only
            // service our CLOSE request after its Drop call (i.e. this method) returns. Blocking on
            // the pipe round-trip here would deadlock both processes (source waits on us, we wait on
            // source). Returning promptly lets the source's DoDragDrop finish and then answer the
            // CLOSE. The source only tears off when the drop reports something other than Move, and
            // we set Move above, so the leftover tab is simply closed a moment later.
            int pidToClose = srcPid;
            Task.Run(() => IpcServer.CloseTabInProcess(pidToClose, token));
        }
    }

    private void DetachToNewWindow(TabItem ti)
    {
        if (ti.Content is not EditorView view)
            return;
        // A single tab in a single window has nowhere better to go.
        if (Tabs.Items.Count <= 1)
            return;

        RemoveTab(ti, dispose: false);

        var w = new MainWindow();
        var p = CursorPositionDip();
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = p.X - 40;
        w.Top = p.Y - 10;
        w.Show();
        w.AdoptView(view, select: true);

        CloseIfEmpty();
    }

    private void CloseIfEmpty()
    {
        if (Tabs.Items.Count == 0)
            Close();
    }

    private Point CursorPositionDip()
    {
        GetCursorPos(out POINT p);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    // ===================== Closing =====================

    /// <summary>Set while an external tool (build/deploy) is force-closing the app.</summary>
    private static bool _forceQuitting;

    /// <summary>
    /// Flush every open document to crash recovery, then shut the whole app down with no save
    /// prompts. Used when a build/deploy needs the exe closed but must not lose unsaved work —
    /// the recovery snapshots are offered again on the next launch. Runs on the UI thread.
    /// </summary>
    public static bool RequestQuitWithRecovery()
    {
        _forceQuitting = true;
        foreach (Window w in Application.Current.Windows)
            if (w is MainWindow mw)
                foreach (var v in mw.AllViews())
                    v.FlushRecovery();
        // Shut down after this returns, so the IPC "OK" reply is sent before the app tears down.
        Application.Current.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
        return true;
    }

    /// <summary>
    /// Non-interactive "save everything, then quit". Titled documents with unsaved changes are
    /// written straight to their file (no dialog); untitled/pathless dirty documents — which
    /// have nowhere to save without prompting — are flushed to crash recovery so they can be
    /// restored on the next launch. Then the app shuts down with no prompts. Used by the build
    /// script as a save-first alternative to <see cref="RequestQuitWithRecovery"/>.
    /// </summary>
    public static bool RequestQuitWithSave()
    {
        _forceQuitting = true;
        foreach (Window w in Application.Current.Windows)
            if (w is MainWindow mw)
                foreach (var v in mw.AllViews())
                {
                    if (!v.IsDirty)
                        continue;
                    // Titled: persist to disk directly (Save(false) never prompts when a path exists).
                    if (!string.IsNullOrEmpty(v.FilePath))
                        v.Save(saveAs: false);
                    else
                        v.FlushRecovery();   // untitled: nowhere to save silently — keep it in recovery
                }
        Application.Current.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
        return true;
    }

    /// <summary>Set when this window should really close, overriding the X-button behaviour.</summary>
    private bool _realClose;

    /// <summary>Tray icon for the "minimise to tray" close behaviour; created on first use.</summary>
    private System.Windows.Forms.NotifyIcon? _tray;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_forceQuitting)
        {
            // Forced quit for redeploy: recovery was already flushed above. Stop timers but keep
            // the recovery files (do NOT Dispose) so the work is restored next launch — no prompts.
            foreach (var view in AllViews())
                view.StopTimers();
            DisposeTray();
            base.OnClosing(e);
            return;
        }

        // Honour the configured X-button behaviour unless a real close was explicitly requested
        // (File > Exit, tray "Exit", or closing the final tab).
        if (!_realClose)
        {
            switch (AppSettings.Current.CloseButton)
            {
                case CloseButtonBehavior.MinimizeToTaskbar:
                    e.Cancel = true;
                    WindowState = WindowState.Minimized;
                    return;
                case CloseButtonBehavior.MinimizeToTray:
                    e.Cancel = true;
                    MinimizeToTray();
                    return;
            }
        }

        foreach (var ti in Tabs.Items.OfType<TabItem>().ToList())
        {
            if (ti.Content is not EditorView view)
                continue;
            Tabs.SelectedItem = ti;
            if (!view.ConfirmDiscardIfDirty())
            {
                e.Cancel = true;
                base.OnClosing(e);
                return;
            }
        }

        // Clean shutdown for this window's documents.
        foreach (var view in AllViews())
            view.Dispose();

        DisposeTray();

        base.OnClosing(e);
    }

    // ===================== Minimise to tray =====================

    private void DisposeTray()
    {
        if (_tray is null)
            return;
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
    }

    /// <summary>Hide the window to the notification area, showing (creating) its tray icon.</summary>
    private void MinimizeToTray()
    {
        EnsureTrayIcon();
        _tray!.Visible = true;
        Hide();                    // drop out of Alt-Tab
        ShowInTaskbar = false;
    }

    /// <summary>Bring the window back from the tray and hide its icon.</summary>
    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        if (_tray is not null)
            _tray.Visible = false;
    }

    private void EnsureTrayIcon()
    {
        if (_tray is not null)
            return;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Restore", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _realClose = true;
            Close();
        }));

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = TryLoadAppIcon(),
            Text = "TreeNotepad",
            ContextMenuStrip = menu,
        };
        // Double-click (or a plain left click) restores the window.
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    /// <summary>The app's own exe icon, falling back to the generic application icon.</summary>
    private static System.Drawing.Icon TryLoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico is not null)
                    return ico;
            }
        }
        catch { /* fall through to the system default */ }
        return System.Drawing.SystemIcons.Application;
    }
}

/// <summary>Minimal ICommand wrapper for KeyBinding actions.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action _action;
    public RelayCommand(Action action) => _action = action;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
