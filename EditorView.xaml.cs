using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NotepadRedo;

/// <summary>
/// A single self-contained document: text editor + branching history tree + all per-file
/// state (undo tree, file path, dirty flag, autosave/recovery). One of these lives inside
/// each tab, and the whole live control is moved between windows on tab tear-off / reattach.
/// </summary>
public partial class EditorView : UserControl, INotifyPropertyChanged
{
    private UndoTree _tree = new();
    private readonly DispatcherTimer _debounce;

    // The history pane is a *flattened* view of the branching tree: a straight run of edits is
    // listed flush-left as a plain sequence, and only genuine fork points (a node with more than
    // one child) push the following branch rows further right. This collection is the flattened,
    // pre-order projection the ListBox binds to; it is rebuilt whenever the tree's shape changes.
    private readonly System.Collections.ObjectModel.ObservableCollection<UndoNode> _historyRows = new();

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
    // dozens of near-identical entries). A burst is broken by navigating/undo/redo, by pressing
    // Enter or pasting (when enabled), or by an idle gap longer than the configured coalesce window
    // — each of those starts a fresh checkpoint node. Per-character mode disables coalescing.
    private UndoNode? _typingNode;         // the leaf node the current burst is being folded into
    private DateTime _lastEditTime;        // when the last edit was committed/coalesced

    // ----- Autosave / crash recovery (per document) -----
    private readonly DispatcherTimer _autosave = new();
    private string _lastRecoveryText = "";
    private DateTime? _lastAutosave;

    // ----- External-change detection / file lock (per titled document) -----
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _watchDebounce;   // coalesce bursts of FS events
    private DateTime _diskWriteTimeUtc;                // last disk mtime we consider "ours"
    private long _diskLength = -1;                     // last disk size we consider "ours"
    private DateTime _suppressUntil;                   // ignore watcher events until this time (our own writes)
    private bool _resolving;                           // an external-change prompt is on screen
    private string? _pendingWhileResolving;            // a newer disk version that arrived mid-prompt
    private DiffMergeWindow? _mergeWindow;             // open merge viewer (so re-changes route to it)
    private FileStream? _lockStream;                   // deny-write lock held while open (optional)
    private string? _lockedPath;                       // path _lockStream currently holds

    // ----- Search pane -----
    private readonly System.Collections.ObjectModel.ObservableCollection<SearchResultVM> _searchResults = new();
    // Proximity mode's explicit term list (each an editable, removable item). Only used when the
    // "near each other" checkbox is on; plain search ignores it and matches the box text literally.
    private readonly System.Collections.ObjectModel.ObservableCollection<SearchTermVM> _searchTerms = new();
    private DispatcherTimer? _searchDebounce;
    private bool _suppressResultNav;       // ignore the SelectionChanged fired while we repopulate
    private const double SearchPaneWidth = 320;

    // ----- Drag-select auto-scroll -----
    // WPF's built-in auto-scroll while drag-selecting past the top/bottom edge lurches in big
    // line/page steps, so it's almost impossible to stop at the right place — a tiny extra mouse
    // move rockets the selection way too far. We take the drag over once the cursor leaves the text
    // viewport and drive a smooth, velocity-controlled scroll instead: speed grows with how far past
    // the edge the cursor is, so just past the edge crawls (fine control) and pushing further speeds
    // up, capped so it never races away.
    private DispatcherTimer? _dragScrollTimer;
    private bool _dragScrollActive;        // took the drag over (cursor crossed an edge this drag)
    private int _dragScrollAnchor;         // fixed end of the selection (the mouse-down point)
    private double _dragScrollVelocity;    // px per tick, signed (+ down / − up); 0 while in view
    private double _dragScrollMouseX;      // last cursor X (Editor coords) for edge hit-testing
    private double _dragScrollOffset;      // our authoritative vertical offset while auto-scrolling
                                           // (see ExtendDragSelection: TextBox.Select scrolls the
                                           // caret into view and would otherwise fight our scroll)
    private const double DragScrollIntervalMs = 16;
    private const double DragScrollMaxPxPerTick = 28;

    /// <summary>Unique id for this document's recovery file.</summary>
    public string RecoveryId { get; private set; } = Guid.NewGuid().ToString("N");

    private static readonly string RecoveryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotepadRedo", "recovery");

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

        // Isolate pastes as their own undo step when the user has that enabled.
        DataObject.AddPastingHandler(Editor, Editor_Pasting);

        // Smooth auto-scroll while drag-selecting beyond the viewport (replaces WPF's lurching one).
        Editor.PreviewMouseMove += Editor_PreviewMouseMove;
        Editor.PreviewMouseLeftButtonUp += (_, _) => EndDragScroll();
        Editor.LostMouseCapture += (_, _) => EndDragScroll();
        // While we're driving that scroll, suppress the TextBox's own "scroll the caret into view"
        // (see Editor_RequestBringIntoView). Register on the Editor and — because the request is
        // raised deep inside the control template and the inner ScrollViewer may act on it before it
        // bubbles up to the Editor — also on the internal PART_ContentHost ScrollViewer once the
        // template is applied.
        Editor.RequestBringIntoView += Editor_RequestBringIntoView;
        Editor.Loaded += (_, _) =>
        {
            if (Editor.Template?.FindName("PART_ContentHost", Editor) is ScrollViewer sv)
                sv.RequestBringIntoView += Editor_RequestBringIntoView;
        };

        _autosave.Tick += (_, _) => WriteRecovery();

        _watchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _watchDebounce.Tick += (_, _) => { _watchDebounce.Stop(); HandleExternalChange(); };

        // The blank starting state (root) is not shown as a row; each committed edit becomes a
        // row in this flattened list.
        Tree.ItemsSource = _historyRows;
        RebuildHistoryRows();

        ApplyAutosaveInterval(AppSettings.Current.AutosaveSeconds);
        ApplyWordWrap(AppSettings.Current.WordWrap);
        ApplyTreeVisible(AppSettings.Current.ShowTree);
        ApplyPreviewFit(AppSettings.Current.PreviewFitToWidth);
        ApplyHistoryMode(AppSettings.Current.HistoryBranchesOnly);
        ApplyFont(AppSettings.Current.FontFamily, AppSettings.Current.FontSize,
                  AppSettings.Current.FontBold, AppSettings.Current.FontItalic);

        ResultsList.ItemsSource = _searchResults;
        TermsList.ItemsSource = _searchTerms;

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

