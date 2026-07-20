# NotepadRedo

A fast, lightweight Notepad-style text editor for Windows with one standout feature: a **branching undo/redo history tree**. Instead of a linear undo stack where redoing down a new path throws away everything you'd undone, NotepadRedo keeps *every* state you've ever visited as a node in a tree — so you can freely explore alternative edits and jump back to any earlier version, on any branch, at any time.

Built with WPF on .NET 8.

---

## Features

### Branching history tree
- Every edit becomes a node in a visual history tree shown in a side pane.
- Undo/redo walks the tree; making a new edit after undoing starts a **new branch** instead of discarding the future you undid.
- Click any node to instantly jump the document to that state.
- **Condensed by default**: to keep a long typing session from burying the pane in one row per keystroke, the tree shows only the *interesting* nodes — **branch points, tips, and your current position** — collapsing each straight run of edits into a single row. Undo/redo stay fully granular (every keystroke group is still its own step); only the *display* condenses. An in-pane **Show all edits** toggle (top-right of the history pane) reveals every edit as its own row; the choice is shared across all windows and persisted.
- The current node is highlighted; each node shows a text preview and metadata.
- Preview text can either show a **fixed number of characters** (adjustable with a slider) or **fit to the pane width** with a trailing ellipsis (toggleable).
- The history pane is resizable (drag the divider) and can be hidden entirely.
- **Configurable undo grouping** (Options → Undo grouping) controls how typing is chunked into history nodes: start a new step on each **line** (Enter), each **paste**, or each **character**, and/or after a configurable **typing pause** (1/2/4/8 seconds or a custom value).
- **Persistent history** (Options → *Remember edit history between sessions*, off by default): when on, a file's entire branching history is written to a sidecar under `%LOCALAPPDATA%` when the file is saved (and on clean close), and restored the next time you open the file — so undo/redo and every branch survive restarts. The history is only restored when the file's on-disk contents still match what the history was anchored to; if the file was changed by something else in the meantime, the stale history is ignored and a fresh root is started. Off by default because it persists document content to `%LOCALAPPDATA%`. Old sidecars are pruned after 90 days.

### Tabs
- Multiple documents open as tabs in a single window.
- **Tear off** a tab by dragging its header out of the window to create a new window.
- **Reattach / reorder** tabs by dragging headers between and within windows.
- **Middle-click** a tab header to close it.

### Multiple windows & instances
- Cross-instance IPC over a named pipe coordinates all running copies.
- Opening a file that's already open just focuses the existing tab/window.
- Configurable: new files open in **a new tab** (of the existing instance) or **a new instance** (separate process).

### Search
- Open the search pane with `Ctrl+F` (or **Edit → Find…**, or the toolbar **Search** toggle button); it slides in on the right. The toolbar button stays lit while the pane is open and toggles it closed again.
- **Literal matching**: the search matches **exactly what you type** — every character, including spaces and quotes, is searched verbatim, with no special syntax. Typing `"blah"` finds the text `"blah"` (quote marks and all); typing ` os ` (with surrounding spaces) finds a standalone `os` rather than the `os` inside `composition`.
- **Case-sensitivity** toggle.
- **Match whole word only** toggle: restricts matches to places where the term stands alone as a word (bounded by non-word characters), so searching `os` won't match inside `composition`. Applies to plain and proximity searches alike.
- **Proximity mode** (the **Only where all items are near each other** checkbox): switches the search box into a multi-item list. Type an item and press **Enter** to add it; each added item appears in a list you can **edit in place**, or **remove** with its × button (or by pressing **Delete** while the item's text is highlighted). **Tab** moves from the add box through each item in turn. Results are only the places where *all* the items occur **within N characters, words, or lines of each other** (N and the unit are configurable). Because items are entered discretely, no quoting or escaping is ever needed — an item can contain spaces and still be one item. Since proximity items are usually whole words, entering proximity mode **turns on "Match whole word only" by default** (so `op` and `po` won't both match inside `opposite`) — uncheck it for substring matching; your previous whole-word setting is restored when you leave proximity mode.
- Results are listed with a one/two-line preview ending in an ellipsis when truncated; **clicking a result moves the caret and selection to that match** and scrolls it into view. `Enter` in the search box steps through matches.

