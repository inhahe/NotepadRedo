using System.Windows;

namespace TreeNotepad;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        window.Show();
        window.Initialize(e.Args);
    }
}
