using System.IO;
using System.Text;

namespace NotepadRedo.LargeText;

/// <summary>A place in a <see cref="LargeDocument"/>: a line, and a character offset within it.</summary>
internal readonly record struct TextPos(long Line, int Col) : IComparable<TextPos>
{
    public int CompareTo(TextPos other) =>
        Line != other.Line ? Line.CompareTo(other.Line) : Col.CompareTo(other.Col);

    public static bool operator <(TextPos a, TextPos b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPos a, TextPos b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPos a, TextPos b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPos a, TextPos b) => a.CompareTo(b) >= 0;
    public static TextPos Min(TextPos a, TextPos b) => a <= b ? a : b;
    public static TextPos Max(TextPos a, TextPos b) => a >= b ? a : b;
}

/// <summary>A line someone typed or pasted. <paramref name="Joined"/>: no line break follows it, the
/// next line continues it (see <see cref="SourceFile"/>).</summary>
internal readonly record struct ELine(string Text, bool Joined);

/// <summary>
/// A run of consecutive lines: either lines [<see cref="Start"/>, Start + <see cref="Count"/>) of a
/// source file, read from disk when needed, or lines held in memory. Immutable, so the undo history
/// can keep pieces the document no longer shows, and a search can scan a snapshot of the list
/// while the document is edited.
/// </summary>
internal sealed class Piece
{
    public readonly SourceFile? Src;
    public readonly long Start;
    public readonly ELine[]? Lines;
    public readonly int Offset;
    public readonly long Count;
    /// <summary>How many of the piece's lines are joined to the next.</summary>
    public readonly long JoinedCount;

    private Piece(SourceFile? src, long start, ELine[]? lines, int offset, long count, long joined)
    {
        Src = src; Start = start; Lines = lines; Offset = offset; Count = count; JoinedCount = joined;
    }

    public static Piece FromSource(SourceFile src, long start, long count) =>
        new(src, start, null, 0, count, src.JoinedBetween(start, start + count));

    public static Piece FromLines(ELine[] lines, int offset, int count)
    {
        long joined = 0;
        for (int i = offset; i < offset + count; i++)
            if (lines[i].Joined) joined++;
        return new(null, 0, lines, offset, count, joined);
    }

    /// <summary>Lines [<paramref name="from"/>, from + <paramref name="count"/>) of this piece.</summary>
    public Piece Slice(long from, long count)
    {
        if (from == 0 && count == Count)
            return this;
        return Src is not null
            ? FromSource(Src, Start + from, count)
            : FromLines(Lines!, Offset + (int)from, (int)count);
    }

    public bool IsJoined(long k) => Src is not null ? Src.IsJoined(Start + k) : Lines![Offset + (int)k].Joined;

    /// <summary>How many of the first <paramref name="k"/> lines are joined.</summary>
    public long JoinedBefore(long k)
    {
        if (JoinedCount == 0) return 0;
        if (Src is not null) return Src.JoinedBetween(Start, Start + k);
        long n = 0;
        for (int i = 0; i < k; i++)
            if (Lines![Offset + i].Joined) n++;
        return n;
    }
}

/// <summary>One step of the undo history: lines [<see cref="Start"/>, Start + RemovedCount) were
/// replaced by InsertedCount lines.</summary>
internal sealed class EditRecord
{
    public long Start;
    public required IReadOnlyList<Piece> Removed;
    public long RemovedCount;
    public required IReadOnlyList<Piece> Inserted;
    public long InsertedCount;
    public (TextPos Anchor, TextPos Caret) Before;
    public (TextPos Anchor, TextPos Caret) After;
    /// <summary>Typing that later typing may still join (see <see cref="LargeDocument.Replace"/>).</summary>
    public bool Open;
}

/// <summary>
/// The text of a large file, edited without ever being held in memory: a piece table whose pieces
/// are runs of whole lines, each either still on disk (<see cref="SourceFile"/>) or replaced by
/// lines typed in. An edit replaces a run of lines with new ones, which costs the same however large
/// the file. The undo history keeps the pieces each edit replaced, so undoing even a deletion of the
/// whole file only puts references back.
/// </summary>
internal sealed class LargeDocument : IDisposable
{
    private List<Piece> _pieces = new();
    /// <summary>_starts[k]: the document line where piece k begins; one more entry than pieces.</summary>
    private long[] _starts = { 0 };
    /// <summary>_joinedBefore[k]: joined lines before piece k.</summary>
    private long[] _joinedBefore = { 0 };