### Autosave, crash recovery & session restore
- Periodic background autosave (configurable interval, or off) parks in-progress work so an unexpected crash or forced quit doesn't lose unsaved changes.
- Unsaved/recovered work is offered for restoration on the next launch.
- **Session restore**: the set of open files is remembered between runs, so relaunching can reopen the same tabs where you left off. The behaviour is configurable (Options → *Reopen last session's files at startup*): **Ask me first** (the default — lists the files and prompts, so a stale session can't silently clobber edits you made elsewhere), **Always reopen**, or **Never reopen**. (Files opened from the command line, or `--new`, start fresh instead of restoring.)
- All unhandled exceptions are logged with full stack traces; UI-thread glitches are caught and swallowed to keep your documents alive rather than crashing.

### External-change detection & diff/merge
- When enabled (Options → *Watch for changes made by other programs*, on by default), NotepadRedo watches every open file and notices when another program modifies it on disk. It then asks what to do:
  - **Reload from disk** (dropping your unsaved edits),
  - **Keep my version** (ignore the change; your next save overwrites it),
  - **Save my version to another file**, then reload the disk version,
  - **Save the disk version to another file**, then keep yours, or
  - **Show a diff and merge…** — open a side-by-side merge viewer.
- The **merge viewer** shows the two versions with changed/added/removed lines tinted and the differing text painted red. The intra-line diff is refined to the **character level** (UltraCompare-style): shared words stay anchored on a word/whitespace alignment, but within a changed run only the differing *characters* are reddened — so `composition` → `compositions` highlights just the trailing "s" rather than the whole word. You pick which side to **keep** — it's outlined and freely editable (copy/paste enabled) — and pull individual red lines across from the other side by **double-clicking** them (or via the right-click menu, which can also replace/insert/remove a line). You can flip which side is kept at any time, re-diff after hand-editing, then save the assembled result back to the file. **Save kept side** writes the kept side to the file and discards the other; **Save both** also writes the kept side to the file but preserves the other side alongside it as a timestamped sibling file (so nothing is lost).
- If the file changes on disk **again** while you're merging, a banner appears and you can fold the new version into the viewer, ignore it, stash it to a file, or save both versions off and bail out.
- **Lock open files** (Options → *Lock open files from outside changes*, off by default): while a file is open, hold it with a deny-write lock so other programs can read it but can't modify or delete it. Saving writes through the held handle.

### Close-button behavior
Choose what the window's **X** button does:
- **Close** the window (prompting to save unsaved work) — the default. With several unsaved tabs you're asked about each in turn, and a **Save All** button on each prompt saves the rest without further asking.
- **Minimize to tray** — hide to the notification area and keep running.
- **Minimize to taskbar** — minimize instead of closing.

### Font & formatting
- **Format** menu with a **Font…** picker (family, size, and style in one dialog) that **previews live in the editor as you browse** — the text updates instantly as you change family/size/bold/italic, and reverts if you cancel.
- Quick toggles for **Bold** (`Ctrl+B`) and **Italic** (`Ctrl+I`).
- A **Size** submenu for common point sizes.
- The chosen font is a shared, persisted preference applied to the editor in every tab and window.

### Other
- Word wrap toggle.
- Standard editing: cut / copy / paste.
- **`.txt` file association**: on launch NotepadRedo registers itself (per-user, no admin) as a program *capable* of opening `.txt` files, so it appears in **Open with** with an **"Always use this app"** option and in **Settings → Default apps**. It never hijacks the association — it only makes itself selectable, so you can set it as your default text editor if you want. The optional `associate-txt.bat` helper *asks first*, then registers and opens Default Apps to help you finish (Windows guards the final `.txt` default with a hashed key, so the last click is always yours).

