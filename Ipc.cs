using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace TreeNotepad;

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
/// </summary>
public sealed class IpcServer : IDisposable
{
    /// <summary>Well-known pipe name for a process, derived from its id.</summary>
    private static string PipeNameFor(int pid) => $"TreeNotepad.{pid}";

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
        string request = ReadLine(server);
        int tab = request.IndexOf('\t');
        string verb = tab < 0 ? request : request.Substring(0, tab);
        string arg = tab < 0 ? "" : request.Substring(tab + 1);

        bool ok = false;
        var app = Application.Current;
        if (app is not null && !string.IsNullOrEmpty(arg))
        {
            ok = app.Dispatcher.Invoke(() => verb switch
            {
                "FOCUS" => MainWindow.TryFocusDocument(arg),
                "OPEN"  => MainWindow.OpenDocument(arg),
                "CLOSE" => MainWindow.CloseTabByToken(arg),
                "QUIT"  => MainWindow.RequestQuitWithRecovery(),
                _       => false,
            });
        }

        var reply = Encoding.UTF8.GetBytes((ok ? "OK" : "NO") + "\n");
        server.Write(reply, 0, reply.Length);
        server.Flush();
        try { server.WaitForPipeDrain(); } catch { /* client already gone */ }
    }

    public void Dispose() => _cts.Cancel();

    /// <summary>
    /// Ask every other TreeNotepad process whether it already has <paramref name="path"/> open;
    /// the first that does is brought to the foreground with that tab selected. Returns true
    /// when a sibling took ownership.
    /// </summary>
    public static bool TryFocusInSibling(string path) => AnySibling(pid => Send(pid, "FOCUS", path, steal: true));

    /// <summary>
    /// Forward <paramref name="path"/> to an existing instance so it opens as a new tab there.
    /// Returns true when a sibling accepted it (tab-mode consolidation).
    /// </summary>
    public static bool OpenInSibling(string path) => AnySibling(pid => Send(pid, "OPEN", path, steal: true));

    /// <summary>Tell a specific process to drop the tab holding <paramref name="token"/>.</summary>
    public static bool CloseTabInProcess(int pid, string token) => Send(pid, "CLOSE", token, steal: false);

    /// <summary>
    /// Ask every other TreeNotepad process to autosave to crash recovery and exit. Returns the
    /// number of siblings that acknowledged. Used before a redeploy overwrites the exe.
    /// </summary>
    public static int QuitAllSiblings()
    {
        int self = Environment.ProcessId;
        string procName;
        try { procName = Process.GetCurrentProcess().ProcessName; }
        catch { return 0; }

        int acked = 0;
        foreach (var proc in SafeGetProcesses(procName))
        {
            using (proc)
            {
                if (proc.Id == self)
                    continue;
                if (Send(proc.Id, "QUIT", "quit", steal: false))
                    acked++;
            }
        }
        return acked;
    }

    /// <summary>Run <paramref name="ask"/> against each sibling process; stop at the first true.</summary>
    private static bool AnySibling(Func<int, bool> ask)
    {
        int self = Environment.ProcessId;
        string procName;
        try { procName = Process.GetCurrentProcess().ProcessName; }
        catch { return false; }

        foreach (var proc in SafeGetProcesses(procName))
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

    private static bool Send(int pid, string verb, string arg, bool steal)
    {
        NamedPipeClientStream? client = null;
        try
        {
            client = new NamedPipeClientStream(".", PipeNameFor(pid), PipeDirection.InOut);
            client.Connect(250);

            if (steal)
                AllowSetForegroundWindow(pid);   // let the target legitimately grab the foreground

            var outBytes = Encoding.UTF8.GetBytes(verb + "\t" + arg + "\n");
            client.Write(outBytes, 0, outBytes.Length);
            client.Flush();

            return ReadLine(client).Trim() == "OK";
        }
        catch
        {
            return false;   // not listening / gone / busy — caller tries the next
        }
        finally
        {
            try { client?.Dispose(); } catch { }
        }
    }

    /// <summary>Read a single '\n'-terminated line of UTF-8 from a pipe stream.</summary>
    private static string ReadLine(PipeStream pipe)
    {
        var bytes = new List<byte>(260);
        int b;
        while ((b = pipe.ReadByte()) != -1 && b != '\n')
            bytes.Add((byte)b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static Process[] SafeGetProcesses(string name)
    {
        try { return Process.GetProcessesByName(name); }
        catch { return Array.Empty<Process>(); }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
