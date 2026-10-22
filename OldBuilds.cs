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
/// extracted folder can't be renamed while a running instance holds a file in it open, and every
/// instance holds one (<see cref="HoldOwnExtraction"/>), so it is renamed first, then deleted: a
/// starting instance never finds it half gone. A local copy's folder can be renamed while its exe
/// runs, but the exe can't be deleted, so the exe goes first.</remarks>
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

    /// <summary>This app's folder under <see cref="ExtractionBase"/>, named for the exe.</summary>
    private static string? AppExtractionFolder =>
        Environment.ProcessPath is string exe
            ? Path.GetFullPath(Path.Combine(ExtractionBase, Path.GetFileNameWithoutExtension(exe)))
            : null;

    /// <summary>A bundle id, which names .NET's extraction folder: 12 characters of base64 (the
    /// url-safe kind, in the ids seen here; the others are allowed too).</summary>
    private static readonly Regex BundleId = new("^[A-Za-z0-9_+=-]{12}$");

    /// <summary>Kept open for the life of the process, so that no sweep can take this instance's
    /// extraction folder.</summary>
    private static FileStream? _held;

    /// <summary>Hold a file in this instance's own extraction folder open, which stops the folder
    /// being renamed, and so swept, while the instance runs. A loaded library doesn't do that by
    /// itself: Windows keeps no handle open on a DLL loaded with LoadLibrary, which is all a
    /// self-contained build has there. (The framework-dependent build's app assembly is held open
    /// by .NET once loaded; holding something too costs nothing.) Call first thing at startup.</summary>
    /// <remarks>The folder is the native search directory .NET was given under the app's
    /// extraction folder: it is there from the start, before any library has been loaded from it.</remarks>
    public static void HoldOwnExtraction()
    {
        try
        {
            if (_held is not null || AppExtractionFolder is not string folder)
                return;
            string? own = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.TrimEnd(Path.DirectorySeparatorChar))
                .FirstOrDefault(d => LocalCopy.IsInside(d, folder));
            if (own is null && typeof(OldBuilds).Assembly.Location is { Length: > 0 } assembly
                && LocalCopy.IsInside(assembly, folder))
                own = Path.GetDirectoryName(assembly);
            if (own is not null && Directory.EnumerateFiles(own).FirstOrDefault() is string file)
                _held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) { CrashLog.Log("Holding this build's extraction folder failed", ex); }
    }

    private static void Sweep()
    {
        try
        {
            if (Environment.ProcessPath is not string exe || AppExtractionFolder is not string extracted)
                return;
            SweepExtracted(extracted);
            SweepCopies(Path.GetFileNameWithoutExtension(exe));
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
                    Directory.Move(folder, doomed);   // refused while an instance holds a file in it
                Directory.Delete(doomed, recursive: true);
            }
            catch (IOException) { }                    // in use
            catch (UnauthorizedAccessException) { }    // in use
        }
    }

    /// <summary>The local copies of exes named <paramref name="name"/>, in <see cref="LocalCopy.Root"/>,
    /// that <see cref="CopiesToSweep"/> picks.</summary>
    private static void SweepCopies(string name)
    {
        if (!Directory.Exists(LocalCopy.Root))
            return;
        var folders = Directory.EnumerateDirectories(LocalCopy.Root)
            .Select(f => (f, Directory.GetCreationTimeUtc(f), Directory.GetLastWriteTimeUtc(f)));
        foreach (string folder in CopiesToSweep(folders, name, Path.GetDirectoryName(Environment.ProcessPath), DateTime.UtcNow))
        {
            try
            {
                foreach (string copy in Directory.EnumerateFiles(folder, "*.exe"))
                {
                    File.SetAttributes(copy, FileAttributes.Normal);   // a read-only one would look in use
                    File.Delete(copy);                                 // refused while it runs
                }
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>How recently a copy folder must have been made or used to be spared regardless.</summary>
    private static readonly TimeSpan RecentlyUsed = TimeSpan.FromMinutes(10);

    /// <summary>Which of <paramref name="folders"/> (folders under <see cref="LocalCopy.Root"/>, with
    /// their creation and last-write times, UTC) to delete, for an instance of an exe named
    /// <paramref name="name"/> running from the folder <paramref name="mine"/> at <paramref name="now"/>.
    /// Only copies of that name are candidates (see <see cref="LocalCopy.TryParseFolder"/>), and of
    /// those, spared are: the one this instance runs from; the newest version, which the next launch
    /// of the current version will want (another build sweeping mustn't make it copy 160 MB again);
    /// and any made or used in the last few minutes, which a launch may be writing or about to start.
    /// Whether a candidate is in use is for the caller to find out.</summary>
    internal static List<string> CopiesToSweep(IEnumerable<(string Folder, DateTime Created, DateTime Written)> folders,
                                               string name, string? mine, DateTime now)
    {
        var copies = folders
            .Select(f => (f.Folder, f.Created, f.Written,
                          Ok: LocalCopy.TryParseFolder(Path.GetFileName(f.Folder), name, out long ticks), Ticks: ticks))
            .Where(c => c.Ok)
            .ToList();
        if (copies.Count == 0)
            return [];
        long newest = copies.Max(c => c.Ticks);
        return copies
            .Where(c => c.Ticks != newest
                        && !string.Equals(c.Folder, mine, StringComparison.OrdinalIgnoreCase)
                        && now - (c.Created > c.Written ? c.Created : c.Written) >= RecentlyUsed)
            .Select(c => c.Folder)
            .ToList();
    }
}
