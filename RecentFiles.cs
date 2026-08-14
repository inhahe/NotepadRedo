using System.IO;
using System.Text.Json;

namespace NotepadRedo;

/// <summary>
/// The most-recently-opened document paths, for the File &gt; Open Recent submenu. Shared by every
/// window and every running instance through one file on disk, which is re-read before each write so
/// two instances opening files at the same time merge rather than clobber each other. Best-effort:
/// any failure just means the list is short or stale, never that the app breaks.
///
/// <para>Nothing here ever probes a document path on the caller's thread. The list is read on the UI
/// thread every time a window is activated, and <c>File.Exists</c> on a remembered path can block for
/// <i>tens of seconds</i> — the path may live on a disconnected network share, an unmounted drive, or
/// a disk that has spun down. Doing that inside <c>WM_ACTIVATE</c> freezes the window solid, and with
/// two windows open (a tab torn off into its own window) activation happens every time focus moves
/// between them, which turns a rare stall into a constant one. So <see cref="Existing"/> is a cached
/// snapshot that costs nothing to read, and <see cref="Refresh"/> updates it on a background thread.
/// </para>
/// </summary>
public static class RecentFiles
{
    /// <summary>How many entries to keep. Enough to be useful, few enough to stay a glanceable menu.</summary>
    public const int MaxEntries = 15;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotepadRedo");
    private static readonly string FilePath = Path.Combine(Dir, "recent.json");

    private sealed record RecentData(List<string> Files);

    /// <summary>
    /// Raised after <see cref="Existing"/> changes, so open windows can rebuild their submenu.
    /// <b>May be raised on a background thread</b> (the existence sweep runs off the UI thread), so
    /// handlers that touch UI must marshal onto the dispatcher.
    /// </summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// The remembered paths that were present as of the last <see cref="Refresh"/>, newest first.
    /// Reading this does no I/O whatsoever — see the type remarks for why that matters.
    /// </summary>
    public static IReadOnlyList<string> Existing => _existing;

    private static string[] _existing = Array.Empty<string>();

    /// <summary>0/1 guard: only one existence sweep runs at a time.</summary>
    private static int _sweeping;

    /// <summary>
    /// Re-read the list and drop paths that no longer exist — <i>on a background thread</i> — then
    /// raise <see cref="Changed"/> if the result differs from the cached one. Returns immediately.
    /// Callers treat this as eventually consistent: a sweep already in flight is left to finish
    /// rather than queued behind, because every window activation asks for another one anyway.
    /// </summary>
    public static void Refresh()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1)
            return;
        Task.Run(() =>
        {
            try
            {
                var found = new List<string>();
                foreach (var f in ReadRaw())
                {
                    // Files deleted or moved since they were opened are dropped from the cache but
                    // left in the file — a path on a disconnected share or unmounted drive shouldn't
                    // be forgotten just because it's briefly unreachable.
                    try { if (File.Exists(f)) found.Add(f); }
                    catch { /* unreachable path — skip for now, keep it on disk */ }
                }
                Publish(found);
            }
            catch { /* best-effort */ }
            finally { Volatile.Write(ref _sweeping, 0); }
        });
    }

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
            Promote(files, full);
            WriteRaw(files);

            // We just opened it, so it plainly exists: fold it into the cache directly rather than
            // paying for a sweep (which would re-probe every other remembered path as well).
            var cached = new List<string>(_existing);
            Promote(cached, full);
            Publish(cached);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Move <paramref name="full"/> to the head of <paramref name="files"/>, trimming to <see cref="MaxEntries"/>.</summary>
    private static void Promote(List<string> files, string full)
    {
        files.RemoveAll(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
        files.Insert(0, full);
        if (files.Count > MaxEntries)
            files.RemoveRange(MaxEntries, files.Count - MaxEntries);
    }

    /// <summary>Forget every remembered path.</summary>
    public static void Clear()
    {
        WriteRaw(new List<string>());
        Publish(new List<string>());
    }

    /// <summary>Swap in a new cached list and announce it, but only if it actually differs.</summary>
    private static void Publish(List<string> files)
    {
        var next = files.ToArray();
        if (next.AsSpan().SequenceEqual(_existing))
            return;
        _existing = next;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// The stored list, unfiltered. Only touches <c>recent.json</c> in LocalAppData — never a
    /// document path — so it is safe to call without probing anything that might be unreachable.
    /// </summary>
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

    private static void WriteRaw(List<string> files)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new RecentData(files)));
        }
        catch { /* best-effort */ }
    }
}