    private readonly List<EditRecord> _history = new();
    private int _undone;          // records at the end of _history that are undone
    private int _savedAt;         // history position of the version on disk; -1 when none is
    private readonly List<SourceFile> _sources = new();
    private readonly LineCache _cache = new(8192);

    /// <summary>The file this document currently reads unchanged lines from.</summary>
    public SourceFile Source { get; private set; }

    /// <summary>Bumped by every change of text, so views and searches can tell their data is stale.</summary>
    public long Version { get; private set; }

    public LargeDocument(SourceFile source)
    {
        Source = source;
        _sources.Add(source);
        SyncWithSource();
    }

    public long LineCount => _starts[^1];

    /// <summary>Joined lines in the whole document; when 0, document and logical lines coincide.</summary>
    public long JoinedCount => _joinedBefore[^1];

    public bool CanUndo => _history.Count - _undone > 0;
    public bool CanRedo => _undone > 0;
    public bool HasEdits => _history.Count > 0;
    public bool IsModified => HistoryPosition != _savedAt;
    private int HistoryPosition => _history.Count - _undone;

    /// <summary>
    /// Take in lines the source has indexed since: only while the document is the source unchanged,
    /// which is the only time its line count can still grow (an edit locks the file or switches to a
    /// copy first). Returns whether the line count changed.
    /// </summary>
    private long _syncedLength = -1;

    public bool SyncWithSource()
    {
        if (HasEdits)
            return false;
        long n = Source.LineCount;
        // The length counts too: text appended to the last line changes no line count.
        long length = Source.IndexedLength;
        if (_pieces.Count == 1 && _pieces[0].Src == Source && _pieces[0].Start == 0 && _pieces[0].Count == n
            && _joinedBefore[^1] == _pieces[0].JoinedCount && length == _syncedLength)
            return false;
        _syncedLength = length;
        _pieces = n > 0 ? new List<Piece> { Piece.FromSource(Source, 0, n) } : new List<Piece>();
        Rebuild();
        _cache.Clear();
        Version++;
        return true;
    }

    /// <summary>Start again from a freshly opened file (a reload): forget the history and every
    /// piece of the old one.</summary>
    public void ReplaceSource(SourceFile source)
    {
        foreach (var s in _sources)
            if (!ReferenceEquals(s, source))
                s.Dispose();
        _sources.Clear();
        _sources.Add(source);
        Source = source;
        _history.Clear();
        _undone = 0;
        _savedAt = 0;
        _pieces = new List<Piece>();
        Rebuild();
        SyncWithSource();
        _cache.Clear();
        Version++;
    }

    // ===================== Reading =====================

    private int PieceAt(long line)
    {
        // The last start not greater than the line.
        int lo = 0, hi = _pieces.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_starts[mid] <= line) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    public string GetLine(long line)
    {
        if (line < 0 || line >= LineCount)
            return "";
        int k = PieceAt(line);
        var p = _pieces[k];
        long i = line - _starts[k];
        if (p.Src is null)
            return p.Lines![p.Offset + (int)i].Text;
        long srcLine = p.Start + i;
        if (_cache.TryGet(p.Src, srcLine, out var text))
            return text;
        text = p.Src.LineText(srcLine);
        _cache.Add(p.Src, srcLine, text);
        return text;
    }

    public int LineLength(long line) => GetLine(line).Length;

    /// <summary>Whether the line has no break after it (see <see cref="ELine"/>).</summary>
    public bool IsJoined(long line)
    {
        if (line < 0 || line >= LineCount || JoinedCount == 0)
            return false;
        int k = PieceAt(line);
        return _pieces[k].IsJoined(line - _starts[k]);
    }

    /// <summary>The 0-based logical line (counting only real line breaks) that holds a document line.</summary>
    public long LogicalLine(long line)
    {
        if (JoinedCount == 0 || line <= 0)
            return line;
        line = Math.Min(line, LineCount - 1);
        int k = PieceAt(line);
        return line - _joinedBefore[k] - _pieces[k].JoinedBefore(line - _starts[k]);
    }

    /// <summary>The first document line of a 0-based logical line.</summary>
    public long FromLogicalLine(long logical)
    {
        if (JoinedCount == 0)
            return Math.Clamp(logical, 0, Math.Max(0, LineCount - 1));
        long lo = 0, hi = LineCount - 1;
        while (lo < hi)
        {
            long mid = (lo + hi) >>> 1;
            if (LogicalLine(mid) < logical) lo = mid + 1;
            else hi = mid;
        }
        // Back up to the segment that starts the logical line.
        while (lo > 0 && IsJoined(lo - 1))
            lo--;
        return lo;
    }

