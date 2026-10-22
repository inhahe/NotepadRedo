using System.IO;
using Microsoft.Win32;

namespace NotepadRedo;

/// <summary>
/// Per-user registration that declares NotepadRedo as capable of opening <c>.txt</c> files. Windows
/// only offers the "Always use this app" option (and lists the app in Settings &gt; Default apps) when
/// an application registers that it supports the type; a bare, unregistered exe browsed to in
/// "Open with" only ever gets a one-off "Just once" launch.
///
/// This never *forces* the default: Windows guards the final <c>.txt</c> choice with a hashed
/// <c>UserChoice</c> key that only the shell can set on the user's behalf. All this does is make
/// NotepadRedo a first-class, selectable handler, so the user can then pick "Always".
///
/// Every write is under <c>HKEY_CURRENT_USER</c> (no admin needed). A launch registers its own exe
/// only when no NotepadRedo is registered, or the registered one is gone (see
/// <see cref="ShouldRegister"/>), so the association follows the exe when it moves, but a copy run
/// from somewhere else (a build folder, a test, a themed variant) doesn't take it over by starting.
/// </summary>
internal static class FileAssociation
{
    private const string ProgId = "NotepadRedo.txt";
    private const string AppExe = "NotepadRedo.exe";   // key under Software\Classes\Applications

    /// <summary>Write the per-user .txt registration for this exe, if <see cref="ShouldRegister"/>
    /// says so. Best-effort; never throws. Checking the registered exe can block for as long as an
    /// unreachable share takes to give up, so call this off the UI thread.</summary>
    public static void EnsureTxtRegistered()
    {
        try
        {
            // The exe the user started, not the local copy this instance may run from (LocalCopy):
            // the copy goes when a later build is swept.
            string? exe = LocalCopy.OriginalExe;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                return;

            string open = $"\"{exe}\" \"%1\"";
            string icon = $"{exe},0";

            string? registered;
            using (var existing = Registry.CurrentUser.OpenSubKey(
                       $@"Software\Classes\{ProgId}\shell\open\command"))
                registered = existing?.GetValue(null) as string;
            if (!ShouldRegister(registered, File.Exists, Directory.Exists))
                return;

            using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
            {
                // ProgId: friendly type name + icon + how to open.
                using (var progid = classes.CreateSubKey(ProgId))
                {
                    progid.SetValue(null, "Text Document");
                    using (var di = progid.CreateSubKey("DefaultIcon"))
                        di.SetValue(null, icon);
                    using (var cmd = progid.CreateSubKey(@"shell\open\command"))
                        cmd.SetValue(null, open);
                }

                // Applications entry: puts NotepadRedo in "Open with" and enables the "Always" option.
                using (var app = classes.CreateSubKey($@"Applications\{AppExe}"))
                {
                    app.SetValue("FriendlyAppName", "NotepadRedo");
                    using (var cmd = app.CreateSubKey(@"shell\open\command"))
                        cmd.SetValue(null, open);
                    using (var st = app.CreateSubKey("SupportedTypes"))
                        st.SetValue(".txt", "");
                }

                // Offer the ProgId in the .txt "Open with" list.
                using (var owp = classes.CreateSubKey(@".txt\OpenWithProgids"))
                    owp.SetValue(ProgId, "");
            }

            // Capabilities + RegisteredApplications: surface NotepadRedo in Settings > Default apps.
            using (var cap = Registry.CurrentUser.CreateSubKey(@"Software\NotepadRedo\Capabilities"))
            {
                cap.SetValue("ApplicationName", "NotepadRedo");
                cap.SetValue("ApplicationDescription", "Branching-undo text editor");
                using (var fa = cap.CreateSubKey("FileAssociations"))
                    fa.SetValue(".txt", ProgId);
            }
            using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                reg.SetValue("NotepadRedo", @"Software\NotepadRedo\Capabilities");
        }
        catch (Exception ex)
        {
            CrashLog.Log("FileAssociation.EnsureTxtRegistered failed", ex);
        }
    }

    /// <summary>Whether a launch should register itself, given the open command already registered
    /// (<paramref name="command"/>, null if none). Yes when there is none, or the exe it names is
    /// gone. No while that exe is still there, whichever copy it is: which NotepadRedo opens .txt
    /// files is the user's choice (associate-txt.bat makes it), not the last one started. And no
    /// while it can't be told: a registered exe on a share that is offline is not gone.</summary>
    /// <param name="fileExists">Checks a file (<see cref="File.Exists"/>; a test passes its own).</param>
    /// <param name="folderExists">Checks a folder (<see cref="Directory.Exists"/>), here the root of
    /// the registered exe's drive or share, to tell "gone" from "unreachable".</param>
    internal static bool ShouldRegister(string? command, Func<string, bool> fileExists,
                                        Func<string, bool> folderExists)
    {
        if (ExeOf(command) is not string registered)
            return true;
        if (fileExists(registered))
            return false;
        // No root (not a full path, or not a path at all): nothing to be offline, so it's gone.
        string? root = Path.GetPathRoot(registered);
        return string.IsNullOrEmpty(root) || folderExists(root);
    }

    /// <summary>The program an open command runs: its first token, quoted or not, or null if the
    /// command is empty.</summary>
    internal static string? ExeOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        command = command.Trim();
        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }
}
