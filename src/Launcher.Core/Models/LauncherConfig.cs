using System.Collections.Generic;

namespace Launcher.Core.Models;

public class LauncherConfig
{
    /// <summary>
    /// itch.io profile name whose games make up the library. This is the itch.io account name,
    /// not the Windows user name — the previous build defaulted to the machine account and so
    /// queried a profile that did not exist.
    /// </summary>
    public string ItchProfileUsername { get; set; } = "birdbox774";

    /// <summary>Root under which each game gets its own folder.</summary>
    public string BaseInstallDirectory { get; set; } = string.Empty;

    /// <summary>Default wharf channel to try when a game has none configured.</summary>
    public string DefaultChannel { get; set; } = "windows";

    public bool AutoCheckUpdates { get; set; } = true;
    public bool CloseOnLaunch { get; set; } = false;

    /// <summary>0 = unlimited.</summary>
    public int MaxBandwidthKbps { get; set; } = 0;

    /// <summary>Applied to every game on top of its own arguments.</summary>
    public string GlobalLaunchArguments { get; set; } = string.Empty;

    /// <summary>
    /// Per-game install settings, keyed by slug. Kept separate from the catalog so a sync
    /// refreshes titles and art without clobbering channel and executable choices.
    /// </summary>
    public Dictionary<string, GameOverride> GameOverrides { get; set; } = new();

    /// <summary>Games added by hand, for drafts and unlisted titles the profile page omits.</summary>
    public List<GameInfo> ManualGames { get; set; } = new();
}

/// <summary>Install settings for one game that survive a catalog sync.</summary>
public class GameOverride
{
    public string Channel { get; set; } = string.Empty;
    public string ExecutableRelativePath { get; set; } = string.Empty;
    public string LaunchArguments { get; set; } = string.Empty;
    public string InstallDirectory { get; set; } = string.Empty;
    public string PreferredSourceId { get; set; } = string.Empty;

    // Install state, persisted so update checks survive a restart.
    public string InstalledBuildId { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    public long InstalledSizeBytes { get; set; }
    public System.DateTime? LastPlayedUtc { get; set; }
    public System.DateTime? LastUpdatedUtc { get; set; }
}
