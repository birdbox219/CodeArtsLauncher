using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Launcher.Publisher.Publishing;

public sealed record ButlerRun(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Runs the vendored butler and hands back its JSON event lines as they arrive.
///
/// Two details that have already cost time here: <c>--json</c> is a global <em>pre-verb</em> flag
/// (<c>butler --json push ...</c>, never <c>butler push --json</c>), and cancelling has to kill the
/// process — abandoning the read loop would leave an upload running with nothing watching it.
/// </summary>
public sealed class ButlerCli
{
    private readonly string _butlerPath;
    private readonly ILogger? _logger;

    public ButlerCli(string butlerPath, ILogger? logger = null)
    {
        _butlerPath = butlerPath;
        _logger = logger;
    }

    public string ButlerPath => _butlerPath;

    public async Task<ButlerRun> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken ct,
        Action<JsonNode>? onJson = null,
        Action<string>? onNonJsonLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _butlerPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,

            // butler writes UTF-8 and prefixes its messages with ∙ / √ / ✘. Without this the
            // console code page decodes them as mojibake ("Γêÿ Dry run..."), and butler's output is
            // shown to you verbatim, so it has to be readable.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _logger?.LogInformation("butler {Args}", string.Join(' ', args));

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {_butlerPath}");

        // A cancelled push must actually stop uploading, so the token kills butler rather than
        // just walking away from its stdout.
        using var kill = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        });

        var stdout = new List<string>();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        while (await proc.StandardOutput.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            stdout.Add(line);

            JsonNode? node = null;
            try { node = JsonNode.Parse(line.Trim()); }
            catch { /* butler occasionally prints plain text even under --json */ }

            if (node is not null) onJson?.Invoke(node);
            else onNonJsonLine?.Invoke(line.TrimEnd());
        }

        await proc.WaitForExitAsync(CancellationToken.None);

        return new ButlerRun(proc.ExitCode, string.Join("\n", stdout), await stderrTask);
    }
}
