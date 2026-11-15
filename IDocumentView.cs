using System.ComponentModel;

namespace NotepadRedo;

/// <summary>
/// One open document, as the shell (<see cref="MainWindow"/>) sees it: what a tab holds. Two kinds
/// implement it: <see cref="EditorView"/>, the full editor, and <see cref="LargeFileView"/>, which
/// opens files too large for it (see "Large files" in design.md). Both are controls; the tab's
/// content is the view itself.
/// </summary>
public interface IDocumentView : INotifyPropertyChanged
{
    string? FilePath { get; }
    bool IsDirty { get; }
    /// <summary>Full path (or "Untitled") plus " *" when there are unsaved changes; bound by the tab.</summary>
    string TabTitle { get; }
    string RecoveryId { get; }

    string CaretText { get; }
    string CountText { get; }
    string NodeText { get; }
    string LineEndingText { get; }
    string LineEndingTooltip { get; }
    string SaveText { get; }
    LineEndingStyle LineEnding { get; }
    void SetLineEnding(LineEndingStyle style);

    event EventHandler? StatusChanged;
    event EventHandler? TitleChanged;
    event EventHandler? SidePaneVisibilityChanged;

    void FocusEditor();
    void Undo();
    void Redo();
    void OpenSearch();
    void ToggleSearch();
    bool IsSearchOpen { get; }
    void FindNext(bool backwards = false, bool keepFocus = false);
    void OpenReplace();
    void ToggleReplace();
    bool IsReplaceOpen { get; }

    bool Save(bool saveAs, bool interactive = true);
    bool ConfirmDiscardIfDirty();
    EditorView.SavePromptResult ConfirmDiscardForQuit();

    void ApplyWordWrap(bool wrap);
    void ApplyFont(string family, double sizePoints, bool bold, bool italic);
    void ApplyTreeVisible(bool show);
    void ApplyPreviewFit(bool fit);
    void ApplyHistoryMode(bool branchesOnly);
    void ApplyAutosaveInterval(int seconds);
    void LayOutPanes();
    void ApplyWatchSetting();
    void ApplyLockSetting();

    /// <summary>Write the crash-recovery snapshot now, if the document has unsaved work.</summary>
    void FlushRecovery();
    /// <summary>A real close: stop everything and delete the recovery snapshot.</summary>
    void Dispose();
    /// <summary>Stop timers, watching and the file lock, but keep the recovery snapshot.</summary>
    void Release();

    /// <summary>The document as the payload of a tab dragged to another process, or null when it
    /// can't move there (the drop then does nothing).</summary>
    string? SerializeForTransfer();
}
