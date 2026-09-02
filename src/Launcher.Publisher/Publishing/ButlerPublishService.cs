using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Launcher.Engine.Butler;
using Microsoft.Extensions.Logging;

namespace Launcher.Publisher.Publishing;

/// <summary>What to push. Mirrors the fields of <c>tools/push-release.ps1</c>.</summary>
public sealed record PushRequest(
    string Owner,
    string Slug,
    string Channel,
    string Folder,
    string? Version,
    bool DryRun,
    bool OnlyIfChanged);

/// <summary>
/// One line of the push, as the browser sees it. <paramref name="Progress"/> is only set on
/// events that carried a real fraction — the UI shows an indeterminate bar otherwise rather than
/// inventing a percentage.
/// </summary>
public sealed record PushEvent(
    string Kind,
    string Message,
    double? Progress = null,
    double? Bps = null,
    double? EtaSeconds = null)
{
    public static PushEvent Log(string message) => new("log", message);
    public static PushEvent Error(string message) => new("error", message);
}

/// <summary>The "Re-used 97.85% of old, added 1.1 MB fresh data" line — the delta, in butler's own words.</summary>
public sealed record PushDelta(double ReusedPercent, string FreshData);

public sealed record PushOutcome(
    bool Success,
    bool DryRun,
    bool Skipped,
    long BuildId,
    PushDelta? Delta,
    string? Error);

/// <summary>
/// Wraps <c>butler push</c> and <c>butler status</c> for the panel.
///
/// Channel parsing is deliberately delegated to <see cref="WharfContentSource.ExtractChannels"/>:
/// the launcher already depends on that parser being right, and a second copy here would be free to
/// drift from it.
/// </summary>
public sealed class ButlerPublishService
{
    private readonly ButlerCli _butler;
    private readonly ILogger<ButlerPublishService>? _logger;

