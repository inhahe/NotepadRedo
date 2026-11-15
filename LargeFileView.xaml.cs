using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NotepadRedo.LargeText;

namespace NotepadRedo;

/// <summary>
/// A document too large for <see cref="EditorView"/>, opened the way a log viewer opens one: the
/// lines are indexed in the background and read from disk as they come into view, so the first
/// screen shows at once and memory stays a small fraction of the file. It can still be edited
/// (<see cref="LargeDocument"/>), searched and saved. What it leaves out is what needs the whole text
/// in memory: the history tree, word wrap, proximity search and the merge viewer. See "Large files"
/// in design.md.
/// </summary>
public partial class LargeFileView : UserControl, IDocumentView
{
    /// <summary>Most matches the results list holds; the search says so when it stops there.</summary>
    private const int MaxResults = 1_000_000;

    private static readonly string RecoveryDir = Path.Combine(AppPaths.DataDir, "recovery", "large");

    private LargeDocument _doc = null!;
    private string _path = "";
    private LineEndingStyle _lineEnding = LineEndingStyle.Crlf;
    private LineEndingStyle _savedLineEnding = LineEndingStyle.Crlf;
    /// <summary>Set by Format → Line endings before indexing has found the file's own style.</summary>
    private bool _lineEndingChosen;
    /// <summary>What the file on disk was when this document last matched it.</summary>
    private FileStamp _diskStamp;
    /// <summary>The file changed on disk while the document had edits; saving asks first.</summary>
    private bool _changedOnDisk;
    private bool _missing;
    private bool _disposed;
    private string? _notice;

    private readonly DispatcherTimer _indexTimer;
    private readonly DispatcherTimer _autosave = new();
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _refreshSearch;
    private System.Threading.Timer? _poll;
    private int _polling;
    /// <summary>Bumped by every save and reload, so a check of the file begun before one is dropped.</summary>
    private int _fileGeneration;
    private int _busy;

    private CancellationTokenSource? _searchCts;
    private readonly ResultsSource _results;
    /// <summary>Document version the results were found in, or -1 while a search is running.</summary>
    private long _resultsVersion = -1;
    private long _lastRecoveryVersion = -1;
    private DateTime? _lastAutosave;