    public long LogicalLineCount => LineCount - JoinedCount;

    /// <summary>The text between two positions, with "\r\n" for each line break.</summary>
    public string GetText(TextPos a, TextPos b)
    {
        if (b < a) (a, b) = (b, a);
        if (a.Line == b.Line)
        {
            var line = GetLine(a.Line);
            int s = Math.Min(a.Col, line.Length), e = Math.Min(b.Col, line.Length);
            return line.Substring(s, Math.Max(0, e - s));
        }
        var sb = new StringBuilder();
        var first = GetLine(a.Line);
        sb.Append(first, Math.Min(a.Col, first.Length), first.Length - Math.Min(a.Col, first.Length));
        for (long l = a.Line; l < b.Line; l++)
        {
            if (!IsJoined(l))
                sb.Append("\r\n");
            if (l + 1 < b.Line)
                sb.Append(GetLine(l + 1));
        }
        var last = GetLine(b.Line);
        sb.Append(last, 0, Math.Min(b.Col, last.Length));
        return sb.ToString();
    }

    /// <summary>A snapshot of the pieces, for scanning on another thread while the document changes.</summary>
    public IReadOnlyList<Piece> Snapshot() => _pieces.ToArray();

    // ===================== Editing =====================

    /// <summary>
    /// Replace the text between <paramref name="a"/> and <paramref name="b"/> with
    /// <paramref name="text"/> (whose line breaks may be any of CRLF, LF or CR).
    /// </summary>
    /// <param name="typing">Typed rather than pasted or set in code: it joins the open typing step
    /// when it falls inside the lines that step touched (and <paramref name="joinTyping"/> allows,
    /// that is the pause since the last keystroke was short), so one undo takes back a burst of typing.</param>
    /// <returns>The position just after the inserted text.</returns>
    public TextPos Replace(TextPos a, TextPos b, string text, (TextPos, TextPos) selectionBefore,
                           bool typing = false, bool joinTyping = false)
    {
        if (b < a) (a, b) = (b, a);
        a = Clamp(a);
        b = Clamp(b);
        string first = GetLine(a.Line);
        string last = b.Line == a.Line ? first : GetLine(b.Line);
        string prefix = first[..a.Col];
        string suffix = last[b.Col..];
        bool lastJoined = IsJoined(b.Line);

        var parts = SplitLines(text);
        var lines = new ELine[parts.Count];
        TextPos end;
        if (parts.Count == 1)
        {
            lines[0] = new ELine(prefix + parts[0] + suffix, lastJoined);
            end = new TextPos(a.Line, prefix.Length + parts[0].Length);
        }
        else
        {
            lines[0] = new ELine(prefix + parts[0], false);
            for (int i = 1; i < parts.Count - 1; i++)
                lines[i] = new ELine(parts[i], false);
            lines[^1] = new ELine(parts[^1] + suffix, lastJoined);
            end = new TextPos(a.Line + parts.Count - 1, parts[^1].Length);
        }

        long count = b.Line - a.Line + 1;
        var inserted = new[] { Piece.FromLines(lines, 0, lines.Length) };
        Apply(a.Line, count, inserted, selectionBefore, (end, end), typing, joinTyping);
        return end;
    }

    /// <summary>
    /// Replace whole lines [<paramref name="start"/>, start + <paramref name="count"/>) with the given
    /// pieces, as one undo step. Used by Replace All, which builds the new lines on another thread.
    /// </summary>
    public void ReplaceLines(long start, long count, IReadOnlyList<Piece> pieces,
                             (TextPos, TextPos) selectionBefore, (TextPos, TextPos) selectionAfter) =>
        Apply(start, count, pieces, selectionBefore, selectionAfter, typing: false, joinTyping: false);

