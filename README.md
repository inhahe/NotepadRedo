# NotepadRedo

A fast, lightweight Notepad-style text editor for Windows with one standout feature: a **branching undo/redo history tree**. Instead of a linear undo stack where redoing down a new path throws away everything you'd undone, NotepadRedo keeps *every* state you've ever visited as a node in a tree — so you can freely explore alternative edits and jump back to any earlier version, on any branch, at any time.

Built with WPF on .NET 8.

---

## Features

### Branching history tree
- Every edit becomes a node in a visual history tree shown in a side pane.
- Undo/redo walks the tree; making a new edit after undoing starts a **new branch** instead of discarding the future you undid.
- Click any node to instantly jump the document to that state.
- The current node is highlighted; each node shows a text preview and metadata.
- Preview text can either show a **fixed number of characters** (adjustable with a slider) or **fit to the pane width** with a trailing ellipsis (toggleable).
- The history pane is resizable (drag the divider) and can be hidden entirely.
- **Configurable undo grouping** (Options → Undo grouping) controls how typing is chunked into history nodes: start a new step on each **line** (Enter), each **paste**, or each **character**, and/or after a configurable **typing pause** (1/2/4/8 seconds or a custom value).

### Tabs
- Multiple documents open as tabs in a single window.
- **Tear off** a tab by dragging its header out of the window to create a new window.
- **Reattach / reorder** tabs by dragging headers between and within windows.
- **Middle-click** a tab header to close it.

### Multiple windows & instances
- Cross-instance IPC over a named pipe coordinates all running copies.
- Opening a file that's already open just focuses the existing tab/window.
- Configurable: new files open in **a new tab** (of the existing instance) or **a new instance** (separate process).

### Autosave & crash recovery
- Periodic background autosave (configurable interval, or off) parks in-progress work so an unexpected crash or forced quit doesn't lose unsaved changes.
- Recovered work is restored on next launch.
- All unhandled exceptions are logged with full stack traces; UI-thread glitches are caught and swallowed to keep your documents alive rather than crashing.

### Close-button behavior
Choose what the window's **X** button does:
- **Close** the window (prompting to save unsaved work) — the default.
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

---

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+N` | New document |
| `Ctrl+O` | Open… |
| `Ctrl+S` | Save |
| `Ctrl+Shift+S` | Save As… |
| `Ctrl+W` / `Ctrl+F4` | Close current tab (prompts to save if there are unsaved changes) |
| `Ctrl+Z` | Undo (walk up the history tree) |
| `Ctrl+Y` / `Ctrl+Shift+Z` | Redo (walk down the history tree) |
| `Ctrl+X` / `Ctrl+C` / `Ctrl+V` | Cut / copy / paste |
| `Ctrl+B` | Toggle bold |
| `Ctrl+I` | Toggle italic |

---

## Command-line usage

```
NotepadRedo.exe [files...] [--new] [--quit] [--quit-save]
```

- `files...` — open one or more files. Files already open elsewhere are focused rather than reopened; otherwise they open as a new tab or new instance per your settings.
- `--new` — start with a blank document even if files are passed.
- `--quit-save` — signal every running instance to save (titled docs to disk, untitled parked in recovery) and exit, then exit. Used by `build.bat` before redeploying.
- `--quit` — signal every running instance to park all work in crash recovery and exit.

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

`build.bat` publishes a self-contained single-file `NotepadRedo.exe`, signals any running instance to save and exit first, then copies the new build into place.

---

## Where settings and data live

Everything is stored under `%LOCALAPPDATA%\NotepadRedo\`:

| File | Purpose |
|---|---|
| `settings.json` | Persisted preferences (autosave interval, word wrap, tree visibility, preview mode, editor font, undo grouping, open-in behavior, close-button behavior). |
| `crash.log` | Timestamped exception log with full stack traces. |
| recovery files | Autosaved copies of in-progress documents, restored on next launch. |

On first launch, if a `%LOCALAPPDATA%\TreeNotepad\` folder exists (from before the rename), it is automatically moved to `NotepadRedo\` so all settings and recovery data carry over.

---

## Project layout

| File | Role |
|---|---|
| `App.xaml(.cs)` | Application entry point, single-instance startup, command-line handling, global exception logging. |
| `MainWindow.xaml(.cs)` | Shell window: tab control, menu, toolbar, status bar, tab tear-off/reattach, keyboard shortcuts, tray/close behavior. |
| `EditorView.xaml(.cs)` | A single document: text editor, history-tree pane, splitter, autosave. |
| `UndoTree.cs` | The branching undo/redo model (`UndoTree` / `UndoNode`). |
| `Ipc.cs` | Named-pipe IPC for cross-instance coordination. |
| `AppSettings.cs` | Persisted user preferences. |
| `Converters.cs` | XAML value converters (e.g. preview width). |
| `CrashLog.cs` | Best-effort exception logging. |

---

## License

See `LICENSE` if present; otherwise all rights reserved by the author.
