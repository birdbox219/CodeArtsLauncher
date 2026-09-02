using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Threading.Tasks;
using Launcher.Publisher.Publishing;
using Xunit;

namespace Launcher.Core.Tests;

/// <summary>
/// Target resolution for the publishing panel. Owner is per-game because two of the six games are
/// collaborations hosted under other accounts, so a single global username would push them to the
/// wrong place — or fail.
/// </summary>
public class PublishTargetTests
{
    [Fact]
    public void FillsOwnerAndChannelFromTheDefaults()
    {
        var target = PublishTarget.Parse("logic-rift", "birdbox774", "windows");

        Assert.Equal("birdbox774/logic-rift:windows", target.Full);
        Assert.Equal("birdbox774/logic-rift", target.GameTarget);
    }

    [Fact]
    public void KeepsAnotherAccountsOwnerForACollaboration()
    {
        var target = PublishTarget.Parse("falcon-eye/what-can-possibly-go-pong", "birdbox774", "windows");

        Assert.Equal("falcon-eye", target.Owner);
        Assert.Equal("falcon-eye/what-can-possibly-go-pong:windows", target.Full);
    }

    [Fact]
    public void AFullTargetOverridesBothDefaults()
    {
        var target = PublishTarget.Parse("ahmed-bahaa66/last-champion:linux", "birdbox774", "windows");

        Assert.Equal("ahmed-bahaa66", target.Owner);
        Assert.Equal("last-champion", target.Slug);
        Assert.Equal("linux", target.Channel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("birdbox774/")]
    [InlineData("logic-rift:")]
    public void RefusesSomethingItCannotTurnIntoATarget(string input)
    {
        Assert.Throws<ArgumentException>(() => PublishTarget.Parse(input, "birdbox774", "windows"));
    }
}

/// <summary>
/// Parsing butler's push output. Every fixture here is copied verbatim from a real
/// `butler --json push` run — a hand-written fixture once certified a parser that found nothing.
/// </summary>
public class PushOutputTests
{
    // Verbatim from `butler --json push .cache\pushprobe birdbox774/logic-rift:windows --dry-run`.
    private const string RealLogLine =
        """{"level":"info","message":"∙ Dry run, listing files we would push...","time":1788363515,"type":"log"}""";

    private const string RealResultLine =
        """{"time":1788363515,"type":"result","value":{"buildId":0,"channel":"windows","dryRun":true,"reason":"dry-run","skipped":false}}""";

    [Fact]
    public void ReadsAButlerLogLine()
    {
        var e = ButlerPublishService.ParseEvent(JsonNode.Parse(RealLogLine));

        Assert.NotNull(e);
        Assert.Equal("log", e!.Kind);
        Assert.Equal("∙ Dry run, listing files we would push...", e.Message);
        Assert.Null(e.Progress);
    }

    [Fact]
    public void AWarningKeepsItsLevel()
    {
        var e = ButlerPublishService.ParseEvent(JsonNode.Parse(
            """{"level":"warn","message":"something is off","type":"log"}"""));

        Assert.Equal("warning", e!.Kind);
    }

    [Fact]
    public void ReadsProgressAsAFraction()
    {
        var e = ButlerPublishService.ParseEvent(JsonNode.Parse(
            """{"type":"progress","progress":0.42,"bps":1048576,"eta":12.5}"""));

        Assert.Equal("progress", e!.Kind);
        Assert.Equal(0.42, e.Progress!.Value, 3);
        Assert.Equal(1048576, e.Bps!.Value);
        Assert.Equal(12.5, e.EtaSeconds!.Value, 3);
    }

    [Fact]
    public void AcceptsTheOlderZeroToHundredProgress()
    {
        var e = ButlerPublishService.ParseEvent(JsonNode.Parse("""{"type":"progress","progress":42}"""));

        Assert.Equal(0.42, e!.Progress!.Value, 3);
    }

    [Fact]
    public void TheResultLineIsNotShownAsALogLine()
    {
        // It carries the build id, which is reported through the outcome instead.
        Assert.Null(ButlerPublishService.ParseEvent(JsonNode.Parse(RealResultLine)));
    }

    [Fact]
    public void ReadsTheReuseFigureThatIsTheWholePoint()
    {
        var delta = ButlerPublishService.ParseDelta("√ Re-used 97.85% of old, added 1.1 MB fresh data");

        Assert.NotNull(delta);
        Assert.Equal(97.85, delta!.ReusedPercent, 2);
        Assert.Equal("1.1 MB", delta.FreshData);
    }

    [Fact]
    public void ReadsTheReuseFigureWithoutTheFreshDataClause()
    {
        var delta = ButlerPublishService.ParseDelta("Re-used 100.00% of old");

        Assert.Equal(100, delta!.ReusedPercent, 2);
        Assert.Equal("", delta.FreshData);
    }

    [Theory]
    [InlineData("∙ Pushing 44 MB (312 files, 0 dirs, 0 symlinks)")]
    [InlineData("For channel `windows`: last build is 1.0.0, downloading its signature")]
    [InlineData("")]
    public void OrdinaryLinesCarryNoDelta(string line)
    {
        Assert.Null(ButlerPublishService.ParseDelta(line));
    }
}

/// <summary>
/// The folder checks that run before an upload. They exist because butler will happily push a zip
/// or a folder with no executable, and the mistake only surfaces later in the launcher.
/// </summary>
public class BuildFolderInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bbpanel_" + Guid.NewGuid().ToString("n")[..8]);