    private void Apply(long start, long count, IReadOnlyList<Piece> inserted,
                       (TextPos, TextPos) before, (TextPos, TextPos) after, bool typing, bool joinTyping)
    {
        long insertedCount = inserted.Sum(p => p.Count);
        var removed = Splice(start, count, inserted);

        // Anything undone is gone for good once something new is done.
        if (_undone > 0)
        {
            if (_savedAt > HistoryPosition)
                _savedAt = -1;
            _history.RemoveRange(_history.Count - _undone, _undone);
            _undone = 0;
        }

        var top = _history.Count > 0 ? _history[^1] : null;
        if (typing && joinTyping && top is { Open: true } && HistoryPosition != _savedAt
            && start >= top.Start && start + count <= top.Start + top.InsertedCount)
        {
            top.InsertedCount += insertedCount - count;
            top.Inserted = SlicePieces(top.Start, top.InsertedCount);
            top.After = after;
            return;
        }
        if (top is not null)
            top.Open = false;
        _history.Add(new EditRecord
        {
            Start = start, Removed = removed, RemovedCount = count,
            Inserted = inserted, InsertedCount = insertedCount,
            Before = before, After = after, Open = typing,
        });
    }

    /// <summary>End the typing step, so the next keystroke starts a new one (a save does this, as
    /// does moving the caret away).</summary>
    public void CloseTypingStep()
    {
        if (_history.Count > 0)
            _history[^1].Open = false;
    }

    /// <summary>Undo one step; returns the selection to restore, or null when there is nothing to undo.</summary>
    public (TextPos Anchor, TextPos Caret)? Undo()
    {
        if (!CanUndo)
            return null;
        var r = _history[_history.Count - _undone - 1];
        r.Open = false;
        Splice(r.Start, r.InsertedCount, r.Removed);
        _undone++;
        return r.Before;
    }

    public (TextPos Anchor, TextPos Caret)? Redo()
    {
        if (!CanRedo)
            return null;
        var r = _history[_history.Count - _undone];
        Splice(r.Start, r.RemovedCount, r.Inserted);
        _undone--;
        return r.After;
    }

    private TextPos Clamp(TextPos p)
    {
        long line = Math.Clamp(p.Line, 0, Math.Max(0, LineCount - 1));
        return new TextPos(line, Math.Clamp(p.Col, 0, LineLength(line)));
    }

    public TextPos ClampPos(TextPos p) => Clamp(p);

    /// <summary>Lines of <paramref name="text"/>, split at every CRLF, LF and CR.</summary>
    internal static List<string> SplitLines(string text)
    {
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\r' && c != '\n')
                continue;
            parts.Add(text[start..i]);
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }
        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>Swap lines [start, start+count) for <paramref name="insert"/>; returns the pieces removed.</summary>
    private List<Piece> Splice(long start, long count, IReadOnlyList<Piece> insert)
    {
        int i0 = SplitAt(start);
        int i1 = SplitAt(start + count);
        var removed = _pieces.GetRange(i0, i1 - i0);
        _pieces.RemoveRange(i0, i1 - i0);
        _pieces.InsertRange(i0, insert.Where(p => p.Count > 0));
        // A document always has at least one line, if only an empty one.
        if (_pieces.Count == 0)
            _pieces.Add(Piece.FromLines(new[] { new ELine("", false) }, 0, 1));
        Rebuild();
        Version++;
        return removed;
    }

    /// <summary>Make a piece boundary at document line <paramref name="line"/>; returns the index of
    /// the piece that starts there (the piece count at the end of the document).</summary>
    private int SplitAt(long line)
    {
        if (line >= LineCount)
            return _pieces.Count;
        int k = PieceAt(line);
        long offset = line - _starts[k];
        if (offset == 0)
            return k;
        var p = _pieces[k];
        _pieces[k] = p.Slice(0, offset);
        _pieces.Insert(k + 1, p.Slice(offset, p.Count - offset));
        Rebuild();
        return k + 1;
    }

    /// <summary>The pieces that make up lines [start, start+count), without changing the document.</summary>
    private List<Piece> SlicePieces(long start, long count)
    {
        var result = new List<Piece>();
        long end = start + count;
        for (int k = start < LineCount ? PieceAt(start) : _pieces.Count; k < _pieces.Count && _starts[k] < end; k++)
        {
            long from = Math.Max(start, _starts[k]) - _starts[k];
            long to = Math.Min(end, _starts[k + 1]) - _starts[k];
            if (to > from)
                result.Add(_pieces[k].Slice(from, to - from));
        }
        return result;
    }

    private void Rebuild()
    {
        int n = _pieces.Count;
        var starts = new long[n + 1];
        var joined = new long[n + 1];
        for (int k = 0; k < n; k++)
        {
            starts[k + 1] = starts[k] + _pieces[k].Count;
            joined[k + 1] = joined[k] + _pieces[k].JoinedCount;
        }
        _starts = starts;
        _joinedBefore = joined;
    }

    // ===================== Saving =====================

