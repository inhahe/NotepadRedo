using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NotepadRedo;

/// <summary>
/// Persists a file's branching undo history (a <see cref="TreeDto"/>) to a per-file sidecar under
/// LocalAppData so the history survives restarts. The sidecar is keyed by a hash of the file's full
/// path and carries a <see cref="HistoryData.Stamp"/> — a hash of the document contents the history
/// was anchored to. On load the stamp is compared against the current on-disk text; a mismatch means
/// the file changed since (edited elsewhere), so the stale history is ignored rather than presented
/// against contents it can't reconstruct. Entirely best-effort: any failure just means history isn't
/// restored, never that the app breaks. Gated by <see cref="AppSettings.PersistHistory"/>.
/// </summary>
public static class HistoryStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotepadRedo", "history");

    /// <summary>Drop sidecars untouched for longer than this so the folder can't grow unbounded.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    /// <summary>The persisted payload: what document the tree was anchored to, plus the tree itself.</summary>
    private sealed record HistoryData(string Path, string Stamp, TreeDto Tree, DateTime SavedAt);

    /// <summary>
    /// Write <paramref name="tree"/> for <paramref name="path"/>, anchored to <paramref name="anchorText"/>
    /// (the on-disk / saved contents the tree's current node reconstructs). Overwrites any previous
    /// sidecar for this path.
    /// </summary>
    public static void Save(string path, TreeDto tree, string anchorText)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var data = new HistoryData(path, Stamp(anchorText), tree, DateTime.Now);
            File.WriteAllText(FileFor(path), JsonSerializer.Serialize(data));
            PruneOld();
        }
        catch { /* best-effort — losing persisted history is not fatal */ }
    }

    /// <summary>
    /// Load the stored history for <paramref name="path"/>, but only if it was anchored to text that
    /// still matches <paramref name="currentDiskText"/>. Returns null when there is no sidecar, it's
    /// unreadable, or the file changed since the history was saved (stale).
    /// </summary>
    public static TreeDto? Load(string path, string currentDiskText)
    {
        try
        {
            var fp = FileFor(path);
            if (!File.Exists(fp))
                return null;
            var data = JsonSerializer.Deserialize<HistoryData>(File.ReadAllText(fp));
            if (data?.Tree is null)
                return null;
            if (data.Stamp != Stamp(currentDiskText))
                return null;   // the file changed since — don't present stale history
            return data.Tree;
        }
        catch { return null; }
    }

    /// <summary>Remove any stored history for <paramref name="path"/> (e.g. when there's nothing to keep).</summary>
    public static void Delete(string path)
    {
        try
        {
            var fp = FileFor(path);
            if (File.Exists(fp))
                File.Delete(fp);
        }
        catch { /* best-effort */ }
    }

    // ----- helpers -----

    /// <summary>Sidecar path: a hash of the (case-folded) full file path keeps names flat and safe.</summary>
    private static string FileFor(string path)
    {
        string key = Path.GetFullPath(path).ToLowerInvariant();
        return Path.Combine(Dir, Hash(key) + ".json");
    }

    /// <summary>Content fingerprint used to detect the file changing out from under the history.</summary>
    private static string Stamp(string text) => Hash(text ?? "");

    private static string Hash(string s)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static void PruneOld()
    {
        try
        {
            var cutoff = DateTime.Now - MaxAge;
            foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
            {
                try { if (File.GetLastWriteTime(file) < cutoff) File.Delete(file); }
                catch { /* skip the odd locked/vanished file */ }
            }
        }
        catch { /* best-effort */ }
    }
}
