using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace NotepadRedo;

/// <summary>
/// A minimal single-span text diff: everything before <see cref="Pos"/> and after the
/// changed region is shared between parent and child. <see cref="OldText"/> is the
/// parent's span, <see cref="NewText"/> the child's. This is always a correct
/// reconstruction (prefix + span + suffix), and tiny for ordinary edits.
/// </summary>
public sealed class TextEdit
{
    public int Pos { get; }
    public string OldText { get; }
    public string NewText { get; }

    private TextEdit(int pos, string oldText, string newText)
    {
        Pos = pos;
        OldText = oldText;
        NewText = newText;
    }

    /// <summary>Rebuild an edit from its serialised parts.</summary>
    public static TextEdit FromParts(int pos, string oldText, string newText) =>
        new(pos, oldText, newText);

    /// <summary>Diff two strings by collapsing the common prefix and suffix. Null if equal.</summary>
    public static TextEdit? Diff(string oldText, string newText)
    {
        if (oldText == newText)
            return null;

        int min = Math.Min(oldText.Length, newText.Length);

        int p = 0;
        while (p < min && oldText[p] == newText[p])
            p++;

        int s = 0;
        while (s < min - p &&
               oldText[oldText.Length - 1 - s] == newText[newText.Length - 1 - s])
            s++;

        string oldMid = oldText.Substring(p, oldText.Length - s - p);
        string newMid = newText.Substring(p, newText.Length - s - p);
        return new TextEdit(p, oldMid, newMid);
    }

    /// <summary>Transform parent text into child text, in place.</summary>
    public void ApplyForward(StringBuilder sb)
    {
        sb.Remove(Pos, OldText.Length);
        sb.Insert(Pos, NewText);
    }

    /// <summary>Transform child text back into parent text, in place.</summary>
    public void ApplyReverse(StringBuilder sb)
    {
        sb.Remove(Pos, NewText.Length);
        sb.Insert(Pos, OldText);
    }
}

/// <summary>
/// One committed text state. Stores only the edit from its parent plus a small cached
/// preview prefix and length — never the full document. Undoing then typing creates a
/// new child branch instead of discarding the old redo history.
/// </summary>
public sealed class UndoNode : INotifyPropertyChanged
{
    /// <summary>How many characters we keep cached per node for previews.</summary>
    public const int PreviewCache = 256;

    /// <summary>Shared preview length used by every node's <see cref="Preview"/>.</summary>
    public static int PreviewLength = 30;

    /// <summary>
    /// When true, <see cref="Preview"/> returns the full (single-line) edit text and the UI trims it
    /// to the pane width with an ellipsis; when false it is clipped to <see cref="PreviewLength"/>.
    /// </summary>
    public static bool FitToWidth;

    public int Id { get; }

    public TextEdit? Edit { get; private set; }   // null only for the root
    public int CaretIndex { get; private set; }
    public int Length { get; private set; }       // full text length at this node
    public DateTime Timestamp { get; }
    public UndoNode? Parent { get; }
    public ObservableCollection<UndoNode> Children { get; } = new();

    private string _previewPrefix;   // raw first PreviewCache chars of this node's text

    public UndoNode(int id, TextEdit? edit, string fullText, int caretIndex, UndoNode? parent)
    {
        Id = id;
        Edit = edit;
        CaretIndex = caretIndex;
        Length = fullText.Length;
        Parent = parent;
        Timestamp = DateTime.Now;
        _previewPrefix = fullText.Length <= PreviewCache
            ? fullText
            : fullText.Substring(0, PreviewCache);
    }

    /// <summary>
    /// A human-readable summary of *what changed* at this node — not the (often identical) opening
    /// text of the whole document. For the root, that's the document's opening text; for every
    /// other node it's the inserted / deleted / replaced span, so sibling edits look distinct.
    /// </summary>
    public string Preview
    {
        get
        {
            // In fit-to-width mode we hand back the full single-line text and let the TextBlock trim
            // it to the pane with an ellipsis; otherwise clip to a fixed character count.
            int n = FitToWidth ? PreviewCache : Math.Max(1, PreviewLength);

            // Root (or any node without an edit): show the start of the document.
            if (Edit is null)
            {
                var t = Flatten(_previewPrefix).Trim();
                if (t.Length == 0)
                    return "\u2205 (empty)";
                return Clip(t, n);
            }

            string ins = Clip(Flatten(Edit.NewText), n);
            string del = Clip(Flatten(Edit.OldText), n);

            if (ins.Length == 0 && del.Length == 0)
                return "(no change)";
            if (del.Length == 0)
                return "+ " + ins;          // pure insertion
            if (ins.Length == 0)
                return "\u2212 " + del;     // pure deletion (minus sign)
            return del + " \u2192 " + ins;  // replacement (arrow)
        }
    }

