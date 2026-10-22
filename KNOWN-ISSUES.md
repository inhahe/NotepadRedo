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

### Changes within one timestamp tick of the reconciled version go unseen
- **Where:** `EditorView` — `HandleExternalChange` and `CheckBeforeOverwrite` treat an equal stamp
  (modified time + size) as "nothing new" and don't read the file.
- **Consequence:** a same-length edit made elsewhere within one timestamp tick of the version the
  document last reconciled with is invisible: 2 s on FAT32, and on SMB servers that round times to
  a second. The next save overwrites it without a question. Same-length edits within that window
  are rare, and every editor that watches modified times shares the gap.
- **Proper fix (deferred):** keep a hash of the reconciled version and compare content as well, at
  least when a watcher event arrives with an unchanged stamp (an event is evidence of a write). The
  price is reading the whole file on each such event, our own saves included.

## Bugs

### The self-contained release read its code from the exe while it ran (fixed 2026-10-22, 1.0.32)
- **Was:** the GitHub release, run from a network share, had the exposure the framework-dependent
  build lost in 1.0.30. Its code stays in the bundle, mapped from the exe as it runs, and extracting
  it would cost every release user about 160 MB of `%TEMP%` per version. If the exe was replaced on
  the share while another computer ran it, after a dropped connection had closed that computer's
  handle, the instance read parts of the new build.
- **Fix:** started from a network or removable drive, it copies itself to `%TEMP%\NotepadRedo\run`
  and runs from there (`LocalCopy`); started anywhere else it costs nothing. The copy is told which
  exe the user started, and names that one in the `.txt` registration and as its windows' taskbar
  identity (`TaskbarIdentity`), so a pin still launches the original. Unused copies are swept
  (`OldBuilds`). See "A running instance reads nothing from its exe" in `design.md`.
- **Verified:** `tests/ExeIndependence/Run.ps1 -SelfContained`. On a self-contained build of the
  previous commit it fails all three of its checks: the instance ran from the share, mapped the
  bundle over SMB, and had no relaunch identity. On this one they pass: a 160 MB first copy took
  about 4 s over `\\localhost`, and a second launch reused it.
- **Hardened after review (Opus max, Fable xhigh):**
  - A self-contained instance used to sweep its *own* extraction folder 30 seconds in, since a
    library loaded with LoadLibrary doesn't hold its folder; checked, and fixed by holding a file
    there open.
  - Concurrent launches take turns copying.
  - A launch that only hands its files over doesn't copy.
  - A copy that won't start falls back to a fresh copy, then to the exe itself, detached.
  - Copies are not left read-only.
  - The newest copy survives other builds' sweeps.
  - Every window of a relocated instance, the merge viewer included, takes the original's
    identity.

  Each checked on a self-contained build: the folder kept 5 of 5 files, where the previous commit
  left 0; three simultaneous launches made one copy; the merge window had the original's ID and
  relaunch command.

### "Show a diff and merge…" did nothing in an instance run from a network drive (fixed 2026-10-22, 1.0.30)
- **Symptom:** in the "changed on disk" prompt, choosing "Show a diff and merge…" closed the prompt
  and nothing else happened. It worked in some instances and not in others.
- **Root cause:** the instance ran `D:\utils\NotepadRedo.exe` on a computer where `D:` is a share
  of the computer that builds it. It started on 1.0.28, and while it ran, the exe was overwritten
  with 1.0.29. A single-file build maps its IL straight out of the exe and reads each page when it
  is first needed. So the merge viewer's constructor, never run in that process before, was read
  from the new build at the old build's offsets: `BadImageFormatException: Bad IL range`, four
  times in `crash.log` from 2026-10-21 on. The UI-thread handler logs and swallows exceptions, so
  nothing visible happened. Instances started after the overwrite worked, hence "sometimes".
  - Evidence: the failing stacks name `OpenMerge(String)`, a 1.0.28 signature. That process held a
    *data* view of the exe on the share (the bundle). And Windows reported the 1.0.29-sized file as
    version 1.0.28, from the running process's stale image.
  - A share refuses to overwrite an exe while a client's handle on it is open (checked over
    `\\localhost`). The overwrite is possible only once a dropped connection (a sleep, a blip) has
    closed that handle. The client then reopens the file by name for its next page.
