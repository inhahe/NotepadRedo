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

        // The app ends when its last editor window closes (MainWindow.OnClosed), or where startup says
        // so; never because some window was the last to close. WPF's default would end it when the
        // window shown while copying the exe closes, and a window whose constructor failed is never
        // closed, so it would keep the process running with nothing on screen.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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
            // Raised on the finalizer thread; telling the user belongs on the UI thread.
            Dispatcher.BeginInvoke(new Action(() => ReportSwallowed(args.Exception)));
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

        bool blankRequested = e.Args.Contains("--new");
        if (FilesLeftAfterSiblings(e.Args) is not List<string> files)
        {
            // Everything was handed to a sibling: exit without ever showing a window.
            Shutdown();
            return;
        }

        // Running from a local copy, every window takes the taskbar identity of the exe the user
        // started; the process's own part of that has to come before the first window.
        if (LocalCopy.IsRelocated && LocalCopy.OriginalExe is string original)
            TaskbarIdentity.ApplyToEveryWindow(original);

        // Are we the first NotepadRedo process? Only the first instance restores the previous session;
        // secondary instances (new-instance mode) just open what they were launched with. Checked here,
        // before we start our own pipe server, but it counts processes by name so ordering is moot.
        bool firstInstance = !IpcServer.AnyOtherInstanceRunning();

        _ipc = new IpcServer();
        _ipc.Start();

        // Declare ourselves a .txt handler for the current user (no admin), so Windows offers
        // "Always use this app" and lists us in Settings > Default apps, unless a NotepadRedo that
        // still exists is registered already. Doesn't force the default: the user still picks it.
        // Only on a real UI launch, not the --quit* modes. Off the UI thread, because checking the
        // registered exe can stall for as long as an offline share takes to give up.
        Task.Run(FileAssociation.EnsureTxtRegistered);

        MainWindow window;
        try
        {
            window = new MainWindow();
            window.Show();
        }
        catch (Exception ex)
        {
            // No window means nothing whose closing would end the process: end it here, even if
            // saying why fails as well (out of window handles, say, which would fail both).
            CrashLog.Log("The main window couldn't be created", ex);
            try
            {
                ThemedDialog.Show(null, $"NotepadRedo couldn't start.\n\n{ex.GetType().Name}: {ex.Message}\n"
                    + $"Details are in {CrashLog.FilePath}", "NotepadRedo", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception shown) { CrashLog.Log("Couldn't report the failed start", shown); }
            Shutdown(1);
            return;
        }
        // A launch from a console (cmd/batch) leaves that console owning the foreground, and because
        // we relaunch detached this window would otherwise come up *behind* it — hiding any prompt
        // Initialize puts up (notably "create a new file?", which then looks like a silent hang).
        // The parent granted this process foreground rights via AllowSetForegroundWindow, so claim
        // them now, before those dialogs appear.
        window.ForceForeground();
        window.Initialize(files, blankRequested, firstInstance);

        // Keep this build's own extracted copy from being swept while it runs, then delete the
        // extracted and local copies of earlier builds that nothing uses any more.
        OldBuilds.HoldOwnExtraction();
        OldBuilds.SweepSoon();
    }

    /// <summary>Offer the files named in <paramref name="args"/> to the instances already running,
    /// and return those left for this one to open, or null if nothing is left for it to do.</summary>
    /// <remarks>A file already open in a sibling is focused there. In tab mode a sibling takes the
    /// rest, and a bare launch (no files) brings the existing editor forward instead of opening a
    /// second, empty window. In new-instance mode, or with <c>--new</c>, the files stay here.</remarks>
    private static List<string>? FilesLeftAfterSiblings(string[] args)
    {
        // Resolve every file arg to an absolute path *here*, while this process's working directory
        // is still the one the user launched from. A relative name (e.g. "notes.txt") otherwise gets
        // handed over IPC to a sibling instance that has a different cwd, which would resolve it
        // against the wrong folder — opening/creating the file in the wrong place.
        var files = args
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .Select(ToAbsolutePath)
            .ToList();
        bool blankRequested = args.Contains("--new");

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
            return remaining.Count == 0 ? null : remaining;
        }
        // Bare "notepadredo" with no filenames while an instance is already running: the user
        // wants the editor they already have, not a second empty window on top of it. (In *new
        // instance* mode that's exactly what the user asked us not to do, and "--new" is an explicit
        // request for a fresh blank, so both fall through to opening normally.)
        if (files.Count == 0 && !blankRequested && !AppSettings.Current.OpenInNewInstance
            && IpcServer.PresentSibling())
            return null;
        return files;
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
        ReportSwallowed(e.Exception);
    }

    /// <summary>Tell the user about <paramref name="ex"/>, which the app caught and carried on from:
    /// in the status bar always, and by a restart notice if the program itself failed to load.</summary>
    /// <remarks>This is code that may never have run before, compiled from the very files whose
    /// damage it may be reporting. If it fails, the failure must not escape: out of the UI-thread
    /// handler it would end the process.</remarks>
    private void ReportSwallowed(Exception ex)
    {
        try { AdviseRestartIfLoadFailure(ex); }
        catch (Exception inner) { CrashLog.Log("Couldn't advise a restart", inner); }
        try { NoteInStatusBar(ex); }
        catch (Exception inner) { CrashLog.Log("Couldn't show the error in the status bar", inner); }
    }

    /// <summary>Show <paramref name="ex"/> in the status bar of the window the user is in. Without
    /// it, a command whose code failed simply did nothing, and only crash.log knew why.</summary>
    private static void NoteInStatusBar(Exception ex)
    {
        if (ex is AggregateException { InnerExceptions.Count: > 0 } aggregate)
            ex = aggregate.InnerExceptions[0];
        string firstLine = ex.Message.Split('\n')[0].Trim();
        // Fully qualified: in here, MainWindow is Application's property.
        NotepadRedo.MainWindow.Foremost()?.ShowErrorNotice($"{ex.GetType().Name}: {firstLine}");
    }

    /// <summary>Set once the user has been told to restart. The advice never changes, and a failure
    /// raised on every layout pass must not bring the dialog back each time.</summary>
    private bool _restartAdvised;

    /// <summary>If <paramref name="ex"/> says part of the program couldn't be loaded, tell the user,
    /// once, to restart NotepadRedo. For a failure caught where it happens as well as one that
    /// reaches the UI-thread handler.</summary>
    internal void AdviseRestartIfLoadFailure(Exception ex)
    {
        if (_restartAdvised || ProgramLoadFailure(ex) is not Exception failure)
            return;
        _restartAdvised = true;
        // Once the failed operation has unwound: it may have been a layout pass, and no dialog belongs
        // in the middle of one. A shutdown drops the posted call, so then nothing is shown.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Whatever window is active, a dialog included, else a visible editor window. Never one
            // that was never shown (whose constructor failed, say): setting that as the owner would
            // throw. With no owner the dialog is centred on the screen.
            var owner = Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                     ?? (NotepadRedo.MainWindow.Foremost() is { IsVisible: true } editor ? editor : null);
            ThemedDialog.Show(owner,
                "Part of NotepadRedo couldn't be loaded, so what you just did may not have worked.\n\n"
                + "This happens when the program's files change while it is running, after a .NET "
                + "update for instance. Save your work and restart NotepadRedo.\n\n"
                + $"{failure.GetType().Name}: {failure.Message}\nDetails are in {CrashLog.FilePath}",
                "NotepadRedo needs a restart", MessageBoxButton.OK, MessageBoxImage.Warning);
        }), DispatcherPriority.Background);
    }

    /// <summary>The exception among <paramref name="ex"/> and its inner exceptions (each of an
    /// aggregate's) saying that part of the program itself couldn't be loaded, or null if none does.</summary>
    /// <remarks>Such a failure is no bug in what the user did. The program's files changed under the
    /// running process (a .NET update deleted the runtime it started on, say), and it will go on
    /// failing at that point until it is restarted. Swallowed silently, it makes a command look as if
    /// it did nothing at all.</remarks>
    internal static Exception? ProgramLoadFailure(Exception? ex)
    {
        switch (ex)
        {
            case null:
                return null;
            case AggregateException aggregate:
                return aggregate.InnerExceptions.Select(ProgramLoadFailure).FirstOrDefault(f => f is not null);
            // An export that Windows doesn't have: a restart changes nothing. (Though .NET counts it a
            // type-load failure, as it does a missing DLL. That one stays in: the app imports only from
            // DLLs every Windows has, so a missing one belongs to the runtime, deleted by an update.)
            case EntryPointNotFoundException:
                break;
            case BadImageFormatException or InvalidProgramException or TypeLoadException or MissingMemberException:
                return ex;
            case FileNotFoundException { FileName: string name } when IsAssemblyName(name):
                return ex;
            case FileLoadException { FileName: string name } when IsAssemblyName(name):
                return ex;
        }
        return ProgramLoadFailure(ex.InnerException);
    }

    /// <summary>Whether a load failure's file name is an assembly's display name
    /// ("System.Foo, Version=8.0.0.0, …") rather than the path of a document.</summary>
    private static bool IsAssemblyName(string name) =>
        name.Contains(", Version=") && name.IndexOfAny(['\\', '/']) < 0;

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
    /// process was started, or nothing was left to start it for (so the caller should exit); false on
    /// failure (so the caller runs in-place).
    /// </summary>
    private static bool TryRelaunchDetached(string[] args)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;
        if (LocalCopy.Needed(exe))
        {
            // Started from a share or removable drive, a build whose code stays in its exe runs from
            // a local copy instead (see LocalCopy), told which exe the user started.
            string? copy = LocalCopy.Find(exe);
            // A first copy of a new version reads ~160 MB over the network. Not for a launch that
            // only hands its files to an instance already running: hand them over first. (The
            // relaunched instance offers what's left again, which costs only the asking.)
            if (copy is null && FilesLeftAfterSiblings(args) is null)
                return true;
            copy ??= MakeLocalCopy(exe);
            Environment.SetEnvironmentVariable(LocalCopy.OriginVariable, exe);
            if (copy is not null)
            {
                if (StartDetached(copy, args))
                    return true;
                // Swept between being found and started: once more. (One that is there but won't
                // start, blocked by a policy say, would only fail again.)
                if (!File.Exists(copy) && MakeLocalCopy(exe) is string again && StartDetached(again, args))
                    return true;
            }
            Environment.SetEnvironmentVariable(LocalCopy.OriginVariable, null);
            CrashLog.Log($"No local copy of {exe} could be started; it runs from where it is, so replacing it "
                         + "there while this instance runs can break the instance", null);
        }
        return StartDetached(exe, args);
    }

    private static string? MakeLocalCopy(string exe) =>
        WhileShowing("Copying NotepadRedo to this computer before it starts…", () => LocalCopy.Make(exe));

    /// <summary>Start <paramref name="exe"/> with <paramref name="args"/> and <c>--detached</c>, detached
    /// from any console (see <see cref="TryRelaunchDetached"/>). Returns false, having logged why, if
    /// it couldn't be started.</summary>
    private static bool StartDetached(string exe, string[] args)
    {
        try
        {
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
            CrashLog.Log($"Starting {exe} detached failed", ex);
            return false;
        }
    }

    /// <summary>Run <paramref name="work"/> on the thread pool, showing <paramref name="message"/>
    /// in a small window meanwhile, and return its result. The window keeps responding: messages are
    /// pumped while the work runs, so Windows doesn't call it hung.</summary>
    private static T WhileShowing<T>(string message, Func<T> work)
    {
        var note = new Window
        {
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Title = "NotepadRedo",
            Content = new System.Windows.Controls.TextBlock { Text = message, Margin = new Thickness(24, 18, 24, 18) },
        };
        note.Show();
        try
        {
            var task = Task.Run(work);
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            return task.GetAwaiter().GetResult();
        }
        finally { note.Close(); }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
