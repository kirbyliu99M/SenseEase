using System;
using System.Collections.Generic;

namespace SenseEase.Infrastructure.Real;

/// <summary>
/// Beta-1 D-16 / theme-2: maps a foreground process name to a COARSE activity category
/// (browser / video / game / office / dev / comm / other). Pure + deterministic so it is
/// unit-testable. The category is the privacy-minimised join key for per-app OFI attribution
/// (D-16: stored on-device with the raw app name; export de-identifies to category-only, D-17).
/// Unknown processes fall through to "other" — never a crash, never a guess.
/// </summary>
public static class AppCategoryMapper
{
    public const string Browser = "browser";
    public const string Video   = "video";
    public const string Game    = "game";
    public const string Office  = "office";
    public const string Dev     = "dev";
    public const string Comm    = "comm";
    public const string Other   = "other";

    // Exact lowercased exe base-names (no extension) → category. Curated, additive: anything not
    // listed is "other" (so a new app is conservatively uncategorised, never mis-binned).
    private static readonly Dictionary<string, string> _exact = new(StringComparer.OrdinalIgnoreCase)
    {
        // browsers
        ["chrome"] = Browser, ["firefox"] = Browser, ["msedge"] = Browser, ["opera"] = Browser,
        ["brave"] = Browser, ["vivaldi"] = Browser, ["arc"] = Browser, ["iexplore"] = Browser,
        // video players
        ["vlc"] = Video, ["mpc-hc"] = Video, ["mpc-hc64"] = Video, ["mpc-be"] = Video,
        ["potplayer"] = Video, ["potplayermini64"] = Video, ["wmplayer"] = Video, ["mpv"] = Video,
        ["kmplayer"] = Video, ["smplayer"] = Video,
        // games / launchers
        ["steam"] = Game, ["steamwebhelper"] = Game, ["epicgameslauncher"] = Game,
        ["battle.net"] = Game, ["riotclientux"] = Game, ["leagueclient"] = Game,
        // office / productivity
        ["winword"] = Office, ["excel"] = Office, ["powerpnt"] = Office, ["outlook"] = Office,
        ["onenote"] = Office, ["acrobat"] = Office, ["acrord32"] = Office, ["notepad"] = Office,
        // dev / terminals
        ["devenv"] = Dev, ["code"] = Dev, ["rider64"] = Dev, ["idea64"] = Dev, ["pycharm64"] = Dev,
        ["sublime_text"] = Dev, ["windowsterminal"] = Dev, ["powershell"] = Dev, ["pwsh"] = Dev,
        ["cmd"] = Dev, ["conhost"] = Dev, ["wt"] = Dev,
        // comms / meetings (a VIMS-relevant class: shared screens, video tiles)
        ["teams"] = Comm, ["ms-teams"] = Comm, ["slack"] = Comm, ["discord"] = Comm,
        ["zoom"] = Comm, ["webexmta"] = Comm,
    };

    /// <summary>Categorise a process base-name (with or without ".exe"). Null/blank ⇒ "other".</summary>
    public static string Categorize(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return Other;
        string name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return _exact.TryGetValue(name, out string? cat) ? cat : Other;
    }
}
