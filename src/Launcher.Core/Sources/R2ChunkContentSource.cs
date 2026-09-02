using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Core.Abstractions;
using Launcher.Core.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Core.Sources;

/// <summary>
/// Player-facing content source for a self-hosted chunk CDN (Cloudflare R2).
///
/// Why this exists alongside wharf: the wharf path drives butler, and butler needs an itch.io API
/// key. A key that can fetch builds must never ship inside a launcher handed to players. This
/// source needs no credentials — it reads a public manifest and pulls only the chunks whose
/// hashes changed, which is the same "download the diff, not the game" behaviour without the
/// account.
///
/// Status: version detection is implemented and works against a published manifest. Chunk
/// transfer is not — it reports plainly rather than pretending, so nothing silently produces a
/// broken install. <see cref="ChunkManifest"/> below fixes the on-disk format the uploader will
/// generate, so the two halves cannot drift apart.
/// </summary>
public class R2ChunkContentSource : IContentSource
{
    private readonly HttpClient _http;
    private readonly string? _baseUrl;
    private readonly ILogger<R2ChunkContentSource>? _logger;

    public string Id => "r2-chunks";
    public string DisplayName => "Direct CDN";

    /// <param name="baseUrl">
    /// Public bucket root, e.g. https://cdn.example.com/games. Null or empty disables the source,
    /// which is the expected state until the bucket exists.
    /// </param>
    public R2ChunkContentSource(
        string? baseUrl,
        HttpClient? http = null,
        ILogger<R2ChunkContentSource>? logger = null)
    {
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _logger = logger;
    }

    public async Task<bool> CanServeAsync(GameInfo game, CancellationToken ct = default)
    {
        if (_baseUrl is null) return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, ManifestUrl(game));
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "No CDN manifest for {Game}.", game.Id);
            return false;
        }
    }

    public async Task<RemoteVersionInfo?> GetRemoteVersionAsync(GameInfo game, CancellationToken ct = default)
    {
        var manifest = await FetchManifestAsync(game, ct);
        if (manifest is null) return null;

        long patchBytes = manifest.TotalBytes;
        bool delta = false;

        // With a local manifest from the installed build, the patch size is exactly the size of
        // the chunks whose hashes differ.
        var local = LoadLocalManifest(game);
        if (local is not null && local.BuildId != manifest.BuildId)
        {
            var have = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in local.Files)
                foreach (var c in f.Chunks) have.Add(c.Hash);

            long needed = 0;
            foreach (var f in manifest.Files)
                foreach (var c in f.Chunks)
                    if (!have.Contains(c.Hash)) needed += c.Size;

            patchBytes = needed;
            delta = needed < manifest.TotalBytes;
        }

        return new RemoteVersionInfo(
            BuildId: manifest.BuildId,
            Version: manifest.Version,
            TotalBytes: manifest.TotalBytes,
            PatchBytes: patchBytes,
            DeltaAvailable: delta,
            SourceRef: ManifestUrl(game));
    }

    public Task<InstallResult> InstallOrUpdateAsync(
        GameInfo game,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default)
    {
        return Task.FromResult(new InstallResult(false, "", "", 0, false,
            "The direct CDN source is not serving downloads yet. Install through itch.io for now."));
    }

    public Task<VerifyResult> VerifyAndRepairAsync(
        GameInfo game,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default)
    {
        return Task.FromResult(new VerifyResult(false, 0, 0, 0,
            "The direct CDN source cannot verify installs yet."));
    }

    private string ManifestUrl(GameInfo game) => $"{_baseUrl}/{game.Id}/{game.Channel}/manifest.json";

    private static string LocalManifestPath(GameInfo game) =>
        Path.Combine(game.InstallDirectory, ".launcher", "manifest.json");

    private async Task<ChunkManifest?> FetchManifestAsync(GameInfo game, CancellationToken ct)
    {
        if (_baseUrl is null) return null;
        try
        {
            return await _http.GetFromJsonAsync<ChunkManifest>(ManifestUrl(game), ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read CDN manifest for {Game}.", game.Id);
            return null;
        }
    }

    private static ChunkManifest? LoadLocalManifest(GameInfo game)
    {
        try
        {
            string path = LocalManifestPath(game);
            return File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize<ChunkManifest>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The manifest published next to a build. Chunks are content-addressed, so any chunk a player
/// already has on disk from a previous version is reused instead of downloaded — that reuse is
/// what makes an update smaller than the game.
/// </summary>
public record ChunkManifest(
    string BuildId,
    string Version,
    long TotalBytes,
    int ChunkSize,
    List<ManifestFile> Files
);

public record ManifestFile(string Path, long Size, List<ManifestChunk> Chunks);

/// <param name="Hash">Content hash; doubles as the object key under /chunks/.</param>
public record ManifestChunk(string Hash, long Offset, int Size);
