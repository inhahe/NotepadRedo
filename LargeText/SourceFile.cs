using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NotepadRedo.LargeText;

/// <summary>
/// An append-only list of longs, stored in fixed-size chunks so that growing it never copies the
/// values already stored, and so that one writer (the indexing thread) can append while readers on
/// other threads look up entries below <see cref="Count"/>. A reader takes the count first and the
/// chunk table second; the writer installs a chunk before publishing a count that covers it.
/// </summary>
internal sealed class LongList
{
    private const int Shift = 16;
    private const int ChunkSize = 1 << Shift;
    private const int Mask = ChunkSize - 1;

    private long[][] _chunks = new long[4][];
    private long _count;

    public long Count => Volatile.Read(ref _count);

    public long this[long index]
    {
        get
        {
            if ((ulong)index >= (ulong)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            var chunks = Volatile.Read(ref _chunks);
            return chunks[index >> Shift][index & Mask];
        }
    }

    /// <summary>Append one value. Only one thread may append at a time.</summary>
    public void Add(long value)
    {
        long n = _count;
        long chunk = n >> Shift;
        var chunks = _chunks;
        if (chunk >= chunks.Length)
        {
            var bigger = new long[chunks.Length * 2][];
            Array.Copy(chunks, bigger, chunks.Length);
            chunks = bigger;
            Volatile.Write(ref _chunks, chunks);
        }
        chunks[chunk] ??= new long[ChunkSize];
        chunks[chunk][n & Mask] = value;
        Volatile.Write(ref _count, n + 1);
    }

    /// <summary>The number of entries below <paramref name="count"/> that are less than
    /// <paramref name="value"/>, for a list kept in ascending order.</summary>
    public long CountBelow(long value, long count)
    {
        long lo = 0, hi = count;
        while (lo < hi)
        {
            long mid = (lo + hi) >>> 1;
            if (this[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>The index of the last entry not greater than <paramref name="value"/>, among the first
    /// <paramref name="count"/> of an ascending list; -1 when there is none.</summary>
    public long LastAtOrBelow(long value, long count) => CountBelow(value + 1, count) - 1;
}

/// <summary>
/// How a file's bytes become characters, as far as finding its lines is concerned. Detection matches
/// what the ordinary editor does (a <see cref="StreamReader"/> that honours a byte-order mark and
/// otherwise reads UTF-8), but the large-file document keeps the encoding and writes it back, BOM
/// included, so that lines nobody edited are saved byte for byte.
/// </summary>
internal sealed class TextCodec
{
    public required Encoding Encoding { get; init; }
    /// <summary>Bytes of byte-order mark at the start of the file (0 when there is none).</summary>
    public required int BomLength { get; init; }
    /// <summary>Bytes per code unit: 1 for UTF-8, 2 for UTF-16, 4 for UTF-32.</summary>
    public required int Unit { get; init; }
    public required bool BigEndian { get; init; }

    /// <summary>The mark the file started with, written back at the start of a save.</summary>
    public byte[] Preamble => BomLength == 0 ? Array.Empty<byte>() : Encoding.GetPreamble();

    public string Name => Encoding switch
    {
        UTF8Encoding => BomLength > 0 ? "UTF-8 with BOM" : "UTF-8",
        UnicodeEncoding => BigEndian ? "UTF-16 BE" : "UTF-16 LE",
        UTF32Encoding => BigEndian ? "UTF-32 BE" : "UTF-32 LE",
        _ => Encoding.WebName,
    };

    public static TextCodec Detect(ReadOnlySpan<byte> head)
    {
        // Invalid bytes decode to U+FFFD, as the ordinary editor's StreamReader does. A line nobody
        // edits is still saved from its original bytes, so loading never damages it.
        if (head.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return new() { Encoding = new UTF8Encoding(true), BomLength = 3, Unit = 1, BigEndian = false };
        if (head.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            return new() { Encoding = new UTF32Encoding(false, true), BomLength = 4, Unit = 4, BigEndian = false };
        if (head.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            return new() { Encoding = new UTF32Encoding(true, true), BomLength = 4, Unit = 4, BigEndian = true };
        if (head.StartsWith(new byte[] { 0xFF, 0xFE }))
            return new() { Encoding = new UnicodeEncoding(false, true), BomLength = 2, Unit = 2, BigEndian = false };
        if (head.StartsWith(new byte[] { 0xFE, 0xFF }))
            return new() { Encoding = new UnicodeEncoding(true, true), BomLength = 2, Unit = 2, BigEndian = true };
        return new() { Encoding = new UTF8Encoding(false), BomLength = 0, Unit = 1, BigEndian = false };
    }

    /// <summary>Whether the code unit at <paramref name="at"/> in <paramref name="data"/> is the
    /// ASCII character <paramref name="c"/>.</summary>
    public bool UnitIs(ReadOnlySpan<byte> data, int at, char c)
    {
        if (at < 0 || at + Unit > data.Length)
            return false;
        for (int i = 0; i < Unit; i++)
        {
            byte expected = (BigEndian ? i == Unit - 1 : i == 0) ? (byte)c : (byte)0;
            if (data[at + i] != expected)
                return false;
        }
        return true;
    }

    /// <summary>Whether a line may be cut at <paramref name="at"/> without splitting a character:
    /// the offset is relative to a run of bytes that starts on a character boundary.</summary>
    public bool IsCharBoundary(ReadOnlySpan<byte> data, int at)
    {
        if (at <= 0 || at >= data.Length)
            return true;
        switch (Unit)
        {
            case 1:
                return (data[at] & 0xC0) != 0x80;          // not a UTF-8 continuation byte
            case 2:
                if (at % 2 != 0)
                    return false;
                int hi = BigEndian ? data[at] : data[at + 1];
                return hi is < 0xDC or > 0xDF;               // not the low half of a surrogate pair
            default:
                return at % Unit == 0;
        }
    }
}

/// <summary>What <see cref="SourceFile.TryLock"/> found.</summary>
internal enum LockResult
{
    /// <summary>Locked (or reading a private copy, which nobody else writes).</summary>
    Locked,
    /// <summary>Another program has the file open for writing.</summary>
    InUse,
    /// <summary>The file no longer holds the bytes indexed.</summary>
    Changed,
}

/// <summary>Identity and version of a file, read through a handle.</summary>
internal readonly record struct FileStamp(long Length, DateTime LastWriteUtc, ulong FileId, uint Volume);

/// <summary>
/// A text file on disk, indexed by line without being read into memory: the byte offset of every
/// line's start, found by a background scan, and the lines read back on demand. This is the
/// "original" side of a <see cref="LargeDocument"/>'s piece table, so it must never change under
/// the document: <see cref="LargeDocument"/> takes care to lock the file, or to switch to a private
/// copy of it, before any edit refers to its lines.
///
/// <para>Lines longer than <see cref="MaxLineBytes"/> are cut into segments (on a character boundary)
/// so that no single line is too long to lay out. A segment that ends at such a cut is
/// <i>joined</i>: it has no line break after it, and the next line continues it.</para>
/// </summary>
internal sealed class SourceFile : IDisposable
{
    /// <summary>Longest line, in bytes, before it is cut into joined segments.</summary>
    public const int MaxLineBytes = 32 * 1024;

    private const int ReadChunk = 4 * 1024 * 1024;
    private const int FirstChunk = 256 * 1024;

    private SafeFileHandle _handle;
    private readonly object _handleLock = new();
    private readonly LongList _starts = new();
    private readonly LongList _joined = new();   // ascending line indexes that end at a cut
    private long _length;                         // bytes indexed so far
    private volatile bool _complete;
    private CancellationTokenSource? _cts;
    private Task? _indexTask;
    private long _crlf, _lf, _cr;
    private bool _crMode;                         // lines end in '\r' alone (no '\n' anywhere seen)

    public string Path { get; private set; }
    public TextCodec Codec { get; }
    public FileStamp Stamp { get; private set; }
    /// <summary>Whether the handle denies other writers.</summary>
    public bool Locked { get; private set; }
    /// <summary>
    /// Set when a save has replaced the file under its name. This handle still reads the old file,
    /// which no name refers to any more, so nobody can open it to change it: it needs no lock (and
    /// locking by name would lock the new file instead).
    /// </summary>
    public bool Replaced { get; set; }

    /// <summary>Set when the file read is a private copy rather than <see cref="Path"/> itself.</summary>
    public string? CopyPath { get; private set; }

    /// <summary>Bytes indexed so far: the file's length once <see cref="Complete"/>.</summary>
    public long IndexedLength => Volatile.Read(ref _length);
    public bool Complete => _complete;
    /// <summary>Error that stopped the index, if one did.</summary>
    public Exception? IndexError { get; private set; }

    /// <summary>Lines that can be read: every line once indexing is complete, and until then every
    /// line whose end has been found.</summary>
    public long LineCount => _complete ? _starts.Count : Math.Max(_extendFloor, _starts.Count - 1);

    /// <summary>While lines appended are indexed (<see cref="TryExtend"/>), the lines there were
    /// before: the last of them is still readable, only growing.</summary>
    private long _extendFloor;

    public LineEndingStyle DetectedLineEnding
    {
        get
        {
            long crlf = Interlocked.Read(ref _crlf), lf = Interlocked.Read(ref _lf), cr = Interlocked.Read(ref _cr);
            if (lf > crlf && lf >= cr) return LineEndingStyle.Lf;
            if (cr > crlf && cr > lf) return LineEndingStyle.Cr;
            return LineEndingStyle.Crlf;
        }
    }

    public bool MixedLineEndings =>
        (Interlocked.Read(ref _crlf) > 0 ? 1 : 0) + (Interlocked.Read(ref _lf) > 0 ? 1 : 0)
        + (Interlocked.Read(ref _cr) > 0 ? 1 : 0) > 1;

    private SourceFile(string path, SafeFileHandle handle, TextCodec codec, bool locked)
    {
        Path = path;
        _handle = handle;
        Codec = codec;
        Locked = locked;
        Stamp = StampOf(handle);
    }

    /// <summary>Open <paramref name="path"/> and start indexing it in the background.</summary>
    /// <param name="lockFile">Deny other programs write access while it is open.</param>
    public static SourceFile Open(string path, bool lockFile)
    {
        var handle = OpenHandle(path, lockFile);
        try
        {
            Span<byte> head = stackalloc byte[4];
            int n = RandomAccess.Read(handle, head, 0);
            var codec = TextCodec.Detect(head[..n]);
            var src = new SourceFile(path, handle, codec, lockFile);
            src.StartIndexing();
            return src;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A source for a file that has just been written from a known list of lines, so it needs no
    /// scan: <paramref name="starts"/> and <paramref name="joined"/> describe it already.
    /// </summary>
    public static SourceFile OpenIndexed(string path, bool lockFile, TextCodec codec, LongList starts,
                                         LongList joined, long length, LineEndingStyle style)
    {
        var handle = OpenHandle(path, lockFile);
        var src = new SourceFile(path, handle, codec, lockFile);
        for (long i = 0; i < starts.Count; i++)
            src._starts.Add(starts[i]);
        for (long i = 0; i < joined.Count; i++)
            src._joined.Add(joined[i]);
        src._length = length;
        src._crMode = style == LineEndingStyle.Cr;
        switch (style)
        {
            case LineEndingStyle.Lf: src._lf = 1; break;
            case LineEndingStyle.Cr: src._cr = 1; break;
            default: src._crlf = 1; break;
        }
        if (src.Stamp.Length != length)
        {
            src.Dispose();
            throw new IOException($"The file was {src.Stamp.Length:N0} bytes after saving, not the {length:N0} written.");
        }
        src._complete = true;
        return src;
    }

    private static SafeFileHandle OpenHandle(string path, bool lockFile) =>
        // Delete sharing even when locked: a save replaces the file by renaming over it, and a
        // rename or delete never changes the bytes this handle reads.
        File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            lockFile ? FileShare.Read | FileShare.Delete : FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);

    /// <summary>
    /// Stop other programs writing to the file, so that the lines an edit refers to stay what they
    /// are. <see cref="LockResult.InUse"/> when a program already has it open for writing (a log
    /// being written, say); <see cref="LockResult.Changed"/> when the bytes indexed are no longer
    /// what the file holds, so the index must be rebuilt before anything refers to it.
    /// </summary>
    public LockResult TryLock()
    {
        if (Locked || CopyPath is not null || Replaced)
            return LockResult.Locked;
        if (!StillIndexed())
            return LockResult.Changed;
        SafeFileHandle locked;
        try { locked = OpenHandle(Path, lockFile: true); }
        catch (IOException) { return LockResult.InUse; }
        catch (UnauthorizedAccessException) { return LockResult.InUse; }
        var s = StampOf(locked);
        // The name may now hold another file; and a write could have landed between the check above
        // and the lock, so the check is made again once nobody else can write.
        if (s.FileId != Stamp.FileId || s.Volume != Stamp.Volume || !StillIndexed(s))
        {
            locked.Dispose();
            return LockResult.Changed;
        }
        SwapHandle(locked);
        Locked = true;
        return LockResult.Locked;
    }

    /// <summary>
    /// Whether the file still holds the bytes indexed: unchanged since its stamp, or only appended
    /// to (the last bytes indexed are still there). A rewrite in place, even at the same size, is not.
    /// </summary>
    public bool StillIndexed(FileStamp? now = null)
    {
        var s = now ?? StampOf(CurrentHandle());
        if (s.Length == Stamp.Length && s.LastWriteUtc == Stamp.LastWriteUtc)
            return true;
        return s.Length >= IndexedLength && TailMatches();
    }

    /// <summary>Let other programs write again (the document no longer refers to these lines, or the
    /// user turned locking off while nothing does).</summary>
    public void Unlock()
    {
        if (!Locked || CopyPath is not null || Replaced)
            return;
        try
        {
            SwapHandle(OpenHandle(Path, lockFile: false));
            Locked = false;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Read from a private copy of the file from now on, for when it can't be locked but an edit
    /// must refer to its lines. Only the bytes indexed are copied, so a program that is appending
    /// to the file meanwhile doesn't disturb it. Runs on the calling thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file no longer holds the bytes indexed.</exception>
    public void SwitchToCopy(string copyPath, IProgress<double>? progress, CancellationToken ct)
    {
        if (CopyPath is not null)
            return;
        if (!StillIndexed())
            throw new InvalidOperationException("The file was changed by another program.");
        long length = IndexedLength;
        var buffer = new byte[ReadChunk];
        using (var output = new FileStream(copyPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            for (long pos = 0; pos < length;)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, length - pos);
                int got = Read(pos, buffer.AsSpan(0, want));
                if (got <= 0)
                    throw new IOException("The file became shorter while it was being copied.");
                output.Write(buffer, 0, got);
                pos += got;
                progress?.Report((double)pos / Math.Max(1, length));
            }
        }
        // Copied while a program could still write: if it rewrote what was indexed meanwhile, the
        // copy holds a mixture, so the copy is checked against the original's end once more.
        if (!StillIndexed())
        {
            try { File.Delete(copyPath); } catch { }
            throw new InvalidOperationException("The file was changed by another program while it was being copied.");
        }
        var copy = File.OpenHandle(copyPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        SwapHandle(copy);
        CopyPath = copyPath;
        // From now on the stamp is the copy's: it is what a recovery snapshot refers to.
        Stamp = StampOf(copy);
    }

    private void SwapHandle(SafeFileHandle handle)
    {
        SafeFileHandle old;
        lock (_handleLock)
        {
            old = _handle;
            _handle = handle;
        }
        old.Dispose();
    }

    // ===================== Indexing =====================

    private void StartIndexing()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _starts.Add(Codec.BomLength);
        _length = Codec.BomLength;
        _indexTask = Task.Run(() => IndexFrom(Codec.BomLength, Codec.BomLength, ct), ct);
    }

    /// <summary>Wait, briefly, until the first <paramref name="lines"/> lines are indexed, so a view
    /// opened on the file has its first screen to show at once.</summary>
    public void WaitForFirstLines(int lines, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!_complete && LineCount < lines && sw.Elapsed < timeout)
            Thread.Sleep(1);
    }

    /// <summary>Wait for the index to finish (tests, and saving).</summary>
    public void WaitForIndex()
    {
        try { _indexTask?.Wait(); }
        catch (AggregateException) { }
    }

    /// <summary>Scan from <paramref name="scanFrom"/> to the end of the file, adding a line start
    /// after every break. <paramref name="lineStart"/> is where the line being scanned began.</summary>
    private void IndexFrom(long lineStart, long scanFrom, CancellationToken ct)
    {
        try
        {
            long fileLength = RandomAccess.GetLength(CurrentHandle());
            int unit = Codec.Unit;
            var buffer = new byte[ReadChunk];
            bool decidedMode = scanFrom > Codec.BomLength;
            long pos = scanFrom;
            // The chunk is read from the line's start whenever that is near, so a cut can look at the
            // bytes around it; otherwise from where scanning resumes.
            // The first read is small, so the first screen of lines is there almost at once.
            int chunk = scanFrom == Codec.BomLength ? FirstChunk : ReadChunk;
            while (pos < fileLength)
            {
                ct.ThrowIfCancellationRequested();
                long chunkStart = Math.Max(pos - Math.Min(pos - lineStart, MaxLineBytes + 8), lineStart);
                chunkStart -= (chunkStart - Codec.BomLength) % unit;
                int want = (int)Math.Min(chunk, fileLength - chunkStart);
                chunk = ReadChunk;
                int got = Read(chunkStart, buffer.AsSpan(0, want));
                got -= got % unit;
                long chunkEnd = chunkStart + got;
                // Nothing new read: the file ends in part of a code unit (a UTF-16 file of odd
                // length), or it became shorter while it was read. Either way the scan is over.
                if (chunkEnd <= pos)
                    break;
                var data = new ReadOnlySpan<byte>(buffer, 0, got);

                if (!decidedMode)
                {
                    // A file with carriage returns but no line feed in its first few megabytes is a
                    // classic-Mac file: its lines end in '\r'.
                    bool hasLf = FindUnit(data, 0, '\n') >= 0;
                    _crMode = !hasLf && FindUnit(data, 0, '\r') >= 0;
                    decidedMode = true;
                }
                char nl = _crMode ? '\r' : '\n';

                int scan = (int)(pos - chunkStart);
                while (true)
                {
                    int found = FindUnit(data, scan, nl);
                    long limit = lineStart + MaxLineBytes;
                    if (found >= 0 && chunkStart + found - lineStart <= MaxLineBytes)
                    {
                        CountEnding(data, found);
                        lineStart = chunkStart + found + unit;
                        _starts.Add(lineStart);
                        scan = found + unit;
                        continue;
                    }
                    // No break within the limit. Cut the line if the bytes past the limit are here.
                    if (limit + unit < chunkEnd)
                    {
                        int at = (int)(limit - chunkStart);
                        at -= (int)((chunkStart + at - Codec.BomLength) % unit);
                        int min = (int)(lineStart - chunkStart) + unit;
                        while (at > min && !Codec.IsCharBoundary(data, at))
                            at -= unit;
                        _joined.Add(_starts.Count - 1);
                        lineStart = chunkStart + at;
                        _starts.Add(lineStart);
                        scan = at;
                        continue;
                    }
                    break;
                }
                pos = chunkEnd;
                Volatile.Write(ref _length, chunkEnd);
            }
            if (pos < fileLength)
            {
                // Stopped short. Bytes that make up no whole code unit still belong to the last line
                // (they are saved as they are); a file that shrank under the scan can't be trusted.
                long actual = RandomAccess.GetLength(CurrentHandle());
                if (actual < fileLength)
                    throw new IOException("The file became shorter while it was being read.");
                pos = fileLength;
            }
            Volatile.Write(ref _length, pos);
            _complete = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            IndexError = ex;
            _complete = true;   // what was indexed stays readable
        }
    }

    private void CountEnding(ReadOnlySpan<byte> data, int at)
    {
        if (_crMode) { _cr++; return; }
        if (Codec.UnitIs(data, at - Codec.Unit, '\r')) _crlf++;
        else _lf++;
    }

    /// <summary>The offset of the first code unit at or after <paramref name="from"/> that is the
    /// ASCII character <paramref name="c"/>, or -1.</summary>
    private int FindUnit(ReadOnlySpan<byte> data, int from, char c)
    {
        int unit = Codec.Unit;
        int lead = Codec.BigEndian ? unit - 1 : 0;
        while (from < data.Length)
        {
            int i = data[from..].IndexOf((byte)c);
            if (i < 0)
                return -1;
            int at = from + i - lead;
            if (at >= from && (at % unit) == 0 && Codec.UnitIs(data, at, c))
                return at;
            from += i + 1;
        }
        return -1;
    }

    /// <summary>
    /// Index what has been appended to the file since it was indexed, provided everything indexed is
    /// still there: the last line may grow, and new lines are added. Returns false (and indexes
    /// nothing) when the file is shorter or its end no longer matches, which is a rewrite rather than
    /// an append.
    /// </summary>
    public bool TryExtend()
    {
        if (!_complete || IndexError is not null || CopyPath is not null)
            return false;
        long old = IndexedLength;
        var stamp = StampOf(CurrentHandle());
        long now = stamp.Length;
        if (now < old)
            return false;
        // An append always makes the file longer: written to at the same length is a rewrite.
        if (now == old)
            return stamp.LastWriteUtc == Stamp.LastWriteUtc;
        if (!TailMatches())
            return false;
        _extendFloor = _starts.Count;
        _complete = false;
        // The last line was taken to end at the end of the file, and now continues.
        long lineStart = _starts[_starts.Count - 1];
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _indexTask = Task.Run(() => IndexFrom(lineStart, old, ct), ct);
        Stamp = StampOf(CurrentHandle());
        return true;
    }

    private byte[]? _tail;

    /// <summary>Remember the last bytes indexed, so <see cref="TryExtend"/> can tell an append from
    /// a rewrite.</summary>
    public void RememberTail()
    {
        long end = IndexedLength;
        int n = (int)Math.Min(4096, end);
        var tail = new byte[n];
        Read(end - n, tail);
        _tail = tail;
    }

    private bool TailMatches()
    {
        if (_tail is null)
            return false;
        var now = new byte[_tail.Length];
        int got = Read(IndexedLength - _tail.Length, now);
        return got == _tail.Length && now.AsSpan().SequenceEqual(_tail);
    }

    // ===================== Reading lines =====================

    private SafeFileHandle CurrentHandle()
    {
        lock (_handleLock)
            return _handle;
    }

    /// <summary>Read bytes at <paramref name="offset"/>; returns how many were read.</summary>
    public int Read(long offset, Span<byte> dest)
    {
        int total = 0;
        while (total < dest.Length)
        {
            SafeFileHandle h = CurrentHandle();
            int n;
            try { n = RandomAccess.Read(h, dest[total..], offset + total); }
            catch (ObjectDisposedException) when (!ReferenceEquals(h, CurrentHandle())) { continue; }
            if (n <= 0)
                break;
            total += n;
        }
        return total;
    }

    public long LineStart(long line) => _starts[line];

    /// <summary>Lines end in '\r' alone (a classic-Mac file).</summary>
    public bool CrMode => _crMode;

    /// <summary>How many lines from <paramref name="first"/> (at most <paramref name="max"/>, at least
    /// one) fit in <paramref name="budget"/> bytes, breaks included.</summary>
    public int LinesWithin(long first, int max, long budget)
    {
        long limit = LineStart(first) + budget;
        long lo = 1, hi = Math.Min(max, LineCount - first);
        while (lo < hi)
        {
            long mid = (lo + hi + 1) >>> 1;
            if (LineEnd(first + mid - 1) <= limit) lo = mid;
            else hi = mid - 1;
        }
        return (int)Math.Max(1, lo);
    }

    /// <summary>The offset just past line <paramref name="line"/>, its break included.</summary>
    public long LineEnd(long line) =>
        line + 1 < _starts.Count ? _starts[line + 1] : IndexedLength;

    /// <summary>Whether the line ends at a cut rather than a break (see the class summary).</summary>
    public bool IsJoined(long line)
    {
        long n = _joined.Count;
        if (n == 0)
            return false;
        long i = _joined.LastAtOrBelow(line, n);
        return i >= 0 && _joined[i] == line;
    }

    /// <summary>How many lines in [<paramref name="from"/>, <paramref name="to"/>) end at a cut.</summary>
    public long JoinedBetween(long from, long to)
    {
        long n = _joined.Count;
        if (n == 0 || to <= from)
            return 0;
        return _joined.CountBelow(to, n) - _joined.CountBelow(from, n);
    }

    /// <summary>Lines in [<paramref name="from"/>, <paramref name="to"/>) that end at a cut, ascending.</summary>
    public IEnumerable<long> JoinedIn(long from, long to)
    {
        long n = _joined.Count;
        for (long i = _joined.CountBelow(from, n); i < n && _joined[i] < to; i++)
            yield return _joined[i];
    }

    /// <summary>Bytes of line <paramref name="line"/>'s break: 0 for a joined line or the last one.</summary>
    public int TerminatorLength(long line, ReadOnlySpan<byte> lineBytes)
    {
        if (IsJoined(line))
            return 0;
        int unit = Codec.Unit;
        int end = lineBytes.Length;
        if (_crMode)
            return Codec.UnitIs(lineBytes, end - unit, '\r') ? unit : 0;
        if (!Codec.UnitIs(lineBytes, end - unit, '\n'))
            return 0;
        return Codec.UnitIs(lineBytes, end - 2 * unit, '\r') ? 2 * unit : unit;
    }

    /// <summary>The bytes of a line, break included.</summary>
    public byte[] LineBytes(long line)
    {
        long start = LineStart(line), end = LineEnd(line);
        var bytes = new byte[end - start];
        int got = Read(start, bytes);
        return got == bytes.Length ? bytes : bytes.AsSpan(0, got).ToArray();
    }

    /// <summary>The text of a line, without its break.</summary>
    public string LineText(long line)
    {
        var bytes = LineBytes(line);
        int term = TerminatorLength(line, bytes);
        return Codec.Encoding.GetString(bytes, 0, bytes.Length - term);
    }

    /// <summary>Decode lines [<paramref name="first"/>, first + <paramref name="count"/>) with one read,
    /// for scanning many lines quickly (search).</summary>
    public void LineTexts(long first, int count, List<string> into)
    {
        if (count <= 0)
            return;
        long start = LineStart(first), end = LineEnd(first + count - 1);
        var bytes = new byte[end - start];
        int got = Read(start, bytes);
        var span = bytes.AsSpan(0, got);
        for (int i = 0; i < count; i++)
        {
            long ls = LineStart(first + i) - start, le = LineEnd(first + i) - start;
            if (le > got)
                le = got;
            var lb = span[(int)Math.Min(ls, got)..(int)le];
            int term = TerminatorLength(first + i, lb);
            into.Add(Codec.Encoding.GetString(lb[..^term]));
        }
    }

    // ===================== Stamps =====================

    // Pack 4: the FILETIMEs are pairs of DWORDs, not 8-byte aligned.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation info);

    public static FileStamp StampOf(SafeFileHandle handle)
    {
        if (GetFileInformationByHandle(handle, out var info))
            return new FileStamp(((long)info.FileSizeHigh << 32) | info.FileSizeLow,
                DateTime.FromFileTimeUtc(info.LastWriteTime),
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.VolumeSerialNumber);
        return new FileStamp(RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle), 0, 0);
    }

    /// <summary>The stamp of whatever <paramref name="path"/> names now; null when it can't be opened.</summary>
    public static FileStamp? Probe(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return StampOf(h);
        }
        catch { return null; }
    }

    /// <summary>Whether <paramref name="s"/> names the same file as this one was opened on.</summary>
    public bool SameFile(FileStamp s) => s.FileId == Stamp.FileId && s.Volume == Stamp.Volume;

    /// <summary>
    /// A source read from a private copy <paramref name="copyPath"/> of <paramref name="originalPath"/>
    /// made earlier (see <see cref="SwitchToCopy"/>), when a document is recovered from a snapshot.
    /// </summary>
    public static SourceFile OpenCopy(string copyPath, string originalPath)
    {
        var src = Open(copyPath, lockFile: true);
        src.Path = originalPath;
        src.CopyPath = copyPath;
        return src;
    }

    public void Dispose() => Dispose(keepCopy: false);

    /// <param name="keepCopy">Leave a private copy on disk, because a recovery snapshot refers to it.</param>
    public void Dispose(bool keepCopy)
    {
        _cts?.Cancel();
        WaitForIndex();
        lock (_handleLock)
            _handle.Dispose();
        if (CopyPath is not null && !keepCopy)
            try { File.Delete(CopyPath); } catch { }
    }
}
