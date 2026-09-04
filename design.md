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

`LineEndings` owns the conversion; `EditorView` calls it at exactly two places, `ReadDocumentText`
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
  (`DetectLineEndingFromDisk`).
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

## Persistence (all under `%LOCALAPPDATA%\NotepadRedo`)

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
