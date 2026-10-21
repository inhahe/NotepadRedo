# NotepadRedo — design

WPF / .NET 8 text editor. Namespace and AssemblyName `NotepadRedo`. Single project, no external
dependencies beyond the framework. `README.md` is the user-facing feature documentation; this file
records how the app is put together and why.

## Process model

A launch is not necessarily a new editor. `App.OnStartup` decides, in order:

1. **Detach.** Unless this is already the `--detached` relaunch or a `--quit*` signalling mode, the
   process re-launches itself via `ShellExecute` (so it is not a console child) and exits, freeing
   the caller's command prompt immediately. `AllowSetForegroundWindow` hands the child our
   foreground rights.
2. **Theme.** A palette is chosen from the executable's own filename (`-Graphite`, `-Sunset`, else
   Fluent), so one build can ship under several names.
3. **Signalling modes.** `--quit`, `--quit-save`, `--quit-prompt` sweep every *other* instance over
   IPC and exit without showing a window. `build.bat` uses these before overwriting the exe;
   `--quit-prompt` returns exit code 2 if the user cancelled.
4. **Path resolution.** Every non-flag argument is made absolute *here*, while the process still has
   the launching working directory. A relative name handed to a sibling over IPC would otherwise
   resolve against that sibling's (different) cwd.
5. **Routing.** With filenames: each is offered to the siblings — `FOCUS` if one already has it
   open, then `OPEN` in tab mode. If every file was taken, exit silently. With *no* filenames and a
   sibling running (tab mode): send `PRESENT` and exit, so a bare `notepadredo` surfaces the editor
   you already have rather than a second empty window.
6. **First-instance flag.** `IpcServer.AnyOtherInstanceRunning()` decides whether this launch is a
   cold start. Only a cold start restores the previous session.

### IPC (`Ipc.cs`)

Each process runs a named-pipe server on `NotepadRedo.<pid>`. One request line `VERB\tARG\n`, one
reply `OK\n` / `NO\n`. Verbs: `FOCUS`, `OPEN`, `CLOSE` (token, used when a tab is torn off across
processes), `QUIT`, `QUITSAVE`, `QUITASK`, `PRESENT`. Callers iterate sibling processes by name
prefix (`NotepadRedo*`, so themed variants are reached too) and stop at the first `OK`. The quit
verbs block on `Timeout.Infinite` because their handler blocks on the user's Save prompts; the
document verbs use a bounded timeout so a wedged sibling can't hang a launch.

## Windows, tabs and documents

- **`MainWindow`** — shell: menu, toolbar, status bar and a `TabControl` of documents. Multiple
  windows per process are supported; tabs drag between them, and across processes via the `CLOSE`
  verb (the origin drops its copy after the move).
- **`EditorView`** — one document: text box, undo tree, search pane, replace pane, autosave,
  external-change watching. Raises `StatusChanged` / `TitleChanged` / `SidePaneVisibilityChanged`,
  which the shell reflects into its chrome. (That last one was `SearchVisibilityChanged` until the
  replace pane arrived — it drives `MainWindow.SyncPaneToggles`, which lights *both* toolbar
  toggles, so it is no longer search-specific.)
- `RemoveTab(ti, dispose)` distinguishes a **real close** (`dispose: true`, view destroyed, session
  may be cleared) from a **tear-off** (`dispose: false`, the view lives on and re-registers itself
  in its new home).

### Tab selection history

`_tabMru` holds the tabs most-recently-active first, maintained in `Tabs_SelectionChanged`. Closing
a tab returns to the previous one, and the one before that if it has since gone. `_suppressMru`
brackets the removal because WPF auto-selects an adjacent tab the moment one is removed, which would
otherwise overwrite the history being consulted.

### Tab header widths

Tab headers show the **full path**, truncated from the *front* (`…\folder\file.txt`) so the file
name survives — WPF's built-in trimming only cuts the end. This is the attached behaviour
`LeadingEllipsisText`: set `LeadingEllipsisText.Path` instead of `Text`, leave `TextTrimming` at
`None`, and it binary-searches the longest fitting suffix using `FormattedText`.

The budget is the element's `MaxWidth`, set from outside by `MainWindow.ApplyTabWidths` — never
derived from the label's own `ActualWidth`, which is content-driven and would ratchet the tab
narrower on every layout pass. `ApplyTabWidths` shares the strip out:

- per-tab overhead is read from `DesiredSize` (measure-based) rather than `ActualWidth`, because the
  `TabPanel` stretches the tabs in a row to fill it and that slack depends on the widths being set;
- the row count is projected from what the tabs *would* have wrapped onto under the old fixed cap,
  so the strip's vertical footprint doesn't grow;
- the resulting area is handed out greedily: a tab whose whole path is shorter than an equal cut
  takes only what it needs and the remainder is re-divided, so one long path beside short ones gets
  nearly everything.

Recomputes are queued at `DispatcherPriority.Loaded` and coalesced, since the inputs only exist once
layout has run. Triggers: tab added/removed, document retitled, strip resized.

### Search navigation

`EditorView.FindNext(backwards)` backs F3 / Shift+F3, Enter / Shift+Enter in the search box, and the
two Edit-menu items. Two decisions shape it:

- It **re-runs the search** (`RunSearch(force: true)`, which is why that method takes a `force` flag —
  it otherwise skips the scan while the pane is collapsed) instead of stepping the existing list. The
  results are rebuilt from scratch on every scan, so a remembered list index would be meaningless,
  and the document may have been edited since.