    public void FocusEditor()
    {
        // At startup the window is shown and this runs before activation is fully settled, so a
        // synchronous Editor.Focus() only takes *logical* focus — the caret appears but keystrokes
        // don't land until the user clicks. Deferring to Input priority lets the window finish
        // activating first, then Keyboard.Focus forces real keyboard focus so typing works at once.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Editor.Focus();
            Keyboard.Focus(Editor);
        }), DispatcherPriority.Input);
    }

    /// <summary>Load a file's contents into this (blank) view.</summary>
    public void LoadFile(string path)
    {
        var text = ReadAllTextShared(path);
        _currentPath = path;
        _savedText = text;
        DeleteRecovery();
        // Restore the persisted branching history when enabled and the sidecar still matches the
        // file on disk; otherwise start a fresh single-root tree from the disk text.
        if (!(AppSettings.Current.PersistHistory && TryRestoreHistory(path, text)))
            ResetTree(text);
        OnPathEstablished();
    }

    /// <summary>
    /// Set this fresh view up as a brand-new document targeted at <paramref name="path"/> that does
    /// not yet exist on disk (the user asked to "create" it, Notepad-style): empty text, the save
    /// target established so Ctrl+S writes straight there with no Save As prompt, and the tab/title
    /// showing the file name. Nothing is written until the user actually saves — so closing an
    /// untouched new document leaves no stray empty file behind, exactly like Notepad.
    /// </summary>
    public void PrepareNewFile(string path)
    {
        _currentPath = path;
        _savedText = "";
        DeleteRecovery();
        ResetTree("");
        OnPathEstablished();   // watch the folder for the file's (future) creation; no lock/stamp yet
    }

    /// <summary>
    /// Try to rebuild this document's saved branching history from its sidecar. Succeeds only when
    /// the stored history still reconstructs the current on-disk text exactly (so the document opens
    /// clean, matching disk, with its full history — including undone/redo branches — available).
    /// Returns false to fall back to a fresh root.
    /// </summary>
    private bool TryRestoreHistory(string path, string diskText)
    {
        var dto = HistoryStore.Load(path, diskText);
        if (dto is null)
            return false;
        try
        {
            var tree = UndoTree.Deserialize(dto);
            string curText = tree.Materialize(tree.Current);
            if (curText != diskText)
                return false;   // anchor drifted — don't open dirty; use a fresh tree instead

            _tree = tree;
            _typingNode = null;
            _currentText = curText;
            RebuildHistoryRows();

            _suppressTextChange = true;
            Editor.Text = curText;
            Editor.CaretIndex = Math.Clamp(tree.Current.CaretIndex, 0, curText.Length);
            _suppressTextChange = false;

            _lastAutosave = null;
            _lastRecoveryText = "";

            SetCurrent(tree.Current);
            RaiseAll();
            Editor.Focus();
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Log("history restore failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Persist this document's whole branching history to its sidecar, anchored to the saved text —
    /// but only when the setting is on and the document is clean (its current text matches what's on
    /// disk), so the anchor is valid. When there's no real history (just the root), any stale sidecar
    /// is removed instead. No-op for untitled buffers (no path to key on).
    /// </summary>
    private void PersistHistory()
    {
        if (!AppSettings.Current.PersistHistory || string.IsNullOrEmpty(_currentPath) || IsDirty)
            return;
        try
        {
            // A tree with only the root carries no history worth keeping.
            if (!_tree.AllNodes().Any(n => n.Parent is not null))
                HistoryStore.Delete(_currentPath!);
            else
                HistoryStore.Save(_currentPath!, _tree.Serialize(), _savedText);
        }
        catch (Exception ex) { CrashLog.Log("history persist failed", ex); }
    }

    /// <summary>Full document snapshot (path + saved/current text + entire history) for tab transfer.</summary>
    public sealed record DocDto(string? Path, string SavedText, string CurrentText, TreeDto Tree);

    /// <summary>Serialise this document (with its whole undo history) for a cross-process tab move.</summary>
    public DocDto SerializeDocument()
    {
        CommitPending();
        return new DocDto(_currentPath, _savedText, Editor.Text, _tree.Serialize());
    }

    /// <summary>Rebuild a document (moved here from another process) into this blank view.</summary>
    public void LoadTransferred(DocDto dto)
    {
        _currentPath = dto.Path;
        _savedText = dto.SavedText;
        _tree = UndoTree.Deserialize(dto.Tree);
        _currentText = dto.CurrentText;
        _typingNode = null;
        RebuildHistoryRows();

        _suppressTextChange = true;
        Editor.Text = dto.CurrentText;
        Editor.CaretIndex = Math.Clamp(_tree.Current.CaretIndex, 0, dto.CurrentText.Length);
        _suppressTextChange = false;

        _lastAutosave = null;
        _lastRecoveryText = "";

        SetCurrent(_tree.Current);
        RaiseAll();
        WriteRecovery();   // this instance now owns crash recovery for the moved document
        OnPathEstablished();
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
        OnPathEstablished();
    }

    // ===================== Editing / commits =====================

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChange)
            return;
        if (AppSettings.Current.UndoPerCharacter)
        {
            // Every keystroke is its own undo step — commit right away instead of debouncing.
            _debounce.Stop();
            CommitPending();
            RaiseAll();
            return;
        }
        _debounce.Stop();
        _debounce.Start();
        RaiseAll();

        // Keep search results in sync with edits made while the pane is open.
        if (SearchPanel.Visibility == Visibility.Visible)
            QueueSearch();
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
            // the pause was short; otherwise start a fresh checkpoint node. Per-character mode never
            // coalesces (every edit is its own node).
            double windowMs = AppSettings.Current.UndoCoalesceSeconds * 1000.0;
            bool canCoalesce = !AppSettings.Current.UndoPerCharacter
                               && ReferenceEquals(_tree.Current, _typingNode)
                               && (DateTime.Now - _lastEditTime).TotalMilliseconds <= windowMs;

            if (canCoalesce && _tree.Coalesce(Editor.Text, Editor.CaretIndex))
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
                RebuildHistoryRows();   // a node was added — reflect the new shape in the list
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
        // Non-reentrant: programmatically selecting the target below fires list-selection events
        // that must not re-enter this mid-jump and start a second navigation.
        if (_navigating)
            return;
        _navigating = true;
        try
        {
            // Reconstruct the target's text from the immutable root, replaying forward edits down
            // to it. This depends on nothing we currently hold materialised, so a stale or
            // corrupted _currentText can't derail the jump or lose the document — clicking any
            // node always restores that node's exact text.
            string text = _tree.Materialize(target);
            _tree.SetCurrent(target);
            _currentText = text;
            _typingNode = null;   // a jump ends the current typing burst — next edit starts anew
            ApplyNode(target, text);
            HideTreeIfTemporary();   // a branch was chosen — collapse a pane revealed only to pick it
        }
        catch (Exception ex)
        {
            CrashLog.Log("NavigateTo failed", ex);
        }
        finally
        {
            _navigating = false;
        }
    }

    private void ApplyNode(UndoNode node, string text)
    {
        _suppressTextChange = true;
        Editor.Text = text;
        int caret = Math.Clamp(node.CaretIndex, 0, text.Length);
        Editor.CaretIndex = caret;
        _suppressTextChange = false;

        SetCurrent(node);
        RaiseAll();
        Editor.Focus();

        // Replacing the whole text resets the editor's scroll, and a programmatic CaretIndex doesn't
        // reliably scroll the caret into view — so jumping to a node could leave the changed region
        // off-screen. Bring the node's caret (where the edit happened) into view, centered, once the
        // TextBox has laid out the new text. Deferred to Background so the layout pass has run.
        Dispatcher.BeginInvoke(new Action(() => BringIntoView(caret, center: true)),
                               DispatcherPriority.Background);
    }

    /// <summary>Highlight and select the given node in the history list.</summary>
    private void SetCurrent(UndoNode node)
    {
        // Clear both flags across the tree so stale highlights (IsCurrent) and stale selection
        // rows (IsSelected — the TwoWay binding otherwise leaves earlier nodes marked selected)
        // don't linger and make it look like several nodes are active at once.
        _suppressTreeSelection = true;
        try
        {
            // In condensed (branches-only) mode a plain mid-run node is hidden until it becomes the
            // current position, and a node that *was* current collapses away once we move off it.
            // Rebuild so the new current shows as a row and the old one disappears before we
            // highlight and scroll. (In show-all mode every node is always listed, so no rebuild.)
            if (AppSettings.Current.HistoryBranchesOnly)
                RebuildRowsCore();

            foreach (var n in _tree.AllNodes())
            {
                if (!ReferenceEquals(n, node))
                {
                    n.IsCurrent = false;
                    n.IsSelected = false;
                }
            }
            node.IsCurrent = true;
            node.IsSelected = true;

            // The root isn't shown as a row; only scroll to nodes that are actually in the list.
            if (_historyRows.Contains(node))
                Tree.ScrollIntoView(node);
        }
        finally
        {
            _suppressTreeSelection = false;
        }
    }

    /// <summary>
    /// Rebuild the flattened history list from the current tree shape. Pre-order walk; each row's
    /// indent is its <see cref="BranchDepth"/> so a straight-line edit history stays flat and only
    /// the extra children of a fork — where redo is ambiguous — step further right. The root is not
    /// listed (it's the blank/initial state).
    ///
    /// When <see cref="AppSettings.HistoryBranchesOnly"/> is on (the default), a straight run of
    /// edits collapses to just its fork points, tips, and the current node, so a long typing session
    /// doesn't bury the pane in one row per keystroke. Undo/redo stay granular either way — only the
    /// display condenses.
    /// </summary>
    private void RebuildHistoryRows()
    {
        _suppressTreeSelection = true;
        RebuildRowsCore();
        _suppressTreeSelection = false;
    }

    /// <summary>The list-clearing rebuild itself, without touching selection suppression — callers
    /// that already hold <c>_suppressTreeSelection</c> (e.g. <see cref="SetCurrent"/>) use this.</summary>
    private void RebuildRowsCore()
    {
        _historyRows.Clear();
        bool branchesOnly = AppSettings.Current.HistoryBranchesOnly;
        var kids = _tree.Root.Children;
        for (int i = 0; i < kids.Count; i++)
            AppendRows(kids[i], branchesOnly);
    }

    private void AppendRows(UndoNode node, bool branchesOnly)
    {
        if (!branchesOnly || IsBranchRow(node))
        {
            node.IndentLevel = BranchDepth(node);
            _historyRows.Add(node);
        }
        var kids = node.Children;
        for (int i = 0; i < kids.Count; i++)
            AppendRows(kids[i], branchesOnly);
    }

    /// <summary>In condensed mode, a node earns its own row only if it's a fork (2+ children), a tip
    /// (no children), or the current position — the interesting points of the history graph.</summary>
    private bool IsBranchRow(UndoNode node) =>
        node.Children.Count != 1 || ReferenceEquals(node, _tree.Current);

    /// <summary>How far right a node sits: the number of steps on its root path that took a
    /// non-first (i.e. forked) child. A straight-line history is depth 0; each additional branch of
    /// a fork is one level deeper. Independent of which rows are shown, so both display modes share
    /// the same indentation.</summary>
    private static int BranchDepth(UndoNode node)
    {
        int depth = 0;
        for (var n = node; n?.Parent is not null; n = n.Parent)
        {
            var siblings = n.Parent!.Children;
            if (siblings.Count > 0 && !ReferenceEquals(siblings[0], n))
                depth++;
        }
        return depth;
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

    private void Tree_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTreeSelection || _navigating)
            return;
        if (Tree.SelectedItem is UndoNode node && node != _tree.Current)
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
            BeginSelfWrite();          // stop the watcher mistaking our own write for an external one
            WriteTextToFile(path, Editor.Text);
            _currentPath = path;
            _savedText = Editor.Text;
            _lastAutosave = null;
            DeleteRecovery();
            OnPathEstablished();       // (re)start the watcher, capture the new disk stamp, (re)apply the lock
            PersistHistory();          // write the whole history sidecar (now anchored to the saved text)
            RaiseAll();
            return true;
        }
        catch (Exception ex)
        {
            ThemedDialog.Show(Window.GetWindow(this), ex.Message, "Save failed",
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
        var result = ThemedDialog.Show(Window.GetWindow(this),
            $"Save changes to {name}?",
            "NotepadRedo",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return result switch
        {
            MessageBoxResult.Yes => Save(false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>Outcome of a multi-document save prompt (see <see cref="ConfirmDiscardForQuit"/>).</summary>
    public enum SavePromptResult
    {
        /// <summary>This document was saved or discarded; keep prompting for the rest.</summary>
        Handled,
        /// <summary>Save this document and every remaining dirty one without further prompts.</summary>
        SaveRemaining,
        /// <summary>The user cancelled; abort the close/quit and keep everything open.</summary>
        Cancel,
    }

    /// <summary>
    /// Prompt to save this document when it's dirty, offering a "Save All" shortcut that saves the
    /// remaining dirty documents without prompting. Returns <see cref="SavePromptResult.Handled"/>
    /// when the document was saved or discarded, <see cref="SavePromptResult.SaveRemaining"/> when
    /// the user chose Save All, or <see cref="SavePromptResult.Cancel"/> on cancel (or a failed save).
    /// </summary>
    public SavePromptResult ConfirmDiscardForQuit()
    {
        if (!IsDirty)
            return SavePromptResult.Handled;
        string name = string.IsNullOrEmpty(_currentPath) ? "Untitled" : Path.GetFileName(_currentPath);
        int choice = ThemedDialog.ShowSaveAll(Window.GetWindow(this),
            $"Save changes to {name}?", "NotepadRedo");
        return choice switch
        {
            0 => Save(false) ? SavePromptResult.Handled : SavePromptResult.Cancel,       // Save
            1 => Save(false) ? SavePromptResult.SaveRemaining : SavePromptResult.Cancel, // Save All
            2 => SavePromptResult.Handled,                                               // Don't Save
            _ => SavePromptResult.Cancel,                                                // Cancel / Esc
        };
    }

    /// <summary>Start a fresh history tree seeded with the given text.</summary>
    private void ResetTree(string text)
    {
        _tree = new UndoTree(text);
        _currentText = text;
        _typingNode = null;
        RebuildHistoryRows();

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

    /// <summary>Apply the shared editor-font preference to this document's text area. The size is
    /// given in points (as stored/picked) and converted to WPF's device-independent pixels here so
    /// callers never have to remember the conversion.</summary>
    public void ApplyFont(string family, double sizePoints, bool bold, bool italic)
    {
        Editor.FontFamily = new System.Windows.Media.FontFamily(family);
        Editor.FontSize = sizePoints * (96.0 / 72.0);
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
        // The search pane (when open) sits to the right of the tree, so its width is reserved space
        // the tree column must not include.
        double rightExtra = SearchPanel.Visibility == Visibility.Visible ? SearchColumn.ActualWidth : 0;
        // Cursor X within this control; the tree fills everything to the right of the cursor
        // except the reserved search pane.
        double cursorX = e.GetPosition(this).X;
        double target = ActualWidth - cursorX - splitter / 2 - rightExtra;

        // Never crowd the editor out: cap the tree at the room left after the editor's minimum.
        double editorMin = 200;
        double max = Math.Max(TreeMinWidth, ActualWidth - editorMin - splitter - rightExtra);
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

    private bool _suppressHistoryModeEvent;

    private void ShowAllEdits_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressHistoryModeEvent)
            return;
        // The toggle reads "Show all edits", so checked = branches-only OFF.
        bool branchesOnly = ShowAllEditsToggle.IsChecked != true;
        AppSettings.Current.HistoryBranchesOnly = branchesOnly;
        AppSettings.Current.Save();
        // Apply to every open document so the choice is process-wide.
        foreach (Window w in Application.Current.Windows)
            if (w is MainWindow mw)
                foreach (var v in mw.AllEditorViews())
                    v.ApplyHistoryMode(branchesOnly);
    }

    /// <summary>Reflect the branches-only / show-all-edits preference in this view's toggle and
    /// rebuild its history list, keeping the current node highlighted and scrolled into view.</summary>
    public void ApplyHistoryMode(bool branchesOnly)
    {
        bool showAll = !branchesOnly;
        if ((ShowAllEditsToggle.IsChecked == true) != showAll)
        {
            _suppressHistoryModeEvent = true;
            ShowAllEditsToggle.IsChecked = showAll;
            _suppressHistoryModeEvent = false;
        }
        RebuildHistoryRows();
        // Re-highlight the current node (and, in branches-only, make sure it's listed).
        SetCurrent(_tree.Current);
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

    /// <summary>
    /// Route Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z to the history tree ourselves. The editor's built-in
    /// undo is off (IsUndoEnabled=False), but the TextBox still swallows these gestures before the
    /// window-level KeyBindings can fire — so we catch them here, on the tunneling PreviewKeyDown,
    /// and handle them before the TextBox sees them.
    /// </summary>
    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            ShowSearch(true);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
                 || (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z))
        {
            Redo();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == 0
                 && AppSettings.Current.UndoBreakOnEnter && !AppSettings.Current.UndoPerCharacter)
        {
            // Let the newline get inserted first, then close this line's burst so the next line
            // becomes a fresh undo step. (Per-character mode already splits every keystroke.)
            Dispatcher.BeginInvoke(new Action(EndBurst), DispatcherPriority.Background);
        }
    }

    /// <summary>Commit whatever is pending and end the current typing burst so the next edit
    /// starts a brand-new history node.</summary>
    private void EndBurst()
    {
        CommitPending();
        _typingNode = null;
    }

    /// <summary>Isolate a paste as its own history node: close the burst before the paste lands
    /// and again afterwards, so the pasted text is separate from the typing around it.</summary>
    private void Editor_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!AppSettings.Current.UndoBreakOnPaste)
            return;
        EndBurst();
        Dispatcher.BeginInvoke(new Action(EndBurst), DispatcherPriority.Background);
    }

    private void RaiseAll()
    {
        // Compute Ln/Col defensively. This runs from TextChanged, which also fires mid text-reset
        // (ResetTree / ReloadFromDisk swap Editor.Text wholesale): at that instant the caret can be
        // out of range for the new text, or the line layout isn't measured yet, and the
        // GetLineIndex/GetCharacterIndex calls throw ArgumentOutOfRangeException. Clamp and guard so a
        // reload can't crash the UI thread.
        int line = 0, col = 0;
        try
        {
            int caret = Math.Max(0, Math.Min(Editor.CaretIndex, Editor.Text.Length));
            line = Editor.GetLineIndexFromCharacterIndex(caret);
            if (line < 0) line = 0;
            int lineStart = Editor.GetCharacterIndexFromLineIndex(line);
            col = Math.Max(0, caret - lineStart);
        }
        catch { /* line metrics not ready — fall back to Ln 1, Col 1 for this tick */ }
        CaretText = $"Ln {line + 1}, Col {col + 1}";
        CountText = $"{Editor.Text.Length} chars";
        NodeText = $"node #{_tree.Current.Id}";

        StatusChanged?.Invoke(this, EventArgs.Empty);
        TitleChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(TabTitle));
    }

    /// <summary>
    /// Triple-click selects the whole paragraph — the run of text between hard line breaks — the way
    /// a word processor does. (WPF's TextBox only gives a word on double-click and does nothing useful
    /// on the third click.) With word wrap on, a paragraph is one logical line that may span several
    /// visual rows, so the entire wrapped block is selected.
    /// </summary>
    private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 3)
            return;

        string text = Editor.Text;
        // Use the click position, not CaretIndex: on the preview event the caret hasn't moved yet.
        int idx = Editor.GetCharacterIndexFromPoint(e.GetPosition(Editor), snapToText: true);
        if (idx < 0)
            idx = Editor.CaretIndex;
        idx = Math.Max(0, Math.Min(idx, text.Length));

        // Grow outward to the nearest line break on each side (handles \n and \r\n; the break chars
        // themselves are left out of the selection).
        int start = idx;
        while (start > 0 && text[start - 1] != '\n' && text[start - 1] != '\r')
            start--;
        int end = idx;
        while (end < text.Length && text[end] != '\n' && text[end] != '\r')
            end++;

        Editor.Select(start, end - start);
        e.Handled = true;   // suppress the default third-click so it can't collapse our selection
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
        _searchDebounce?.Stop();
        _watchDebounce.Stop();
    }

    /// <summary>
    /// Force an immediate crash-recovery snapshot of the current text. Used before a forced /
    /// redeploy shutdown so no unsaved work is lost even though the process is about to be closed.
    /// </summary>
    public void FlushRecovery() => WriteRecovery(force: true);

    /// <summary>Stop timers and clear the recovery file — called when the tab is closed cleanly.</summary>
    public void Dispose()
    {
        PersistHistory();   // capture any post-save branch exploration while the doc is clean
        StopTimers();
        StopWatching();
        ReleaseFileLock();
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

    // ===================== External-change detection / file lock =====================

    /// <summary>
    /// Called whenever this document acquires or changes its on-disk path (open / save / transfer /
    /// recover): capture the current disk timestamp so our own write isn't mistaken for an external
    /// change, (re)start the file-system watcher, and apply (or release) the deny-write lock.
    /// </summary>
    private void OnPathEstablished()
    {
        CaptureDiskStamp();
        ApplyFileLock();
        StartWatching();
    }

    /// <summary>Record the file's current modified-time and size as the "known" (ours) disk state.</summary>
    private void CaptureDiskStamp()
    {
        try
        {
            var fi = new FileInfo(_currentPath!);
            if (fi.Exists) { _diskWriteTimeUtc = fi.LastWriteTimeUtc; _diskLength = fi.Length; }
        }
        catch { /* stamp stays as-is; a spurious prompt is better than a crash */ }
    }

    /// <summary>Mark a short window during which watcher events are treated as our own write.</summary>
    private void BeginSelfWrite() => _suppressUntil = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);

    private void StartWatching()
    {
        StopWatching();
        if (string.IsNullOrEmpty(_currentPath) || !AppSettings.Current.WatchExternalChanges)
            return;
        try
        {
            var dir = Path.GetDirectoryName(_currentPath);
            if (string.IsNullOrEmpty(dir))
                return;
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_currentPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                             | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            _watcher.Changed += Watcher_Event;
            _watcher.Created += Watcher_Event;
            _watcher.Renamed += Watcher_Event;
            _watcher.EnableRaisingEvents = true;
        }
        catch { _watcher = null; }
    }

    private void StopWatching()
    {
        if (_watcher is null)
            return;
        try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); }
        catch { }
        _watcher = null;
    }

    // FS events arrive on a threadpool thread — marshal onto the UI thread and debounce the burst.
    private void Watcher_Event(object sender, FileSystemEventArgs e)
    {
        try { Dispatcher.BeginInvoke(new Action(() => { _watchDebounce.Stop(); _watchDebounce.Start(); })); }
        catch { }
    }

    /// <summary>
    /// After a settled burst of watcher events, decide whether the file genuinely changed under us
    /// and, if so, surface the resolution prompt (or route the change to an open merge viewer).
    /// </summary>
    private void HandleExternalChange()
    {
        if (string.IsNullOrEmpty(_currentPath))
            return;

        FileInfo fi;
        try { fi = new FileInfo(_currentPath); } catch { return; }
        if (!fi.Exists)
            return;   // deleted/renamed away — keep our buffer; a Save re-creates it

        DateTime wt; long len;
        try { wt = fi.LastWriteTimeUtc; len = fi.Length; } catch { return; }

        if (wt == _diskWriteTimeUtc && len == _diskLength)
            return;   // no real change since we last looked
        if (DateTime.UtcNow < _suppressUntil)
        {
            _diskWriteTimeUtc = wt; _diskLength = len;   // our own save — adopt the new stamp
            return;
        }

        string diskText;
        try { diskText = ReadAllTextShared(_currentPath); }
        catch { return; }   // mid-write by the other program; the next event will settle

        if (diskText == Editor.Text)
        {
            _diskWriteTimeUtc = wt; _diskLength = len;   // content identical (e.g. touched) — nothing to do
            return;
        }

        // A merge viewer is already open for this document — fold the new version into it.
        if (_mergeWindow is not null)
        {
            _diskWriteTimeUtc = wt; _diskLength = len;
            _mergeWindow.NotifyDiskChanged(diskText);
            return;
        }

        // A prompt is already up — stash the newest content and re-check once it closes.
        if (_resolving)
        {
            _pendingWhileResolving = diskText;
            return;
        }

        _diskWriteTimeUtc = wt; _diskLength = len;
        ResolveExternalChange(diskText);
    }

    /// <summary>The five-way "the file changed on disk" resolution prompt.</summary>
    private void ResolveExternalChange(string diskText)
    {
        _resolving = true;
        try
        {
            string name = string.IsNullOrEmpty(_currentPath) ? "This file" : Path.GetFileName(_currentPath!);
            bool dirty = IsDirty;
            var choices = new List<string>
            {
                dirty ? "Reload from disk (discard my unsaved edits)" : "Reload from disk",
                "Keep my version (ignore the change; overwrites on next save)",
                "Save my version to another file, then reload from disk",
                "Save the disk version to another file, then keep mine",
                "Show a diff and merge\u2026",
            };
            var owner = Window.GetWindow(this);
            int choice = ThemedDialog.ShowChoices(owner,
                $"\u201c{name}\u201d was changed by another program.",
                "File changed on disk", choices, MessageBoxImage.Warning, defaultIndex: dirty ? 4 : 0);

            switch (choice)
            {
                case 0: ReloadFromDisk(diskText); break;
                case 1: break;   // keep mine — the advanced stamp stops us re-prompting
                case 2: if (SaveSnapshot("mine", Editor.Text) is not null) ReloadFromDisk(diskText); break;
                case 3: SaveSnapshot("disk", diskText); break;
                case 4: OpenMerge(diskText); break;
                default: break;  // dismissed — keep mine
            }
        }
        finally { _resolving = false; }

        // A newer version landed while the prompt was up — re-evaluate against it.
        if (_pendingWhileResolving is not null)
        {
            _pendingWhileResolving = null;
            Dispatcher.BeginInvoke(new Action(HandleExternalChange), DispatcherPriority.Background);
        }
    }

    /// <summary>Open the side-by-side merge viewer and apply its outcome.</summary>
    private void OpenMerge(string diskText)
    {
        var owner = Window.GetWindow(this);
        var win = new DiffMergeWindow(owner, _currentPath ?? "", Editor.Text, diskText);
        _mergeWindow = win;
        try { win.ShowDialog(); }
        finally { _mergeWindow = null; }

        if (win.Saved && win.ResultText is string merged)
        {
            SetEditorText(merged);   // land the merged text as an edit…
            Save(false);             // …and write it straight to disk
        }
        else if (win.ExitAndReload)
        {
            // Both versions were saved to sibling files; reload the current on-disk version fresh.
            try { ReloadFromDisk(ReadAllTextShared(_currentPath!)); } catch { }
        }
    }

    /// <summary>Replace the editor's text with disk content and re-anchor the history/dirty state.</summary>
    private void ReloadFromDisk(string diskText)
    {
        _savedText = diskText;
        DeleteRecovery();
        ResetTree(diskText);
        CaptureDiskStamp();
        // The content changed under us, so the old history no longer reconstructs it — overwrite the
        // sidecar with the fresh (single-root) tree anchored to the new disk text.
        PersistHistory();
    }

    /// <summary>Programmatically set the editor text and commit it as a single history node.</summary>
    private void SetEditorText(string text)
    {
        _suppressTextChange = true;
        Editor.Text = text;
        Editor.CaretIndex = Math.Clamp(Editor.CaretIndex, 0, text.Length);
        _suppressTextChange = false;
        _typingNode = null;
        CommitPending();
    }

    /// <summary>Write a snapshot next to the original file with a timestamped suffix; report where.</summary>
    private string? SaveSnapshot(string suffix, string text)
    {
        try
        {
            string basePath = string.IsNullOrEmpty(_currentPath) ? "untitled.txt" : _currentPath!;
            string dir = Path.GetDirectoryName(basePath) ?? Environment.CurrentDirectory;
            string stem = Path.GetFileNameWithoutExtension(basePath);
            string ext = Path.GetExtension(basePath);
            if (string.IsNullOrEmpty(stem)) stem = "untitled";
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string path = Path.Combine(dir, $"{stem}.{suffix}-{stamp}{ext}");
            File.WriteAllText(path, text);
            ThemedDialog.Show(Window.GetWindow(this), $"Saved to:\n{path}", "Saved",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return path;
        }
        catch (Exception ex)
        {
            ThemedDialog.Show(Window.GetWindow(this), ex.Message, "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>Re-evaluate the external-change watcher for this view after the setting changed.</summary>
    public void ApplyWatchSetting() => StartWatching();

    /// <summary>Re-evaluate the deny-write lock for this view after the setting changed.</summary>
    public void ApplyLockSetting() => ApplyFileLock();

    // ----- file lock -----

    private void ApplyFileLock()
    {
        // Drop a stale lock when the setting was turned off or the path changed.
        if (_lockStream is not null &&
            (!AppSettings.Current.LockFileWhileOpen || !PathsEqual(_lockedPath, _currentPath)))
            ReleaseFileLock();

        if (!AppSettings.Current.LockFileWhileOpen || _lockStream is not null
            || string.IsNullOrEmpty(_currentPath) || !File.Exists(_currentPath))
            return;

        try
        {
            // Hold the file open for our own read/write while denying other writers (they may read).
            _lockStream = new FileStream(_currentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            _lockedPath = _currentPath;
        }
        catch
        {
            // Already open for writing elsewhere, etc. — proceed unlocked rather than failing to open.
            _lockStream = null;
            _lockedPath = null;
        }
    }

    private void ReleaseFileLock()
    {
        try { _lockStream?.Dispose(); } catch { }
        _lockStream = null;
        _lockedPath = null;
    }

    /// <summary>Write text to a path, going through the held lock handle when it owns that path.</summary>
    private void WriteTextToFile(string path, string text)
    {
        if (_lockStream is not null && PathsEqual(_lockedPath, path))
        {
            _lockStream.Position = 0;
            _lockStream.SetLength(0);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            _lockStream.Write(bytes, 0, bytes.Length);
            _lockStream.Flush(flushToDisk: true);
        }
        else
        {
            File.WriteAllText(path, text);
        }
    }

    /// <summary>Read a file without locking out other readers/writers (tolerant of concurrent access).</summary>
    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    // ===================== Search =====================

    /// <summary>Open the search pane and focus its input (called by Ctrl+F and the Edit menu).</summary>
    public void OpenSearch() => ShowSearch(true);

    /// <summary>True while the search pane is showing.</summary>
    public bool IsSearchOpen => SearchPanel.Visibility == Visibility.Visible;

    /// <summary>Raised when the search pane is shown or hidden (for toolbar toggle sync).</summary>
    public event EventHandler? SearchVisibilityChanged;

    /// <summary>Open the search pane if closed, close it if open (toolbar toggle).</summary>
    public void ToggleSearch() => ShowSearch(!IsSearchOpen);

    private void ShowSearch(bool show)
    {
        if (show)
        {
            SearchPanel.Visibility = Visibility.Visible;
            SearchColumn.MinWidth = 180;
            SearchColumn.Width = new GridLength(SearchPaneWidth);
            SearchBox.Focus();
            SearchBox.SelectAll();
            RunSearch();
        }
        else
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            SearchColumn.MinWidth = 0;
            SearchColumn.Width = new GridLength(0);
            Editor.Focus();
        }
        SearchVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SearchClose_Click(object sender, RoutedEventArgs e) => ShowSearch(false);

    private void SearchInput_Changed(object sender, TextChangedEventArgs e) => QueueSearch();

    // Checkboxes (case-sensitive / whole-word).
    private void SearchOption_Changed(object sender, RoutedEventArgs e)
    {
        // Skip while Proximity_Changed is programmatically flipping the whole-word box; it runs the
        // search itself afterward, so we'd otherwise search twice.
        if (_syncingWholeWord) return;
        if (IsLoaded) RunSearch();
    }

    // Remembers the user's "match whole word only" choice from before proximity mode auto-forced it
    // on, so leaving proximity restores it (null = not currently overridden).
    private bool? _wholeWordBeforeProximity;
    // Set while Proximity_Changed toggles WholeWordCheck itself, to suppress its change handler.
    private bool _syncingWholeWord;

    // The "near each other" checkbox switches between plain literal search and the multi-item
    // proximity list. Show/hide the item-list UI and the "within N" row, then re-search.
    private void Proximity_Changed(object sender, RoutedEventArgs e)
    {
        bool prox = ProximityCheck.IsChecked == true;
        TermListPanel.Visibility = prox ? Visibility.Visible : Visibility.Collapsed;
        ProximityRow.Visibility = prox ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.ToolTip = prox
            ? "Type an item and press Enter to add it; results match where all items appear near each other."
            : "Type text to find — matched exactly as typed.";

        // Proximity items are conceptually whole words (searching "op" and "po" shouldn't match
        // inside "opposite"), so default "match whole word only" ON when entering proximity mode.
        // The user can still uncheck it for substring proximity. Restore the prior setting on exit
        // so plain search isn't left with an unexpected whole-word default.
        _syncingWholeWord = true;
        try
        {
            if (prox)
            {
                _wholeWordBeforeProximity ??= WholeWordCheck.IsChecked == true;
                WholeWordCheck.IsChecked = true;
            }
            else if (_wholeWordBeforeProximity is bool prev)
            {
                WholeWordCheck.IsChecked = prev;
                _wholeWordBeforeProximity = null;
            }
        }
        finally { _syncingWholeWord = false; }

        if (IsLoaded) RunSearch();
    }

    // Proximity unit dropdown (its own signature so the XAML delegate binds cleanly).
    private void SearchUnit_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) RunSearch();
    }

    /// <summary>Add the search box's current text as a new proximity item, then clear the box for
    /// the next one. No-op if the box is empty.</summary>
    private void AddTermFromBox()
    {
        string t = SearchBox.Text;
        if (string.IsNullOrEmpty(t))
            return;
        _searchTerms.Add(new SearchTermVM { Text = t });
        SearchBox.Clear();
        SearchBox.Focus();
        RunSearch();
    }

    private void RemoveTerm_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SearchTermVM vm)
            RemoveTerm(vm);
    }

    // An item was edited in place — re-run (debounced) so results track the change.
    private void TermEdit_Changed(object sender, TextChangedEventArgs e) => QueueSearch();

    /// <summary>Key handling inside an item's edit box. Pressing Delete while the whole item is
    /// highlighted (the state right after you Tab onto it, or Select-All) removes the item from the
    /// list instead of just clearing its text — so you can Tab through and prune with Delete.</summary>
    private void TermEdit_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not SearchTermVM vm)
            return;

        if (e.Key == Key.Delete && tb.SelectionLength == tb.Text.Length)
        {
            RemoveTerm(vm);
            e.Handled = true;
        }
    }

    /// <summary>Remove an item, re-search, and move focus to a sensible neighbour (the item that
    /// slid into its place, else the previous one, else back to the add box) so repeated Delete
    /// keeps pruning down the list.</summary>
    private void RemoveTerm(SearchTermVM vm)
    {
        int idx = _searchTerms.IndexOf(vm);
        if (idx < 0)
            return;
        _searchTerms.Remove(vm);
        RunSearch();

        // Containers regenerate after the removal; defer focus until layout has caught up.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_searchTerms.Count == 0)
            {
                SearchBox.Focus();
                return;
            }
            int target = Math.Min(idx, _searchTerms.Count - 1);
            var tb = TermTextBoxAt(target);
            if (tb is not null) { tb.Focus(); tb.SelectAll(); }
            else SearchBox.Focus();
        }), DispatcherPriority.Background);
    }

    /// <summary>The editable TextBox inside the item container at <paramref name="index"/>, if any.</summary>
    private TextBox? TermTextBoxAt(int index)
    {
        if (index < 0 || index >= _searchTerms.Count)
            return null;
        return TermsList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement fe
            ? FindVisualChild<TextBox>(fe)
            : null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                return typed;
            if (FindVisualChild<T>(child) is T deeper)
                return deeper;
        }
        return null;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ShowSearch(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _searchDebounce?.Stop();

            // In proximity mode, Enter commits the typed text as a new item (when non-empty) instead
            // of stepping results — that's how you build up the list to match near each other.
            if (ProximityCheck.IsChecked == true && !string.IsNullOrEmpty(SearchBox.Text))
            {
                AddTermFromBox();
                e.Handled = true;
                return;
            }

            // Otherwise Enter runs the search now and steps to the next result (wrapping).
            RunSearch();
            if (_searchResults.Count > 0)
            {
                int next = ResultsList.SelectedIndex + 1;
                if (next >= _searchResults.Count) next = 0;
                ResultsList.SelectedIndex = next;
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }
            e.Handled = true;
        }
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressResultNav)
            return;
        if (ResultsList.SelectedItem is SearchResultVM r)
            NavigateToMatch(r);
    }

    /// <summary>Debounce rapid input so we don't re-scan the whole document on every keystroke.</summary>
    private void QueueSearch()
    {
        if (_searchDebounce is null)
        {
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _searchDebounce.Tick += (_, _) => { _searchDebounce!.Stop(); RunSearch(); };
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void RunSearch()
    {
        if (SearchPanel.Visibility != Visibility.Visible)
            return;

        _suppressResultNav = true;
        _searchResults.Clear();
        _suppressResultNav = false;

        bool caseSensitive = CaseSensitiveCheck.IsChecked == true;
        bool wholeWord = WholeWordCheck.IsChecked == true;
        bool proximity = ProximityCheck.IsChecked == true;

        string text = Editor.Text;
        List<SearchMatch> matches;

        if (proximity)
        {
            // Match near-each-other over the explicit item list, plus whatever's currently typed in
            // the box as a provisional item so results narrow live before you press Enter to add it.
            var terms = _searchTerms.Select(t => t.Text).Where(s => s.Length > 0).ToList();
            if (!string.IsNullOrEmpty(SearchBox.Text))
                terms.Add(SearchBox.Text);

            if (terms.Count == 0)
            {
                SearchStatus.Text = "";
                return;
            }

            ProximityUnit unit = ProximityUnitBox.SelectedIndex switch
            {
                1 => ProximityUnit.Words,
                2 => ProximityUnit.Lines,
                _ => ProximityUnit.Characters,
            };
            if (!int.TryParse(ProximityN.Text, out int n) || n < 0)
                n = 0;

            matches = SearchEngine.FindProximity(text, terms, caseSensitive, unit, n, wholeWord);
        }
        else
        {
            // Plain mode: match the box text exactly as typed (spaces, quotes, and all).
            string query = SearchBox.Text;
            if (string.IsNullOrEmpty(query))
            {
                SearchStatus.Text = "";
                return;
            }
            matches = SearchEngine.FindAll(text, query, caseSensitive, wholeWord);
        }

        foreach (var m in matches)
        {
            int line = LineOf(text, m.Start);
            _searchResults.Add(new SearchResultVM
            {
                Start = m.Start,
                Length = m.Length,
                Preview = SearchEngine.Preview(text, m),
                Location = $"Ln {line + 1}",
            });
        }

        SearchStatus.Text = matches.Count switch
        {
            0 => "No results",
            1 => "1 result",
            _ => $"{matches.Count} results",
        };
    }

    /// <summary>Move the caret/selection to a result and scroll it into view.</summary>
    private void NavigateToMatch(SearchResultVM r)
    {
        int len = Editor.Text.Length;
        int start = Math.Clamp(r.Start, 0, len);
        int selLen = Math.Clamp(r.Length, 0, len - start);

        Editor.Focus();
        // Select() highlights the match and leaves the caret at its end. (Don't set
        // CaretIndex afterwards — doing so collapses the selection, hiding the match.)
        Editor.Select(start, selLen);

        // Defer the scroll to Background priority so the TextBox runs a layout pass
        // after being focused/selected first. Queried before that pass,
        // GetLineIndexFromCharacterIndex / ScrollToLine / GetRectFromCharacterIndex
        // return stale positions and the match is left off-screen — which is why the
        // cursor "couldn't be found" after clicking a result.
        Dispatcher.BeginInvoke(new Action(() => BringIntoView(start)),
                               DispatcherPriority.Background);

        RaiseAll();
    }

    /// <summary>Scroll the editor so the character at <paramref name="index"/> is visible,
    /// both vertically (its line) and horizontally. The horizontal pass matters when
    /// word-wrap is off and the match sits far to the right: <see cref="System.Windows.Controls.Primitives.TextBoxBase.ScrollToLine"/>
    /// only moves vertically, so the caret would otherwise stay scrolled off the right edge.</summary>
    private void BringIntoView(int index, bool center = false)
    {
        int len = Editor.Text.Length;
        index = Math.Clamp(index, 0, len);

        int line = Editor.GetLineIndexFromCharacterIndex(index);
        if (line >= 0)
            Editor.ScrollToLine(line);   // realizes the line and ensures it's at least visible

        // For node jumps, center the changed line in the viewport so its surrounding context is
        // visible (ScrollToLine alone can leave it pinned to an edge). GetRectFromCharacterIndex is
        // viewport-relative, so add VerticalOffset to get the content-space Y.
        if (center)
        {
            Rect lr = Editor.GetRectFromCharacterIndex(index);
            if (!lr.IsEmpty && Editor.ViewportHeight > 0)
            {
                double contentY = lr.Y + Editor.VerticalOffset;
                double target = contentY - (Editor.ViewportHeight - lr.Height) / 2;
                Editor.ScrollToVerticalOffset(Math.Max(0, target));
            }
        }

        // GetRectFromCharacterIndex is viewport-relative, so an X outside
        // [0, ViewportWidth] means the character is scrolled off-screen horizontally.
        Rect rect = Editor.GetRectFromCharacterIndex(index);
        if (!rect.IsEmpty && Editor.ViewportWidth > 0)
        {
            const double margin = 24;
            double contentX = rect.X + Editor.HorizontalOffset;
            if (rect.X < margin)
                Editor.ScrollToHorizontalOffset(Math.Max(0, contentX - margin));
            else if (rect.X > Editor.ViewportWidth - margin)
                Editor.ScrollToHorizontalOffset(contentX - Editor.ViewportWidth + margin);
        }
    }

    // ===================== Drag-select auto-scroll =====================

    /// <summary>While the left button is held (a text selection is in progress), take the drag over
    /// once the cursor passes above/below the text viewport and drive the scroll smoothly instead of
    /// letting WPF lurch. Once taken over, we keep driving the whole drag (even back inside the view)
    /// so the selection always pivots on the original mouse-down point.</summary>
    private void Editor_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDragScroll();
            return;
        }

        var pos = e.GetPosition(Editor);
        double top = Editor.Padding.Top;
        double bottom = top + Editor.ViewportHeight;
        bool beyond = pos.Y < top || pos.Y > bottom;

        // Normal in-view selection that hasn't triggered auto-scroll yet: leave it to WPF.
        if (!_dragScrollActive && !beyond)
            return;

        if (!_dragScrollActive)
        {
            _dragScrollActive = true;
            _dragScrollOffset = Editor.VerticalOffset;   // seed our authoritative scroll position
            // Fix the end opposite the drag direction — that's the original mouse-down point:
            // dragging down keeps the top (SelectionStart), dragging up keeps the bottom.
            _dragScrollAnchor = pos.Y > bottom
                ? Editor.SelectionStart
                : Editor.SelectionStart + Editor.SelectionLength;
        }

        _dragScrollMouseX = pos.X;
        _dragScrollVelocity = pos.Y < top ? -EdgeSpeed(top - pos.Y)
                            : pos.Y > bottom ? EdgeSpeed(pos.Y - bottom)
                            : 0;

        if (_dragScrollVelocity != 0)
            EnsureDragScrollTimer();     // beyond the edge — keep scrolling on a timer
        else
        {
            StopDragScrollTimer();       // back inside — no scroll, but we still drive selection
            _dragScrollOffset = Editor.VerticalOffset;   // stay synced so a later edge-cross resumes here
        }

        // Extend selection to the cursor (clamped into the viewport for the hit-test).
        ExtendDragSelection(
            Math.Clamp(pos.X, Editor.Padding.Left, Editor.Padding.Left + Editor.ViewportWidth - 1),
            Math.Clamp(pos.Y, top, bottom - 1));
        // While auto-scrolling, re-assert our offset after the Select() above (its synchronous
        // caret-scroll would otherwise drag the view around between timer ticks — see DragScrollTick).
        if (_dragScrollVelocity != 0)
            Editor.ScrollToVerticalOffset(_dragScrollOffset);
        e.Handled = true;                // suppress WPF's own (lurching) auto-scroll + selection
    }

    /// <summary>Pixels-per-tick for a given overshoot past the edge: a gentle floor so just-past-edge
    /// crawls, growing linearly, capped so it never races away.</summary>
    private static double EdgeSpeed(double overshootPx) =>
        Math.Min(DragScrollMaxPxPerTick, 1.5 + overshootPx * 0.14);

    private void EnsureDragScrollTimer()
    {
        if (_dragScrollTimer is not null) return;
        _dragScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(DragScrollIntervalMs),
        };
        _dragScrollTimer.Tick += (_, _) => DragScrollTick();
        _dragScrollTimer.Start();
    }

    private void StopDragScrollTimer()
    {
        _dragScrollTimer?.Stop();
        _dragScrollTimer = null;
    }

    /// <summary>End of the drag: stop scrolling and hand control back to WPF for the next gesture.</summary>
    private void EndDragScroll()
    {
        _dragScrollActive = false;
        StopDragScrollTimer();
    }

    /// <summary>
    /// While a drag-scroll is in progress, veto the TextBox's automatic "scroll the caret into view".
    /// Editor.Select() (used to grow the selection each tick) raises this request with the caret at the
    /// fixed anchor — the BOTTOM of the selection when dragging up — so honouring it would scroll the
    /// view back down, fighting our upward auto-scroll (visible as flicker, never reaching the top).
    /// We are the sole authority on the scroll position during a drag, so it's safe to suppress it
    /// entirely; normal bring-into-view resumes the moment the drag ends.
    /// </summary>
    private void Editor_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (_dragScrollActive)
            e.Handled = true;
    }

    private void DragScrollTick()
    {
        // The button may have been released past the edge without another mouse-move — stop then.
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            EndDragScroll();
            return;
        }

        // Advance OUR authoritative offset (never read back Editor.VerticalOffset — Select() below
        // pollutes it by scrolling the caret into view).
        double max = Math.Max(0, Editor.ExtentHeight - Editor.ViewportHeight);
        _dragScrollOffset = Math.Clamp(_dragScrollOffset + _dragScrollVelocity, 0, max);

        // Scroll to our target FIRST so the hit-test below sees the freshly scrolled text...
        Editor.ScrollToVerticalOffset(_dragScrollOffset);

        // ...extend the selection to the character under a point pinned just inside the edge we're
        // scrolling toward (at the cursor's X). As the text scrolls under that fixed point, the
        // covered character advances, growing the selection smoothly.
        double edgeY = _dragScrollVelocity > 0
            ? Editor.Padding.Top + Editor.ViewportHeight - 1
            : Editor.Padding.Top + 1;
        double x = Math.Clamp(_dragScrollMouseX,
                              Editor.Padding.Left,
                              Editor.Padding.Left + Editor.ViewportWidth - 1);
        ExtendDragSelection(x, edgeY);

        // ...then RE-ASSERT our offset. ExtendDragSelection's Editor.Select() synchronously scrolls the
        // caret into view (via IScrollInfo.MakeVisible, not the RequestBringIntoView event — so it can't
        // be vetoed there). When dragging up the caret is pinned at the bottom anchor, so that scroll
        // jumps the view back down; being the LAST write before the frame renders, it would otherwise
        // win and the view would never move up. Writing our offset last makes ours win, and because the
        // intermediate caret-scroll is never painted there's no flicker.
        Editor.ScrollToVerticalOffset(_dragScrollOffset);
    }

    /// <summary>Select from the fixed anchor to the character under the given Editor-space point.</summary>
    private void ExtendDragSelection(double x, double y)
    {
        int idx = Editor.GetCharacterIndexFromPoint(new Point(x, y), snapToText: true);
        if (idx < 0) return;
        int start = Math.Min(_dragScrollAnchor, idx);
        int len = Math.Abs(idx - _dragScrollAnchor);
        // Editor.Select() puts the caret at start+len and synchronously scrolls it into view. Callers
        // that are auto-scrolling (DragScrollTick / PreviewMouseMove) re-assert our own offset right
        // after this so that caret-scroll can't hijack the view — see the note in DragScrollTick.
        Editor.Select(start, len);
    }

    /// <summary>Logical (newline-based) line number of a character index.</summary>
    private static int LineOf(string text, int index)
    {
        int line = 0;
        int end = Math.Clamp(index, 0, text.Length);
        for (int i = 0; i < end; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>One row in the results list: preview text plus the target character range.</summary>
    public sealed class SearchResultVM
    {
        public string Preview { get; init; } = "";
        public string Location { get; init; } = "";
        public int Start { get; init; }
        public int Length { get; init; }
    }

    /// <summary>One editable term in proximity mode's item list.</summary>
    public sealed class SearchTermVM : INotifyPropertyChanged
    {
        private string _text = "";
        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
