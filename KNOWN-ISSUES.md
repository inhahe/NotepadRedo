# Known Issues / Tech Debt

## Tech debt

### Merge viewer: stale line indices if you hand-edit *then* click-accept without re-diffing
- **Where:** `DiffMergeWindow.xaml.cs` — `AcceptRow` uses the `Row.KeptLineIndex` / `KeptInsertPos`
  captured at the last `Render()`. The kept (editable) pane is *not* re-diffed on manual typing
  (by design — re-rendering mid-type would fight the caret; the "Re-diff" button recomputes
  highlights on demand).
- **Consequence:** If the user manually edits the kept pane and then, *without* pressing Re-diff,
  double-clicks / right-click-accepts a source line, the stored index can be off by the net line
  delta of their manual edit, so the wrong kept line may be replaced/removed. `AcceptRow` reads the
  box back first and clamps the index, so it can't crash or corrupt structure — worst case it edits
  a neighbouring line. Pressing **Re-diff** (or doing another accept) resyncs.
- **Proper fix (deferred):** tag each rendered kept paragraph with a stable id and resolve the
  target line by id at accept-time instead of by positional index, so interleaved manual edits and
  click-accepts always hit the intended line. Low priority — the dominant workflow is click-accepts
  *or* manual editing, rarely interleaved within one line without a Re-diff.

## Bugs

### "Reload from disk" loaded an older version, not the latest (fixed 2026-10-20, 1.0.29)
- **Symptom:** on a mapped network drive, the "changed on disk" prompt appeared after the file was
  edited on another computer, and Reload brought in *a* change, but not the latest one. Nothing
  ever prompted about the rest.
- **Root cause:** every choice acted on the text read when the prompt went up. If the file changed
  again before the user answered, `ReloadFromDisk` loaded the old text and then *probed* the file's
  stamp, recording the newer write as seen. The re-check stashed for "a change arrived during the
  prompt" compared against that fresh stamp and found nothing. The same lost-update ordering (read,
  then probe) sat in `LoadFile`. Not network-specific: a network drive just makes it likely, since
  saves from another machine arrive while nobody is at this one to answer.
