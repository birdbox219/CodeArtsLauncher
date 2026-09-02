using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Engine.Butler.Rpc;
using Microsoft.Extensions.Logging;

namespace Launcher.Engine.Butler.Process;

public class ButlerDaemonManager : IAsyncDisposable
{
    private System.Diagnostics.Process? _process;
    private ButlerRpcClient? _rpcClient;
    private readonly ILogger? _logger;

    public ButlerDaemonManager(ILogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<ButlerRpcClient> StartDaemonAsync(
        string butlerPath,
        string dbPath,
        CancellationToken ct = default
    )
    {
        if (!File.Exists(butlerPath))
        {
            throw new FileNotFoundException($"butler.exe was not found at '{butlerPath}'", butlerPath);
        }

        string? dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
        {
            Directory.CreateDirectory(dbDir);
        }

        int myPid = Environment.ProcessId;
        string args = $"--json daemon --transport=tcp --destiny-pid {myPid} --dbpath \"{dbPath}\"";

        _logger?.LogInformation("Starting butler daemon: {ButlerPath} {Args}", butlerPath, args);

        var startInfo = new ProcessStartInfo
        {
            FileName = butlerPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start butler.exe process.");

        string? handshakeSecret = null;
        string? tcpAddress = null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        // Read lines until finding "butlerd/listen-notification"
        while (!cts.Token.IsCancellationRequested)
        {
            string? line = await _process.StandardOutput.ReadLineAsync(cts.Token);
            if (line == null)
            {
                string stderr = await _process.StandardError.ReadToEndAsync(CancellationToken.None);
                throw new InvalidOperationException($"Butler process terminated unexpectedly. Stderr: {stderr}");
            }

            _logger?.LogDebug("Butler init line: {Line}", line);

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var typeProp) &&
                    typeProp.GetString() == "butlerd/listen-notification")
                {
                    handshakeSecret = root.GetProperty("secret").GetString();
                    tcpAddress = root.GetProperty("tcp").GetProperty("address").GetString();
                    break;
                }
            }
            catch (JsonException)
            {
                // Ignore non-json or formatting log lines
            }
        }

        if (string.IsNullOrEmpty(handshakeSecret) || string.IsNullOrEmpty(tcpAddress))
        {
            throw new InvalidOperationException("Failed to obtain handshake authentication details from butler daemon.");
        }

        _logger?.LogInformation("Butler daemon listening at {Address}", tcpAddress);

        var parts = tcpAddress.Split(':');
        string host = parts[0];
        int port = int.Parse(parts[1]);

        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host, port, ct);

        _rpcClient = new ButlerRpcClient(tcpClient, _logger);
        await _rpcClient.AuthenticateAsync(handshakeSecret, ct);

        return _rpcClient;
    }

    public async ValueTask DisposeAsync()
    {
        if (_rpcClient != null)
        {
            await _rpcClient.DisposeAsync();
            _rpcClient = null;
        }

        if (_process != null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch
            {
                // Process already exiting
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
    }
}
