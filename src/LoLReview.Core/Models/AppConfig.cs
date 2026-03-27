#nullable enable

namespace LoLReview.Core.Models;

/// <summary>
/// Application configuration stored in %LOCALAPPDATA%\LoLReviewData\config.json.
/// </summary>
public class AppConfig
{
    public string GithubToken { get; set; } = "";
    public string AscentFolder { get; set; } = "";
    public Dictionary<string, string> Keybinds { get; set; } = new();
    public bool TiltFixMode { get; set; }
    public string ClipsFolder { get; set; } = "";
    public int ClipsMaxSizeMb { get; set; } = 2048;
    public bool BackupEnabled { get; set; }
    public string BackupFolder { get; set; } = "";

    /// <summary>
    /// Default keybind map — each action maps to a key-event string.
    /// Users can remap these in Settings.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultKeybinds =
        new Dictionary<string, string>
        {
            { "play_pause",    "space" },
            { "seek_fwd_5",    "Right" },
            { "seek_back_5",   "Left" },
            { "seek_fwd_2",    "Shift-Right" },
            { "seek_back_2",   "Shift-Left" },
            { "seek_fwd_10",   "Control-Right" },
            { "seek_back_10",  "Control-Left" },
            { "seek_fwd_1",    "Alt-Right" },
            { "seek_back_1",   "Alt-Left" },
            { "bookmark",      "b" },
            { "speed_up",      "bracketright" },
            { "speed_down",    "bracketleft" },
            { "clip_in",       "i" },
            { "clip_out",      "o" },
        };
}
