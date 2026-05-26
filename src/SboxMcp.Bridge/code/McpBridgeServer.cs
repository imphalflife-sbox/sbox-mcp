using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

// `Sandbox.WebSocket` exists and is pulled in by the addon's global `using Sandbox`,
// so the unqualified `WebSocket` type is ambiguous. Alias resolves it to the .NET one.
using WebSocket = System.Net.WebSockets.WebSocket;

namespace SboxMcp.Bridge;

/// <summary>
/// WebSocket server hosted inside the s&box editor. Accepts many concurrent MCP-server
/// clients (one per Claude Code session) and routes each incoming request through the
/// shared CommandRouter, which marshals to the editor main thread. Per-client request/
/// response is isolated on the client's own socket — no cross-client correlation needed.
/// </summary>
public class McpBridgeServer : IDisposable
{
	public const int DefaultPort = 29015;

	private readonly int _port;
	private HttpListener _listener;
	private CancellationTokenSource _cts;
	// SkipHotload here cuts the upgrader's walk before it reaches the BCL-internal
	// TaskFactory closures via ClientConnection._ws._innerStream._context. Field is left
	// null on the migrated instance; Start() re-initializes, and old clients are orphaned
	// (their sockets die on next OS-level read). _listener/_cts migrate normally so Stop()
	// can release the port before Start() rebinds.
	[SkipHotload] private List<ClientConnection> _clients = new();
	private readonly object _clientsLock = new();
	private bool _disposed;

	public bool IsListening => _listener?.IsListening ?? false;
	public int Port => _port;

	public int ClientCount
	{
		get { lock ( _clientsLock ) return _clients.Count; }
	}

	public McpBridgeServer( int port = DefaultPort )
	{
		_port = port;
	}

	/// <summary>
	/// Begin accepting client connections. Idempotent — calling twice with the same
	/// instance is a no-op.
	/// </summary>
	public void Start()
	{
		if ( _cts is not null )
			return;

		// SkipHotload leaves these null on a migrated instance — re-initialize defensively.
		_clients ??= new List<ClientConnection>();

		_cts = new CancellationTokenSource();
		_listener = new HttpListener();
		_listener.Prefixes.Add( $"http://localhost:{_port}/" );

		try
		{
			_listener.Start();
		}
		catch ( Exception ex )
		{
			Log.Error( $"[MCP Bridge] Failed to bind port {_port}: {ex.Message}" );
			_cts.Dispose();
			_cts = null;
			_listener = null;
			return;
		}

		Log.Info( $"[MCP Bridge] Listening on ws://localhost:{_port}/" );
		_ = AcceptLoop( _cts.Token );
	}

	/// <summary>
	/// Stop accepting new clients and close all open connections.
	/// </summary>
	public void Stop()
	{
		_cts?.Cancel();

		try { _listener?.Stop(); }
		catch { /* listener already disposed */ }
		_listener = null;

		ClientConnection[] toClose;
		lock ( _clientsLock )
		{
			// _clients can be null after a hotload (SkipHotload leaves it at default).
			toClose = _clients?.ToArray() ?? Array.Empty<ClientConnection>();
			_clients?.Clear();
		}
		foreach ( var c in toClose )
			c.Dispose();

		_cts?.Dispose();
		_cts = null;

		Log.Info( "[MCP Bridge] Stopped." );
	}

	private async Task AcceptLoop( CancellationToken ct )
	{
		while ( !ct.IsCancellationRequested && _listener?.IsListening == true )
		{
			HttpListenerContext ctx;
			try
			{
				ctx = await _listener.GetContextAsync();
			}
			catch ( ObjectDisposedException ) { return; }
			catch ( HttpListenerException ) { return; }
			catch ( Exception ex )
			{
				Log.Warning( $"[MCP Bridge] Accept error: {ex.Message}" );
				continue;
			}

			if ( !ctx.Request.IsWebSocketRequest )
			{
				ctx.Response.StatusCode = 400;
				ctx.Response.Close();
				continue;
			}

			HttpListenerWebSocketContext wsCtx;
			try
			{
				wsCtx = await ctx.AcceptWebSocketAsync( null );
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[MCP Bridge] WebSocket upgrade failed: {ex.Message}" );
				continue;
			}

			var endpoint = ctx.Request.RemoteEndPoint?.ToString() ?? "?";
			var client = new ClientConnection( wsCtx.WebSocket, endpoint );

			lock ( _clientsLock ) _clients.Add( client );
			Log.Info( $"[MCP Bridge] Client connected: {endpoint} (total: {ClientCount})" );

			_ = HandleClient( client, ct );
		}
	}