- It **anchors on the caret**, not on the selected result: the next match is the first one starting at
  or after the selection (one past it when a match is currently selected), the previous one is the
  last starting before it, and both wrap. So F3 does the obvious thing after you have clicked
  somewhere else in the document, and it works with the pane closed.

#### One result is current, in the list *and* in the text

`SelectResult(idx, keepFocus)` is the **only** thing that makes a result current. Highlighting the
row, scrolling it into view, updating the "n of m" counter, and putting the caret/selection on the
match in the document are one operation, not four — split apart they drift, and you end up with the
pane saying "2 of 3" while the caret sits somewhere else entirely. `Results_SelectionChanged` routes
straight back through it rather than doing its own navigation, so nothing can move the list's
selection without the document following.

That divergence was a real bug: **clicking the row that is already selected raises no
`SelectionChanged` at all**, so it used to do nothing but move focus. That is exactly the click you
make after wandering off in the document and wanting to get back to the match you were on. So
`Results_MouseUp` navigates unconditionally, and resolves the row *under the pointer* (walking up the
tree from the hit-test source) rather than reading `SelectedIndex` — otherwise a click on the empty
space below the last row would teleport the caret to the current match.

#### Where the focus goes

`NavigateToMatch(r, keepFocus)` takes the decision as an explicit argument rather than sniffing
`SearchPanel.IsKeyboardFocusWithin`, because the two callers that share a focus state want opposite
things (a click in the result list vs. an arrow key in it).

| Caller | `keepFocus` | Why |
|---|---|---|
| F3 / Shift+F3, Edit-menu Find Next/Previous | `false` | Landing in the document with a real caret *is* the point. F3 keeps cycling because it is a window-level `InputBinding`, so it fires with focus in the editor. |
| Enter / Shift+Enter in the search box | `true` | The box must survive so it can be pressed again. |
| Down / Up in the search box | `true` | Same reason. These exist because the result list is otherwise unreachable from the keyboard — it sits after the options and the term list in tab order, so Tab-walking to it passes through every checkbox — and because a box with a list beneath it reads as a completion popup, so the arrows are the reflex. They *step* rather than move focus into the list, which keeps the query editable and works in proximity mode, where Enter is taken by "add this term" and the arrows are the only stepping keys left. A single-line `TextBox` ignores Down/Up, so nothing is being taken away. |
| Arrow keys down the result list (`Results_SelectionChanged`) | `true` | Moving focus on the first press would make the second arrow key move the caret instead. |
| Click on a result (`Results_MouseUp`) | `false` | A click is a deliberate "take me there", so it hands the keyboard to the document. Handled separately since a click and an arrow key are indistinguishable inside `SelectionChanged` — and because re-clicking the current row fires no `SelectionChanged` at all. |

Keeping the match *visible* while focus stays in the pane is what
`FocusManager.IsFocusScope="True"` on `SearchPanel` is for. The pane is a tool beside the document,
the same relationship a toolbar or menu has, and inside its own focus scope the editor's selection
stays **active** — so the match keeps its normal selection highlight rather than vanishing.

`Editor.IsInactiveSelectionHighlightEnabled` is *not* the mechanism, despite being the obvious
candidate: measured on .NET 8 it resolves `SystemColors.InactiveSelectionHighlightBrushKey` and
coerces `SelectionBrush` from it correctly, but paints nothing, because `IsSelectionActive` has
already gone false by then. Don't reach for it (or for overriding that brush key) if this regresses.

#### Tab and Alt inside a focus-scope pane

`FocusManager.IsFocusScope="True"` earns its place (above), but it costs two things that had to be
put back by hand — both were latent in the search pane long before the replace pane existed:

- **Tab navigation dies.** A focus scope is not, by itself, a tab-navigation *container*; with the
  window as the container, Tab from inside the pane goes nowhere at all and the keystroke is simply
  swallowed by the focused `TextBox`. `KeyboardNavigation.TabNavigation="Cycle"` on the pane is
  necessary but — measured — **not sufficient**. The working fix is the shared helper
  `TabNavigateWithin(pane, e)`, called from each pane's tunnelling `PreviewKeyDown`, which does the
  `MoveFocus` itself. Cycle-within-the-pane is also the right *behaviour* for a tool pane: Tab walks
  round its own fields and Esc is how you leave.
- **Access keys were never registered** — this one is not the focus scope's fault at all, but it
  surfaced here first because these are the only buttons in the app with an `_` in their caption.
  The themed `Button` template in `Themes/Controls.xaml` used a bare `<ContentPresenter/>`, and
  `ContentPresenter.RecognizesAccessKey` **defaults to `False`**, so `"Replace _all"` rendered the
  underscore literally and `Alt+A` did nothing. Fixed on the template, so it holds for every button.

### The view stays put when the editor is re-laid out

A `ScrollViewer` keeps its offset in **pixels**. With word wrap on, how many visual rows the text
occupies depends on the editor's *width* — so every width change re-wraps the text under an offset
that no longer means the same thing, and silently lands you somewhere else in the document. Showing
or hiding the search pane or the history tree, dragging the divider, and resizing or maximising the
window all do this; so does toggling word wrap (which changes the row count without changing the
width).

That is what made **"search, press Enter, press Esc" look like it threw the caret away**: nothing had
touched the selection — the caret was still exactly on the match — but closing the pane re-wrapped the
text 320px wider and scrolled the view hundreds of paragraphs past it.

