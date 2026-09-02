using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Core.Abstractions;
using Launcher.Core.Enums;
using Launcher.Core.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Core.Services;

/// <summary>
/// The install engine, built on <see cref="IContentSource"/>.
///
/// Sources are tried in registration order, filtered by <see cref="IContentSource.CanServeAsync"/>
/// and by a game's <see cref="GameInfo.PreferredSourceId"/>. Today that means wharf; when the
/// self-hosted R2 chunk source lands it registers alongside and players stop needing itch
/// credentials, with no change here or in the UI.
/// </summary>
public class ContentSourceGameEngine : IGameInstallEngine
{
    private readonly IReadOnlyList<IContentSource> _sources;
    private readonly ILogger<ContentSourceGameEngine>? _logger;
    private readonly Dictionary<string, Process> _running = new();

    private CancellationTokenSource? _operationCts;
    private int _busy;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public ContentSourceGameEngine(
        IEnumerable<IContentSource> sources,
        ILogger<ContentSourceGameEngine>? logger = null)
    {
        _sources = sources.ToList();
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "Install engine ready with {Count} content source(s): {Sources}",
            _sources.Count,
            _sources.Count == 0 ? "none" : string.Join(", ", _sources.Select(s => s.DisplayName)));
        return Task.CompletedTask;
    }

    // ---------------- state ----------------

    public async Task<GameStatus> CheckStateAsync(GameInfo game, CancellationToken ct = default)
    {
        if (_running.TryGetValue(game.Id, out var proc) && !proc.HasExited)
            return new GameStatus(LauncherState.GameRunning);

        var source = await ResolveSourceAsync(game, ct);
        if (source is null)
        {
            return game.IsInstalled
                ? new GameStatus(LauncherState.ReadyToPlay, null, "",
                    "Installed, but no content source can check for updates.")
                : GameStatus.NotInstalled(
                    $"'{game.Title}' cannot be installed yet. It needs a wharf channel — " +
                    $"push a build with: butler push <folder> {game.Owner}/{game.Id}:windows");
        }

        RemoteVersionInfo? remote;
        try
        {
            remote = await source.GetRemoteVersionAsync(game, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Update check failed for {Game}.", game.Id);
            return game.IsInstalled
                ? new GameStatus(LauncherState.ReadyToPlay, null, source.Id,
                    $"Could not check for updates: {ex.Message}")
                : GameStatus.Error($"Could not check {game.Title}: {ex.Message}");
        }

        if (remote is null)
        {
            // An itch.io zip upload is not a wharf build: it has no channel, no build id and no
            // block signature, so it cannot be diffed. Saying so is more useful than "no build",
            // because the game clearly *is* downloadable from the website.
            string zipNote = game.HasItchDownload
                ? $"\nitch.io has '{game.ItchDownloadName}'" +
                  (string.IsNullOrWhiteSpace(game.ItchDownloadSize) ? "" : $" ({game.ItchDownloadSize})") +
                  ", but a zip upload cannot be patched. Push the unpacked folder to a channel:"
                : $"\nNo build published on '{game.Channel}'. Push one with:";

            return game.IsInstalled
                ? new GameStatus(LauncherState.ReadyToPlay, null, source.Id,
                    "No published build to compare against.")
                : GameStatus.NotInstalled(
                    zipNote.TrimStart('\n') +
                    $"\n  butler push <folder> {game.Owner}/{game.Id}:{game.Channel}");
        }

        if (!game.IsInstalled)
            return new GameStatus(LauncherState.NotInstalled, remote, source.Id);

        // The real comparison the previous engine never made.
        bool outdated = !string.Equals(game.InstalledBuildId, remote.BuildId, StringComparison.Ordinal);

        return outdated
            ? new GameStatus(LauncherState.UpdateAvailable, remote, source.Id,
                remote.DeltaAvailable
                    ? $"Patch update: {DownloadProgress.FormatBytes(remote.PatchBytes)} instead of " +
                      $"{DownloadProgress.FormatBytes(remote.TotalBytes)}"
                    : null)
            : new GameStatus(LauncherState.ReadyToPlay, remote, source.Id);
    }

    // ---------------- install ----------------

    public async Task<InstallResult> InstallOrUpdateAsync(
        GameInfo game,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return new InstallResult(false, "", "", 0, false, "Another download is already running.");

        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            var source = await ResolveSourceAsync(game, _operationCts.Token);
            if (source is null)
            {
                return new InstallResult(false, "", "", 0, false,
                    $"No content source can install '{game.Title}'. It needs a wharf channel: " +
                    $"butler push <folder> {game.Owner}/{game.Id}:windows");
            }

            if (string.IsNullOrWhiteSpace(game.InstallDirectory))
                return new InstallResult(false, "", "", 0, false, "No install directory is set.");

            _logger?.LogInformation("Installing {Game} from {Source} into {Dir}",
                game.Id, source.DisplayName, game.InstallDirectory);

            var result = await source.InstallOrUpdateAsync(game, progress, _operationCts.Token);

            if (result.Success)
            {
                // Record what is on disk now, so the next check can detect a new build.
                game.InstalledBuildId = result.BuildId;
                game.InstalledVersion = result.Version;
                game.LastUpdatedUtc = DateTime.UtcNow;
                game.InstalledSizeBytes = MeasureInstall(game.InstallDirectory);
                game.PreferredSourceId = source.Id;

                if (string.IsNullOrWhiteSpace(game.ExecutableRelativePath))
                {
                    string? found = game.DetectExecutable();
                    if (found is not null)
                    {
                        game.ExecutableRelativePath = found;
                        _logger?.LogInformation("Detected executable for {Game}: {Exe}", game.Id, found);
                    }
                    else
                    {
                        _logger?.LogWarning(
                            "No executable found under {Dir} for {Game}; set one in Settings.",
                            game.InstallDirectory, game.Id);
                    }
                }
            }
            else
            {
                _logger?.LogError("Install of {Game} failed: {Error}", game.Id, result.Error);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Install of {Game} was cancelled.", game.Id);
            return new InstallResult(false, "", "", 0, false, "Cancelled.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Install of {Game} threw.", game.Id);
            return new InstallResult(false, "", "", 0, false, ex.Message);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            Volatile.Write(ref _busy, 0);
        }
    }

    public async Task<VerifyResult> VerifyAndRepairAsync(
        GameInfo game,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return new VerifyResult(false, 0, 0, 0, "Another operation is already running.");

        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var source = await ResolveSourceAsync(game, _operationCts.Token);
            if (source is null)
                return new VerifyResult(false, 0, 0, 0, "No content source can verify this game.");

            return await source.VerifyAndRepairAsync(game, progress, _operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            return new VerifyResult(false, 0, 0, 0, "Cancelled.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Verify of {Game} threw.", game.Id);
            return new VerifyResult(false, 0, 0, 0, ex.Message);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            Volatile.Write(ref _busy, 0);
        }
    }

    // ---------------- launch ----------------

    public Task<bool> LaunchGameAsync(
        GameInfo game,
        string? additionalArgs = null,
        CancellationToken ct = default)
    {
        string exe = game.FullExecutablePath;

        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            string? detected = game.DetectExecutable();
            if (detected is not null)
            {
                game.ExecutableRelativePath = detected;
                exe = game.FullExecutablePath;
            }
        }

        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            _logger?.LogError("Cannot launch {Game}: no executable under {Dir}.",
                game.Id, game.InstallDirectory);
            return Task.FromResult(false);
        }

        try
        {
            string args = string.Join(' ', new[] { game.LaunchArguments, additionalArgs }
                .Where(a => !string.IsNullOrWhiteSpace(a)));

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = true
            };

            var proc = Process.Start(psi);
            if (proc is null) return Task.FromResult(false);

            _running[game.Id] = proc;
            game.LastPlayedUtc = DateTime.UtcNow;
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                _running.Remove(game.Id);
                GameExited?.Invoke(game.Id);
            };

            _logger?.LogInformation("Launched {Game}: {Exe} {Args}", game.Id, exe, args);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Launching {Game} failed.", game.Id);
            return Task.FromResult(false);
        }
    }

    /// <summary>Raised with the game id when a launched game exits, so the UI can re-enable Play.</summary>
    public event Action<string>? GameExited;

    public Task CancelOperationAsync()
    {
        try { _operationCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
        return Task.CompletedTask;
    }

    // ---------------- helpers ----------------

    private async Task<IContentSource?> ResolveSourceAsync(GameInfo game, CancellationToken ct)
    {
        var ordered = string.IsNullOrWhiteSpace(game.PreferredSourceId)
            ? _sources
            : _sources.OrderByDescending(s =>
                s.Id.Equals(game.PreferredSourceId, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var source in ordered)
        {
            try
            {
                if (await source.CanServeAsync(game, ct)) return source;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Source {Source} failed its availability check.", source.Id);
            }
        }

        return null;
    }

    private static long MeasureInstall(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CancelOperationAsync();
        foreach (var source in _sources)
        {
            try { await source.DisposeAsync(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Disposing {Source} threw.", source.Id); }
        }
    }
}
