using System.IO;
using System.Text.Json;

namespace NotepadRedo;

/// <summary>
/// Remembers which document files were open between runs so a restart reopens the same tabs.
/// Only saved-file paths are stored here; unsaved / untitled buffers are handled separately by the
/// crash-recovery snapshots (see <see cref="EditorView"/>). Best-effort: any failure just means the
/// session isn't restored, never that the app breaks.
/// </summary>
public static class SessionStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotepadRedo");
    private static readonly string FilePath = Path.Combine(Dir, "session.json");

    private sealed record SessionData(List<string> Files, DateTime SavedAt);

    /// <summary>Persist the ordered list of open file paths (overwrites any previous session).</summary>
    public static void Save(IEnumerable<string> files)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var data = new SessionData(files.ToList(), DateTime.Now);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
        }
        catch { /* best-effort — losing the session file is not fatal */ }
    }

    /// <summary>The previously-open file paths that still exist on disk, in their saved order.</summary>
    public static List<string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(FilePath));
                if (data?.Files is { } files)
                    return files.Where(f => !string.IsNullOrEmpty(f) && File.Exists(f)).ToList();
            }
        }
        catch { /* corrupt / unreadable — start fresh */ }
        return new List<string>();
    }
}
