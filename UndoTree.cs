using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace TreeNotepad;

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

    public int Id { get; }
    public TextEdit? Edit { get; }        // null only for the root
    public int CaretIndex { get; }
    public int Length { get; }            // full text length at this node
    public DateTime Timestamp { get; }
    public UndoNode? Parent { get; }
    public ObservableCollection<UndoNode> Children { get; } = new();

    private readonly string _previewPrefix;   // raw first PreviewCache chars of this node's text

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

    /// <summary>First N characters of the text, flattened to a single line.</summary>
    public string Preview
    {
        get
        {
            var t = _previewPrefix
                .Replace("\r\n", " ")
                .Replace('\n', ' ')
                .Replace('\r', ' ')
                .Replace('\t', ' ')
                .Trim();
            if (t.Length == 0)
                return "\u2205 (empty)";
            int n = Math.Max(1, PreviewLength);
            return t.Length <= n ? t : t.Substring(0, n) + "\u2026";
        }
    }

    public string Meta => $"#{Id}  \u00b7  {Length} chars  \u00b7  {Timestamp:HH:mm:ss}";

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent != value) { _isCurrent = value; OnPropertyChanged(); } }
    }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    /// <summary>Re-raise the preview/meta bindings (used when preview length changes).</summary>
    public void RaisePreviewChanged()
    {
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(Meta));
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

    public UndoTree(string initialText = "")
    {
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
    /// Reconstruct the text of <paramref name="target"/> given that <paramref name="from"/>
    /// currently materialises to <paramref name="fromText"/>. Walks up to the lowest common
    /// ancestor applying reverse edits, then down to the target applying forward edits.
    /// </summary>
    public static string Reconstruct(UndoNode from, string fromText, UndoNode target)
    {
        if (ReferenceEquals(from, target))
            return fromText;

        UndoNode lca = LowestCommonAncestor(from, target);
        var sb = new StringBuilder(fromText);

        for (var n = from; !ReferenceEquals(n, lca); n = n.Parent!)
            n.Edit!.ApplyReverse(sb);

        var down = new List<UndoNode>();
        for (var n = target; !ReferenceEquals(n, lca); n = n.Parent!)
            down.Add(n);
        down.Reverse();

        foreach (var n in down)
            n.Edit!.ApplyForward(sb);

        return sb.ToString();
    }

    private static UndoNode LowestCommonAncestor(UndoNode a, UndoNode b)
    {
        var ancestors = new HashSet<UndoNode>();
        for (UndoNode? n = a; n is not null; n = n.Parent)
            ancestors.Add(n);
        for (UndoNode? n = b; n is not null; n = n.Parent)
            if (ancestors.Contains(n))
                return n;
        throw new InvalidOperationException("Nodes are not in the same tree.");
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
    /// per-edit deltas — so this stays small. <paramref name="currentText"/> must be the
    /// materialised text of <see cref="Current"/>, from which the root text is reconstructed.
    /// </summary>
    public TreeDto Serialize(string currentText)
    {
        string rootText = Reconstruct(Current, currentText, Root);
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