So `EditorView` anchors the view to a **character**, not a pixel offset:

- `Editor_ScrollChanged` (hooked on the template's `PART_ContentHost`, alongside
  `Editor_RequestBringIntoView`) records the character at the top of the viewport on every ordinary
  scroll, and puts that character back at the top when the event carries a `ViewportWidthChange`.
- `ApplyWordWrap` asks for the same correction explicitly, because re-wrapping changes only the
  extent's *height*.
- **Height-only changes are deliberately left alone.** The extent also grows as you type, and there
  the editor scrolling to follow the caret is exactly right — re-anchoring would fight it. That is the
  reason the trigger is the width change and not "the extent changed".
- `RestoreScrollAnchor` runs in two `DispatcherPriority.Loaded` hops: line metrics still describe the
  old wrapping until a layout pass has run, and `ScrollToLine` only promises the line is *somewhere*
  in view, so the exact top alignment can only be measured after that scroll has been applied.
- Drag-select auto-scroll opts out (`_dragScrollActive`) — it is the sole authority on the offset
  while a drag is in flight.

### Ln / Col are logical lines

`RaiseAll` counts newlines in the text rather than calling `Editor.GetLineIndexFromCharacterIndex`,
which reports the **visual row**. With word wrap on the two diverge wildly (a 1000-paragraph document
reported "Ln 2496"), and the visual number contradicted the search pane, which has always listed the
logical line of each match. `MemoryExtensions.Count` + `LastIndexOf` keep it O(n) with vectorised
scans, which is cheap enough for the per-keystroke call.

### The font is a display setting, so it has no shortcut

Bold and italic exist only inside **Format → Font…**. There used to be top-level Format → Bold /
Italic toggles on `Ctrl+B` / `Ctrl+I`, and they were a trap: a `.txt` file stores no formatting, so the
only thing they could ever do is restyle the shared display font — every document in every window at
once — yet sitting in a Format menu on word-processor shortcuts they read as "italicise what I'm
typing". `B` is beside `V` and `N`; one slip and every tab went italic, with no visible cause but a
check mark. In the Font dialog, next to the family and size, it is unmistakably a property of how the
text is *shown*, which is also where Notepad puts it.

## The history tree

Whether the pane shows is one process-wide, persisted preference (`AppSettings.ShowTree`), not a
per-document state like the search pane's. That is why its ✕ calls `MainWindow.SetTreePreference`,
the same switch as the toolbar toggle and View → Show History Tree: a per-document hide would leave
the toolbar lit for a pane that isn't showing. The one per-document exception is a pane revealed
only to pick an ambiguous redo branch (`RevealTreeTemporarily`), which the preference also closes.

Every node stores one `TextEdit` — a single contiguous splice (`Pos`, `OldText`, `NewText`) from its
parent's text to its own — and `UndoTree.Materialize` rebuilds any node's text by replaying splices
down from the immutable `RootText`. Undo, redo and a click in the history pane are all
`EditorView.NavigateTo(node)`.

### Undo lands on the change

`ChangeSite` decides where the caret goes after a move, and the answer is **on the change itself**:

- one step back (`Ctrl+Z`) selects the text the undo put back — the undone node's `OldText` span;
- one step forward (`Ctrl+Y`) selects the text the redo put back — the redone node's `NewText` span;
- a step that only took text away leaves a bare caret where that text was;
- a jump across several nodes lands on the first difference between the two texts (`TextEdit.Diff`)
  and selects **nothing**, because the net difference between distant versions can cover most of the
  document, and a selection that size is one keystroke away from replacing it.

It used to restore the caret recorded on the destination node — where the caret happened to be when
that *earlier* state was committed. Undo a line typed at the bottom of a file and the caret leapt to
wherever the previous edit had been, often off screen, so there was no way to see what the undo had
done. Selecting the restored text is the Win32 edit-control and Word convention, and it makes "did
that undo exactly what I meant?" answerable at a glance.

The selection is set under `_programmaticSelection`, and a visible replace pane is explicitly reset to
"the whole document": a scope captured as character offsets doesn't survive the text being swapped
wholesale. That reset always happened, as a side effect of undo leaving a bare caret that read as the
user clearing their selection; with undo now leaving a selection it has to be deliberate.

### A typing burst is one contiguous edit

Typing within the coalescing window is folded into one node (`UndoTree.Coalesce`), but **only an edit
that touches the burst's own region joins it**. Without that rule, typing a word, jumping to another
paragraph and typing again inside the window produced a node whose single splice stretched from the
first place to the second: one `Ctrl+Z` undid both, and the "what changed" selection would have swept
up all the untouched text between them.

The check can only see edits once they are committed, and commits are debounced by half a second —
type, press `Ctrl+End`, type again quickly, and the *pending* diff already spans both places. So
`FlushTypingBeforeCaretMove` commits pending typing the moment a click or navigation key is about to
move the caret. It does not end the burst: stepping back over a typo and carrying on is still one
step, because the correction touches the text just typed.

## How a term becomes matches

Both panes offer the same three matching options — **case sensitive**, **match whole word only**,
**regular expression** — and they are carried as one `MatchOptions` record struct rather than three
positional `bool` parameters. Three bare bools in a row are indistinguishable at a call site and
trivially transposed; naming them at construction makes that impossible, and having *one* type shared
by search and replace is what stops the two features drifting apart on what an option means.

