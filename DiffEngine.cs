using System.Text;

namespace NotepadRedo;

/// <summary>Kind of a line-level diff operation.</summary>
public enum DiffOpKind { Equal, Delete, Insert, Change }

/// <summary>
/// One aligned row of a line diff. <see cref="Left"/> is the line from the first ("mine") text and
/// <see cref="Right"/> the line from the second ("disk") text; either is null on a pure insert /
/// delete. <see cref="LeftIndex"/> / <see cref="RightIndex"/> are the 0-based source line numbers
/// (or -1 when that side has no line here).
/// </summary>
public readonly record struct DiffOp(DiffOpKind Kind, string? Left, string? Right, int LeftIndex, int RightIndex);

/// <summary>One inline segment of a changed line: a run of text that is either shared or differing.</summary>
public readonly record struct InlineSpan(string Text, bool Differs);

/// <summary>
/// Pure text-diff logic (no UI), ported from orchestrator2's <c>diff.js</c> so the merge viewer
/// matches its "UltraCompare" look: an LCS line diff with adjacent delete+insert pairs merged into
/// change rows, plus a word/whitespace-token inline diff that marks the differing spans within a
/// changed line. Kept dependency-free and unit-testable.
/// </summary>
public static class DiffEngine
{
    /// <summary>
    /// Above this many cells (lines_a × lines_b) the O(n·m) LCS table is too big/slow, so we skip
    /// the fine alignment and emit a single coarse change row covering everything. Typical text
    /// files stay far below this.
    /// </summary>
    public const long MaxLcsCells = 8_000_000;

    /// <summary>
    /// Cap (chars_a × chars_b) for the character-level refinement inside a changed block. Above this
    /// the fine char alignment is skipped and the whole block is marked differing (rare — only for
    /// very long single-line changes).
    /// </summary>
    public const long MaxInlineCells = 4_000_000;

