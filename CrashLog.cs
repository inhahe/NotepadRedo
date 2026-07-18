using System.IO;
using System.Text;

namespace NotepadRedo;

/// <summary>
/// Best-effort crash / error logging. Every unhandled exception (and any caught-but-notable
/// failure) is appended with a full stack trace to a log file under LocalAppData so problems
/// that only show up in the wild can be diagnosed after the fact.
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotepadRedo");

    /// <summary>Full path of the crash log (exposed so the UI can point the user at it).</summary>
    public static string FilePath { get; } = Path.Combine(Dir, "crash.log");

    /// <summary>Append a timestamped entry with the exception's full traceback.</summary>
    public static void Log(string context, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("======================================================================");
            sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("]  ")
              .Append("pid ").Append(Environment.ProcessId).Append("  ").AppendLine(context);
            if (ex is not null)
                sb.AppendLine(ex.ToString());   // message + full stack, including inner exceptions
            sb.AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath, sb.ToString());
            }
        }
        catch { /* logging must never itself throw */ }
    }
}
