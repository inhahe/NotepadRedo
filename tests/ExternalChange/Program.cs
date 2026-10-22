// Regression tests for the "changed on disk" prompt.
//
// Each test drives a real EditorView, with its real FileSystemWatcher, through the prompt while a
// second writer — standing in for another program, or another computer on a network drive — keeps
// changing the file. The prompt is answered by clicking its buttons in code; the host window is
// parked off-screen. The app's data folder is pointed at a scratch folder (NOTEPADREDO_DATA_DIR), so
// the user's own settings, crash recovery and history are never read or written.
//
//   dotnet run --project tests\ExternalChange               the editor and the writer share a path
//   dotnet run --project tests\ExternalChange -- --share    and again with the editor reading through
//                                                           \\localhost\<drive>$ (SMB) while the
//                                                           writer goes straight to the disk, the way
//                                                           another machine's writes reach a share
//   ... -- --filter=merge                                   only the tests whose name contains "merge"
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
    private const string SaveFailedTitle = "Save failed";
    private const string ChangedAgainTitle = "File changed again";   // the merge viewer's own question

    // Prompt buttons, in the order ResolveExternalChange lists them.
    private const int Reload = 0, KeepMine = 1, SaveMineThenReload = 2, SaveDiskThenKeepMine = 3, Merge = 4;

    // Distinct lengths as well as distinct text, so no two versions can share a stamp.
    private const string V0 = "v0\r\n";
    private const string V1 = "v1 one\r\n";
    private const string V2 = "v2 two two\r\n";
    private const string V3 = "v3 three three three\r\n";
    /// <summary>A recovered document's unsaved text: its saved text, <see cref="V0"/>, plus an edit.</summary>
    private const string Mine = V0 + "an unsaved edit\r\n";

    private sealed class TestFailure(string message) : Exception(message);

    /// <summary>One open document plus the two names its file goes by.</summary>
    /// <param name="EditorPath">Where the editor opens it.</param>
    /// <param name="WriterPath">Where the "other program" writes.</param>
    /// <param name="Dir">The folder the test owns (on the writer's side), deleted afterwards.</param>
    private sealed record Doc(EditorView View, Window Host, string EditorPath, string WriterPath, string Dir)
    {
        public string Text => TextOf(View);
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
        new("Keep mine asks again about a version that landed while the prompt was up", KeepMineAsksAboutNewer),
        new("Saving the disk version saves the newest one", SaveDiskSavesNewest),
        new("A copy that fails to save leaves the change unanswered", FailedCopyLeavesChangeUnanswered),
        new("Saving mine, then reloading, reloads the newest version", SaveMineThenReloadLoadsNewest),
        new("The merge viewer opens on the newest version", MergeShowsNewest),
        new("The merge viewer follows the file even back to the editor's own text", MergeFollowsDiskToOurText),
        new("Only one document asks at a time", OneQuestionAtATime),
        new("Changes are still noticed after the watcher fails", WatcherRecoversFromError, SmbOnly: true),
        new("A folder that couldn't be watched is watched once it appears", WatchedOnceFolderAppears,
            d => { Directory.Delete(d.Dir, recursive: true); Recover(d); }),
        new("A recovered document whose file was edited since the crash raises the prompt",
            RecoveredAfterEditPrompts, d => { d.WriteExternally(V1); Recover(d); }),
        new("A recovered document whose file is untouched raises no prompt", RecoveredUntouchedStaysQuiet, Recover),
        new("Rewriting the file with what we last saved raises no prompt", RewriteWithSavedTextStaysQuiet, Recover),
        new("With watching off, a recovered document raises no prompt", WatchingOffStaysQuiet,
            d => { AppSettings.Current.WatchExternalChanges = false; d.WriteExternally(V1); Recover(d); }),
        new("Saving over a change not yet reported asks first", SavingOverUnreportedChangeAsks, Recover),
        new("A save that may not ask leaves a changed file alone", QuietSaveLeavesChangedFileAlone, Recover),
        new("A change written just after our own save is noticed", ChangeJustAfterOurSaveIsNoticed, Recover),
        new("A save made while the file's question is open writes nothing", SaveDuringOpenQuestionRefuses, Recover),
        new("A re-change the merge viewer never had answered is asked about", MergeLeavesUnansweredChangeOpen),
        new("The merge viewer's save leaves a version it never showed alone", MergeSaveLeavesUnseenVersionAlone),
        new("Closing a tab ends its questions", ClosedTabAsksNothing),
        new("A save that may not ask shows no error either", QuietSaveFailureShowsNothing, Recover),
        new("The version Keep mine answered for turning up again raises no prompt", KeptVersionAgainIsQuiet),
        new("Closing Save's question means not now: nothing written, and the next save asks",
            ClosingSavesQuestionMeansNotNow),
        new("Turning watching on checks the file at once", TurningWatchingOnChecksAtOnce,
            d => { AppSettings.Current.WatchExternalChanges = false; d.WriteExternally(V1); Recover(d); }),
        new("A file that comes to hold the editor's text makes the document saved", FileMatchingEditorIsSaved, Recover),
    };

    /// <summary>Recover the document from a crash snapshot taken while the file held <see cref="V0"/>.</summary>
    private static void Recover(Doc d) =>
        d.View.LoadRecovered(new EditorView.RecoveryData(
            Guid.NewGuid().ToString("N"), d.EditorPath, V0, Mine, DateTime.Now));

    [STAThread]
    private static int Main(string[] args)
    {
        // Before any of the app's types is initialised: they read the data folder once. (Naming
        // OverrideVariable initialises nothing, because it is a const.)
        string data = Path.Combine(Path.GetTempPath(), "nr-extchange-data-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.OverrideVariable, data);

        string filter = args.FirstOrDefault(a => a.StartsWith("--filter="))?["--filter=".Length..] ?? "";
        int failures = CheckLoadFailureClassifier(filter) + CheckRegistrationRule(filter);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                foreach (var loc in Locations(args.Contains("--share")))
                {
                    // Asked for and not to be had: a pass that tests nothing must not pass quietly.
                    if (!Directory.Exists(loc.EditorDir))
                    {
                        Console.WriteLine($"FAIL [{loc.Name}] {loc.EditorDir} is not reachable, so nothing was tested there");
                        failures++;
                        continue;
                    }
                    foreach (var test in Tests)
                        if ((!test.SmbOnly || loc.Name == "smb")
                            && test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            && !await Run($"[{loc.Name}] {test.Name}", test, loc.WriterDir, loc.EditorDir))
                            failures++;
                }
            }
            catch (Exception ex) { Console.WriteLine(ex); failures++; }
            finally { app.Shutdown(); }
        });
        app.Run();
        try { Directory.Delete(data, recursive: true); } catch { }

        Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures} test(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Which exceptions <see cref="App.ProgramLoadFailure"/> treats as the program's own files
    /// failing to load, the cue for telling the user to restart. Not about external changes, but this
    /// is the only C# test project. Returns the number of failed cases.</summary>
    private static int CheckLoadFailureClassifier(string filter)
    {
        const string DisplayName = "System.Drawing.Common, Version=8.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51";
        var badImage = new BadImageFormatException("Bad IL range.");
        var missingAssembly = new FileNotFoundException("Could not load file or assembly", DisplayName);
        var dllGone = new DllNotFoundException("Unable to load DLL 'PenImc_cor3.dll'");
        var cases = new (string Name, Exception? Thrown, Exception? Expected)[]
        {
            ("a bad image", badImage, badImage),
            ("a bad image inside a TargetInvocationException",
                new System.Reflection.TargetInvocationException(badImage), badImage),
            ("an assembly that can't be found, inside a XamlParseException",
                new System.Windows.Markup.XamlParseException("xaml", missingAssembly), missingAssembly),
            ("the second of an aggregate's inner exceptions",
                new AggregateException(new InvalidOperationException(), badImage), badImage),
            ("a document that can't be found, though its name has \", Version=\"",
                new FileNotFoundException("gone", @"C:\notes\Plan, Version=2.txt"), null),
            ("a runtime DLL deleted by an update", dllGone, dllGone),
            ("an export Windows doesn't have", new EntryPointNotFoundException("x"), null),
            ("an ordinary bug", new InvalidOperationException("x"), null),
        };
        int failed = 0;
        foreach (var (name, thrown, expected) in cases)
        {
            string label = $"[unit] ProgramLoadFailure: {name}";
            if (!label.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var actual = App.ProgramLoadFailure(thrown);
            if (ReferenceEquals(actual, expected))
                Console.WriteLine($"ok   {label}");
            else
            {
                Console.WriteLine($"FAIL {label}\n     expected {Describe(expected)}, got {Describe(actual)}");
                failed++;
            }
        }
        return failed;

        static string Describe(Exception? e) => e is null ? "none" : e.GetType().Name;
    }

    /// <summary>When a launch takes over the .txt registration (<see cref="FileAssociation.ShouldRegister"/>):
    /// only when none is there, or the registered exe is gone, never because a different copy
    /// started. Checked against a pretend file system. Returns the number of failed cases.</summary>
    private static int CheckRegistrationRule(string filter)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\utils\NotepadRedo.exe" };
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\", @"D:\" };
        var cases = new (string Name, string? Command, bool Expected)[]
        {
            ("nothing registered", null, true),
            ("an empty command", "  ", true),
            ("a copy that still exists", "\"D:\\utils\\NotepadRedo.exe\" \"%1\"", false),
            ("a copy that still exists, unquoted", @"D:\utils\NotepadRedo.exe %1", false),
            ("a copy that is gone", "\"C:\\old\\NotepadRedo.exe\" \"%1\"", true),
            ("a copy on a drive that is offline", "\"Q:\\utils\\NotepadRedo.exe\" \"%1\"", false),
            ("a copy on a share that is offline", "\"\\\\server\\share\\NotepadRedo.exe\" \"%1\"", false),
            ("a command that names no path", "\"<|>\" \"%1\"", true),
            ("a relative path", "NotepadRedo.exe %1", true),
        };
        int failed = 0;
        foreach (var (name, command, expected) in cases)
        {
            string label = $"[unit] FileAssociation.ShouldRegister: {name}";
            if (!label.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            bool actual = FileAssociation.ShouldRegister(command, files.Contains, folders.Contains);
            if (actual == expected)
                Console.WriteLine($"ok   {label}");
            else
            {
                Console.WriteLine($"FAIL {label}\n     expected {expected}, got {actual}");
                failed++;
            }
        }
        return failed;
    }

    /// <summary>The settings every test starts from (in memory only): the other program must be able
    /// to write, so no deny-write lock, and no autosave or persisted history to clutter the scratch
    /// data folder.</summary>
    private static void ResetSettings()
    {
        var s = AppSettings.Current;
        s.WatchExternalChanges = true;
        s.LockFileWhileOpen = false;
        s.AutosaveSeconds = 0;
        s.PersistHistory = false;
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

        // The same temp folder, reached through its drive's admin share. Not the test binaries' own
        // folder: that is on a share already when the project is checked out on one.
        string onDrive = temp.TrimEnd('\\');
        yield return ("smb", onDrive, @"\\localhost\" + onDrive[0] + "$" + onDrive.Substring(2));
    }

    private static async Task<bool> Run(string name, Test test, string writerRoot, string editorRoot)
    {
        ResetSettings();
        string leaf = "nr-extchange-" + Guid.NewGuid().ToString("N")[..8];
        string dir = Path.Combine(writerRoot, leaf);
        Directory.CreateDirectory(dir);
        string writerPath = Path.Combine(dir, "doc.txt");
        File.WriteAllText(writerPath, V0);

        var (view, host) = Host();
        var doc = new Doc(view, host, Path.Combine(editorRoot, leaf, "doc.txt"), writerPath, dir);
        try
        {
            (test.Load ?? (d => d.View.LoadFile(d.EditorPath)))(doc);
            await Task.Delay(500);   // let the watcher settle before the first external write
            await test.Body(doc);
            Console.WriteLine($"ok   {name}");
            return true;
        }
        catch (Exception ex)
        {
            string why = ex is TestFailure ? ex.Message : ex.ToString();
            Console.WriteLine($"FAIL {name}\n     {why}");
            return false;
        }
        finally
        {
            // A failure can leave a prompt open, with the view's handler parked inside it.
            await CloseDialogs();
            view.Dispose();
            host.Close();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>A blank editor in a window of its own, parked off-screen.</summary>
    private static (EditorView View, Window Host) Host()
    {
        var view = new EditorView();
        var host = new Window
        {
            Content = view, Width = 900, Height = 600,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false,
        };
        host.Show();
        return (view, host);
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

    /// <summary>Keep mine answers for the version it was asked about. The one that landed while the
    /// question was up was never seen, so overwriting it on the next save needs a question of its own.</summary>
    private static async Task KeepMineAsksAboutNewer(Doc d)
    {
        var prompt = await RaisePromptThenWriteAgain(d);
        Choose(prompt, KeepMine);
        Choose(await ExpectPrompt("for the version that landed while the first question was up"), KeepMine);
        await ExpectNoPrompt("after Keep mine was given for the newest version too");
        Expect(d.Text == V0, $"Keep mine left {Show(d.Text)} in the editor, not {Show(V0)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    private static async Task SaveDiskSavesNewest(Doc d)
    {
        var prompt = await RaisePromptThenWriteAgain(d);
        Choose(prompt, SaveDiskThenKeepMine);
        await DismissNotice(SavedTitle);
        string saved = File.ReadAllText(Directory.GetFiles(d.Dir, "doc.disk-*.txt").Single());
        Expect(saved == V2, $"the disk version was saved as {Show(saved)}, but the file holds {Show(V2)}");
        await ExpectNoPrompt("after saving the disk version");
        Expect(d.Text == V0, $"the editor holds {Show(d.Text)}, not the kept {Show(V0)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    /// <summary>The copy is named after the second it is made; turning the next half minute's names
    /// into folders makes writing it fail.</summary>
    private static async Task FailedCopyLeavesChangeUnanswered(Doc d)
    {
        d.WriteExternally(V1);
        var prompt = await ExpectPrompt("after the external write");
        var now = DateTime.Now;
        for (int s = 0; s < 30; s++)
            Directory.CreateDirectory(Path.Combine(d.Dir, $"doc.disk-{now.AddSeconds(s):yyyyMMdd-HHmmss}.txt"));
        Choose(prompt, SaveDiskThenKeepMine);
        await DismissNotice(SaveFailedTitle);
        Choose(await ExpectPrompt("again, since the copy the answer depended on was never made"), KeepMine);
        await ExpectNoPrompt("after Keep mine");
        Expect(d.Text == V0, $"the editor holds {Show(d.Text)}, not the kept {Show(V0)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    private static async Task SaveMineThenReloadLoadsNewest(Doc d)
    {
        var prompt = await RaisePromptThenWriteAgain(d);
        Choose(prompt, SaveMineThenReload);
        await DismissNotice(SavedTitle);
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
        var merge = await ExpectMergeViewer();
        string onDisk = OnDiskSide(merge);
        Expect(onDisk.Contains(V2.TrimEnd()) && !onDisk.Contains(V1.TrimEnd()),
            $"the viewer's on-disk side shows {Show(onDisk)}, but the file holds {Show(V2)}");
        merge.Close();   // without saving: the editor keeps its own text
        await ExpectNoPrompt("after closing the merge viewer");
        Expect(d.Text == V0, $"the editor holds {Show(d.Text)}, not its own {Show(V0)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    private static async Task MergeFollowsDiskToOurText(Doc d)
    {
        d.WriteExternally(V1);
        Choose(await ExpectPrompt("after the external write"), Merge);
        var merge = await ExpectMergeViewer();
        d.WriteExternally(V0);   // back to exactly what the editor holds
        Choose(await ExpectWindow(ChangedAgainTitle, "in the merge viewer for the file changing back"), 0);
        await Task.Delay(300);
        string onDisk = OnDiskSide(merge);
        Expect(onDisk.Contains(V0.TrimEnd()) && !onDisk.Contains(V1.TrimEnd()),
            $"the viewer's on-disk side shows {Show(onDisk)}, but the file holds {Show(V0)}");
        merge.Close();
        await ExpectNoPrompt("after closing the merge viewer");
        await ExpectLaterChangesStillNoticed(d);
    }

    /// <summary>Two documents whose files change together: their questions must come one after the
    /// other, not stacked on top of each other.</summary>
    private static async Task OneQuestionAtATime(Doc d)
    {
        string otherWriter = Path.Combine(d.Dir, "other.txt");
        File.WriteAllText(otherWriter, V0);
        var (other, otherHost) = Host();
        try
        {
            other.LoadFile(Path.Combine(Path.GetDirectoryName(d.EditorPath)!, "other.txt"));
            await Task.Delay(500);
            d.WriteExternally(V1);
            File.WriteAllText(otherWriter, V1);
            var first = await ExpectPrompt("after both files changed");
            await Task.Delay(1500);   // time for the second document's check to come due
            int open = Application.Current.Windows.OfType<Window>().Count(w => w.Title == PromptTitle && w.IsVisible);
            Expect(open == 1, $"{open} questions were on screen at once");
            Choose(first, Reload);
            Choose(await ExpectPrompt("for the second document once the first was answered"), Reload);
            await ExpectNoPrompt("after both were answered");
            Expect(d.Text == V1 && TextOf(other) == V1,
                $"the documents hold {Show(d.Text)} and {Show(TextOf(other))}, not {Show(V1)}");
        }
        finally
        {
            await CloseDialogs();
            other.Dispose();
            otherHost.Close();
        }
    }

    /// <summary>Over SMB, deleting the watched folder stops the watcher with an error, which is the
    /// state a dropped network connection leaves it in. The folder then comes back holding a new
    /// version, written while nothing was watching.</summary>
    private static async Task WatcherRecoversFromError(Doc d)
    {
        Directory.Delete(d.Dir, recursive: true);
        await Task.Delay(500);
        Directory.CreateDirectory(d.Dir);
        d.WriteExternally(V1);
        Choose(await ExpectPrompt("for a change made while the watcher was down"), Reload);
        await ExpectNoPrompt("after Reload");
        Expect(d.Text == V1, $"Reload loaded {Show(d.Text)}, not {Show(V1)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    /// <summary>The document was recovered while its folder was missing (an offline share, as far as
    /// the watcher can tell), so no watcher could be made at first.</summary>
    private static async Task WatchedOnceFolderAppears(Doc d)
    {
        Directory.CreateDirectory(d.Dir);
        d.WriteExternally(V1);
        Choose(await ExpectPrompt("once the folder that couldn't be watched came back"), Reload);
        await ExpectNoPrompt("after Reload");
        Expect(d.Text == V1, $"Reload loaded {Show(d.Text)}, not {Show(V1)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    private static async Task RecoveredAfterEditPrompts(Doc d)
    {
        Choose(await ExpectPrompt("for a file edited between the crash and the recovery"), Reload);
        await ExpectNoPrompt("after Reload");
        Expect(d.Text == V1, $"Reload loaded {Show(d.Text)}, but the file holds {Show(V1)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    private static async Task RecoveredUntouchedStaysQuiet(Doc d)
    {
        await ExpectNoPrompt("for a recovered file nobody else has changed");
        Expect(d.Text == Mine, $"the recovered document holds {Show(d.Text)}, not {Show(Mine)}");
        Expect(d.View.IsDirty, "the recovered edit isn't marked unsaved");
        await ExpectLaterChangesStillNoticed(d);
    }

    /// <summary>A touch, or a sync client fetching the file again: a new stamp, but the file holds
    /// just what the document was last saved as, so overwriting it with the unsaved edits loses
    /// nobody's work.</summary>
    private static async Task RewriteWithSavedTextStaysQuiet(Doc d)
    {
        d.WriteExternally(V0);
        await ExpectNoPrompt("for a file rewritten with the text it already held");
        Expect(d.Text == Mine, $"the document holds {Show(d.Text)}, not its own {Show(Mine)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    private static async Task WatchingOffStaysQuiet(Doc d)
    {
        await ExpectNoPrompt("with watching for outside changes turned off");
        Expect(d.Text == Mine, $"the recovered document holds {Show(d.Text)}, not {Show(Mine)}");
    }

    /// <summary>Save runs before the watcher's check has come due, so the watcher has reported
    /// nothing: Save itself has to notice.</summary>
    private static async Task SavingOverUnreportedChangeAsks(Doc d)
    {
        d.WriteExternally(V1);
        var save = d.View.Dispatcher.InvokeAsync(() => d.View.Save(saveAs: false));
        Choose(await ExpectPrompt("when saving over a change nobody had been asked about"), KeepMine);
        Expect(await save.Task, "Save reported failure after Keep mine");
        string onDisk = File.ReadAllText(d.WriterPath);
        Expect(onDisk == Mine, $"the file holds {Show(onDisk)} after Keep mine and Save, not {Show(Mine)}");
        await ExpectNoPrompt("after the save");
        await ExpectLaterChangesStillNoticed(d);
    }

    /// <summary>The non-interactive quit saves this way; it then parks the document in recovery.</summary>
    private static async Task QuietSaveLeavesChangedFileAlone(Doc d)
    {
        d.WriteExternally(V1);
        Expect(!d.View.Save(saveAs: false, interactive: false), "the save reported success");
        string onDisk = File.ReadAllText(d.WriterPath);
        Expect(onDisk == V1, $"the file holds {Show(onDisk)}: the save overwrote {Show(V1)}");
        Expect(d.Text == Mine, $"the document holds {Show(d.Text)}, not its own {Show(Mine)}");
        Choose(await ExpectPrompt("for the change the save left alone"), Reload);
        await ExpectNoPrompt("after Reload");
        Expect(d.Text == V1, $"Reload loaded {Show(d.Text)}, not {Show(V1)}");
    }

    /// <summary>A write that lands right after our own save is someone else's, and must be noticed.</summary>
    private static async Task ChangeJustAfterOurSaveIsNoticed(Doc d)
    {
        Expect(d.View.Save(saveAs: false), "the save failed");
        await Task.Delay(300);
        d.WriteExternally(V1);
        Choose(await ExpectPrompt("for a change written just after our own save"), Reload);
        await ExpectNoPrompt("after Reload");
        Expect(d.Text == V1, $"Reload loaded {Show(d.Text)}, not {Show(V1)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    /// <summary>A quit sweep can reach a document while its question is still on screen. Neither a
    /// save that may ask nor one that may not can answer for the user.</summary>
    private static async Task SaveDuringOpenQuestionRefuses(Doc d)
    {
        d.WriteExternally(V1);
        var prompt = await ExpectPrompt("after the external write");
        Expect(!d.View.Save(saveAs: false, interactive: false), "the quiet save reported success");
        Expect(!d.View.Save(saveAs: false), "the save reported success");
        string onDisk = File.ReadAllText(d.WriterPath);
        Expect(onDisk == V1, $"a save made during the question overwrote {Show(V1)} with {Show(onDisk)}");
        Choose(prompt, KeepMine);
        await ExpectNoPrompt("after Keep mine");
        await ExpectLaterChangesStillNoticed(d);
    }

    /// <summary>The file changes again while merging; the viewer's own question about it is closed
    /// unanswered, and then the viewer is closed too.</summary>
    private static async Task MergeLeavesUnansweredChangeOpen(Doc d)
    {
        d.WriteExternally(V1);
        Choose(await ExpectPrompt("after the external write"), Merge);
        var merge = await ExpectMergeViewer();
        d.WriteExternally(V2);
        (await ExpectWindow(ChangedAgainTitle, "in the merge viewer")).Close();
        await Task.Delay(300);
        merge.Close();
        Choose(await ExpectPrompt("for the version the merge viewer's question was closed on"), Reload);
        await ExpectNoPrompt("after Reload");
        Expect(d.Text == V2, $"Reload loaded {Show(d.Text)}, not {Show(V2)}");
        await ExpectLaterChangesStillNoticed(d);
    }

    /// <summary>As above, but the merge is saved: its result must not go over a version the viewer
    /// never had answered.</summary>
    private static async Task MergeSaveLeavesUnseenVersionAlone(Doc d)
    {
        d.WriteExternally(V1);
        Choose(await ExpectPrompt("after the external write"), Merge);
        var merge = await ExpectMergeViewer();
        d.WriteExternally(V2);
        (await ExpectWindow(ChangedAgainTitle, "in the merge viewer")).Close();
        await Task.Delay(300);
        ((Button)merge.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var prompt = await ExpectPrompt("for the version the merge viewer's question was closed on");
        string onDisk = File.ReadAllText(d.WriterPath);
        Expect(onDisk == V2, $"the merge's save replaced {Show(V2)}, which it never showed, with {Show(onDisk)}");
        Choose(prompt, Reload);
        await ExpectNoPrompt("after Reload");
        await ExpectLaterChangesStillNoticed(d);
    }

    /// <summary>A question raised from closing a tab (Save from "Save changes?") is followed by a
    /// check; the tab is gone by the time it runs.</summary>
    private static async Task ClosedTabAsksNothing(Doc d)
    {
        d.WriteExternally(V1);
        Choose(await ExpectPrompt("after the external write"), KeepMine);
        d.WriteExternally(V2);   // lands before the check that follows the answer
        d.View.Dispose();
        await ExpectNoPrompt("for a tab that has been closed");
    }

    private static async Task QuietSaveFailureShowsNothing(Doc d)
    {
        File.SetAttributes(d.WriterPath, FileAttributes.ReadOnly);
        try
        {
            var save = d.View.Dispatcher.InvokeAsync(() => d.View.Save(saveAs: false, interactive: false));
            await Task.Delay(1000);
            if (Find(SaveFailedTitle) is Window shown)
            {
                shown.Close();
                throw new TestFailure("the non-interactive save showed \"Save failed\" and waited for a click");
            }
            Expect(!await save.Task, "saving over a read-only file reported success");
        }
        finally { File.SetAttributes(d.WriterPath, FileAttributes.Normal); }
    }

    /// <summary>A sync client fetching the file again, say: the very version the user already chose
    /// to write over.</summary>
    private static async Task KeptVersionAgainIsQuiet(Doc d)
    {
        d.WriteExternally(V1);
        Choose(await ExpectPrompt("after the external write"), KeepMine);
        await ExpectNoPrompt("after Keep mine");
        d.WriteExternally(V1);
        await ExpectNoPrompt("for the version Keep mine already answered for");
        await ExpectLaterChangesStillNoticed(d);
    }

    private static async Task ClosingSavesQuestionMeansNotNow(Doc d)
    {
        d.WriteExternally(V1);
        var save = d.View.Dispatcher.InvokeAsync(() => d.View.Save(saveAs: false));
        (await ExpectPrompt("when saving over a change nobody had been asked about")).Close();
        Expect(!await save.Task, "Save reported success though nothing was written");
        string onDisk = File.ReadAllText(d.WriterPath);
        Expect(onDisk == V1, $"closing the question still let the save replace {Show(V1)} with {Show(onDisk)}");
        await ExpectNoPrompt("from the watcher, about the version the question was closed on");

        var again = d.View.Dispatcher.InvokeAsync(() => d.View.Save(saveAs: false));
        Choose(await ExpectPrompt("when saving again over the version the question was closed on"), KeepMine);
        Expect(await again.Task, "Save reported failure after Keep mine");
        onDisk = File.ReadAllText(d.WriterPath);
        Expect(onDisk == V0, $"the file holds {Show(onDisk)} after Keep mine and Save, not {Show(V0)}");
    }

    private static async Task TurningWatchingOnChecksAtOnce(Doc d)
    {
        await ExpectNoPrompt("with watching turned off");
        AppSettings.Current.WatchExternalChanges = true;
        d.View.ApplyWatchSetting();
        Choose(await ExpectPrompt("once watching was turned on"), Reload);
        await ExpectNoPrompt("after Reload");
        Expect(d.Text == V1, $"Reload loaded {Show(d.Text)}, not {Show(V1)}");
    }

    /// <summary>Another program saves exactly the text the document holds.</summary>
    private static async Task FileMatchingEditorIsSaved(Doc d)
    {
        d.WriteExternally(Mine);
        await ExpectNoPrompt("for a file that now holds the editor's own text");
        Expect(!d.View.IsDirty, "the document still counts as unsaved, though the file holds its text");
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

    private static Task<Window> ExpectPrompt(string when) => ExpectWindow(PromptTitle, when);

    private static async Task<Window> ExpectWindow(string title, string when)
    {
        for (int waited = 0; waited < 10_000; waited += 50)
        {
            if (Find(title) is Window w)
                return w;
            await Task.Delay(50);
        }
        throw new TestFailure($"no \"{title}\" window appeared {when}");
    }

    private static async Task ExpectNoPrompt(string when)
    {
        await Task.Delay(2000);
        if (Find(PromptTitle) is not null)
            throw new TestFailure($"a prompt appeared {when}");
    }

    private static async Task<DiffMergeWindow> ExpectMergeViewer()
    {
        for (int waited = 0; waited < 5_000; waited += 50)
        {
            if (Application.Current.Windows.OfType<DiffMergeWindow>().FirstOrDefault(w => w.IsVisible) is { } merge)
                return merge;
            await Task.Delay(50);
        }
        throw new TestFailure("the merge viewer never opened");
    }

    private static string OnDiskSide(DiffMergeWindow merge)
    {
        var doc = ((RichTextBox)merge.FindName("RightBox")).Document;
        return new TextRange(doc.ContentStart, doc.ContentEnd).Text;
    }

    private static async Task DismissNotice(string title) =>
        (await ExpectWindow(title, "after the answer")).Close();

    /// <summary>Close every window but the test's own hosts, innermost first, and let the handlers
    /// parked inside them unwind.</summary>
    private static async Task CloseDialogs()
    {
        foreach (var w in Application.Current.Windows.OfType<Window>().Reverse().ToList())
            if (w.Content is not EditorView)
                w.Close();
        await Task.Delay(300);
    }

    private static string TextOf(EditorView view) => view.SerializeDocument().CurrentText;

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
