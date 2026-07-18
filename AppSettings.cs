using System.IO;
using System.Text.Json;

namespace TreeNotepad;

/// <summary>What pressing the window's X (close) button should do.</summary>
public enum CloseButtonBehavior
{
    /// <summary>Close the window (prompting to save unsaved work) — the normal behaviour.</summary>
    Close,
    /// <summary>Hide to the notification area (system tray); keep running in the background.</summary>
    MinimizeToTray,
    /// <summary>Minimise to the taskbar instead of closing.</summary>
    MinimizeToTaskbar,
}

/// <summary>
/// Process-wide, persisted user preferences. Stored as JSON under LocalAppData so the
/// choice survives restarts and is shared by every window/instance.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Autosave (crash-recovery) period in seconds; 0 disables it.</summary>
    public int AutosaveSeconds { get; set; } = 30;

    /// <summary>When true, New/Open spawn a separate process; when false they add a tab.</summary>
    public bool OpenInNewInstance { get; set; }

    public bool WordWrap { get; set; } = true;
    public bool ShowTree { get; set; } = true;

    /// <summary>
    /// When true, history-tree previews show as much text as fits on one line (then a trailing
    /// ellipsis) instead of a fixed character count. The "Preview chars" slider is ignored.
    /// </summary>
    public bool PreviewFitToWidth { get; set; }

    /// <summary>What the window's X (close) button does.</summary>
    public CloseButtonBehavior CloseButton { get; set; } = CloseButtonBehavior.Close;

    // ----- Editor font (applied to the text area of every document) -----

    /// <summary>Editor font family name.</summary>
    public string FontFamily { get; set; } = "Consolas";

    /// <summary>Editor font size, in points (as shown in the font picker). Converted to WPF
    /// device-independent units when applied to the editor.</summary>
    public double FontSize { get; set; } = 11;

    public bool FontBold { get; set; }
    public bool FontItalic { get; set; }

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TreeNotepad");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
}
