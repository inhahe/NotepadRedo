using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;

namespace NotepadRedo.LargeText;

/// <summary>
/// The text area of the large-file editor: draws only the lines in view, straight from a
/// <see cref="LargeDocument"/>, and turns keys and the mouse into caret moves and edits. It scrolls
/// by whole lines (<see cref="TopLine"/>), so a file of any number of lines scrolls the same, and it
/// never wraps: a wrapped view of a file whose line lengths aren't known can't be scrolled by line.
/// The scroll bars live outside it, in <see cref="NotepadRedo.LargeFileView"/>, which mirrors
/// <see cref="ScrollChanged"/> onto them.
/// </summary>
internal sealed class LargeTextArea : FrameworkElement
{
    private const double TextPadding = 6;
    private const double GutterPadding = 8;

    private LargeDocument? _doc;
    private readonly DrawingVisual _caretVisual = new();
    private readonly DispatcherTimer _blink;
    private readonly DispatcherTimer _dragScroll;
    private readonly Dictionary<long, CachedLine> _lines = new();
    private TextFormatter _formatter = TextFormatter.Create();
    private Typeface _typeface = new("Consolas");
    private double _fontSize = 14;
    private double _lineHeight = 17;
    private double _spaceWidth = 7;
    private double _maxWidthSeen;
    private double _desiredX = -1;
    private long _topLine;
    private double _hOffset;
    private bool _selecting;
    private bool _selectingLines;
    private TextPos _lineSelectOrigin;
    private Point _lastMouse;
    /// <summary>When the last keystroke that may still join the open undo step landed; null after
    /// anything that ends that step (a caret move, a paste, undo).</summary>
    private long? _lastKeystrokeMs;

    public LargeTextArea()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        AddVisualChild(_caretVisual);
        InputMethod.SetIsInputMethodEnabled(this, true);

