using System.IO;

namespace NotepadRedo;

/// <summary>Where NotepadRedo keeps its data: settings, session, recent files, crash recovery,
/// persisted history and the crash log.</summary>
public static class AppPaths
{
    /// <summary>Names another data folder in place of the default. The tests point it at a scratch
    /// folder, so they never read the user's settings or leave anything in their recovery folder.</summary>
    public const string OverrideVariable = "NOTEPADREDO_DATA_DIR";

    /// <summary><c>%LOCALAPPDATA%\NotepadRedo</c>, unless <see cref="OverrideVariable"/> is set.</summary>
    public static string DataDir { get; }

    static AppPaths()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? custom = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrEmpty(custom))
        {
            DataDir = custom;
            return;   // never migrate the user's real data into a scratch folder
        }
        DataDir = Path.Combine(local, "NotepadRedo");

        // One-time migration from the app's old name: move the whole folder, so settings, crash
        // recovery and logs carry over. It lives here, ahead of every class that might create
        // DataDir first (the crash log, say), since an existing DataDir stops the move.
        try
        {
            var oldDir = Path.Combine(local, "TreeNotepad");
            if (Directory.Exists(oldDir) && !Directory.Exists(DataDir))
                Directory.Move(oldDir, DataDir);
        }
        catch { /* best-effort; if it fails the user just starts fresh */ }
    }
}
