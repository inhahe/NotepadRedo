using System.IO;
using System.Text.Json;

namespace NotepadRedo;

/// <summary>
/// The most-recently-opened document paths, for the File &gt; Open Recent submenu. Shared by every
/// window and every running instance through one file on disk, which is re-read before each write so
/// two instances opening files at the same time merge rather than clobber each other. Best-effort:
/// any failure just means the list is short or stale, never that the app breaks.
/// </summary>
public static class RecentFiles
{
    /// <summary>How many entries to keep. Enough to be useful, few enough to stay a glanceable menu.</summary>
    public const int MaxEntries = 15;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotepadRedo");
    private static readonly string FilePath = Path.Combine(Dir, "recent.json");

    private sealed record RecentData(List<string> Files);

    /// <summary>Raised after the list changes, so open windows can rebuild their submenu.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Record that <paramref name="path"/> was just opened (or saved under a new name), moving it to
    /// the top of the list. A path already present is moved rather than duplicated, compared
    /// case-insensitively since Windows paths are.
    /// </summary>
    public static void Add(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            string full = Path.GetFullPath(path);
            var files = ReadRaw();
            files.RemoveAll(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
            files.Insert(0, full);
            if (files.Count > MaxEntries)
                files.RemoveRange(MaxEntries, files.Count - MaxEntries);
            Write(files);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Forget every remembered path.</summary>
    public static void Clear() => Write(new List<string>());

    /// <summary>
    /// The remembered paths, newest first, filtered to those that still exist. Files deleted or moved
    /// since they were opened are dropped from the returned list but left in the file — a path on a
    /// disconnected network share or unmounted drive shouldn't be forgotten just because it's briefly
    /// unreachable.
    /// </summary>
    public static List<string> Load()
    {
        var result = new List<string>();
        foreach (var f in ReadRaw())
        {
            try { if (File.Exists(f)) result.Add(f); }
            catch { /* unreachable path — skip for now, keep it on disk */ }
        }
        return result;
    }

    private static List<string> ReadRaw()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<RecentData>(File.ReadAllText(FilePath));
                if (data?.Files is { } files)
                    return files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            }
        }
        catch { /* corrupt / unreadable — start fresh */ }
        return new List<string>();
    }

    private static void Write(List<string> files)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new RecentData(files)));
        }
        catch { /* best-effort */ }
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