        _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetCaretBlinkMs()) };
        _blink.Tick += (_, _) => _caretVisual.Opacity = _caretVisual.Opacity > 0 ? 0 : 1;
        _dragScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _dragScroll.Tick += (_, _) => DragScrollTick();

        GotKeyboardFocus += (_, _) => { ShowCaret(); _blink.Start(); };
        LostKeyboardFocus += (_, _) => { _blink.Stop(); _caretVisual.Opacity = 0; };
        SizeChanged += (_, _) => { InvalidateVisual(); RaiseScrollChanged(); };

        ContextMenu = BuildContextMenu();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();

    private static int GetCaretBlinkMs()
    {
        try
        {
            uint t = GetCaretBlinkTime();
            return t is > 0 and < 5000 ? (int)t : 530;
        }
        catch { return 530; }
    }

    // ===================== Appearance =====================

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(LargeTextArea),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(LargeTextArea),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((LargeTextArea)d).ResetLayoutCache()));
    public static readonly DependencyProperty GutterBackgroundProperty = DependencyProperty.Register(
        nameof(GutterBackground), typeof(Brush), typeof(LargeTextArea),
        new FrameworkPropertyMetadata(Brushes.WhiteSmoke, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GutterForegroundProperty = DependencyProperty.Register(
        nameof(GutterForeground), typeof(Brush), typeof(LargeTextArea),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush), typeof(Brush), typeof(LargeTextArea),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x66, 0x33, 0x99, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush), typeof(Brush), typeof(LargeTextArea),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xD5, 0x4F)), FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush GutterBackground { get => (Brush)GetValue(GutterBackgroundProperty); set => SetValue(GutterBackgroundProperty, value); }
    public Brush GutterForeground { get => (Brush)GetValue(GutterForegroundProperty); set => SetValue(GutterForegroundProperty, value); }
    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public Brush HighlightBrush { get => (Brush)GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }

    /// <summary>Use the editor font. <paramref name="sizeDip"/> is in device-independent pixels.</summary>
    public void SetFont(FontFamily family, double sizeDip, FontWeight weight, FontStyle style)
    {
        _typeface = new Typeface(family, style, weight, FontStretches.Normal);
        _fontSize = sizeDip;
        _lineHeight = Math.Ceiling(family.LineSpacing * sizeDip);
        var space = new FormattedText(" ", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            _typeface, _fontSize, Brushes.Black, PixelsPerDip);
        _spaceWidth = Math.Max(1, space.WidthIncludingTrailingWhitespace);
        _maxWidthSeen = 0;
        ResetLayoutCache();
        InvalidateVisual();
        UpdateCaret();
        RaiseScrollChanged();
    }

    private double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>Marks matches of the current search in each line drawn; null draws none.</summary>
    public Func<string, IReadOnlyList<(int Start, int Length)>>? Highlighter
    {
        get => _highlighter;
        set { _highlighter = value; foreach (var l in _lines.Values) l.Highlights = null; InvalidateVisual(); }
    }
    private Func<string, IReadOnlyList<(int Start, int Length)>>? _highlighter;

    // ===================== Document and state =====================

    public LargeDocument? Document
    {
        get => _doc;
        set
        {
            _doc = value;
            _topLine = 0;
            _hOffset = 0;
            Anchor = Caret = default;
            ResetLayoutCache();
            InvalidateVisual();
            RaiseScrollChanged();
            RaiseCaretMoved();
        }
    }

    /// <summary>Edits are refused (and <see cref="EditRefused"/> raised) while set.</summary>
    public bool IsReadOnly { get; set; }

    public TextPos Anchor { get; private set; }
    public TextPos Caret { get; private set; }
    public bool HasSelection => Anchor != Caret;
    public TextPos SelectionStart => TextPos.Min(Anchor, Caret);
    public TextPos SelectionEnd => TextPos.Max(Anchor, Caret);

    public long TopLine => _topLine;
    public double HorizontalOffset => _hOffset;
    public double LineHeight => _lineHeight;
    /// <summary>Lines that fit wholly in the view.</summary>
    public int FullRows => Math.Max(1, (int)Math.Floor(ActualHeight / _lineHeight));
    private int VisibleRows => Math.Max(1, (int)Math.Ceiling(ActualHeight / _lineHeight));
    public double TextViewportWidth => Math.Max(0, ActualWidth - GutterWidth - TextPadding);
    public double ExtentWidth => _maxWidthSeen + TextPadding * 2 + _spaceWidth;

    public event EventHandler? CaretMoved;
    public event EventHandler? ScrollChanged;
    /// <summary>Raised before an edit; set Cancel to refuse it (the view locks the file here).</summary>
    public event EventHandler<System.ComponentModel.CancelEventArgs>? BeforeEdit;
    public event EventHandler? Edited;
    /// <summary>An edit was refused because the area is read-only.</summary>
    public event EventHandler? EditRefused;

    private void RaiseCaretMoved() => CaretMoved?.Invoke(this, EventArgs.Empty);
    private void RaiseScrollChanged() => ScrollChanged?.Invoke(this, EventArgs.Empty);

    private long LineCount => _doc is null ? 0 : Math.Max(1, _doc.LineCount);

    /// <summary>Put the caret at <paramref name="caret"/>, selecting from <paramref name="anchor"/>.</summary>
    public void Select(TextPos anchor, TextPos caret, bool scroll = true)
    {
        if (_doc is null)
            return;
        Anchor = _doc.ClampPos(anchor);
        Caret = _doc.ClampPos(caret);
        _desiredX = -1;
        _lastKeystrokeMs = null;   // moved elsewhere (a search, go to line): typing starts a new step
        AfterCaretMove(scroll);
    }

    private void AfterCaretMove(bool scroll)
    {
        if (scroll)
            EnsureCaretVisible();
        ShowCaret();
        UpdateCaret();
        InvalidateVisual();
        RaiseCaretMoved();
    }

    public void ScrollTo(long topLine, double? hOffset = null)
    {
        long max = Math.Max(0, LineCount - FullRows);
        topLine = Math.Clamp(topLine, 0, max);
        double h = Math.Clamp(hOffset ?? _hOffset, 0, Math.Max(0, ExtentWidth - TextViewportWidth));
        if (topLine == _topLine && h == _hOffset)
            return;
        _topLine = topLine;
        _hOffset = h;
        InvalidateVisual();
        UpdateCaret();
        RaiseScrollChanged();
    }

    /// <summary>Scroll so <paramref name="line"/> is in view, about a third of the way down when it
    /// has to move.</summary>
    public void ScrollIntoView(long line)
    {
        int rows = FullRows;
        if (line >= _topLine && line < _topLine + rows)
            return;
        ScrollTo(line - rows / 3);
    }

    public void EnsureCaretVisible()
    {
        int rows = FullRows;
        long top = _topLine;
        if (Caret.Line < top) top = Caret.Line;
        else if (Caret.Line >= top + rows) top = Caret.Line - rows + 1;
        double x = CaretX(Caret);
        double h = _hOffset;
        double width = TextViewportWidth;
        if (x < h) h = Math.Max(0, x - width / 4);
        else if (x > h + width - _spaceWidth) h = x - width * 3 / 4;
        _maxWidthSeen = Math.Max(_maxWidthSeen, x);
        ScrollTo(top, h);
    }

    /// <summary>The document changed under the area (an edit made elsewhere, a reload, more lines
    /// indexed): drop what was laid out and redraw.</summary>
    public void Refresh()
    {
        if (_doc is not null)
        {
            Anchor = _doc.ClampPos(Anchor);
            Caret = _doc.ClampPos(Caret);
        }
        InvalidateVisual();
        UpdateCaret();
        RaiseScrollChanged();
    }

    // ===================== Layout of lines =====================

    private sealed class CachedLine
    {
        public required string Text;
        public required List<(TextLine Line, int Start, double X)> Segments;
        public double Width;
        public IReadOnlyList<(int Start, int Length)>? Highlights;
    }

    private void ResetLayoutCache()
    {
        foreach (var l in _lines.Values)
            foreach (var s in l.Segments)
                s.Line.Dispose();
        _lines.Clear();
        _formatter = TextFormatter.Create(TextOptions.GetTextFormattingMode(this));
    }

    private CachedLine Layout(long line)
    {
        string text = _doc!.GetLine(line);
        if (_lines.TryGetValue(line, out var cached) && ReferenceEquals(cached.Text, text))
            return cached;
        if (cached is not null)
            foreach (var s in cached.Segments)
                s.Line.Dispose();

        var props = new RunProps(_typeface, _fontSize, Foreground);
        var para = new ParaProps(props, _spaceWidth * 4);
        var source = new LineSource(text, props);
        var segments = new List<(TextLine, int, double)>();
        int pos = 0;
        double x = 0;
        // Without wrapping a line is normally formatted in one go, but the formatter may still stop
        // short of the end of a very long one; the rest is laid out after it.
        do
        {
            var tl = _formatter.FormatLine(source, pos, 1e6, para, null);
            segments.Add((tl, pos, x));
            x += tl.WidthIncludingTrailingWhitespace;
            if (tl.Length <= 0)
                break;
            pos += tl.Length;
        } while (pos < text.Length);

        var result = new CachedLine { Text = text, Segments = segments, Width = x };
        _lines[line] = result;
        if (x > _maxWidthSeen)
        {
            _maxWidthSeen = x;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, RaiseScrollChanged);
        }
        return result;
    }

    /// <summary>Forget laid-out lines well away from the view.</summary>
    private void TrimCache()
    {
        long keepFrom = _topLine - VisibleRows, keepTo = _topLine + 2L * VisibleRows;
        if (_lines.Count <= VisibleRows * 4)
            return;
        foreach (var key in _lines.Keys.Where(k => k < keepFrom || k > keepTo).ToList())
        {
            foreach (var s in _lines[key].Segments)
                s.Line.Dispose();
            _lines.Remove(key);
        }
    }

    /// <summary>X of a column within its line's text, before scrolling and padding.</summary>
    private double ColumnX(CachedLine cl, int col)
    {
        col = Math.Clamp(col, 0, cl.Text.Length);
        for (int i = cl.Segments.Count - 1; i >= 0; i--)
        {
            var (tl, start, x) = cl.Segments[i];
            if (col >= start)
                return x + tl.GetDistanceFromCharacterHit(new CharacterHit(col, 0));
        }
        return 0;
    }

    private int ColumnAt(CachedLine cl, double x)
    {
        if (x <= 0)
            return 0;
        foreach (var (tl, start, sx) in cl.Segments)
        {
            if (x < sx + tl.WidthIncludingTrailingWhitespace || ReferenceEquals(tl, cl.Segments[^1].Line))
            {
                var hit = tl.GetCharacterHitFromDistance(x - sx);
                return Math.Clamp(hit.FirstCharacterIndex + hit.TrailingLength, 0, cl.Text.Length);
            }
        }
        return cl.Text.Length;
    }

    private double CaretX(TextPos p) => _doc is null ? 0 : ColumnX(Layout(p.Line), p.Col);

    private double TextLeft => GutterWidth + TextPadding - _hOffset;

    private double GutterWidth
    {
        get
        {
            if (_doc is null)
                return 0;
            long n = Math.Max(1, _doc.LogicalLineCount);
            // As drawn: with the culture's group separators, each allowed a digit's width.
            int chars = Math.Max(3, n.ToString("N0", CultureInfo.CurrentCulture).Length);
            return chars * DigitWidth + GutterPadding * 2;
        }
    }

    private double DigitWidth
    {
        get
        {
            if (_digitWidthFor != (_typeface, _fontSize))
            {
                _digitWidth = new FormattedText("0", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    _typeface, _fontSize * 0.9, Brushes.Black, PixelsPerDip).WidthIncludingTrailingWhitespace;
                _digitWidthFor = (_typeface, _fontSize);
            }
            return _digitWidth;
        }
    }
    private double _digitWidth;
    private (Typeface, double)? _digitWidthFor;

    /// <summary>The position under a point of the area.</summary>
    private TextPos PositionAt(Point p)
    {
        if (_doc is null)
            return default;
        long line = _topLine + (long)Math.Floor(p.Y / _lineHeight);
        line = Math.Clamp(line, 0, LineCount - 1);
        var cl = Layout(line);
        return new TextPos(line, ColumnAt(cl, p.X - TextLeft));
    }

    // ===================== Rendering =====================

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _caretVisual;

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(Background, null, new Rect(size));
        if (_doc is null)
            return;
        // Laid-out lines are reused by their text, so stale entries are harmless; dropping those far
        // from the view bounds the memory scrolling through a file would otherwise accumulate.
        TrimCache();

        double gutter = GutterWidth;
        double ppd = PixelsPerDip;
        int rows = VisibleRows;
        long count = _doc.LineCount;
        var selStart = SelectionStart;
        var selEnd = SelectionEnd;
        bool hasSel = HasSelection;

        dc.PushClip(new RectangleGeometry(new Rect(gutter, 0, Math.Max(0, size.Width - gutter), size.Height)));
        for (int r = 0; r < rows; r++)
        {
            long line = _topLine + r;
            if (line >= count)
                break;
            double y = r * _lineHeight;
            var cl = Layout(line);
            double left = TextLeft;

            if (_highlighter is not null)
            {
                cl.Highlights ??= _highlighter(cl.Text);
                foreach (var (start, length) in cl.Highlights)
                    DrawRange(dc, cl, start, start + length, left, y, HighlightBrush);
            }

            if (hasSel && line >= selStart.Line && line <= selEnd.Line)
            {
                int from = line == selStart.Line ? selStart.Col : 0;
                int to = line == selEnd.Line ? selEnd.Col : cl.Text.Length;
                DrawRange(dc, cl, from, to, left, y, SelectionBrush);
                if (line < selEnd.Line && !_doc.IsJoined(line))
                {
                    // The line break is selected too: show it as a space-wide block past the text.
                    double x = left + cl.Width;
                    dc.DrawRectangle(SelectionBrush, null, new Rect(x, y, _spaceWidth, _lineHeight));
                }
            }

            foreach (var (tl, _, x) in cl.Segments)
            {
                double sx = left + x;
                if (sx > size.Width || sx + tl.WidthIncludingTrailingWhitespace < gutter)
                    continue;
                tl.Draw(dc, new Point(sx, y + Math.Max(0, (_lineHeight - tl.Height) / 2)), InvertAxes.None);
            }
        }
        dc.Pop();

        // Gutter: logical line numbers; a joined segment's continuation shows an arrow instead.
        dc.DrawRectangle(GutterBackground, null, new Rect(0, 0, gutter, size.Height));
        for (int r = 0; r < rows; r++)
        {
            long line = _topLine + r;
            if (line >= count)
                break;
            string label = line > 0 && _doc.IsJoined(line - 1) ? "↪" : (_doc.LogicalLine(line) + 1).ToString("N0", CultureInfo.CurrentCulture).Replace(" ", " ");
            var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                _typeface, _fontSize * 0.9, GutterForeground, ppd);
            dc.DrawText(ft, new Point(gutter - GutterPadding - ft.WidthIncludingTrailingWhitespace,
                r * _lineHeight + (_lineHeight - ft.Height) / 2));
        }
    }

    private void DrawRange(DrawingContext dc, CachedLine cl, int from, int to, double left, double y, Brush brush)
    {
        from = Math.Clamp(from, 0, cl.Text.Length);
        to = Math.Clamp(to, 0, cl.Text.Length);
        if (to <= from)
            return;
        double x0 = ColumnX(cl, from), x1 = ColumnX(cl, to);
        dc.DrawRectangle(brush, null, new Rect(left + x0, y, Math.Max(1, x1 - x0), _lineHeight));
    }

    private void UpdateCaret()
    {
        using var dc = _caretVisual.RenderOpen();
        if (_doc is null)
            return;
        long row = Caret.Line - _topLine;
        if (row < 0 || row >= VisibleRows)
            return;
        double x = TextLeft + CaretX(Caret);
        if (x < GutterWidth)
            return;
        double y = row * _lineHeight;
        dc.DrawRectangle(Foreground, null, new Rect(Math.Round(x * PixelsPerDip) / PixelsPerDip, y, 1.5, _lineHeight));
    }

    private void ShowCaret()
    {
        _caretVisual.Opacity = IsKeyboardFocused ? 1 : 0;
        if (IsKeyboardFocused)
        {
            _blink.Stop();
            _blink.Start();
        }
    }

    // ===================== Text runs =====================

    private sealed class RunProps(Typeface typeface, double size, Brush fg) : TextRunProperties
    {
        public override Typeface Typeface => typeface;
        public override double FontRenderingEmSize => size;
        public override double FontHintingEmSize => size;
        public override TextDecorationCollection? TextDecorations => null;
        public override Brush ForegroundBrush => fg;
        public override Brush? BackgroundBrush => null;
        public override CultureInfo CultureInfo => CultureInfo.CurrentUICulture;
        public override TextEffectCollection? TextEffects => null;
    }

    private sealed class ParaProps(TextRunProperties run, double tab) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => run;
        public override TextWrapping TextWrapping => TextWrapping.NoWrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
        public override double DefaultIncrementalTab => tab;
    }

    private sealed class LineSource(string text, TextRunProperties props) : TextSource
    {
        public override TextRun GetTextRun(int index) =>
            index < text.Length
                ? new TextCharacters(text, index, text.Length - index, props)
                : new TextEndOfParagraph(1);

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int limit)
        {
            limit = Math.Clamp(limit, 0, text.Length);
            return new TextSpan<CultureSpecificCharacterBufferRange>(limit,
                new CultureSpecificCharacterBufferRange(CultureInfo.CurrentUICulture,
                    new CharacterBufferRange(text, 0, limit)));
        }

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }

    // ===================== Editing =====================

    private bool CanEdit()
    {
        if (_doc is null)
            return false;
        if (IsReadOnly)
        {
            EditRefused?.Invoke(this, EventArgs.Empty);
            return false;
        }
        var e = new System.ComponentModel.CancelEventArgs();
        BeforeEdit?.Invoke(this, e);
        return !e.Cancel;
    }

    /// <summary>Replace the selection with <paramref name="text"/>.</summary>
    public void InsertText(string text, bool typing)
    {
        if (!CanEdit())
            return;
        var s = AppSettings.Current;
        long now = Environment.TickCount64;
        bool join = typing && !s.UndoPerCharacter && now - _lastKeystrokeMs <= s.UndoCoalesceSeconds * 1000;
        if (typing && s.UndoBreakOnEnter && (text == "\r\n" || text == "\n"))
            join = false;
        _lastKeystrokeMs = typing ? now : null;
        var end = _doc!.Replace(SelectionStart, SelectionEnd, text, (Anchor, Caret), typing, join);
        Anchor = Caret = end;
        _desiredX = -1;
        AfterEdit();
    }

    private void DeleteRange(TextPos a, TextPos b)
    {
        if (a == b || !CanEdit())
            return;
        var s = AppSettings.Current;
        long now = Environment.TickCount64;
        bool join = !s.UndoPerCharacter && now - _lastKeystrokeMs <= s.UndoCoalesceSeconds * 1000;
        _lastKeystrokeMs = now;
        var end = _doc!.Replace(TextPos.Min(a, b), TextPos.Max(a, b), "", (Anchor, Caret), typing: true, join);
        Anchor = Caret = end;
        _desiredX = -1;
        AfterEdit();
    }

    private void AfterEdit()
    {
        EnsureCaretVisible();
        ShowCaret();
        UpdateCaret();
        InvalidateVisual();
        Edited?.Invoke(this, EventArgs.Empty);
        RaiseCaretMoved();
        RaiseScrollChanged();
    }

    /// <summary>Undo or redo moved the text: show the selection it restored.</summary>
    public void AfterHistoryMove((TextPos Anchor, TextPos Caret) sel)
    {
        _lastKeystrokeMs = null;
        Anchor = _doc!.ClampPos(sel.Anchor);
        Caret = _doc.ClampPos(sel.Caret);
        _desiredX = -1;
        AfterEdit();
    }

    /// <summary>Copy the selection to the clipboard; false (having said why) when it couldn't be.</summary>
    public bool Copy()
    {
        if (_doc is null || !HasSelection)
            return false;
        try
        {
            Clipboard.SetText(_doc.GetText(SelectionStart, SelectionEnd));
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or OutOfMemoryException
                                       or ArgumentOutOfRangeException)
        {
            ThemedDialog.Show(Window.GetWindow(this), "The selection could not be copied: " + ex.Message,
                "Copy failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    public void Cut()
    {
        if (!HasSelection)
            return;
        // Nothing is deleted that didn't reach the clipboard.
        if (!Copy() || !CanEdit())
            return;
        var end = _doc!.Replace(SelectionStart, SelectionEnd, "", (Anchor, Caret));
        Anchor = Caret = end;
        _lastKeystrokeMs = null;
        AfterEdit();
    }

    public void Paste()
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch (System.Runtime.InteropServices.COMException) { return; }
        if (string.IsNullOrEmpty(text))
            return;
        InsertText(text, typing: !AppSettings.Current.UndoBreakOnPaste);
        _lastKeystrokeMs = null;
    }

    public void SelectAll()
    {
        if (_doc is null)
            return;
        long last = LineCount - 1;
        Select(new TextPos(0, 0), new TextPos(last, _doc.LineLength(last)), scroll: false);
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string gesture, Action act)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
            return mi;
        }
        var cut = Item("Cu_t", "Ctrl+X", Cut);
        var copy = Item("_Copy", "Ctrl+C", () => Copy());
        Item("_Paste", "Ctrl+V", Paste);
        menu.Items.Add(new Separator());
        Item("Select _All", "Ctrl+A", SelectAll);
        menu.Opened += (_, _) => { cut.IsEnabled = copy.IsEnabled = HasSelection; };
        return menu;
    }

    // ===================== Keyboard =====================

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text))
            return;
        // Control characters arrive here too (Ctrl+letter, Backspace, Enter, Esc); keys handle those.
        if (e.Text.Any(c => char.IsControl(c) && c != '\t'))
            return;
        InsertText(e.Text, typing: true);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _doc is null)
            return;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        bool shift = mods.HasFlag(ModifierKeys.Shift);
        bool alt = mods.HasFlag(ModifierKeys.Alt);
        if (alt)
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        switch (key)
        {
            // Without Shift, Left and Right first collapse a selection to its own edge.
            case Key.Left when HasSelection && !shift && !ctrl: MoveTo(SelectionStart, false, keepX: false); break;
            case Key.Right when HasSelection && !shift && !ctrl: MoveTo(SelectionEnd, false, keepX: false); break;
            case Key.Left: MoveTo(ctrl ? WordLeft(Caret) : CharLeft(Caret), shift, keepX: false); break;
            case Key.Right: MoveTo(ctrl ? WordRight(Caret) : CharRight(Caret), shift, keepX: false); break;
            case Key.Up when ctrl: ScrollTo(_topLine - 1); break;
            case Key.Down when ctrl: ScrollTo(_topLine + 1); break;
            case Key.Up: MoveVertically(-1, shift); break;
            case Key.Down: MoveVertically(1, shift); break;
            case Key.PageUp: PageMove(-1, shift); break;
            case Key.PageDown: PageMove(1, shift); break;
            case Key.Home when ctrl: MoveTo(new TextPos(0, 0), shift, keepX: false); break;
            case Key.End when ctrl:
                MoveTo(new TextPos(LineCount - 1, _doc.LineLength(LineCount - 1)), shift, keepX: false); break;
            case Key.Home: MoveTo(SmartHome(Caret), shift, keepX: false); break;
            case Key.End: MoveTo(new TextPos(Caret.Line, _doc.LineLength(Caret.Line)), shift, keepX: false); break;
            case Key.Back:
                if (HasSelection) DeleteRange(Anchor, Caret);
                else DeleteRange(ctrl ? WordLeft(Caret) : CharLeft(Caret), Caret);
                break;
            case Key.Delete when shift && !ctrl: Cut(); break;
            case Key.Delete:
                if (HasSelection) DeleteRange(Anchor, Caret);
                else DeleteRange(Caret, ctrl ? WordRight(Caret) : CharRight(Caret));
                break;
            case Key.Enter: InsertText("\r\n", typing: true); break;
            case Key.Tab when !ctrl: InsertText("\t", typing: true); break;
            case Key.A when ctrl && !shift: SelectAll(); break;
            case Key.C when ctrl && !shift: Copy(); break;
            case Key.Insert when ctrl && !shift: Copy(); break;
            case Key.X when ctrl && !shift: Cut(); break;
            case Key.V when ctrl && !shift: Paste(); break;
            case Key.Insert when shift && !ctrl: Paste(); break;
            default: e.Handled = false; break;
        }
    }

    private void MoveTo(TextPos p, bool extend, bool keepX)
    {
        p = _doc!.ClampPos(p);
        Caret = p;
        if (!extend)
            Anchor = p;
        if (!keepX)
            _desiredX = -1;
        _lastKeystrokeMs = null;
        AfterCaretMove(scroll: true);
    }

    private void MoveVertically(long delta, bool extend)
    {
        if (_desiredX < 0)
            _desiredX = CaretX(Caret);
        long line = Math.Clamp(Caret.Line + delta, 0, LineCount - 1);
        if (line == Caret.Line)
        {
            // Past the first or last line: go to its start or end, as a text box does.
            MoveTo(delta < 0 ? new TextPos(line, 0) : new TextPos(line, _doc!.LineLength(line)), extend, keepX: true);
            return;
        }
        MoveTo(new TextPos(line, ColumnAt(Layout(line), _desiredX)), extend, keepX: true);
    }

    private void PageMove(int direction, bool extend)
    {
        int rows = Math.Max(1, FullRows - 1);
        ScrollTo(_topLine + direction * rows);
        MoveVertically(direction * rows, extend);
    }

    private TextPos CharLeft(TextPos p)
    {
        if (p.Col > 0)
        {
            string text = _doc!.GetLine(p.Line);
            int col = p.Col - 1;
            if (col > 0 && char.IsLowSurrogate(text[col]) && char.IsHighSurrogate(text[col - 1]))
                col--;
            return new TextPos(p.Line, col);
        }
        if (p.Line == 0)
            return p;
        long prev = p.Line - 1;
        int len = _doc!.LineLength(prev);
        // A joined line has no break to step over: the character before is its last one.
        return new TextPos(prev, _doc.IsJoined(prev) ? Math.Max(0, len - 1) : len);
    }

    private TextPos CharRight(TextPos p)
    {
        string text = _doc!.GetLine(p.Line);
        if (p.Col < text.Length)
        {
            int col = p.Col + 1;
            if (col < text.Length && char.IsLowSurrogate(text[col]) && char.IsHighSurrogate(text[col - 1]))
                col++;
            return new TextPos(p.Line, col);
        }
        if (p.Line >= LineCount - 1)
            return p;
        return new TextPos(p.Line + 1, _doc.IsJoined(p.Line) ? Math.Min(1, _doc.LineLength(p.Line + 1)) : 0);
    }

    private static int CharClass(char c) =>
        char.IsWhiteSpace(c) ? 0 : char.IsLetterOrDigit(c) || c == '_' ? 1 : 2;

    private TextPos WordLeft(TextPos p)
    {
        if (p.Col == 0)
            return CharLeft(p);
        string t = _doc!.GetLine(p.Line);
        int i = p.Col;
        while (i > 0 && CharClass(t[i - 1]) == 0) i--;
        if (i > 0)
        {
            int cls = CharClass(t[i - 1]);
            while (i > 0 && CharClass(t[i - 1]) == cls) i--;
        }
        return new TextPos(p.Line, i);
    }

    private TextPos WordRight(TextPos p)
    {
        string t = _doc!.GetLine(p.Line);
        if (p.Col >= t.Length)
            return CharRight(p);
        int i = p.Col;
        int cls = CharClass(t[i]);
        while (i < t.Length && CharClass(t[i]) == cls) i++;
        while (i < t.Length && CharClass(t[i]) == 0) i++;
        return new TextPos(p.Line, i);
    }

    private TextPos SmartHome(TextPos p)
    {
        string t = _doc!.GetLine(p.Line);
        int indent = 0;
        while (indent < t.Length && (t[indent] == ' ' || t[indent] == '\t')) indent++;
        return new TextPos(p.Line, p.Col == indent ? 0 : indent);
    }

    private (TextPos, TextPos) WordAt(TextPos p)
    {
        string t = _doc!.GetLine(p.Line);
        if (t.Length == 0)
            return (p, p);
        int at = Math.Min(p.Col, t.Length - 1);
        int cls = CharClass(t[at]);
        int s = at, e = at;
        while (s > 0 && CharClass(t[s - 1]) == cls) s--;
        while (e < t.Length && CharClass(t[e]) == cls) e++;
        return (new TextPos(p.Line, s), new TextPos(p.Line, e));
    }

    private (TextPos, TextPos) LineRange(long line)
    {
        var start = new TextPos(line, 0);
        var end = line + 1 < LineCount ? new TextPos(line + 1, 0) : new TextPos(line, _doc!.LineLength(line));
        return (start, end);
    }

    // ===================== Mouse =====================

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_doc is null)
            return;
        Focus();
        Keyboard.Focus(this);
        var pt = e.GetPosition(this);
        var pos = PositionAt(pt);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _lastKeystrokeMs = null;

        if (pt.X < GutterWidth)
        {
            // A press in the gutter selects whole lines; dragging extends by lines.
            var (ls, le) = LineRange(pos.Line);
            _selectingLines = true;
            _lineSelectOrigin = shift ? Anchor : ls;
            Anchor = shift ? Anchor : ls;
            Caret = shift && pos.Line < Anchor.Line ? ls : le;
        }
        else if (e.ClickCount == 2)
        {
            var (ws, we) = WordAt(pos);
            Anchor = ws;
            Caret = we;
        }
        else if (e.ClickCount >= 3)
        {
            var (ls, le) = LineRange(pos.Line);
            Anchor = ls;
            Caret = le;
        }
        else
        {
            Caret = pos;
            if (!shift)
                Anchor = pos;
        }
        _desiredX = -1;
        _selecting = true;
        _lastMouse = pt;
        CaptureMouse();
        e.Handled = true;
        AfterCaretMove(scroll: false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting || !IsMouseCaptured)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndSelecting();
            return;
        }
        _lastMouse = e.GetPosition(this);
        ExtendToMouse();
        bool outside = _lastMouse.Y < 0 || _lastMouse.Y > ActualHeight
                    || _lastMouse.X < GutterWidth || _lastMouse.X > ActualWidth;
        if (outside) _dragScroll.Start();
        else _dragScroll.Stop();
    }

    private void ExtendToMouse()
    {
        var pos = PositionAt(_lastMouse);
        if (_selectingLines)
        {
            var (ls, le) = LineRange(pos.Line);
            if (pos.Line < _lineSelectOrigin.Line) { Anchor = LineRange(_lineSelectOrigin.Line).Item2; Caret = ls; }
            else { Anchor = _lineSelectOrigin; Caret = le; }
        }
        else
            Caret = pos;
        UpdateCaret();
        InvalidateVisual();
        RaiseCaretMoved();
    }

    private void DragScrollTick()
    {
        if (!_selecting)
        {
            _dragScroll.Stop();
            return;
        }
        double step = 0;
        if (_lastMouse.Y < 0) step = Math.Floor(_lastMouse.Y / _lineHeight);
        else if (_lastMouse.Y > ActualHeight) step = Math.Ceiling((_lastMouse.Y - ActualHeight) / _lineHeight);
        double h = _hOffset;
        if (_lastMouse.X < GutterWidth) h -= _spaceWidth * 2;
        else if (_lastMouse.X > ActualWidth) h += _spaceWidth * 2;
        ScrollTo(_topLine + (long)Math.Clamp(step, -50, 50), h);
        ExtendToMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndSelecting();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _selecting = false;
        _selectingLines = false;
        _dragScroll.Stop();
    }

    private void EndSelecting()
    {
        _selecting = false;
        _selectingLines = false;
        _dragScroll.Stop();
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int lines = SystemParameters.WheelScrollLines;
        if (lines <= 0) lines = 3;
        double notches = e.Delta / 120.0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            ScrollTo(_topLine, _hOffset - notches * _spaceWidth * 8);
        else
            ScrollTo(_topLine - (long)Math.Round(notches * lines));
        e.Handled = true;
    }

    protected override void OnQueryCursor(QueryCursorEventArgs e)
    {
        base.OnQueryCursor(e);
        e.Cursor = e.GetPosition(this).X < GutterWidth ? Cursors.Arrow : Cursors.IBeam;
        e.Handled = true;
    }
}
