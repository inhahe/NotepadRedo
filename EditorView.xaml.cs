using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TreeNotepad;

/// <summary>
/// A single self-contained document: text editor + branching history tree + all per-file
/// state (undo tree, file path, dirty flag, autosave/recovery). One of these lives inside
/// each tab, and the whole live control is moved between windows on tab tear-off / reattach.
/// </summary>
public partial class EditorView : UserControl, INotifyPropertyChanged
{
    private UndoTree _tree = new();
    private readonly DispatcherTimer _debounce;

    private bool _suppressTextChange;      // ignore programmatic edits
    private bool _suppressTreeSelection;   // ignore programmatic tree selection
    private bool _navigating;              // a commit/navigate is in flight — block reentrancy

    private string _currentText = "";      // materialised text of _tree.Current
    private string? _currentPath;
    private string _savedText = "";        // text as last saved/opened, for the dirty flag

    private const int DebounceMs = 500;

    // ----- Typing-burst coalescing -----
    // Consecutive edits within one continuous typing session are folded into a single history node
    // rather than creating one node per 500ms debounce tick (which used to bury the tree under
    // dozens of near-identical entries). A burst is broken by navigating/undo/redo, or by an idle
    // gap longer than CoalesceWindowMs — each of those starts a fresh checkpoint node.
    private const int CoalesceWindowMs = 4000;
    private UndoNode? _typingNode;         // the leaf node the current burst is being folded into
    private DateTime _lastEditTime;        // when the last edit was committed/coalesced

    // ----- Autosave / crash recovery (per document) -----
    private readonly DispatcherTimer _autosave = new();
    private string _lastRecoveryText = "";
    private DateTime? _lastAutosave;

    /// <summary>Unique id for this document's recovery file.</summary>
    public string RecoveryId { get; private set; } = Guid.NewGuid().ToString("N");

    private static readonly string RecoveryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TreeNotepad", "recovery");

    private string RecoveryPath => Path.Combine(RecoveryDir, RecoveryId + ".json");

    public sealed record RecoveryData(string Id, string? Path, string SavedText, string Text, DateTime SavedAt);

    // ----- Events consumed by the hosting window -----
    /// <summary>Caret / count / node / save-state changed — refresh the status bar.</summary>
    public event EventHandler? StatusChanged;
    /// <summary>File path or dirty flag changed — refresh the tab header and window title.</summary>
    public event EventHandler? TitleChanged;

    public EditorView()
    {
        InitializeComponent();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounce.Tick += Debounce_Tick;

        _autosave.Tick += (_, _) => WriteRecovery();

        // Empty-root fix: bind to the root's children so the blank starting state is not
        // shown as a node. Each first edit becomes a top-level item; sibling branches stay
        // as separate top-level items rather than nesting under one another.
        Tree.ItemsSource = _tree.Root.Children;

        ApplyAutosaveInterval(AppSettings.Current.AutosaveSeconds);
        ApplyWordWrap(AppSettings.Current.WordWrap);
        ApplyTreeVisible(AppSettings.Current.ShowTree);
        ApplyPreviewFit(AppSettings.Current.PreviewFitToWidth);
        ApplyFont(AppSettings.Current.FontFamily, AppSettings.Current.FontSize,
                  AppSettings.Current.FontBold, AppSettings.Current.FontItalic);

        Loaded += (_, _) => RaiseAll();
    }

    // ===================== Public surface for the shell =====================

    public string? FilePath => _currentPath;
    public bool IsDirty => Editor.Text != _savedText;

    /// <summary>Full path (or "Untitled") plus a trailing * when there are unsaved changes.</summary>
    public string TabTitle =>
        (string.IsNullOrEmpty(_currentPath) ? "Untitled" : _currentPath) + (IsDirty ? " *" : "");

    public string CaretText { get; private set; } = "Ln 1, Col 1";
    public string CountText { get; private set; } = "0 chars";
    public string NodeText { get; private set; } = "node #0";

    public string SaveText => IsDirty
        ? (_lastAutosave is DateTime t ? $"Not saved \u00b7 autosaved {t:HH:mm:ss}" : "Not saved")
        : "Saved";

    public void FocusEditor() => Editor.Focus();

    /// <summary>Load a file's contents into this (blank) view.</summary>
    public void LoadFile(string path)
    {
        var text = File.ReadAllText(path);
        _currentPath = path;
        _savedText = text;
        DeleteRecovery();
        ResetTree(text);
    }

