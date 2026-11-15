using System.Text;

namespace NotepadRedo.LargeText;

/// <summary>A match in a <see cref="LargeDocument"/>, from (Line, Col) to (EndLine, EndCol).</summary>
internal readonly record struct LargeMatch(long Line, int Col, long EndLine, int EndCol)
{
    public TextPos Start => new(Line, Col);
    public TextPos End => new(EndLine, EndCol);
}

/// <summary>
/// A frozen list of pieces (see <see cref="LargeDocument.Snapshot"/>) with its line numbering, which
/// a background search reads while the document goes on being edited.
/// </summary>
internal sealed class PieceView
{
    public IReadOnlyList<Piece> Pieces { get; }
    private readonly long[] _starts;

    public PieceView(IReadOnlyList<Piece> pieces)
    {
        Pieces = pieces;
        _starts = new long[pieces.Count + 1];
        for (int k = 0; k < pieces.Count; k++)
            _starts[k + 1] = _starts[k] + pieces[k].Count;
    }

    public long LineCount => _starts[^1];

    /// <summary>The piece holding a line, and the line's offset in it.</summary>
    public (Piece Piece, long Offset) PieceAt(long line)
    {
        int k = PieceIndex(line);
        return (Pieces[k], line - _starts[k]);
    }

    private int PieceIndex(long line)
    {
        int lo = 0, hi = Pieces.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_starts[mid] <= line) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Lines [first, first + count), with whether each is joined to the next.</summary>
    public void ReadLines(long first, int count, List<string> text, List<bool> joined)
    {
        long end = Math.Min(LineCount, first + count);
        long line = first;
        while (line < end)
        {
            int k = PieceIndex(line);
            var p = Pieces[k];
            long off = line - _starts[k];
            int n = (int)Math.Min(p.Count - off, end - line);
            if (p.Src is { } src)
            {
                src.LineTexts(p.Start + off, n, text);
                for (int i = 0; i < n; i++)
                    joined.Add(p.JoinedCount > 0 && src.IsJoined(p.Start + off + i));
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    var el = p.Lines![p.Offset + (int)off + i];
                    text.Add(el.Text);
                    joined.Add(el.Joined);
                }
            }
            line += n;
        }
    }

    /// <summary>The pieces that make up lines [start, start + count).</summary>
    public IEnumerable<Piece> Slice(long start, long count)
    {
        long end = start + count;
        if (count <= 0 || start >= LineCount)
            yield break;
        for (int k = PieceIndex(start); k < Pieces.Count && _starts[k] < end; k++)
        {
            long from = Math.Max(start, _starts[k]) - _starts[k];
            long to = Math.Min(end, _starts[k + 1]) - _starts[k];
            if (to > from)
                yield return Pieces[k].Slice(from, to - from);
        }
    }
}

/// <summary>
/// Searching and replacing in a <see cref="LargeDocument"/> without reading it all into memory: the
/// lines are taken a block at a time and searched with <see cref="SearchEngine"/> and
/// <see cref="ReplaceEngine"/>, the same code the ordinary editor uses, so an option means the same
/// thing in both. A block is a run of whole lines joined with "\n" (so <c>^</c> and <c>$</c> work
/// per line) and ends at a real line break, so no match is lost at a block boundary unless a regex
/// spans more lines than a block holds.
/// </summary>
internal static class LargeSearch
{
    /// <summary>Characters per block, roughly.</summary>
    private const int BlockChars = 1 << 20;
    /// <summary>Lines read from disk at a time while a block is filled.</summary>
    private const int ReadBatch = 2048;

    /// <summary>One block: its text, the document line it starts at, and where each line starts in it.</summary>
    internal sealed class Block
    {
        public long FirstLine;
        public string Text = "";
        public List<int> LineStarts = new();
        public List<bool> Joined = new();
        public long EndLine => FirstLine + LineStarts.Count;

