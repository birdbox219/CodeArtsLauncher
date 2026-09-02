using System;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Core.Models;

namespace Launcher.Core.Abstractions;

/// <summary>
/// Orchestrates install, update, verify and launch across the available content sources.
/// The engine itself knows nothing about itch.io or butler — it picks a source per game via
/// <see cref="IContentSource"/>, which is what lets the R2 chunk CDN drop in later without
/// touching the launcher.
/// </summary>
public interface IGameInstallEngine : IAsyncDisposable
{
    bool IsBusy { get; }

    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Compares the installed build against the newest remote one and reports what can be done.
    /// Returns the remote build too, so callers can show the patch size.
    /// </summary>
    Task<GameStatus> CheckStateAsync(GameInfo game, CancellationToken ct = default);

    /// <summary>
    /// Installs or updates in place, preferring a delta when the source has one. Returns what
    /// actually happened so the caller can persist the new build id and report bytes saved.
    /// </summary>
    Task<InstallResult> InstallOrUpdateAsync(
        GameInfo game,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default
    );

    Task<VerifyResult> VerifyAndRepairAsync(
        GameInfo game,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default
    );

    Task<bool> LaunchGameAsync(
        GameInfo game,
        string? additionalArgs = null,
        CancellationToken ct = default
    );

    /// <summary>Raised with the game id when a launched game exits, so Play can be re-enabled.</summary>
    event Action<string>? GameExited;

    Task CancelOperationAsync();
}