    /// <summary>
    /// Write the document to <paramref name="output"/> in its source's encoding. Lines still on disk
    /// are copied byte for byte, line breaks included, unless <paramref name="convertEndings"/> asks
    /// for every break to become <paramref name="ending"/>; lines typed in always get it.
    /// </summary>
    /// <param name="starts">Receives the byte offset of every line written, and
    /// <paramref name="joined"/> every joined line, so the file just written needs no index scan.</param>
    public long WriteTo(Stream output, LineEndingStyle ending, bool convertEndings, LongList starts,
                        LongList joined, IProgress<double>? progress, CancellationToken ct)
    {
        var codec = Source.Codec;
        var enc = codec.Encoding;
        byte[] newline = enc.GetBytes(LineEndings.Sequence(ending));
        var pieces = _pieces.ToArray();
        long total = LineCount;
        long pos = 0;
        var buffer = new byte[1 << 20];
        long written = 0, budget = Math.Max(1, EstimateBytes(pieces));

        void Put(ReadOnlySpan<byte> bytes)
        {
            output.Write(bytes);
            pos += bytes.Length;
        }

        Put(codec.Preamble);
        long line = 0;
        foreach (var p in pieces)
        {
            ct.ThrowIfCancellationRequested();
            if (p.Src is { } src && !convertEndings)
            {
                // Byte offsets of the run's lines in the output follow from the source's by one shift.
                long first = p.Start, lastSrc = p.Start + p.Count - 1;
                long from = src.LineStart(first);
                long to = src.LineEnd(lastSrc);
                bool isDocLast = line + p.Count == total;
                var lastBytes = src.LineBytes(lastSrc);
                int term = src.TerminatorLength(lastSrc, lastBytes);
                if (isDocLast)
                    to -= term;
                long shift = pos - from;
                for (long i = first; i <= lastSrc; i++)
                    starts.Add(src.LineStart(i) + shift);
                foreach (long j in src.JoinedIn(first, lastSrc + 1))
                    joined.Add(line + (j - first));
                for (long at = from; at < to;)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buffer.Length, to - at);
                    int got = src.Read(at, buffer.AsSpan(0, want));
                    if (got <= 0)
                        throw new IOException($"{src.Path} could not be read past byte {at:N0}.");
                    Put(buffer.AsSpan(0, got));
                    at += got;
                    written += got;
                    progress?.Report((double)written / budget);
                }
                if (!isDocLast && term == 0 && !src.IsJoined(lastSrc))
                    Put(newline);   // the source's last line, which had no break, now has a line after it
                line += p.Count;
                continue;
            }

            if (p.Src is { } cs)
            {
                // Every break rewritten: each line's own bytes, then the new break. The bytes are
                // copied rather than decoded and encoded again, so nothing but the breaks changes
                // (bytes that aren't valid in the encoding included).
                for (long k = 0; k < p.Count;)
                {
                    ct.ThrowIfCancellationRequested();
                    long first = p.Start + k;
                    int n = cs.LinesWithin(first, (int)Math.Min(p.Count - k, int.MaxValue), buffer.Length);
                    long from = cs.LineStart(first);
                    int span = (int)(cs.LineEnd(first + n - 1) - from);
                    var run = span <= buffer.Length ? buffer : new byte[span];   // one line longer than the buffer
                    int got = cs.Read(from, run.AsSpan(0, span));
                    if (got < span)
                        throw new IOException($"{cs.Path} could not be read past byte {from + got:N0}.");
                    for (int i = 0; i < n; i++, k++, line++)
                    {
                        long sl = first + i;
                        var lb = run.AsSpan((int)(cs.LineStart(sl) - from), (int)(cs.LineEnd(sl) - cs.LineStart(sl)));
                        int term = cs.TerminatorLength(sl, lb);
                        starts.Add(pos);
                        Put(lb[..^term]);
                        written += lb.Length;
                        if (cs.IsJoined(sl))
                            joined.Add(line);
                        else if (line + 1 < total)
                            Put(newline);
                    }
                    progress?.Report((double)written / budget);
                }
                continue;
            }

