using System.Reflection;
using Microsoft.CodeAnalysis;

namespace SboxMcp.Bridge.Diagnostics;

// Static facade — kept as `static class` to match the original type signature so
// the s&box hotload framework can swap this assembly without rejecting the type
// (changing a `static class` to a regular `class` would break hotload compat).
//
// Event handlers and runtime state live on BridgeInstance below. This class
// holds only:
//   - the singleton ref to the BridgeInstance (preserved across hotload via
//     Sandbox.Hotload's static-field reference updating)
//   - the two static [Event] handlers needed to bootstrap and rehydrate that
//     instance after each hotload (editor.created and hotloaded)
//   - read-only accessors that DiagnosticsHandler uses to render bridge state
//
// HTTP server stripped vs the original tower_defense version — the MCP bridge's
// WebSocket transport delivers responses; we only need state and event subscriptions here.
internal static class DiagnosticsBridge
{
	private static BridgeInstance _instance;

	internal static BridgeInstance Instance => _instance;
	internal static LogRing Logs => _instance?.Logs;
	internal static CompileSnapshot LastCompile => _instance?.LastCompile;
	internal static DateTime? EditorStartedAt => _instance?.EditorStartedAt;
	internal static DateTime? LastCompileAt => _instance?.LastCompileAt;
	internal static int HotloadStaticCount => _instance?.HotloadStaticCount ?? 0;
	internal static int CompileCompleteCount => _instance?.CompileCompleteCount ?? 0;
	internal static int ToolFrameCount => _instance?.ToolFrameCount ?? 0;
	internal static string AssemblyVersion => _instance?.AssemblyVersion;
	internal static int LoggerSubscriberCount => BridgeInstance.GetLoggerSubscriberCount();

	[Event( "editor.created" )]
	public static void OnEditorCreated( EditorMainWindow _ )
	{
		EnsureInstance( reset: true );
	}

	// Static safety net. Fires after each assembly hotload — used to:
	//   1. Bootstrap the instance when this code is first hotloaded into a
	//      running editor whose previous editor.dll didn't have this code.
	//   2. Heal any case where Sandbox.Hotload's static-field preservation didn't
	//      survive (defense in depth).
	// In the normal post-bootstrap case, the instance handlers on BridgeInstance
	// fire alongside this and do most of the work; this method is a thin guard.
	[Event( "hotloaded" )]
	public static void OnHotloadedStatic()
	{
		EnsureInstance( reset: false );
		if ( _instance != null ) _instance.HotloadStaticCount++;
	}

	private static void EnsureInstance( bool reset )
	{
		var fresh = _instance == null;
		if ( fresh )
		{
			_instance = new BridgeInstance();
			EditorEvent.Register( _instance );
		}

		_instance.AssemblyVersion = typeof( DiagnosticsBridge ).Assembly.GetName().Version?.ToString();

		if ( reset )
			_instance.ResetState();
		else
			_instance.RehydrateState();

		_instance.Start();
	}
}

// Instance carrying the [Event]-handled state. Registered once via
// EditorEvent.Register(...) from DiagnosticsBridge.EnsureInstance, then travels
// across hotloads via the engine EventSystem's OrphanedInstances rescue path —
// which is the codepath ConsoleWidget relies on for the same purpose.
internal class BridgeInstance
{
	internal LogRing Logs { get; private set; }
	internal CompileSnapshot LastCompile { get; private set; }
	internal DateTime? EditorStartedAt { get; private set; }
	internal DateTime? LastCompileAt { get; private set; }

	internal int HotloadStaticCount;
	internal int CompileCompleteCount;
	internal int ToolFrameCount;
	internal string AssemblyVersion;

	private const int LogCapacity = 5000;

	internal void ResetState()
	{
		EditorStartedAt = DateTime.UtcNow;
		LastCompile = null;
		LastCompileAt = null;
		Logs = new LogRing( LogCapacity );
	}

	internal void RehydrateState()
	{
		Logs ??= new LogRing( LogCapacity );
		EditorStartedAt ??= DateTime.UtcNow;
	}

	[Event( "app.exit" )]
	public void OnAppExit() => Stop();

	[Event( "tool.frame" )]
	public void EnsureLoggerSubscribed()
	{
		ToolFrameCount++;
		if ( Logs == null ) return;
		EditorUtility.RemoveLogger( OnLogMessage );
		EditorUtility.AddLogger( OnLogMessage );
	}

	[Event( "compile.complete" )]
	public void OnCompileComplete( CompileGroup group )
	{
		CompileCompleteCount++;
		try
		{
			LastCompile = BuildSnapshot( group );
			LastCompileAt = DateTime.UtcNow;
			Log.Info( $"[MCP Bridge Diag] compile snapshot #{CompileCompleteCount}: {LastCompile.ErrorCount} errors, {LastCompile.WarningCount} warnings" );
		}
		catch ( Exception ex )
		{
			Log.Error( $"[MCP Bridge Diag] snapshot build failed: {ex.GetType().Name}: {ex.Message}" );
		}
	}

