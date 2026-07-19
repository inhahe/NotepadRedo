using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace NotepadRedo;

/// <summary>
/// Cross-instance coordination. Each process runs a tiny named-pipe server; other processes
/// connect to ask it to do something with a document. The protocol is one request line
/// "VERB\tARG\n" and one reply line "OK\n" / "NO\n". Verbs:
///   FOCUS &lt;path&gt;   — if this process has the file open, select that tab and come forward.
///   OPEN  &lt;path&gt;   — open the file here as a new tab (tab-mode consolidation).
///   CLOSE &lt;token&gt;  — remove the tab whose document RecoveryId == token (used on tab tear-off
///                       across processes, so the origin drops its copy after the move).
///   QUIT  &lt;any&gt;     — flush every open document to crash recovery and exit (used by the build
///                       script to close the app cleanly before overwriting the exe).
///   QUITSAVE &lt;any&gt;  — save titled docs to disk and untitled docs to crash recovery, then exit
///                       (save-first variant of QUIT).
///   QUITASK &lt;any&gt;   — interactive quit: prompt to save each unsaved document (Yes/No/Cancel) and
///                       exit; reply is OK when quitting, NO when the user cancelled. The reply is
///                       only sent once the prompts are answered, so the caller blocks on the user.
/// </summary>
public sealed class IpcServer : IDisposable
{
    /// <summary>Well-known pipe name for a process, derived from its id.</summary>
    private static string PipeNameFor(int pid) => $"NotepadRedo.{pid}";

    private readonly CancellationTokenSource _cts = new();

    public void Start() => _ = Task.Run(() => ListenLoop(_cts.Token));

