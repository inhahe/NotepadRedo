using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NotepadRedo;

public partial class App : Application
{
    private IpcServer? _ipc;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Detach from a launching console so its prompt returns immediately. cmd.exe (and batch
        // scripts) WAIT for a process they start to exit before returning — this is true for GUI
        // apps too, not just console apps (the reason `notepad`/`calc` seem to return instantly is
        // that those are stub launchers that exit immediately). So a plain `notepadredo file.txt`
        // would block the prompt until the editor window is closed.
        //
        // To avoid that we relaunch ourselves as a process detached from any console (via
        // ShellExecute, so it isn't a console child) and exit this one immediately: whatever launched
        // us — cmd, a batch file, another script — unblocks at once while the real editor runs in the
        // detached instance. We can't cheaply tell "launched from a console that will wait" apart from
        // "launched by Explorer" (a GUI process isn't attached to a console window, so GetConsoleWindow
        // is 0 in BOTH cases even though cmd still waits on the process handle), so we always relaunch.
        // Explorer/double-click launches don't wait anyway and simply pay one cheap, invisible extra
        // process hop (the throwaway original never shows a window). We skip the relaunch only when:
        //   • it's a --quit* signalling mode — build.bat launches those and MUST block on their exit
        //     code (e.g. --quit-prompt returns 2 when the user cancels), so they must not detach;
        //   • we're already the detached relaunch (marked with --detached).
        bool signallingQuit = e.Args.Contains("--quit")
                           || e.Args.Contains("--quit-save")
                           || e.Args.Contains("--quit-prompt");
        if (!signallingQuit && !e.Args.Contains("--detached"))
        {
            if (TryRelaunchDetached(e.Args))
            {
                Shutdown();
                return;
            }
            // Relaunch failed (already logged): fall through and run in-place — a blocking window is
            // far better than opening nothing.
        }

        // Pick a visual theme from the executable's own filename, so a single build can be shipped
        // under several names (NotepadRedo-Graphite.exe, -Sunset.exe) to compare looks. The plain
        // "NotepadRedo.exe" gets the Fluent theme by default.
        ApplyThemeFromExeName();

        // Log a full traceback for every unhandled exception. UI-thread exceptions are logged
        // and swallowed so a transient bug doesn't destroy the user's unsaved work; truly fatal
        // (non-UI) exceptions are logged on the way down.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Log($"FATAL AppDomain.UnhandledException (terminating={args.IsTerminating})",
                         args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Log("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        // Signalling launch: tell every running instance to save/park its work and exit, then exit
        // ourselves without ever showing a window. Used by build.bat before a redeploy.
        //   --quit-prompt : each instance prompts to save unsaved work (Yes/No/Cancel); we block
        //                   until the user answers and every instance exits. Exit code 2 means the
        //                   user cancelled (an instance was left open) so the caller can abort.
        //   --quit-save   : titled docs saved to disk, untitled parked in crash recovery (silent).
        //   --quit        : everything parked in crash recovery (nothing written to its file).
        // Check the more specific flags first so "--quit-save"/"--quit-prompt" aren't swallowed by
        // the "--quit" case.
        if (e.Args.Contains("--quit-prompt"))
        {
            var result = IpcServer.QuitAllSiblingsInteractive();
            Shutdown(result == IpcServer.QuitResult.Cancelled ? 2 : 0);
            return;
        }
        if (e.Args.Contains("--quit-save"))
        {
            IpcServer.QuitAllSiblingsAndSave();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--quit"))
        {
            IpcServer.QuitAllSiblings();
            Shutdown();
            return;
        }

        // Resolve every file arg to an absolute path *here*, while this process's working directory
        // is still the one the user launched from. A relative name (e.g. "notes.txt") otherwise gets
        // handed over IPC to a sibling instance that has a different cwd, which would resolve it
        // against the wrong folder — opening/creating the file in the wrong place.
        var files = e.Args
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .Select(ToAbsolutePath)
            .ToList();
        bool blankRequested = e.Args.Contains("--new");

        if (files.Count > 0 && !blankRequested)
        {
            var remaining = new List<string>();
            foreach (var f in files)
            {
                // Already open somewhere? Just focus that tab/instance.
                if (IpcServer.TryFocusInSibling(f))
                    continue;
                // Tab mode: hand the file to the existing instance so it opens as a new tab there
                // (rather than spawning a second window). Instance mode: fall through and open here.
                if (!AppSettings.Current.OpenInNewInstance && IpcServer.OpenInSibling(f))
                    continue;
                remaining.Add(f);
            }

            // Everything was routed to a sibling — exit without ever showing a window.
            if (remaining.Count == 0)
            {
                Shutdown();
                return;
            }
            files = remaining;
        }

        _ipc = new IpcServer();
        _ipc.Start();

        // Declare ourselves a .txt handler for the current user (idempotent, no admin), so Windows
        // offers "Always use this app" and lists us in Settings > Default apps. Doesn't force the
        // default — the user still picks it. Only runs on a real UI launch, not the --quit* modes.
        FileAssociation.EnsureTxtRegistered();

        var window = new MainWindow();
        window.Show();
        window.Initialize(files, blankRequested);
    }

    /// <summary>
    /// Resolve a possibly-relative command-line path to an absolute one against the current working
    /// directory. Falls back to the original string if the path is malformed.
    /// </summary>
    private static string ToAbsolutePath(string p)
    {
        try { return Path.GetFullPath(p); }
        catch { return p; }
    }

    /// <summary>
    /// Merge a palette (overriding the base colours) plus the themed control styles. The palette is
    /// chosen from this executable's filename; an unmatched name (plain NotepadRedo.exe) uses Fluent.
    /// </summary>
    private void ApplyThemeFromExeName()
    {
        string name;
        try { name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "") ?? ""; }
        catch { return; }

        string? palette = null;
        if (name.Contains("Fluent", StringComparison.OrdinalIgnoreCase))
            palette = "Themes/Palette.Fluent.xaml";
        else if (name.Contains("Graphite", StringComparison.OrdinalIgnoreCase))
            palette = "Themes/Palette.Graphite.xaml";
        else if (name.Contains("Sunset", StringComparison.OrdinalIgnoreCase))
            palette = "Themes/Palette.Sunset.xaml";

        // Default look for the plain "NotepadRedo.exe": Fluent. The named variant exes still pick
        // their own palette above; only an unmatched name falls through to this default.
        palette ??= "Themes/Palette.Fluent.xaml";

        try
        {
            Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri(palette, UriKind.Relative) });
            Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri("Themes/Controls.xaml", UriKind.Relative) });
        }
        catch (Exception ex)
        {
            CrashLog.Log($"Failed to apply theme '{palette}'", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Log("DispatcherUnhandledException (UI thread)", e.Exception);
        // Keep the app alive: the user's open documents are worth more than crashing on a
        // recoverable UI glitch. The full traceback is already on disk for diagnosis.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ipc?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Relaunch this executable as a process detached from the current console, carrying the same
    /// command-line arguments plus a "--detached" sentinel so the new instance doesn't detach again.
    /// Started via ShellExecute (UseShellExecute = true) so it is NOT a console child of the launching
    /// cmd/batch — the caller can then exit right away and free the prompt. The working directory is
    /// preserved so relative file paths on the command line still resolve. Returns true when the new
    /// process was started (so the caller should exit); false on failure (so the caller runs in-place).
    /// </summary>
    private static bool TryRelaunchDetached(string[] args)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,                              // detach from the parent console
                WorkingDirectory = Directory.GetCurrentDirectory(),  // keep relative paths resolving
            };
            psi.ArgumentList.Add("--detached");
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            var child = Process.Start(psi);
            if (child is not null)
            {
                // Let the freshly launched instance legitimately take the foreground (we still own it).
                try { AllowSetForegroundWindow(child.Id); } catch { /* best-effort */ }
            }
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Log("Detached relaunch failed; starting in-place instead", ex);
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