    public BuildFolderInspectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string File_(string name, int bytes = 16)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void AcceptsAFolderAndPicksTheLikelyExecutable()
    {
        File_("LogicRift.exe", 4096);
        File_("UnityCrashHandler64.exe", 8192);   // bigger, but never the game
        File_("data/resources.assets", 2048);

        var report = BuildFolderInspector.Inspect(_root);

        Assert.True(report.CanPush);
        Assert.Equal(3, report.FileCount);
        Assert.Equal(4096 + 8192 + 2048, report.TotalBytes);
        Assert.Equal("LogicRift.exe", report.Executable);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void RefusesAFileAndSaysWhyAZipCannotBePatched()
    {
        string zip = File_("Build.zip");

        var report = BuildFolderInspector.Inspect(zip);

        Assert.False(report.CanPush);
        Assert.Contains("not a folder", string.Join(" ", report.Problems));
        Assert.Contains("full download", string.Join(" ", report.Problems));
    }

    [Fact]
    public void RefusesAnEmptyFolder()
    {
        var report = BuildFolderInspector.Inspect(_root);

        Assert.False(report.CanPush);
        Assert.Contains("empty", string.Join(" ", report.Problems));
    }

    [Fact]
    public void RefusesAPathThatIsNotThere()
    {
        var report = BuildFolderInspector.Inspect(Path.Combine(_root, "nope"));

        Assert.False(report.CanPush);
        Assert.NotEmpty(report.Problems);
    }

    [Fact]
    public void RefusesNothingAtAll()
    {
        Assert.False(BuildFolderInspector.Inspect(null).CanPush);
        Assert.False(BuildFolderInspector.Inspect("  ").CanPush);
    }

    [Fact]
    public void WarnsWhenThereIsNoExecutableToRunAfterInstalling()
    {
        File_("game.pck");

        var report = BuildFolderInspector.Inspect(_root);

        Assert.True(report.CanPush);
        Assert.Contains("No .exe", string.Join(" ", report.Warnings));
    }

    [Fact]
    public void WarnsWhenTheFolderIsJustAZip()
    {
        File_("Build.zip", 1024);

        var report = BuildFolderInspector.Inspect(_root);

        // Pushable, but pointless: one blob that changes wholesale on every rebuild.
        Assert.True(report.CanPush);
        Assert.Contains("Unpack it", string.Join(" ", report.Warnings));
    }
}

/// <summary>The event plumbing that lets the browser be reloaded in the middle of an upload.</summary>
public class PushJobTests
{
    private static PushRequest Request() =>
        new("birdbox774", "logic-rift", "windows", @"D:\builds\LogicRift", "1.0.1", false, true);

    [Fact]
    public async Task ReplaysWhatAlreadyHappenedThenStreamsTheRest()
    {
        var job = new PushJob("j1", Request());
        job.Append(PushEvent.Log("first"));

        var (backlog, live) = job.Subscribe();
        job.Append(PushEvent.Log("second"));
        job.Complete(new PushOutcome(true, false, false, 1939423, new PushDelta(97.85, "1.1 MB"), null));

        Assert.Equal(new[] { "first" }, backlog.Select(e => e.Message));

        var streamed = new List<string>();
        await foreach (var e in live.ReadAllAsync()) streamed.Add(e.Message);

        Assert.Equal(new[] { "second" }, streamed);
        Assert.True(job.Completed);
        Assert.Equal(1939423, job.Outcome!.BuildId);
    }

    [Fact]
    public void ProgressIsKeptOnlyAsTheLatest()
    {
        var job = new PushJob("j2", Request());

        job.Append(PushEvent.Log("pushing"));
        job.Append(new PushEvent("progress", "", 0.1));
        job.Append(new PushEvent("progress", "", 0.9));

        // Progress arrives many times a second; replaying every tick would bury the actual log.
        Assert.Equal(new[] { "pushing" }, job.Events.Select(e => e.Message));
        Assert.Equal(0.9, job.LastProgress!.Progress!.Value, 3);
    }

    [Fact]
    public async Task SubscribingAfterTheEndDoesNotHangWaitingForMore()
    {
        var job = new PushJob("j3", Request());
        job.Append(PushEvent.Log("done already"));
        job.Complete(new PushOutcome(true, true, false, 0, null, null));

        var (backlog, live) = job.Subscribe();

        Assert.Single(backlog);

        // The stream has to end, or the browser's log would sit there waiting forever.
        var streamed = new List<PushEvent>();
        await foreach (var e in live.ReadAllAsync()) streamed.Add(e);
        Assert.Empty(streamed);
    }

    [Fact]
    public void OnlyOnePushRunsAtATime()
    {
        var store = new PushJobStore();

        Assert.True(store.TryStart(Request(), out var first, out _));
        Assert.False(store.TryStart(Request(), out var second, out string? error));

        Assert.Null(second);
        Assert.Contains("already running", error);

        // Two pushes to one channel race for the head build, so the second has to wait.
        first!.Complete(new PushOutcome(true, false, false, 1, null, null));
        Assert.True(store.TryStart(Request(), out _, out _));
    }
}