    private async Task ListenLoop(CancellationToken token)
    {
        string name = PipeNameFor(Environment.ProcessId);
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                HandleConnection(server);
            }
            catch (OperationCanceledException) { break; }
            catch { /* keep serving; a bad connection shouldn't kill the loop */ }
        }
    }

    private static void HandleConnection(NamedPipeServerStream server)
    {
        bool ok = false;
        try
        {
            string request = ReadLine(server);
            int tab = request.IndexOf('\t');
            string verb = tab < 0 ? request : request.Substring(0, tab);
            string arg = tab < 0 ? "" : request.Substring(tab + 1);

            var app = Application.Current;
            if (app is not null && !string.IsNullOrEmpty(arg))
            {
                // Run the action on the UI thread, but never let an exception in it swallow the
                // reply: a launching sibling blocks reading our answer, so if we die silently it
                // hangs forever. Catch inside the Invoke so we always fall through to write a reply.
                ok = app.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        return verb switch
                        {
                            "FOCUS" => MainWindow.TryFocusDocument(arg),
                            "OPEN"  => MainWindow.OpenDocument(arg),
                            "CLOSE" => MainWindow.CloseTabByToken(arg),
                            "QUIT"  => MainWindow.RequestQuitWithRecovery(),
                            "QUITSAVE" => MainWindow.RequestQuitWithSave(),
                            "QUITASK"  => MainWindow.RequestQuitWithPrompt(),
                            _       => false,
                        };
                    }
                    catch (Exception ex)
                    {
                        CrashLog.Log($"IPC handler '{verb}' threw", ex);
                        return false;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            CrashLog.Log("IPC HandleConnection failed", ex);
        }

        // Always answer, even on failure, so the caller's reply-read unblocks promptly.
        try
        {
            var reply = Encoding.UTF8.GetBytes((ok ? "OK" : "NO") + "\n");
            server.Write(reply, 0, reply.Length);
            server.Flush();
            try { server.WaitForPipeDrain(); } catch { /* client already gone */ }
        }
        catch { /* client already gone */ }
    }

    public void Dispose() => _cts.Cancel();

    /// <summary>
    /// Ask every other NotepadRedo process whether it already has <paramref name="path"/> open;
    /// the first that does is brought to the foreground with that tab selected. Returns true
    /// when a sibling took ownership.
    /// </summary>
    public static bool TryFocusInSibling(string path) => AnySibling(pid => Send(pid, "FOCUS", path, steal: true, replyTimeoutMs: OpenReplyTimeoutMs));

    /// <summary>
    /// Forward <paramref name="path"/> to an existing instance so it opens as a new tab there.
    /// Returns true when a sibling accepted it (tab-mode consolidation).
    /// </summary>
    public static bool OpenInSibling(string path) => AnySibling(pid => Send(pid, "OPEN", path, steal: true, replyTimeoutMs: OpenReplyTimeoutMs));

    /// <summary>Tell a specific process to drop the tab holding <paramref name="token"/>.</summary>
    public static bool CloseTabInProcess(int pid, string token) => Send(pid, "CLOSE", token, steal: false, replyTimeoutMs: OpenReplyTimeoutMs);

    /// <summary>
    /// Ask every other NotepadRedo process to autosave to crash recovery and exit. Returns the
    /// number of siblings that acknowledged. Used before a redeploy overwrites the exe.
    /// </summary>
    public static int QuitAllSiblings()
    {
        int self = Environment.ProcessId;
        int acked = 0;
        foreach (var proc in AppProcesses())
        {
            using (proc)
            {
                if (proc.Id == self)
                    continue;
                if (Send(proc.Id, "QUIT", "quit", steal: false, replyTimeoutMs: Timeout.Infinite))
                    acked++;
            }
        }
        return acked;
    }

    /// <summary>
    /// Ask every other NotepadRedo process to save its work (titled documents to disk, untitled
    /// ones to crash recovery) and exit. Returns the number of siblings that acknowledged. Used
    /// before a redeploy overwrites the exe when unsaved work should be persisted, not just parked.
    /// </summary>
    public static int QuitAllSiblingsAndSave()
    {
        int self = Environment.ProcessId;
        int acked = 0;
        foreach (var proc in AppProcesses())
        {
            using (proc)
            {
                if (proc.Id == self)
                    continue;
                if (Send(proc.Id, "QUITSAVE", "quit", steal: false, replyTimeoutMs: Timeout.Infinite))
                    acked++;
            }
        }
        return acked;
    }

    /// <summary>Outcome of an interactive <see cref="QuitAllSiblingsInteractive"/> sweep.</summary>
    public enum QuitResult { NoneRunning, AllClosed, Cancelled }

    /// <summary>
    /// Ask every other NotepadRedo process to close interactively — each one prompts to save its
    /// unsaved work (Yes/No/Cancel) — and BLOCK until it has actually exited. Because a sibling
    /// only replies once its prompts are answered, and we then wait for the process to exit, this
    /// call does not return until the user has decided the fate of every open document. If the user
    /// cancels a prompt (leaving that instance open) the sweep reports <see cref="QuitResult.Cancelled"/>
    /// so the caller can abort the redeploy rather than force-killing unsaved work.
    /// </summary>
    public static QuitResult QuitAllSiblingsInteractive()
    {
        int self = Environment.ProcessId;
        var siblings = AppProcesses().Where(p => p.Id != self).ToList();
        if (siblings.Count == 0)
            return QuitResult.NoneRunning;

        bool cancelled = false;
        foreach (var proc in siblings)
        {
            using (proc)
            {
                // Send blocks until the instance answers its Save? prompts (the reply is written
                // only after the UI action returns), so this waits for the user with no polling.
                bool quitting = Send(proc.Id, "QUITASK", "quit", steal: true, replyTimeoutMs: Timeout.Infinite);
                if (quitting)
                {
                    // It acknowledged the quit (its work is already saved/discarded per the prompt),
                    // so wait for it to exit. Cap the wait so a slow teardown can't block the deploy
                    // forever; at that point its work is safe, so forcing it is fine.
                    try
                    {
                        if (!proc.WaitForExit(15000))
                        {
                            try { proc.Kill(); } catch { }
                            try { proc.WaitForExit(3000); } catch { }
                        }
                    }
                    catch { /* already gone — nothing to wait for */ }
                }
                else
                {
                    // Reply was NO (user cancelled) or it wasn't reachable. If it's still alive,
                    // treat that as a cancel so the deploy aborts instead of killing unsaved work.
                    try { if (!proc.HasExited) cancelled = true; } catch { }
                }
            }
        }
        return cancelled ? QuitResult.Cancelled : QuitResult.AllClosed;
    }

    /// <summary>Run <paramref name="ask"/> against each sibling process; stop at the first true.</summary>
    private static bool AnySibling(Func<int, bool> ask)
    {
        int self = Environment.ProcessId;
        foreach (var proc in AppProcesses())
        {
            using (proc)
            {
                if (proc.Id == self)
                    continue;
                if (ask(proc.Id))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// How long a launching instance waits for a sibling to answer a FOCUS/OPEN/CLOSE request before
    /// giving up on it. These handlers reply near-instantly in the normal case; the cap only exists so
    /// a wedged or unresponsive sibling can never hang the process that's trying to open a file. The
    /// quit verbs deliberately pass <see cref="Timeout.Infinite"/> because they must block on the
    /// user's interactive Save prompts.
    /// </summary>
    private const int OpenReplyTimeoutMs = 8000;

    private static bool Send(int pid, string verb, string arg, bool steal, int replyTimeoutMs)
    {
        NamedPipeClientStream? client = null;
        try
        {
            // PipeOptions.Asynchronous so the reply read below can honour a cancellation timeout via
            // overlapped I/O — without it, ReadAsync's token is only checked before the read starts.
            client = new NamedPipeClientStream(".", PipeNameFor(pid), PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(250);

            if (steal)
                AllowSetForegroundWindow(pid);   // let the target legitimately grab the foreground

            var outBytes = Encoding.UTF8.GetBytes(verb + "\t" + arg + "\n");
            client.Write(outBytes, 0, outBytes.Length);
            client.Flush();

            return ReadLine(client, replyTimeoutMs).Trim() == "OK";
        }
        catch
        {
            return false;   // not listening / gone / busy / timed out — caller tries the next
        }
        finally
        {
            try { client?.Dispose(); } catch { }
        }
    }

    /// <summary>Read a single '\n'-terminated line of UTF-8 from a pipe stream (blocking, no timeout).</summary>
    private static string ReadLine(PipeStream pipe)
    {
        var bytes = new List<byte>(260);
        int b;
        while ((b = pipe.ReadByte()) != -1 && b != '\n')
            bytes.Add((byte)b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Read a single '\n'-terminated line, giving up after <paramref name="timeoutMs"/> (or blocking
    /// forever when it is <see cref="Timeout.Infinite"/>). A timeout returns "" so the caller treats
    /// the sibling as not having accepted the request rather than hanging on it indefinitely.
    /// </summary>
    private static string ReadLine(PipeStream pipe, int timeoutMs)
    {
        if (timeoutMs == Timeout.Infinite)
            return ReadLine(pipe);

        using var cts = new CancellationTokenSource(timeoutMs);
        var bytes = new List<byte>(260);
        var one = new byte[1];
        try
        {
            while (true)
            {
                int n = pipe.ReadAsync(one.AsMemory(0, 1), cts.Token).AsTask().GetAwaiter().GetResult();
                if (n <= 0 || one[0] == (byte)'\n')
                    break;
                bytes.Add(one[0]);
            }
        }
        catch (OperationCanceledException) { return ""; }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Every running instance of this app, matched by executable-name *prefix* ("NotepadRedo") so
    /// themed variant builds (NotepadRedo-Graphite.exe, NotepadRedo-Sunset.exe) are found too — they
    /// all listen on the same "NotepadRedo.&lt;pid&gt;" pipe, so a redeploy or focus must reach them.
    /// (Pre-rename TreeNotepad.exe instances listen on a different pipe name and can't be signalled;
    /// build.bat detects and refuses those separately.)
    /// </summary>
    private static Process[] AppProcesses()
    {
        try
        {
            return Process.GetProcesses()
                .Where(p =>
                {
                    try { return p.ProcessName.StartsWith("NotepadRedo", StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                })
                .ToArray();
        }
        catch { return Array.Empty<Process>(); }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
