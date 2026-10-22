using System.IO;
using System.Text.RegularExpressions;

namespace NotepadRedo;

/// <summary>
/// Deletes what earlier builds left in temp folders: .NET's extracted copies of them, and the local
/// copies <see cref="LocalCopy"/> ran them from. Neither cleans up after itself, so each build would
/// leave its own behind for good (about 230 KB extracted per framework-dependent build, about 10 MB
/// of native libraries per self-contained one, and the whole 160 MB exe per relocated one).
/// </summary>
/// <remarks>Only folders nothing is using are deleted, which each kind tells differently. An
/// extracted folder can't be renamed while a running instance has a library from it loaded, so it is
/// renamed first, then deleted: a starting instance never sees it half gone. A local copy's folder can
/// be renamed while its exe runs, but the exe can't be deleted, so the exe goes first.</remarks>
internal static class OldBuilds
{
    /// <summary>Delete, in the background and once startup has long finished, every extracted and
    /// local copy of this exe that no running instance is using.</summary>
    public static void SweepSoon() =>
        Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Sweep(), TaskScheduler.Default);

    /// <summary>The folder .NET extracts a single-file app's bundle into, per app name.</summary>
    internal static string ExtractionBase =>
        Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Path.GetTempPath(), ".net");

    /// <summary>A bundle id, which names .NET's extraction folder: 12 characters of base64url.</summary>
    private static readonly Regex BundleId = new("^[A-Za-z0-9_-]{12}$");

    private static void Sweep()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;
            string name = Path.GetFileNameWithoutExtension(exe);
            SweepExtracted(Path.Combine(ExtractionBase, name));
            SweepCopies(name);
        }
        catch (Exception ex) { CrashLog.Log("Sweeping old builds failed", ex); }
    }

    /// <summary>.NET's extraction folders for this app, in <paramref name="appFolder"/>. Only
    /// folders named like a bundle id, so that a base folder pointed somewhere unexpected costs
    /// nothing but what .NET itself put there; and folders an earlier sweep renamed but didn't get to
    /// delete.</summary>
    private static void SweepExtracted(string appFolder)
    {
        if (!Directory.Exists(appFolder))
            return;
        foreach (string folder in Directory.EnumerateDirectories(appFolder))
        {
            string leaf = Path.GetFileName(folder);
            bool claimed = leaf.EndsWith(".old", StringComparison.Ordinal)
                           && BundleId.IsMatch(leaf[..^".old".Length]);
            if (!claimed && !BundleId.IsMatch(leaf))
                continue;
            try
            {
                string doomed = claimed ? folder : folder + ".old";
                if (!claimed)
                    Directory.Move(folder, doomed);   // refused while an instance has a library from it loaded
                Directory.Delete(doomed, recursive: true);
            }
            catch (IOException) { }                    // in use
            catch (UnauthorizedAccessException) { }    // in use
        }
    }

    /// <summary>The local copies of exes named <paramref name="name"/>, in <see cref="LocalCopy.Root"/>,
    /// except the one this instance runs from, and any made in the last few minutes: a launch of
    /// another build may be copying into that folder, or about to start what it copied.</summary>
    private static void SweepCopies(string name)
    {
        if (!Directory.Exists(LocalCopy.Root))
            return;
        string? mine = Path.GetDirectoryName(Environment.ProcessPath);
        var ours = new Regex("^" + Regex.Escape(name) + "-[0-9a-f]+-[0-9a-f]+$");
        foreach (string folder in Directory.EnumerateDirectories(LocalCopy.Root))
        {
            if (!ours.IsMatch(Path.GetFileName(folder))
                || string.Equals(folder, mine, StringComparison.OrdinalIgnoreCase)
                || DateTime.UtcNow - Directory.GetCreationTimeUtc(folder) < TimeSpan.FromMinutes(10))
                continue;
            try
            {
                foreach (string copy in Directory.EnumerateFiles(folder, "*.exe"))
                    File.Delete(copy);                 // refused while it runs
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
