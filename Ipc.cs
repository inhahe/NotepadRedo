using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace TreeNotepad;

/// <summary>
/// Cross-instance coordination so a file is only ever open once. Each process runs a tiny
/// named-pipe server; when any process is asked to open a file it first asks its siblings
/// (over their pipes) whether one already has it, and if so hands focus over instead of
/// opening a duplicate.
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
        string path = ReadLine(server);
        bool focused = false;
        if (!string.IsNullOrEmpty(path))
        {
            var app = Application.Current;
            if (app is not null)
                focused = app.Dispatcher.Invoke(() => MainWindow.TryFocusDocument(path));
        }

        var reply = Encoding.UTF8.GetBytes((focused ? "OK" : "NO") + "\n");
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
    public static bool TryFocusInSibling(string path)
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
                if (AskSibling(proc.Id, path))
                    return true;
            }
        }
        return false;
    }

    private static bool AskSibling(int pid, string path)
    {
        NamedPipeClientStream? client = null;
        try
        {
            client = new NamedPipeClientStream(".", PipeNameFor(pid), PipeDirection.InOut);
            client.Connect(250);

            // Let the target legitimately steal the foreground from us.
            AllowSetForegroundWindow(pid);

            var outBytes = Encoding.UTF8.GetBytes(path + "\n");
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
