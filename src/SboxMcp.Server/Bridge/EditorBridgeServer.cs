using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SboxMcp.Server.Bridge;

/// <summary>
/// Bridge transport for the MCP server. The .NET MCP server is the WebSocket client;
/// the bridge addon inside the s&box editor is the server that accepts many clients.
/// This inversion lets multiple Claude Code sessions (each with its own MCP server
/// subprocess) drive one editor concurrently — there is no shared :29015 port to fight over.
/// Class name retained for DI/back-compat; behavior is now client-side.
/// </summary>
public sealed class EditorBridgeServer : BackgroundService
{
    private readonly ILogger<EditorBridgeServer> _logger;
    private readonly int _port;
    private ClientWebSocket? _ws;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SendCommandTimeout = TimeSpan.FromSeconds(30);

    public bool IsConnected => _ws is { State: WebSocketState.Open };
    public int Port => _port;

    public EditorBridgeServer(ILogger<EditorBridgeServer> logger)
    {
        _logger = logger;
        _port = int.TryParse(Environment.GetEnvironmentVariable("SBOX_MCP_PORT"), out var p) ? p : 29015;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loggedDownOnce = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndPumpAsync(stoppingToken);
                loggedDownOnce = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Quiet during normal "editor not open yet" polling — log first failure only,
                // then stay silent until we reconnect.
                if (!loggedDownOnce)
                {
                    _logger.LogInformation("s&box bridge unreachable ({Reason}); will retry every {Delay}s",
                        ex.Message, ReconnectDelay.TotalSeconds);
                    loggedDownOnce = true;
                }
            }
            finally
            {
                FailPending("Bridge disconnected.");
                _ws?.Dispose();
                _ws = null;
            }

            try { await Task.Delay(ReconnectDelay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectAndPumpAsync(CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();

        var uri = new Uri($"ws://localhost:{_port}/");
        await _ws.ConnectAsync(uri, ct);
        _logger.LogInformation("Connected to s&box bridge at {Uri}", uri);

        await ReceiveLoopAsync(_ws, ct);

        _logger.LogInformation("s&box bridge connection closed");
    }

    private async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                try
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (WebSocketException)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            ms.Seek(0, SeekOrigin.Begin);
            BridgeResponse? response;
            try
            {
                response = await JsonSerializer.DeserializeAsync<BridgeResponse>(ms, cancellationToken: ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize bridge response");
                continue;
            }

            if (response is null) continue;

            if (_pending.TryRemove(response.Id, out var tcs))
                tcs.TrySetResult(response);
            else
                _logger.LogDebug("Received response for unknown id {Id}", response.Id);
        }
    }

    private void FailPending(string reason)
    {
        foreach (var (key, tcs) in _pending)
        {
            if (_pending.TryRemove(key, out _))
                tcs.TrySetException(new InvalidOperationException(reason));
        }
    }

    public async Task<BridgeResponse> SendCommandAsync(string command, object? @params, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open })
            throw new InvalidOperationException(
                "No s&box editor bridge is connected. Open s&box and ensure the MCP Bridge dock is loaded.");

        var id = Guid.NewGuid().ToString();
        var request = new BridgeRequest { Id = id, Command = command, Params = @params };
        var json = JsonSerializer.SerializeToUtf8Bytes(request);

        var tcs = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            // ClientWebSocket.SendAsync is not safe for concurrent senders; serialize.
            await _sendLock.WaitAsync(ct);
            try
            {
                await ws.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SendCommandTimeout);

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"Timed out waiting for bridge response to command '{command}'.");
        }
    }
}
