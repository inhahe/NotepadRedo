// Checks of large-file mode (see "Large files" in design.md): the line index, the piece table, its
// undo history, saving, search and replace, against the plain string operations they stand in for.

using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using NotepadRedo;
using NotepadRedo.LargeText;

internal static class LargeFileTests
{
    private static string Dir = "";

    private static int Case(string filter, string name, Func<string?> body)
    {
        string label = "[large] " + name;
        if (!label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return 0;
        string? failure;
        try { failure = body(); }
        catch (Exception ex) { failure = ex.ToString(); }
        Console.WriteLine(failure is null ? $"ok   {label}" : $"FAIL {label}: {failure}");
        return failure is null ? 0 : 1;
    }

    /// <summary>The model checks: no UI. Returns the number that failed.</summary>
    public static int CheckModel(string filter)
    {
        Dir = Path.Combine(Path.GetTempPath(), "nr-large-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Dir);
        int failed = 0;
        try
        {
            foreach (var (name, bytes, expected) in Samples())
            {
                failed += Case(filter, $"Indexing reads every line: {name}", () => CheckIndex(bytes, expected));
                failed += Case(filter, $"Saving an unedited file writes it byte for byte: {name}", () => CheckRoundTrip(bytes));
            }
            failed += Case(filter, "A line longer than the limit is cut into joined segments, on character boundaries", CheckCuts);
            failed += Case(filter, "Random edits match the same edits made to a string, and undo and redo retrace them", () => CheckRandomEdits(1));
            failed += Case(filter, "Random edits in a file with cut lines and CR line ends", () => CheckRandomEdits(2));
            failed += Case(filter, "Saving an edited file writes its text, and keeps unedited lines' own line ends", CheckSaveEdited);
            failed += Case(filter, "Undo reaches past a save", CheckUndoPastSave);
            failed += Case(filter, "Typing within the pause window is one undo step; elsewhere it is another", CheckTypingSteps);
            failed += Case(filter, "Search finds what a search of the whole text finds", CheckSearch);
            failed += Case(filter, "Find next and previous wrap round, starting from a position", CheckFindNext);
            failed += Case(filter, "Replace All gives what replacing in the whole text gives", CheckReplaceAll);
            failed += Case(filter, "Lines appended to the file are indexed without reading it again", CheckAppend);
            failed += Case(filter, "A file another program is writing can't be locked, and is copied instead", CheckLockAndCopy);
        }
        finally
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
        return failed;
    }

    // ===================== Samples =====================

    /// <summary>Files, and the logical lines each should read as.</summary>
    private static IEnumerable<(string, byte[], string[])> Samples()
    {
        var utf8 = new UTF8Encoding(false);
        (string, byte[], string[]) Text(string name, string text, Encoding enc, bool bom, string[] lines)
        {
            var body = enc.GetBytes(text);
            return (name, bom ? enc.GetPreamble().Concat(body).ToArray() : body, lines);
        }
        yield return Text("CRLF", "one\r\ntwo\r\nthree", utf8, false, new[] { "one", "two", "three" });
        yield return Text("LF with a final break", "one\ntwo\n", utf8, false, new[] { "one", "two", "" });
        yield return Text("CR", "one\rtwo\rthree\r", utf8, false, new[] { "one", "two", "three", "" });
        yield return Text("mixed", "a\r\nb\nc\r\n\r\nd", utf8, false, new[] { "a", "b", "c", "", "d" });
        yield return Text("empty", "", utf8, false, new[] { "" });
        yield return Text("UTF-8 with BOM", "é\r\nñ", new UTF8Encoding(true), true, new[] { "é", "ñ" });
        yield return Text("UTF-16 LE", "α\r\nβγ\r\n", new UnicodeEncoding(false, true), true, new[] { "α", "βγ", "" });
        yield return Text("UTF-16 BE", "α\nβ\n𝄞", new UnicodeEncoding(true, true), true, new[] { "α", "β", "𝄞" });
        yield return Text("UTF-32", "x\ny", new UTF32Encoding(false, true), true, new[] { "x", "y" });
        yield return ("invalid UTF-8", new byte[] { (byte)'a', 0xFF, (byte)'\n', 0xC3 }, new[] { "a\uFFFD", "\uFFFD" });
    }

    private static string Write(byte[] bytes)
    {
        string path = Path.Combine(Dir, Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static SourceFile OpenIndexed(string path, bool lockFile = false)
    {
        var src = SourceFile.Open(path, lockFile);
        src.WaitForIndex();
        if (src.IndexError is not null)
            throw src.IndexError;
        return src;
    }

    /// <summary>The document's logical lines: joined segments put back together.</summary>
    private static List<string> LogicalLines(LargeDocument doc)
    {
        var lines = new List<string>();
        var sb = new StringBuilder();
        for (long i = 0; i < doc.LineCount; i++)
        {
            sb.Append(doc.GetLine(i));
            if (!doc.IsJoined(i))
            {
                lines.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            lines.Add(sb.ToString());
        return lines;
    }

    private static string AllText(LargeDocument doc) =>
        doc.GetText(new TextPos(0, 0), new TextPos(doc.LineCount - 1, doc.LineLength(doc.LineCount - 1)));

    private static string? Compare(IReadOnlyList<string> actual, IReadOnlyList<string> expected)
    {
        if (actual.Count != expected.Count)
            return $"{actual.Count} lines, expected {expected.Count}: [{string.Join("|", actual.Take(8))}]";
        for (int i = 0; i < actual.Count; i++)
            if (actual[i] != expected[i])
                return $"line {i} is \"{Escape(actual[i])}\", expected \"{Escape(expected[i])}\"";
        return null;
    }

    private static string Escape(string s) =>
        s.Length > 80 ? s[..80].Replace("\r", "\\r").Replace("\n", "\\n") + "…" : s.Replace("\r", "\\r").Replace("\n", "\\n");

    // ===================== Index and round trip =====================

    private static string? CheckIndex(byte[] bytes, string[] expected)
    {
        using var src = OpenIndexed(Write(bytes));
        using var doc = new LargeDocument(src);
        return Compare(LogicalLines(doc), expected);
    }

    private static string? CheckRoundTrip(byte[] bytes)
    {
        using var src = OpenIndexed(Write(bytes));
        using var doc = new LargeDocument(src);
        var ms = new MemoryStream();
        var starts = new LongList();
        var joined = new LongList();
        doc.WriteTo(ms, src.DetectedLineEnding, false, starts, joined, null, CancellationToken.None);
        var written = ms.ToArray();
        if (!written.AsSpan().SequenceEqual(bytes))
            return $"wrote {written.Length} bytes [{BitConverter.ToString(written.Take(32).ToArray())}], " +
                   $"read {bytes.Length} [{BitConverter.ToString(bytes.Take(32).ToArray())}]";
        // The index handed over for the written file is the one a scan of it finds.
        for (long i = 0; i < starts.Count; i++)
            if (starts[i] != src.LineStart(i))
                return $"line {i} was written at {starts[i]}, but starts at {src.LineStart(i)}";
        return starts.Count == src.LineCount ? null : $"{starts.Count} line starts written for {src.LineCount} lines";
    }

    private static string? CheckCuts()
    {
        // Long runs of 2-, 3- and 4-byte characters, so a cut at the limit lands inside one.
        var sb = new StringBuilder();
        sb.Append("short\n");
        for (int i = 0; i < 40000; i++) sb.Append("é");
        sb.Append('\n');
        for (int i = 0; i < 30000; i++) sb.Append("€");
        sb.Append("\r\n");
        for (int i = 0; i < 30000; i++) sb.Append("😀");
        sb.Append("\nend");
        string text = sb.ToString();
        var bytes = Encoding.UTF8.GetBytes(text);
        using var src = OpenIndexed(Write(bytes));
        using var doc = new LargeDocument(src);
        var expected = text.Replace("\r\n", "\n").Split('\n');
        if (Compare(LogicalLines(doc), expected) is string bad)
            return bad;
        if (doc.JoinedCount == 0)
            return "no line was cut";
        for (long i = 0; i < doc.LineCount; i++)
            if (doc.GetLine(i).Contains('\uFFFD'))
                return $"line {i} was cut inside a character";
        if (doc.LogicalLineCount != expected.Length)
            return $"{doc.LogicalLineCount} logical lines, expected {expected.Length}";
        // Logical line numbers map back and forth.
        for (long l = 0; l < doc.LogicalLineCount; l++)
            if (doc.LogicalLine(doc.FromLogicalLine(l)) != l)
                return $"logical line {l} maps to document line {doc.FromLogicalLine(l)}, which is logical {doc.LogicalLine(doc.FromLogicalLine(l))}";
        // And it saves as it was.
        var ms = new MemoryStream();
        doc.WriteTo(ms, LineEndingStyle.Lf, false, new LongList(), new LongList(), null, CancellationToken.None);
        return ms.ToArray().AsSpan().SequenceEqual(bytes) ? null : "saving it changed the bytes";
    }

    // ===================== Edits =====================

    /// <summary>The offset in <see cref="AllText"/> of a position.</summary>
    private static int Offset(LargeDocument doc, TextPos p)
    {
        int n = 0;
        for (long i = 0; i < p.Line; i++)
            n += doc.LineLength(i) + (doc.IsJoined(i) ? 0 : 2);
        return n + p.Col;
    }

    private static string? CheckRandomEdits(int seed)
    {
        var rng = new Random(seed);
        var sb = new StringBuilder();
        string nl = seed == 2 ? "\r" : "\r\n";
        for (int i = 0; i < 300; i++)
        {
            sb.Append($"line {i} ").Append(new string((char)('a' + i % 26), rng.Next(0, 30)));
            if (seed == 2 && i % 50 == 7)
                sb.Append(new string('x', SourceFile.MaxLineBytes + 100));   // will be cut
            sb.Append(nl);
        }
        string original = sb.ToString();
        using var src = OpenIndexed(Write(Encoding.UTF8.GetBytes(original)));
        using var doc = new LargeDocument(src);
        string reference = original.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        if (AllText(doc) != reference)
            return "the text read isn't the file's";

        var states = new List<string> { reference };
        string[] inserts = { "", "x", "hello", "\r\n", "two\r\nlines", "a\nb\nc", "\r\n\r\n", "tail\r" };
        for (int step = 0; step < 400; step++)
        {
            long l1 = rng.Next((int)doc.LineCount);
            long l2 = Math.Min(doc.LineCount - 1, l1 + (rng.Next(4) == 0 ? rng.Next(5) : 0));
            var a = new TextPos(l1, rng.Next(doc.LineLength(l1) + 1));
            var b = new TextPos(l2, rng.Next(doc.LineLength(l2) + 1));
            if (b < a) (a, b) = (b, a);
            string ins = inserts[rng.Next(inserts.Length)];
            int oa = Offset(doc, a), ob = Offset(doc, b);
            var end = doc.Replace(a, b, ins, (a, b), typing: rng.Next(2) == 0, joinTyping: rng.Next(2) == 0);
            string normalized = ins.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            reference = reference[..oa] + normalized + reference[ob..];
            string actual = AllText(doc);
            if (actual != reference)
                return $"after edit {step} ({a}–{b} ← \"{Escape(ins)}\") the text differs at {FirstDifference(actual, reference)}";
            if (Offset(doc, end) != oa + normalized.Length)
                return $"edit {step} left the caret at {Offset(doc, end)}, expected {oa + normalized.Length}";
            if (rng.Next(3) == 0)
                doc.CloseTypingStep();
            states.Add(reference);
        }

        // Undo all the way back, checking each step lands on a state seen before, in order.
        int idx = states.Count - 1;
        int undone = 0;
        while (doc.Undo() is not null)
        {
            undone++;
            string t = AllText(doc);
            int found = states.LastIndexOf(t, idx - 1);
            if (found < 0)
                return $"undo {undone} reached a text that was never there";
            idx = found;
        }
        if (AllText(doc) != states[0])
            return "undoing everything didn't restore the file";
        while (doc.Redo() is not null) { }
        return AllText(doc) == states[^1] ? null : "redoing everything didn't restore the last text";
    }

    private static string FirstDifference(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return $"{i}: \"{Escape(a[i..Math.Min(a.Length, i + 20)])}\" vs \"{Escape(b[i..Math.Min(b.Length, i + 20)])}\"";
    }

    private static string? CheckSaveEdited()
    {
        // LF file with one CRLF line, no final break.
        string original = "alpha\nbeta\r\ngamma\ndelta";
        string path = Write(Encoding.UTF8.GetBytes(original));
        var src = OpenIndexed(path, lockFile: true);
        var doc = new LargeDocument(src);
        try
        {
            doc.Replace(new TextPos(2, 0), new TextPos(2, 5), "GAMMA\r\nnew", default);   // typed lines get LF
            doc.Replace(new TextPos(4, 5), new TextPos(4, 5), "\r\nlast", default);       // past the old end
            string? bad = SaveTo(doc, path, LineEndingStyle.Lf);
            if (bad is not null) return bad;
            string expected = "alpha\nbeta\r\nGAMMA\nnew\ndelta\nlast";
            string actual = File.ReadAllText(path);
            if (actual != expected)
                return $"saved \"{Escape(actual)}\", expected \"{Escape(expected)}\"";
            // The document now reads from the saved file, and its text is unchanged.
            if (AllText(doc) != expected.Replace("\r\n", "\n").Replace("\n", "\r\n"))
                return "the document's text changed in saving";
            if (doc.IsModified)
                return "the document is still modified after saving";
            // Deleting the last line drops the break before it.
            doc.Replace(new TextPos(4, 5), new TextPos(5, 4), "", default);
            bad = SaveTo(doc, path, LineEndingStyle.Lf);
            if (bad is not null) return bad;
            actual = File.ReadAllText(path);
            return actual == "alpha\nbeta\r\nGAMMA\nnew\ndelta" ? null : $"then saved \"{Escape(actual)}\"";
        }
        finally { doc.Dispose(); }
    }

    /// <summary>Save the way <see cref="LargeFileView"/> does: a temporary file replacing the old.</summary>
    private static string? SaveTo(LargeDocument doc, string path, LineEndingStyle ending, bool convert = false)
    {
        string temp = path + ".tmp";
        var starts = new LongList();
        var joined = new LongList();
        long length;
        using (var fs = new FileStream(temp, FileMode.Create))
            length = doc.WriteTo(fs, ending, convert, starts, joined, null, CancellationToken.None);
        try { File.Replace(temp, path, null, true); }
        catch (Exception ex) { return "replacing the file failed while the old one is open: " + ex.Message; }
        var saved = SourceFile.OpenIndexed(path, true, doc.Source.Codec, starts, joined, length, ending);
        doc.AdoptSaved(saved);
        // A scan of the file finds the index it was handed.
        using var check = OpenIndexed(path);
        if (check.LineCount != saved.LineCount)
            return $"the saved file has {check.LineCount} lines, the index handed over {saved.LineCount}";
        for (long i = 0; i < check.LineCount; i++)
            if (check.LineStart(i) != saved.LineStart(i) || check.IsJoined(i) != saved.IsJoined(i))
                return $"line {i} of the saved file isn't where the index handed over says";
        return null;
    }

    private static string? CheckUndoPastSave()
    {
        string path = Write(Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree\r\n"));
        var src = OpenIndexed(path, lockFile: true);
        using var doc = new LargeDocument(src);
        doc.Replace(new TextPos(1, 0), new TextPos(1, 3), "TWO", default);
        if (SaveTo(doc, path, LineEndingStyle.Crlf) is string bad)
            return bad;
        doc.Replace(new TextPos(0, 0), new TextPos(0, 3), "ONE", default);
        if (AllText(doc) != "ONE\r\nTWO\r\nthree\r\n")
            return "the edit after saving went wrong";
        doc.Undo();
        if (doc.IsModified)
            return "undoing to the saved version isn't the saved version";
        doc.Undo();
        if (AllText(doc) != "one\r\ntwo\r\nthree\r\n")
            return $"undoing past the save gave \"{Escape(AllText(doc))}\"";
        if (!doc.IsModified)
            return "the version before the save counts as saved";
        doc.Redo();
        doc.Redo();
        return AllText(doc) == "ONE\r\nTWO\r\nthree\r\n" ? null : "redo after undoing past the save went wrong";
    }

    private static string? CheckTypingSteps()
    {
        using var src = OpenIndexed(Write(Encoding.UTF8.GetBytes("abc\r\ndef\r\nghi")));
        using var doc = new LargeDocument(src);
        var p = new TextPos(0, 3);
        foreach (char c in "xyz")
            p = doc.Replace(p, p, c.ToString(), (p, p), typing: true, joinTyping: true);
        p = doc.Replace(new TextPos(0, 6), new TextPos(0, 6), "\r\n", (p, p), typing: true, joinTyping: true);
        p = doc.Replace(p, p, "q", (p, p), typing: true, joinTyping: true);
        // Elsewhere: a step of its own, though within the pause.
        doc.Replace(new TextPos(3, 0), new TextPos(3, 0), "far", (p, p), typing: true, joinTyping: true);
        doc.Undo();
        if (AllText(doc) != "abcxyz\r\nq\r\ndef\r\nghi")
            return $"the first undo gave \"{Escape(AllText(doc))}\"";
        doc.Undo();
        return AllText(doc) == "abc\r\ndef\r\nghi" ? null : $"the second undo gave \"{Escape(AllText(doc))}\"";
    }

    // ===================== Search and replace =====================

    private static (LargeDocument, SourceFile, string) SearchSample()
    {
        var rng = new Random(7);
        var sb = new StringBuilder();
        string[] words = { "error", "Error", "warning", "info", "errors", "ok", "x" };
        for (int i = 0; i < 20000; i++)
        {
            for (int w = 0; w < rng.Next(1, 8); w++)
                sb.Append(words[rng.Next(words.Length)]).Append(' ');
            sb.Append(i).Append('\n');
        }
        string text = sb.ToString();
        var src = OpenIndexed(Write(Encoding.UTF8.GetBytes(text)));
        var doc = new LargeDocument(src);
        // One edited line, so a search crosses an edited piece too.
        doc.Replace(new TextPos(5, 0), new TextPos(5, 0), "error in an edit ", default);
        return (doc, src, AllText(doc).Replace("\r\n", "\n"));
    }

    private static TextPos PosOf(string text, int index)
    {
        int line = 0, start = 0;
        for (int i = 0; i < index; i++)
            if (text[i] == '\n') { line++; start = i + 1; }
        return new TextPos(line, index - start);
    }

    private static string? CheckSearch()
    {
        var (doc, src, text) = SearchSample();
        using (doc)
        {
            var view = new PieceView(doc.Snapshot());
            foreach (var (term, opt) in new[]
                     {
                         ("error", new MatchOptions()), ("Error", new MatchOptions(CaseSensitive: true)),
                         ("error", new MatchOptions(WholeWord: true)), (@"^ok \w+", new MatchOptions(Regex: true)),
                         (@"\d+$", new MatchOptions(Regex: true)), ("rror", new MatchOptions(WholeWord: true)),
                     })
            {
                var found = new List<LargeMatch>();
                LargeSearch.FindAll(view, term, opt, int.MaxValue, (b, _) => found.AddRange(b), CancellationToken.None);
                var expected = SearchEngine.FindAll(text, term, opt);
                if (found.Count != expected.Count)
                    return $"\"{term}\": {found.Count} matches, expected {expected.Count}";
                // Positions: spot-check, since mapping every one through the text is slow.
                for (int i = 0; i < expected.Count; i += Math.Max(1, expected.Count / 50))
                {
                    var want = PosOf(text, expected[i].Start);
                    if (found[i].Start != want)
                        return $"\"{term}\" match {i} at {found[i].Start}, expected {want}";
                }
            }
        }
        return null;
    }

    private static string? CheckFindNext()
    {
        var (doc, _, text) = SearchSample();
        using (doc)
        {
            var view = new PieceView(doc.Snapshot());
            var all = SearchEngine.FindAll(text, "warning", new MatchOptions(WholeWord: true));
            var positions = all.Select(m => PosOf(text, m.Start)).ToList();
            var from = new TextPos(10000, 0);
            var next = LargeSearch.FindNext(view, "warning", new MatchOptions(WholeWord: true), from, false, null, CancellationToken.None);
            var want = positions.First(p => p >= from);
            if (next?.Start != want)
                return $"forward from {from}: {next?.Start}, expected {want}";
            var prev = LargeSearch.FindNext(view, "warning", new MatchOptions(WholeWord: true), from, true, null, CancellationToken.None);
            want = positions.Last(p => p < from);
            if (prev?.Start != want)
                return $"back from {from}: {prev?.Start}, expected {want}";
            var wrap = LargeSearch.FindNext(view, "warning", new MatchOptions(WholeWord: true), new TextPos(doc.LineCount - 1, 0), false, null, CancellationToken.None);
            if (wrap?.Start != positions[0])
                return $"forward from the end didn't wrap to the first match: {wrap?.Start}";
            var none = LargeSearch.FindNext(view, "no such thing", new MatchOptions(), from, false, null, CancellationToken.None);
            return none is null ? null : "found a term that isn't there";
        }
    }

    private static string? CheckReplaceAll()
    {
        foreach (var (find, repl, opt) in new[]
                 {
                     ("error", "E", new MatchOptions(WholeWord: true)),
                     (@"(\w+) (\d+)$", "$2:$1", new MatchOptions(Regex: true)),
                     (@"(\d)\nok", "$1 OK", new MatchOptions(Regex: true)),   // joins lines
                     ("x", "a\nb", new MatchOptions(CaseSensitive: true, WholeWord: true)),   // splits lines
                 })
        {
            var (doc, _, text) = SearchSample();
            using (doc)
            {
                var view = new PieceView(doc.Snapshot());
                var (start, count, pieces, replaced) = LargeSearch.ReplaceAll(view, find, repl, opt, null, CancellationToken.None);
                var matches = ReplaceEngine.Find(text, find, repl, opt, 0, text.Length);
                if (replaced != matches.Count)
                    return $"\"{find}\": replaced {replaced}, expected {matches.Count}";
                if (pieces is null)
                    return $"\"{find}\" matched nothing, so the case tests nothing";
                doc.ReplaceLines(start, count, pieces, default, default);
                string expected = ReplaceEngine.Apply(text, matches).Replace("\n", "\r\n");
                string actual = AllText(doc);
                if (actual != expected)
                    return $"\"{find}\" → \"{repl}\": differs at {FirstDifference(actual, expected)}";
                doc.Undo();
                if (AllText(doc).Replace("\r\n", "\n") != text)
                    return $"\"{find}\": undoing Replace All didn't restore the text";
            }
        }
        return null;
    }

    // ===================== The file changing =====================

    private static string? CheckAppend()
    {
        string path = Write(Encoding.UTF8.GetBytes("first\nsecond"));
        using var src = OpenIndexed(path);
        using var doc = new LargeDocument(src);
        src.RememberTail();
        File.AppendAllText(path, " continued\nthird\n");
        if (!src.TryExtend())
            return "an append was taken for a rewrite";
        src.WaitForIndex();
        doc.SyncWithSource();
        if (Compare(LogicalLines(doc), new[] { "first", "second continued", "third", "" }) is string bad)
            return bad;
        // Text added to the last line, with no break after it: the line count stays, the line grows.
        doc.GetLine(3);   // cached as empty
        src.RememberTail();
        File.AppendAllText(path, "partial");
        if (!src.TryExtend())
            return "a second append was taken for a rewrite";
        src.WaitForIndex();
        doc.SyncWithSource();
        if (Compare(LogicalLines(doc), new[] { "first", "second continued", "third", "partial" }) is string bad2)
            return "after text was added to the last line: " + bad2;
        src.RememberTail();
        File.WriteAllText(path, "rewritten entirely, and longer than before\n");
        return src.TryExtend() ? "a rewrite was taken for an append" : null;
    }

    private static string? CheckLockAndCopy()
    {
        string path = Write(Encoding.UTF8.GetBytes("a\nb\nc\n"));
        using var src = OpenIndexed(path);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            if (src.TryLock())
                return "locked a file another program has open for writing";
            src.SwitchToCopy(Path.Combine(Dir, "copy.src"), null, CancellationToken.None);
            writer.SetLength(0);   // the other program empties the file
        }
        if (src.LineText(1) != "b")
            return "the copy doesn't read as the file did";
        if (!src.TryLock())
            return "a source reading from its copy reports it can't be locked";
        src.Dispose();
        return File.Exists(Path.Combine(Dir, "copy.src")) ? "the copy was left behind" : null;
    }

    // ===================== The view =====================

    /// <summary>The view, in a real window: opening a big file, typing, saving.</summary>
    public static async Task<int> CheckView(string filter)
    {
        const string Label = "[large] The view opens a large file at once, edits it and saves it";
        if (!Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return 0;
        string dir = Path.Combine(Path.GetTempPath(), "nr-largeview-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "big.log");
        string? failure = null;
        Window? host = null;
        LargeFileView? view = null;
        try
        {
            // About 60 MB: big enough that the full editor would take its time.
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20))
                for (int i = 0; i < 700_000; i++)
                    w.Write(Line(i));
            long size = new FileInfo(path).Length;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            view = new LargeFileView();
            view.LoadFile(path);
            host = new Window { Content = view, Width = 900, Height = 600, Left = -3000, Top = -3000, ShowActivated = false };
            host.Show();
            await Idle();
            double firstScreen = sw.Elapsed.TotalMilliseconds;
            var area = (LargeTextArea)view.FindName("Area");
            if (area.Document is null || area.Document.GetLine(0).Length == 0)
                failure = "the first line didn't show";
            // Wait for the index.
            while (!area.Document!.Source.Complete && sw.Elapsed.TotalSeconds < 60)
                await Task.Delay(50);
            await Task.Delay(400);
            double indexed = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"     {size / 1e6:F0} MB: first screen after {firstScreen:F0} ms, indexed after {indexed:F0} ms, " +
                              $"{GC.GetTotalMemory(true) / 1e6:F0} MB managed memory");
            if (failure is null && area.Document.LogicalLineCount != 700_001)
                failure = $"{area.Document.LogicalLineCount} lines, expected 700,001";
            if (failure is null && firstScreen > 3000)
                failure = $"the first screen took {firstScreen:F0} ms";

            if (failure is null)
            {
                area.Select(new TextPos(350_000, 0), new TextPos(350_000, 0));
                area.InsertText("EDITED ", typing: true);
                area.Select(new TextPos(699_999, 0), new TextPos(700_000, 0));
                area.InsertText("", typing: false);
                if (!view.IsDirty)
                    failure = "editing didn't make the document dirty";
            }
            if (failure is null)
            {
                sw.Restart();
                if (!view.Save(saveAs: false, interactive: false))
                    failure = "saving failed";
                Console.WriteLine($"     saved in {sw.Elapsed.TotalMilliseconds:F0} ms");
            }
            if (failure is null)
            {
                // File.ReadLines doesn't count the empty line after the final break.
                var lines = File.ReadLines(path).ToList();
                string last = Line(699_998).TrimEnd('\n');
                if (lines.Count != 699_999 || !lines[350_000].StartsWith("EDITED 2026") || lines[^1] != last)
                    failure = $"the saved file isn't right: {lines.Count} lines, line 350000 \"{lines[350_000]}\", last \"{lines[^1]}\"";
                else if (view.IsDirty)
                    failure = "the document is still dirty after saving";
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            try { view?.Dispose(); } catch { }
            try { host?.Close(); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
        Console.WriteLine(failure is null ? $"ok   {Label}" : $"FAIL {Label}: {failure}");
        return failure is null ? 0 : 1;
    }

    /// <summary>Unsaved edits survive a crash: the snapshot refers to the file's lines rather than
    /// copying them, and a document is rebuilt from it; one whose file changed since is refused.</summary>
    public static async Task<int> CheckRecovery(string filter)
    {
        const string Label = "[large] Unsaved edits are recovered from a snapshot, unless the file changed since";
        if (!Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return 0;
        string dir = Path.Combine(Path.GetTempPath(), "nr-largerec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "a.log");
        string? failure = null;
        var views = new List<LargeFileView>();
        try
        {
            LargeFileView.ClearAllRecoveries();
            File.WriteAllText(path, string.Concat(Enumerable.Range(0, 5000).Select(i => $"line {i}\n")));
            var view = new LargeFileView();
            views.Add(view);
            view.LoadFile(path);
            var area = (LargeTextArea)view.FindName("Area");
            area.Document!.Source.WaitForIndex();
            await Task.Delay(400);   // the view notices the index is complete
            area.Select(new TextPos(2500, 0), new TextPos(2500, 4));
            area.InsertText("LINE", typing: true);
            area.Select(new TextPos(10, 0), new TextPos(4000, 0));
            area.InsertText("", typing: false);
            string expected = AllText(area.Document);
            view.FlushRecovery();
            view.Release();   // as a crash leaves it: the snapshot stays

            var snaps = LargeFileView.ScanRecoveries();
            if (snaps.Count != 1)
                failure = $"{snaps.Count} snapshots found, expected 1";
            else if (new FileInfo(Path.Combine(AppPaths.DataDir, "recovery", "large", snaps[0].Id + ".json")).Length > 4096)
                failure = "the snapshot copied the file's lines instead of referring to them";
            else
            {
                var back = new LargeFileView();
                views.Add(back);
                back.LoadRecovered(snaps[0]);
                var backArea = (LargeTextArea)back.FindName("Area");
                if (AllText(backArea.Document!) != expected)
                    failure = "the recovered text isn't the text that was unsaved";
                else if (!back.IsDirty)
                    failure = "the recovered document isn't marked unsaved";
                back.Release();

                // The file changes before the next launch: the snapshot's references are void.
                File.AppendAllText(path, "more\n");
                try
                {
                    var stale = new LargeFileView();
                    views.Add(stale);
                    stale.LoadRecovered(snaps[0]);
                    failure ??= "a snapshot of a file changed since was recovered anyway";
                }
                catch (InvalidOperationException) { }
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            foreach (var v in views) try { v.Dispose(); } catch { }
            LargeFileView.ClearAllRecoveries();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
        Console.WriteLine(failure is null ? $"ok   {Label}" : $"FAIL {Label}: {failure}");
        return failure is null ? 0 : 1;
    }

    /// <summary>Time the large-file operations on a real file (--bench=path): not a pass/fail test.</summary>
    public static void Bench(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var src = SourceFile.Open(path, lockFile: false);
        src.WaitForFirstLines(500, TimeSpan.FromSeconds(5));
        Console.WriteLine($"first 500 lines: {sw.ElapsedMilliseconds} ms");
        src.WaitForIndex();
        Console.WriteLine($"indexed {src.LineCount:N0} lines, {src.IndexedLength / 1e6:F0} MB: {sw.ElapsedMilliseconds} ms; " +
                          $"managed memory {GC.GetTotalMemory(true) / 1e6:F0} MB");
        using var doc = new LargeDocument(src);
        var view = new PieceView(doc.Snapshot());
        foreach (var (term, opt) in new[] { ("ERROR", new MatchOptions(CaseSensitive: true)), ("error", new MatchOptions()),
                                            (@"id=0000\w+", new MatchOptions(Regex: true)) })
        {
            sw.Restart();
            long n = 0;
            LargeSearch.FindAll(view, term, opt, 1_000_000, (b, _) => n += b.Count, CancellationToken.None);
            Console.WriteLine($"find all \"{term}\": {n:N0} matches in {sw.ElapsedMilliseconds} ms");
        }
        sw.Restart();
        var m = LargeSearch.FindNext(view, "no such text", new MatchOptions(), new TextPos(0, 0), false, null, CancellationToken.None);
        Console.WriteLine($"find next with no match (whole file): {sw.ElapsedMilliseconds} ms");
        doc.Replace(new TextPos(1_000_000, 0), new TextPos(1_000_000, 0), "EDIT ", default);
        string temp = path + ".benchsave";
        sw.Restart();
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            doc.WriteTo(fs, LineEndingStyle.Crlf, false, new LongList(), new LongList(), null, CancellationToken.None);
        Console.WriteLine($"save with one edit: {sw.ElapsedMilliseconds} ms");
        File.Delete(temp);
        sw.Restart();
        var (start, count, pieces, replaced) = LargeSearch.ReplaceAll(view, "ERROR", "FAILURE", new MatchOptions(CaseSensitive: true), null, CancellationToken.None);
        Console.WriteLine($"replace all ERROR: {replaced:N0} in {sw.ElapsedMilliseconds} ms; managed memory {GC.GetTotalMemory(true) / 1e6:F0} MB");
    }

    /// <summary>Time the full editor on a file (--benchfull=path), to choose the size from which
    /// large-file mode takes over: loading, then typing.</summary>
    public static async Task BenchFull(string path)
    {
        if (Environment.GetEnvironmentVariable("NR_BENCH_NOWRAP") == "1")
            AppSettings.Current.WordWrap = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var view = new EditorView();
        view.LoadFile(path);
        var host = new Window { Content = view, Width = 900, Height = 600, Left = -3000, Top = -3000, ShowActivated = false };
        host.Show();
        await Idle();
        Console.WriteLine($"full editor, {new FileInfo(path).Length / 1e6:F1} MB: loaded and shown in {sw.ElapsedMilliseconds} ms; " +
                          $"managed memory {GC.GetTotalMemory(true) / 1e6:F0} MB");
        view.Editor.CaretIndex = view.Editor.Text.Length / 2;
        sw.Restart();
        for (int i = 0; i < 20; i++)
        {
            view.Editor.SelectedText = "x";
            view.Editor.CaretIndex += 1;
            await Idle();
        }
        Console.WriteLine($"  typing: {sw.ElapsedMilliseconds / 20.0:F0} ms per keystroke");
        sw.Restart();
        await Task.Delay(1500);   // the undo commit runs on its timer
        await Idle();
        Console.WriteLine($"  commit and idle: {sw.ElapsedMilliseconds - 1500} ms beyond the wait");
        view.Dispose();
        host.Close();
    }

    private static string Line(int i) =>
        $"2026-11-15 12:00:{i % 60:D2}.{i % 1000:D3} [worker-{i % 16}] INFO request {i} handled in {i % 997} ms\n";

    private static async Task Idle()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
