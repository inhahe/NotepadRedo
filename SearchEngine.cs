using System.Text;

namespace NotepadRedo;

/// <summary>Unit in which the proximity window ("within N of each other") is measured.</summary>
public enum ProximityUnit { Characters, Words, Lines }

/// <summary>A single match location in the document: a character range [Start, Start+Length).</summary>
public readonly record struct SearchMatch(int Start, int Length)
{
    public int End => Start + Length;
}

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
    /// <summary>All (possibly overlapping) occurrences of <paramref name="needle"/> in the text.
    /// When <paramref name="wholeWord"/> is set, an occurrence only counts if it isn't flanked by a
    /// word character on either side (so "os" won't match inside "composition").</summary>
    public static List<SearchMatch> FindAll(string text, string needle, bool caseSensitive,
                                            bool wholeWord = false)
    {
        var results = new List<SearchMatch>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle))
            return results;

        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int idx = 0;
        while (idx <= text.Length - needle.Length)
        {
            int f = text.IndexOf(needle, idx, cmp);
            if (f < 0) break;
            if (!wholeWord || IsWholeWord(text, f, f + needle.Length))
                results.Add(new SearchMatch(f, needle.Length));
            idx = f + 1;   // allow overlapping matches
        }
        return results;
    }

    /// <summary>True when the char just before <paramref name="start"/> and the char at
    /// <paramref name="end"/> are both non-word characters (or the text edge) — i.e. the range
    /// [start, end) stands alone as a word rather than sitting inside a longer run of letters/digits.
    /// Word characters are letters, digits, and underscore.</summary>
    private static bool IsWholeWord(string text, int start, int end)
    {
        bool leftOk = start <= 0 || !IsWordChar(text[start - 1]);
        bool rightOk = end >= text.Length || !IsWordChar(text[end]);
        return leftOk && rightOk;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Every cluster where all <paramref name="terms"/> appear within <paramref name="n"/> of each
    /// other (in the given unit). Each result spans from the first term's start to the last term's
    /// end within the smallest covering window; results don't overlap.
    /// </summary>
    public static List<SearchMatch> FindProximity(string text, IReadOnlyList<string> terms,
                                                  bool caseSensitive, ProximityUnit unit, int n,
                                                  bool wholeWord = false)
    {
        var results = new List<SearchMatch>();
        if (string.IsNullOrEmpty(text) || terms.Count == 0)
            return results;

        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // Collect every occurrence of every term, tagged with which term it is.
        var occ = new List<(int start, int end, int term)>();
        for (int t = 0; t < terms.Count; t++)
        {
            string term = terms[t];
            if (term.Length == 0) continue;
            int idx = 0;
            while (idx <= text.Length - term.Length)
            {
                int f = text.IndexOf(term, idx, cmp);
                if (f < 0) break;
                if (!wholeWord || IsWholeWord(text, f, f + term.Length))
                    occ.Add((f, f + term.Length, t));
                idx = f + 1;
            }
        }

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