### The options are orthogonal

Regex reinterprets the term as a pattern; whole-word then constrains **wherever that term matched**
to stand alone as a word. Neither disables the other, and that is a deliberate answer to "regex can
already do `\b`, so whole-word is redundant":

- With an alternation, the checkbox applies the constraint to the whole pattern — `cat|dog|bird`
  rather than `\b(?:cat|dog|bird)\b` — so it stays useful *with* regex on.
- Proximity mode genuinely needs it (see below), and that mode is where most people meet it.
- Removing it would push the commonest refinement of a search into regex syntax, and a checkbox that
  greys itself out when another one is ticked is worse than one that composes.

Because whole-word is applied *after* matching rather than baked into the pattern, it is a single
predicate, `SearchEngine.IsWholeWord(text, start, end)` — public so `ReplaceEngine` shares it.

That predicate constrains **only the edges that could actually be embedded**, i.e. those where the
match's own first/last character is a word character. Checking both edges unconditionally (the
obvious implementation, and the original one) is subtly wrong once the option composes with regex:
a match beginning with a space, a bracket or a newline cannot be the tail of a longer word, so
requiring a non-word character *before* it too asks for something the match already guarantees.
In practice it meant `  \[\d+_\d+\]` with whole-word ticked matched nothing at all, since the
character before the leading space is nearly always a letter — a silent empty result with no hint
as to why. Terms that begin and end in word characters, which is what the option is really for,
behave exactly as before. An empty range has no characters of its own, so both of its edges stay
constrained.

### One place that knows how a term is scanned

`SearchEngine.Occurrences(text, term, opt, allowOverlap)` is the sole enumerator: plain search,
proximity search (each term is simply its own little search) and the engine's own `FindAll` all go
through it, so literal / regex / whole-word cannot behave differently in one mode than another.

`allowOverlap` is the one axis where search and replace legitimately differ, so it is a parameter
rather than a fork: browsing wants "aa" in "aaa" to list two hits (`idx = f + 1`), splicing must not
(`idx = f + term.Length`). It only affects literal matching — regex `NextMatch()` never overlaps.

`ReplaceEngine` deliberately keeps its **own** scan loop instead of consuming `Occurrences`. It needs
the live `Match` object to expand `$1` via `Match.Result`, and it needs a scope; the honest shared
seam is therefore the *predicates* (`IsWholeWord`, `BuildRegex`), not the iteration. `BuildRegex`
lives in `SearchEngine` and is called by both, so `RegexOptions.Multiline`, `IgnoreCase` and the 2 s
timeout can't diverge between the panes.

### A bad pattern is the normal state, not an error

The search re-runs on **every keystroke**, and you necessarily pass through `(` on the way to
`(a|b)` — so an unparseable pattern is what the box holds most of the time it is being typed into.
`RunSearch` therefore catches `ArgumentException` (which `RegexParseException` derives from) and
`RegexMatchTimeoutException` and writes them into the pane's status line, leaving the previous
results cleared. It is a status message, not a dialog and not an exception that reaches the shell.

### The proximity label has to work while the mode is off

The checkbox used to read *"Only where items are near each other"*, which answers none of the
questions it raises: what is an item, does it split on spaces or commas, can a term *be* a whole
string, and how near is near. The reason it read that way is a UI-ordering trap — everything that
would explain the mode (the term list, the `within [40] [characters] of each other` row) is
`Collapsed` **until the mode is on**, so the label is read in the one state where no explanation is
visible.

So the label states the mode's shape by itself — **"Find several terms near each other"** — the
distance row is a visible, editable option rather than a hidden constant, and the tooltip says that
terms are entered one at a time and may therefore contain spaces (which is also why no quoting or
delimiter syntax exists, and why "search for the entire string" is just a term with spaces in it).
`UpdateSearchBoxHint` spells out all four combinations of proximity × regex in the search box's
tooltip rather than one generic sentence that would be wrong in three of them.

Entering proximity mode turns whole-word **on** by default (terms in a proximity query are nearly
always words; without it `op` and `po` both match inside `opposite` and every such word is a false
cluster). `_wholeWordBeforeProximity` restores the user's own setting on the way out, and
`_syncingWholeWord` stops that programmatic toggle from re-entering `SearchOption_Changed`.

## Replace

`ReplaceEngine` is pure and UI-free, the same shape as `SearchEngine`: `Find(text, find,
replacement, opt, scopeStart, scopeLength)` returns `ReplaceMatch(Start, Length,
Replacement)` records with each match's replacement text **already resolved** (so `$1` is expanded
once, where the `Match` object is still in hand), and `Apply` splices them in.

It is deliberately *not* `SearchEngine.FindAll`. Two differences matter:

- **Matches must not overlap.** `FindAll` advances `idx = f + 1` so it can report overlapping hits
  to the results list; a replace scan advances past the whole match. Replacing overlapping matches
  is meaningless.
- **A zero-width regex match must still advance the scan**, or Replace All spins forever.

Whole-word is judged against the **whole document**, not against the scope: restricting a replace to
a selection that cuts through the middle of a word must not make that fragment look like a word.

The regex scan uses `re.Match(text, startat)` + `NextMatch()`, **not** the
`Match(text, beginning, length)` overload. That overload redefines where the string begins and ends
as far as the engine is concerned, so with `RegexOptions.Multiline` on, `^` would match at the first
character of the selection even mid-line, and `\b` / lookbehind would stop seeing the surrounding
document. `NextMatch()` also handles stepping past a zero-width match. Matches that *straddle* the
end of the scope are skipped rather than truncated. `Multiline` is on because `^`/`$` meaning
line start/end is what a text-editor user expects; a 2 s `RegexTimeout` guards catastrophic
backtracking.