        /// <summary>Characters of line <paramref name="i"/> of the block, its break not included.</summary>
        public int LineLength(int i)
        {
            int end = i + 1 < LineStarts.Count ? LineStarts[i + 1] - (Joined[i] ? 0 : 1) : Text.Length;
            return end - LineStarts[i];
        }

        public TextPos PosAt(int index)
        {
            int i = LineStarts.BinarySearch(index);
            if (i < 0) i = ~i - 1;
            return new TextPos(FirstLine + i, Math.Min(index - LineStarts[i], LineLength(i)));
        }

        /// <summary>The position of the end of a match: a match that took a line's break ends at
        /// the start of the next line.</summary>
        public TextPos EndPosAt(int index) => PosAt(index);
    }

    /// <summary>Accumulates a block's text: lines separated by "\n", and a joined line by nothing.
    /// The block itself doesn't end in a break, so no match runs off its end.</summary>
    private sealed class BlockBuilder(Block block)
    {
        private readonly StringBuilder _sb = new();
        private bool _pendingBreak;

        public int Length => _sb.Length;

        public void Add(ReadOnlySpan<char> line, bool joined)
        {
            if (_pendingBreak)
                _sb.Append('\n');
            block.LineStarts.Add(_sb.Length);
            block.Joined.Add(joined);
            _sb.Append(line);
            _pendingBreak = !joined;
        }

        public void Finish() => block.Text = _sb.ToString();
    }

    /// <summary>Build the block of lines starting at <paramref name="first"/>.</summary>
    internal static Block ReadBlock(PieceView view, long first, int maxChars = BlockChars)
    {
        var block = new Block { FirstLine = first };
        var builder = new BlockBuilder(block);
        long line = first;
        var text = new List<string>(ReadBatch);
        var joined = new List<bool>(ReadBatch);
        while (line < view.LineCount && builder.Length < maxChars)
        {
            var (piece, offset) = view.PieceAt(line);
            long left = piece.Count - offset;
            // Lines on disk with no cut among them: decode the whole run of bytes at once.
            if (piece.Src is { } src && piece.JoinedCount == 0)
            {
                long budget = Math.Max(1, (maxChars - builder.Length) * (long)src.Codec.Unit);
                int n = src.LinesWithin(piece.Start + offset, (int)Math.Min(left, int.MaxValue), budget);
                if (AddSourceLines(builder, src, piece.Start + offset, n))
                {
                    line += n;
                    continue;
                }
            }
            // Otherwise line by line, stopping at a real line break once the block is full (a run of
            // joined segments can carry it past, up to four times the size).
            text.Clear();
            joined.Clear();
            view.ReadLines(line, (int)Math.Min(left, ReadBatch), text, joined);
            if (text.Count == 0)
                break;
            int i = 0;
            for (; i < text.Count; i++)
            {
                builder.Add(text[i], joined[i]);
                if (builder.Length >= maxChars && (!joined[i] || builder.Length >= maxChars * 4))
                {
                    i++;
                    break;
                }
            }
            line += i;
            if (i < text.Count)
                break;
        }
        builder.Finish();
        return block;
    }

    /// <summary>Add <paramref name="count"/> lines of a source, none of them joined, by decoding
    /// their bytes in one go. False (and nothing added) in the rare case the decoded text doesn't
    /// split into the lines the index found, which the caller then reads one by one.</summary>
    private static bool AddSourceLines(BlockBuilder builder, SourceFile src, long first, int count)
    {
        long start = src.LineStart(first), end = src.LineEnd(first + count - 1);
        var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent((int)(end - start));
        char[]? chars = null;
        try
        {
            int got = src.Read(start, bytes.AsSpan(0, (int)(end - start)));
            var enc = src.Codec.Encoding;
            chars = System.Buffers.ArrayPool<char>.Shared.Rent(enc.GetMaxCharCount(got));
            int n = enc.GetChars(bytes, 0, got, chars, 0);
            var span = new ReadOnlySpan<char>(chars, 0, n);
            char nl = src.CrMode ? '\r' : '\n';
            // Check first, so a mismatch adds nothing.
            int breaks = span.Count(nl);
            bool lastHasBreak = n > 0 && span[^1] == nl;
            if (breaks != (lastHasBreak ? count : count - 1))
                return false;
            int pos = 0;
            for (int k = 0; k < count; k++)
            {
                int at = span[pos..].IndexOf(nl);
                var lineText = at < 0 ? span[pos..] : span.Slice(pos, at);
                if (!src.CrMode && at >= 0 && lineText.Length > 0 && lineText[^1] == '\r')
                    lineText = lineText[..^1];
                builder.Add(lineText, joined: false);
                pos = at < 0 ? span.Length : pos + at + 1;
            }
            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
            if (chars is not null)
                System.Buffers.ArrayPool<char>.Shared.Return(chars);
        }
    }