	internal void Start()
	{
		Logs ??= new LogRing( LogCapacity );

		// Scrub any stale OnLogMessage delegates left over from a previous
		// instance of this assembly. Sandbox.Diagnostics.Logging.OnMessage is owned
		// by the engine assembly (Sandbox.System.dll, never reloads), but the
		// invocation list still holds delegates whose Method handles point at types
		// from older versions of this assembly. Logging.Write wraps the multicast
		// invoke in try/finally with no catch, so if any older delegate throws on
		// invoke, the entire chain aborts and our live delegate never fires.
		ScrubStaleSelfSubscriptions();

		// Avoid double-subscribe across reloads. EditorUtility.AddLogger would
		// otherwise stack the delegate each time Start() runs.
		EditorUtility.RemoveLogger( OnLogMessage );
		EditorUtility.AddLogger( OnLogMessage );

		Log.Info( $"[MCP Bridge Diag] subscribed (asm v{AssemblyVersion})" );
	}

	internal void Stop()
	{
		EditorUtility.RemoveLogger( OnLogMessage );
		// Logs intentionally retained so log cursor continuity survives a stop/start.
	}

	private void OnLogMessage( LogEvent e )
	{
		try
		{
			// Drop our own bridge chatter — every MCP command emits a
			// "[MCP Bridge] {endpoint} → {command}" line plus per-handler ack lines.
			// At sustained tool-call rates these saturate the 5000-slot ring within
			// a session and push out the engine/game logs the caller actually wants.
			// The s&box console and the dock activity log still show them; the ring
			// just stops mirroring information the caller already has from the
			// response payload.
			var msg = e.Message;
			if ( msg != null && msg.StartsWith( "[MCP Bridge", StringComparison.Ordinal ) )
				return;

			Logs?.Append(
				time: e.Time,
				level: e.Level.ToString(),
				logger: e.Logger ?? "",
				message: msg ?? "",
				exception: e.Exception?.ToString() );
		}
		catch
		{
			// Never throw from a log handler — would re-enter the logger.
		}
	}

	internal static int GetLoggerSubscriberCount()
	{
		try
		{
			var (field, del) = ResolveLoggingOnMessage();
			if ( field == null ) return -1;
			if ( del == null ) return 0;
			return del.GetInvocationList().Length;
		}
		catch ( Exception )
		{
			return -3;
		}
	}

	// Walks Sandbox.Diagnostics.Logging.OnMessage's invocation list and removes
	// any delegate whose Method.DeclaringType has the same FullName as
	// BridgeInstance but is NOT this loaded BridgeInstance type. That signature
	// catches subscriptions made by older (now-unloaded) versions of this
	// assembly — those delegates throw on invoke and poison the multicast chain.
	private static void ScrubStaleSelfSubscriptions()
	{
		try
		{
			var (field, del) = ResolveLoggingOnMessage();
			if ( field == null || del == null ) return;

			var ourType = typeof( BridgeInstance );
			Delegate cleaned = del;
			int removed = 0;

			foreach ( var sub in del.GetInvocationList() )
			{
				var declaring = sub.Method?.DeclaringType;
				if ( declaring == null ) continue;
				if ( declaring == ourType ) continue;
				if ( declaring.FullName != ourType.FullName ) continue;

				cleaned = Delegate.Remove( cleaned, sub );
				removed++;
			}

			if ( removed > 0 )
			{
				field.SetValue( null, cleaned );
				Log.Info( $"[MCP Bridge Diag] scrubbed {removed} stale OnMessage subscribers from prior assembly versions" );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[MCP Bridge Diag] subscription scrub failed: {ex.GetType().Name}: {ex.Message}" );
		}
	}

	private static (FieldInfo field, Delegate del) ResolveLoggingOnMessage()
	{
		var loggingType = Type.GetType( "Sandbox.Diagnostics.Logging, Sandbox.System" );
		if ( loggingType == null ) return (null, null);
		var field = loggingType.GetField( "OnMessage", BindingFlags.Static | BindingFlags.NonPublic );
		if ( field == null ) return (null, null);
		return (field, field.GetValue( null ) as Delegate);
	}

	// CompileGroup.BuildResult is a struct (Results), and Results.Diagnostics is
	// List<Diagnostic> which can be null when no compile has occurred. Handle inline.
	private static CompileSnapshot BuildSnapshot( CompileGroup group )
	{
		var diagList = group?.BuildResult.Diagnostics;
		var count = diagList?.Count ?? 0;
		var entries = new DiagnosticEntry[count];
		int errors = 0, warnings = 0;

		for ( int i = 0; i < count; i++ )
		{
			var d = diagList[i];
			var loc = d.Location;
			var span = loc != null ? loc.GetLineSpan() : default;
			var hasSpan = loc != null && loc.IsInSource;

			var path = hasSpan ? (span.Path ?? "") : "";
			var line = hasSpan ? span.StartLinePosition.Line + 1 : 0;
			var col = hasSpan ? span.StartLinePosition.Character + 1 : 0;
			var sev = d.Severity.ToString();

			if ( d.Severity == DiagnosticSeverity.Error ) errors++;
			else if ( d.Severity == DiagnosticSeverity.Warning ) warnings++;

			entries[i] = new DiagnosticEntry(
				Severity: sev,
				Id: d.Id ?? "",
				Message: d.GetMessage(),
				FilePath: path,
				Line: line,
				Column: col );
		}

		return new CompileSnapshot(
			CompletedAt: DateTime.UtcNow.ToString( "o" ),
			Success: errors == 0,
			ErrorCount: errors,
			WarningCount: warnings,
			Diagnostics: entries );
	}
}
