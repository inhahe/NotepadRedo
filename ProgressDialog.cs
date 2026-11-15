using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NotepadRedo;

/// <summary>
/// Run a long job (saving or copying a large file) on the thread pool while the UI waits for it.
/// A job that finishes within a moment shows nothing; a longer one shows a modal window with a
/// progress bar, which keeps the app's windows from taking input meanwhile, and keeps the message
/// loop running so Windows doesn't call the app hung.
/// </summary>
internal static class ProgressDialog
{
    /// <summary>How long a job may run before the window appears.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    /// <summary>Run <paramref name="work"/> and wait for it.</summary>
    /// <param name="cancellable">Offer a Cancel button, which cancels the job's token.</param>
    /// <returns>The job's result; false when it was cancelled.</returns>
    /// <exception cref="Exception">Whatever the job threw, other than its cancellation.</exception>
    public static bool Run(Window? owner, string title, string message,
                           Action<IProgress<double>, CancellationToken> work, bool cancellable)
    {
        using var cts = new CancellationTokenSource();
        double fraction = 0;
        var reporter = new Reporter(f => Volatile.Write(ref fraction, f));
        var task = Task.Run(() => work(reporter, cts.Token), cts.Token);

        if (!task.Wait(Grace))
        {
            var bar = new ProgressBar { Height = 16, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 0) };
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
                Foreground = Brush("Theme.EditorFg", Brushes.Black),
            });
            panel.Children.Add(bar);
            var dlg = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                MinWidth = 380,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                Owner = owner is { IsVisible: true } ? owner : null,
                Background = Brush("Theme.WindowBg", Brushes.White),
                Content = panel,
            };
            if (cancellable)
            {
                var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 12, 0, 0),
                                          HorizontalAlignment = HorizontalAlignment.Right };
                cancel.Click += (_, _) => { cts.Cancel(); cancel.IsEnabled = false; };
                panel.Children.Add(cancel);
            }
            bool done = false;
            // Closing the window by hand would leave the job running with nobody waiting: refuse,
            // and treat it as Cancel where there is one.
            dlg.Closing += (_, e) =>
            {
                if (!done)
                {
                    e.Cancel = true;
                    if (cancellable) cts.Cancel();
                }
            };
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                bar.Value = Math.Clamp(Volatile.Read(ref fraction), 0, 1);
                if (task.IsCompleted)
                {
                    timer.Stop();
                    done = true;
                    dlg.Close();
                }
            };
            dlg.Loaded += (_, _) => timer.Start();
            dlg.ShowDialog();
        }

        try
        {
            task.GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return false;
        }
    }

    private sealed class Reporter(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private static Brush Brush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
