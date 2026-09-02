using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Launcher.Engine.Butler.Rpc;

/// <summary>
/// Newline-delimited JSON-RPC 2.0 client for butlerd.
///
/// butlerd is bidirectional: besides responses and notifications it sends *requests* to the
/// client (both "id" and "method" set) for things like picking an upload or confirming an
/// action. Those must be answered or the daemon blocks forever waiting, stalling the install.
/// Unhandled server requests get an explicit method-not-found reply rather than silence.
/// </summary>
public class ButlerRpcClient : IAsyncDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _listenTask;

    private int _nextId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();

    /// <summary>Fired for server notifications (no id): Progress, Log, TaskStarted, and friends.</summary>
    public event Action<string, JsonNode?>? NotificationReceived;

    /// <summary>
    /// Fired for server-initiated requests. Return a result node to answer, or null to decline
    /// (which sends a method-not-found error back).
    /// </summary>
    public Func<string, JsonNode?, JsonNode?>? RequestHandler;

    public ButlerRpcClient(TcpClient tcpClient, ILogger? logger = null)
    {
        _tcpClient = tcpClient;
        _logger = logger;
        _stream = _tcpClient.GetStream();
        _reader = new StreamReader(_stream, new UTF8Encoding(false));
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true };
        _listenTask = Task.Run(ListenLoopAsync);
    }

    public Task AuthenticateAsync(string secret, CancellationToken ct = default) =>
        SendRequestAsync("Meta.Authenticate", new { secret }, ct);

    public async Task<JsonNode?> SendRequestAsync(
        string method,
        object? parameters = null,
        CancellationToken ct = default)
    {
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var removed)) removed.TrySetCanceled(ct);
        });

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters is null ? new JsonObject() : JsonSerializer.SerializeToNode(parameters)
        };

        _logger?.LogDebug("butlerd -> [{Id}] {Method}", id, method);
        await WriteLineAsync(payload.ToJsonString(), ct);

        return await tcs.Task;
    }

    public async Task<T?> SendRequestAsync<T>(
        string method,
        object? parameters = null,
        CancellationToken ct = default)
    {
        var node = await SendRequestAsync(method, parameters, ct);
        return node is null ? default : node.Deserialize<T>();
    }

    private async Task WriteLineAsync(string json, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ListenLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                string? line = await _reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonObject? obj;
                try
                {
                    obj = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException ex)
                {
                    _logger?.LogWarning(ex, "Unparseable line from butlerd: {Line}", Truncate(line));
                    continue;
                }

                if (obj is null) continue;

                bool hasId = obj.TryGetPropertyValue("id", out var idNode) && idNode is not null;
                bool hasMethod = obj.TryGetPropertyValue("method", out var methodNode) && methodNode is not null;

                // Server-initiated request: must be answered.
                if (hasId && hasMethod)
                {
                    await HandleServerRequestAsync(obj, idNode!, methodNode!.GetValue<string>());
                    continue;
                }

                // Response to one of ours.
                if (hasId)
                {
                    CompletePending(obj, idNode!);
                    continue;
                }

                // Notification.
                if (hasMethod)
                {
                    obj.TryGetPropertyValue("params", out var paramsNode);
                    try
                    {
                        NotificationReceived?.Invoke(methodNode!.GetValue<string>(), paramsNode);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Notification handler threw.");
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "butlerd listen loop failed.");
            FailAllPending(ex);
        }
        finally
        {
            FailAllPending(new IOException("butlerd connection closed."));
        }
    }

    private void CompletePending(JsonObject obj, JsonNode idNode)
    {
        if (idNode.GetValue<int?>() is not int id) return;
        if (!_pending.TryRemove(id, out var tcs)) return;

        if (obj.TryGetPropertyValue("error", out var errorNode) && errorNode is not null)
        {
            string message = errorNode is JsonObject eo && eo.TryGetPropertyValue("message", out var m)
                ? m?.GetValue<string>() ?? errorNode.ToJsonString()
                : errorNode.ToJsonString();

            _logger?.LogError("butlerd error [{Id}]: {Error}", id, Truncate(message));
            tcs.TrySetException(new ButlerRpcException(message));
            return;
        }

        obj.TryGetPropertyValue("result", out var resultNode);
        tcs.TrySetResult(resultNode);
    }

    private async Task HandleServerRequestAsync(JsonObject obj, JsonNode idNode, string method)
    {
        obj.TryGetPropertyValue("params", out var paramsNode);
        _logger?.LogDebug("butlerd <- request {Method}", method);

        JsonNode? result = null;
        try
        {
            result = RequestHandler?.Invoke(method, paramsNode);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Request handler for {Method} threw.", method);
        }

        var reply = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNode.DeepClone()
        };

        if (result is not null)
        {
            reply["result"] = result;
        }
        else
        {
            reply["error"] = new JsonObject
            {
                ["code"] = -32601,
                ["message"] = $"Launcher does not handle server request '{method}'."
            };
        }

        try
        {
            await WriteLineAsync(reply.ToJsonString(), _cts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not reply to butlerd request {Method}.", method);
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(ex);
        }
    }

    private static string Truncate(string s, int max = 400) =>
        s.Length <= max ? s : s[..max] + "…";

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try { await _listenTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }

        try { _writer.Dispose(); } catch { }
        try { _reader.Dispose(); } catch { }
        try { await _stream.DisposeAsync(); } catch { }
        _tcpClient.Dispose();
        _writeLock.Dispose();
        _cts.Dispose();
    }
}

public class ButlerRpcException : Exception
{
    public ButlerRpcException(string message) : base(message) { }
}
