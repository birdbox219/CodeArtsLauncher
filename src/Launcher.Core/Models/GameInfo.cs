using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Launcher.Core.Models;

/// <summary>
/// One game in the launcher's library.
///
/// Fields split into three groups:
///   - Catalog: discovered from the itch.io profile page, refreshed on sync.
///   - Install: how this machine runs it. Persisted, never overwritten by a sync.
///   - Local state: what is currently on disk.
/// </summary>
public class GameInfo
{
    // ---- Catalog (from itch.io, refreshed on sync) ----

    /// <summary>Slug used for the install folder and config key, e.g. "logic-rift".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Numeric itch.io game id, stable across renames.</summary>
    public long ItchGameId { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Account that hosts the game, e.g. "birdbox774" or "falcon-eye".</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Display name of the credited author.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>True when hosted under another account, i.e. a collaboration.</summary>
    public bool IsCollaboration { get; set; }

    /// <summary>Public itch.io page, e.g. "https://birdbox774.itch.io/logic-rift".</summary>
    public string PageUrl { get; set; } = string.Empty;

    public string CoverImageUrl { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;

    /// <summary>Platforms itch lists for the page, e.g. ["windows"].</summary>
    public List<string> Platforms { get; set; } = new();

    /// <summary>
    /// True when the itch.io page offers a playable web build ("Play in browser").
    /// </summary>
    public bool HasWebBuild { get; set; }

    /// <summary>
    /// True once the game's own page has been read for its download list. Until then nothing is
    /// known about downloads, and the launcher must not claim a game is browser-only: itch only
    /// shows platform icons for uploads whose platform traits were ticked, so a plain zip upload
    /// looks identical to no upload at all on the profile grid.
    /// </summary>
    public bool UploadsChecked { get; set; }

    /// <summary>True when the itch.io page has at least one downloadable file.</summary>
    public bool HasItchDownload { get; set; }

    /// <summary>File name of the first itch.io upload, e.g. "LogicRift.zip". Empty if none.</summary>
    public string ItchDownloadName { get; set; } = string.Empty;

    /// <summary>Size of that upload as itch reports it, e.g. "44 MB". Empty if unknown.</summary>
    public string ItchDownloadSize { get; set; } = string.Empty;

    // ---- Install configuration (persisted, survives catalog sync) ----

    /// <summary>
    /// Wharf channel name only, e.g. "windows". Combined with <see cref="Owner"/> and
    /// <see cref="Id"/> to form a butler target. Empty until the game is pushed.
    /// </summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>Which content source to use: "wharf", "r2", or empty to auto-detect.</summary>
    public string PreferredSourceId { get; set; } = string.Empty;

    public string InstallDirectory { get; set; } = string.Empty;

    /// <summary>Relative path to the executable. Auto-detected after install when empty.</summary>
    public string ExecutableRelativePath { get; set; } = string.Empty;

    public string LaunchArguments { get; set; } = string.Empty;

    // ---- Local install state ----

    /// <summary>Build id currently on disk. Empty when not installed.</summary>
    public string InstalledBuildId { get; set; } = string.Empty;

    public string InstalledVersion { get; set; } = string.Empty;
    public long InstalledSizeBytes { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }

    // ---- Derived ----

    /// <summary>butler target, e.g. "birdbox774/logic-rift:windows". Empty without a channel.</summary>
    public string ButlerTarget =>
        string.IsNullOrWhiteSpace(Owner) || string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Channel)
            ? string.Empty
            : $"{Owner}/{Id}:{Channel}";

    /// <summary>True once a wharf channel is configured for this game.</summary>
    public bool HasChannel => !string.IsNullOrWhiteSpace(Channel);

    /// <summary>
    /// A web-only game has nothing to install or patch: no downloadable file anywhere, only an
    /// embedded web build. The only honest action is to open the page and play it there.
    ///
    /// Deliberately requires <see cref="UploadsChecked"/>. Judging this from the profile grid's
    /// platform icons was wrong: three of the six games have zip uploads with no platform traits
    /// ticked, so they showed no icons and were misreported as browser-only.
    /// </summary>
    public bool IsBrowserOnly => UploadsChecked && HasWebBuild && !HasItchDownload;

    /// <summary>
    /// True when there is something to download — either a tagged desktop platform on the profile
    /// grid, or an upload found on the game's own page.
    /// </summary>
    public bool IsDownloadable => Platforms.Count > 0 || HasItchDownload;


    public string FullExecutablePath =>
        string.IsNullOrWhiteSpace(InstallDirectory) || string.IsNullOrWhiteSpace(ExecutableRelativePath)
            ? string.Empty
            : Path.Combine(InstallDirectory, ExecutableRelativePath);

    /// <summary>
    /// Installed means we recorded a build and the directory has content. Deliberately not
    /// keyed on the exe alone: the exe path may not be known until after the first install.
    /// </summary>
    public bool IsInstalled =>
        !string.IsNullOrWhiteSpace(InstalledBuildId)
        && !string.IsNullOrWhiteSpace(InstallDirectory)
        && Directory.Exists(InstallDirectory);

    /// <summary>True when a launchable executable is present on disk.</summary>
    public bool HasPlayableExecutable =>
        !string.IsNullOrWhiteSpace(FullExecutablePath) && File.Exists(FullExecutablePath);

    /// <summary>
    /// Best-effort search for the game executable, used when the catalog does not name one.
    /// Prefers a name resembling the game, then the largest exe, skipping known non-game binaries.
    /// </summary>
    public string? DetectExecutable()
    {
        if (string.IsNullOrWhiteSpace(InstallDirectory) || !Directory.Exists(InstallDirectory))
            return null;

        string[] skip = { "unitycrashhandler", "crashpad", "vcredist", "dxsetup", "uninstall", "unins000" };

        var candidates = Directory
            .EnumerateFiles(InstallDirectory, "*.exe", SearchOption.AllDirectories)
            .Where(p => !skip.Any(s => Path.GetFileNameWithoutExtension(p).ToLowerInvariant().Contains(s)))
            .Select(p => new FileInfo(p))
            .ToList();

        if (candidates.Count == 0) return null;

        string normalizedTitle = new string(Title.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        var byName = candidates.FirstOrDefault(f =>
        {
            string n = new string(Path.GetFileNameWithoutExtension(f.Name).Where(char.IsLetterOrDigit).ToArray())
                .ToLowerInvariant();
            return n.Length > 0 && normalizedTitle.Length > 0
                   && (normalizedTitle.Contains(n) || n.Contains(normalizedTitle));
        });

        var chosen = byName ?? candidates.OrderByDescending(f => f.Length).First();
        return Path.GetRelativePath(InstallDirectory, chosen.FullName);
    }
}