`BuildRegex` throws `ArgumentException` for a bad pattern (`RegexParseException` derives from it),
and `Match.Result` throws the same type for a bad `$`-construct in the *replacement* — which is why
the pane says "Invalid regular expression: …" rather than "Invalid pattern".

### Its own column

The replace pane is a **fifth grid column**, not a second tenant of the search column, so both panes
can be open at once. Everything that measures the chrome beside the editor (`Splitter_MouseMove`)
sums both columns' widths.

### The selection scope is captured, not read live

`_replaceScope` is a stored `(Start, Length)`, not a read of `Editor.SelectionLength` at replace
time. It has to be: stepping to the next match *is itself* a selection change, so a live read would
see the scope collapse to the single match on the first Replace Next.

- `_programmaticSelection` brackets every selection the app makes, so only the **user's** selection
  changes redefine the scope (or, when they clear it, disable "The selected text" and fall back to
  the whole document).
- `ShiftReplaceScope(delta)` keeps the captured range correct across each edit. `ApplyReplacementText`
  deliberately does **not** re-derive the scope from the selection afterwards — a single Replace
  Next leaves a bare caret, which would read back as "the user cleared their selection" and throw
  the scope away mid-run.
- The scope radios have **no `GroupName`**: `RadioButton` groups by name per *visual root*, and every
  tab shares one window, so a name would make all open documents' radios one group. Without one they
  group by their shared parent panel, which is what's wanted.

### One undo node per replace

Both buttons route through `SetEditorText`, which suppresses `TextChanged` — that is what makes a
whole Replace All a single node in the undo tree. The price is that everything which normally rides
on `TextChanged` has to be re-run by hand afterwards (`RunSearch`, `RaiseAll`, and the scroll
position). Assigning `Editor.Text` also scrolls the view to the top, so `ApplyReplacementText` queues
a `BringIntoView(caret)` at `DispatcherPriority.Background`; Replace Next queues its own scroll to
the *following* match after that, which lands later in the queue and therefore wins, as it should.

### Highlight, then replace

