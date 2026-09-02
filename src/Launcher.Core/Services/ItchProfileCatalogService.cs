using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Core.Abstractions;
using Launcher.Core.Models;
using Microsoft.Extensions.Logging;

namespace Launcher.Core.Services;

/// <summary>
/// Builds the library from an itch.io public profile page (https://itch.io/profile/{user}).
///
/// Why scrape rather than use the API:
///   - /profile/games returns only games the account owns, so collaborations under other
///     accounts are missing. The profile page lists everything the user is credited on.
///   - It needs no API key. A butler-issued key is wharf-scoped and is rejected for
///     `profile:games` and `game:view:uploads` anyway.
///
/// The tradeoff is that this depends on itch.io's markup. Results are cached to disk so a
/// markup change or an offline start degrades to the last known-good catalog rather than an
/// empty library, and <see cref="CatalogResult.Diagnostic"/> explains what happened.
/// </summary>
public class ItchProfileCatalogService : IItchCatalogService
{
    private readonly HttpClient _http;
    private readonly ILogger<ItchProfileCatalogService>? _logger;
    private readonly string _cachePath;

    // Cells look like:
    //   <div dir="auto" class="game_cell has_cover lazy_images" data-game_id="4861498">
    // Attribute order is not stable, so the cell is found by its class and the id is read
    // separately. \b keeps this from matching the nested game_cell_data / game_cell_tools divs.
    private static readonly Regex CellStart = new(
        @"<div[^>]*\bclass=""[^""]*\bgame_cell\b[^""]*""[^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex CellGameId = new(
        @"data-game_id=""(?<id>\d+)""",
        RegexOptions.Compiled);

    // The real anchor carries data-action and data-label between class and href:
    //   <a data-action="game_grid" class="title game_link" data-label="..." href="...">Title</a>
    // so this anchors on the surrounding game_title div and allows attributes in any order.
    private static readonly Regex TitleLink = new(
        @"class=""game_title""[^>]*>\s*<a\b[^>]*\bhref=""(?<url>[^""]+)""[^>]*>(?<title>[^<]*)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex Cover = new(
        @"data-lazy_src=""(?<url>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex AuthorLink = new(
        @"class=""game_author""><a[^>]*href=""(?<url>[^""]+)""[^>]*>(?<name>[^<]+)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ShortText = new(
        @"class=""game_text""[^>]*>(?<text>[^<]*)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex Genre = new(
        @"class=""game_genre"">(?<genre>[^<]*)",
        RegexOptions.Compiled);

    private static readonly Regex PlatformIcon = new(
        @"icon-(?<p>windows8|android|apple|tux|html5)",
        RegexOptions.Compiled);

    // Web-only games carry no platform icon, just <span class="web_flag">Play in browser</span>.
    private static readonly Regex WebFlag = new(@"class=""web_flag""", RegexOptions.Compiled);

    // On a game's own page an upload looks like:
    //   <a data-upload_id="16350683" class="button download_btn" ...>Download</a> ...
    //   <strong title="LogicRift.zip" class="name">LogicRift.zip</strong>
    //   <span class="file_size"><span>44 MB</span></span>
    private static readonly Regex DownloadButton = new(@"data-upload_id=""\d+""", RegexOptions.Compiled);

    private static readonly Regex UploadName = new(
        @"class=""upload_name""[^>]*>\s*<strong[^>]*\btitle=""(?<name>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex UploadSize = new(
        @"class=""file_size""[^>]*>\s*<span>(?<size>[^<]*)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public ItchProfileCatalogService(HttpClient? http = null, ILogger<ItchProfileCatalogService>? logger = null)
        : this(http, logger, null)
    {
    }

    public ItchProfileCatalogService(
        HttpClient? http,
        ILogger<ItchProfileCatalogService>? logger,
        string? cachePath)
    {
        _http = http ?? CreateDefaultClient();
        _logger = logger;
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyGameLauncher", "cache", "catalog.json");
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        // itch.io serves a reduced page to clients with no user agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "BirdboxLauncher/1.0 (+https://itch.io/profile/birdbox774)");
        return client;
    }

    public async Task<CatalogResult> FetchProfileGamesAsync(
        string profileUsername,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileUsername))
        {
            return CatalogResult.Failed(
                "No itch.io profile configured. Set your profile name in Settings.");
        }

        string user = profileUsername.Trim().TrimStart('@');
        string url = $"https://itch.io/profile/{Uri.EscapeDataString(user)}";

        try
        {
            using var response = await _http.GetAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return LoadCacheOr($"itch.io has no profile named '{user}'. Check the spelling in Settings.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return LoadCacheOr($"itch.io returned {(int)response.StatusCode} for {user}'s profile.");
            }

            string html = await response.Content.ReadAsStringAsync(ct);
            var games = ParseProfilePage(html, user);

            if (games.Count == 0)
            {
                return LoadCacheOr(
                    $"Profile '{user}' loaded but listed no games. If your games are drafts or " +
                    "unlisted they will not appear here; add them manually in Settings.");
            }

            await ProbeUploadsAsync(games, ct);

            await SaveCacheAsync(games, ct);
            _logger?.LogInformation("Loaded {Count} games from itch.io profile {User}.", games.Count, user);
            return new CatalogResult(games, FromCache: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read itch.io profile page for {User}.", user);
            return LoadCacheOr($"Could not reach itch.io ({ex.GetType().Name}). Showing the last synced catalog.");
        }
    }

