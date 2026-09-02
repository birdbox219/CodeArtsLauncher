using System;
using System.IO;
using System.Linq;
using Launcher.Core.Enums;
using Launcher.Core.Models;
using Launcher.Core.Services;
using Launcher.Core.ViewModels;
using Launcher.Engine.Butler;
using Xunit;

namespace Launcher.Core.Tests;

public class DownloadProgressTests
{
    [Fact]
    public void FormatsRealNumbers()
    {
        var progress = new DownloadProgress(
            Fraction: 0.5,
            BytesTransferred: 50 * 1024 * 1024,
            TotalBytes: 100 * 1024 * 1024,
            SpeedBytesPerSecond: 10 * 1024 * 1024,
            Eta: TimeSpan.FromSeconds(5),
            Stage: "Downloading");

        Assert.Equal(50, progress.Percent);
        Assert.Equal("10.0 MB/s", progress.FormattedSpeed);
        Assert.Equal("50.0 MB / 100.0 MB", progress.FormattedProgress);
        Assert.Equal("5s left", progress.FormattedEta);
    }

    [Fact]
    public void UnknownValuesSaySoInsteadOfShowingZero()
    {
        var progress = new DownloadProgress(0, 0, 0, 0, null, "Preparing");

        Assert.Equal("—", progress.FormattedSpeed);
        Assert.Equal("—", progress.FormattedEta);
    }

    [Theory]
    [InlineData(0L, "0 MB")]
    [InlineData(900L, "900 B")]
    [InlineData(1536L, "2 KB")]
    [InlineData(5L * 1024 * 1024, "5.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.0 GB")]
    public void ScalesUnits(long bytes, string expected) =>
        Assert.Equal(expected, DownloadProgress.FormatBytes(bytes));
}

public class GameInfoTests
{
    [Fact]
    public void FullExecutablePathCombinesDirectoryAndRelativePath()
    {
        var game = new GameInfo
        {
            Id = "test-game",
            InstallDirectory = @"C:\Games\MyGame",
            ExecutableRelativePath = @"Binaries\Win64\Game.exe"
        };

        Assert.Equal(@"C:\Games\MyGame\Binaries\Win64\Game.exe", game.FullExecutablePath);
    }

    [Fact]
    public void ButlerTargetNeedsOwnerSlugAndChannel()
    {
        var game = new GameInfo { Id = "logic-rift", Owner = "birdbox774" };

        Assert.False(game.HasChannel);
        Assert.Equal("", game.ButlerTarget);

        game.Channel = "windows";

        Assert.True(game.HasChannel);
        Assert.Equal("birdbox774/logic-rift:windows", game.ButlerTarget);
    }

