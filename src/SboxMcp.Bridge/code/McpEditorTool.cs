using SboxMcp.Bridge.Diagnostics;

namespace SboxMcp.Bridge;

/// <summary>
/// s&box Editor Dock entry point for the MCP Bridge.
/// Hosts the WebSocket server that accepts MCP-server clients (one per connected
/// Claude Code session) and surfaces connection state.
/// </summary>
[Dock( "Editor", "MCP Bridge", "smart_toy" )]
public class McpBridgeDock : Widget
{
	public static McpBridgeDock Current { get; private set; }

	public int CommandCount { get; set; }

	private McpBridgeServer _server;
	private int _port = McpBridgeServer.DefaultPort;
	private readonly List<string> _logEntries = new();
	private const int MaxLogEntries = 50;
	private DateTime _startedAt;

	private Editor.Label _statusDot;
	private Editor.Label _statusText;
	private Editor.Label _urlLabel;
	private Button _toggleButton;
	private Editor.Label _commandCountLabel;
	private Editor.Label _clientCountLabel;
	private Editor.Label _uptimeLabel;
	private Editor.Label _editorUptimeLabel;
	private Editor.Label _lastCompileLabel;
	private Editor.Label _logBufferLabel;
	private Editor.Label _compileCountLabel;
	private Editor.Label _hotloadCountLabel;
	private Editor.Label _loggerSubsLabel;
	private Layout _logLayout;
	private LineEdit _portField;

	public McpBridgeDock( Widget parent ) : base( parent )
	{
		Current = this;

		MinimumSize = 200;
		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 8;

		BuildHeader();
		BuildControls();
		BuildStats();
		BuildDiagnostics();
		BuildLog();

		StartServer();
	}

	private void BuildHeader()
	{
		var header = Layout.AddRow();
		header.Spacing = 6;

		_statusDot = new Editor.Label( "●", this );
		_statusDot.SetStyles( "color: #e05252; font-size: 18px;" );
		header.Add( _statusDot );

		var textCol = header.AddColumn();
		textCol.Spacing = 2;

		_statusText = new Editor.Label( "Stopped", this );
		textCol.Add( _statusText );

		_urlLabel = new Editor.Label( $"ws://localhost:{_port}", this );
		_urlLabel.SetStyles( "color: #888888; font-size: 11px;" );
		textCol.Add( _urlLabel );

		header.AddStretchCell();
	}

	private void BuildControls()
	{
		var row = Layout.AddRow();
		row.Spacing = 6;

		var portLabel = new Editor.Label( "Port:", this );
		row.Add( portLabel );

		_portField = new LineEdit( this );
		var portField = _portField;
		portField.Text = _port.ToString();
		portField.MinimumWidth = 80;
		portField.MaximumWidth = 100;
		portField.TextEdited += v =>
		{
			if ( int.TryParse( v, out var p ) )
			{
				_port = p;
				_urlLabel.Text = $"ws://localhost:{_port}";
			}
		};
		row.Add( portField );

		row.AddStretchCell();

		_toggleButton = new Button( "Stop", this );
		_toggleButton.Icon = "stop";
		_toggleButton.Clicked = () =>
		{
			if ( _server != null && _server.IsListening )
				StopServer();
			else
				StartServer();
		};
		row.Add( _toggleButton );
	}

	private void BuildStats()
	{
		var statsRow = Layout.AddRow();
		statsRow.Spacing = 12;

		var clientsCol = statsRow.AddColumn();
		clientsCol.Spacing = 2;
		clientsCol.Add( new Editor.Label( "Clients", this ) );
		_clientCountLabel = new Editor.Label( "0", this );
		_clientCountLabel.SetStyles( "font-size: 18px; font-weight: bold;" );
		clientsCol.Add( _clientCountLabel );

		var cmdCol = statsRow.AddColumn();
		cmdCol.Spacing = 2;
		cmdCol.Add( new Editor.Label( "Commands", this ) );
		_commandCountLabel = new Editor.Label( "0", this );
		_commandCountLabel.SetStyles( "font-size: 18px; font-weight: bold;" );
		cmdCol.Add( _commandCountLabel );

		var uptimeCol = statsRow.AddColumn();
		uptimeCol.Spacing = 2;
		uptimeCol.Add( new Editor.Label( "Uptime", this ) );
		_uptimeLabel = new Editor.Label( "--", this );
		_uptimeLabel.SetStyles( "font-size: 18px; font-weight: bold;" );
		uptimeCol.Add( _uptimeLabel );

		statsRow.AddStretchCell();
	}

	// Diagnostics section reads from DiagnosticsBridge — the in-editor compile.complete
	// listener and EditorUtility.AddLogger ring buffer that backs the diagnostics MCP tools.
	// Lets the operator see at a glance whether [Event] dispatch is healthy across hotloads
	// without having to call diagnostics.get_logs.
	private void BuildDiagnostics()
	{
		var sep = new Editor.Label( "Diagnostics", this );
		sep.SetStyles( "color: #888888; font-size: 11px; text-transform: uppercase; letter-spacing: 1px;" );
		Layout.Add( sep );

		var grid = Layout.AddRow();
		grid.Spacing = 12;

		var leftCol = grid.AddColumn();
		leftCol.Spacing = 4;
		_editorUptimeLabel = AddLabeledRow( leftCol, "Editor up", "--" );
		_lastCompileLabel  = AddLabeledRow( leftCol, "Last compile", "--" );
		_logBufferLabel    = AddLabeledRow( leftCol, "Log buffer", "0 / 0" );

		var rightCol = grid.AddColumn();
		rightCol.Spacing = 4;
		_compileCountLabel = AddLabeledRow( rightCol, "Compiles", "0" );
		_hotloadCountLabel = AddLabeledRow( rightCol, "Hotloads", "0" );
		_loggerSubsLabel   = AddLabeledRow( rightCol, "Logger subs", "0" );

		grid.AddStretchCell();
	}