    /// <summary>e.g. "Re-used 97.85% of old, added 1.1 MB fresh data" (with a leading glyph).</summary>
    private static readonly Regex ReusedLine = new(
        @"Re-used\s+(?<pct>[0-9]+(?:\.[0-9]+)?)\s*%\s+of\s+old(?:\s*,\s*added\s+(?<fresh>[0-9.]+\s*[KMGT]?i?B))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ButlerPublishService(ButlerCli butler, ILogger<ButlerPublishService>? logger = null)
    {
        _butler = butler;
        _logger = logger;
    }

    /// <summary>
    /// The channels a game already has. Empty means it has never been pushed — which is the state
    /// five of the six games are in, and the reason nothing was ever updatable.
    /// </summary>
    public async Task<(IReadOnlyList<WharfChannel> Channels, string? Error)> GetChannelsAsync(
        string gameTarget, CancellationToken ct)
    {
        try
        {
            var run = await _butler.RunAsync(new[] { "--json", "status", gameTarget }, ct);

            if (run.ExitCode != 0)
            {
                // "no channel" is not a failure, it is the answer for an unpushed game.
                string text = (run.Stderr + run.Stdout).ToLowerInvariant();
                if (text.Contains("no channel") || text.Contains("no build"))
                    return (Array.Empty<WharfChannel>(), null);

                return (Array.Empty<WharfChannel>(), LastLine(run.Stderr, run.Stdout));
            }

            return (WharfContentSource.ExtractChannels(run.Stdout), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (Array.Empty<WharfChannel>(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<PushOutcome> PushAsync(
        PushRequest request,
        Action<PushEvent> emit,
        CancellationToken ct)
    {
        var target = new PublishTarget(request.Owner, request.Slug, request.Channel);

        var report = BuildFolderInspector.Inspect(request.Folder);
        if (!report.CanPush)
        {
            string why = string.Join(" ", report.Problems);
            emit(PushEvent.Error(why));
            return new PushOutcome(false, request.DryRun, false, 0, null, why);
        }

        foreach (var warning in report.Warnings)
            emit(new PushEvent("warning", warning));

        emit(PushEvent.Log($"Target   {target.Full}"));
        emit(PushEvent.Log($"Folder   {report.Path}"));
        emit(PushEvent.Log(
            $"Contents {report.FileCount} files, {FormatBytes(report.TotalBytes)}" +
            (report.Executable is null ? "" : $", exe {report.Executable}")));
        if (!string.IsNullOrWhiteSpace(request.Version))
            emit(PushEvent.Log($"Version  {request.Version!.Trim()}"));

        var args = new List<string> { "--json", "push", report.Path, target.Full };
        if (!string.IsNullOrWhiteSpace(request.Version))
            args.AddRange(new[] { "--userversion", request.Version!.Trim() });
        if (request.OnlyIfChanged) args.Add("--if-changed");
        if (request.DryRun) args.Add("--dry-run");

        long buildId = 0;
        bool skipped = false;
        PushDelta? delta = null;

        ButlerRun run;
        try
        {
            run = await _butler.RunAsync(args, ct,
                onJson: node =>
                {
                    var parsed = ParseEvent(node);
                    if (parsed is not null) emit(parsed);

                    // Verified shapes (real `butler --json push --dry-run`):
                    //   {"level":"info","message":"...","time":...,"type":"log"}
                    //   {"type":"result","value":{"buildId":0,"channel":"windows","dryRun":true,
                    //                             "reason":"dry-run","skipped":false}}
                    if (node is JsonObject o && (o["type"]?.GetValue<string>() ?? "") == "result")
                    {
                        buildId = o["value"]?["buildId"]?.GetValue<long?>() ?? buildId;
                        skipped = o["value"]?["skipped"]?.GetValue<bool?>() ?? skipped;
                    }

                    if (parsed?.Kind == "log" && ParseDelta(parsed.Message) is { } d) delta = d;
                },
                onNonJsonLine: line =>
                {
                    emit(PushEvent.Log(line));
                    if (ParseDelta(line) is { } d) delta = d;
                });
        }
        catch (OperationCanceledException)
        {
            emit(PushEvent.Error("Cancelled — butler was stopped."));
            return new PushOutcome(false, request.DryRun, false, 0, null, "Cancelled.");
        }
        catch (Exception ex)
        {
            string why = $"Could not run butler: {ex.Message}";
            emit(PushEvent.Error(why));
            return new PushOutcome(false, request.DryRun, false, 0, null, why);
        }

        if (run.ExitCode != 0)
        {
            string why = LastLine(run.Stderr, run.Stdout)
                         ?? $"butler push exited with code {run.ExitCode}.";

            if (why.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                why.Contains("403", StringComparison.Ordinal))
            {
                why += " For a collaboration hosted under another account you need upload rights " +
                       "on that game — otherwise the owner has to push it.";
            }

            emit(PushEvent.Error(why));
            return new PushOutcome(false, request.DryRun, false, 0, null, why);
        }

        if (!request.DryRun && !skipped)
        {
            // Read the channel back, so the panel reports what itch.io actually holds rather than
            // just echoing the request.
            var (channels, error) = await GetChannelsAsync(target.GameTarget, ct);
            var head = channels.FirstOrDefault(c =>
                string.Equals(c.Name, target.Channel, StringComparison.OrdinalIgnoreCase));

            if (head is not null)
            {
                emit(PushEvent.Log(
                    $"Channel `{head.Name}` is now at build {head.BuildId}" +
                    (string.IsNullOrWhiteSpace(head.Version) ? "" : $" ({head.Version})") + "."));
                if (buildId == 0 && long.TryParse(head.BuildId, out long fromStatus)) buildId = fromStatus;
            }
            else if (error is not null)
            {
                emit(new PushEvent("warning", $"Pushed, but reading the channel back failed: {error}"));
            }
        }

        return new PushOutcome(true, request.DryRun, skipped, buildId, delta, null);
    }

    /// <summary>Turns one butler JSON event into something the browser can render.</summary>
    internal static PushEvent? ParseEvent(JsonNode? node)
    {
        if (node is not JsonObject o) return null;

        switch (o["type"]?.GetValue<string>())
        {
            case "log":
            {
                string message = o["message"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(message)) return null;
                string level = o["level"]?.GetValue<string>() ?? "info";
                string kind = level is "warn" or "warning" ? "warning" : level is "error" ? "error" : "log";
                return new PushEvent(kind, message);
            }

            case "progress":
            {
                // butler reports a 0..1 fraction; older builds used 0..100.
                double raw = o["progress"]?.GetValue<double?>()
                             ?? o["percentage"]?.GetValue<double?>()
                             ?? -1;
                if (raw < 0) return null;
                double fraction = raw > 1 ? raw / 100.0 : raw;

                return new PushEvent(
                    "progress",
                    "",
                    Math.Clamp(fraction, 0, 1),
                    o["bps"]?.GetValue<double?>(),
                    o["eta"]?.GetValue<double?>());
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Pulls the re-use figure out of butler's own summary line. This is the number the whole
    /// project exists for, so it is reported verbatim and never estimated.
    /// </summary>
    internal static PushDelta? ParseDelta(string message)
    {
        var m = ReusedLine.Match(message ?? "");
        if (!m.Success) return null;

        if (!double.TryParse(m.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
            return null;

        string fresh = m.Groups["fresh"].Success ? m.Groups["fresh"].Value.Trim() : "";
        return new PushDelta(pct, fresh);
    }

    private static string? LastLine(params string[] streams)
    {
        foreach (var s in streams)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var line = s.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
        }
        return null;
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
