using System.Windows;

namespace TreeNotepad;

public partial class App : Application
{
    private IpcServer? _ipc;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var files = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        bool blankRequested = e.Args.Contains("--new");

        // If every file passed on the command line is already open in another instance, hand
        // focus over and exit without ever showing a window (no flash, no duplicate).
        if (files.Count > 0 && !blankRequested)
        {
            var remaining = files.Where(f => !IpcServer.TryFocusInSibling(f)).ToList();
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

    protected override void OnExit(ExitEventArgs e)
    {
        _ipc?.Dispose();
        base.OnExit(e);
    }
}
