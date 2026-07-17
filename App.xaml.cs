using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TreeNotepad;

public partial class App : Application
{
    private IpcServer? _ipc;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        // Signalling launch: tell every running instance to autosave-to-recovery and exit, then
        // exit ourselves without ever showing a window. Used by build.bat before a redeploy.
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

        var window = new MainWindow();
        window.Show();
        window.Initialize(files, blankRequested);
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
