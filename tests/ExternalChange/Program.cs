// Regression tests for the "changed on disk" prompt.
//
// Each test drives a real EditorView, with its real FileSystemWatcher, through the prompt while a
// second writer — standing in for another program, or another computer on a network drive — keeps
// changing the file. The prompt is answered by clicking its buttons in code; the host window is
// parked off-screen. Settings are changed in memory only and never saved.
//
//   dotnet run --project tests\ExternalChange               the editor and the writer share a path
//   dotnet run --project tests\ExternalChange -- --share    and again with the editor reading through
//                                                           \\localhost\<drive>$ (SMB) while the
//                                                           writer goes straight to the disk, the way
//                                                           another machine's writes reach a share
//
// Exit code 0 when every test passes.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using NotepadRedo;

internal static class Program
{
    private const string PromptTitle = "File changed on disk";
    private const string SavedTitle = "Saved";

    // Prompt buttons, in the order ResolveExternalChange lists them.
    private const int Reload = 0, KeepMine = 1, SaveMineThenReload = 2, SaveDiskThenKeepMine = 3, Merge = 4;

    // Distinct lengths as well as distinct text, so no two versions can share a stamp.
    private const string V0 = "v0\r\n";
    private const string V1 = "v1 one\r\n";
    private const string V2 = "v2 two two\r\n";
    private const string V3 = "v3 three three three\r\n";

    private sealed class TestFailure(string message) : Exception(message);

    /// <summary>One open document plus the two names its file goes by.</summary>
    /// <param name="EditorPath">Where the editor opens it.</param>
    /// <param name="WriterPath">Where the "other program" writes.</param>
    /// <param name="Dir">The folder the test owns (on the writer's side), deleted afterwards.</param>
    private sealed record Doc(EditorView View, Window Host, string EditorPath, string WriterPath, string Dir)
    {
        public string Text => View.SerializeDocument().CurrentText;
        public void WriteExternally(string text) => File.WriteAllText(WriterPath, text);
    }

    /// <param name="Load">How the document comes to be open, once the file holds <see cref="V0"/>;
    /// by default the user opens it.</param>
    /// <param name="SmbOnly">The test needs a watcher error, and only over SMB does deleting the
    /// watched folder produce one (a local watcher outlives its folder).</param>
    private sealed record Test(string Name, Func<Doc, Task> Body, Action<Doc>? Load = null, bool SmbOnly = false);

    private static readonly Test[] Tests =
    {
        new("Reload loads the newest version, not the one that raised the prompt", ReloadLoadsNewest),
        new("Keep mine covers a change that landed while the prompt was up", KeepMineCoversNewest),
        new("Saving the disk version saves the newest one", SaveDiskSavesNewest),
        new("Saving mine, then reloading, reloads the newest version", SaveMineThenReloadLoadsNewest),
        new("The merge viewer opens on the newest version", MergeShowsNewest),
    };

