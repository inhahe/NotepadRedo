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
/// Every write is under <c>HKEY_CURRENT_USER</c> (no admin needed) and idempotent, so it is safe to
/// call on each launch. The registered open-command points at the *current* executable path, so the
/// association follows the exe if it moves and a fresh build re-registers itself automatically.
/// </summary>
internal static class FileAssociation
{
    private const string ProgId = "NotepadRedo.txt";
    private const string AppExe = "NotepadRedo.exe";   // key under Software\Classes\Applications

    /// <summary>Write/refresh the per-user .txt registration. Best-effort; never throws.</summary>
    public static void EnsureTxtRegistered()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                return;

            string open = $"\"{exe}\" \"%1\"";
            string icon = $"{exe},0";

            // Fast path: already registered for THIS exe path — skip the churn.
            using (var existing = Registry.CurrentUser.OpenSubKey(
                       $@"Software\Classes\{ProgId}\shell\open\command"))
            {
                if (existing?.GetValue(null) as string == open)
                    return;
            }

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
}