    /// <summary>
    /// Splits the page at each game cell and pulls the fields out of every block. Kept tolerant:
    /// a cell missing a cover or genre still yields a usable entry.
    /// </summary>
    internal static List<GameInfo> ParseProfilePage(string html, string profileUser)
    {
        var games = new List<GameInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var starts = CellStart.Matches(html).Select(m => m.Index).ToList();

        for (int i = 0; i < starts.Count; i++)
        {
            int begin = starts[i];
            int end = i + 1 < starts.Count ? starts[i + 1] : html.Length;
            string cell = html[begin..end];

            var titleMatch = TitleLink.Match(cell);
            if (!titleMatch.Success) continue;

            string pageUrl = HtmlDecode(titleMatch.Groups["url"].Value);
            string title = HtmlDecode(titleMatch.Groups["title"].Value).Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            var (owner, slug) = SplitItchUrl(pageUrl);
            if (string.IsNullOrEmpty(slug)) continue;

            // A profile can list the same game in both a carousel and the grid below it.
            if (!seen.Add($"{owner}/{slug}")) continue;

            long.TryParse(CellGameId.Match(cell).Groups["id"].Value, out long itchId);

            var authorMatch = AuthorLink.Match(cell);
            string author = authorMatch.Success ? HtmlDecode(authorMatch.Groups["name"].Value).Trim() : owner;

            var platforms = PlatformIcon.Matches(cell)
                .Select(m => m.Groups["p"].Value switch
                {
                    "windows8" => "windows",
                    "apple" => "macos",
                    "tux" => "linux",
                    var other => other
                })
                .Distinct()
                .ToList();

            games.Add(new GameInfo
            {
                Id = slug,
                ItchGameId = itchId,
                Title = title,
                Owner = owner,
                Author = author,
                IsCollaboration = !owner.Equals(profileUser, StringComparison.OrdinalIgnoreCase),
                PageUrl = pageUrl,
                CoverImageUrl = Cover.Match(cell) is { Success: true } c ? HtmlDecode(c.Groups["url"].Value) : "",
                Description = ShortText.Match(cell) is { Success: true } t
                    ? HtmlDecode(t.Groups["text"].Value).Trim()
                    : "",
                Genre = Genre.Match(cell) is { Success: true } g ? HtmlDecode(g.Groups["genre"].Value).Trim() : "",
                Platforms = platforms,
                HasWebBuild = WebFlag.IsMatch(cell)
            });
        }

        return games;
    }

    /// <summary>
    /// Reads each game's own page to find out what can actually be downloaded.
    ///
    /// Needed because the profile grid only shows a platform icon when the uploader ticked the
    /// platform traits on the upload. Three of this profile's games have zip uploads with no
    /// traits set, so the grid showed nothing and they looked browser-only when they are not.
    ///
    /// Best effort: a page that fails to load leaves <see cref="GameInfo.UploadsChecked"/> false,
    /// which the model reads as "unknown" rather than "no download".
    /// </summary>
    private async Task ProbeUploadsAsync(List<GameInfo> games, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(3);

        await Task.WhenAll(games.Select(async game =>
        {
            if (string.IsNullOrWhiteSpace(game.PageUrl)) return;

            await gate.WaitAsync(ct);
            try
            {
                using var response = await _http.GetAsync(game.PageUrl, ct);
                if (!response.IsSuccessStatusCode) return;

                string html = await response.Content.ReadAsStringAsync(ct);
                var (hasDownload, name, size) = ParseGamePage(html);

                game.UploadsChecked = true;
                game.HasItchDownload = hasDownload;
                game.ItchDownloadName = name;
                game.ItchDownloadSize = size;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Could not read the game page for {Game}.", game.Id);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    /// <summary>Pulls the download list off a single game page.</summary>
    internal static (bool HasDownload, string Name, string Size) ParseGamePage(string html)
    {
        if (!DownloadButton.IsMatch(html)) return (false, "", "");

        string name = UploadName.Match(html) is { Success: true } n
            ? HtmlDecode(n.Groups["name"].Value).Trim()
            : "";

        string size = UploadSize.Match(html) is { Success: true } s
            ? HtmlDecode(s.Groups["size"].Value).Trim()
            : "";

        return (true, name, size);
    }

    /// <summary>"https://falcon-eye.itch.io/what-can-possibly-go-pong" -> ("falcon-eye", "what-can-possibly-go-pong").</summary>
    internal static (string Owner, string Slug) SplitItchUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return ("", "");

        string host = uri.Host;
        string owner = host.EndsWith(".itch.io", StringComparison.OrdinalIgnoreCase)
            ? host[..^".itch.io".Length]
            : "";

        string slug = uri.AbsolutePath.Trim('/');
        if (slug.Contains('/')) slug = slug[(slug.LastIndexOf('/') + 1)..];

        return (owner, slug);
    }

    private static string HtmlDecode(string s) => WebUtility.HtmlDecode(s) ?? s;

    // ---- disk cache ----

    private CatalogResult LoadCacheOr(string diagnostic)
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var cached = JsonSerializer.Deserialize<List<GameInfo>>(File.ReadAllText(_cachePath));
                if (cached is { Count: > 0 })
                {
                    _logger?.LogInformation("Serving {Count} games from catalog cache.", cached.Count);
                    return new CatalogResult(cached, FromCache: true, diagnostic);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Catalog cache at {Path} could not be read.", _cachePath);
        }

        return CatalogResult.Failed(diagnostic);
    }

    private async Task SaveCacheAsync(List<GameInfo> games, CancellationToken ct)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(
                _cachePath,
                JsonSerializer.Serialize(games, new JsonSerializerOptions { WriteIndented = true }),
                ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not write catalog cache to {Path}.", _cachePath);
        }
    }
}
