using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace NotepadRedo;

/// <summary>
/// Side-by-side merge viewer (styled after orchestrator2's edit-tool diff). The two panes hold
/// "mine" (the editor's text) and "on disk"; changed/added/removed lines are tinted and the
/// differing words are painted red. One side is the <b>kept</b> side — it's outlined and editable,
/// and the user assembles the final text there. Red lines on the source side can be copied across
/// by double-clicking (or via the right-click menu); the kept side's own menu can replace/insert/
/// remove a line. The user can flip which side is kept at any time.
///
/// If the file changes on disk <i>again</i> while merging, <see cref="NotifyDiskChanged"/> surfaces
/// a banner + prompt letting the user fold the new version in, ignore it, or bail out.
/// </summary>
public partial class DiffMergeWindow : Window
{
    private readonly string _filePath;
    private readonly string _fileName;

    private string _leftWork;    // "mine" (editor) working text
    private string _rightWork;   // "on disk" working text
    private bool _keepLeft = true;

    private string? _pendingDisk;   // a newer disk version awaiting the user's decision
    private bool _suppressRadio;
    private bool _rendered;

    /// <summary>True when the user pressed "Save kept side" (result is in <see cref="ResultText"/>).</summary>
    public bool Saved { get; private set; }

    /// <summary>The assembled kept text, valid when <see cref="Saved"/> is true.</summary>
    public string? ResultText { get; private set; }

    /// <summary>True when the user chose "save both &amp; reload" — the host should reload from disk.</summary>
    public bool ExitAndReload { get; private set; }

    private readonly List<Row> _rows = new();

    // One rendered diff line: the source op plus where it maps on the kept side.
    private sealed record Row(DiffOp Op, int KeptLineIndex, int KeptInsertPos, bool KeptHasLine);

