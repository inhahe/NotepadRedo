using System.IO;
using System.Text.Json;

namespace NotepadRedo;

/// <summary>What pressing the window's X (close) button should do.</summary>
public enum CloseButtonBehavior
{
    /// <summary>Close the window (prompting to save unsaved work) — the normal behaviour.</summary>
    Close,
    /// <summary>Hide to the notification area (system tray); keep running in the background.</summary>
    MinimizeToTray,
    /// <summary>Minimise to the taskbar instead of closing.</summary>
    MinimizeToTaskbar,
}

/// <summary>How the previous session's open files are handled at startup.</summary>
public enum SessionRestoreMode
{
    /// <summary>Ask (listing the files) before reopening anything — the default, safest choice.</summary>
    Prompt,
    /// <summary>Silently reopen every file from the last session.</summary>
    Always,
    /// <summary>Never reopen; always start with a blank document.</summary>
    Never,
}

/// <summary>
/// Process-wide, persisted user preferences. Stored as JSON under LocalAppData so the
/// choice survives restarts and is shared by every window/instance.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Autosave (crash-recovery) period in seconds; 0 disables it.</summary>
    public int AutosaveSeconds { get; set; } = 30;

    /// <summary>When true, New/Open spawn a separate process; when false they add a tab.</summary>
    public bool OpenInNewInstance { get; set; }

    public bool WordWrap { get; set; } = true;
    public bool ShowTree { get; set; } = true;

    /// <summary>
    /// When true, history-tree previews show as much text as fits on one line (then a trailing
    /// ellipsis) instead of a fixed character count. The "Preview chars" slider is ignored.
    /// </summary>
    public bool PreviewFitToWidth { get; set; }

    /// <summary>
    /// When true (the default), the history-tree pane condenses each straight run of edits down to
    /// just its branch points, tips, and the current node — so long linear typing sessions don't
    /// bury the tree in one row per keystroke. Undo/redo stay granular regardless; only the display
    /// collapses. When false, every edit is shown as its own row.
    /// </summary>
    public bool HistoryBranchesOnly { get; set; } = true;

    /// <summary>What the window's X (close) button does.</summary>
    public CloseButtonBehavior CloseButton { get; set; } = CloseButtonBehavior.Close;

    /// <summary>How the previous session's open files are handled at startup.</summary>
    public SessionRestoreMode RestoreSession { get; set; } = SessionRestoreMode.Prompt;

    /// <summary>When true, watch open files for external modification and offer to reconcile.</summary>
    public bool WatchExternalChanges { get; set; } = true;

    /// <summary>When true, an opened file is held with a deny-write share lock so other programs
    /// can read but not modify or delete it while it is open in NotepadRedo.</summary>
    public bool LockFileWhileOpen { get; set; }

    /// <summary>
    /// When true, a file's whole branching undo history is written to a sidecar under LocalAppData
    /// when it's saved (and on clean close), and restored the next time the file is opened — provided
    /// the on-disk contents still match. Off by default (it persists document content to LocalAppData).
    /// </summary>
    public bool PersistHistory { get; set; }

    // ----- Editor font (applied to the text area of every document) -----

    /// <summary>Editor font family name.</summary>
    public string FontFamily { get; set; } = "Consolas";

    /// <summary>Editor font size, in points (as shown in the font picker). Converted to WPF
    /// device-independent units when applied to the editor.</summary>
    public double FontSize { get; set; } = 11;

    public bool FontBold { get; set; }
    public bool FontItalic { get; set; }

    // ----- Undo granularity (how typing is grouped into history-tree nodes) -----

    /// <summary>When true, pressing Enter ends the current typing burst so each line becomes
    /// its own undo step.</summary>
    public bool UndoBreakOnEnter { get; set; } = true;

    /// <summary>When true, a paste is isolated as its own undo step (separate from the typing
    /// before and after it).</summary>
    public bool UndoBreakOnPaste { get; set; } = true;

    /// <summary>When true, every single character (and deletion) is its own undo step —
    /// coalescing and the pause timer are ignored.</summary>
    public bool UndoPerCharacter { get; set; }

    /// <summary>How long a typing pause (in seconds) can be before the next edit starts a fresh
    /// undo step. Consecutive edits closer together than this are folded into one node.</summary>
    public double UndoCoalesceSeconds { get; set; } = 4;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotepadRedo");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    /// <summary>
    /// One-time migration: if the old TreeNotepad data folder exists and the new NotepadRedo
    /// folder does not, move it wholesale so settings, crash recovery, and logs carry over.
    /// </summary>
    static AppSettings()
    {
        try
        {
            var oldDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TreeNotepad");
            if (Directory.Exists(oldDir) && !Directory.Exists(Dir))
                Directory.Move(oldDir, Dir);
        }
        catch { /* best-effort; if it fails the user just starts fresh */ }
    }

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    /// <summary>
    /// Re-read settings from disk into this singleton, picking up changes saved by other
    /// instances. Called when a window is activated (brought to the foreground).
    /// </summary>
    public void Reload()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            var fresh = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            if (fresh is null)
                return;
            AutosaveSeconds    = fresh.AutosaveSeconds;
            OpenInNewInstance   = fresh.OpenInNewInstance;
            WordWrap            = fresh.WordWrap;
            ShowTree            = fresh.ShowTree;
            PreviewFitToWidth   = fresh.PreviewFitToWidth;
            HistoryBranchesOnly = fresh.HistoryBranchesOnly;
            CloseButton         = fresh.CloseButton;
            RestoreSession      = fresh.RestoreSession;
            WatchExternalChanges = fresh.WatchExternalChanges;
            LockFileWhileOpen   = fresh.LockFileWhileOpen;
            PersistHistory      = fresh.PersistHistory;
            FontFamily          = fresh.FontFamily;
            FontSize            = fresh.FontSize;
            FontBold            = fresh.FontBold;
            FontItalic          = fresh.FontItalic;
            UndoBreakOnEnter    = fresh.UndoBreakOnEnter;
            UndoBreakOnPaste    = fresh.UndoBreakOnPaste;
            UndoPerCharacter    = fresh.UndoPerCharacter;
            UndoCoalesceSeconds = fresh.UndoCoalesceSeconds;
        }
        catch { /* best-effort */ }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
}