    [STAThread]
    private static int Main(string[] args)
    {
        // The other program must be able to write (no deny-write lock), and nothing may land in the
        // user's recovery or history folders.
        var s = AppSettings.Current;
        s.WatchExternalChanges = true;
        s.LockFileWhileOpen = false;
        s.AutosaveSeconds = 0;
        s.PersistHistory = false;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int failures = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                foreach (var loc in Locations(args.Contains("--share")))
                    foreach (var test in Tests)
                        if ((!test.SmbOnly || loc.Name == "smb")
                            && !await Run($"[{loc.Name}] {test.Name}", test, loc.WriterDir, loc.EditorDir))
                            failures++;
            }
            catch (Exception ex) { Console.WriteLine(ex); failures++; }
            finally { app.Shutdown(); }
        });
        app.Run();

        Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures} test(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The folders to run every test in: a local one, and optionally one reached over SMB.
    /// The writer's and the editor's names for it are given separately, because over SMB they are
    /// two names for one folder.</summary>
    private static IEnumerable<(string Name, string WriterDir, string EditorDir)> Locations(bool share)
    {
        string temp = Path.GetTempPath();
        yield return ("local", temp, temp);
        if (!share)
            yield break;

        // The admin share of the drive the test binaries are on, which is a local fixed drive.
        string onDrive = AppContext.BaseDirectory.TrimEnd('\\');
        string unc = @"\\localhost\" + onDrive[0] + "$" + onDrive.Substring(2);
        if (Directory.Exists(unc))
            yield return ("smb", onDrive, unc);
        else
            Console.WriteLine($"SKIP smb: {unc} is not reachable");
    }

    private static async Task<bool> Run(string name, Test test, string writerRoot, string editorRoot)
    {
        string leaf = "nr-extchange-" + Guid.NewGuid().ToString("N")[..8];
        string dir = Path.Combine(writerRoot, leaf);
        Directory.CreateDirectory(dir);
        string writerPath = Path.Combine(dir, "doc.txt");
        File.WriteAllText(writerPath, V0);

        var view = new EditorView();
        var host = new Window
        {
            Content = view, Width = 900, Height = 600,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false,
        };
        host.Show();
        var doc = new Doc(view, host, Path.Combine(editorRoot, leaf, "doc.txt"), writerPath, dir);
        try
        {
            (test.Load ?? (d => d.View.LoadFile(d.EditorPath)))(doc);
            await Task.Delay(500);   // let the watcher settle before the first external write
            await test.Body(doc);
            Console.WriteLine($"ok   {name}");
            return true;
        }
        catch (TestFailure f)
        {
            Console.WriteLine($"FAIL {name}\n     {f.Message}");
            return false;
        }
        finally
        {
            // A failure can leave a prompt open, with the view's handler parked inside it.
            foreach (var w in Application.Current.Windows.OfType<Window>().Where(w => w != host).ToList())
                w.Close();
            await Task.Delay(300);
            view.Dispose();
            host.Close();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ===================== The tests =====================

    private static async Task ReloadLoadsNewest(Doc d)
    {
        var prompt = await RaisePromptThenWriteAgain(d);
        Choose(prompt, Reload);
        await ExpectNoPrompt("after Reload");
        Expect(d.Text == V2, $"Reload loaded {Show(d.Text)}, but the file holds {Show(V2)}");
        Expect(!d.View.IsDirty, "the reloaded document is marked dirty");
        await ExpectLaterChangesStillNoticed(d);
    }

    private static async Task KeepMineCoversNewest(Doc d)
    {
        var prompt = await RaisePromptThenWriteAgain(d);
        Choose(prompt, KeepMine);
        await ExpectNoPrompt("after Keep mine, which answers for the file as it was when answered");
        Expect(d.Text == V0, $"Keep mine left {Show(d.Text)} in the editor, not {Show(V0)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    private static async Task SaveDiskSavesNewest(Doc d)
    {
        var prompt = await RaisePromptThenWriteAgain(d);
        Choose(prompt, SaveDiskThenKeepMine);
        await DismissSavedNotice();
        string saved = File.ReadAllText(Directory.GetFiles(d.Dir, "doc.disk-*.txt").Single());
        Expect(saved == V2, $"the disk version was saved as {Show(saved)}, but the file holds {Show(V2)}");
        await ExpectNoPrompt("after saving the disk version");
        Expect(d.Text == V0, $"the editor holds {Show(d.Text)}, not the kept {Show(V0)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    private static async Task SaveMineThenReloadLoadsNewest(Doc d)
    {
        var prompt = await RaisePromptThenWriteAgain(d);
        Choose(prompt, SaveMineThenReload);
        await DismissSavedNotice();
        string mine = File.ReadAllText(Directory.GetFiles(d.Dir, "doc.mine-*.txt").Single());
        Expect(mine == V0, $"my version was saved as {Show(mine)}, not {Show(V0)}");
        await ExpectNoPrompt("after saving mine and reloading");
        Expect(d.Text == V2, $"Reload loaded {Show(d.Text)}, but the file holds {Show(V2)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    private static async Task MergeShowsNewest(Doc d)
    {
        var prompt = await RaisePromptThenWriteAgain(d);
        Choose(prompt, Merge);
        DiffMergeWindow? merge = null;
        for (int waited = 0; merge is null && waited < 5_000; waited += 50)
        {
            await Task.Delay(50);
            merge = Application.Current.Windows.OfType<DiffMergeWindow>().FirstOrDefault(w => w.IsVisible);
        }
        Expect(merge is not null, "the merge viewer never opened");
        var right = ((RichTextBox)merge!.FindName("RightBox")).Document;
        string onDisk = new TextRange(right.ContentStart, right.ContentEnd).Text;
        Expect(onDisk.Contains(V2.TrimEnd()) && !onDisk.Contains(V1.TrimEnd()),
            $"the viewer's on-disk side shows {Show(onDisk)}, but the file holds {Show(V2)}");
        merge.Close();   // without saving: the editor keeps its own text
        await ExpectNoPrompt("after closing the merge viewer");
        Expect(d.Text == V0, $"the editor holds {Show(d.Text)}, not its own {Show(V0)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    // ===================== Steps =====================

    /// <summary>The reported sequence: another program changes the file, the prompt comes up, and
    /// the file changes again before anyone answers it.</summary>
    private static async Task<Window> RaisePromptThenWriteAgain(Doc d)
    {
        d.WriteExternally(V1);
        var prompt = await ExpectPrompt("after the first external write");
        d.WriteExternally(V2);
        await Task.Delay(1500);   // long enough for the watcher to report it while the prompt is up
        return prompt;
    }

    /// <summary>Whatever was answered, the next change made elsewhere must still raise the prompt and
    /// still reload.</summary>
    private static async Task ExpectLaterChangesStillNoticed(Doc d)
    {
        d.WriteExternally(V3);
        Choose(await ExpectPrompt("for a change made after the prompt was answered"), Reload);
        await ExpectNoPrompt("after the second Reload");
        Expect(d.Text == V3, $"the second Reload loaded {Show(d.Text)}, not {Show(V3)}");
    }

    private static async Task<Window> ExpectPrompt(string when)
    {
        for (int waited = 0; waited < 10_000; waited += 50)
        {
            if (Find(PromptTitle) is Window w)
                return w;
            await Task.Delay(50);
        }
        throw new TestFailure($"no prompt appeared {when}");
    }

    private static async Task ExpectNoPrompt(string when)
    {
        await Task.Delay(2000);
        if (Find(PromptTitle) is not null)
            throw new TestFailure($"a prompt appeared {when}");
    }

    private static async Task DismissSavedNotice()
    {
        for (int waited = 0; waited < 5_000; waited += 50)
        {
            if (Find(SavedTitle) is Window w) { w.Close(); await Task.Delay(100); return; }
            await Task.Delay(50);
        }
        throw new TestFailure("the \"Saved to\" notice never appeared");
    }

    private static Window? Find(string title) =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == title && w.IsVisible);

    private static void Choose(Window prompt, int index) =>
        ((Panel)prompt.Content).Children.OfType<Button>().ElementAt(index)
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Expect(bool condition, string failure)
    {
        if (!condition)
            throw new TestFailure(failure);
    }

    private static string Show(string text) => "\"" + text.TrimEnd() + "\"";
}