Replace Next is a two-press cycle by design: it replaces only the match that is **currently
selected**, so the first press (when the selection isn't a match) merely highlights and scrolls to
the next one. You always see what you are about to change. `HighlightRange` is shared with the
search pane, so both features put you on text identically.

## Line endings: two representations, converted at the edges

A document exists in two forms and they must never be confused. **On disk** it has whatever endings
it came with; **in the editor** it is always CRLF. The second half is not a choice — WPF's `TextBox`
inserts `\r\n` whenever the user presses Enter, whatever the surrounding buffer uses. Holding an LF
file verbatim therefore produces a *mixed* buffer the moment anyone types in it, and that mixture is
what gets written back.

`LineEndings` owns the conversion; `EditorView` calls it at exactly two places, `ReadDisk`
(disk → buffer, remembering the file's style) and `WriteTextToFile` (buffer → disk, re-applying it).
Everything between them can assume a uniform buffer.

### What this actually fixed

An LF file was rewritten by another program as CRLF. The raw strings differed, so
`HandleExternalChange` raised the five-way "changed on disk" prompt — but `DiffEngine.SplitLines`
normalises CRLF/CR to LF *before* splitting, so the merge viewer computed **zero** differing rows and
showed two panes that were, visibly, identical. The user was asked to resolve a conflict that could
not be seen. Reconstructing that document's own undo tree from its history sidecar showed the whole
mechanism: the file loaded with 0 CR / 12 LF, gained a CR at every node where Enter had been pressed,
and at the node where the prompt fired the buffer was line-for-line identical to disk while differing
from it in eleven bytes.

Three separate defects sat behind that, and all three are fixed by converting at the edges:

- the app **created** mixed-ending files by typing into non-CRLF ones;
- it **compared bytes** where it meant to compare content, so an ending-only rewrite raised a prompt;
- the merge viewer could **render nothing** and say nothing about it.

### The rules

- **The style follows the file.** If something else rewrites the file as LF, the next save keeps it
  LF rather than flipping it back and starting a tug-of-war. `AdoptDiskLineEnding` is called from
  every path that reads the file. The one thing that outranks the file is an explicit, unsaved
  **Format → Line endings** choice, which is why `_lineEnding` (what we will write) and
  `_savedLineEnding` (what is on disk) are separate fields.
- **A pending style change counts as dirty**, even though the text is untouched — otherwise the Save
  that would apply it is a no-op and the menu appears to do nothing.
- **An ending-only change on disk is adopted silently.** It is not a conflict: the content is
  identical and there is nothing for the user to resolve.
- **Reads that don't go through the file** — a tab moved from another process, a crash-recovery
  snapshot — normalise their carried text and take the style from the file they point at
  (`TryReadDisk`). The same read decides whether the file has moved on since that text was saved
  (`EstablishPathForForeignText`, below).
- **The history stamp is taken over the converted text.** Sidecars written before this existed no
  longer match for LF files, so those documents start from a fresh root once — the same self-healing
  path a file edited outside the app already took.

### The merge viewer never shows an unexplained blank diff

`UpdateInvisibleNotice` runs on every render: when no op is anything but `Equal` while the two texts
differ, `LineEndings.DescribeInvisibleDifference` names the reason — line endings (with each side's
style), trailing whitespace and on how many lines, or invisible characters and the offset of the
first one — and a banner says so. This is deliberately a *fallback* rather than something folded into
the diff itself: normalising endings before splitting is the right behaviour for reading a diff, and
the notice covers the case where that normalisation is precisely what hides the answer.

## The "changed on disk" prompt

A `FileSystemWatcher` per titled document feeds a 350 ms debounce, then `HandleExternalChange`:
probe the file's stamp (modified time + size); if it differs from `_diskStamp`, read the file and
compare *content*; if it holds something new, ask the five-way question. The rules below keep that
honest. Their shared aim: **no write made elsewhere is ever overwritten without a question**.

### The stamp belongs to a version's content

`_diskStamp` must describe the disk version the document was last reconciled with — loaded, saved,
or answered for — or be null when there is none (a file that doesn't exist yet, or a recovered
document whose file has moved on). A stamp *newer* than that content is the dangerous direction:
the next probe sees "no change", and a write nobody has seen is treated as handled. So:

- `ReadDisk` takes the stamp through the same handle **before** reading the text. A write landing
  mid-read leaves the stamp older than the text, which costs at most one re-check that finds nothing.
- `WriteTextToFile` takes the stamp through the *writing* handle before letting it go. That handle
  shares reading only, so nobody else can have written in between. If the time still moves when
  the handle closes (an SMB server settling it), the next check finds the content equal to what was
  saved and adopts it. A probe after closing could already see someone else's write.
- Every caller records `_diskStamp` itself, because only the caller knows where the stamp comes
  from. `LoadFile` and `ReloadFromDisk` use the read's own stamp, and `Save` uses the write's.
- Text that came from elsewhere (a recovery snapshot, a tab moved from another process) carries
  its own `_savedText`, and the file may have moved on since. A crash can be recovered days later.
  `EstablishPathForForeignText` adopts the file only if it holds nothing new. Otherwise it records
  no stamp, and installing the watcher checks the file at once, which raises the prompt.
- Every stamp comes from a handle opened one way (`OpenShared`, `StampOf`), so probes and reads are
  never compared across two kinds of query.

A stamp-based check has one blind spot: a same-length edit made within one timestamp tick of the
reconciled version (2 s on FAT32). It goes unseen. Closing that gap would mean reading the whole
file on every check; see KNOWN-ISSUES.

### What counts as news

`HoldsNothingNew`: a file that holds what the document last loaded or saved, exactly what is in the
editor, or a version the user already chose to write over (`_overruledText`, set by Keep mine and by
saving the disk version off) is adopted without a word, since writing over it loses nobody's work.
That covers our own save reported late, a touch, a sync client fetching the file again, and another
program saving the very text we have. `Adopt` also marks the document saved when the file now
holds exactly the editor's text. Both sides are compared in the editor's line-ending form, because a
paste can leave the buffer itself mixed. One cost: Keep mine leaves `_savedText` at the last load
or save, so a tab moved to another process after Keep mine asks again.

### Answering the question

The question can sit on screen for hours while another machine keeps saving to the share.

- **Reload, merge, and the two "save to another file" choices act on the file as it is when the
  user answers** (`Newest`: the version that raised the prompt if the stamp hasn't moved, else a
  fresh read). Reload loads it, the merge viewer shows it, and the copy is made from it.
- **Keep mine settles only the version it was asked about** (`Overrule`). A newer one arrived
  unseen, so it gets a question of its own. Settling it too would let the next save overwrite work
  nobody looked at.
- **Closing the question means "not now".** The version goes into `_diskStamp`, so the watcher
  doesn't ask again, and into `_unanswered`, so saving over it does.
- **A version is settled only once its choice has been carried out.** If saving a copy fails, the
  change stays unanswered and the question comes back.
- While a prompt is up, `HandleExternalChange` does nothing for that document; it is queued once
  more after the prompt closes, which catches anything written meanwhile. A merge viewer opened
  from the prompt is the exception. It receives each new version live through `NotifyDiskChanged`,
  even one equal to the editor's text, since its on-disk side must follow the file. Those versions
  are tracked in `_mergeSeen`, not `_diskStamp`. Only when the viewer closes with nothing left
  unresolved (`DiffMergeWindow.HasUnresolvedDiskChange`) is the newest one settled. A re-change
  whose question in the viewer was closed, or whose copy failed, stays unanswered and is asked
  about after the viewer closes.
- **One question at a time across documents** (`_promptsOpen`). A document whose check comes due
  while another's prompt is up joins `_waitingForPrompt`, and is woken when the last prompt
  closes. Otherwise a second modal would stack on top, come up first, and cover a tab that isn't
  its own. The prompt also selects its document's tab.
- A closed tab (`_disposed`) asks nothing, even if a check was already posted for it. Save can now
  raise the question from closing a tab, and the check that follows an answer may run after the tab
  is gone.

This fixed a reported bug: on a mapped network drive, Reload loaded *a* change but not the latest.
The prompt reloaded the text read when it went up, then probed the stamp, which recorded the newer
write as seen. A change that had arrived during the prompt was stashed for a re-check, which then
compared against that fresh stamp and found nothing. Network drives made it common only because
saves from another computer arrive while nobody is at this one to answer; the same thing happens
locally.

### Saving checks first

The watcher can miss a change. A share may send no change notifications at all, a write can land
while the watcher is being restarted, or the folder may not be watchable when the file is opened.
So `Save` calls `CheckBeforeOverwrite` before writing over the document's own file:

- **Write** if the file is gone, unchanged since it was reconciled (with no unanswered version), or
  holds nothing new.
- **Ask** otherwise, with the usual question. Only Keep mine, or saving the disk version off first,
  goes on to write, and only if nothing newer landed meanwhile. A question that ends in Reload,
  or in a merge that saved itself, means the document now *is* the file, so `Save` reports success
  without writing (`NowMatchesFile`, told apart by `_reconciliations`). Any other outcome is a
  refusal, and `Save` reports failure.
- **Refuse** when no question can be asked. That is the case for a non-interactive save (`--quit-save`
  passes `interactive: false`; the document goes to crash recovery instead, and no dialog of any
  kind is shown, a failed write included). It is also the case for a save made inside this
  document's own question: the merge viewer saving its result over a version it never showed, or a
  quit sweep arriving through IPC while the question is open.
- **Can't read it:** if the stamp has moved but the file can't be read, it is never overwritten
  unasked. A separate question offers to overwrite anyway, with Don't save as the default, and a
  non-interactive save refuses.

With watching turned off, the check is skipped, as are all the others. Saving to the same file
leaves the running watcher alone. Restarting it on every save would leave a moment in which
nothing watches, and would restart a pending retry's back-off.

### Watching keeps going

A `FileSystemWatcher` that raises `Error` has stopped for good, except after a buffer overflow:
.NET clears `EnableRaisingEvents` and raises the event once. On a network drive that is routine.
The share drops whenever either machine sleeps or the connection blips, and the document used to go
deaf to outside changes until it was saved or reopened. `OnWatcherError` therefore recreates the
watcher in `ResumeWatching`, retrying after 1, 2, 4 … up to 60 s while the folder can't be reached.
`StartWatching` does the same when no watcher can be made in the first place (a document opened
or recovered while its share is offline). `InstallWatcher` checks the file once, whenever a watcher
starts. Anything written while nobody was watching raised no event, and a document whose watching
was just turned on may hold a version it never reconciled. Details:

- Each retry runs on the thread pool. Creating a watcher on an unreachable share blocks until the
  network gives up, which is the same tens-of-seconds stall the activation rule below guards against.
- An overflow is told apart by its exception (`InternalBufferOverflowException`), not by
  `EnableRaisingEvents`. That flag is cleared when a pending read fails, but not when a new read
  can't be started at all, which leaves a watcher that looks enabled and watches nothing.
- That second kind of failure is raised from inside `EnableRaisingEvents`, so for a watcher made on
  the thread pool it can come before `ResumeWatching` has installed it. Each `FileWatcher` therefore
  carries its own `Failed` flag, set on the thread raising the error before anything is posted, and
  the retry discards a failed one instead of installing it dead. (A single shared field for this
  could be wiped by an older retry finishing late.)
- A watcher that dies within two minutes of starting carries on the back-off (`_resumeAttempt`)
  rather than starting it over, so a server that refuses notifications isn't asked every second.
- `_watchGeneration` lets a pending retry see that watching was restarted or stopped meanwhile (a
  Save As, the setting turned off, the tab closed) and drop the watcher it made.

### Tests

`tests/ExternalChange` hosts real `EditorView`s off-screen, with real watchers, and answers prompts
by clicking their buttons. Each of the rules above has a test that fails without it, except where
the failure can't be produced on demand: the write-handle stamp, the watcher that fails while
being started, and the running watcher being kept across saves. Every test runs against a local
folder and, with `--share`, over SMB (`\\localhost\<drive>$`). The watcher-error test runs over SMB
only: deleting the watched folder there stops the watcher with an error the way a dropped connection
does, while locally a watcher outlives its folder. The data folder is pointed at a scratch folder
through `NOTEPADREDO_DATA_DIR` (see Persistence), so a run never reads the user's settings or leaves
anything in their recovery folder.

## The merge viewer's two panes are levelled, not merely paired

The two documents are built row for row — a diff op contributes one paragraph to each side, with a
one-space spacer standing in where a side has no line. That pairs the rows but **does not align
them**, because a row's height depends on how its line wraps: the spacer is one visual row while the
line opposite it may wrap to four. Every wrapped line therefore pushes one side down relative to the
other, the error accumulates downwards, and past the first screenful the panes have nothing to do
with each other — measured on a 600-row diff, identical text ended up **1300 px apart**, which is what
"the matching text portions are supposed to be aligned" was reporting.

`AlignRows` levels each pair after layout by giving the shorter paragraph bottom **padding** (not
margin — padding is inside the `Background`, so a tinted row's colour fills the added space and the
block still reads as one unit). Two passes are unavoidable: the correction is the difference between
two heights that only exist once WPF has laid the text out. Previous padding is cleared first so each
pass measures natural heights rather than compounding the last one, and because padding never changes
how text *wraps*, one corrective pass always converges.

Three things keep it honest:

- **It re-runs whenever wrapping can change** — either pane resizing (the window or the splitter) and
  edits to the kept side — debounced, because a resize drag raises `SizeChanged` continuously.
- **Rows are levelled independently**, which is what makes the cheap path correct: when the user
  types, only the caret's paragraph can have changed height, so fixing *that* row restores the whole
  column. A keystroke costs four measurements instead of re-measuring the document (10 ms vs. a
  visible stall on a 600-row diff). Structural edits — Enter, Backspace joining two paragraphs, a
  multi-line paste — change the paragraph count and fall back to the full pass.
- **The full pass probes each paragraph once**, at its top, taking its height as the distance to the
  next paragraph's top (they are stacked with no margin, so that distance *is* the height). Resolving
  a text pointer against laid-out text costs about an eighth of a millisecond — nothing once, a third
  of a second across both sides of a 600-row diff.

`_aligning` guards re-entrancy: setting a `Block`'s padding raises `TextChanged`, which would
otherwise re-enter through the edit path forever. `_rendering` does the same for the `TextChanged`
storm raised by swapping the documents, which is not the user typing.

## Persistence (all under `%LOCALAPPDATA%\NotepadRedo`)

Every class takes the folder from `AppPaths.DataDir`. The environment variable
`NOTEPADREDO_DATA_DIR` names another folder instead: the tests use it so that a run never touches
the user's own data. `AppPaths` also performs the one-time move of the pre-rename `TreeNotepad`
folder. It used to live in `AppSettings`' static constructor, where any class that created the new
folder first (the crash log, say) silently stopped the move. It is skipped when the variable is
set, so a scratch folder can never receive the user's real data.

| File | Owner | Contents |
|---|---|---|
| `settings.json` | `AppSettings` | All options. `Reload()` re-reads on window activation so instances stay in sync — **any new setting must be added to `Reload()` as well as `Save()`**. Gated by a `FileSystemWatcher` dirty flag, so the usual activation costs no I/O (see below). |
| `session.json` | `SessionStore` | Paths of open *saved* files, for session restore. |
| `recent.json` | `RecentFiles` | Up to 15 most-recently-opened paths for File → Open Recent. Re-read before each write so concurrent instances merge instead of clobbering; raises `Changed` so open windows rebuild their submenu. **`Changed` may be raised on a background thread** — handlers that touch UI must marshal to the dispatcher. |
| `recovery\` | `EditorView` | Crash-recovery snapshots — this is where *unsaved* and untitled work lives, including for titled documents. |
| `history\` | `HistoryStore` | Optional persisted undo trees, keyed to the file's on-disk content; pruned after 90 days. |
| `crash.log` | `CrashLog` | Every unhandled exception with a full traceback. |

Session rules worth remembering:

- `SaveSession(allowEmpty: false)` refuses to write an empty list, so a blank/`--new` window can't
  wipe a real session. Only a genuine close of the last tab passes `allowEmpty: true`.
- `_forceQuitting` suppresses session writes during a redeploy sweep.
- `OnClosing` deliberately does *not* save the session, so `session.json` keeps the last
  structural-change state rather than being rewritten at exit.
- Restore runs before command-line files are opened, so the named file lands on top as the active
  tab; `RequestOpenFile` de-dupes against what restore already reopened.

## Never touch the filesystem on the activation path

`MainWindow.Activated` runs inside `WM_ACTIVATE`, on the UI thread, with the message pump stopped.
Anything slow there freezes the window outright. This bit us: `RebuildRecentMenu` used to call a
`RecentFiles.Load()` that did `File.Exists` on every remembered path, and `File.Exists` on a path
that lives on a disconnected network share, an unmounted drive, or a spun-down disk blocks for *tens
of seconds* at 0% CPU. A dump taken mid-freeze showed exactly that stack
(`GetFileAttributesEx` → `File.Exists` → `RecentFiles.Load` → `RebuildRecentMenu` → `Window.WmActivate`).

What made it a *constant* symptom rather than a rare one is a second window — a tab torn off into its
own window. With one window, activation happens only when you alt-tab back into the app; with two, it
happens every time focus moves between them, so the same latent cost fires orders of magnitude more
often. Reproduce by putting an unroutable UNC path (e.g. `\\192.0.2.1\share\x.txt`, TEST-NET-1) at
the head of `recent.json`; note that Windows negative-caches the host afterwards, which is why the
real-world symptom is intermittent.

So, the rule: **the `Activated` handler does no I/O.**

- `RebuildRecentMenu` renders `RecentFiles.Existing`, a cached snapshot that costs nothing to read.
  `RecentFiles.Refresh()` does the existence sweep on a threadpool thread (guarded so only one runs)
  and raises `Changed` only when the result differs; `MainWindow` marshals that back to the
  dispatcher. Paths that fail to probe are dropped from the cache but *kept in the file*, so a
  briefly-unreachable share isn't forgotten.
- `AppSettings.Reload()` returns immediately unless a static `FileSystemWatcher` on `settings.json`
  saw a change. If the watcher can't be created, or reports an error, the flag is forced on and we
  fall back to re-reading every time — stale settings would be worse than the old cost, and
  `settings.json` is always local anyway.

## Dialogs

`ThemedDialog` replaces `MessageBox` so prompts follow the palette. All three entry points —
`Show()` (Y/N/O/C), `ShowSaveAll()` (S/A/D/C) and `ShowChoices()` (1‑9) — answer to bare keys, map
Escape to the natural cancel, and call `Activate()` plus focus the default button on `Loaded`. That
last part is not cosmetic: a launch from a console leaves cmd owning the foreground, so without it
the prompt can come up behind and look like a silent hang.

## Versioning

`VERSION` at the project root is the single source of truth; the csproj reads it into `<Version>`
and `release-github.bat` tags releases from it. Bump it on every build.