---

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+N` | New document |
| `Ctrl+O` | Open… |
| `Ctrl+S` | Save |
| `Ctrl+Shift+S` | Save As… |
| `Ctrl+W` / `Ctrl+F4` | Close current tab (prompts to save if there are unsaved changes) |
| `Ctrl+F` | Find… (open the search pane) |
| `Ctrl+Z` | Undo (walk up the history tree) |
| `Ctrl+Y` / `Ctrl+Shift+Z` | Redo (walk down the history tree) |
| `Ctrl+X` / `Ctrl+C` / `Ctrl+V` | Cut / copy / paste |
| `Ctrl+B` | Toggle bold |
| `Ctrl+I` | Toggle italic |

---

## Command-line usage

```
NotepadRedo.exe [files...] [--new] [--quit-prompt] [--quit] [--quit-save]
```

- `files...` — open one or more files. Files already open elsewhere are focused rather than reopened; otherwise they open as a new tab or new instance per your settings.
- `--new` — start with a blank document even if files are passed.
- `--quit-prompt` — signal every running instance to close **interactively**: each prompts to save its unsaved work (Save / Save All / Don't Save / Cancel). With multiple unsaved documents you're asked about each one in turn; **Save All** saves the current document and every remaining one without further prompts. This call **blocks** until the user has answered every prompt and each instance has exited, and reports exit code `2` if the user cancels (leaving an instance open). Used by `build.bat` before redeploying, so a redeploy can't overwrite the exe until you've decided the fate of your unsaved work.
- `--quit-save` — signal every running instance to save silently (titled docs to disk, untitled parked in recovery) and exit, then exit. Non-interactive alternative to `--quit-prompt`.
- `--quit` — signal every running instance to park all work in crash recovery and exit.

Launching from a console (`cmd.exe`, a batch file, a script) returns the prompt **immediately** — NotepadRedo detaches itself so the console isn't held open until you close the editor. (This matters because `cmd` waits for any program it starts to exit, GUI apps included; NotepadRedo works around that by relaunching itself detached and letting the original process exit at once.) The `--quit*` signalling modes are exempt, since callers like `build.bat` need to block on their result.

---

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) on Windows.

```sh
dotnet build NotepadRedo.csproj -c Release
```

Or produce a single-file `win-x64` executable and deploy it via the helper script:

```sh
build.bat
```

`build.bat` publishes a self-contained single-file `NotepadRedo.exe`, then signals every running instance to close (prompting you to save each unsaved document) and **waits** until they have all exited before copying the new build into place. If you cancel a save prompt the redeploy aborts, leaving that instance untouched.

### Publishing a GitHub release

The app version is stored in one place — the **`VERSION`** file at the project root — which the csproj reads into `<Version>` (so it's stamped into the exe). Bump `VERSION` when you build, then:

```sh
release-github.bat
```

`release-github.bat` (no argument) reads the current version from `VERSION`, and — if a release for it doesn't already exist — builds a fresh self-contained `NotepadRedo.exe` into a throwaway `release\` folder (kept separate from the deployed copies so it never collides with a running instance's file lock) and publishes it as a GitHub release, with the exe and `associate-txt.bat` attached, via the [GitHub CLI](https://cli.github.com/) (`gh`, which must be installed and authenticated). It **aborts if that version is already released** (so it only publishes after `VERSION` has been bumped). It builds from your working tree and does not push source.

---

## Where settings and data live

Everything is stored under `%LOCALAPPDATA%\NotepadRedo\`:

| File | Purpose |
|---|---|
| `settings.json` | Persisted preferences (autosave interval, word wrap, tree visibility, preview mode, history condensing (branches-only), editor font, undo grouping, open-in behavior, close-button behavior, session-restore mode, external-change watching, file locking, persistent history). |
| `crash.log` | Timestamped exception log with full stack traces. |
| `session.json` | The set of files open at last exit, reopened on next launch (session restore). |
| recovery files | Autosaved copies of in-progress documents, restored on next launch. |
| `history\` | Per-file branching-history sidecars (when *Remember edit history between sessions* is on), keyed by a hash of the file path and anchored to a content stamp; pruned after 90 days. |

On first launch, if a `%LOCALAPPDATA%\TreeNotepad\` folder exists (from before the rename), it is automatically moved to `NotepadRedo\` so all settings and recovery data carry over.

---

## Project layout

| File | Role |
|---|---|
| `App.xaml(.cs)` | Application entry point, single-instance startup, command-line handling, global exception logging. |
| `MainWindow.xaml(.cs)` | Shell window: tab control, menu, toolbar, status bar, tab tear-off/reattach, keyboard shortcuts, tray/close behavior. |
| `EditorView.xaml(.cs)` | A single document: text editor, history-tree pane, splitter, autosave, external-change watching and file locking. |
| `UndoTree.cs` | The branching undo/redo model (`UndoTree` / `UndoNode`). |
| `Ipc.cs` | Named-pipe IPC for cross-instance coordination. |
| `SearchEngine.cs` | Pure, testable text-search logic (plain find + proximity clustering). |
| `DiffEngine.cs` | Pure, testable line + inline diff logic used by the merge viewer. |
| `DiffMergeWindow.xaml(.cs)` | Side-by-side diff/merge viewer for reconciling external changes. |
| `SessionStore.cs` | Reads/writes `session.json` for reopening last session's files. |
| `HistoryStore.cs` | Reads/writes the per-file branching-history sidecars (persistent history). |
| `AppSettings.cs` | Persisted user preferences. |
| `ThemedDialog.cs` | Themed replacements for `MessageBox` (incl. the multi-choice resolution prompts). |
| `Converters.cs` | XAML value converters (e.g. preview width). |
| `CrashLog.cs` | Best-effort exception logging. |

---

## License

See `LICENSE` if present; otherwise all rights reserved by the author.
