using System;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Core.Models;

namespace Launcher.Core.Abstractions;

/// <summary>
/// A place game content can be installed from and incrementally updated against.
///
/// Two implementations are planned:
///   - WharfContentSource: itch.io channels via butler/butlerd. Delta patches come free,
///     but only once a game has actually been pushed with `butler push`.
///   - R2ChunkContentSource: self-hosted content-defined chunks on Cloudflare R2. Needs no
///     itch.io credentials, so it is the player-facing path.
/// </summary>
public interface IContentSource : IAsyncDisposable
{
    /// <summary>Stable id used in config and logs, e.g. "wharf" or "r2".</summary>
    string Id { get; }

    /// <summary>Human readable name for the UI.</summary>
    string DisplayName { get; }

    /// <summary>True when this source has usable content for the game and is configured.</summary>
    Task<bool> CanServeAsync(GameInfo game, CancellationToken ct = default);

    /// <summary>
    /// What the source currently offers. Null when the source cannot see the game at all
    /// (no channel pushed, no manifest uploaded, no credentials).
    /// </summary>
    Task<RemoteVersionInfo?> GetRemoteVersionAsync(GameInfo game, CancellationToken ct = default);

    /// <summary>
    /// Install or update in place. Implementations must apply a delta when one is available
    /// and fall back to a full download otherwise, reporting which happened via progress.
    /// </summary>
    Task<InstallResult> InstallOrUpdateAsync(
        GameInfo game,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default
    );

    /// <summary>Checksum the local install against the source and repair damage.</summary>
    Task<VerifyResult> VerifyAndRepairAsync(
        GameInfo game,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default
    );
}