    /// <summary>Every match in the document, in order, delivered in batches.</summary>
    /// <param name="cap">Stop after this many.</param>
    /// <returns>Whether the cap stopped the search before the end.</returns>
    /// <exception cref="ArgumentException">A regex that doesn't parse.</exception>
    public static bool FindAll(PieceView view, string term, MatchOptions opt, int cap,
                               Action<List<LargeMatch>, double> batch, CancellationToken ct)
    {
        if (opt.Regex)
            SearchEngine.BuildRegex(term, opt.CaseSensitive);   // throw for a bad pattern up front
        long line = 0;
        long found = 0;
        while (line < view.LineCount)
        {
            ct.ThrowIfCancellationRequested();
            var block = ReadBlock(view, line);
            var list = new List<LargeMatch>();
            foreach (var m in SearchEngine.Occurrences(block.Text, term, opt, allowOverlap: true))
            {
                var s = block.PosAt(m.Start);
                var e = block.EndPosAt(m.End);
                list.Add(new LargeMatch(s.Line, s.Col, e.Line, e.Col));
                if (++found >= cap)
                {
                    batch(list, 1);
                    return true;
                }
            }
            line = block.EndLine;
            batch(list, (double)line / Math.Max(1, view.LineCount));
            if (block.LineStarts.Count == 0)
                break;
        }
        return false;
    }