            for (long k = 0; k < p.Count; k++, line++)
            {
                if ((k & 0xFFF) == 0)
                    ct.ThrowIfCancellationRequested();
                starts.Add(pos);
                var el = p.Lines![p.Offset + (int)k];
                string text = el.Text;
                bool isJoined = el.Joined;
                var bytes = enc.GetBytes(text);
                Put(bytes);
                written += bytes.Length;
                if (isJoined)
                    joined.Add(line);
                else if (line + 1 < total)
                    Put(newline);
                if ((k & 0xFF) == 0)
                    progress?.Report((double)written / budget);
            }
        }
        output.Flush();
        return pos;
    }

    private static long EstimateBytes(Piece[] pieces)
    {
        long n = 0;
        foreach (var p in pieces)
            if (p.Src is { } s && p.Count > 0)
                n += s.LineEnd(p.Start + p.Count - 1) - s.LineStart(p.Start);
            else if (p.Lines is not null)
                for (int i = 0; i < p.Count; i++)
                    n += p.Lines[p.Offset + i].Text.Length + 2;
        return n;
    }

    /// <summary>
    /// After a save: the file now holds exactly the document, so read every line from it, as one
    /// piece. The old source stays open while the undo history still refers to it.
    /// </summary>
    public void AdoptSaved(SourceFile saved)
    {
        if (!_sources.Contains(saved))
            _sources.Add(saved);
        Source = saved;
        _pieces = new List<Piece> { Piece.FromSource(saved, 0, saved.LineCount) };
        Rebuild();
        // Each history record keeps its own pieces, so positions stay right; only the marker moves.
        _savedAt = HistoryPosition;
        CloseTypingStep();
        Version++;
        ReleaseUnusedSources();
    }

    /// <summary>Close sources neither the document nor its history refers to any more.</summary>
    private void ReleaseUnusedSources()
    {
        var used = new HashSet<SourceFile> { Source };
        foreach (var p in _pieces)
            if (p.Src is not null) used.Add(p.Src);
        foreach (var r in _history)
        {
            foreach (var p in r.Removed) if (p.Src is not null) used.Add(p.Src);
            foreach (var p in r.Inserted) if (p.Src is not null) used.Add(p.Src);
        }
        foreach (var s in _sources.ToArray())
            if (!used.Contains(s))
            {
                _sources.Remove(s);
                _cache.Forget(s);
                s.Dispose();
            }
    }

    /// <summary>The sources the document's lines are read from now (not those only its history keeps).</summary>
    public IEnumerable<SourceFile> CurrentSources() =>
        _pieces.Where(p => p.Src is not null).Select(p => p.Src!).Distinct();

    /// <summary>The sources kept, besides <see cref="Source"/>, only for the undo history.</summary>
    public IEnumerable<SourceFile> OlderSources() => _sources.Where(s => !ReferenceEquals(s, Source));

    public void MarkSaved()
    {
        _savedAt = HistoryPosition;
        CloseTypingStep();
    }

    public void Dispose() => Dispose(keepCopies: false);

    /// <param name="keepCopies">Leave private copies of files on disk for a recovery snapshot.</param>
    public void Dispose(bool keepCopies)
    {
        foreach (var s in _sources)
            s.Dispose(keepCopies);
        _sources.Clear();
    }

    /// <summary>Most-recently-used decoded lines of the sources, keyed by source and line.</summary>
    private sealed class LineCache
    {
        private readonly int _capacity;
        private readonly Dictionary<(SourceFile, long), LinkedListNode<(SourceFile Src, long Line, string Text)>> _map = new();
        private readonly LinkedList<(SourceFile Src, long Line, string Text)> _order = new();

        public LineCache(int capacity) => _capacity = capacity;

        public bool TryGet(SourceFile src, long line, out string text)
        {
            lock (_map)
            {
                if (_map.TryGetValue((src, line), out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    text = node.Value.Text;
                    return true;
                }
            }
            text = "";
            return false;
        }

        public void Add(SourceFile src, long line, string text)
        {
            lock (_map)
            {
                if (_map.ContainsKey((src, line)))
                    return;
                var node = _order.AddFirst((src, line, text));
                _map[(src, line)] = node;
                while (_order.Count > _capacity)
                {
                    var last = _order.Last!;
                    _order.RemoveLast();
                    _map.Remove((last.Value.Src, last.Value.Line));
                }
            }
        }

        public void Clear()
        {
            lock (_map) { _map.Clear(); _order.Clear(); }
        }

        public void Forget(SourceFile src)
        {
            lock (_map)
                for (var node = _order.First; node is not null;)
                {
                    var next = node.Next;
                    if (ReferenceEquals(node.Value.Src, src))
                    {
                        _map.Remove((src, node.Value.Line));
                        _order.Remove(node);
                    }
                    node = next;
                }
        }
    }
}