    [Fact]
    public void NotInstalledUntilABuildIsRecorded()
    {
        string dir = Path.Combine(Path.GetTempPath(), "launcher-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var game = new GameInfo { Id = "g", InstallDirectory = dir };

            // A directory alone is not an install: the build id is what update checks compare.
            Assert.False(game.IsInstalled);

            game.InstalledBuildId = "12345";
            Assert.True(game.IsInstalled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DetectExecutablePrefersTheGameAndSkipsCrashHandlers()
    {
        string dir = Path.Combine(Path.GetTempPath(), "launcher-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The crash handler is typically the alphabetically first and sometimes the largest.
            File.WriteAllText(Path.Combine(dir, "UnityCrashHandler64.exe"), new string('x', 4000));
            File.WriteAllText(Path.Combine(dir, "LogicRift.exe"), new string('x', 100));

            var game = new GameInfo { Id = "logic-rift", Title = "Logic Rift", InstallDirectory = dir };

            Assert.Equal("LogicRift.exe", game.DetectExecutable());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class RemoteVersionInfoTests
{
    [Fact]
    public void ReportsWhatADeltaSaves()
    {
        var remote = new RemoteVersionInfo("42", "1.1.0",
            TotalBytes: 500 * 1024 * 1024,
            PatchBytes: 20 * 1024 * 1024,
            DeltaAvailable: true);

        Assert.Equal(480L * 1024 * 1024, remote.BytesSaved);
        Assert.Equal(0.04, remote.PatchRatio, precision: 3);
    }

    [Fact]
    public void ClaimsNoSavingsWithoutADelta()
    {
        var remote = new RemoteVersionInfo("42", "1.1.0", 500, 500, DeltaAvailable: false);

        Assert.Equal(0, remote.BytesSaved);
    }
}

public class ItchProfileParsingTests
{
    // Copied verbatim from https://itch.io/profile/birdbox774, two cells out of six, only the
    // wrapping div removed. Kept byte-for-byte on purpose: an earlier hand-written fixture used
    // data-game_id before class= and href straight after class="title game_link", neither of
    // which is what itch.io actually serves, so the parser passed its tests and found 0 games.
    private const string ProfileHtml = """
        <div class="game_grid_widget base_widget scrolling_inner game_list">
        <div dir="auto" class="game_cell has_cover lazy_images" data-game_id="4861498"><div class="game_thumb" style="background-color:#111111;"><a class="thumb_link game_link" tabindex="-1" href="https://ahmed-bahaa66.itch.io/last-champion" data-label="game:4861498:thumb" data-action="game_grid"><img class="lazy_loaded" width="315" height="250" data-lazy_src="https://img.itch.zone/aW1nLzI5MDQ0ODcxLnBuZw==/315x250%23c/NK6WEm.png"/></a><div class="game_cell_tools"><a data-register_action="add_to_collection" href="/g/ahmed-bahaa66/last-champion/add-to-collection" class="action_btn add_to_collection_btn"><span class="icon icon-playlist_add"></span>Add to collection</a></div></div><div class="game_cell_data"><div class="game_title"><a data-action="game_grid" class="title game_link" data-label="game:4861498:title" href="https://ahmed-bahaa66.itch.io/last-champion">Last Champion</a></div><div title="Hit the critical point. Survive as the Last Champion" class="game_text">Hit the critical point. Survive as the Last Champion</div><div class="game_author"><a data-action="game_grid" data-label="user:17163766" href="https://ahmed-bahaa66.itch.io">Ahmed Bahaa</a></div><div class="game_genre">Survival</div><div class="game_platform"><span title="Download for Windows" aria-hidden="true" class="icon icon-windows8"></span> </div></div></div>
        <div dir="auto" class="game_cell has_cover lazy_images" data-game_id="4256044"><div class="game_thumb" style="background-color:#eeeeee;"><a class="thumb_link game_link" tabindex="-1" href="https://birdbox774.itch.io/logic-rift" data-label="game:4256044:thumb" data-action="game_grid"><img class="lazy_loaded" width="315" height="250" data-lazy_src="https://img.itch.zone/aW1nLzI1MzU1ODU2LnBuZw==/315x250%23c/xiABBz.png"/></a><div class="game_cell_tools"><a data-register_action="add_to_collection" href="/g/birdbox774/logic-rift/add-to-collection" class="action_btn add_to_collection_btn"><span class="icon icon-playlist_add"></span>Add to collection</a></div></div><div class="game_cell_data"><div class="game_title"><a data-action="game_grid" class="title game_link" data-label="game:4256044:title" href="https://birdbox774.itch.io/logic-rift">Logic Rift</a></div><div title="watch full walkthrough : https://youtu.be/85j-vByF3Ko" class="game_text">watch full walkthrough : https://youtu.be/85j-vByF3Ko</div><div class="game_author"><a data-action="game_grid" data-label="user:15055750" href="https://birdbox774.itch.io">Birdbox774</a></div><div class="game_genre">Puzzle</div><div class="game_platform"><span class="web_flag">Play in browser</span></div></div></div>
        </div>
        """;

    [Fact]
    public void ReadsEveryGameOnThePage()
    {
        var games = ItchProfileCatalogService.ParseProfilePage(ProfileHtml, "birdbox774");

        Assert.Equal(2, games.Count);
    }

    [Fact]
    public void ReadsTheFieldsTheUiShows()
    {
        var game = ItchProfileCatalogService.ParseProfilePage(ProfileHtml, "birdbox774")
            .Single(g => g.Id == "last-champion");

        Assert.Equal(4861498, game.ItchGameId);
        Assert.Equal("Last Champion", game.Title);
        Assert.Equal("ahmed-bahaa66", game.Owner);
        Assert.Equal("Ahmed Bahaa", game.Author);
        Assert.Equal("https://img.itch.zone/aW1nLzI5MDQ0ODcxLnBuZw==/315x250%23c/NK6WEm.png", game.CoverImageUrl);
        Assert.Equal("Hit the critical point. Survive as the Last Champion", game.Description);
        Assert.Equal("Survival", game.Genre);
        Assert.Equal(new[] { "windows" }, game.Platforms);
        Assert.True(game.IsDownloadable);
        Assert.False(game.IsBrowserOnly);
    }

    /// <summary>
    /// Four of the six games on the profile show no platform icon. That does not make them
    /// browser-only — see <see cref="ItchGamePageParsingTests"/>.
    /// </summary>
    [Fact]
    public void MarksWebOnlyGamesAsBrowserOnly()
    {
        var game = ItchProfileCatalogService.ParseProfilePage(ProfileHtml, "birdbox774")
            .Single(g => g.Id == "logic-rift");

        Assert.Empty(game.Platforms);
        Assert.True(game.HasWebBuild);

        // The grid alone cannot decide this: logic-rift really does have a 44 MB zip upload.
        Assert.False(game.UploadsChecked);
        Assert.False(game.IsBrowserOnly);
        Assert.Equal("Puzzle", game.Genre);
    }

    /// <summary>
    /// The reason the library is built from the profile page rather than the API: /profile/games
    /// only returns games the account owns, so a game hosted under a collaborator's account was
    /// missing entirely.
    /// </summary>
    [Fact]
    public void IncludesGamesHostedUnderOtherAccountsAsCollaborations()
    {
        var games = ItchProfileCatalogService.ParseProfilePage(ProfileHtml, "birdbox774");

        Assert.True(games.Single(g => g.Id == "last-champion").IsCollaboration);
        Assert.False(games.Single(g => g.Id == "logic-rift").IsCollaboration);
    }

    /// <summary>Attribute order on the cell div is not contractual, so both orders must parse.</summary>
    [Fact]
    public void ToleratesEitherAttributeOrderOnTheCellDiv()
    {
        const string legacyOrder = """
            <div dir="auto" data-game_id="99" class="game_cell has_cover lazy_images">
              <div class="game_cell_data">
                <div class="game_title"><a class="title game_link" href="https://birdbox774.itch.io/g">G</a></div>
              </div>
            </div>
            """;

        var game = Assert.Single(ItchProfileCatalogService.ParseProfilePage(legacyOrder, "birdbox774"));

        Assert.Equal("g", game.Id);
        Assert.Equal(99, game.ItchGameId);
    }

    /// <summary>A profile can show the same game in a carousel and again in the grid below it.</summary>
    [Fact]
    public void DoesNotListTheSameGameTwice()
    {
        var games = ItchProfileCatalogService.ParseProfilePage(ProfileHtml + ProfileHtml, "birdbox774");

        Assert.Equal(2, games.Count);
    }

    [Theory]
    [InlineData("https://falcon-eye.itch.io/what-can-possibly-go-pong", "falcon-eye", "what-can-possibly-go-pong")]
    [InlineData("https://birdbox774.itch.io/logic-rift", "birdbox774", "logic-rift")]
    [InlineData("https://itch.io/jam/some-jam", "", "some-jam")]
    public void SplitsOwnerAndSlugFromPageUrls(string url, string owner, string slug)
    {
        var (actualOwner, actualSlug) = ItchProfileCatalogService.SplitItchUrl(url);

        Assert.Equal(owner, actualOwner);
        Assert.Equal(slug, actualSlug);
    }

    [Fact]
    public void IgnoresMarkupWithNoGameCells()
    {
        Assert.Empty(ItchProfileCatalogService.ParseProfilePage("<html><body>nothing here</body></html>", "x"));
    }

    /// <summary>The nested game_cell_data and game_cell_tools divs must not read as extra games.</summary>
    [Fact]
    public void DoesNotMistakeNestedCellDivsForGames()
    {
        Assert.Empty(ItchProfileCatalogService.ParseProfilePage(
            """<div class="game_cell_data"><div class="game_cell_tools">x</div></div>""", "x"));
    }
}

/// <summary>
/// Covers the correction that matters most: a game with a plain zip upload has no platform icon
/// on the profile grid, because itch only shows icons for uploads whose platform traits were
/// ticked. Judging "browser only" from the grid alone marked three downloadable games unplayable.
/// </summary>
public class ItchGamePageParsingTests
{
    // Verbatim from https://birdbox774.itch.io/logic-rift.
    private const string WithDownload = """
        <div class="uploads"><h2 id="download">Download</h2><div id="upload_list_8797363" class="upload_list_widget base_widget"><div class="upload"><a data-upload_id="16350683" class="button download_btn" href="javascript:void(0);">Download</a><div class="info_column"><div class="upload_name"><strong title="MackarelOnPlateFinalLogicRift.zip" class="name">MackarelOnPlateFinalLogicRift.zip</strong> <span class="file_size"><span>44 MB</span></span> <span class="download_platforms"></span></div></div></div></div></div>
        """;

    [Fact]
    public void FindsAZipUploadWithNoPlatformTraits()
    {
        var (hasDownload, name, size) = ItchProfileCatalogService.ParseGamePage(WithDownload);

        Assert.True(hasDownload);
        Assert.Equal("MackarelOnPlateFinalLogicRift.zip", name);
        Assert.Equal("44 MB", size);
    }

    [Fact]
    public void ReportsNoDownloadForAWebOnlyPage()
    {
        var (hasDownload, name, size) = ItchProfileCatalogService.ParseGamePage(
            """<div class="html_embed_widget"><iframe src="https://html-classic.itch.zone/x"></iframe></div>""");

        Assert.False(hasDownload);
        Assert.Equal("", name);
        Assert.Equal("", size);
    }

    [Fact]
    public void BrowserOnlyIsUnknownUntilTheGamePageIsRead()
    {
        // web_flag on the grid, page not yet probed: not enough to call it browser-only.
        var game = new GameInfo { Id = "g", HasWebBuild = true };

        Assert.False(game.IsBrowserOnly);

        game.UploadsChecked = true;
        Assert.True(game.IsBrowserOnly);

        game.HasItchDownload = true;
        Assert.False(game.IsBrowserOnly);
        Assert.True(game.IsDownloadable);
    }
}

public class WharfStatusParsingTests
{
    [Fact]
    public void ReadsChannelHeadBuildAndSize()
    {
        const string stdout = """
            {"type":"log","message":"opening db"}
            not json at all
            {"type":"result","value":{"target":"birdbox774/logic-rift","channels":[{"name":"windows","head":{"id":998877,"userVersion":"1.2.0","files":[{"size":1048576},{"size":2097152}]}}]}}
            """;

        var channels = WharfContentSource.ExtractChannels(stdout);

        var channel = Assert.Single(channels);
        Assert.Equal("windows", channel.Name);
        Assert.Equal("998877", channel.BuildId);
        Assert.Equal("1.2.0", channel.Version);
        Assert.Equal(3145728, channel.TotalBytes);
    }

    /// <summary>
    /// What every one of the profile's games currently returns: authenticated fine, but never
    /// pushed, so there is no channel and nothing to patch against.
    /// </summary>
    [Fact]
    public void ReportsNoChannelsForAGameThatWasNeverPushed()
    {
        const string stdout = """{"type":"result","value":{"target":"birdbox774/logic-rift","channels":[]}}""";

        Assert.Empty(WharfContentSource.ExtractChannels(stdout));
    }

    [Fact]
    public void SurvivesUnexpectedOutput()
    {
        Assert.Empty(WharfContentSource.ExtractChannels(""));
        Assert.Empty(WharfContentSource.ExtractChannels("{\"type\":\"progress\",\"progress\":0.5}"));
    }

    /// <summary>
    /// Verbatim from a real `butler --json status birdbox774/logic-rift --show-all-files` against
    /// a pushed build. The point of keeping it: the head object has **no files array**, so there is
    /// no size to sum and the size has to come from somewhere else.
    /// </summary>
    [Fact]
    public void RealStatusOutputCarriesNoSize()
    {
        const string stdout = """
            {"time":1788362075,"type":"result","value":{"channels":[{"head":{"createdAt":"2026-09-02T15:05:34Z","id":1939422,"state":"completed","updatedAt":"2026-09-02T15:06:13Z","userVersion":"1.0.0","version":1},"name":"windows","tags":"","uploadId":19073617}],"target":"birdbox774/logic-rift"}}
            """;

        var channel = Assert.Single(WharfContentSource.ExtractChannels(stdout));

        Assert.Equal("windows", channel.Name);
        Assert.Equal("1939422", channel.BuildId);
        Assert.Equal("1.0.0", channel.Version);

        // Not a parsing failure — butler simply does not report it here.
        Assert.Equal(0, channel.TotalBytes);
    }
}

/// <summary>
/// The size shown before a game is installed. It comes from itch.io's wharf API because
/// `butler status` does not report it — see <see cref="WharfStatusParsingTests.RealStatusOutputCarriesNoSize"/>.
/// </summary>
public class WharfUploadSizeTests
{
    // Verbatim from https://itch.io/api/1/{key}/wharf/channels?target=birdbox774/logic-rift,
    // truncated after the fields that matter. Note channels is an object here, not an array.
    private const string ChannelsJson = """
        {"channels":{"windows":{"head":{"id":1939422,"user_id":15055750,"version":1,"state":"completed","game_id":4256044,"upload_id":19073617,"parent_build_id":-1,"user_version":"1.0.0"},"upload":{"p_linux":false,"demo":false,"preorder":false,"id":19073617,"size":47837609,"channel_name":"windows","build_id":1939422,"type":"default","game_id":4256044,"filename":"logic-rift-windows.zip","storage":"build","p_windows":true,"p_osx":false},"name":"windows"}}}
        """;

    [Fact]
    public void ReadsTheDownloadSizeForTheRequestedChannel()
    {
        Assert.Equal(47837609, WharfContentSource.ExtractUploadSize(ChannelsJson, "windows"));
    }

    /// <summary>A single channel under an unexpected name is still unambiguous.</summary>
    [Fact]
    public void FallsBackToTheOnlyChannelWhenTheNameDiffers()
    {
        Assert.Equal(47837609, WharfContentSource.ExtractUploadSize(ChannelsJson, "win64"));
    }

    [Fact]
    public void ReturnsNullRatherThanZeroWhenThereIsNothingToRead()
    {
        Assert.Null(WharfContentSource.ExtractUploadSize("", "windows"));
        Assert.Null(WharfContentSource.ExtractUploadSize("not json", "windows"));
        Assert.Null(WharfContentSource.ExtractUploadSize("""{"channels":{}}""", "windows"));
        Assert.Null(WharfContentSource.ExtractUploadSize(
            """{"channels":{"windows":{"upload":{"size":0}}}}""", "windows"));
    }

    /// <summary>
    /// Two channels and neither matches: guessing would show one game's size for another.
    /// </summary>
    [Fact]
    public void RefusesToGuessBetweenSeveralChannels()
    {
        Assert.Null(WharfContentSource.ExtractUploadSize(
            """{"channels":{"windows":{"upload":{"size":100}},"linux":{"upload":{"size":200}}}}""",
            "osx"));
    }
}

/// <summary>
/// What the UI shows when a size is not known, and which size it is showing. A published build
/// whose size could not be read displayed "0 MB download", which looked like a broken upload.
/// </summary>
public class SizeDisplayTests
{
    [Fact]
    public void SaysUnknownRatherThanZeroBeforeInstalling()
    {
        var item = new GameItemViewModel(new GameInfo { Id = "g", Title = "G", Channel = "windows" })
        {
            Status = new GameStatus(
                LauncherState.NotInstalled,
                new RemoteVersionInfo("1939422", "1.0.0", TotalBytes: 0, PatchBytes: 0, DeltaAvailable: false),
                "wharf")
        };

        Assert.Equal("—", item.SizeText);
        Assert.Equal("Not installed", item.StatusText);
    }

    [Fact]
    public void QuotesTheDownloadSizeOnceItIsKnown()
    {
        var item = new GameItemViewModel(new GameInfo { Id = "g", Title = "G", Channel = "windows" })
        {
            Status = new GameStatus(
                LauncherState.NotInstalled,
                new RemoteVersionInfo("1939422", "1.0.0", 47837609, 47837609, false),
                "wharf")
        };

        Assert.Equal("45.6 MB", item.SizeText);
        Assert.Equal("DOWNLOAD", item.SizeLabel);
        Assert.Equal("Not installed  ·  45.6 MB download", item.StatusText);
    }

    /// <summary>
    /// The compressed download and the unpacked folder are different numbers, both correct. The
    /// label has to change or the jump looks like a bug.
    /// </summary>
    [Fact]
    public void SwitchesToOnDiskSizeAfterInstalling()
    {
        var item = new GameItemViewModel(new GameInfo
        {
            Id = "g",
            Title = "G",
            InstalledBuildId = "1939422",
            InstalledSizeBytes = 126L * 1024 * 1024
        });

        Assert.Equal("ON DISK", item.SizeLabel);
        Assert.Equal("126.0 MB", item.SizeText);
    }
}