- **Fix:** the answer is applied to the file as it is when given (`ResolveExternalChange` re-reads
  it), and a stamp is only ever recorded with the content it belongs to (`ReadDisk` takes it through
  the read's own handle, before reading). See "The 'changed on disk' prompt" in `design.md`.
- **Regression test:** `tests/ExternalChange` (every prompt choice, locally and over SMB).

### Outside changes stopped being noticed after a network drop (fixed 2026-10-20, 1.0.29)
- **Symptom (by inspection; reproduced over SMB):** once a network drive dropped (either computer
  sleeping, a connection blip), a document on it stopped reacting to outside changes until it was
  saved or reopened.
- **Root cause:** a `FileSystemWatcher` that hits an error clears `EnableRaisingEvents` and raises
  `Error` (confirmed in the .NET 8 source, `FileSystemWatcher.Win32.cs`). Nothing handled `Error`.
- **Fix:** `OnWatcherError` recreates the watcher off the UI thread, retrying for as long as the
  folder can't be reached, then checks the file. See "A watcher that fails is restarted" in
  `design.md`.
- **Regression test:** `tests/ExternalChange -- --share` (deleting the watched folder over SMB kills
  the watcher the same way).

### Pre-rename data migration could be skipped (latent; fixed 2026-10-20, 1.0.29)
- **By inspection:** the one-time move of `%LOCALAPPDATA%\TreeNotepad` to `NotepadRedo` ran in
  `AppSettings`' static constructor, and is skipped when the new folder already exists. Any class
  that created that folder before `AppSettings` was first touched (the crash log logging an early
  exception, say) would have made the move never happen, stranding the old settings and recovery
  files.
- **Fix:** the move now lives in `AppPaths`, which every class asks for the folder, so it runs
  before anything can create it.

### A recovered document overwrote changes made to its file after the crash (fixed 2026-10-20, 1.0.29)
- **Symptom (by inspection):** crash with unsaved edits, edit the file somewhere else, relaunch and
  recover. No prompt, and the next save silently replaced the outside edit.
- **Root cause:** recovered and moved-between-processes documents carry their own `_savedText`, but
  probed the file's current stamp, marking whatever the file now held as already seen.
- **Fix:** `EstablishPathForForeignText` adopts the stamp only if the file still holds `_savedText`.
  Otherwise it checks at once, which raises the prompt.
- **Regression test:** `tests/ExternalChange` (recovery against an edited and an untouched file).
- **Known cost:** Keep mine leaves `_savedText` at the last load or save. A tab moved to another
  process after Keep mine therefore asks again. That's harmless, and rarer than the overwrite was
  bad.

### Esc after a search appeared to move the caret off the match (fixed 2026-08-14, 1.0.20)
- **Symptom:** search for something, press Enter so the match is highlighted (no clicking a result in
  the pane), then press Esc — and the document is showing somewhere else entirely, so it looks like
  the caret was thrown away.
- **Root cause:** the caret never moved at all. Closing the pane widens the editor by 320px; with word
  wrap on the text re-wraps into far fewer visual rows while the `ScrollViewer` keeps its offset in
  *pixels*, so the same offset now points hundreds of paragraphs further on. Verified by screenshot:
  the status bar read `Ln 500, Col 131` both before and after Esc while the view jumped from para 500
  to para 826. The same jump happened on every other re-layout — opening the pane, toggling the
  history tree, dragging the divider, resizing the window, toggling word wrap.
- **Fix:** `EditorView` now pins the character at the top of the viewport across width changes (and
  across a word-wrap toggle). See "The view stays put when the editor is re-laid out" in `design.md`.
- **Regression test (manual):** with word wrap on, open a document of a few hundred wrapped
  paragraphs, search for a term far down it, press Enter, then Esc — the match must still be on
  screen, in the same place. Repeat with word wrap off, and repeat toggling wrap from View → Word Wrap.

### Status bar reported wrapped rows as "Ln" (fixed 2026-08-14, 1.0.20)
- **Symptom:** with word wrap on, the caret on logical line 500 of a 1000-line file showed as
  `Ln 2496` in the status bar, while the search pane listed the same match as `Ln 500`.
- **Root cause:** `RaiseAll` used `Editor.GetLineIndexFromCharacterIndex`, which returns the *visual*
  row; `RunSearch` used the text-counting `LineOf`. Only the search pane was right.
- **Fix:** `RaiseAll` counts `'\n'` in the text (vectorised `MemoryExtensions.Count`) and derives the
  column from `LastIndexOf('\n')`.

### NullReferenceException in font live-preview across windows (defensively fixed 2026-07-17)
- **Symptom:** A `System.NullReferenceException` was logged once (crash.log, 2026-07-17 21:01)
  during `Font_Click` → `PreviewFont` → `AllOpenViews()+MoveNext()`. It was swallowed by the
  global UI-thread handler, so the app kept running.
- **Analysis:** `AllOpenViews()` enumerates `Application.Current.Windows`, and for each
  `MainWindow` calls `AllViews()`, which read `Tabs.Items`. WPF's `Window` base constructor
  registers a window into `Application.Current.Windows` *before* the derived `InitializeComponent`
  assigns the `Tabs` field, so a transiently partly-constructed window can be enumerated and
  `Tabs` observed as null. (Release-build inlining collapsed the inner iterator frame into
  `AllOpenViews+MoveNext`, which is why the stack pointed there.)
- **Fix applied:** `AllViews()` now returns an empty sequence when `Tabs is null` instead of
  dereferencing it. See `MainWindow.xaml.cs` `AllViews()`.
- **Status:** Not deterministically reproduced. If it recurs, capture the exact repro (multi-window
  tear-off + font preview timing) and revisit whether a deeper ordering fix is warranted.

### Clicking the already-selected search result did nothing (fixed 2026-08-14, 1.0.19)
- **Symptom:** The search pane showed a result highlighted ("2 of 3") while the caret was somewhere
  else and no match was highlighted in the document. Clicking that highlighted row didn't fix it.
- **Root cause:** navigation hung off `Results_SelectionChanged`, and clicking the row that is
  *already* selected raises no `SelectionChanged`. `Results_MouseUp` only called `Editor.Focus()`.
  So the one click you make after wandering off in the document — "take me back to the match I was
  on" — was the exact click that did nothing.
- **Fix:** `SelectResult(idx, keepFocus)` is now the single place that makes a result current (row
  highlight + counter + caret/selection together), `Results_SelectionChanged` routes through it, and
  `Results_MouseUp` navigates unconditionally to the row under the pointer. See "One result is
  current, in the list *and* in the text" in `design.md`.
- **Regression test (manual):** click a result, click elsewhere in the document, click the same
  result again — the caret must land on the match. Then click the empty space below the last row —
  nothing must move.

### Window freezes for seconds at a time, worse with a torn-off tab (fixed 2026-08-14, 1.0.18)
- **Symptom:** The window stopped responding for several seconds at a time, at 0% CPU. It started
  only after a tab was dragged out into its own window, and had never happened before the Open
  Recent feature landed (`f437a94`).
- **Root cause:** `MainWindow.Activated` → `RebuildRecentMenu()` → `RecentFiles.Load()`, which ran
  `File.Exists` on every remembered path *synchronously, inside `WM_ACTIVATE`*. A remembered path on
  a disconnected share / unmounted drive / spun-down disk makes that block for tens of seconds.
  Proven with a full dump taken mid-freeze (`dotnet-dump`): `GetFileAttributesEx` → `File.Exists` →
  `RecentFiles.Load` → `RebuildRecentMenu` → `Window.WmActivate`. The torn-off window was causal, not
  coincidental: with two windows, activation fires on every focus change between them instead of only
  on alt-tab into the app.
- **Fix:** the existence sweep moved to a background thread behind `RecentFiles.Existing` /
  `RecentFiles.Refresh()`, and `AppSettings.Reload()` gated behind a `FileSystemWatcher` dirty flag.
  See "Never touch the filesystem on the activation path" in `design.md` — that section is the rule
  to keep; regressions here will look like a mysterious app-wide hang, not like a file-I/O bug.
- **Repro (for regression testing):** put `\\192.0.2.1\share\x.txt` (TEST-NET-1, black-holed) at the
  head of `%LOCALAPPDATA%\NotepadRedo\recent.json`, open two windows, and alternate focus. Before the
  fix the first activation blocked ~42 s; after it, ~20 ms. Windows negative-caches the dead host
  afterwards, so re-testing needs a fresh unreachable host or a wait.
