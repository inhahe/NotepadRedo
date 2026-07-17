using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
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
            Content = "\u00d7",
            FontSize = 12,
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
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
            Close();
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

        foreach (var f in dlg.FileNames)
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
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

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

    private void ShowTree_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.ShowTree = ShowTreeItem.IsChecked;
        AppSettings.Current.Save();
        foreach (var v in AllViews())
            v.ApplyTreeVisible(AppSettings.Current.ShowTree);
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

    private void SyncOptionMenus()
    {
        var s = AppSettings.Current;
        WordWrapItem.IsChecked = s.WordWrap;
        ShowTreeItem.IsChecked = s.ShowTree;
        OpenInTabItem.IsChecked = !s.OpenInNewInstance;
        OpenInInstanceItem.IsChecked = s.OpenInNewInstance;

        AutoOff.IsChecked = s.AutosaveSeconds == 0;
        Auto15.IsChecked = s.AutosaveSeconds == 15;
        Auto30.IsChecked = s.AutosaveSeconds == 30;
        Auto60.IsChecked = s.AutosaveSeconds == 60;
        Auto300.IsChecked = s.AutosaveSeconds == 300;
    }

    private IEnumerable<EditorView> AllViews() =>
        Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<EditorView>();

    private void SetUpKeyBindings()
    {
        void Bind(Key key, ModifierKeys mods, Action action) =>
            InputBindings.Add(new KeyBinding(new RelayCommand(action), new KeyGesture(key, mods)));

        Bind(Key.Z, ModifierKeys.Control, () => ActiveView?.Undo());
        Bind(Key.Y, ModifierKeys.Control, () => ActiveView?.Redo());
        Bind(Key.N, ModifierKeys.Control, () => New_Click(this, new RoutedEventArgs()));
        Bind(Key.O, ModifierKeys.Control, () => Open_Click(this, new RoutedEventArgs()));
        Bind(Key.S, ModifierKeys.Control, () => ActiveView?.Save(false));
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Shift, () => ActiveView?.Save(true));
        Bind(Key.W, ModifierKeys.Control, () => CloseTab(Tabs.SelectedItem as TabItem));
    }

    // ===================== Tab drag: tear-off & reattach =====================

    private void Tab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragArmed = true;
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

        var effect = DragDrop.DoDragDrop(ti, data, DragDropEffects.Move);

        // No window accepted the drop (and the tab is still ours) → tear off at the cursor.
        if (effect != DragDropEffects.Move && ReferenceEquals(TabDrag.Item, ti) && FindTab(view) is not null)
            DetachToNewWindow(ti);

        TabDrag.Item = null;
        TabDrag.Source = null;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (!e.Data.GetDataPresent(TabDragFormat))
            return;   // leave ordinary text/file drops for the editor to handle
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
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
            IpcServer.CloseTabInProcess(srcPid, token);
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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
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

        base.OnClosing(e);
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
