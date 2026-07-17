using System.IO;
using System.Text.Json;

namespace TreeNotepad;

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