    // ----- palette -----
    private static readonly Brush Red        = Frozen(Color.FromRgb(0xD1, 0x34, 0x38));
    private static readonly Brush ChangeBg   = Frozen(Color.FromArgb(0x22, 0x2E, 0xA0, 0x8A));
    private static readonly Brush InsertBg   = Frozen(Color.FromArgb(0x22, 0x3F, 0xB9, 0x50));
    private static readonly Brush DeleteBg   = Frozen(Color.FromArgb(0x22, 0xD1, 0x34, 0x38));
    private static readonly Brush GapBg      = Frozen(Color.FromArgb(0x14, 0x80, 0x80, 0x80));
    private static readonly Brush KeptOutline = Frozen(Color.FromRgb(0x3B, 0x82, 0xF6));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public DiffMergeWindow(Window? owner, string filePath, string mineText, string diskText)
    {
        InitializeComponent();
        Owner = owner;
        _filePath = filePath;
        _fileName = string.IsNullOrEmpty(filePath) ? "Untitled" : Path.GetFileName(filePath);
        _leftWork = mineText ?? "";
        _rightWork = diskText ?? "";

        LeftHeader.Text = "Mine (editor)";
        RightHeader.Text = $"On disk — {_fileName}";
        Title = $"Resolve differences — {_fileName}";

        // Lock the two panes' scrolling together so matching rows stay side-by-side. The panes are
        // built row-for-row aligned (gaps fill the missing side), so mirroring one's scroll offset
        // onto the other keeps corresponding lines level.
        LeftBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(LeftBox_ScrollChanged));
        RightBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(RightBox_ScrollChanged));

        Render();
    }

    private bool _syncingScroll;

    private void LeftBox_ScrollChanged(object sender, ScrollChangedEventArgs e) => MirrorScroll(from: LeftBox, to: RightBox, e);
    private void RightBox_ScrollChanged(object sender, ScrollChangedEventArgs e) => MirrorScroll(from: RightBox, to: LeftBox, e);

    private void MirrorScroll(RichTextBox from, RichTextBox to, ScrollChangedEventArgs e)
    {
        if (_syncingScroll) return;
        if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;
        _syncingScroll = true;
        try
        {
            if (e.VerticalChange != 0) to.ScrollToVerticalOffset(from.VerticalOffset);
            if (e.HorizontalChange != 0) to.ScrollToHorizontalOffset(from.HorizontalOffset);
        }
        finally { _syncingScroll = false; }
    }

    private string KeptWork
    {
        get => _keepLeft ? _leftWork : _rightWork;
        set { if (_keepLeft) _leftWork = value; else _rightWork = value; }
    }

    // ===================== Rendering =====================

    private void Render()
    {
        // Reassigning a RichTextBox.Document destroys the focused element's visual subtree. If focus
        // is inside a pane when we swap, it drops to null mid-swap and — because this is a modal
        // dialog whose owner is disabled — Windows moves activation to another top-level window,
        // which looks like the whole app losing focus. Park focus on a persistent control (the
        // toolbar radio) *before* the swap so focus never enters limbo, then restore it after.
        bool hadFocusWithin = _rendered && IsKeyboardFocusWithin;
        if (hadFocusWithin)
            Keyboard.Focus(KeepLeftRadio);

        var leftLines = DiffEngine.SplitLines(_leftWork);
        var rightLines = DiffEngine.SplitLines(_rightWork);
        var ops = DiffEngine.DiffLines(leftLines, rightLines);

        var leftDoc = new FlowDocument { PagePadding = new Thickness(4) };
        var rightDoc = new FlowDocument { PagePadding = new Thickness(4) };
        _rows.Clear();

        int keptCount = 0;   // running index into the kept side's real lines
        foreach (var op in ops)
        {
            bool keptHasLine = _keepLeft ? op.LeftIndex >= 0 : op.RightIndex >= 0;
            var row = new Row(op, keptHasLine ? keptCount : -1, keptCount, keptHasLine);
            _rows.Add(row);
            if (keptHasLine) keptCount++;

            var lp = BuildParagraph(op, leftSide: true);
            var rp = BuildParagraph(op, leftSide: false);

            if (op.Kind != DiffOpKind.Equal)
            {
                if (_keepLeft) { AttachKept(lp, row); AttachSource(rp, row); }
                else           { AttachSource(lp, row); AttachKept(rp, row); }
            }

            leftDoc.Blocks.Add(lp);
            rightDoc.Blocks.Add(rp);
        }

        LeftBox.Document = leftDoc;
        RightBox.Document = rightDoc;

        LeftBox.IsReadOnly = !_keepLeft;
        RightBox.IsReadOnly = _keepLeft;
        LeftOutline.BorderBrush = _keepLeft ? KeptOutline : Brushes.Transparent;
        RightOutline.BorderBrush = _keepLeft ? Brushes.Transparent : KeptOutline;

        _suppressRadio = true;
        KeepLeftRadio.IsChecked = _keepLeft;
        KeepRightRadio.IsChecked = !_keepLeft;
        _suppressRadio = false;

        _rendered = true;

        // Focus was parked on the radio above; move it back into the (editable) kept pane once the
        // new content hosts are realized. Deferred to Input priority so the pane's document is live.
        if (hadFocusWithin)
        {
            var keptBox = _keepLeft ? LeftBox : RightBox;
            Dispatcher.BeginInvoke(new Action(() => keptBox.Focus()),
                                   System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private static Paragraph BuildParagraph(DiffOp op, bool leftSide)
    {
        string? line = leftSide ? op.Left : op.Right;
        var p = new Paragraph { Margin = new Thickness(0) };

        if (line is null)
        {
            // No line on this side — a spacer that keeps the two panes row-aligned.
            p.Tag = "gap";
            p.Background = GapBg;
            p.Inlines.Add(new Run(" "));
            return p;
        }

        switch (op.Kind)
        {
            case DiffOpKind.Equal:
                p.Inlines.Add(new Run(line));
                break;

            case DiffOpKind.Change:
                var (l, r) = DiffEngine.InlineDiff(op.Left ?? "", op.Right ?? "");
                foreach (var span in leftSide ? l : r)
                {
                    // Only the differing spans get an explicit (red) brush; leave the rest unset so
                    // they inherit the editor foreground. Setting Foreground = null would render the
                    // text invisibly (WPF draws no glyphs for a null brush rather than inheriting).
                    var run = new Run(span.Text);
                    if (span.Differs) run.Foreground = Red;
                    p.Inlines.Add(run);
                }
                p.Background = ChangeBg;
                break;

            case DiffOpKind.Delete:   // line exists only on the left
                p.Inlines.Add(new Run(line) { Foreground = Red });
                p.Background = DeleteBg;
                break;

            case DiffOpKind.Insert:   // line exists only on the right
                p.Inlines.Add(new Run(line) { Foreground = Red });
                p.Background = InsertBg;
                break;
        }
        return p;
    }

    private void AttachSource(Paragraph p, Row row)
    {
        p.Cursor = Cursors.Hand;
        p.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { AcceptRow(row); e.Handled = true; }
        };
        var menu = new ContextMenu();
        var mi = new MenuItem { Header = "Copy this line to the kept side" };
        mi.Click += (_, _) => AcceptRow(row);
        menu.Items.Add(mi);
        p.ContextMenu = menu;
    }

    private void AttachKept(Paragraph p, Row row)
    {
        string? srcLine = _keepLeft ? row.Op.Right : row.Op.Left;
        string header = srcLine is null ? "Remove this line"
                       : row.KeptHasLine ? "Replace with the source version"
                       : "Insert the source line here";
        var menu = new ContextMenu();
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => AcceptRow(row);
        menu.Items.Add(mi);
        p.ContextMenu = menu;
    }

    // ===================== Merge operations =====================

    /// <summary>Make the kept side match the source side for one diff row, then re-diff.</summary>
    private void AcceptRow(Row row)
    {
        SyncKeptFromUi();
        var keptLines = new List<string>(DiffEngine.SplitLines(KeptWork));

        string? srcLine = _keepLeft ? row.Op.Right : row.Op.Left;

        if (srcLine is not null && row.KeptHasLine)
        {
            if (row.KeptLineIndex >= 0 && row.KeptLineIndex < keptLines.Count)
                keptLines[row.KeptLineIndex] = srcLine;               // Change → replace
        }
        else if (srcLine is not null && !row.KeptHasLine)
        {
            int pos = Math.Clamp(row.KeptInsertPos, 0, keptLines.Count);
            keptLines.Insert(pos, srcLine);                           // source has an extra line → add it
        }
        else if (srcLine is null && row.KeptHasLine)
        {
            if (row.KeptLineIndex >= 0 && row.KeptLineIndex < keptLines.Count)
                keptLines.RemoveAt(row.KeptLineIndex);                // kept has an extra line → drop it
        }

        KeptWork = string.Join("\r\n", keptLines);
        Render();
    }

    /// <summary>Pull any hand-typed edits out of the editable (kept) pane back into the model.</summary>
    private void SyncKeptFromUi()
    {
        if (!_rendered)
            return;
        var box = _keepLeft ? LeftBox : RightBox;
        if (box.Document is not null)
            KeptWork = ReadBack(box);
    }

    private static string ReadBack(RichTextBox box)
    {
        var lines = new List<string>();
        foreach (var block in box.Document.Blocks)
        {
            if (block is not Paragraph p)
                continue;
            string t = new TextRange(p.ContentStart, p.ContentEnd).Text.Replace("\r", "").Replace("\n", "");
            if ((p.Tag as string) == "gap" && t.Trim().Length == 0)
                continue;   // an alignment spacer the user never typed into
            lines.Add(t);
        }
        return string.Join("\r\n", lines);
    }

    // ===================== Toolbar handlers =====================

    private void KeepSide_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressRadio)
            return;
        SyncKeptFromUi();   // preserve edits on the side we're leaving
        _keepLeft = KeepLeftRadio.IsChecked == true;
        Render();
    }

    private void Rediff_Click(object sender, RoutedEventArgs e)
    {
        SyncKeptFromUi();
        Render();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SyncKeptFromUi();
        ResultText = KeptWork;
        Saved = true;
        Close();
    }

    /// <summary>
    /// Save both versions: the kept side becomes the file (returned as <see cref="ResultText"/> for
    /// the host to write to the real filename), and the other — non-kept — side is written to a
    /// timestamped sibling file next to it, so neither version is lost.
    /// </summary>
    private void SaveBoth_Click(object sender, RoutedEventArgs e)
    {
        SyncKeptFromUi();

        // The non-kept side is read-only, so its model text is authoritative. Name the sibling after
        // which side it is: "disk" when we're keeping mine, "mine" when we're keeping the disk copy.
        string otherText   = _keepLeft ? _rightWork : _leftWork;
        string otherSuffix = _keepLeft ? "disk" : "mine";

        string? otherPath = SaveToSibling(otherSuffix, otherText);
        if (otherPath is null)
            return;   // a write failed and was already reported; stay open so nothing is lost

        ThemedDialog.Show(this,
            $"The other version was saved to:\n{otherPath}\n\n" +
            $"The kept version will be written to “{_fileName}”.",
            "Saved both versions", MessageBoxButton.OK, MessageBoxImage.Information);

        ResultText = KeptWork;
        Saved = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Saved = false;
        Close();
    }

    // ===================== Mid-merge re-change =====================

    /// <summary>
    /// Called by the host when the file changes on disk <i>again</i> while this viewer is open.
    /// Shows a banner and prompts the user for how to fold in (or discard) the new version.
    /// </summary>
    public void NotifyDiskChanged(string newDiskText)
    {
        _pendingDisk = newDiskText;
        BannerText.Text = $"“{_fileName}” changed on disk again.";
        Banner.Visibility = Visibility.Visible;
        ReviewPending();
    }

    private void Banner_Click(object sender, RoutedEventArgs e) => ReviewPending();

    private void ReviewPending()
    {
        if (_pendingDisk is null)
        {
            Banner.Visibility = Visibility.Collapsed;
            return;
        }

        int choice = ThemedDialog.ShowChoices(this,
            $"“{_fileName}” changed on disk again while you're merging. What would you like to do?",
            "File changed again",
            new[]
            {
                "Reload the new version into the diff viewer",
                "Ignore it (my merge will overwrite it when saved)",
                "Save both versions to new files, reload the file, and close the merge",
                "Save the new version to another file, then ignore it",
            },
            MessageBoxImage.Warning, defaultIndex: 0);

        switch (choice)
        {
            case 0:   // fold the new disk version into the "on disk" pane
                if (_keepLeft) SyncKeptFromUi();   // keep left-side hand edits; right is being replaced
                _rightWork = _pendingDisk;
                _pendingDisk = null;
                Banner.Visibility = Visibility.Collapsed;
                Render();
                break;

            case 1:   // ignore — merge result overwrites when saved
                _pendingDisk = null;
                Banner.Visibility = Visibility.Collapsed;
                break;

            case 2:   // save both, reload from disk, close the viewer
                SaveBothAndExit();
                break;

            case 3:   // stash the new disk version to a sibling file, then ignore
                if (SaveToSibling("disk", _pendingDisk) is not null)
                {
                    _pendingDisk = null;
                    Banner.Visibility = Visibility.Collapsed;
                }
                break;

            default:  // dismissed — leave the banner up so they can Review… later
                break;
        }
    }

    private void SaveBothAndExit()
    {
        SyncKeptFromUi();
        string? mine = SaveToSibling("merge", KeptWork);
        string? disk = SaveToSibling("disk", _pendingDisk ?? _rightWork);
        if (mine is null || disk is null)
            return;   // a write failed and was already reported; stay open

        ThemedDialog.Show(this,
            $"Saved your in-progress merge to:\n{mine}\n\nand the new disk version to:\n{disk}\n\n" +
            "The editor will reload the current file from disk.",
            "Saved both versions", MessageBoxButton.OK, MessageBoxImage.Information);

        _pendingDisk = null;
        ExitAndReload = true;
        Saved = false;
        Close();
    }

    /// <summary>Write <paramref name="text"/> next to the original file with a timestamped suffix.</summary>
    private string? SaveToSibling(string suffix, string text)
    {
        try
        {
            string dir = Path.GetDirectoryName(_filePath) ?? Environment.CurrentDirectory;
            string stem = Path.GetFileNameWithoutExtension(_filePath);
            string ext = Path.GetExtension(_filePath);
            if (string.IsNullOrEmpty(stem)) stem = "untitled";
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string path = Path.Combine(dir, $"{stem}.{suffix}-{stamp}{ext}");
            File.WriteAllText(path, text);
            return path;
        }
        catch (Exception ex)
        {
            ThemedDialog.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }
}
