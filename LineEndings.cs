using System.Text;

namespace NotepadRedo;

/// <summary>Which newline sequence a document uses on disk.</summary>
public enum LineEndingStyle
{
    /// <summary>"\r\n" — Windows.</summary>
    Crlf,
    /// <summary>"\n" — Unix, and what most tooling writes.</summary>
    Lf,
    /// <summary>"\r" — classic Mac. Rare, but a file that has them shouldn't be mangled.</summary>
    Cr,
}

/// <summary>
/// Line-ending detection and conversion, kept in one place because a document has <b>two</b>
/// representations and they must never be confused:
///
/// <list type="bullet">
///   <item><b>On disk</b> — whatever the file actually uses, which we preserve.</item>
///   <item><b>In the editor</b> — always CRLF. That is not a choice: WPF's <c>TextBox</c> inserts
///   "\r\n" whenever the user presses Enter, whatever the rest of the buffer uses. Holding an LF
///   file verbatim therefore produces a <i>mixed</i> buffer the moment anyone types, and the
///   mixture is then written back to the file.</item>
/// </list>
///
/// <para>That mixture caused a genuinely baffling bug: an LF file was rewritten as CRLF by another
/// program, so the raw texts differed and the "changed on disk" prompt appeared — but the diff
/// viewer normalises endings before splitting into lines, so both sides rendered <i>identical</i>
/// and there was nothing to resolve. Converting on the way in and back on the way out removes the
/// whole class of problem: the buffer is uniform, comparisons are about content, and the file keeps
/// the endings it came with.</para>
/// </summary>
public static class LineEndings
{
    /// <summary>The sequence the editor buffer always uses internally.</summary>
    public const string EditorSequence = "\r\n";

    public static string Sequence(LineEndingStyle style) => style switch
    {
        LineEndingStyle.Lf => "\n",
        LineEndingStyle.Cr => "\r",
        _ => "\r\n",
    };

    /// <summary>Short name for the status bar and for messages ("CRLF", "LF", "CR").</summary>
    public static string Label(LineEndingStyle style) => style switch
    {
        LineEndingStyle.Lf => "LF",
        LineEndingStyle.Cr => "CR",
        _ => "CRLF",
    };

    /// <summary>Name plus the platform it is associated with, for menus and tooltips.</summary>
    public static string LongLabel(LineEndingStyle style) => style switch
    {
        LineEndingStyle.Lf => "LF (Unix)",
        LineEndingStyle.Cr => "CR (classic Mac)",
        _ => "CRLF (Windows)",
    };

    /// <summary>How many of each kind of break the text contains. A "\r\n" counts once, as CRLF —
    /// never as one CR plus one LF — so the three counts partition the breaks rather than
    /// double-counting them.</summary>
    public static (int Crlf, int Lf, int Cr) Count(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (c == '\n') lf++;
        }
        return (crlf, lf, cr);
    }

    /// <summary>The dominant style. Ties go to CRLF, and so does a file with no line breaks at all —
    /// on Windows that is the least surprising thing to append when the user first presses Enter.</summary>
    public static LineEndingStyle Detect(string text)
    {
        var (crlf, lf, cr) = Count(text);
        if (lf > crlf && lf >= cr) return LineEndingStyle.Lf;
        if (cr > crlf && cr > lf) return LineEndingStyle.Cr;
        return LineEndingStyle.Crlf;
    }

    /// <summary>True when the text uses more than one kind of break — worth telling the user about,
    /// since saving will unify them.</summary>
    public static bool IsMixed(string text)
    {
        var (crlf, lf, cr) = Count(text);
        int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        return kinds > 1;
    }

    /// <summary>Convert any mixture to the editor's internal CRLF form.</summary>
    public static string ToEditor(string text) => Convert(text, "\r\n");

    /// <summary>Convert an editor buffer to the form that goes on disk. Any stray endings (a paste
    /// can bring LF text into the buffer) are unified too, so the file is never left mixed.</summary>
    public static string FromEditor(string text, LineEndingStyle style) => Convert(text, Sequence(style));

    /// <summary>Rewrite every CRLF / CR / LF as <paramref name="target"/>, in one pass.</summary>
    private static string Convert(string text, string target)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        // Nothing to do is the common case (a CRLF file in a CRLF buffer); do not allocate for it.
        bool needsWork = false;
        for (int i = 0; i < text.Length && !needsWork; i++)
        {
            char c = text[i];
            if (c != '\r' && c != '\n') continue;
            int len = (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
            needsWork = len != target.Length
                     || string.CompareOrdinal(text, i, target, 0, target.Length) != 0;
            i += len - 1;
        }
        if (!needsWork)
            return text;

        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                sb.Append(target);
            }
            else if (c == '\n') sb.Append(target);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Describe how two texts differ when a <i>line</i> diff of them shows nothing — i.e. the
    /// difference is real but invisible on screen. Returns null when they really are identical.
    /// The merge viewer uses this so it can never present two panes that look the same with no
    /// explanation, which is exactly what a line-endings-only change used to do.
    /// </summary>
    public static string? DescribeInvisibleDifference(string a, string b)
    {
        if (a == b)
            return null;

        var countsA = Count(a);
        var countsB = Count(b);
        if (countsA != countsB)
        {
            string an = Label(Detect(a)), bn = Label(Detect(b));
            return an == bn
                ? $"their line endings (both are mostly {an}, but the mixtures differ)"
                : $"their line endings ({an} on the left, {bn} on the right)";
        }

        // Same breaks, so line for line the text differs only in characters that do not show.
        string[] la = Split(a), lb = Split(b);
        if (la.Length == lb.Length)
        {
            int differing = 0, trailingOnly = 0;
            for (int i = 0; i < la.Length; i++)
            {
                if (la[i] == lb[i]) continue;
                differing++;
                if (la[i].TrimEnd() == lb[i].TrimEnd()) trailingOnly++;
            }
            if (differing > 0 && differing == trailingOnly)
                return trailingOnly == 1
                    ? "trailing whitespace on one line"
                    : $"trailing whitespace on {trailingOnly} lines";
        }

        return $"characters that do not show on screen (first at position {FirstDifference(a, b) + 1})";
    }

    private static string[] Split(string text) => Convert(text, "\n").Split('\n');

    private static int FirstDifference(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i]) return i;
        return n;
    }
}
