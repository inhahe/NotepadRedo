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

namespace NotepadRedo;

/// <summary>
/// Shell window: hosts a TabControl of <see cref="EditorView"/> documents, the menu, and a
/// shared status bar reflecting the active tab. Tabs can be torn off into new windows and
/// dragged back between windows.
/// </summary>
public partial class MainWindow : Window
{
    private const string TabDragFormat = "NotepadRedoTab";     // marker: this drag is a tab
    private const string DocFormat = "NotepadRedoDoc";         // JSON DocDto for cross-process moves
    private const string PidFormat = "NotepadRedoPid";         // origin process id
    private const string TokenFormat = "NotepadRedoToken";     // origin document RecoveryId

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
        // Re-fit the (possibly left-truncated) title whenever the window width changes.
        SizeChanged += (_, e) => { if (e.WidthChanged) UpdateChrome(); };
        Activated += (_, _) =>
        {
            // Re-read settings from disk so changes made by other instances are picked up
            // as soon as this window gets focus.
            AppSettings.Current.Reload();
            SyncOptionMenus();
        };
    }

    /// <summary>
    /// Seed a freshly-shown window. On the first instance of a launch we bring back the previous
    /// session first — crash snapshots (which carry unsaved edits, incl. for titled docs) then the
    /// saved-file list — so that any file named on the command line opens on top of them as the
    /// focused tab. Session restore runs whether or not a file was passed; it's skipped only for a
    /// requested blank (<c>--new</c>), for secondary instances (new-instance mode), and per the
    /// restore-mode preference (see <see cref="RestoreSession"/>).
    /// </summary>
    public void Initialize(IEnumerable<string> files, bool blankRequested, bool firstInstance)
    {
        var list = files.ToList();

        if (firstInstance && !blankRequested)
        {
            // RestoreSession de-dupes against anything recovery already reopened, so a file that was
            // dirty at exit keeps its recovered version.
            OfferRecovery();
            RestoreSession();
        }

        foreach (var f in list)
            // Explicit command-line filenames: offer to create a missing one, Notepad-style.
            // Opened after any restored session so the launched file becomes the active tab;
            // focuses instead of duplicating if it's already open here (e.g. also in the session).
            RequestOpenFile(f, offerCreate: true);

        // Command-line files were given but none opened — every one was missing-and-declined (or
        // failed to open) — and nothing else is open. Don't fall through to a surprise blank
        // "Untitled" tab; close instead, the way Notepad exits when you decline to create the file it
        // was launched with. (If the session restored tabs, those remain and we don't close.)
        if (Tabs.Items.Count == 0 && !blankRequested && list.Count > 0)
        {
            _realClose = true;   // bypass the X-button "minimise to tray" behaviour — really exit
            Close();
            return;
        }

        if (Tabs.Items.Count == 0)
            AddView(CreateBlankView(), select: true);

        ActiveView?.FocusEditor();
    }

    /// <summary>
    /// Reopen the files that were open in the previous session, honouring the restore-mode
    /// preference: Never skips it, Always reopens silently, and Prompt (the default) lists the
    /// files and asks first — so a stale session can't silently clobber edits made elsewhere.
    /// Already-open files are skipped (TryFocusDocument de-dupes against recovered tabs).
    /// </summary>
    private void RestoreSession()
    {
        if (AppSettings.Current.RestoreSession == SessionRestoreMode.Never)
            return;

        var files = SessionStore.Load();
        if (files.Count == 0)
            return;

        if (AppSettings.Current.RestoreSession == SessionRestoreMode.Prompt)
        {
            const int shown = 12;
            string list = string.Join("\n", files.Take(shown).Select(f => "  \u2022 " + f));
            if (files.Count > shown)
                list += $"\n  \u2026 and {files.Count - shown} more";
            var result = ThemedDialog.Show(this,
                $"Reopen {files.Count} file(s) from your last session?\n\n{list}",
                "NotepadRedo \u2014 Restore session",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;
        }

        foreach (var path in files)
            RequestOpenFile(path);
    }

    /// <summary>
    /// Persist the set of open document files (across every window in this process) so the next
    /// launch reopens the same tabs. Called at each structural change (open/close/save). Skipped
    /// during a forced redeploy quit so the last good session list is preserved untouched.
    ///
    /// By default an <i>empty</i> result is NOT written: a window showing only a blank/untitled buffer
    /// (a <c>--new</c> launch, a fresh startup, or a tab torn off to another window mid-drag) has no
    /// file paths, and letting that overwrite session.json would wipe a remembered session. Only an
    /// explicit close of the last file tab passes <paramref name="allowEmpty"/> so the workspace can
    /// genuinely be cleared.
    /// </summary>
    public static void SaveSession(bool allowEmpty = false)
    {
        if (_forceQuitting)
            return;
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Window w in Application.Current.Windows)
            if (w is MainWindow mw)
                foreach (var v in mw.AllViews())
                    if (v.FilePath is string p && seen.Add(NormalizePath(p)))
                        paths.Add(p);
        if (paths.Count == 0 && !allowEmpty)
            return;
        SessionStore.Save(paths);
    }

    /// <summary>Prompt to restore snapshots left behind by a crashed session. Returns true if any restored.</summary>
    private bool OfferRecovery()
    {
        var snaps = EditorView.ScanRecoveries().ToList();
        if (snaps.Count == 0)
            return false;

        var result = ThemedDialog.Show(this,
            $"{snaps.Count} unsaved document(s) from a previous session were found.\n\nRecover them?",
            "NotepadRedo \u2014 Recover unsaved work",
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
            Title = "NotepadRedo";
            return;
        }
        Title = ComputeTitle(view);
        CaretStatus.Text = view.CaretText;
        CountStatus.Text = view.CountText;
        NodeStatus.Text = view.NodeText;
        SaveStatus.Text = view.SaveText;
        SaveStatus.Foreground = view.IsDirty ? Brushes.Firebrick : Brushes.ForestGreen;
        SyncSearchToggle();
    }

    /// <summary>Keep the toolbar Search toggle in sync with the active view's search pane.</summary>
    private void SyncSearchToggle() => SearchToggle.IsChecked = ActiveView?.IsSearchOpen == true;

    private const string TitleSuffix = " - NotepadRedo";

    /// <summary>
    /// Build the window title. When the file path is too long to fit in the title bar, the path
    /// portion is left-truncated with a leading ellipsis so the (more informative) rightmost part
    /// of the path — the file name and its nearest folders — stays visible. The dirty marker and
    /// the " - NotepadRedo" suffix are always preserved.
    ///
    /// WPF exposes a single <see cref="Window.Title"/> string that drives both the title bar and the
    /// taskbar button, and the taskbar button's width can't be measured from WPF. We size the string
    /// to the (measurable) title-bar width; the taskbar shows a prefix of the same string, so it will
    /// likewise begin with the ellipsis and the visible tail when space is tight.
    /// </summary>
    private string ComputeTitle(EditorView view)
    {
        string path = string.IsNullOrEmpty(view.FilePath) ? "Untitled" : view.FilePath!;
        string dirty = view.IsDirty ? " *" : "";
        string full = path + dirty + TitleSuffix;

        // Available width for the caption text: the window minus the icon and caption buttons.
        // We reserve a generous fixed margin (icon + min/max/close ≈ 200 DIPs) so we err toward
        // truncating a little early rather than letting the OS clip the tail we tried to keep.
        double avail = ActualWidth - 200;
        if (ActualWidth <= 0 || avail <= 0)
            return full;

        var typeface = new Typeface(SystemFonts.CaptionFontFamily, SystemFonts.CaptionFontStyle,
                                    SystemFonts.CaptionFontWeight, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double emSize = SystemFonts.CaptionFontSize;

        double Measure(string s) => new FormattedText(
            s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, emSize,
            Brushes.Black, dpi).WidthIncludingTrailingWhitespace;

        if (Measure(full) <= avail)
            return full;

        // The tail (dirty marker + app suffix) is always kept; only the path is shortened.
        string tail = dirty + TitleSuffix;
        // Binary-search the longest suffix of the path that still fits with a leading ellipsis.
        int lo = 0, hi = path.Length;   // number of leading path chars to drop
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            string candidate = "\u2026" + path.Substring(mid) + tail;
            if (Measure(candidate) <= avail) hi = mid;   // fits — try dropping fewer
            else lo = mid + 1;                            // too wide — drop more
        }
        return "\u2026" + path.Substring(Math.Min(lo, path.Length)) + tail;
    }

    private void View_Changed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, ActiveView))
            UpdateChrome();
    }

    private void View_SearchVisibilityChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, ActiveView))
            SyncSearchToggle();
    }

    /// <summary>Toolbar Search toggle: open/close the active view's search pane.</summary>
    private void ToggleSearch_Click(object sender, RoutedEventArgs e)
    {
        ActiveView?.ToggleSearch();
        SyncSearchToggle();
    }

    // ===================== Tab lifecycle =====================

    private EditorView CreateBlankView() => new();

    /// <summary>
    /// Open <paramref name="path"/> as a new tab. When <paramref name="offerCreate"/> is set (an
    /// explicit open — a command-line filename or a hand-off from another launch) and the file does
    /// not exist but its folder does, prompt to create it, Notepad-style; on Yes a fresh empty
    /// document targeted at that path is opened (written only when the user saves). Returns true when
    /// a tab was opened/created, false when nothing was (open error, or the user declined to create).
    /// </summary>
    private bool OpenFileInTab(string path, bool offerCreate = false)
    {
        if (offerCreate && !File.Exists(path) && !Directory.Exists(path))
        {
            // The file isn't there. Only offer to create it when its folder is valid — otherwise the
            // path itself is bad, so fall through and let LoadFile surface a clear "not found" error.
            string? dir = null;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(path)); } catch { /* malformed path */ }
            if (dir is not null && Directory.Exists(dir))
            {
                var choice = ThemedDialog.Show(this,
                    $"Cannot find '{Path.GetFileName(path)}'.\n\nDo you want to create a new file?",
                    "NotepadRedo",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice != MessageBoxResult.Yes)
                    return false;

                var created = CreateBlankView();
                created.PrepareNewFile(Path.GetFullPath(path));
                AddView(created, select: true);
                return true;
            }
        }

        var view = CreateBlankView();
        try
        {
            view.LoadFile(path);
        }
        catch (Exception ex)
        {
            ThemedDialog.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        AddView(view, select: true);
        return true;
    }

    /// <summary>
    /// Open a file, but if it is already open — in this process or another instance — just
    /// focus the existing tab instead of creating a duplicate. Returns true if it was focused
    /// (rather than newly opened here).
    /// </summary>
    private bool RequestOpenFile(string path, bool offerCreate = false)
    {
        if (TryFocusDocument(path) || IpcServer.TryFocusInSibling(path))
            return true;
        OpenFileInTab(path, offerCreate);
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
        // This is an explicit hand-off of a command-line filename from another launch, so honour the
        // same "offer to create a missing file" behaviour as opening on our own command line.
        mw.OpenFileInTab(path, offerCreate: true);
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
        view.SearchVisibilityChanged += View_SearchVisibilityChanged;

        var ti = new TabItem { Content = view, Header = BuildHeader(view) };
        Tabs.Items.Add(ti);
        if (select)
            Tabs.SelectedItem = ti;
        UpdateChrome();
        SaveSession();
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
            // Truncation is done by LeadingEllipsisText (leading "…", keeps the file name) — not the
            // built-in trailing ellipsis, which would hide the informative tail of a long path.
            TextTrimming = TextTrimming.None,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetBinding(LeadingEllipsisText.PathProperty, new System.Windows.Data.Binding(nameof(EditorView.TabTitle)));
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
            view.SearchVisibilityChanged -= View_SearchVisibilityChanged;
            ti.Content = null;          // release so the control can be re-parented
            if (dispose)
                view.Dispose();
        }
        Tabs.Items.Remove(ti);
        UpdateChrome();
        // A real close (dispose) of the last file tab may clear the session; a tear-off (move to
        // another window) must not — the view lives on and AddView will re-record it there.
        SaveSession(allowEmpty: dispose);
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

    // Save through the active view, then refresh the session list — Save As can turn an untitled
    // buffer into a titled file (or change its path), which changes what should be reopened.
    private void Save_Click(object sender, RoutedEventArgs e) { ActiveView?.Save(false); SaveSession(); }
    private void SaveAs_Click(object sender, RoutedEventArgs e) { ActiveView?.Save(true); SaveSession(); }
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
            // We're already a running GUI app with no console to unblock, so mark the child
            // "--detached" — it should open directly rather than doing another detach-relaunch hop
            // (see App.OnStartup). Without this it would needlessly spawn a grandchild and exit.
            psi.ArgumentList.Add("--detached");
            psi.ArgumentList.Add(arg);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            ThemedDialog.Show(null, ex.Message, "Launch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ===================== Menu: Edit / View =====================

    private void Undo_Click(object sender, RoutedEventArgs e) => ActiveView?.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => ActiveView?.Redo();
    private void Find_Click(object sender, RoutedEventArgs e) => ActiveView?.OpenSearch();

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

    private void RestoreMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag ||
            !Enum.TryParse<SessionRestoreMode>(tag, out var mode))
            return;
        AppSettings.Current.RestoreSession = mode;
        SaveAndSyncOptions();
    }

    private void WatchChanges_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.WatchExternalChanges = WatchChangesItem.IsChecked;
        AppSettings.Current.Save();
        foreach (var v in AllOpenViews())
            v.ApplyWatchSetting();
        SaveAndSyncOptions();
    }

    private void LockFile_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.LockFileWhileOpen = LockFileItem.IsChecked;
        AppSettings.Current.Save();
        foreach (var v in AllOpenViews())
            v.ApplyLockSetting();
        SaveAndSyncOptions();
    }

    private void PersistHistory_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.PersistHistory = PersistHistoryItem.IsChecked;
        SaveAndSyncOptions();
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

        RestorePrompt.IsChecked = s.RestoreSession == SessionRestoreMode.Prompt;
        RestoreAlways.IsChecked = s.RestoreSession == SessionRestoreMode.Always;
        RestoreNever.IsChecked  = s.RestoreSession == SessionRestoreMode.Never;

        WatchChangesItem.IsChecked = s.WatchExternalChanges;
        LockFileItem.IsChecked     = s.LockFileWhileOpen;
        PersistHistoryItem.IsChecked = s.PersistHistory;

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
        // Tabs can be null for a window that's registered in Application.Current.Windows but hasn't
        // finished InitializeComponent yet (the WPF Window base ctor self-registers before the
        // derived fields are wired up), so guard it — cross-window enumerators must not throw.
        Tabs is null ? Enumerable.Empty<EditorView>()
                     : Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<EditorView>();

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
        Bind(Key.F, ModifierKeys.Control, () => ActiveView?.OpenSearch());
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

    /// <summary>
    /// Interactive "save-first" quit used by the redeploy script. Walks every open document across
    /// every window and, for each with unsaved changes, shows the normal <c>Save changes?</c>
    /// (Yes/No/Cancel) prompt. If the user cancels any prompt the whole quit is aborted — every
    /// window is left open and this returns <c>false</c>, so the build script can bail out instead
    /// of force-killing. Otherwise each document is disposed (persisted or discarded per the user's
    /// choice, its recovery snapshot cleared) and the app is shut down; returns <c>true</c>.
    /// Runs on the UI thread (invoked from the IPC handler), so the modal prompts pump normally.
    /// </summary>
    public static bool RequestQuitWithPrompt()
    {
        var windows = Application.Current.Windows.OfType<MainWindow>().ToList();

        // First pass: prompt for every dirty document across every window. Each prompt offers a
        // "Save All" shortcut that saves the remaining ones silently; a single Cancel aborts.
        var docs = new List<(Action, EditorView)>();
        foreach (var mw in windows)
        {
            var win = mw;
            foreach (var ti in mw.Tabs.Items.OfType<TabItem>().ToList())
            {
                if (ti.Content is not EditorView view)
                    continue;
                var tab = ti;
                docs.Add((() =>
                {
                    // Bring a tray-hidden / minimised window forward so its prompt is visible, then
                    // select the document being asked about.
                    if (win.Visibility != Visibility.Visible)
                        win.Show();
                    win.ShowInTaskbar = true;
                    if (win.WindowState == WindowState.Minimized)
                        win.WindowState = WindowState.Normal;
                    win.Activate();
                    win.Tabs.SelectedItem = tab;
                }, view));
            }
        }

        if (!PromptSaveEach(docs))
            return false;   // user cancelled — keep everything open, don't quit

        // Everyone confirmed (saved or chose to discard). Tear each document down cleanly (clearing
        // its recovery snapshot and releasing any file lock), then shut the whole app down.
        _forceQuitting = true;   // OnClosing must not prompt a second time during shutdown
        foreach (var mw in windows)
            foreach (var v in mw.AllViews())
                v.Dispose();

        // Shut down after this returns so the IPC "OK" reply reaches the caller before we tear down.
        Application.Current.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
        return true;
    }

    /// <summary>
    /// Prompt to save each dirty document in <paramref name="docs"/> in turn. Each prompt offers a
    /// "Save All" shortcut: once chosen, every remaining dirty document is saved silently without
    /// further prompting. <paramref name="bringForward"/> is invoked before a document's own prompt
    /// so its window/tab is visible and selected first. Returns false if the user cancelled (in which
    /// case the caller should abort the close/quit and leave everything open).
    /// </summary>
    private static bool PromptSaveEach(IEnumerable<(Action bringForward, EditorView view)> docs)
    {
        bool saveAll = false;
        foreach (var (bringForward, view) in docs)
        {
            if (!view.IsDirty)
                continue;

            if (saveAll)
            {
                if (!view.Save(false))
                    return false;   // a silent save failed (error already shown) — abort
                continue;
            }

            bringForward();
            switch (view.ConfirmDiscardForQuit())
            {
                case EditorView.SavePromptResult.Handled:
                    break;
                case EditorView.SavePromptResult.SaveRemaining:
                    saveAll = true;   // this one is already saved; save the rest silently
                    break;
                default:
                    return false;     // cancelled
            }
        }
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

        var docs = Tabs.Items.OfType<TabItem>()
            .Where(ti => ti.Content is EditorView)
            .Select(ti => ((Action)(() => Tabs.SelectedItem = ti), (EditorView)ti.Content))
            .ToList();
        if (!PromptSaveEach(docs))
        {
            e.Cancel = true;
            base.OnClosing(e);
            return;
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
            Text = "NotepadRedo",
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
