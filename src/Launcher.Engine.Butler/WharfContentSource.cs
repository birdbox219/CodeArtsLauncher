using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Core.Abstractions;
using Launcher.Core.Models;
using Launcher.Engine.Butler.Process;
using Launcher.Engine.Butler.Rpc;
using Microsoft.Extensions.Logging;

namespace Launcher.Engine.Butler;

/// <summary>
/// Content source backed by itch.io wharf channels, driven through butler.
///
/// Two paths, picked automatically:
///
///   1. butlerd (Install.Queue -> Install.Perform). The only path that applies wharf *patches*,
///      so the only one that gives incremental updates. Requires a full-scope itch.io API key:
///      a butler-issued key is wharf-only and Profile.LoginWithAPIKey fails on it with
///      "api key does not permit `profile:me`".
///
///   2. butler CLI `fetch`. Works with a wharf-scoped key but always downloads the whole
///      build — no delta. Used as a fallback so the launcher still installs without a
///      full-scope key.
///
/// Either way the game must have been pushed with `butler push` first. Web-uploaded zips have
/// no channel, so `status` reports no channels and there is nothing to install or diff.
/// </summary>
public class WharfContentSource : IContentSource
{
    private readonly string _butlerPath;
    private readonly string _dbPath;
    private readonly ILogger? _logger;
    private readonly Func<string?> _apiKeyProvider;
    private readonly ButlerDaemonManager _daemon;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    private ButlerRpcClient? _rpc;
    private bool _daemonUnavailable;
    private bool _loggedIn;
    private long _profileId;

    public string Id => "wharf";
    public string DisplayName => "itch.io (wharf)";

    public WharfContentSource(
        string butlerPath,
        string dbPath,
        Func<string?> apiKeyProvider,
        ILogger? logger = null,
        HttpClient? http = null)
    {
        _butlerPath = butlerPath;
        _dbPath = dbPath;
        _apiKeyProvider = apiKeyProvider;
        _logger = logger;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttp = http is null;
        _daemon = new ButlerDaemonManager(logger);
    }

    public Task<bool> CanServeAsync(GameInfo game, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(_butlerPath) && game.HasChannel);

    // ---------------- version / update detection ----------------

    public async Task<RemoteVersionInfo?> GetRemoteVersionAsync(GameInfo game, CancellationToken ct = default)
    {
        if (!game.HasChannel) return null;

        var (exit, stdout, stderr) = await RunButlerAsync(
            new[] { "--json", "status", game.ButlerTarget, "--show-all-files" }, ct);

        if (exit != 0)
        {
            _logger?.LogWarning("butler status failed for {Target}: {Err}", game.ButlerTarget, Trim(stderr));
            return null;
        }

        var channels = ExtractChannels(stdout);
        if (channels.Count == 0)
        {
            _logger?.LogInformation(
                "No wharf channel for {Target}. The game has not been pushed with `butler push`.",
                game.ButlerTarget);
            return null;
        }

        // Prefer the exact channel, else the only one available.
        var channel = channels.FirstOrDefault(c =>
                          string.Equals(c.Name, game.Channel, StringComparison.OrdinalIgnoreCase))
                      ?? (channels.Count == 1 ? channels[0] : null);

        if (channel is null)
        {
            _logger?.LogWarning(
                "Channel '{Channel}' not found for {Game}. Available: {Available}",
                game.Channel, game.Id, string.Join(", ", channels.Select(c => c.Name)));
            return null;
        }

        bool isUpgrade = !string.IsNullOrWhiteSpace(game.InstalledBuildId)
                         && game.InstalledBuildId != channel.BuildId;

        // `butler --json status` reports the head build's id and version but not its files, so
        // there is nothing in its output to sum and ExtractChannels comes back with 0. That is
        // why a freshly pushed game showed "0 MB download" until it was installed and the folder
        // could be measured on disk. Ask itch.io for the real upload size instead.
        long totalBytes = channel.TotalBytes;
        if (totalBytes <= 0)
        {
            totalBytes = await TryFetchUploadSizeAsync(game, channel.Name, ct) ?? 0;
        }

        // Ask butlerd for the real patch size when it can; otherwise assume a full download.
        long patchBytes = totalBytes;
        bool delta = false;

        if (isUpgrade)
        {
            var planned = await TryPlanUpgradeAsync(game, ct);
            if (planned is not null)
            {
                patchBytes = planned.Value;
                delta = totalBytes > 0 && planned.Value < totalBytes;
            }
        }

        return new RemoteVersionInfo(
            BuildId: channel.BuildId,
            Version: channel.Version,
            TotalBytes: totalBytes,
            PatchBytes: patchBytes,
            DeltaAvailable: delta,
            SourceRef: game.ButlerTarget);
    }