    /// <summary>Split text into lines for diffing (normalising CRLF/CR to LF first).</summary>
    public static string[] SplitLines(string text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>Line diff of two texts, returning aligned equal/insert/delete/change rows in order.</summary>
    public static List<DiffOp> DiffLines(string[] a, string[] b)
    {
        a ??= Array.Empty<string>();
        b ??= Array.Empty<string>();

        if ((long)a.Length * b.Length > MaxLcsCells)
            return CoarseDiff(a, b);

        var dp = LcsTable(a, b);

        // Backtrack into raw equal/insert/delete ops (with source indices).
        var raw = new List<DiffOp>();
        int i = a.Length, j = b.Length;
        while (i > 0 || j > 0)
        {
            if (i > 0 && j > 0 && a[i - 1] == b[j - 1])
            {
                raw.Add(new DiffOp(DiffOpKind.Equal, a[i - 1], b[j - 1], i - 1, j - 1));
                i--; j--;
            }
            else if (j > 0 && (i == 0 || dp[i, j - 1] >= dp[i - 1, j]))
            {
                raw.Add(new DiffOp(DiffOpKind.Insert, null, b[j - 1], -1, j - 1));
                j--;
            }
            else
            {
                raw.Add(new DiffOp(DiffOpKind.Delete, a[i - 1], null, i - 1, -1));
                i--;
            }
        }
        raw.Reverse();

        // Merge each run of deletes immediately followed by inserts into paired change rows.
        var ops = new List<DiffOp>(raw.Count);
        int k = 0;
        while (k < raw.Count)
        {
            if (raw[k].Kind == DiffOpKind.Delete)
            {
                var dels = new List<DiffOp>();
                while (k < raw.Count && raw[k].Kind == DiffOpKind.Delete) { dels.Add(raw[k]); k++; }
                var ins = new List<DiffOp>();
                while (k < raw.Count && raw[k].Kind == DiffOpKind.Insert) { ins.Add(raw[k]); k++; }

                int paired = Math.Min(dels.Count, ins.Count);
                for (int p = 0; p < paired; p++)
                    ops.Add(new DiffOp(DiffOpKind.Change, dels[p].Left, ins[p].Right, dels[p].LeftIndex, ins[p].RightIndex));
                for (int p = paired; p < dels.Count; p++)
                    ops.Add(dels[p]);
                for (int p = paired; p < ins.Count; p++)
                    ops.Add(ins[p]);
            }
            else
            {
                ops.Add(raw[k]);
                k++;
            }
        }
        return ops;
    }

    /// <summary>
    /// Inline diff of two (changed) lines, refined to the character level. First aligns on
    /// word/whitespace tokens (so shared words stay anchored and the alignment doesn't drift), then
    /// within each changed block runs a character-level LCS so only the differing <em>characters</em>
    /// are marked — e.g. <c>composition</c> → <c>compositions</c> reddens just the trailing "s",
    /// matching UltraCompare rather than reddening the whole word. Returns the segment lists for each
    /// side; segments with <see cref="InlineSpan.Differs"/> true are the parts to paint red.
    /// </summary>
    public static (List<InlineSpan> left, List<InlineSpan> right) InlineDiff(string a, string b)
    {
        var tokA = Tokenize(a ?? "");
        var tokB = Tokenize(b ?? "");
        var dp = LcsTable(tokA, tokB);

        int i = tokA.Length, j = tokB.Length;
        var rev = new List<(int side, string text)>();  // side: 0 both, 1 left, 2 right
        while (i > 0 || j > 0)
        {
            if (i > 0 && j > 0 && tokA[i - 1] == tokB[j - 1])
            {
                rev.Add((0, tokA[i - 1]));
                i--; j--;
            }
            else if (j > 0 && (i == 0 || dp[i, j - 1] >= dp[i - 1, j]))
            {
                rev.Add((2, tokB[j - 1]));
                j--;
            }
            else
            {
                rev.Add((1, tokA[i - 1]));
                i--;
            }
        }
        rev.Reverse();

        var left = new List<InlineSpan>();
        var right = new List<InlineSpan>();
        var leftBuf = new StringBuilder();
        var rightBuf = new StringBuilder();

        void FlushChanged()
        {
            if (leftBuf.Length == 0 && rightBuf.Length == 0) return;
            RefineChars(leftBuf.ToString(), rightBuf.ToString(), left, right);
            leftBuf.Clear();
            rightBuf.Clear();
        }

        // Walk the token alignment; equal tokens anchor and flush the accumulated changed block,
        // where the character-level refinement happens.
        foreach (var (side, text) in rev)
        {
            if (side == 0)
            {
                FlushChanged();
                AppendSpan(left, text, false);
                AppendSpan(right, text, false);
            }
            else if (side == 1) leftBuf.Append(text);
            else rightBuf.Append(text);
        }
        FlushChanged();
        return (left, right);
    }

    // ---- helpers ----

    /// <summary>
    /// Character-level LCS of one changed block: <paramref name="a"/> is the left text, <paramref
    /// name="b"/> the right. Appends shared characters as non-differing spans to both sides and the
    /// differing characters (marked) to their own side.
    /// </summary>
    private static void RefineChars(string a, string b, List<InlineSpan> left, List<InlineSpan> right)
    {
        if (a.Length == 0 && b.Length == 0) return;
        if (a.Length == 0) { AppendSpan(right, b, true); return; }
        if (b.Length == 0) { AppendSpan(left, a, true); return; }

        // Pathologically long single-line change: skip fine alignment, mark the whole block.
        if ((long)a.Length * b.Length > MaxInlineCells)
        {
            AppendSpan(left, a, true);
            AppendSpan(right, b, true);
            return;
        }

        var dp = LcsCharTable(a, b);
        int i = a.Length, j = b.Length;
        var rev = new List<(int side, char ch)>();  // side: 0 both, 1 left, 2 right
        while (i > 0 || j > 0)
        {
            if (i > 0 && j > 0 && a[i - 1] == b[j - 1])
            {
                rev.Add((0, a[i - 1]));
                i--; j--;
            }
            else if (j > 0 && (i == 0 || dp[i, j - 1] >= dp[i - 1, j]))
            {
                rev.Add((2, b[j - 1]));
                j--;
            }
            else
            {
                rev.Add((1, a[i - 1]));
                i--;
            }
        }
        rev.Reverse();

        // Batch consecutive same-side characters into one span so AppendSpan isn't called per char.
        int p = 0;
        var sb = new StringBuilder();
        while (p < rev.Count)
        {
            int side = rev[p].side;
            sb.Clear();
            while (p < rev.Count && rev[p].side == side) { sb.Append(rev[p].ch); p++; }
            string s = sb.ToString();
            if (side == 0) { AppendSpan(left, s, false); AppendSpan(right, s, false); }
            else if (side == 1) AppendSpan(left, s, true);
            else AppendSpan(right, s, true);
        }
    }

    private static int[,] LcsCharTable(string a, string b)
    {
        int m = a.Length, n = b.Length;
        var dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return dp;
    }

    private static void AppendSpan(List<InlineSpan> spans, string text, bool differs)
    {
        // Coalesce with the previous span when the flag matches, so runs render as one Run.
        if (spans.Count > 0 && spans[^1].Differs == differs)
            spans[^1] = new InlineSpan(spans[^1].Text + text, differs);
        else
            spans.Add(new InlineSpan(text, differs));
    }

    private static int[,] LcsTable(string[] a, string[] b)
    {
        int m = a.Length, n = b.Length;
        var dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        return dp;
    }

    /// <summary>Split into word runs and whitespace runs (matches diff.js's <c>/(\S+|\s+)/g</c>).</summary>
    private static string[] Tokenize(string s)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            int start = i;
            bool ws = char.IsWhiteSpace(s[i]);
            while (i < s.Length && char.IsWhiteSpace(s[i]) == ws) i++;
            tokens.Add(s.Substring(start, i - start));
        }
        return tokens.ToArray();
    }

    /// <summary>Fallback for pathologically large inputs: one change row spanning both whole texts.</summary>
    private static List<DiffOp> CoarseDiff(string[] a, string[] b)
    {
        var ops = new List<DiffOp>();
        int max = Math.Max(a.Length, b.Length);
        for (int i = 0; i < max; i++)
        {
            string? l = i < a.Length ? a[i] : null;
            string? r = i < b.Length ? b[i] : null;
            if (l == r) ops.Add(new DiffOp(DiffOpKind.Equal, l, r, i, i));
            else if (l is null) ops.Add(new DiffOp(DiffOpKind.Insert, null, r, -1, i));
            else if (r is null) ops.Add(new DiffOp(DiffOpKind.Delete, l, null, i, -1));
            else ops.Add(new DiffOp(DiffOpKind.Change, l, r, i, i));
        }
        return ops;
    }
}
