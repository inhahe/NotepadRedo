using System.Text;
using System.Text.RegularExpressions;

namespace NotepadRedo;

/// <summary>A range that is going to be replaced, together with the literal text that will take its
/// place. In regex mode the replacement is already <i>expanded</i> (group references like <c>$1</c>
/// substituted for this particular match), so applying a list of these needs no further regex work.</summary>
public readonly record struct ReplaceMatch(int Start, int Length, string Replacement)
{
    public int End => Start + Length;
}

/// <summary>
/// Pure find-and-replace logic (no UI), so it can be reasoned about and unit-tested on its own.
///
/// <para>It deliberately differs from <see cref="SearchEngine"/> in two ways, both forced by the fact
/// that these matches are going to be <i>edited</i> rather than merely listed:</para>
/// <list type="bullet">
///   <item>Matches never overlap. <see cref="SearchEngine.FindAll"/> reports overlapping hits (useful
///   when you're browsing occurrences of "aa" in "aaa"); replacing overlapping ranges is meaningless,
///   so scanning here resumes past the end of each match.</item>
///   <item>A zero-length match (easy to write in regex — <c>x*</c>, <c>^</c>, <c>\b</c>) still advances
///   the scan by one character, so "replace all" terminates instead of spinning forever inserting text
///   at the same spot.</item>
/// </list>
/// </summary>
public static class ReplaceEngine
{
    /// <summary>
    /// Every place in <paramref name="text"/> that would be replaced, in document order, restricted to
    /// the half-open range [<paramref name="scopeStart"/>, scopeStart + <paramref name="scopeLength"/>).
    /// Pass the whole document's bounds for an unrestricted replace.
    /// </summary>
    /// <exception cref="ArgumentException">The regex pattern is invalid (message is the parse error).</exception>
    /// <exception cref="RegexMatchTimeoutException">The pattern took too long against this text.</exception>
    public static List<ReplaceMatch> Find(string text, string find, string replacement,
                                          MatchOptions opt, int scopeStart, int scopeLength)
    {
        var results = new List<ReplaceMatch>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(find))
            return results;

        scopeStart = Math.Clamp(scopeStart, 0, text.Length);
        int scopeEnd = Math.Clamp(scopeStart + Math.Max(0, scopeLength), scopeStart, text.Length);
        if (scopeStart >= scopeEnd)
            return results;

        if (opt.Regex)
            FindRegex(text, find, replacement, opt, scopeStart, scopeEnd, results);
        else
            FindLiteral(text, find, replacement, opt, scopeStart, scopeEnd, results);

        return results;
    }

    private static void FindLiteral(string text, string find, string replacement, MatchOptions opt,
                                    int scopeStart, int scopeEnd, List<ReplaceMatch> results)
    {
        var cmp = opt.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int idx = scopeStart;
        while (idx <= scopeEnd - find.Length)
        {
            int f = text.IndexOf(find, idx, scopeEnd - idx, cmp);
            if (f < 0) break;
            // Whole-word is judged against the whole document, not the scope: a selection that cuts
            // through the middle of a word shouldn't make that fragment look like a standalone word.
            if (!opt.WholeWord || SearchEngine.IsWholeWord(text, f, f + find.Length))
                results.Add(new ReplaceMatch(f, find.Length, replacement));
            idx = f + find.Length;   // non-overlapping: resume past what we just claimed
        }
    }

    private static void FindRegex(string text, string pattern, string replacement, MatchOptions opt,
                                  int scopeStart, int scopeEnd, List<ReplaceMatch> results)
    {
        var re = BuildRegex(pattern, opt.CaseSensitive);

        // Match against the whole text and merely *start* at the scope, rather than using the
        // (beginning, length) overload: that one redefines where the string begins and ends as far as
        // the engine is concerned, so "^" would match at the scope's first character (and at every
        // resumption point of a manual scan loop) even mid-line. Match(text, startat) keeps the real
        // string boundaries, so anchors and \b still see the surrounding document.
        // NextMatch() also handles stepping past a zero-width match for us.
        for (Match m = re.Match(text, scopeStart); m.Success; m = m.NextMatch())
        {
            if (m.Index >= scopeEnd)
                break;
            if (m.Index + m.Length > scopeEnd)
                continue;   // straddles the end of the selection — not wholly inside it
            if (opt.WholeWord && !SearchEngine.IsWholeWord(text, m.Index, m.Index + m.Length))
                continue;
            results.Add(new ReplaceMatch(m.Index, m.Length, m.Result(replacement)));
        }
    }

    /// <summary>Compile the user's pattern, turning a bad one into an <see cref="ArgumentException"/>
    /// whose message is safe to show in the pane. Delegates to <see cref="SearchEngine.BuildRegex"/> so
    /// a pattern means the same thing in both panes.</summary>
    public static Regex BuildRegex(string pattern, bool caseSensitive)
    {
        try
        {
            return SearchEngine.BuildRegex(pattern, caseSensitive);
        }
        catch (ArgumentException ex)
        {
            // RegexParseException derives from ArgumentException; its message carries the position of
            // the problem, which is exactly what the user needs to fix it.
            throw new ArgumentException(ex.Message, ex);
        }
    }

    /// <summary>Splice a set of replacements (document order, non-overlapping — i.e. straight from
    /// <see cref="Find"/>) into the text, returning the new document.</summary>
    public static string Apply(string text, IReadOnlyList<ReplaceMatch> matches)
    {
        if (matches.Count == 0)
            return text;

        var sb = new StringBuilder(text.Length);
        int cursor = 0;
        foreach (var m in matches)
        {
            if (m.Start < cursor)
                continue;   // defensive: skip anything that overlaps what we already emitted
            sb.Append(text, cursor, m.Start - cursor);
            sb.Append(m.Replacement);
            cursor = m.End;
        }
        sb.Append(text, cursor, text.Length - cursor);
        return sb.ToString();
    }

    /// <summary>Net change in document length if <paramref name="matches"/> were all applied.</summary>
    public static int Delta(IReadOnlyList<ReplaceMatch> matches)
    {
        int d = 0;
        foreach (var m in matches)
            d += m.Replacement.Length - m.Length;
        return d;
    }
}
