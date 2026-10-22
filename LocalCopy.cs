using System.IO;
using System.Runtime.InteropServices;

namespace NotepadRedo;

/// <summary>
/// Runs an instance started from a network or removable drive from a local copy of its exe, when
/// its code would otherwise be read from that exe for as long as it runs. That is the self-contained
/// build: its code stays in the bundle, mapped from the exe a page at a time. Replaced on the share
/// while it runs (a build on another computer, after a dropped connection), the exe then feeds the
/// instance pieces of the new build. The framework-dependent build needs none of this: it loads its
/// code from an extracted copy (see "A running instance reads nothing from its exe" in design.md).
/// </summary>
/// <remarks>The launcher, which relaunches itself detached anyway (App.OnStartup), launches the copy
/// instead, and passes the exe the user started in <see cref="OriginVariable"/>. That one, not the
/// copy, is what the .txt registration and a taskbar pin must name (<see cref="OriginalExe"/>).
/// </remarks>
internal static class LocalCopy
{
    /// <summary>Environment variable through which a launcher tells the instance it starts from a
    /// local copy which exe the user actually started.</summary>
    public const string OriginVariable = "NOTEPADREDO_ORIGIN";

    /// <summary>Where the copies live: one folder per source exe and version,
    /// <c>&lt;name&gt;-&lt;size&gt;-&lt;time&gt;</c>, holding the exe under its own name (which picks
    /// the theme, and is how instances find each other).</summary>
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "NotepadRedo", "run");

    /// <summary>Whether this build's code is read from its exe as it runs: a self-contained bundle,
    /// whose assemblies have no file of their own. A framework-dependent build extracts its code
    /// and loads it from there, which gives it a location.</summary>
    public static bool CodeReadFromExe => string.IsNullOrEmpty(typeof(LocalCopy).Assembly.Location);

    /// <summary>Whether <paramref name="exe"/> should be run from a local copy rather than as it is.</summary>
    public static bool Needed(string exe) =>
        CodeReadFromExe && OnNetworkOrRemovableDrive(exe) && !IsCopy(exe);

    /// <summary>Whether <paramref name="exe"/> is one of the copies.</summary>
    public static bool IsCopy(string exe) => IsInside(exe, Root);

    /// <summary>The exe the user started: the one this instance is a copy of, else this one.</summary>
    public static string? OriginalExe { get; } = ResolveOriginal();

    /// <summary>Whether this instance runs from a copy, started for <see cref="OriginalExe"/>.</summary>
    public static bool IsRelocated => OriginalExe is not null && OriginalExe != Environment.ProcessPath;

    private static string? ResolveOriginal()
    {
        string? self = Environment.ProcessPath;
        string? origin = Environment.GetEnvironmentVariable(OriginVariable);
        // Only a copy has an origin: the variable is inherited by whatever this process starts.
        return self is not null && !string.IsNullOrEmpty(origin) && IsCopy(self) ? origin : self;
    }

    /// <summary>The existing, complete copy of <paramref name="exe"/> as it is now, or null if
    /// there is none yet. A copy found is marked as just used, so that a sweep running meanwhile
    /// (which spares recent folders) doesn't take it before it starts.</summary>
    public static string? Find(string exe)
    {
        string copy = CopyPathFor(new FileInfo(exe));
        if (!File.Exists(copy))
            return null;
        try { Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(copy)!, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return copy;
    }

    /// <summary>Copy <paramref name="exe"/> to its place under <see cref="Root"/> and return the
    /// copy's path, or null if that failed (the caller then runs the exe as it is). Can take a
    /// while: the self-contained exe is about 160 MB, read over the network. Launches making the
    /// same copy at once take turns, and the later ones use what the first made.</summary>
    public static string? Make(string exe)
    {
        try
        {
            var source = new FileInfo(exe);
            string copy = CopyPathFor(source);
            string folder = Path.GetDirectoryName(copy)!;
            using var turn = new Mutex(false, @"Local\NotepadRedo.LocalCopy." + Path.GetFileName(folder));
            bool mine;
            try { mine = turn.WaitOne(TurnWait); }
            catch (AbandonedMutexException) { mine = true; }   // a launch that died while copying: ours now
            if (!mine)
            {
                // Another launch has been copying for minutes (a share that stopped answering, say).
                // Rather than queue behind it indefinitely, run the exe as it is.
                CrashLog.Log($"Gave up waiting for another launch to copy {exe}", null);
                return null;
            }
            try
            {
                if (File.Exists(copy))
                    return copy;   // made by the launch this one waited for
                Directory.CreateDirectory(folder);
                // Partial copies left by launches that died: none is being written while we hold the
                // turn (one being written can't be deleted anyway, which leaves it alone).
                foreach (string stale in Directory.EnumerateFiles(folder, "*.partial"))
                {
                    try { File.Delete(stale); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                // Under a name of its own, then renamed: a copy under the real name is always complete.
                string partial = $"{copy}.{Environment.ProcessId}.partial";
                File.Copy(exe, partial, overwrite: true);
                // Not read-only, as a file on read-only media or an admin's share may be: the sweep
                // couldn't delete it, and would take that for its being in use.
                File.SetAttributes(partial, FileAttributes.Normal);
                source.Refresh();
                if (CopyPathFor(source) != copy)
                {
                    // Replaced while it was being copied: the copy is of neither version.
                    File.Delete(partial);
                    return null;
                }
                // The turn is per logon session; the same user starting it in another session may
                // have finished first.
                try { File.Move(partial, copy); }
                catch (IOException) when (File.Exists(copy)) { File.Delete(partial); }
                return copy;
            }
            finally { turn.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            CrashLog.Log($"Copying {exe} to run it locally failed", ex);
            return null;
        }
    }

    /// <summary>How long a launch waits for another one making the same copy before giving up.</summary>
    private static readonly TimeSpan TurnWait = TimeSpan.FromMinutes(2);

    /// <summary>Whether <paramref name="path"/> lies inside <paramref name="folder"/> (a full path
    /// without a trailing separator), at any depth.</summary>
    internal static bool IsInside(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the copy of <paramref name="source"/>, as it is now, belongs. Size and time
    /// tell versions apart, so a new build gets a folder of its own.</summary>
    private static string CopyPathFor(FileInfo source) =>
        Path.Combine(Root,
            FolderName(Path.GetFileNameWithoutExtension(source.Name), source.Length, source.LastWriteTimeUtc.Ticks),
            source.Name);

    /// <summary>The folder a copy of version (<paramref name="length"/>, <paramref name="ticks"/>)
    /// of an exe named <paramref name="name"/> lives in. <see cref="TryParseFolder"/> reads it back.</summary>
    private static string FolderName(string name, long length, long ticks) => $"{name}-{length:x}-{ticks:x}";

    /// <summary>Whether <paramref name="folder"/>, a folder name under <see cref="Root"/>, holds a
    /// copy of an exe named <paramref name="name"/>, and if so the time of the version it is a copy
    /// of (<paramref name="ticks"/>: later is newer).</summary>
    public static bool TryParseFolder(string folder, string name, out long ticks)
    {
        ticks = 0;
        if (!folder.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase))
            return false;
        var parts = folder[(name.Length + 1)..].Split('-');
        return parts.Length == 2
            && long.TryParse(parts[0], System.Globalization.NumberStyles.AllowHexSpecifier, null, out _)
            && long.TryParse(parts[1], System.Globalization.NumberStyles.AllowHexSpecifier, null, out ticks);
    }

    private static bool OnNetworkOrRemovableDrive(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return false;
            if (!root.EndsWith('\\'))
                root += '\\';
            uint type = GetDriveType(root);
            return type is DriveRemote or DriveRemovable;
        }
        catch (Exception) { return false; }
    }

    private const uint DriveRemovable = 2, DriveRemote = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
    private static extern uint GetDriveType(string rootPathName);
}
