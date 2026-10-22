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
    public static bool IsCopy(string exe) =>
        Path.GetFullPath(exe).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
    /// there is none yet.</summary>
    public static string? Find(string exe)
    {
        string copy = CopyPathFor(new FileInfo(exe));
        return File.Exists(copy) ? copy : null;
    }

    /// <summary>Copy <paramref name="exe"/> to its place under <see cref="Root"/> and return the
    /// copy's path, or null if that failed (the caller then runs the exe as it is). Can take a
    /// while: the self-contained exe is about 160 MB, read over the network.</summary>
    public static string? Make(string exe)
    {
        try
        {
            var source = new FileInfo(exe);
            string copy = CopyPathFor(source);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            // Under a name of its own, then renamed: a copy under the real name is always complete,
            // and two launches at once don't write into each other's.
            string partial = $"{copy}.{Environment.ProcessId}.partial";
            File.Copy(exe, partial, overwrite: true);
            source.Refresh();
            if (CopyPathFor(source) != copy)
            {
                // Replaced while it was being copied: the copy is of neither version.
                File.Delete(partial);
                return null;
            }
            try { File.Move(partial, copy); }
            catch (IOException) when (File.Exists(copy)) { File.Delete(partial); }   // another launch won
            return copy;
        }
        catch (Exception ex)
        {
            CrashLog.Log($"Copying {exe} to run it locally failed", ex);
            return null;
        }
    }

    /// <summary>Where the copy of <paramref name="source"/>, as it is now, belongs. Size and time
    /// tell versions apart, so a new build gets a folder of its own.</summary>
    private static string CopyPathFor(FileInfo source) =>
        Path.Combine(Root,
            $"{Path.GetFileNameWithoutExtension(source.Name)}-{source.Length:x}-{source.LastWriteTimeUtc.Ticks:x}",
            source.Name);

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
