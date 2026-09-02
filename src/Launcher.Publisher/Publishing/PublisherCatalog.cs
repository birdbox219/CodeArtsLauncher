using Launcher.Core.Abstractions;
using Launcher.Core.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Publisher.Publishing;

public sealed record PanelChannel(string Name, string BuildId, string Version);

/// <summary>One row of the panel's game list.</summary>
public sealed record PanelGame(
    string Slug,
    string Owner,
    string Title,
    bool IsCollaboration,
    string PageUrl,
    string CoverImageUrl,
    string DefaultChannel,
    IReadOnlyList<PanelChannel> Channels,
    string? ChannelError,
    bool HasItchDownload,
    string ItchDownloadName,
    string ItchDownloadSize,
    bool HasWebBuild)
{
    /// <summary>False means the game has never been pushed, so there is no patch chain yet.</summary>
    public bool IsPublished => Channels.Count > 0;
}

public sealed record PanelCatalog(
    string Profile,
    IReadOnlyList<PanelGame> Games,
    bool FromCache,
    string? Diagnostic,
    DateTimeOffset FetchedUtc);

/// <summary>
/// The game list the panel publishes to, built from the same scrape the launcher uses — the public
/// profile page, because <c>/profile/games</c> drops collaborations hosted under other accounts and
/// two of these six games are exactly that.
///
/// Each game's channels are then read from butler, which is what says whether it has ever been
/// pushed.
/// </summary>
public sealed class PublisherCatalog
{
    private readonly IItchCatalogService _catalog;
    private readonly IConfigService _config;
    private readonly ButlerPublishService _butler;
    private readonly ILogger<PublisherCatalog>? _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private PanelCatalog? _cached;

    public PublisherCatalog(
        IItchCatalogService catalog,
        IConfigService config,
        ButlerPublishService butler,
        ILogger<PublisherCatalog>? logger = null)
    {
        _catalog = catalog;
        _config = config;
        _butler = butler;
        _logger = logger;
    }

    /// <summary>
    /// Cached for the lifetime of the panel: the scrape is six page fetches plus a
    /// <c>butler status</c> per game, which is slow enough to be annoying on every keystroke.
    /// The refresh button forces it, and a successful push refreshes it too.
    /// </summary>
    public async Task<PanelCatalog> GetAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && _cached is not null) return _cached;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (!refresh && _cached is not null) return _cached;

            var config = await _config.LoadConfigAsync(ct);
            string profile = config.ItchProfileUsername;

            var result = await _catalog.FetchProfileGamesAsync(profile, ct);

            var games = result.Games.ToList();
            foreach (var manual in config.ManualGames)
            {
                if (games.Any(g => string.Equals(g.Id, manual.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                games.Add(manual);
            }

            var rows = await ProbeChannelsAsync(games, config, ct);

            _cached = new PanelCatalog(profile, rows, result.FromCache, result.Diagnostic, DateTimeOffset.UtcNow);
            return _cached;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Drops the cache so the next read re-scrapes; used after a push lands.</summary>
    public void Invalidate() => _cached = null;

    private async Task<IReadOnlyList<PanelGame>> ProbeChannelsAsync(
        IReadOnlyList<GameInfo> games, LauncherConfig config, CancellationToken ct)
    {
        // Three at a time: enough to keep the wait short, not enough to look like a scraper.
        using var gate = new SemaphoreSlim(3, 3);

        var tasks = games.Select(async game =>
        {
            await gate.WaitAsync(ct);
            try
            {
                string defaultChannel =
                    config.GameOverrides.TryGetValue(game.Id, out var over) && !string.IsNullOrWhiteSpace(over.Channel)
                        ? over.Channel
                        : !string.IsNullOrWhiteSpace(game.Channel)
                            ? game.Channel
                            : config.DefaultChannel;

                var (channels, error) = await _butler.GetChannelsAsync($"{game.Owner}/{game.Id}", ct);

                if (error is not null)
                    _logger?.LogInformation("Channel probe for {Game}: {Error}", game.Id, error);

                return new PanelGame(
                    game.Id,
                    game.Owner,
                    game.Title,
                    game.IsCollaboration,
                    game.PageUrl,
                    game.CoverImageUrl,
                    defaultChannel,
                    channels.Select(c => new PanelChannel(c.Name, c.BuildId, c.Version)).ToList(),
                    error,
                    game.HasItchDownload,
                    game.ItchDownloadName,
                    game.ItchDownloadSize,
                    game.HasWebBuild);
            }
            finally
            {
                gate.Release();
            }
        });

        var rows = await Task.WhenAll(tasks);

        // Unpushed games first: those are the ones that still need doing.
        return rows
            .OrderBy(r => r.IsPublished ? 1 : 0)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