    /// <summary>Collapse line breaks / tabs so an edit spanning newlines stays a single row.</summary>
    private static string Flatten(string s) => s
        .Replace("\r\n", "\u23ce")   // ⏎ return symbol keeps newline edits visible
        .Replace('\n', '\u23ce')
        .Replace('\r', '\u23ce')
        .Replace('\t', ' ');

    /// <summary>Truncate to n characters with a trailing ellipsis.</summary>
    private static string Clip(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "\u2026";

    public string Meta => $"#{Id}  \u00b7  {Length} chars  \u00b7  {Timestamp:HH:mm:ss}";

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent != value) { _isCurrent = value; OnPropertyChanged(); } }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// How far this row is indented in the flattened history list. A straight-line run of edits
    /// (each node with a single child) stays at the same level, so ordinary linear history reads
    /// as a flat list; indentation only steps in at genuine fork points where redo is ambiguous.
    /// Recomputed by the view whenever the tree's shape changes.
    /// </summary>
    private int _indentLevel;
    public int IndentLevel
    {
        get => _indentLevel;
        set { if (_indentLevel != value) { _indentLevel = value; OnPropertyChanged(); } }
    }

    /// <summary>Re-raise the preview/meta bindings (used when preview length changes).</summary>
    public void RaisePreviewChanged()
    {
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(Meta));
    }

    /// <summary>
    /// Rewrite this node's edit and cached text in place. Used to coalesce a continuous run of
    /// typing into a single history node (instead of one node per debounce tick) — only ever
    /// applied to the leaf node that the current typing burst created.
    /// </summary>
    internal void UpdateEdit(TextEdit edit, string fullText, int caretIndex)
    {
        Edit = edit;
        CaretIndex = caretIndex;
        Length = fullText.Length;
        _previewPrefix = fullText.Length <= PreviewCache ? fullText : fullText.Substring(0, PreviewCache);
        RaisePreviewChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// A branching undo history. Node texts are reconstructed on demand from edit deltas;
/// the only materialised full copy is whatever the caller holds for <see cref="Current"/>.
/// </summary>
public sealed class UndoTree
{
    private int _nextId;

    public UndoNode Root { get; }
    public UndoNode Current { get; private set; }

    /// <summary>
    /// The full text of the root node. The root carries no edit and is never mutated, so this is a
    /// stable anchor from which any node's text can be reconstructed by replaying forward edits —
    /// independent of whatever the caller currently holds materialised. See <see cref="Materialize"/>.
    /// </summary>
    public string RootText { get; }

    public UndoTree(string initialText = "")
    {
        RootText = initialText;
        Root = new UndoNode(_nextId++, null, initialText, initialText.Length, null);
        Current = Root;
        Current.IsCurrent = true;
    }

    public bool CanUndo => Current.Parent is not null;
    public bool CanRedo => Current.Children.Count > 0;

    /// <summary>
    /// Record the transition from the current node's text (<paramref name="oldText"/>) to
    /// <paramref name="newText"/> as a child node. Returns null when nothing changed.
    /// </summary>
    public UndoNode? Commit(string oldText, string newText, int caretIndex)
    {
        var edit = TextEdit.Diff(oldText, newText);
        if (edit is null)
            return null;
        var node = new UndoNode(_nextId++, edit, newText, caretIndex, Current);
        Current.Children.Add(node);
        Current = node;
        return node;
    }

    /// <summary>
    /// Fold a continuation edit into the current node instead of adding a new child. The current
    /// node must be a childless, non-root leaf (an in-progress typing node); its edit is recomputed
    /// as the diff from its parent's text straight to <paramref name="newText"/>, so a whole run of
    /// consecutive keystrokes collapses to one history node. <paramref name="currentText"/> is the
    /// materialised text of <see cref="Current"/>. Returns false when coalescing doesn't apply
    /// (caller should <see cref="Commit"/> a new node instead).
    /// </summary>
    public bool Coalesce(string newText, int caretIndex)
    {
        if (Current.Parent is null || Current.Children.Count > 0)
            return false;

        string parentText = Materialize(Current.Parent);
        var edit = TextEdit.Diff(parentText, newText);
        if (edit is null)
            return false;   // typing came back to exactly the parent's text — let the caller decide

        Current.UpdateEdit(edit, newText, caretIndex);
        return true;
    }

    /// <summary>The most recently created child of the current node (newest redo branch).</summary>
    public UndoNode? NewestChild()
    {
        if (Current.Children.Count == 0)
            return null;
        UndoNode best = Current.Children[0];
        foreach (var c in Current.Children)
            if (c.Id > best.Id) best = c;
        return best;
    }

    public void SetCurrent(UndoNode node) => Current = node;

    /// <summary>
    /// Reconstruct the text of <paramref name="target"/> from the immutable <see cref="RootText"/>,
    /// replaying every forward edit on the path root → target. This depends on nothing the caller
    /// holds, so it can never be derailed by a stale or corrupted "current text" — making tree
    /// navigation loss-proof: jumping to any node always yields that node's exact text.
    /// </summary>
    public string Materialize(UndoNode target)
    {
        var path = new List<UndoNode>();
        for (var n = target; n.Parent is not null; n = n.Parent)
            path.Add(n);
        path.Reverse();

        var sb = new StringBuilder(RootText);
        foreach (var n in path)
            n.Edit!.ApplyForward(sb);
        return sb.ToString();
    }

    public IEnumerable<UndoNode> AllNodes()
    {
        var stack = new Stack<UndoNode>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.Children)
                stack.Push(c);
        }
    }

    // ===================== Serialization (for cross-process tab transfer) =====================

    /// <summary>
    /// Capture the whole branching history as a flat DTO. Node texts are not stored — only the
    /// per-edit deltas plus the immutable root text — so this stays small.
    /// </summary>
    public TreeDto Serialize()
    {
        string rootText = RootText;
        var nodes = new List<NodeDto>();
        foreach (var n in AllNodes())
        {
            if (n.Edit is null || n.Parent is null)
                continue;   // root carries no edit
            nodes.Add(new NodeDto(n.Id, n.Parent.Id, n.Edit.Pos, n.Edit.OldText, n.Edit.NewText, n.CaretIndex));
        }
        nodes.Sort((a, b) => a.Id.CompareTo(b.Id));   // parents always precede children
        return new TreeDto(rootText, nodes, Current.Id);
    }

    /// <summary>Rebuild a full tree (including the current-node selection) from a DTO.</summary>
    public static UndoTree Deserialize(TreeDto dto)
    {
        var tree = new UndoTree(dto.RootText);
        var byId = new Dictionary<int, UndoNode> { [tree.Root.Id] = tree.Root };
        var textById = new Dictionary<int, string> { [tree.Root.Id] = dto.RootText };
        int maxId = tree.Root.Id;

        foreach (var nd in dto.Nodes)   // sorted so each parent already exists
        {
            if (!byId.TryGetValue(nd.ParentId, out var parent))
                continue;   // orphan (shouldn't happen) — skip defensively
            var edit = TextEdit.FromParts(nd.Pos, nd.OldText, nd.NewText);
            var sb = new StringBuilder(textById[nd.ParentId]);
            edit.ApplyForward(sb);
            string childText = sb.ToString();

            var node = new UndoNode(nd.Id, edit, childText, nd.CaretIndex, parent);
            parent.Children.Add(node);
            byId[nd.Id] = node;
            textById[nd.Id] = childText;
            if (nd.Id > maxId) maxId = nd.Id;
        }

        tree._nextId = maxId + 1;
        if (byId.TryGetValue(dto.CurrentId, out var cur))
        {
            tree.Root.IsCurrent = false;
            tree.Current = cur;
        }
        return tree;
    }
}

/// <summary>One serialised history node: its edit delta plus parent/caret metadata.</summary>
public sealed record NodeDto(int Id, int ParentId, int Pos, string OldText, string NewText, int CaretIndex);

/// <summary>A serialised branching history: the root's full text plus every edit delta.</summary>
public sealed record TreeDto(string RootText, List<NodeDto> Nodes, int CurrentId);