    /// <summary>Full document snapshot (path + saved/current text + entire history) for tab transfer.</summary>
    public sealed record DocDto(string? Path, string SavedText, string CurrentText, TreeDto Tree);

    /// <summary>Serialise this document (with its whole undo history) for a cross-process tab move.</summary>
    public DocDto SerializeDocument()
    {
        CommitPending();
        return new DocDto(_currentPath, _savedText, Editor.Text, _tree.Serialize(_currentText));
    }

    /// <summary>Rebuild a document (moved here from another process) into this blank view.</summary>
    public void LoadTransferred(DocDto dto)
    {
        _currentPath = dto.Path;
        _savedText = dto.SavedText;
        _tree = UndoTree.Deserialize(dto.Tree);
        _currentText = dto.CurrentText;
        _typingNode = null;
        Tree.ItemsSource = _tree.Root.Children;

        _suppressTextChange = true;
        Editor.Text = dto.CurrentText;
        Editor.CaretIndex = Math.Clamp(_tree.Current.CaretIndex, 0, dto.CurrentText.Length);
        _suppressTextChange = false;

        _lastAutosave = null;
        _lastRecoveryText = "";

        SetCurrent(_tree.Current);
        RaiseAll();
        WriteRecovery();   // this instance now owns crash recovery for the moved document
        Editor.Focus();
    }

    /// <summary>Seed this view directly from a recovered snapshot (marked dirty as appropriate).</summary>
    public void LoadRecovered(RecoveryData data)
    {
        RecoveryId = data.Id;
        _currentPath = data.Path;
        _savedText = data.SavedText;
        ResetTree(data.Text);
        WriteRecovery();   // re-establish the recovery file immediately
    }