- **Fix:** nothing is read from the exe after startup. The app's assembly is extracted and loaded
  from `%TEMP%\.net`, and the exe is marked run-from-swap. Load failures that remain possible now
  produce a restart notice instead of silence. See "A running instance reads nothing from its exe"
  in `design.md`.
- **Also:** `build.bat` failed with a bare "Copy FAILED" whenever another computer was running the
  exe with its connection intact, because the share refuses that overwrite. It now moves the old
  exe aside (a rename is allowed), copies the new one in, and deletes the old copy, which the share
  allows as well. A failed copy puts the old exe back. Tested against an exe that an instance was
  running over `\\localhost`, and with the copy made to fail.
- **Verified:** by the files an instance maps. 1.0.29 run over SMB holds a data view of its exe;
  1.0.30 maps `NotepadRedo.dll` from `%TEMP%\.net` and has none. The merge viewer opens in 1.0.30
  run over SMB, framework-dependent and self-contained. The notice was shown for an injected `Bad IL
  range` in `OpenMerge`, where the old handler showed nothing. Not reproduced end to end: making a
  server close a client's handle needs admin rights. The reporter's own instance reproduced it live.
- **Regression test:** `tests/ExeIndependence/Run.ps1`. It fails all four of its checks on 1.0.29.
- **Still exposed:** any instance started before 1.0.30 (before 1.0.32 for the self-contained
  release), until it is restarted.

### An instance starting just as another sweeps can lose its extraction folder (open)
- **By design analysis** (2026-10-22, not observed): `OldBuilds` sweeps an extraction folder when
  it can rename it, which it can't while an instance holds a file there open. Every instance holds
  one from the first line of `OnStartup` (`HoldOwnExtraction`). Before that, from the moment the
  .NET host checks the folder exists (well under a second earlier), it holds nothing there. A
  framework-dependent instance whose folder is taken in that gap fails to start, and the next start
  extracts again. A self-contained one runs on, but any native library it hasn't loaded yet is gone:
  pen input (`PenImc_cor3.dll`), say.
- **When:** another instance of the same exe name, any version, runs its sweep (30 seconds after it
  starts) inside that gap.
- **Proper fix (deferred):** a lock that the host itself would take, which it doesn't.

### A framework-dependent instance loses unloaded assemblies when .NET is updated under it (open)
- **Symptom:** `crash.log`, 2026-10-14 09:20, at a Windows restart:
  `FileNotFoundException: System.Diagnostics.Tracing, Version=8.0.0.0`, raised from WPF's shutdown
  telemetry.
- **Root cause:** Windows Update installed .NET 8.0.32 at 00:18 that day and deleted 8.0.31, which
  the instance was running on. Files a process has loaded are locked and survive the deletion;
  assemblies it hasn't loaded yet are gone. Any command that first needs one then fails until the
  instance is restarted. Today only two of the app's references load lazily:
  `System.Security.Cryptography` (history sidecars, whose failures are caught and only lose the
  history) and `System.Drawing.Common` (the tray icon). WPF and the framework load more on their
  own, as the shutdown case shows.
- **Mitigation:** a load failure that reaches the UI-thread handler tells the user to restart,
  rather than leaving the command to do nothing (1.0.30). A tray icon that can't be made no longer
  throws out of `OnClosing`: WPF then finished closing the window without asking about unsaved
  work, and the app exited. The window is minimized to the taskbar instead, and the user is told to
  restart (1.0.31).
- **Proper fix (a trade-off, not taken):** loading the app's lazy references at startup would lock
  only those assemblies, not the ones they load in turn when used. A self-contained build can't be
  serviced under it, and since 1.0.32 it is safe on a network drive too (it runs from a local copy),
  so `build.bat` could publish self-contained. The price: a ~160 MB exe per build instead of
  ~380 KB, written to both deploy places, and on each computer that runs it over the network a
  160 MB copy per build (swept once unused).
- **Reproduce:** point `DOTNET_ROOT` at a private copy of the runtime, start an instance, delete
  `System.Drawing.Common.dll` from the copy, then close the window with "Minimize to tray" set. The
  window must end up minimized, with the restart notice on screen.