    /// <summary>
    /// Reads a channel's download size from itch.io's wharf API, because `butler status` does not
    /// report it.
    ///
    /// This uses the same wharf-scoped key butler itself uses, so it needs no extra permission,
    /// and like every other key here it is read from the environment or the local butler login and
    /// never embedded in the build. Returns null when there is no key or the request fails, and
    /// callers then show "—" rather than inventing a number.
    /// </summary>
    private async Task<long?> TryFetchUploadSizeAsync(GameInfo game, string channelName, CancellationToken ct)
    {
        string? key = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            string target = $"{game.Owner}/{game.Id}";
            string url = $"https://itch.io/api/1/{key.Trim()}/wharf/channels" +
                         $"?target={Uri.EscapeDataString(target)}";

            long? size = ExtractUploadSize(await _http.GetStringAsync(url, ct), channelName);

            if (size is null)
                _logger?.LogDebug("No upload size in the wharf response for {Game}.", game.Id);

            return size;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately only the exception type: the request URL carries the API key, and some
            // exception messages quote the URL they failed on. A missing size is cosmetic, so it
            // is not worth risking a key in launcher.log.
            _logger?.LogDebug("Could not read the wharf upload size for {Game} ({Error}).",
                game.Id, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Uses Install.Plan to learn the download size for an upgrade. Returns null when butlerd
    /// is not usable, in which case callers fall back to assuming a full download.
    /// </summary>
    private async Task<long?> TryPlanUpgradeAsync(GameInfo game, CancellationToken ct)
    {
        var rpc = await TryGetLoggedInRpcAsync(ct);
        if (rpc is null || game.ItchGameId <= 0) return null;

        try
        {
            var plan = await rpc.SendRequestAsync("Install.Plan", new
            {
                gameId = game.ItchGameId,
                downloadSessionId = Guid.NewGuid().ToString()
            }, ct);

            long? size = plan?["info"]?["upload"]?["size"]?.GetValue<long?>()
                         ?? plan?["upload"]?["size"]?.GetValue<long?>();
            return size;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Install.Plan unavailable for {Game}.", game.Id);
            return null;
        }
    }

    // ---------------- install / update ----------------

    public async Task<InstallResult> InstallOrUpdateAsync(
        GameInfo game,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default)
    {
        if (!game.HasChannel)
        {
            return new InstallResult(false, "", "", 0, false,
                $"'{game.Title}' has no wharf channel. Push it first:\n" +
                $"  butler push <build-folder> {game.Owner}/{game.Id}:windows");
        }

        Directory.CreateDirectory(game.InstallDirectory);

        var remote = await GetRemoteVersionAsync(game, ct);
        if (remote is null)
        {
            return new InstallResult(false, "", "", 0, false,
                $"No build found on channel '{game.Channel}' for {game.Owner}/{game.Id}. " +
                "Push a build with `butler push` first.");
        }

        // Delta path: only butlerd applies wharf patches.
        var rpc = await TryGetLoggedInRpcAsync(ct);
        if (rpc is not null && game.ItchGameId > 0)
        {
            try
            {
                return await InstallViaDaemonAsync(rpc, game, remote, progress, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "butlerd install failed for {Game}; falling back to a full `butler fetch`.", game.Id);
                progress.Report(new DownloadProgress(0, 0, remote.TotalBytes, 0, null,
                    "Patch service unavailable — downloading the full build"));
            }
        }

        // Fallback: full download via the CLI.
        return await InstallViaCliFetchAsync(game, remote, progress, ct);
    }

    private async Task<InstallResult> InstallViaDaemonAsync(
        ButlerRpcClient rpc,
        GameInfo game,
        RemoteVersionInfo remote,
        IProgress<DownloadProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new DownloadProgress(0, 0, remote.PatchBytes, 0, null, "Planning update"));

        string staging = Path.Combine(Path.GetTempPath(), "MyGameLauncher", "staging", game.Id);
        Directory.CreateDirectory(staging);

        long lastBytes = 0;
        bool sawPatch = false;

        void OnNotification(string method, JsonNode? p)
        {
            if (!method.Equals("Progress", StringComparison.OrdinalIgnoreCase) || p is not JsonObject o)
                return;

            double fraction = o["progress"]?.GetValue<double?>() ?? 0;
            double bps = o["bps"]?.GetValue<double?>() ?? 0;
            double etaSeconds = o["eta"]?.GetValue<double?>() ?? 0;

            long total = remote.PatchBytes > 0 ? remote.PatchBytes : remote.TotalBytes;
            lastBytes = (long)(fraction * total);

            progress.Report(new DownloadProgress(
                Math.Clamp(fraction, 0, 1),
                lastBytes,
                total,
                bps,
                etaSeconds > 0 ? TimeSpan.FromSeconds(etaSeconds) : null,
                sawPatch ? "Applying patch" : "Downloading"));
        }

        void OnTask(string method, JsonNode? p)
        {
            if (method.Equals("TaskStarted", StringComparison.OrdinalIgnoreCase)
                && p is JsonObject o
                && (o["type"]?.GetValue<string>() ?? "").Contains("patch", StringComparison.OrdinalIgnoreCase))
            {
                sawPatch = true;
            }
        }

        void Handler(string m, JsonNode? p) { OnNotification(m, p); OnTask(m, p); }

        rpc.NotificationReceived += Handler;
        try
        {
            var queue = await rpc.SendRequestAsync("Install.Queue", new
            {
                game = new { id = game.ItchGameId },
                installFolder = game.InstallDirectory,
                stagingFolder = staging,
                reason = string.IsNullOrWhiteSpace(game.InstalledBuildId) ? "install" : "update",
                fastQueue = false
            }, ct) ?? throw new InvalidOperationException("Install.Queue returned no result.");

            string? queueId = queue["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(queueId))
                throw new InvalidOperationException("Install.Queue returned no queue id.");

            // This is the call the previous implementation was missing entirely, which is why
            // nothing was ever downloaded even when the queue call itself succeeded.
            await rpc.SendRequestAsync("Install.Perform", new
            {
                id = queueId,
                stagingFolder = staging
            }, ct);

            progress.Report(new DownloadProgress(1, lastBytes, lastBytes, 0, TimeSpan.Zero, "Update complete"));

            return new InstallResult(
                Success: true,
                BuildId: remote.BuildId,
                Version: remote.Version,
                BytesTransferred: lastBytes,
                WasDelta: sawPatch || remote.DeltaAvailable);
        }
        finally
        {
            rpc.NotificationReceived -= Handler;
            TryDeleteDirectory(staging);
        }
    }

    private async Task<InstallResult> InstallViaCliFetchAsync(
        GameInfo game,
        RemoteVersionInfo remote,
        IProgress<DownloadProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new DownloadProgress(0, 0, remote.TotalBytes, 0, null, "Downloading build"));

        long transferred = 0;

        var (exit, _, stderr) = await RunButlerAsync(
            new[] { "--json", "fetch", game.ButlerTarget, game.InstallDirectory },
            ct,
            onJsonLine: node =>
            {
                if (node is not JsonObject o) return;
                if ((o["type"]?.GetValue<string>() ?? "") != "progress") return;

                // butler reports progress as a 0..1 fraction; older builds used 0..100.
                double raw = o["progress"]?.GetValue<double?>()
                             ?? o["percentage"]?.GetValue<double?>()
                             ?? 0;
                double fraction = raw > 1 ? raw / 100.0 : raw;

                double bps = o["bps"]?.GetValue<double?>() ?? 0;
                double eta = o["eta"]?.GetValue<double?>() ?? 0;

                transferred = (long)(fraction * remote.TotalBytes);
                progress.Report(new DownloadProgress(
                    Math.Clamp(fraction, 0, 1),
                    transferred,
                    remote.TotalBytes,
                    bps,
                    eta > 0 ? TimeSpan.FromSeconds(eta) : null,
                    "Downloading build"));
            });

        if (exit != 0)
        {
            return new InstallResult(false, "", "", transferred, false,
                $"butler fetch failed: {Trim(stderr)}");
        }

        progress.Report(new DownloadProgress(1, remote.TotalBytes, remote.TotalBytes, 0, TimeSpan.Zero, "Install complete"));

        return new InstallResult(
            Success: true,
            BuildId: remote.BuildId,
            Version: remote.Version,
            BytesTransferred: remote.TotalBytes,
            WasDelta: false);
    }

    // ---------------- verify ----------------

    public async Task<VerifyResult> VerifyAndRepairAsync(
        GameInfo game,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(game.InstallDirectory))
            return new VerifyResult(false, 0, 0, 0, "Game is not installed.");

        progress.Report(new DownloadProgress(0.1, 0, 100, 0, null, "Checking installed files"));

        // Walking a large install folder and stat-ing every file is slow enough to stutter the UI,
        // so it goes to the thread pool rather than running inline on the caller's thread.
        var (files, totalSize) = await Task.Run(() =>
        {
            var found = Directory.GetFiles(game.InstallDirectory, "*", SearchOption.AllDirectories);
            return (found, found.Sum(f => new FileInfo(f).Length));
        }, ct);

        // `butler verify` needs a signature file for the build (`verify <signature> <dir>`), which
        // is only obtainable for a pushed build. The previous code called it as
        // `verify <dir> --json`, which is the wrong argument order and always failed.
        // Without a signature the honest check is a structural one, then let a re-install repair.
        bool exeOk = game.HasPlayableExecutable || game.DetectExecutable() is not null;

        progress.Report(new DownloadProgress(1, totalSize, totalSize, 0, TimeSpan.Zero, "Check complete"));

        if (files.Length == 0)
            return new VerifyResult(false, 0, 0, 0, "Install folder is empty. Reinstall to repair.");

        if (!exeOk)
            return new VerifyResult(false, files.Length, 0, 0,
                "No executable found in the install folder. Reinstall to repair.");

        return new VerifyResult(Healthy: true, FilesChecked: files.Length, FilesRepaired: 0, BytesRepaired: 0);
    }

