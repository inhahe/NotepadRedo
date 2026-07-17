using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TreeNotepad;

public partial class MainWindow : Window
{
    private UndoTree _tree = new();
    private readonly ObservableCollection<UndoNode> _roots = new();
    private readonly DispatcherTimer _debounce;

    private bool _suppressTextChange;      // ignore programmatic edits
    private bool _suppressTreeSelection;   // ignore programmatic tree selection

    private string _currentText = "";      // materialised text of _tree.Current
    private string? _currentPath;
    private string _savedText = "";        // text as last saved/opened, for the dirty flag

    private const int DebounceMs = 500;

    // ----- Autosave / crash recovery -----
    private readonly DispatcherTimer _autosave = new();
    private int _autosaveSeconds = 30;             // default period; 0 = off
    private string _lastRecoveryText = "";          // last text flushed to the recovery file
    private DateTime? _lastAutosave;                // timestamp of the last recovery flush

    /// <summary>Snapshot written periodically so unsaved work survives a crash. Never the real save file.</summary>
    private static readonly string RecoveryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TreeNotepad");
    private static readonly string RecoveryPath = Path.Combine(RecoveryDir, "recovery.json");

    private sealed record RecoveryData(string? Path, string SavedText, string Text, DateTime SavedAt);

    public MainWindow()
    {
        InitializeComponent();

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounce.Tick += Debounce_Tick;

        _autosave.Tick += (_, _) => WriteRecovery();

        _roots.Add(_tree.Root);
        Tree.ItemsSource = _roots;

        SetUpKeyBindings();
        ApplyAutosaveInterval(_autosaveSeconds);

        Loaded += (_, _) =>
        {
            TryRecover();
            Editor.Focus();
            UpdateStatus();
            UpdateTitle();
        };
    }