### An exception out of `MainWindow.OnClosing` closed the window without saving (fixed 2026-10-22, 1.0.32)
- **Found by review** (2026-10-22): WPF finishes closing a window whose `OnClosing` throws. If
  `PromptSaveEach`, a save it triggered, or the tear-down after it threw, the window closed without
  the save the user was being asked about. Recovery files survived only as of the last autosave, and
  with autosave off there were none: the work was gone.
- **Fix:** the close runs inside a `try`. A failure before the documents' teardown flushes every
  document's recovery snapshot, keeps the window open once and says so. A second close, or a forced
  quit, goes through, releasing the documents (watchers and file locks too) but keeping the
  snapshots for the next launch. A failure in the teardown, when every question has been answered,
  finishes the close. See "A failed close keeps the work" in `design.md`.
- **Verified** with a build whose save prompt throws (injected, not committed). With autosave off,
  dirty document, X: before, the window and process closed with no snapshot written. After, the
  window stayed open with the notice and one snapshot; a second X closed it and kept the snapshot.
  And with disposing a document throwing: 1.0.32 as first committed kept the window open with its
  documents half shut down; now it closes.

### A window whose constructor failed could keep the app alive invisibly (fixed 2026-10-22, 1.0.32)
- **Found by review** (2026-10-22): WPF's `Window` constructor registers the window
  in `Application.Windows` before the derived constructor runs. If `InitializeComponent`, or
  anything after it in a window's constructor, throws, the half-made window stays registered, never
  shown and never closed. The app shuts down when its last window closes (`OnLastWindowClose`), so
  once the user closes every real window, that one keeps the process alive with nothing on screen.
  The next launch then counts it as a running instance: it doesn't restore the session, and in tab
  mode hands its files to the invisible process. (The 2026-07-17 NullReferenceException entry
  below shows that early registration at work.)
- **When:** any exception in a window's constructor. A load failure in `DiffMergeWindow`'s
  `InitializeComponent` after a .NET update, or a failure while tearing a tab off into a new
  `MainWindow`. The reported `Bad IL range` did not do it: that one is raised while compiling the
  constructor, before any of it runs.
- **Fix:** the app ends when its last *shown* `MainWindow` closes (`ShutdownMode` is
  `OnExplicitShutdown`). A `MainWindow` whose constructor fails closes itself before rethrowing.
  Everything that looks across windows goes through `MainWindow.ShownWindows()`, which skips a
  never-shown one, and `OnClosing` and `OnClosed` do nothing for one.
  Before, an orphaned `MainWindow` could also be picked to host a file or sync its menus, and throw.
  See "The app lives as long as its editor windows" in `design.md`.
- **Verified** with a build whose merge window constructor throws after `InitializeComponent`
  (injected, not committed): merge, then close the main window. Before, the process kept running
  with no window. After, it ended.

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
- **Hardened after review (Opus and Fable):** other ways an outside write could still be overwritten
  without a question are closed as well. Keep mine covers only the version it was asked about. A
  stamp is settled only once its choice succeeded. The blind 1.5 s after each save is gone. Saving
  checks the file first (`ReadyToOverwrite`). Questions come one at a time. Each has a test that
  fails without it. See "The 'changed on disk' prompt" in `design.md`.

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
- **Hardened after review:** a watcher that can't be made at all (a document opened or recovered
  while its share is offline) now gets the same retries. A retry no longer installs a watcher whose
  first read failed while it was being started. Repeated quick failures keep backing off. That
  install race has no test: the failure it needs (a first read that can't be started) can't be
  produced on demand.

### Pre-rename data migration never ran (fixed 2026-10-20, 1.0.29)
- **By inspection:** the one-time move of `%LOCALAPPDATA%\TreeNotepad` to `NotepadRedo` sat in the
  body of `AppSettings`' static constructor, and is skipped when the new folder already exists.
  C# runs static field initialisers before that body, and one of them, the settings
  `FileSystemWatcher` (`CreateWatcher`), creates the new folder. So from the build that added that
  watcher on, the move never ran at all. Anyone whose first launch after the rename used such a
  build has the old folder stranded beside a new one.
- **Fix:** the move now lives in `AppPaths`, which every class asks for the folder, so it runs
  before anything can create it. Installs already stranded aren't rescued: the move still skips once
  `NotepadRedo` exists. None is on the development machine (checked 2026-10-20: no `TreeNotepad`
  folder). If one turns up elsewhere, merge its files in by hand, or teach `AppPaths` to merge.

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