    // ---------------- butlerd plumbing ----------------

    /// <summary>
    /// Returns a butlerd client that has a logged-in profile, or null when that is not possible.
    /// Null is the normal case with a wharf-scoped key and callers must degrade gracefully.
    /// </summary>
    private async Task<ButlerRpcClient?> TryGetLoggedInRpcAsync(CancellationToken ct)
    {
        if (_daemonUnavailable) return null;
        if (_rpc is not null && _loggedIn) return _rpc;

        try
        {
            _rpc ??= await _daemon.StartDaemonAsync(_butlerPath, _dbPath, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "butlerd could not be started; delta updates are unavailable.");
            _daemonUnavailable = true;
            return null;
        }

        if (_loggedIn) return _rpc;

        string? apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger?.LogInformation(
                "No itch.io API key configured, so butlerd cannot log in. Installs will be full " +
                "downloads. Add a full-scope key from itch.io/user/settings/api-keys for delta updates.");
            _daemonUnavailable = true;
            return null;
        }

        try
        {
            var profile = await _rpc!.SendRequestAsync("Profile.LoginWithAPIKey",
                new { apiKey = apiKey.Trim() }, ct);

            _profileId = profile?["profile"]?["id"]?.GetValue<long?>() ?? 0;
            _loggedIn = true;
            _logger?.LogInformation("butlerd logged in (profile {ProfileId}); delta updates enabled.", _profileId);
            return _rpc;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                "butlerd login failed, so updates will be full downloads. A butler-issued key is " +
                "wharf-only; create a full-scope key at itch.io/user/settings/api-keys. Detail: {Message}",
                ex.Message);
            _daemonUnavailable = true;
            return null;
        }
    }

    // ---------------- butler CLI plumbing ----------------

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunButlerAsync(
        IReadOnlyList<string> args,
        CancellationToken ct,
        Action<JsonNode>? onJsonLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _butlerPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,

            // butler writes UTF-8. Its failure messages end up in the diagnostic banner, so decoding
            // them with the console code page would put mojibake in front of the player.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {_butlerPath}");

        var stdout = new List<string>();
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            stdout.Add(line);

            if (onJsonLine is null) continue;
            try
            {
                if (JsonNode.Parse(line) is { } node) onJsonLine(node);
            }
            catch
            {
                // Non-JSON progress chatter; ignore.
            }
        }

        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, string.Join("\n", stdout), await stderrTask);
    }

    /// <summary>
    /// Pulls channel entries out of `butler --json status` output. Shape:
    /// {"type":"result","value":{"channels":[...],"target":"user/game"}}
    /// Field names are probed defensively because they vary across butler versions.
    /// </summary>
    internal static List<WharfChannel> ExtractChannels(string stdout)
    {
        var result = new List<WharfChannel>();

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(line.Trim()); }
            catch { continue; }

            if (node is not JsonObject obj) continue;
            if ((obj["type"]?.GetValue<string>() ?? "") != "result") continue;

            var channels = obj["value"]?["channels"]?.AsArray();
            if (channels is null) continue;

            foreach (var c in channels)
            {
                if (c is not JsonObject co) continue;

                var head = co["head"] ?? co["latest"] ?? co["build"];

                string name = co["name"]?.GetValue<string>() ?? "";
                string buildId =
                    head?["id"]?.ToString()
                    ?? co["buildId"]?.ToString()
                    ?? "";

                string version =
                    head?["userVersion"]?.GetValue<string>()
                    ?? head?["version"]?.ToString()
                    ?? "";

                long size = 0;
                if (head?["files"]?.AsArray() is { } files)
                {
                    foreach (var f in files)
                        size += f?["size"]?.GetValue<long?>() ?? 0;
                }
                size = size != 0 ? size : head?["size"]?.GetValue<long?>() ?? 0;

                if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(buildId))
                    result.Add(new WharfChannel(name, buildId, version, size));
            }
        }

        return result;
    }

    /// <summary>
    /// Pulls the download size out of an itch.io wharf channels response:
    /// {"channels":{"windows":{"head":{...},"upload":{"size":47837609,...},"name":"windows"}}}
    ///
    /// Note the shape difference from <see cref="ExtractChannels"/>: here channels is an object
    /// keyed by channel name, not an array.
    /// </summary>
    internal static long? ExtractUploadSize(string json, string channelName)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch { return null; }

        if (node?["channels"] is not JsonObject channels || channels.Count == 0) return null;

        JsonNode? channel = channels
            .FirstOrDefault(kv => string.Equals(kv.Key, channelName, StringComparison.OrdinalIgnoreCase))
            .Value;

        // A lone channel under a different name is still unambiguous.
        channel ??= channels.Count == 1 ? channels.First().Value : null;

        long? size = channel?["upload"]?["size"]?.GetValue<long?>()
                     ?? channel?["head"]?["size"]?.GetValue<long?>();

        return size is > 0 ? size : null;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* staging leftovers are harmless */ }
    }

    private static string Trim(string s) =>
        string.IsNullOrWhiteSpace(s) ? "(no output)" : s.Trim().Replace("\r", "").Split('\n').Last();

    public async ValueTask DisposeAsync()
    {
        if (_rpc is not null)
        {
            await _rpc.DisposeAsync();
            _rpc = null;
        }
        await _daemon.DisposeAsync();
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>One wharf channel and the build at its head.</summary>
public record WharfChannel(string Name, string BuildId, string Version, long TotalBytes);