	private async Task HandleClient( ClientConnection client, CancellationToken ct )
	{
		try
		{
			await client.ReceiveLoop( ct );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[MCP Bridge] Client {client.RemoteEndpoint} error: {ex.Message}" );
		}
		finally
		{
			lock ( _clientsLock ) _clients.Remove( client );
			client.Dispose();
			Log.Info( $"[MCP Bridge] Client disconnected: {client.RemoteEndpoint} (total: {ClientCount})" );
		}
	}

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		Stop();
	}
}

/// <summary>
/// One MCP server client connection. Owns its socket, send-serialization lock, and
/// receive loop. Each client's request/response cycle is independent of every other
/// client's — the bridge does not need to correlate ids across connections.
/// </summary>
internal sealed class ClientConnection : IDisposable
{
	private const int ReceiveBufferSize = 8192;

	private readonly WebSocket _ws;
	private readonly SemaphoreSlim _sendLock = new( 1, 1 );
	private bool _disposed;

	public string RemoteEndpoint { get; }
	public bool IsConnected => _ws.State == WebSocketState.Open;

	public ClientConnection( WebSocket ws, string remoteEndpoint )
	{
		_ws = ws;
		RemoteEndpoint = remoteEndpoint;
	}

	public async Task ReceiveLoop( CancellationToken ct )
	{
		var buffer = new byte[ReceiveBufferSize];
		using var ms = new System.IO.MemoryStream();

		while ( _ws.State == WebSocketState.Open && !ct.IsCancellationRequested )
		{
			ms.SetLength( 0 );
			WebSocketReceiveResult result;

			do
			{
				result = await _ws.ReceiveAsync( new ArraySegment<byte>( buffer ), ct );

				if ( result.MessageType == WebSocketMessageType.Close )
				{
					await _ws.CloseAsync( WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None );
					return;
				}

				ms.Write( buffer, 0, result.Count );
			}
			while ( !result.EndOfMessage );

			var json = Encoding.UTF8.GetString( ms.ToArray() );
			_ = ProcessMessage( json );
		}
	}

	private async Task ProcessMessage( string json )
	{
		BridgeRequest request = null;

		try
		{
			request = JsonSerializer.Deserialize<BridgeRequest>( json );
			if ( request is null )
			{
				Log.Warning( $"[MCP Bridge] {RemoteEndpoint}: received null or invalid message" );
				return;
			}

			Log.Info( $"[MCP Bridge] {RemoteEndpoint} → {request.Command} (id={request.Id})" );
			var response = await CommandRouter.Route( request );
			await SendResponse( response );
		}
		catch ( Exception ex )
		{
			Log.Error( $"[MCP Bridge] {RemoteEndpoint} processing error: {ex.Message}" );
			if ( request is not null )
				await SendResponse( BridgeResponse.Fail( request.Id, ex.Message ) );
		}
	}

	public async Task SendResponse( BridgeResponse response )
	{
		if ( !IsConnected )
			return;

		var json = JsonSerializer.Serialize( response );
		var bytes = Encoding.UTF8.GetBytes( json );

		await _sendLock.WaitAsync();
		try
		{
			await _ws.SendAsync(
				new ArraySegment<byte>( bytes ),
				WebSocketMessageType.Text,
				endOfMessage: true,
				CancellationToken.None );
		}
		finally
		{
			_sendLock.Release();
		}
	}

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;

		try { _ws.CloseAsync( WebSocketCloseStatus.NormalClosure, "Disposed", CancellationToken.None ).GetAwaiter().GetResult(); }
		catch { /* best-effort close */ }

		_ws.Dispose();
		_sendLock.Dispose();
	}
}