    public LargeFileView()
    {
        InitializeComponent();
        _results = new ResultsSource(this);
        ResultsList.ItemsSource = _results;

        _indexTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _indexTimer.Tick += (_, _) => IndexTick();
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); RunSearch(); };
        _refreshSearch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _refreshSearch.Tick += (_, _) => { _refreshSearch.Stop(); if (IsSearchOpen) RunSearch(); };
        _autosave.Tick += (_, _) => WriteRecovery(sync: false);

        Area.CaretMoved += (_, _) => RaiseStatus();
        Area.ScrollChanged += (_, _) => SyncScrollBars();
        Area.BeforeEdit += Area_BeforeEdit;
        Area.Edited += (_, _) => OnEdited();
        Area.EditRefused += (_, _) => ShowNotice(_doc.Source.IndexError is not null
            ? "The file couldn't be read to the end, so it can't be edited."
            : "Still reading the file; it can be edited once every line is indexed.");

        // The Edit menu's Cut, Copy and Paste are routed commands aimed at the focused element.
        Area.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => Area.Copy(), (_, e) => e.CanExecute = Area.HasSelection));
        Area.CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, (_, _) => Area.Cut(), (_, e) => e.CanExecute = Area.HasSelection));
        Area.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, (_, _) => Area.Paste(), (_, e) => e.CanExecute = true));
        Area.CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll, (_, _) => Area.SelectAll(), (_, e) => e.CanExecute = true));

        PreviewKeyDown += View_PreviewKeyDown;

        ApplyFont(AppSettings.Current.FontFamily, AppSettings.Current.FontSize,
                  AppSettings.Current.FontBold, AppSettings.Current.FontItalic);
        ApplyAutosaveInterval(AppSettings.Current.AutosaveSeconds);
    }

    /// <summary>Whether a file is large enough to open here rather than in the full editor.</summary>
    public static bool ShouldOpen(string path)
    {
        int mb = AppSettings.Current.LargeFileThresholdMB;
        if (mb <= 0)
            return false;
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists && fi.Length >= mb * 1024L * 1024L;
        }
        catch { return false; }
    }

    /// <summary>Open a file in this (new) view.</summary>
    public void LoadFile(string path)
    {
        _path = Path.GetFullPath(path);
        var src = OpenSource(_path);
        src.WaitForFirstLines(500, TimeSpan.FromMilliseconds(200));
        Attach(new LargeDocument(src));
        _diskStamp = src.Stamp;
        StartWatching();
    }

    /// <summary>Open a file, locked when Options → Lock open files asks for it. A file another program
    /// has open for writing can't be locked; it opens unlocked, as the full editor does, and an
    /// edit then works on a private copy (see <see cref="Area_BeforeEdit"/>).</summary>
    private static SourceFile OpenSource(string path)
    {
        if (AppSettings.Current.LockFileWhileOpen)
        {
            try { return SourceFile.Open(path, lockFile: true); }
            catch (IOException) when (File.Exists(path)) { }
        }
        return SourceFile.Open(path, lockFile: false);
    }

    private void Attach(LargeDocument doc)
    {
        _doc = doc;
        Area.Document = doc;
        Area.IsReadOnly = true;
        _indexTimer.Start();
        IndexTick();
    }

    // ===================== Indexing =====================

    private void IndexTick()
    {
        if (_disposed)
            return;
        var src = _doc.Source;
        bool complete = src.Complete;   // read first: lines indexed before it are all in the count
        if (_doc.SyncWithSource())
        {
            Area.Refresh();
            if (FollowCheck.IsChecked == true)
                FollowEnd();
        }
        if (complete)
        {
            _indexTimer.Stop();
            OnIndexComplete();
        }
        RaiseStatus();
        UpdateBanner();
    }

    private void OnIndexComplete()
    {
        var src = _doc.Source;
        if (src.IndexError is not null)
        {
            ShowNotice($"Reading stopped at byte {src.IndexedLength:N0}: {src.IndexError.Message}");
            return;
        }
        try { src.RememberTail(); } catch { /* only costs a full re-read when lines are appended */ }
        if (!_doc.HasEdits)
        {
            bool pending = _lineEndingChosen && _lineEnding != _savedLineEnding;
            _savedLineEnding = src.DetectedLineEnding;
            if (!pending)
                _lineEnding = _savedLineEnding;
        }
        Area.IsReadOnly = false;
        if (_pendingPlace is { } place)
        {
            _pendingPlace = null;
            Area.Select(place.Caret, place.Caret, scroll: false);
            Area.ScrollTo(place.TopLine);
        }
        if (IsSearchOpen && _resultsVersion != _doc.Version)
            RunSearch();
        RaiseAll();
    }

    private void FollowEnd()
    {
        long last = Math.Max(0, _doc.LineCount - 1);
        Area.Select(new TextPos(last, 0), new TextPos(last, 0), scroll: false);
        Area.ScrollTo(last);
    }

    private void Follow_Changed(object sender, RoutedEventArgs e)
    {
        if (FollowCheck.IsChecked == true && _doc is not null)
            FollowEnd();
    }

    // ===================== Watching the file =====================

    private void StartWatching()
    {
        _poll?.Dispose();
        _poll = null;
        if (!AppSettings.Current.WatchExternalChanges || _disposed)
            return;
        // Polled by name rather than watched: a share may send no change notifications at all, and
        // a probe by name sees a file replaced by a rename, which a handle already open never would.
        _poll = new System.Threading.Timer(_ => Poll(), null, 1000, 1000);
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;
        string path = _path;
        int generation = Volatile.Read(ref _fileGeneration);
        var probe = SourceFile.Probe(path);
        bool exists = probe is not null || File.Exists(path);
        Dispatcher.BeginInvoke(() =>
        {
            // A check that overlapped a save (or a reload) may have seen the file halfway through
            // it: the next one will tell.
            try
            {
                if (_busy == 0 && generation == _fileGeneration)
                    HandleProbe(probe, exists);
            }
            catch (Exception ex) { CrashLog.Log("Checking a large file for changes failed", ex); }
            finally { Volatile.Write(ref _polling, 0); }
        });
    }

    private void HandleProbe(FileStamp? probe, bool exists)
    {
        if (_disposed)
            return;
        bool wasMissing = _missing;
        _missing = !exists;
        if (_missing != wasMissing)
            UpdateBanner();
        if (probe is not FileStamp s || s == _diskStamp)
            return;
        var src = _doc.Source;
        if (!src.Complete)
            return;   // asked again once the index is done

        // Without unsaved edits the document follows the file: lines appended are taken in, and a
        // file rewritten is read again (which forgets the undo history; it holds nothing unsaved).
        if (!IsDirty && src.CopyPath is null)
        {
            if (!_doc.HasEdits && src.SameFile(s) && s.Length >= src.IndexedLength && src.TryExtend())
            {
                _diskStamp = s;
                // No edits until the new lines are in: an edit's lock or copy needs a finished index.
                Area.IsReadOnly = !src.Complete;
                _indexTimer.Start();
                return;
            }
            Reload();
            return;
        }
        if (!_changedOnDisk)
        {
            _changedOnDisk = true;
            UpdateBanner();
        }
    }

    /// <summary>Read the file again from scratch (it was rewritten while the document had no edits).</summary>
    private void Reload()
    {
        SourceFile fresh;
        try { fresh = OpenSource(_path); }
        catch (Exception ex)
        {
            ShowNotice("The file changed on disk but couldn't be read again: " + ex.Message);
            return;
        }
        long top = Area.TopLine;
        var caret = Area.Caret;
        Interlocked.Increment(ref _fileGeneration);
        _doc.ReplaceSource(fresh);
        _diskStamp = fresh.Stamp;
        _changedOnDisk = false;
        Area.IsReadOnly = true;
        Area.Refresh();
        Area.Select(caret, caret, scroll: false);
        Area.ScrollTo(top);
        _indexTimer.Start();
        ShowNotice($"Reloaded at {DateTime.Now:HH:mm:ss}: another program rewrote the file.");
        ClearResults();
        if (IsSearchOpen)
            _refreshSearch.Start();
    }

    public void ApplyWatchSetting() => StartWatching();

    public void ApplyLockSetting()
    {
        var src = _doc.Source;
        if (AppSettings.Current.LockFileWhileOpen)
        {
            if (src.Complete && src.TryLock() == LockResult.Changed)
                Reload();
        }
        else if (!_doc.HasEdits)
            src.Unlock();   // while edits refer to its lines, the lock is what keeps them valid
    }

    // ===================== Editing =====================

    /// <summary>
    /// Before the first edit, make sure the lines it refers to can't change: lock the file against
    /// other writers, or, when a program already has it open for writing (a log in progress), read
    /// from a private copy from now on.
    /// </summary>
    private void Area_BeforeEdit(object? sender, CancelEventArgs e)
    {
        if (!EnsureProtected(_doc.Source))
            e.Cancel = true;
    }

    /// <summary>
    /// Make sure the lines of <paramref name="src"/> can't change while the document refers to
    /// them: lock it, or read from a private copy when another program is writing to it. False when
    /// neither could be done, or the file had already changed, in which case (with no unsaved work)
    /// it is read again and the caller's edit is dropped.
    /// </summary>
    private bool EnsureProtected(SourceFile src)
    {
        switch (src.TryLock())
        {
            case LockResult.Locked:
                return true;
            case LockResult.Changed:
                // What is on screen is no longer the file: editing it would mix the two.
                if (!IsDirty && ReferenceEquals(src, _doc.Source))
                {
                    Reload();
                    ThemedDialog.Show(Window.GetWindow(this),
                        $"Another program changed {Path.GetFileName(_path)}, so it has been read again. Make the edit again.",
                        "NotepadRedo", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                    ShowNotice("That file was changed by another program; the edit can't refer to it.");
                return false;
        }
        Directory.CreateDirectory(RecoveryDir);
        string copy = Path.Combine(RecoveryDir, RecoveryId + "." + Guid.NewGuid().ToString("N")[..8] + ".src");
        bool ok;
        try
        {
            ok = Busy(() => ProgressDialog.Run(Window.GetWindow(this), "Copying the file",
                $"Another program has {Path.GetFileName(src.Path)} open for writing. Before it is edited, " +
                "NotepadRedo makes a private copy of it, so the lines you edit can't change underneath you.",
                (p, ct) => src.SwitchToCopy(copy, p, ct), cancellable: true));
        }
        catch (Exception ex)
        {
            ThemedDialog.Show(Window.GetWindow(this), "The file couldn't be copied, so it can't be edited: " + ex.Message,
                "NotepadRedo", MessageBoxButton.OK, MessageBoxImage.Error);
            ok = false;
        }
        if (!ok)
        {
            TryDelete(copy);
            return false;
        }
        if (FollowCheck.IsChecked == true)
            FollowCheck.IsChecked = false;
        ShowNotice("Editing a private copy: another program is still writing to the file.");
        return true;
    }

    /// <summary>
    /// Undo or redo, then protect every source the document now refers to: an undo past a save
    /// brings back lines of a file that wasn't protected (opened again unlocked after the save, or
    /// left behind by Save As). If one can't be protected, the move is taken back.
    /// </summary>
    private void MoveInHistory(bool undo)
    {
        if (Area.IsReadOnly || !EnsureProtected(_doc.Source))
            return;
        if ((undo ? _doc.Undo() : _doc.Redo()) is not { } sel)
            return;
        foreach (var src in _doc.CurrentSources())
            if (!EnsureProtected(src))
            {
                sel = (undo ? _doc.Redo() : _doc.Undo()) ?? sel;
                break;
            }
        Area.AfterHistoryMove(sel);
        FocusEditor();
    }

    private void OnEdited()
    {
        RaiseAll();
        if (IsSearchOpen)
        {
            ClearResults(keepStatus: true);
            _refreshSearch.Stop();
            _refreshSearch.Start();
        }
    }

    public void Undo() => MoveInHistory(undo: true);

    public void Redo() => MoveInHistory(undo: false);

    // ===================== Saving =====================

    public bool Save(bool saveAs, bool interactive = true)
    {
        var owner = Window.GetWindow(this);
        if (!_doc.Source.Complete || _doc.Source.IndexError is not null)
        {
            if (interactive)
                ThemedDialog.Show(owner, _doc.Source.IndexError is null
                        ? "The file is still being read. Save once every line is indexed."
                        : "The file couldn't be read to the end, so saving it would lose the rest.",
                    "Save", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        string path = _path;
        if (saveAs)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = Path.GetFileName(_path),
                InitialDirectory = Path.GetDirectoryName(_path),
            };
            if (dlg.ShowDialog(owner) != true)
                return false;
            path = Path.GetFullPath(dlg.FileName);
        }
        bool samePath = string.Equals(path, _path, StringComparison.OrdinalIgnoreCase);

        if (samePath && AppSettings.Current.WatchExternalChanges && !ConfirmOverwrite(owner, interactive))
            return false;

        return Busy(() => WriteAndAdopt(path, samePath, owner, interactive));
    }

    /// <summary>
    /// Run a job that reads or replaces the source while a progress window may pump messages (a
    /// save, the private copy, Replace All). Checks of the file are held off meanwhile, and one begun
    /// before is dropped: it could extend or reload the very source the job is using, or have seen
    /// the file halfway through being replaced.
    /// </summary>
    private T Busy<T>(Func<T> job)
    {
        _busy++;
        Interlocked.Increment(ref _fileGeneration);
        try { return job(); }
        finally
        {
            _busy--;
            Interlocked.Increment(ref _fileGeneration);
        }
    }

    /// <summary>The part of <see cref="Save"/> that writes: to a temporary file beside the target,
    /// which then replaces it, after which the document reads from the file written.</summary>
    private bool WriteAndAdopt(string path, bool samePath, Window? owner, bool interactive)
    {
        string dir = Path.GetDirectoryName(path)!;
        string temp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        var starts = new LongList();
        var joined = new LongList();
        long length = 0;
        bool convert = _lineEnding != _savedLineEnding;
        try
        {
            bool done;
            if (interactive)
                done = ProgressDialog.Run(owner, "Saving", $"Saving {Path.GetFileName(path)}…",
                    (p, ct) => length = WriteTemp(temp, convert, starts, joined, p, ct), cancellable: true);
            else
            {
                length = WriteTemp(temp, convert, starts, joined, null, CancellationToken.None);
                done = true;
            }
            if (!done)
            {
                TryDelete(temp);
                return false;
            }
            ReplaceFile(temp, path);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            if (interactive)
                ThemedDialog.Show(owner, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                CrashLog.Log("non-interactive save of a large file failed", ex);
            return false;
        }

        // The file now holds exactly the document: read unchanged lines from it from here on.
        SourceFile saved;
        try
        {
            saved = SourceFile.OpenIndexed(path, AppSettings.Current.LockFileWhileOpen, _doc.Source.Codec,
                starts, joined, length, _lineEnding);
        }
        catch (Exception ex)
        {
            // Written, but it can't be opened again: stay on the old source, which still reads.
            if (interactive)
                ThemedDialog.Show(owner, "The file was saved, but couldn't be opened again: " + ex.Message,
                    "NotepadRedo", MessageBoxButton.OK, MessageBoxImage.Warning);
            _doc.MarkSaved();
            _path = path;
            RaiseAll();
            return true;
        }
        try { saved.RememberTail(); } catch { }
        _doc.AdoptSaved(saved);
        // The files kept only for undo: one this save replaced reads the old file, which nothing
        // can write to any more; one left at another path (Save As) needn't stay locked against
        // other programs, and is protected again if undo brings its lines back.
        foreach (var older in _doc.OlderSources())
        {
            if (older.CopyPath is null && string.Equals(older.Path, path, StringComparison.OrdinalIgnoreCase))
                older.Replaced = true;
            else
                older.Unlock();
        }
        _path = path;
        _savedLineEnding = _lineEnding;
        _diskStamp = saved.Stamp;
        _changedOnDisk = false;
        _missing = false;
        _lastAutosave = null;
        DeleteRecovery();
        if (!samePath)
            StartWatching();
        Area.Refresh();
        RaiseAll();
        return true;
    }

    private long WriteTemp(string temp, bool convert, LongList starts, LongList joined, IProgress<double>? p, CancellationToken ct)
    {
        using var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        long n = _doc.WriteTo(fs, _lineEnding, convert, starts, joined, p, ct);
        fs.Flush(flushToDisk: true);
        return n;
    }

    /// <summary>Put the written file in place of the old one. Replacing keeps the old file's
    /// attributes and permissions; a share that can't replace gets an overwriting rename.</summary>
    private static void ReplaceFile(string temp, string path)
    {
        if (!File.Exists(path))
        {
            File.Move(temp, path);
            return;
        }
        try { File.Replace(temp, path, null, ignoreMetadataErrors: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            File.Move(temp, path, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>Before writing over the file: if another program changed it since it was last read
    /// or saved, ask (or, non-interactively, refuse).</summary>
    private bool ConfirmOverwrite(Window? owner, bool interactive)
    {
        var probe = SourceFile.Probe(_path);
        if (probe is null || (probe == _diskStamp && !_changedOnDisk))
            return true;
        if (!interactive)
            return false;
        int choice = ThemedDialog.ShowChoices(owner,
            $"“{Path.GetFileName(_path)}” has been changed by another program since it was opened. " +
            "Saving writes your version over those changes.",
            "File changed on disk",
            new[] { "Overwrite it with my version", "Don't save" },
            MessageBoxImage.Warning, defaultIndex: 1);
        return choice == 0;
    }

    public bool ConfirmDiscardIfDirty()
    {
        if (!IsDirty)
            return true;
        var result = ThemedDialog.Show(Window.GetWindow(this),
            $"Save changes to {Path.GetFileName(_path)}?", "NotepadRedo",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return result switch
        {
            MessageBoxResult.Yes => Save(false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    public EditorView.SavePromptResult ConfirmDiscardForQuit()
    {
        if (!IsDirty)
            return EditorView.SavePromptResult.Handled;
        int choice = ThemedDialog.ShowSaveAll(Window.GetWindow(this),
            $"Save changes to {Path.GetFileName(_path)}?", "NotepadRedo");
        return choice switch
        {
            0 => Save(false) ? EditorView.SavePromptResult.Handled : EditorView.SavePromptResult.Cancel,
            1 => Save(false) ? EditorView.SavePromptResult.SaveRemaining : EditorView.SavePromptResult.Cancel,
            2 => EditorView.SavePromptResult.Handled,
            _ => EditorView.SavePromptResult.Cancel,
        };
    }

    // ===================== Shell surface =====================

    public string? FilePath => _path;
    public bool IsDirty => _doc is not null && (_doc.IsModified || _lineEnding != _savedLineEnding);
    public string TabTitle => _path + (IsDirty ? " *" : "");
    public string RecoveryId { get; private set; } = Guid.NewGuid().ToString("N");

    public string CaretText
    {
        get
        {
            if (_doc is null) return "";
            var c = Area.Caret;
            long logical = _doc.LogicalLine(c.Line);
            // A segment of a cut line counts its columns from the start of the whole line.
            long col = c.Col;
            for (long l = c.Line - 1; l >= 0 && _doc.IsJoined(l); l--)
                col += _doc.LineLength(l);
            string s = $"Ln {logical + 1:N0}, Col {col + 1:N0}";
            if (Area.HasSelection)
                s += SelectionSummary();
            return s;
        }
    }

    private string SelectionSummary()
    {
        var a = Area.SelectionStart;
        var b = Area.SelectionEnd;
        if (a.Line == b.Line)
            return $" ({b.Col - a.Col:N0} selected)";
        return $" ({b.Line - a.Line + (b.Col > 0 ? 1 : 0):N0} lines selected)";
    }

    public string CountText
    {
        get
        {
            if (_doc is null) return "";
            var src = _doc.Source;
            string lines = $"{_doc.LogicalLineCount:N0} lines";
            if (!src.Complete)
            {
                long total = Math.Max(1, src.Stamp.Length);
                return $"{lines} so far · indexing {100.0 * src.IndexedLength / total:F0}%";
            }
            return $"{lines} · {FormatSize(src.IndexedLength)}";
        }
    }

    public string NodeText => "Large file";

    public string LineEndingText => LineEndings.Label(_lineEnding);

    public string LineEndingTooltip
    {
        get
        {
            string s = $"New lines are saved with {LineEndings.LongLabel(_lineEnding)}.";
            if (_lineEnding != _savedLineEnding)
                s += $" The file uses {LineEndings.Label(_savedLineEnding)}; saving converts every line.";
            else if (_doc?.Source.MixedLineEndings == true)
                s += " The file has mixed line endings; lines you don't edit keep their own.";
            return s + " Change it under Format → Line endings.";
        }
    }

    public LineEndingStyle LineEnding => _lineEnding;

    public void SetLineEnding(LineEndingStyle style)
    {
        if (_lineEnding == style)
            return;
        _lineEnding = style;
        _lineEndingChosen = true;
        RaiseAll();
    }

    public string SaveText => IsDirty
        ? (_lastAutosave is DateTime t ? $"Not saved · autosaved {t:HH:mm:ss}" : "Not saved")
        : "Saved";

    public event EventHandler? StatusChanged;
    public event EventHandler? TitleChanged;
    public event EventHandler? SidePaneVisibilityChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _lastDirty;

    private void RaiseStatus() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseAll()
    {
        RaiseStatus();
        bool dirty = IsDirty;
        if (dirty != _lastDirty)
        {
            _lastDirty = dirty;
            TitleChanged?.Invoke(this, EventArgs.Empty);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabTitle)));
        UpdateBanner();
    }

    private void ShowNotice(string text)
    {
        _notice = text;
        UpdateBanner();
    }

    private void UpdateBanner()
    {
        if (_doc is null)
            return;
        var src = _doc.Source;
        string s = $"Large-file mode · {src.Codec.Name}";
        if (src.CopyPath is not null)
            s += " · editing a private copy";
        else if (src.Locked)
            s += " · locked against other writers";
        if (_missing)
            s += " · the file has been deleted or moved; saving writes it again";
        else if (_changedOnDisk)
            s += " · changed on disk by another program; saving will ask before overwriting";
        if (_notice is not null)
            s += " · " + _notice;
        BannerText.Text = s;
        BannerText.ToolTip = "This file is too large for the full editor, so it is read from disk as you scroll " +
            "instead of being loaded. It can be edited, searched and saved; the history tree, word wrap and " +
            "proximity search are not available for it. The size above which files open this way is under " +
            "Options → Large files.";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} bytes",
    };

    public void FocusEditor()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Area.Focus();
            Keyboard.Focus(Area);
        }), DispatcherPriority.Input);
    }

    public void ApplyFont(string family, double sizePoints, bool bold, bool italic) =>
        Area.SetFont(new FontFamily(family), sizePoints * (96.0 / 72.0),
            bold ? FontWeights.Bold : FontWeights.Normal, italic ? FontStyles.Italic : FontStyles.Normal);

    // Settings that have no meaning here: the text never wraps, and there is no history tree.
    public void ApplyWordWrap(bool wrap) { }
    public void ApplyTreeVisible(bool show) { }
    public void ApplyPreviewFit(bool fit) { }
    public void ApplyHistoryMode(bool branchesOnly) { }
    public void LayOutPanes() { }

    public void ApplyAutosaveInterval(int seconds)
    {
        _autosave.Stop();
        if (seconds > 0)
        {
            _autosave.Interval = TimeSpan.FromSeconds(seconds);
            _autosave.Start();
        }
    }

    // ===================== Scrolling =====================

    private bool _syncingScroll;

    private void SyncScrollBars()
    {
        if (_doc is null)
            return;
        _syncingScroll = true;
        try
        {
            long count = Math.Max(1, _doc.LineCount);
            int rows = Area.FullRows;
            VScroll.Minimum = 0;
            VScroll.Maximum = Math.Max(0, count - rows);
            VScroll.ViewportSize = rows;
            VScroll.LargeChange = Math.Max(1, rows - 1);
            VScroll.Value = Area.TopLine;
            double width = Area.TextViewportWidth;
            double extent = Area.ExtentWidth;
            HScroll.Minimum = 0;
            HScroll.Maximum = Math.Max(0, extent - width);
            HScroll.ViewportSize = Math.Max(1, width);
            HScroll.LargeChange = Math.Max(1, width * 0.8);
            HScroll.SmallChange = 20;
            HScroll.Value = Area.HorizontalOffset;
            HScroll.Visibility = extent > width + 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _syncingScroll = false; }
    }

    private void VScroll_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
    {
        if (!_syncingScroll)
            Area.ScrollTo((long)Math.Round(VScroll.Value));
    }

    private void HScroll_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
    {
        if (!_syncingScroll)
            Area.ScrollTo(Area.TopLine, HScroll.Value);
    }

    // ===================== Keys and go-to-line =====================

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
        {
            GoToBox.Focus();
            GoToBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
        {
            // A focused text box would take Ctrl+H as Backspace before the window's binding saw it.
            OpenReplace();
            e.Handled = true;
        }
    }

    private void GoToBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            FocusEditor();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        string text = GoToBox.Text.Replace(",", "").Replace(" ", "").Replace(" ", "").Trim();
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) || n < 1)
        {
            ShowNotice("Type a line number to go to.");
            return;
        }
        long line = _doc.FromLogicalLine(Math.Min(n, _doc.LogicalLineCount) - 1);
        Area.Select(new TextPos(line, 0), new TextPos(line, 0), scroll: false);
        Area.ScrollTo(line - Area.FullRows / 3);
        FocusEditor();
    }

    // ===================== Search =====================

    public bool IsSearchOpen => SearchPanel.Visibility == Visibility.Visible;
    public bool IsReplaceOpen => IsSearchOpen && ReplaceRow.Visibility == Visibility.Visible;

    public void OpenSearch() => ShowSearch(true, replace: false);
    public void OpenReplace() => ShowSearch(true, replace: true);
    public void ToggleSearch() => ShowSearch(!(IsSearchOpen && !IsReplaceOpen), replace: false);
    public void ToggleReplace() => ShowSearch(!IsReplaceOpen, replace: true);

    private void ShowSearch(bool show, bool replace)
    {
        if (show)
        {
            bool wasOpen = IsSearchOpen;
            SearchPanel.Visibility = Visibility.Visible;
            SearchSplitter.Visibility = Visibility.Visible;
            ReplaceRow.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
            SearchRow.Height = new GridLength(Math.Max(120, AppSettings.Current.LargeSearchPaneHeight));
            // A one-line selection is what the user most likely wants to find.
            if (Area.HasSelection && Area.SelectionStart.Line == Area.SelectionEnd.Line)
            {
                string sel = _doc.GetText(Area.SelectionStart, Area.SelectionEnd);
                if (sel.Length is > 0 and < 500)
                    SearchBox.Text = sel;
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }), DispatcherPriority.Input);
            if (!wasOpen)
                RunSearch();
        }
        else
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            SearchSplitter.Visibility = Visibility.Collapsed;
            SearchRow.Height = new GridLength(0);
            _searchCts?.Cancel();
            ClearResults();
            Area.Highlighter = null;
            FocusEditor();
        }
        SidePaneVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SearchSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        double h = SearchRow.ActualHeight;
        if (h > 0)
            MainWindow.ChangeSettings(s => s.LargeSearchPaneHeight = h);
    }

    private void SearchClose_Click(object sender, RoutedEventArgs e) => ShowSearch(false, false);

    private void SearchPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ShowSearch(false, false);
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SearchOption_Changed(object sender, RoutedEventArgs e)
    {
        if (IsSearchOpen)
            RunSearch();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Enter:
                FindNext(backwards: shift, keepFocus: true);
                e.Handled = true;
                break;
            case Key.Down:
                FindNext(backwards: false, keepFocus: true);
                e.Handled = true;
                break;
            case Key.Up:
                FindNext(backwards: true, keepFocus: true);
                e.Handled = true;
                break;
        }
    }

    private void ReplaceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ReplaceNext();
            e.Handled = true;
        }
    }

    private void FindNextButton_Click(object sender, RoutedEventArgs e) => FindNext(false, keepFocus: true);
    private void FindPrevButton_Click(object sender, RoutedEventArgs e) => FindNext(true, keepFocus: true);

    private MatchOptions Options => new(CaseSensitiveCheck.IsChecked == true, WholeWordCheck.IsChecked == true,
                                        RegexCheck.IsChecked == true);

    private void ClearResults(bool keepStatus = false)
    {
        _resultsVersion = -1;
        _results.Clear();
        if (!keepStatus)
            SearchStatus.Text = "";
    }

    /// <summary>Find every match in the background, listing them as they are found.</summary>
    private void RunSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        ClearResults();
        string term = SearchBox.Text;
        var opt = Options;
        Area.Highlighter = MakeHighlighter(term, opt);
        if (string.IsNullOrEmpty(term))
            return;
        if (!_doc.Source.Complete)
        {
            SearchStatus.Text = "Waiting for the file to be indexed…";
            return;   // OnIndexComplete runs it
        }
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var view = new PieceView(_doc.Snapshot());
        long version = _doc.Version;
        SearchStatus.Text = "Searching…";
        var started = DateTime.UtcNow;
        double lastProgress = 0;
        var pending = new List<LargeMatch>();
        var flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        void Flush()
        {
            if (cts.IsCancellationRequested) return;
            List<LargeMatch> batch;
            lock (pending) { batch = pending.ToList(); pending.Clear(); }
            if (batch.Count > 0)
                _results.AddRange(batch);
            SearchStatus.Text = $"Searching… {lastProgress:P0} · {_results.Count:N0} matches so far";
        }
        flush.Tick += (_, _) => Flush();
        flush.Start();

        Task.Run(() => LargeSearch.FindAll(view, term, opt, MaxResults, (batch, progress) =>
        {
            lock (pending) pending.AddRange(batch);
            lastProgress = progress;
        }, cts.Token), cts.Token).ContinueWith(t =>
        {
            flush.Stop();
            if (cts.IsCancellationRequested || _disposed)
                return;
            Flush();
            if (t.IsFaulted)
            {
                // What was listed before the failure is not every match: nothing may step through
                // it as if it were.
                _resultsVersion = -1;
                var ex = t.Exception!.GetBaseException();
                SearchStatus.Text = ex is RegexMatchTimeoutException
                    ? "The pattern took too long on a line; simplify it."
                    : ex is ArgumentException ? "Invalid regular expression: " + ex.Message
                    : "Search failed: " + ex.Message;
                return;
            }
            _resultsVersion = version;
            double secs = (DateTime.UtcNow - started).TotalSeconds;
            string count = _results.Count == 1 ? "1 match" : $"{_results.Count:N0} matches";
            SearchStatus.Text = t.Result
                ? $"Stopped at {MaxResults:N0} matches."
                : $"{count} ({secs:F1} s)";
            SelectResultAtCaret();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Highlight in the text the result at or after the caret, without moving anything.</summary>
    private void SelectResultAtCaret()
    {
        int i = _results.IndexAtOrAfter(Area.SelectionStart);
        if (i >= 0 && _results.Get(i).Start == Area.SelectionStart && _results.Get(i).End == Area.SelectionEnd)
        {
            _selectingResult = true;
            ResultsList.SelectedIndex = i;
            _selectingResult = false;
        }
    }

    /// <summary>Finds the current term in one line, for highlighting it where it shows.</summary>
    private static Func<string, IReadOnlyList<(int, int)>>? MakeHighlighter(string term, MatchOptions opt)
    {
        if (string.IsNullOrEmpty(term))
            return null;
        Regex? re = null;
        if (opt.Regex)
        {
            try { re = SearchEngine.BuildRegex(term, opt.CaseSensitive); }
            catch (ArgumentException) { return null; }
        }
        return line =>
        {
            var list = new List<(int, int)>();
            try
            {
                if (re is not null)
                {
                    for (var m = re.Match(line); m.Success && list.Count < 1000; m = m.NextMatch())
                        if (m.Length > 0 && (!opt.WholeWord || SearchEngine.IsWholeWord(line, m.Index, m.Index + m.Length)))
                            list.Add((m.Index, m.Length));
                }
                else
                    foreach (var m in SearchEngine.Occurrences(line, term, opt))
                    {
                        list.Add((m.Start, m.Length));
                        if (list.Count >= 1000) break;
                    }
            }
            catch (RegexMatchTimeoutException) { }
            return list;
        };
    }

    private CancellationTokenSource? _findCts;

    public void FindNext(bool backwards = false, bool keepFocus = false)
    {
        string term = SearchBox.Text;
        if (string.IsNullOrEmpty(term))
        {
            OpenSearch();
            return;
        }
        var opt = Options;
        // From the caret: forwards, the first match after the start of the selection (so the match
        // selected now is passed over); backwards, the last one before it.
        var sel = Area.SelectionStart;
        TextPos from = backwards ? sel : Area.HasSelection ? new TextPos(sel.Line, sel.Col + 1) : Area.Caret;

        if (_resultsVersion == _doc.Version && _results.Count > 0 && !_results.Capped)
        {
            int i = backwards ? _results.IndexBefore(from) : _results.IndexAtOrAfter(from);
            if (i < 0)
                i = backwards ? _results.Count - 1 : 0;   // wrap round
            SelectResult(i, keepFocus);
            return;
        }
        if (_resultsVersion == _doc.Version && _results.Count == 0)
        {
            SearchStatus.Text = "No matches.";
            return;
        }

        // No complete list to step through: scan from the caret in the background.
        _findCts?.Cancel();
        var cts = new CancellationTokenSource();
        _findCts = cts;
        var view = new PieceView(_doc.Snapshot());
        long version = _doc.Version;
        ShowNotice("Searching…");
        Task.Run(() => LargeSearch.FindNext(view, term, opt, from, backwards, null, cts.Token), cts.Token)
            .ContinueWith(t =>
            {
                if (cts.IsCancellationRequested || _disposed)
                    return;
                _notice = null;
                UpdateBanner();
                if (t.IsFaulted)
                {
                    var ex = t.Exception!.GetBaseException();
                    ShowNotice(ex is ArgumentException ? "Invalid regular expression: " + ex.Message : "Search failed: " + ex.Message);
                    return;
                }
                if (t.Result is not LargeMatch m)
                {
                    ShowNotice($"“{term}” was not found.");
                    return;
                }
                if (_doc.Version != version)
                    return;
                NavigateTo(m, keepFocus);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private bool _selectingResult;

    private void SelectResult(int index, bool keepFocus)
    {
        _selectingResult = true;
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        _selectingResult = false;
        SearchStatus.Text = $"{index + 1:N0} of {_results.Count:N0}";
        NavigateTo(_results.Get(index), keepFocus);
    }

    private void NavigateTo(LargeMatch m, bool keepFocus)
    {
        Area.Select(m.Start, m.End, scroll: false);
        Area.ScrollIntoView(m.Line);
        Area.EnsureCaretVisible();
        if (!keepFocus)
            FocusEditor();
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingResult || ResultsList.SelectedIndex < 0)
            return;
        int i = ResultsList.SelectedIndex;
        SearchStatus.Text = $"{i + 1:N0} of {_results.Count:N0}";
        NavigateTo(_results.Get(i), keepFocus: true);
    }

    private void Results_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // The row under the pointer, so clicking the selected row again still goes back to it.
        var item = ItemsControl.ContainerFromElement(ResultsList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item is null)
            return;
        int i = ResultsList.ItemContainerGenerator.IndexFromContainer(item);
        if (i >= 0)
            NavigateTo(_results.Get(i), keepFocus: false);
    }

    // ===================== Replace =====================

    private void ReplaceNext_Click(object sender, RoutedEventArgs e) => ReplaceNext();

    /// <summary>Replace the selected match, if the selection is one, then go on to the next.</summary>
    private void ReplaceNext()
    {
        string find = SearchBox.Text;
        if (string.IsNullOrEmpty(find) || Area.IsReadOnly)
            return;
        var a = Area.SelectionStart;
        var b = Area.SelectionEnd;
        if (Area.HasSelection && a.Line == b.Line)
        {
            string line = _doc.GetLine(a.Line);
            List<ReplaceMatch> found;
            try { found = ReplaceEngine.Find(line, find, ReplaceBox.Text, Options, a.Col, b.Col - a.Col); }
            catch (ArgumentException ex)
            {
                SearchStatus.Text = "Invalid regular expression: " + ex.Message;
                return;
            }
            var match = found.FirstOrDefault(m => m.Start == a.Col && m.Length == b.Col - a.Col);
            if (found.Count > 0 && match.Length == b.Col - a.Col && match.Start == a.Col)
            {
                var ce = new CancelEventArgs();
                Area_BeforeEdit(this, ce);
                if (ce.Cancel)
                    return;
                var end = _doc.Replace(a, b, match.Replacement, (Area.Anchor, Area.Caret));
                Area.AfterHistoryMove((end, end));
            }
        }
        FindNext(backwards: false, keepFocus: true);
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        string find = SearchBox.Text;
        if (string.IsNullOrEmpty(find))
            return;
        if (Area.IsReadOnly)
        {
            ShowNotice("Still reading the file; Replace All works once every line is indexed.");
            return;
        }
        var ce = new CancelEventArgs();
        Area_BeforeEdit(this, ce);
        if (ce.Cancel)
            return;
        var view = new PieceView(_doc.Snapshot());
        long version = _doc.Version;
        var opt = Options;
        string replacement = ReplaceBox.Text;
        (long Start, long Count, List<Piece>? Pieces, long Replaced) result = default;
        bool done;
        try
        {
            done = Busy(() => ProgressDialog.Run(Window.GetWindow(this), "Replace All", $"Replacing “{find}”…",
                (p, ct) => result = LargeSearch.ReplaceAll(view, find, replacement, opt, p, ct), cancellable: true));
        }
        catch (ArgumentException ex)
        {
            SearchStatus.Text = "Invalid regular expression: " + ex.Message;
            return;
        }
        catch (RegexMatchTimeoutException)
        {
            SearchStatus.Text = "The pattern took too long on a line; simplify it.";
            return;
        }
        if (!done || _doc.Version != version)
            return;
        if (result.Pieces is null)
        {
            SearchStatus.Text = "Nothing to replace.";
            return;
        }
        var sel = (Area.Anchor, Area.Caret);
        var at = new TextPos(result.Start, 0);
        _doc.ReplaceLines(result.Start, result.Count, result.Pieces, sel, (at, at));
        Area.AfterHistoryMove((at, at));
        SearchStatus.Text = result.Replaced == 1 ? "Replaced 1 match." : $"Replaced {result.Replaced:N0} matches.";
    }

    /// <summary>
    /// The results list's items: every match found, as compact records, made into rows only as the
    /// list scrolls to them, so a million matches cost a few megabytes rather than a million rows.
    /// </summary>
    private sealed class ResultsSource(LargeFileView owner) : IList, INotifyCollectionChanged
    {
        private readonly List<LargeMatch> _matches = new();

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public bool Capped => _matches.Count >= MaxResults;
        public int Count => _matches.Count;
        public LargeMatch Get(int i) => _matches[i];

        public void AddRange(List<LargeMatch> batch)
        {
            _matches.AddRange(batch);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public void Clear()
        {
            if (_matches.Count == 0)
                return;
            _matches.Clear();
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public int IndexAtOrAfter(TextPos p)
        {
            int lo = 0, hi = _matches.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (_matches[mid].Start < p) lo = mid + 1;
                else hi = mid;
            }
            return lo < _matches.Count ? lo : -1;
        }

        public int IndexBefore(TextPos p)
        {
            int i = IndexAtOrAfter(p);
            return (i < 0 ? _matches.Count : i) - 1;
        }

        public object? this[int index]
        {
            get
            {
                var m = _matches[index];
                string line = owner._doc.GetLine(m.Line);
                int from = Math.Max(0, m.Col - 40);
                int len = Math.Min(line.Length - from, 300);
                string preview = (from > 0 ? "…" : "") + line.Substring(from, Math.Max(0, len)).Replace('\t', ' ');
                return new ResultRow($"{owner._doc.LogicalLine(m.Line) + 1:N0}", preview) { Index = index };
            }
            set => throw new NotSupportedException();
        }

        public bool IsFixedSize => false;
        public bool IsReadOnly => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public int Add(object? value) => throw new NotSupportedException();
        public bool Contains(object? value) => false;
        public int IndexOf(object? value) => value is ResultRow r ? r.Index : -1;
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        void IList.Clear() => throw new NotSupportedException();
        public void CopyTo(Array array, int index)
        {
            for (int i = 0; i < _matches.Count; i++)
                array.SetValue(this[i], index + i);
        }
        public IEnumerator GetEnumerator()
        {
            for (int i = 0; i < _matches.Count; i++)
                yield return this[i];
        }
    }

    public sealed record ResultRow(string Location, string Preview)
    {
        public int Index { get; init; } = -1;
    }

    // ===================== Crash recovery =====================

    private string RecoveryPath => Path.Combine(RecoveryDir, RecoveryId + ".json");

    /// <summary>A run of lines in a snapshot: lines [S, S+N) of the source, or the lines L.</summary>
    public sealed record PieceDto(long S, long N, string[]? L, int[]? J);

    /// <summary>
    /// What it takes to rebuild a large document's unsaved work: the file it was read from, as it
    /// was then, and the pieces in order. Lines still on disk are kept as references, so a snapshot
    /// is the size of the edits rather than of the file.
    /// </summary>
    public sealed record RecoveryData(string Id, string Path, string SourcePath, bool SourceIsCopy,
        long SrcLength, DateTime SrcWriteUtc, ulong SrcFileId, LineEndingStyle LineEnding,
        long CaretLine, int CaretCol, long TopLine, List<PieceDto> Pieces, DateTime SavedAt);

    private void WriteRecovery(bool sync)
    {
        if (_doc is null || _disposed)
            return;
        try
        {
            if (!IsDirty)
            {
                DeleteRecovery();
                return;
            }
            if (!sync && _doc.Version == _lastRecoveryVersion)
                return;
            var src = _doc.Source;
            var pieces = _doc.Snapshot();
            var data = new RecoveryData(RecoveryId, _path, src.CopyPath ?? src.Path, src.CopyPath is not null,
                src.Stamp.Length, src.Stamp.LastWriteUtc, src.Stamp.FileId, _lineEnding,
                Area.Caret.Line, Area.Caret.Col, Area.TopLine, new List<PieceDto>(), DateTime.Now);
            _lastRecoveryVersion = _doc.Version;
            string path = RecoveryPath;
            void Write()
            {
                foreach (var p in pieces)
                {
                    if (ReferenceEquals(p.Src, src))
                        data.Pieces.Add(new PieceDto(p.Start, p.Count, null, null));
                    else
                    {
                        // Lines of a source the snapshot can't refer to (an older version, kept for
                        // undo) are written out.
                        var lines = new List<string>();
                        var joined = new List<int>();
                        for (long k = 0; k < p.Count; k++)
                        {
                            string text = p.Src is { } s ? s.LineText(p.Start + k) : p.Lines![p.Offset + (int)k].Text;
                            if (p.IsJoined(k)) joined.Add((int)k);
                            lines.Add(text);
                        }
                        data.Pieces.Add(new PieceDto(0, p.Count, lines.ToArray(), joined.Count > 0 ? joined.ToArray() : null));
                    }
                }
                Directory.CreateDirectory(RecoveryDir);
                string temp = path + ".tmp";
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(fs, data);
                File.Move(temp, path, overwrite: true);
            }
            if (sync)
                Write();
            else
                Task.Run(() =>
                {
                    try { Write(); }
                    catch (Exception ex) { CrashLog.Log("Writing a large file's recovery snapshot failed", ex); }
                });
            _lastAutosave = DateTime.Now;
            RaiseStatus();
        }
        catch (Exception ex) { CrashLog.Log("Writing a large file's recovery snapshot failed", ex); }
    }

    private void DeleteRecovery()
    {
        _lastRecoveryVersion = -1;
        TryDelete(RecoveryPath);
    }

    public void FlushRecovery() => WriteRecovery(sync: true);

    /// <summary>Recovery snapshots of large documents left by a previous session.</summary>
    public static List<RecoveryData> ScanRecoveries()
    {
        var list = new List<RecoveryData>();
        try
        {
            if (!Directory.Exists(RecoveryDir))
                return list;
            foreach (var file in Directory.EnumerateFiles(RecoveryDir, "*.json"))
            {
                try
                {
                    using var fs = File.OpenRead(file);
                    if (JsonSerializer.Deserialize<RecoveryData>(fs) is { } d)
                        list.Add(d);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    public static void ClearAllRecoveries()
    {
        try
        {
            if (Directory.Exists(RecoveryDir))
                foreach (var f in Directory.EnumerateFiles(RecoveryDir))
                    TryDelete(f);
        }
        catch { }
    }

    /// <summary>
    /// Rebuild a document from its snapshot: open the file it was read from, check it is still the
    /// version the snapshot refers to, and replay the pieces as one undoable edit.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file has changed since, so the snapshot's
    /// references to its lines no longer mean what they did: the snapshot is useless.</exception>
    /// <exception cref="IOException">The file can't be read or protected now; the snapshot may still
    /// be recovered later.</exception>
    public void LoadRecovered(RecoveryData data)
    {
        RecoveryId = data.Id;
        _path = data.Path;
        SourceFile src;
        if (data.SourceIsCopy)
            src = SourceFile.OpenCopy(data.SourcePath, data.Path);
        else
        {
            // Locked, whatever the setting says: the edits refer to its lines. A program that has it
            // open for writing again (a log restarted) gets the private copy instead, below.
            try { src = SourceFile.Open(data.SourcePath, lockFile: true); }
            catch (IOException) when (File.Exists(data.SourcePath)) { src = SourceFile.Open(data.SourcePath, lockFile: false); }
        }
        try
        {
            if (src.Stamp.Length != data.SrcLength || src.Stamp.LastWriteUtc != data.SrcWriteUtc
                || (data.SrcFileId != 0 && src.Stamp.FileId != data.SrcFileId))
                throw new InvalidOperationException(
                    $"{data.Path} has changed since the unsaved edits were made, so they can't be put back on it.");
            src.WaitForIndex();
            if (src.IndexError is not null)
                throw new IOException($"{data.SourcePath} couldn't be read: {src.IndexError.Message}");
            var pieces = new List<Piece>();
            foreach (var p in data.Pieces)
            {
                if (p.L is null)
                {
                    if (p.S < 0 || p.S + p.N > src.LineCount)
                        throw new InvalidOperationException($"The snapshot of {data.Path} doesn't match the file.");
                    pieces.Add(Piece.FromSource(src, p.S, p.N));
                }
                else
                {
                    var joined = p.J is null ? new HashSet<int>() : new HashSet<int>(p.J);
                    var lines = p.L.Select((t, i) => new ELine(t, joined.Contains(i))).ToArray();
                    pieces.Add(Piece.FromLines(lines, 0, lines.Length));
                }
            }
            var doc = new LargeDocument(src);
            var start = new TextPos(0, 0);
            doc.ReplaceLines(0, doc.LineCount, pieces, (start, start), (start, start));
            _doc = doc;
            Area.Document = doc;
            try { src.RememberTail(); } catch { }
            if (!EnsureProtected(src))
                throw new IOException($"{data.Path} is being written by another program and couldn't be copied.");
            _diskStamp = data.SourceIsCopy ? SourceFile.Probe(data.Path) ?? default : src.Stamp;
            _savedLineEnding = src.DetectedLineEnding;
            _lineEnding = data.LineEnding;
            Area.IsReadOnly = false;
            var caret = new TextPos(data.CaretLine, data.CaretCol);
            Area.Select(caret, caret, scroll: false);
            Area.ScrollTo(data.TopLine);
            StartWatching();
            ShowNotice("Recovered unsaved edits from the previous session.");
            RaiseAll();
        }
        catch
        {
            src.Dispose(keepCopy: true);
            throw;
        }
    }

    /// <summary>Forget a snapshot that can't be recovered (and the private copy it refers to).</summary>
    public static void DiscardRecovery(RecoveryData data)
    {
        TryDelete(Path.Combine(RecoveryDir, data.Id + ".json"));
        if (data.SourceIsCopy)
            TryDelete(data.SourcePath);
    }

    // ===================== Moving between processes =====================

    /// <summary>What a tab dragged to another process carries: only a document without unsaved
    /// edits can go, by path, since the edits refer to lines this process has open.</summary>
    public sealed record TransferData(string Path, long CaretLine, int CaretCol, long TopLine);

    public string? SerializeForTransfer() => IsDirty || _doc.Source.CopyPath is not null
        ? null
        : JsonSerializer.Serialize(new TransferData(_path, Area.Caret.Line, Area.Caret.Col, Area.TopLine));

    public void LoadTransferred(TransferData data)
    {
        LoadFile(data.Path);
        // The lines may not be indexed yet: the place is taken up once they are (OnIndexComplete).
        _pendingPlace = (new TextPos(data.CaretLine, data.CaretCol), data.TopLine);
        if (_doc.Source.Complete)
            IndexTick();
    }

    /// <summary>Where a tab moved from another process was scrolled to, put back once indexed.</summary>
    private (TextPos Caret, long TopLine)? _pendingPlace;

    // ===================== Lifetime =====================

    public void Dispose()
    {
        Release(keepCopy: false);
        DeleteRecovery();
    }

    /// <summary>Keeps the recovery snapshot, and the private copies it refers to; with no snapshot
    /// (nothing unsaved) there is nothing for a copy to be kept for.</summary>
    public void Release() => Release(keepCopy: File.Exists(RecoveryPath));

    private void Release(bool keepCopy)
    {
        if (_disposed)
            return;
        _disposed = true;
        _indexTimer.Stop();
        _autosave.Stop();
        _searchDebounce.Stop();
        _refreshSearch.Stop();
        _poll?.Dispose();
        _poll = null;
        _searchCts?.Cancel();
        _findCts?.Cancel();
        _doc?.Dispose(keepCopy);
    }
}
