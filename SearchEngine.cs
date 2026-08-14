using System.Text;
using System.Text.RegularExpressions;

namespace NotepadRedo;

/// <summary>Unit in which the proximity window ("within N of each other") is measured.</summary>
public enum ProximityUnit { Characters, Words, Lines }

/// <summary>A single match location in the document: a character range [Start, Start+Length).</summary>
public readonly record struct SearchMatch(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// How a term is matched. Bundled into one value rather than passed as a run of three bare bools,
/// which at a call site are indistinguishable and easy to transpose. Shared by search and replace so
/// the two features can't drift apart on what an option means.
/// <para>
/// The three are deliberately <b>orthogonal</b>: <see cref="Regex"/> reinterprets the term as a
/// pattern, and <see cref="WholeWord"/> then constrains wherever that term matched to stand alone as
/// a word. Regex can express word boundaries itself with <c>\b</c>, but the checkbox stays useful
/// with it — it applies the constraint to an entire alternation without having to bracket every
/// branch — and keeping the options independent avoids a control that mysteriously greys itself out.
/// </para>
/// </summary>
public readonly record struct MatchOptions(bool CaseSensitive = false,
                                           bool WholeWord = false,
                                           bool Regex = false);

/// <summary>
/// Pure text-search logic (no UI), so it can be unit-tested directly. Two modes:
///  • plain — every occurrence of the query, matched <b>literally</b> (exactly the characters typed,
///    including spaces and quotes — no tokenising or special syntax);
///  • proximity — every place where all of an explicit list of terms occur within N characters/words/
///    lines of each other (the smallest window that covers every term, span measured in the unit).
/// The two modes take their input differently: plain from one query string, proximity from a caller-
/// supplied list of terms (the UI collects those as discrete items), so neither relies on parsing
/// magic characters out of a single string.
/// </summary>
public static class SearchEngine
{
    /// <summary>How long a runaway regex is allowed to chew on the document before we give up.
    /// Catastrophic backtracking is easy to type by accident, and the search re-runs on every
    /// keystroke, so an unbounded match would hang the UI mid-word.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Compile a user-typed pattern. <see cref="RegexOptions.Multiline"/> is on because in a text
    /// editor <c>^</c> and <c>$</c> meaning start/end of a <b>line</b> is what people expect —
    /// document-wide anchors are the unusual case, and <c>\A</c> / <c>\z</c> still express them.
    /// </summary>
    /// <exception cref="ArgumentException">The pattern doesn't parse
    /// (<see cref="RegexParseException"/> derives from this).</exception>
    public static Regex BuildRegex(string pattern, bool caseSensitive)
    {
        var opts = RegexOptions.Multiline;
        if (!caseSensitive) opts |= RegexOptions.IgnoreCase;
        return new Regex(pattern, opts, RegexTimeout);
    }

    /// <summary>
    /// Every occurrence of one term, in document order — the single place that knows how a term is
    /// turned into positions, so literal/regex/whole-word behave identically everywhere.
    /// </summary>
    /// <param name="allowOverlap">Report matches that overlap a previous one (plain search does, so
    /// searching "aa" in "aaa" lists two hits; replacing must not, or the splices would collide).
    /// Regex matching never overlaps — <c>NextMatch</c> resumes at the end of the previous match —
    /// so this only affects literal matching.</param>
    /// <exception cref="ArgumentException">Regex mode with a pattern that doesn't parse.</exception>
    public static IEnumerable<SearchMatch> Occurrences(string text, string term, MatchOptions opt,
                                                       bool allowOverlap = true)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
            yield break;

        if (opt.Regex)
        {
            var re = BuildRegex(term, opt.CaseSensitive);
            // NextMatch() resumes after the previous match and knows to step past a zero-width one,
            // which a hand-rolled "scan from index" loop has to special-case or spin forever on.
            for (Match m = re.Match(text); m.Success; m = m.NextMatch())
                if (!opt.WholeWord || IsWholeWord(text, m.Index, m.Index + m.Length))
                    yield return new SearchMatch(m.Index, m.Length);
            yield break;
        }

        var cmp = opt.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int idx = 0;
        while (idx <= text.Length - term.Length)
        {
            int f = text.IndexOf(term, idx, cmp);
            if (f < 0) break;
            if (!opt.WholeWord || IsWholeWord(text, f, f + term.Length))
                yield return new SearchMatch(f, term.Length);
            idx = allowOverlap ? f + 1 : f + term.Length;
        }
    }

    /// <summary>All occurrences of <paramref name="needle"/> in the text, overlaps included.</summary>
    /// <exception cref="ArgumentException">Regex mode with a pattern that doesn't parse.</exception>
    public static List<SearchMatch> FindAll(string text, string needle, MatchOptions opt)
        => Occurrences(text, needle, opt).ToList();

    /// <summary>
    /// True when the range [start, end) isn't sitting <i>inside</i> a longer run of word characters —
    /// which is all "match whole word only" can sensibly mean. Word characters are letters, digits
    /// and underscore.
    ///
    /// <para>Each edge is only constrained when it could actually be embedded, i.e. when the match's
    /// own character at that edge is a word character. A match that <b>begins</b> with a space, a
    /// bracket or a newline cannot be the tail of a longer word, so demanding a non-word character
    /// before it as well would reject something the match itself already rules out. Doing that was a
    /// real trap: with whole-word ticked, a pattern like <c>  \[\d+_\d+\]</c> (or a literal
    /// <c>" os "</c>) matched <i>nothing</i>, because the character before the leading space is
    /// nearly always a letter. Terms that do start and end in word characters — the ordinary case,
    /// and the only one the option is really aimed at — are unaffected.</para>
    ///
    /// <para>An empty range has no edge characters of its own, so both edges stay constrained: a
    /// zero-width match still has to stand between two non-word characters.</para>
    /// </summary>
    public static bool IsWholeWord(string text, int start, int end)
    {
        bool empty = end <= start;
        bool leftOk = start <= 0 || !IsWordChar(text[start - 1])
                                 || (!empty && !IsWordChar(text[start]));
        bool rightOk = end >= text.Length || !IsWordChar(text[end])
                                          || (!empty && !IsWordChar(text[end - 1]));
        return leftOk && rightOk;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Every cluster where all <paramref name="terms"/> appear within <paramref name="n"/> of each
    /// other (in the given unit). Each result spans from the first term's start to the last term's
    /// end within the smallest covering window; results don't overlap.
    /// </summary>
    /// <exception cref="ArgumentException">Regex mode with a term that doesn't parse.</exception>
    public static List<SearchMatch> FindProximity(string text, IReadOnlyList<string> terms,
                                                  MatchOptions opt, ProximityUnit unit, int n)
    {
        var results = new List<SearchMatch>();
        if (string.IsNullOrEmpty(text) || terms.Count == 0)
            return results;

        // Collect every occurrence of every term, tagged with which term it is. Each term is matched
        // by the same rules as a plain search, so "regular expression" and "whole word" mean exactly
        // what they do in the other mode — each term is simply its own little search.
        var occ = new List<(int start, int end, int term)>();
        for (int t = 0; t < terms.Count; t++)
            foreach (var m in Occurrences(text, terms[t], opt))
                occ.Add((m.Start, m.End, t));

        int termCount = terms.Count(t => t.Length > 0);
        if (termCount == 0) return results;

        // Every term must appear somewhere, else no cluster can exist.
        var present = new HashSet<int>();
        foreach (var o in occ) present.Add(o.term);
        if (present.Count < termCount) return results;

        occ.Sort((a, b) => a.start.CompareTo(b.start));

        // Position mapper: char index -> ordinal in the chosen unit (chars use the index directly).
        Func<int, int> ord = unit switch
        {
            ProximityUnit.Words => BuildWordOrdinal(text),
            ProximityUnit.Lines => BuildLineOrdinal(text),
            _ => (p => p),
        };

        // Classic minimum-window-covering-all-terms sweep. For each right edge, shrink the left
        // edge to the smallest window that still covers every term, then test its span.
        var count = new int[terms.Count];
        int covered = 0;
        int left = 0;
        int lastEmittedEnd = -1;

        for (int right = 0; right < occ.Count; right++)
        {
            if (count[occ[right].term]++ == 0) covered++;

            if (covered == termCount)
            {
                // Trim redundant occurrences off the left so the window is minimal.
                while (count[occ[left].term] > 1)
                {
                    count[occ[left].term]--;
                    left++;
                }

                int wStart = occ[left].start;
                int wEnd = occ[right].end;
                int span = unit == ProximityUnit.Characters
                    ? wEnd - wStart
                    : ord(occ[right].start) - ord(occ[left].start);

                if (span <= n && wStart >= lastEmittedEnd)
                {
                    results.Add(new SearchMatch(wStart, Math.Max(1, wEnd - wStart)));
                    lastEmittedEnd = wEnd;
                }
            }
        }
        return results;
    }

    /// <summary>Ordinal of the word that contains each character index (0-based, monotonic).</summary>
    private static Func<int, int> BuildWordOrdinal(string text)
    {
        // wordAt[p] = number of word-starts strictly before p. A word starts at a non-whitespace
        // char whose predecessor is whitespace (or start of text).
        var wordAt = new int[text.Length + 1];
        int words = 0;
        bool prevWs = true;
        for (int i = 0; i < text.Length; i++)
        {
            wordAt[i] = words;
            bool ws = char.IsWhiteSpace(text[i]);
            if (!ws && prevWs) words++;
            prevWs = ws;
        }
        wordAt[text.Length] = words;
        return p => wordAt[Math.Clamp(p, 0, text.Length)];
    }

    /// <summary>Ordinal of the line that contains each character index (0-based newline count).</summary>
    private static Func<int, int> BuildLineOrdinal(string text)
    {
        var lineAt = new int[text.Length + 1];
        int lines = 0;
        for (int i = 0; i < text.Length; i++)
        {
            lineAt[i] = lines;
            if (text[i] == '\n') lines++;
        }
        lineAt[text.Length] = lines;
        return p => lineAt[Math.Clamp(p, 0, text.Length)];
    }

    /// <summary>
    /// Build a one/two-line preview snippet centred on a match, collapsing runs of whitespace so
    /// the result reads as a compact single line. The UI trims it with an ellipsis if still too long.
    /// </summary>
    public static string Preview(string text, SearchMatch m, int contextBefore = 24, int maxLen = 200)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int start = Math.Clamp(m.Start - contextBefore, 0, text.Length);
        int end = Math.Clamp(m.Start + Math.Max(m.Length, 1) + (maxLen - contextBefore), 0, text.Length);
        if (end < start) end = start;

        var sb = new StringBuilder(end - start + 1);
        if (start > 0) sb.Append('\u2026');
        bool lastWs = false;
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            if (c == '\r') continue;
            if (char.IsWhiteSpace(c))
            {
                if (!lastWs) sb.Append(' ');
                lastWs = true;
            }
            else { sb.Append(c); lastWs = false; }
        }
        if (end < text.Length) sb.Append('\u2026');
        return sb.ToString().Trim();
    }
}
