using Launcher.Core.Enums;

namespace Launcher.Core.Models;

/// <summary>
/// The result of an update check: what the launcher should offer to do, plus the remote build it
/// compared against.
///
/// The previous engine returned a bare <see cref="LauncherState"/> and hardcoded
/// "if installed, assume ready to play", which made <see cref="LauncherState.UpdateAvailable"/>
/// unreachable — the launcher could never tell you an update existed. Carrying the remote build
/// alongside the state means the UI can also show the patch size, which is the whole point of
/// wharf.
/// </summary>
/// <param name="State">What action is available now.</param>
/// <param name="Remote">The newest remote build, when a source could report one.</param>
/// <param name="SourceId">Which content source answered.</param>
/// <param name="Message">Human-readable detail, especially when something is unavailable.</param>
public record GameStatus(
    LauncherState State,
    RemoteVersionInfo? Remote = null,
    string SourceId = "",
    string? Message = null
)
{
    public bool HasUpdate => State == LauncherState.UpdateAvailable;

    /// <summary>Bytes the next install/update will move: the patch when there is one.</summary>
    public long DownloadSize => Remote is null
        ? 0
        : Remote.PatchBytes > 0 ? Remote.PatchBytes : Remote.TotalBytes;

    public static GameStatus NotInstalled(string? message = null) =>
        new(LauncherState.NotInstalled, Message: message);

    public static GameStatus Error(string message) =>
        new(LauncherState.Error, Message: message);
}