	private Editor.Label AddLabeledRow( Layout col, string label, string initial )
	{
		var row = col.AddRow();
		row.Spacing = 6;
		var lblLabel = new Editor.Label( label, this );
		lblLabel.SetStyles( "color: #888888; font-size: 11px;" );
		row.Add( lblLabel );
		var valueLabel = new Editor.Label( initial, this );
		valueLabel.SetStyles( "font-size: 11px;" );
		row.Add( valueLabel );
		row.AddStretchCell();
		return valueLabel;
	}

	private void BuildLog()
	{
		Layout.Add( new Editor.Label( "Activity Log", this ) );

		var scroll = new ScrollArea( this );
		scroll.Canvas = new Widget( this );
		scroll.Canvas.Layout = Layout.Column();
		_logLayout = scroll.Canvas.Layout;
		_logLayout.Spacing = 3;
		_logLayout.Margin = 4;
		Layout.Add( scroll, 1 );
	}

	public override void OnDestroyed()
	{
		if ( Current == this )
			Current = null;
		StopServer();
		base.OnDestroyed();
	}

	[EditorEvent.Frame]
	public void Frame()
	{
		if ( !_statusDot.IsValid() || !_statusText.IsValid() || !_toggleButton.IsValid() )
			return;

		var listening = _server != null && _server.IsListening;
		var clientCount = listening ? _server.ClientCount : 0;

		// Green dot if a client is connected, amber if listening but idle, red if stopped.
		var dotColor = !listening ? "#e05252"
			: clientCount > 0 ? "#52e052"
			: "#e0a052";
		_statusDot.SetStyles( $"color: {dotColor}; font-size: 18px;" );

		_statusText.Text = !listening
			? "Stopped"
			: clientCount == 0
				? "Listening (no clients)"
				: clientCount == 1
					? "Listening (1 client)"
					: $"Listening ({clientCount} clients)";

		_toggleButton.Text = listening ? "Stop" : "Start";
		_toggleButton.Icon = listening ? "stop" : "play_arrow";
		_clientCountLabel.Text = clientCount.ToString();
		_commandCountLabel.Text = CommandCount.ToString();
		_portField.ReadOnly = listening;

		if ( listening && _startedAt != default )
		{
			var elapsed = DateTime.Now - _startedAt;
			_uptimeLabel.Text = elapsed.TotalHours >= 1
				? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m"
				: $"{elapsed.Minutes}m {elapsed.Seconds:D2}s";
		}
		else
		{
			_uptimeLabel.Text = "--";
		}

		UpdateDiagnostics();
	}

	private void UpdateDiagnostics()
	{
		if ( !_editorUptimeLabel.IsValid() ) return;

		var startedAt = DiagnosticsBridge.EditorStartedAt;
		_editorUptimeLabel.Text = startedAt.HasValue
			? FormatDuration( DateTime.UtcNow - startedAt.Value )
			: "--";

		var compiledAt = DiagnosticsBridge.LastCompileAt;
		var snap = DiagnosticsBridge.LastCompile;
		if ( compiledAt.HasValue && snap != null )
		{
			var age = FormatDuration( DateTime.UtcNow - compiledAt.Value );
			_lastCompileLabel.Text = snap.Success
				? $"{age} ago  ✓"
				: $"{age} ago  ✗ {snap.ErrorCount}e";
		}
		else
		{
			_lastCompileLabel.Text = "(none yet)";
		}

		var ring = DiagnosticsBridge.Logs;
		_logBufferLabel.Text = ring != null ? $"{ring.Count} / {ring.Capacity}" : "--";
		_compileCountLabel.Text = DiagnosticsBridge.CompileCompleteCount.ToString();
		_hotloadCountLabel.Text = DiagnosticsBridge.HotloadStaticCount.ToString();
		_loggerSubsLabel.Text = DiagnosticsBridge.LoggerSubscriberCount.ToString();
	}

	private static string FormatDuration( TimeSpan d )
	{
		if ( d.TotalSeconds < 60 ) return $"{(int)d.TotalSeconds}s";
		if ( d.TotalMinutes < 60 ) return $"{(int)d.TotalMinutes}m";
		if ( d.TotalHours < 24 ) return $"{(int)d.TotalHours}h {d.Minutes:D2}m";
		return $"{(int)d.TotalDays}d {d.Hours:D2}h";
	}

	public void AddLog( string message )
	{
		var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
		_logEntries.Insert( 0, entry );
		if ( _logEntries.Count > MaxLogEntries )
			_logEntries.RemoveAt( _logEntries.Count - 1 );

		_logLayout.Clear( true );
		foreach ( var e in _logEntries )
		{
			var lbl = new Editor.Label( e, this );
			lbl.SetStyles( "font-size: 11px;" );
			_logLayout.Add( lbl );
		}
	}

	private void StartServer()
	{
		StopServer();
		_server = new McpBridgeServer( _port );
		_server.Start();
		_startedAt = DateTime.Now;
		Log.Info( $"[MCP Bridge] Server started on ws://localhost:{_port}" );
		AddLog( $"Listening on ws://localhost:{_port}" );
	}

	private void StopServer()
	{
		if ( _server is null )
			return;
		_server.Stop();
		_server.Dispose();
		_server = null;
		_startedAt = default;
		AddLog( "Stopped." );
	}
}
