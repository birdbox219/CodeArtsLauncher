using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Core.Models;
using Launcher.Engine.Butler;
using Launcher.Engine.Butler.Process;
using Xunit;

namespace Launcher.Core.Tests;

public class ButlerDaemonIntegrationTests
{
    [Fact]
    public async Task StartDaemonAsync_SpawnsButlerAndAuthenticatesSuccessfully()
    {
        // Resolve project root tools/butler/butler.exe
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        // From bin/Debug/net9.0/ up to project root
        string projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        string butlerPath = Path.Combine(projectRoot, "tools", "butler", "butler.exe");

        if (!File.Exists(butlerPath))
        {
            // Fallback check current directory
            butlerPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "butler", "butler.exe"));
        }

        Assert.True(File.Exists(butlerPath), $"Butler binary not found at {butlerPath}");

        string dbPath = Path.Combine(projectRoot, ".cache", "test_daemon.db");

        await using var manager = new ButlerDaemonManager();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var rpcClient = await manager.StartDaemonAsync(butlerPath, dbPath, cts.Token);
        Assert.NotNull(rpcClient);

        // Test sending a lightweight RPC check (Version.Get)
        var versionResult = await rpcClient.SendRequestAsync("Version.Get", null, cts.Token);
        Assert.NotNull(versionResult);

        // Clean up db file afterwards
        try
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
        catch { }
    }

    /// <summary>
    /// End-to-end size check against the live channel, because the unit tests can only prove the
    /// parsing. `butler status` reports no size at all, so this is the test that would have caught
    /// the launcher showing "0 MB download" for a published 45.6 MB build.
    ///
    /// Needs network and a butler login; excluded from the default run with the rest of this class.
    /// </summary>
    [Fact]
    public async Task GetRemoteVersion_ReportsARealDownloadSizeForAPushedGame()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        string butlerPath = Path.Combine(projectRoot, "tools", "butler", "butler.exe");

        Assert.True(File.Exists(butlerPath), $"Butler binary not found at {butlerPath}");

        await using var source = new WharfContentSource(
            butlerPath,
            Path.Combine(projectRoot, ".cache", "test_size.db"),
            ReadItchApiKey);

        var game = new GameInfo
        {
            Id = "logic-rift",
            Owner = "birdbox774",
            ItchGameId = 4256044,
            Channel = "windows"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var remote = await source.GetRemoteVersionAsync(game, cts.Token);

        Assert.NotNull(remote);
        Assert.NotEqual("", remote!.BuildId);
        Assert.True(remote.TotalBytes > 0, "The download size came back 0, so the UI would show 0 MB.");
    }

    /// <summary>Same lookup order the app uses: environment first, then the local butler login.</summary>
    private static string? ReadItchApiKey()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("ITCH_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

        string creds = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "itch", "butler_creds");

        return File.Exists(creds) ? File.ReadAllText(creds).Trim() : null;
    }
}
