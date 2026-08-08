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
- **`EditorView`** — one document: text box, undo tree, search pane, autosave, external-change
  watching. Raises `StatusChanged` / `TitleChanged` / `SearchVisibilityChanged`, which the shell
  reflects into its chrome.
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

## Persistence (all under `%LOCALAPPDATA%\NotepadRedo`)

| File | Owner | Contents |
|---|---|---|
| `settings.json` | `AppSettings` | All options. `Reload()` re-reads on window activation so instances stay in sync — **any new setting must be added to `Reload()` as well as `Save()`**. |
| `session.json` | `SessionStore` | Paths of open *saved* files, for session restore. |
| `recent.json` | `RecentFiles` | Up to 15 most-recently-opened paths for File → Open Recent. Re-read before each write so concurrent instances merge instead of clobbering; raises `Changed` so open windows rebuild their submenu. |
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

## Dialogs

`ThemedDialog` replaces `MessageBox` so prompts follow the palette. All three entry points —
`Show()` (Y/N/O/C), `ShowSaveAll()` (S/A/D/C) and `ShowChoices()` (1‑9) — answer to bare keys, map
Escape to the natural cancel, and call `Activate()` plus focus the default button on `Loaded`. That
last part is not cosmetic: a launch from a console leaves cmd owning the foreground, so without it
the prompt can come up behind and look like a silent hang.

## Versioning

`VERSION` at the project root is the single source of truth; the csproj reads it into `<Version>`
and `release-github.bat` tags releases from it. Bump it on every build.
