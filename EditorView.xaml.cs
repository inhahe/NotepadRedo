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

    private string _currentText = "";      // materialised text of _tree.Current
    private string? _currentPath;
    private string _savedText = "";        // text as last saved/opened, for the dirty flag

    private const int DebounceMs = 500;

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
        var node = _tree.Commit(_currentText, Editor.Text, Editor.CaretIndex);
        if (node is not null)
        {
            _currentText = Editor.Text;
            SetCurrent(node);
            RaiseAll();
        }
    }

    private void NavigateTo(UndoNode target)
    {
        string text = UndoTree.Reconstruct(_tree.Current, _currentText, target);
        _tree.SetCurrent(target);
        _currentText = text;
        ApplyNode(target, text);
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
        foreach (var n in _tree.AllNodes())
            n.IsCurrent = false;
        node.IsCurrent = true;

        var p = node.Parent;
        while (p is not null) { p.IsExpanded = true; p = p.Parent; }

        _suppressTreeSelection = true;
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
        var child = _tree.NewestChild();
        if (child is not null)
            NavigateTo(child);
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSelection)
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

    public void ApplyTreeVisible(bool show)
    {
        TreePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TreeColumn.Width = show ? new GridLength(340) : new GridLength(0);
    }

    private void PreviewSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int len = (int)e.NewValue;
        UndoNode.PreviewLength = len;
        if (PreviewLenLabel is not null)
            PreviewLenLabel.Text = len.ToString();
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

    private void WriteRecovery()
    {
        try
        {
            if (!IsDirty) { DeleteRecovery(); return; }
            if (Editor.Text == _lastRecoveryText) return;

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

    /// <summary>Stop timers and clear the recovery file — called when the tab is closed cleanly.</summary>
    public void Dispose()
    {
        _debounce.Stop();
        _autosave.Stop();
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