    /// <summary>The first match starting at or after <paramref name="from"/> (forwards) or before it
    /// (backwards), wrapping round the end; null when there is none.</summary>
    public static LargeMatch? FindNext(PieceView view, string term, MatchOptions opt, TextPos from,
                                       bool backwards, IProgress<double>? progress, CancellationToken ct)
    {
        if (opt.Regex)
            SearchEngine.BuildRegex(term, opt.CaseSensitive);
        long total = Math.Max(1, view.LineCount);
        if (!backwards)
        {
            // From the caret's line to the end, then from the top to the caret's line.
            long line = from.Line;
            long scanned = 0;
            bool wrapped = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var block = ReadBlock(view, line);
                foreach (var m in SearchEngine.Occurrences(block.Text, term, opt, allowOverlap: true))
                {
                    var s = block.PosAt(m.Start);
                    if (!wrapped && s < from)
                        continue;
                    if (wrapped && s >= from)
                        return null;
                    var e = block.EndPosAt(m.End);
                    return new LargeMatch(s.Line, s.Col, e.Line, e.Col);
                }
                scanned += block.LineStarts.Count;
                line = block.EndLine;
                progress?.Report(Math.Min(1, (double)scanned / total));
                if (line >= view.LineCount || block.LineStarts.Count == 0)
                {
                    if (wrapped)
                        return null;
                    wrapped = true;
                    line = 0;
                }
                if (wrapped && line > from.Line)
                    return null;
            }
        }
        else
        {
            // Blocks ending at the caret's line, going up, then from the bottom back down to it.
            const int Step = 4096;
            long end = from.Line + 1;
            long scanned = 0;
            bool wrapped = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long start = Math.Max(0, end - Step);
                var block = ReadBlock(view, start, int.MaxValue / 8);
                LargeMatch? best = null;
                foreach (var m in SearchEngine.Occurrences(block.Text, term, opt, allowOverlap: true))
                {
                    var s = block.PosAt(m.Start);
                    if (s.Line >= end)
                        break;
                    if (!wrapped && s >= from)
                        break;
                    if (wrapped && s <= from)
                        continue;
                    var e = block.EndPosAt(m.End);
                    best = new LargeMatch(s.Line, s.Col, e.Line, e.Col);
                }
                if (best is not null)
                    return best;
                scanned += end - start;
                progress?.Report(Math.Min(1, (double)scanned / total));
                if (start == 0)
                {
                    if (wrapped)
                        return null;
                    wrapped = true;
                    end = view.LineCount;
                }
                else
                    end = start;
                if (wrapped && end <= from.Line)
                    return null;
            }
        }
    }

    /// <summary>
    /// Replace every match, building the new lines for the lines that change and keeping every other
    /// line as the piece it was. Returns the pieces for lines [Start, Start + Count) of the document,
    /// and how many replacements were made; null pieces when nothing matched.
    /// </summary>
    /// <exception cref="ArgumentException">A bad regex or replacement pattern.</exception>
    public static (long Start, long Count, List<Piece>? Pieces, long Replaced) ReplaceAll(
        PieceView view, string find, string replacement, MatchOptions opt,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (opt.Regex)
            SearchEngine.BuildRegex(find, opt.CaseSensitive);
        var result = new List<Piece>();
        long firstChanged = -1, keptFrom = -1, replaced = 0;
        long line = 0;
        var pending = new List<ELine>();

        void FlushPending()
        {
            if (pending.Count == 0) return;
            var arr = pending.ToArray();
            result.Add(Piece.FromLines(arr, 0, arr.Length));
            pending.Clear();
        }

        while (line < view.LineCount)
        {
            ct.ThrowIfCancellationRequested();
            var block = ReadBlock(view, line);
            var matches = ReplaceEngine.Find(block.Text, find, replacement, opt, 0, block.Text.Length);
            if (matches.Count > 0)
            {
                // Group the matches by the run of lines each touches; replace within each run.
                int mi = 0;
                while (mi < matches.Count)
                {
                    var s = block.PosAt(matches[mi].Start);
                    var e = block.EndPosAt(matches[mi].End);
                    long runFirst = s.Line, runLast = e.Line;
                    int mj = mi + 1;
                    while (mj < matches.Count && block.PosAt(matches[mj].Start).Line <= runLast)
                    {
                        runLast = Math.Max(runLast, block.EndPosAt(matches[mj].End).Line);
                        mj++;
                    }
                    if (firstChanged < 0)
                        firstChanged = keptFrom = runFirst;
                    if (runFirst > keptFrom)
                    {
                        // Lines between two runs stay the pieces they were.
                        FlushPending();
                        result.AddRange(view.Slice(keptFrom, runFirst - keptFrom));
                    }

                    int i0 = (int)(runFirst - block.FirstLine), i1 = (int)(runLast - block.FirstLine);
                    int textStart = block.LineStarts[i0];
                    int textEnd = block.LineStarts[i1] + block.LineLength(i1);
                    var sb = new StringBuilder();
                    int at = textStart;
                    for (int k = mi; k < mj; k++)
                    {
                        sb.Append(block.Text, at, matches[k].Start - at);
                        sb.Append(matches[k].Replacement);
                        at = matches[k].End;
                    }
                    sb.Append(block.Text, at, Math.Max(0, textEnd - at));
                    var lines = LargeDocument.SplitLines(sb.ToString());
                    for (int k = 0; k < lines.Count; k++)
                        pending.Add(new ELine(lines[k], k == lines.Count - 1 && block.Joined[i1]));
                    replaced += mj - mi;
                    keptFrom = runLast + 1;
                    mi = mj;
                }
            }
            line = block.EndLine;
            progress?.Report((double)line / Math.Max(1, view.LineCount));
            if (block.LineStarts.Count == 0)
                break;
        }
        if (firstChanged < 0)
            return (0, 0, null, 0);
        FlushPending();
        return (firstChanged, keptFrom - firstChanged, result, replaced);
    }
}