    // ===================== Editing / commits =====================

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChange)
            return;
        _debounce.Stop();
        _debounce.Start();
        RaiseAll();
    }

    private void Debounce_Tick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        CommitPending();
    }

    /// <summary>Capture the editor's current text as a new tree node (if it changed).</summary>
    private void CommitPending()
    {
        _debounce.Stop();
        // Non-reentrant: a reentrant call (e.g. from a tree-selection event fired while we update
        // the selection below) must not commit a second, duplicate node for the same edit.
        if (_navigating)
            return;
        _navigating = true;
        try
        {
            if (Editor.Text == _currentText)
                return;   // nothing changed since the last commit

            // Fold this edit into the current burst's node when it's still the open typing leaf and
            // the pause was short; otherwise start a fresh checkpoint node.
            bool canCoalesce = ReferenceEquals(_tree.Current, _typingNode)
                               && (DateTime.Now - _lastEditTime).TotalMilliseconds <= CoalesceWindowMs;

            if (canCoalesce && _tree.Coalesce(_currentText, Editor.Text, Editor.CaretIndex))
            {
                _currentText = Editor.Text;
                _lastEditTime = DateTime.Now;
                RaiseAll();
                return;
            }

            var node = _tree.Commit(_currentText, Editor.Text, Editor.CaretIndex);
            if (node is not null)
            {
                _currentText = Editor.Text;
                _typingNode = node;
                _lastEditTime = DateTime.Now;
                SetCurrent(node);
                RaiseAll();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Log("CommitPending failed", ex);
        }
        finally
        {
            _navigating = false;
        }
    }

    private void NavigateTo(UndoNode target)
    {
        // Non-reentrant: programmatically selecting the target below can fire tree-selection
        // events that would otherwise re-enter this while _currentText is half-updated,
        // desyncing the model and (previously) crashing text reconstruction.
        if (_navigating)
            return;
        _navigating = true;
        try
        {
            string text = UndoTree.Reconstruct(_tree.Current, _currentText, target);
            _tree.SetCurrent(target);
            _currentText = text;
            _typingNode = null;   // a jump ends the current typing burst — next edit starts anew
            ApplyNode(target, text);
            HideTreeIfTemporary();   // a branch was chosen — collapse a pane revealed only to pick it
        }
        catch (Exception ex)
        {
            // Reconstruction should never fail, but if the model ever desyncs, log a full
            // traceback and recover by re-anchoring on the target instead of crashing.
            CrashLog.Log("NavigateTo failed — resyncing to target node", ex);
            ResyncTo(target);
        }
        finally
        {
            _navigating = false;
        }
    }

    /// <summary>
    /// Recovery path: adopt <paramref name="target"/> as the current node using the editor's
    /// live text as ground truth, without attempting delta reconstruction. Keeps the app usable
    /// even if the history model ever gets into an inconsistent state.
    /// </summary>
    private void ResyncTo(UndoNode target)
    {
        try
        {
            _tree.SetCurrent(target);
            _currentText = Editor.Text;
            _typingNode = null;
            SetCurrent(target);
            RaiseAll();
        }
        catch (Exception ex)
        {
            CrashLog.Log("ResyncTo failed", ex);
        }
    }

    private void ApplyNode(UndoNode node, string text)
    {
        _suppressTextChange = true;
        Editor.Text = text;
        Editor.CaretIndex = Math.Clamp(node.CaretIndex, 0, text.Length);
        _suppressTextChange = false;

        SetCurrent(node);
        RaiseAll();
        Editor.Focus();
    }

    /// <summary>Highlight, expand to and select the given node in the tree view.</summary>
    private void SetCurrent(UndoNode node)
    {
        // Clear both flags across the tree so stale highlights (IsCurrent) and stale selection
        // rows (IsSelected — the TwoWay binding otherwise leaves earlier nodes marked selected)
        // don't linger and make it look like several nodes are active at once.
        _suppressTreeSelection = true;
        foreach (var n in _tree.AllNodes())
        {
            if (!ReferenceEquals(n, node))
            {
                n.IsCurrent = false;
                n.IsSelected = false;
            }
        }
        node.IsCurrent = true;

        var p = node.Parent;
        while (p is not null) { p.IsExpanded = true; p = p.Parent; }

        node.IsSelected = true;
        _suppressTreeSelection = false;
    }

    // ===================== Undo / Redo =====================

    public void Undo()
    {
        CommitPending();
        var parent = _tree.Current.Parent;
        if (parent is not null)
            NavigateTo(parent);
    }

    public void Redo()
    {
        CommitPending();

        // Ambiguous redo: the current node has more than one child branch. If the tree is
        // hidden, reveal it so the user can choose which branch instead of silently redoing
        // into the newest one. (When the tree is already visible we just take the newest.)
        if (_tree.Current.Children.Count > 1 && TreePanel.Visibility != Visibility.Visible)
        {
            RevealTreeTemporarily();
            return;
        }

        var child = _tree.NewestChild();
        if (child is not null)
            NavigateTo(child);
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSelection || _navigating)
            return;
        if (e.NewValue is UndoNode node && node != _tree.Current)
        {
            CommitPending();
            if (node != _tree.Current)
                NavigateTo(node);
        }
    }

    // ===================== Save =====================

    /// <summary>Returns true when the document ends up saved (false if the user cancelled).</summary>
    public bool Save(bool saveAs)
    {
        CommitPending();
        var path = _currentPath;
        if (saveAs || string.IsNullOrEmpty(path))
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = string.IsNullOrEmpty(path) ? "untitled.txt" : Path.GetFileName(path)
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true)
                return false;
            path = dlg.FileName;
        }

        try
        {
            File.WriteAllText(path, Editor.Text);
            _currentPath = path;
            _savedText = Editor.Text;
            _lastAutosave = null;
            DeleteRecovery();
            RaiseAll();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Prompt to save when dirty. Returns false only if the user cancels.</summary>
    public bool ConfirmDiscardIfDirty()
    {
        if (!IsDirty)
            return true;
        string name = string.IsNullOrEmpty(_currentPath) ? "Untitled" : Path.GetFileName(_currentPath);
        var result = MessageBox.Show(Window.GetWindow(this),
            $"Save changes to {name}?",
            "TreeNotepad",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return result switch
        {
            MessageBoxResult.Yes => Save(false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>Start a fresh history tree seeded with the given text.</summary>
    private void ResetTree(string text)
    {
        _tree = new UndoTree(text);
        _currentText = text;
        _typingNode = null;
        Tree.ItemsSource = _tree.Root.Children;

        _suppressTextChange = true;
        Editor.Text = text;
        Editor.CaretIndex = 0;
        _suppressTextChange = false;

        _lastAutosave = null;
        _lastRecoveryText = "";

        SetCurrent(_tree.Root);
        RaiseAll();
        Editor.Focus();
    }

    // ===================== View options =====================

    public void ApplyWordWrap(bool wrap) =>
        Editor.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

    /// <summary>Apply the shared editor-font preference to this document's text area.</summary>
    public void ApplyFont(string family, double size, bool bold, bool italic)
    {
        Editor.FontFamily = new System.Windows.Media.FontFamily(family);
        Editor.FontSize = size;
        Editor.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
        Editor.FontStyle = italic ? FontStyles.Italic : FontStyles.Normal;
    }

    /// <summary>Whether the tree pane is only showing to let the user pick an ambiguous redo branch.</summary>
    private bool _treeTemporarilyShown;

    /// <summary>Apply the persistent show/hide preference (clears any temporary reveal).</summary>
    public void ApplyTreeVisible(bool show)
    {
        _treeTemporarilyShown = false;
        SetTreePaneVisible(show);
    }

    /// <summary>Last width the tree pane had while visible, restored the next time it is shown.</summary>
    private double _treeWidth = 340;

    private void SetTreePaneVisible(bool show)
    {
        // Remember the user's chosen width before collapsing so toggling doesn't reset it.
        if (!show && TreePanel.Visibility == Visibility.Visible && TreeColumn.ActualWidth > 0)
            _treeWidth = TreeColumn.ActualWidth;

        TreePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // MinWidth must drop to 0 when hidden, otherwise the column keeps its minimum width
        // and leaves an empty gap even with Width=0.
        TreeColumn.MinWidth = show ? TreeMinWidth : 0;
        TreeColumn.Width = show ? new GridLength(_treeWidth) : new GridLength(0);
    }

    /// <summary>Smallest the tree pane may be dragged; also the persistent-hide floor.</summary>
    private const double TreeMinWidth = 80;

    private bool _draggingSplitter;

    /// <summary>
    /// Resize the tree pane by dragging the divider. We capture the mouse on the divider and drive
    /// <c>TreeColumn.Width</c> from the cursor's X relative to this control — a stable ancestor whose
    /// width doesn't change during the drag.
    ///
    /// The dragging state is validated against the live button state on every move: if the button is
    /// no longer down (e.g. a mouse-up was missed, or capture was lost during the resize layout pass),
    /// we end the drag and release capture immediately. Without that guard a stale capture would steal
    /// all mouse input app-wide — the divider would "move when the mouse is merely near it" and other
    /// controls (like the preview slider) would stop responding entirely.
    /// </summary>
    private void Splitter_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TreePanel.Visibility != Visibility.Visible)
            return;
        _draggingSplitter = true;
        Splitter.CaptureMouse();
        Splitter.LostMouseCapture += Splitter_LostMouseCapture;
        e.Handled = true;
    }

    private void Splitter_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_draggingSplitter)
            return;

        // Only resize while the left button is genuinely held; otherwise the drag is over.
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            EndSplitterDrag();
            return;
        }

        double splitter = Splitter.ActualWidth;
        // Cursor X within this control; the tree fills everything to the right of the cursor.
        double cursorX = e.GetPosition(this).X;
        double target = ActualWidth - cursorX - splitter / 2;

        // Never crowd the editor out: cap the tree at the room left after the editor's minimum.
        double editorMin = 200;
        double max = Math.Max(TreeMinWidth, ActualWidth - editorMin - splitter);
        target = Math.Clamp(target, TreeMinWidth, max);

        TreeColumn.Width = new GridLength(target);
        _treeWidth = target;
    }

    private void Splitter_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_draggingSplitter)
            return;
        EndSplitterDrag();
        e.Handled = true;
    }

    private void Splitter_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => EndSplitterDrag();

    /// <summary>End a divider drag and let go of the mouse, however the drag was interrupted.</summary>
    private void EndSplitterDrag()
    {
        _draggingSplitter = false;
        Splitter.LostMouseCapture -= Splitter_LostMouseCapture;
        if (Splitter.IsMouseCaptured)
            Splitter.ReleaseMouseCapture();
    }

    /// <summary>Reveal the tree just long enough for the user to choose a redo branch.</summary>
    private void RevealTreeTemporarily()
    {
        _treeTemporarilyShown = true;
        SetTreePaneVisible(true);
        Tree.Focus();
    }

    /// <summary>Collapse a temporarily-revealed tree once the persistent preference is "hidden".</summary>
    private void HideTreeIfTemporary()
    {
        if (_treeTemporarilyShown && !AppSettings.Current.ShowTree)
        {
            _treeTemporarilyShown = false;
            SetTreePaneVisible(false);
        }
    }

    private void PreviewSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int len = (int)e.NewValue;
        UndoNode.PreviewLength = len;
        if (PreviewLenLabel is not null)
            PreviewLenLabel.Text = len.ToString();
        RefreshPreviews();
    }

    private bool _suppressFitEvent;

    private void FitWidth_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFitEvent)
            return;
        bool fit = FitWidthCheck.IsChecked == true;
        AppSettings.Current.PreviewFitToWidth = fit;
        AppSettings.Current.Save();
        // Apply to every open document so the choice is process-wide.
        foreach (Window w in Application.Current.Windows)
            if (w is MainWindow mw)
                foreach (var v in mw.AllEditorViews())
                    v.ApplyPreviewFit(fit);
    }

    /// <summary>Reflect the fit-to-width preference in this view's UI and repaint its previews.</summary>
    public void ApplyPreviewFit(bool fit)
    {
        UndoNode.FitToWidth = fit;
        if (FitWidthCheck.IsChecked != fit)
        {
            _suppressFitEvent = true;
            FitWidthCheck.IsChecked = fit;
            _suppressFitEvent = false;
        }
        // The character-count slider is meaningless in fit-to-width mode.
        PreviewCharsRow.IsEnabled = !fit;
        RefreshPreviews();
    }

    private void RefreshPreviews()
    {
        foreach (var n in _tree.AllNodes())
            n.RaisePreviewChanged();
    }

    // ===================== Status =====================

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e) => RaiseAll();

    private void RaiseAll()
    {
        int caret = Editor.CaretIndex;
        int line = Editor.GetLineIndexFromCharacterIndex(caret);
        int lineStart = Editor.GetCharacterIndexFromLineIndex(line);
        int col = caret - lineStart;
        CaretText = $"Ln {line + 1}, Col {col + 1}";
        CountText = $"{Editor.Text.Length} chars";
        NodeText = $"node #{_tree.Current.Id}";

        StatusChanged?.Invoke(this, EventArgs.Empty);
        TitleChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(TabTitle));
    }

    // ===================== Autosave / crash recovery =====================

    public void ApplyAutosaveInterval(int seconds)
    {
        _autosave.Stop();
        if (seconds > 0)
        {
            _autosave.Interval = TimeSpan.FromSeconds(seconds);
            _autosave.Start();
        }
    }

    private void WriteRecovery(bool force = false)
    {
        try
        {
            if (!IsDirty) { DeleteRecovery(); return; }
            if (!force && Editor.Text == _lastRecoveryText) return;

            Directory.CreateDirectory(RecoveryDir);
            var data = new RecoveryData(RecoveryId, _currentPath, _savedText, Editor.Text, DateTime.Now);
            File.WriteAllText(RecoveryPath, JsonSerializer.Serialize(data));
            _lastRecoveryText = Editor.Text;
            _lastAutosave = DateTime.Now;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { /* autosave is best-effort */ }
    }

    private void DeleteRecovery()
    {
        _lastRecoveryText = "";
        try
        {
            if (File.Exists(RecoveryPath))
                File.Delete(RecoveryPath);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Stop this document's timers without touching its recovery file.</summary>
    public void StopTimers()
    {
        _debounce.Stop();
        _autosave.Stop();
    }

    /// <summary>
    /// Force an immediate crash-recovery snapshot of the current text. Used before a forced /
    /// redeploy shutdown so no unsaved work is lost even though the process is about to be closed.
    /// </summary>
    public void FlushRecovery() => WriteRecovery(force: true);

    /// <summary>Stop timers and clear the recovery file — called when the tab is closed cleanly.</summary>
    public void Dispose()
    {
        StopTimers();
        DeleteRecovery();
    }

    /// <summary>Enumerate recovery snapshots left behind by a previous (crashed) session.</summary>
    public static IEnumerable<RecoveryData> ScanRecoveries()
    {
        if (!Directory.Exists(RecoveryDir))
            yield break;
        foreach (var file in Directory.EnumerateFiles(RecoveryDir, "*.json"))
        {
            RecoveryData? data = null;
            try { data = JsonSerializer.Deserialize<RecoveryData>(File.ReadAllText(file)); }
            catch { }
            if (data is not null && !string.IsNullOrEmpty(data.Text))
                yield return data;
        }
    }

    public static void ClearAllRecoveries()
    {
        try
        {
            if (Directory.Exists(RecoveryDir))
                foreach (var f in Directory.EnumerateFiles(RecoveryDir, "*.json"))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
