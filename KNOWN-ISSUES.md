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