    private void SetUpKeyBindings()
    {
        void Bind(Key key, ModifierKeys mods, Action action) =>
            InputBindings.Add(new KeyBinding(new RelayCommand(action),
                new KeyGesture(key, mods)));

        Bind(Key.Z, ModifierKeys.Control, DoUndo);
        Bind(Key.Y, ModifierKeys.Control, DoRedo);
        Bind(Key.N, ModifierKeys.Control, DoNew);
        Bind(Key.O, ModifierKeys.Control, DoOpen);
        Bind(Key.S, ModifierKeys.Control, () => DoSave(false));
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Shift, () => DoSave(true));
    }

    // ===================== Editing / commits =====================

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChange)
            return;
        _debounce.Stop();
        _debounce.Start();
        UpdateTitle();
        UpdateStatus();
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
            UpdateStatus();
        }
    }

    /// <summary>Reconstruct a node's text and move the editor to it.</summary>
    private void NavigateTo(UndoNode target)
    {
        string text = UndoTree.Reconstruct(_tree.Current, _currentText, target);
        _tree.SetCurrent(target);
        _currentText = text;
        ApplyNode(target, text);
    }

    /// <summary>Push a reconstructed snapshot back into the editor.</summary>
    private void ApplyNode(UndoNode node, string text)
    {
        _suppressTextChange = true;
        Editor.Text = text;
        Editor.CaretIndex = Math.Clamp(node.CaretIndex, 0, text.Length);
        _suppressTextChange = false;

        SetCurrent(node);
        UpdateTitle();
        UpdateStatus();
        Editor.Focus();
    }

    /// <summary>Highlight, expand to and select the given node in the tree view.</summary>
    private void SetCurrent(UndoNode node)
    {
        foreach (var n in _tree.AllNodes())
            n.IsCurrent = false;
        node.IsCurrent = true;

        // Make sure the path from root is expanded so the node is visible.
        var p = node.Parent;
        while (p is not null) { p.IsExpanded = true; p = p.Parent; }

        _suppressTreeSelection = true;
        node.IsSelected = true;
        _suppressTreeSelection = false;
    }

    // ===================== Undo / Redo =====================

    private void DoUndo()
    {
        CommitPending();
        var parent = _tree.Current.Parent;
        if (parent is not null)
            NavigateTo(parent);
    }

    private void DoRedo()
    {
        CommitPending();
        var child = _tree.NewestChild();
        if (child is not null)
            NavigateTo(child);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => DoUndo();
    private void Redo_Click(object sender, RoutedEventArgs e) => DoRedo();

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

    // ===================== File =====================

    private void New_Click(object sender, RoutedEventArgs e) => DoNew();
    private void Open_Click(object sender, RoutedEventArgs e) => DoOpen();
    private void Save_Click(object sender, RoutedEventArgs e) => DoSave(false);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => DoSave(true);
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void DoNew()
    {
        if (!ConfirmDiscardIfDirty())
            return;
        _currentPath = null;
        _savedText = "";
        DeleteRecovery();
        ResetTree("");
    }

    private void DoOpen()
    {
        if (!ConfirmDiscardIfDirty())
            return;
        var dlg = new OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                var text = File.ReadAllText(dlg.FileName);
                _currentPath = dlg.FileName;
                _savedText = text;
                DeleteRecovery();
                ResetTree(text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private bool DoSave(bool saveAs)
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
            if (dlg.ShowDialog(this) != true)
                return false;
            path = dlg.FileName;
        }

        try
        {
            File.WriteAllText(path, Editor.Text);
            _currentPath = path;
            _savedText = Editor.Text;
            _lastAutosave = null;
            DeleteRecovery();          // clean on disk now — nothing to recover
            UpdateTitle();
            UpdateStatus();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Start a fresh history tree seeded with the given text.</summary>
    private void ResetTree(string text)
    {
        _tree = new UndoTree(text);
        _currentText = text;
        _roots.Clear();
        _roots.Add(_tree.Root);

        _suppressTextChange = true;
        Editor.Text = text;
        Editor.CaretIndex = 0;
        _suppressTextChange = false;

        _lastAutosave = null;
        _lastRecoveryText = "";

        SetCurrent(_tree.Root);
        UpdateTitle();
        UpdateStatus();
        Editor.Focus();
    }

    private bool ConfirmDiscardIfDirty()
    {
        if (Editor.Text == _savedText)
            return true;
        var result = MessageBox.Show(this,
            "You have unsaved changes. Save before continuing?",
            "TreeNotepad",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return result switch
        {
            MessageBoxResult.Yes => DoSave(false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardIfDirty())
        {
            e.Cancel = true;
        }
        else
        {
            // Clean shutdown: no crash to recover from.
            _autosave.Stop();
            DeleteRecovery();
        }
        base.OnClosing(e);
    }

    // ===================== Autosave / crash recovery =====================

    private void AutosaveInterval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string tag ||
            !int.TryParse(tag, out int seconds))
            return;

        foreach (var item in new[] { AutoOff, Auto15, Auto30, Auto60, Auto300 })
            item.IsChecked = ReferenceEquals(item, mi);

        ApplyAutosaveInterval(seconds);
    }

    /// <summary>Restart the recovery timer at the given period (0 disables it).</summary>
    private void ApplyAutosaveInterval(int seconds)
    {
        _autosaveSeconds = seconds;
        _autosave.Stop();
        if (seconds > 0)
        {
            _autosave.Interval = TimeSpan.FromSeconds(seconds);
            _autosave.Start();
        }
    }

    /// <summary>Flush the current editor text to the recovery file (only while dirty).</summary>
    private void WriteRecovery()
    {
        try
        {
            if (Editor.Text == _savedText)   // nothing unsaved to protect
            {
                DeleteRecovery();
                return;
            }
            if (Editor.Text == _lastRecoveryText)   // unchanged since last flush
                return;

            Directory.CreateDirectory(RecoveryDir);
            var data = new RecoveryData(_currentPath, _savedText, Editor.Text, DateTime.Now);
            File.WriteAllText(RecoveryPath, JsonSerializer.Serialize(data));
            _lastRecoveryText = Editor.Text;
            _lastAutosave = DateTime.Now;
            UpdateSaveStatus();
        }
        catch
        {
            // Autosave is best-effort; never interrupt the user with its failures.
        }
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

    /// <summary>On startup, offer to restore unsaved work left behind by a previous crash.</summary>
    private void TryRecover()
    {
        RecoveryData? data = null;
        try
        {
            if (File.Exists(RecoveryPath))
                data = JsonSerializer.Deserialize<RecoveryData>(File.ReadAllText(RecoveryPath));
        }
        catch { data = null; }

        if (data is null || string.IsNullOrEmpty(data.Text))
        {
            DeleteRecovery();
            return;
        }

        string where = string.IsNullOrEmpty(data.Path) ? "an untitled document" : Path.GetFileName(data.Path);
        var result = MessageBox.Show(this,
            $"Unsaved work for {where} was found from a previous session " +
            $"(autosaved {data.SavedAt:g}).\n\nRecover it?",
            "TreeNotepad \u2014 Recover unsaved work",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _currentPath = data.Path;
            _savedText = data.SavedText;   // restore the correct dirty state
            ResetTree(data.Text);
        }
        else
        {
            DeleteRecovery();
        }
    }

    // ===================== View =====================

    private void WordWrap_Click(object sender, RoutedEventArgs e) =>
        Editor.TextWrapping = WordWrapItem.IsChecked ? TextWrapping.Wrap : TextWrapping.NoWrap;

    private void ShowTree_Click(object sender, RoutedEventArgs e)
    {
        bool show = ShowTreeItem.IsChecked;
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
        if (_tree is not null)
            foreach (var n in _tree.AllNodes())
                n.RaisePreviewChanged();
    }

    // ===================== Status / title =====================

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e) => UpdateStatus();

    private void UpdateStatus()
    {
        int caret = Editor.CaretIndex;
        int line = Editor.GetLineIndexFromCharacterIndex(caret);
        int lineStart = Editor.GetCharacterIndexFromLineIndex(line);
        int col = caret - lineStart;
        CaretStatus.Text = $"Ln {line + 1}, Col {col + 1}";
        CountStatus.Text = $"{Editor.Text.Length} chars";
        NodeStatus.Text = $"node #{_tree.Current.Id}";
        UpdateSaveStatus();
    }

    /// <summary>Reflect the dirty flag (and last autosave time) in the status bar.</summary>
    private void UpdateSaveStatus()
    {
        if (SaveStatus is null)
            return;
        bool dirty = Editor.Text != _savedText;
        if (!dirty)
        {
            SaveStatus.Text = "Saved";
            SaveStatus.Foreground = Brushes.ForestGreen;
        }
        else
        {
            SaveStatus.Text = _lastAutosave is DateTime t
                ? $"Not saved \u00b7 autosaved {t:HH:mm:ss}"
                : "Not saved";
            SaveStatus.Foreground = Brushes.Firebrick;
        }
    }

    private void UpdateTitle()
    {
        string name = string.IsNullOrEmpty(_currentPath) ? "Untitled" : Path.GetFileName(_currentPath);
        bool dirty = Editor.Text != _savedText;
        Title = $"{(dirty ? "*" : "")}{name} - TreeNotepad";
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
