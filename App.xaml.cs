using System.IO;
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

        var files = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
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
}
