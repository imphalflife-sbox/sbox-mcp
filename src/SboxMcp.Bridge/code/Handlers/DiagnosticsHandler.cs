using System.Text.Json;
using SboxMcp.Bridge.Diagnostics;

namespace SboxMcp.Bridge.Handlers;

/// <summary>
/// Handles diagnostics commands: diagnostics.get_logs, diagnostics.get_compile.
/// Backed by DiagnosticsBridge, which subscribes to EditorUtility.AddLogger and
/// [Event("compile.complete")] inside the editor.
/// </summary>
public static class DiagnosticsHandler
{
	/// <summary>
	/// diagnostics.get_logs — Returns log entries from the in-editor ring buffer.
	/// Params (all optional):
	///   tail: int (1..5000)  — return the most-recent N entries, oldest-first.
	///   since: long           — return entries with Seq > since (cursor mode).
	///   max: int (1..5000)    — cap on returned count for since mode (default 500).
	/// `tail` takes precedence when both are provided.
	/// Returns: { entries, nextCursor, count, mode, bridge }
	/// </summary>
	public static Task<object> HandleGetLogs( BridgeRequest request )
	{
		var ring = DiagnosticsBridge.Logs;
		if ( ring == null )
			throw new InvalidOperationException( "Diagnostics log ring not initialized — bridge instance not started." );

		long since = GetParamLong( request, "since", -1 );
		int max = (int)Math.Clamp( GetParamLong( request, "max", 500 ), 1, 5000 );

		LogEntry[] entries;
		string mode;
		var tailRaw = GetParamLongOptional( request, "tail" );
		if ( tailRaw.HasValue )
		{
			int tailN = (int)Math.Clamp( tailRaw.Value, 1, 5000 );
			entries = ring.Tail( tailN );
			mode = "tail";
		}
		else
		{
			entries = ring.Since( since, max );
			mode = "since";
		}

		long nextCursor = entries.Length > 0
			? entries[^1].Seq
			: Math.Max( since, ring.NextSeq - 1 );

		return Task.FromResult<object>( new
		{
			entries,
			nextCursor,
			count = entries.Length,
			mode,
			bridge = BuildBridgeMeta(),
		} );
	}

	/// <summary>
	/// diagnostics.get_compile — Returns the snapshot from the most recent
	/// compile.complete event since editor startup. No params.
	/// Returns: { available, snapshot?, note?, bridge }
	/// </summary>
	public static Task<object> HandleGetCompile( BridgeRequest request )
	{
		var snap = DiagnosticsBridge.LastCompile;
		if ( snap == null )
		{
			return Task.FromResult<object>( new
			{
				available = false,
				note = "no compile has completed since editor start",
				bridge = BuildBridgeMeta(),
			} );
		}
		return Task.FromResult<object>( new
		{
			available = true,
			snapshot = snap,
			bridge = BuildBridgeMeta(),
		} );
	}

	// Operator-visible bridge state — surfaced on every diagnostics response so the
	// caller can sanity-check session age, log buffer fill, and last compile age
	// without guessing at why a query returned what it did.
	internal static object BuildBridgeMeta()
	{
		var startedAt = DiagnosticsBridge.EditorStartedAt;
		var compiledAt = DiagnosticsBridge.LastCompileAt;
		var ring = DiagnosticsBridge.Logs;
		var now = DateTime.UtcNow;
		return new
		{
			editorStartedAt = startedAt?.ToString( "o" ),
			editorUptimeSeconds = startedAt.HasValue ? (now - startedAt.Value).TotalSeconds : (double?)null,
			lastCompileAt = compiledAt?.ToString( "o" ),
			lastCompileAgeSeconds = compiledAt.HasValue ? (now - compiledAt.Value).TotalSeconds : (double?)null,
			logBufferCount = ring?.Count ?? 0,
			logBufferCapacity = ring?.Capacity ?? 0,
			// Diagnostic counters — used to verify [Event] dispatch is reaching us
			// across hotloads. If hotloadStaticCount climbs but compileCompleteCount
			// stays flat after a recompile, instance-method dispatch is broken.
			assemblyVersion = DiagnosticsBridge.AssemblyVersion,
			hotloadStaticCount = DiagnosticsBridge.HotloadStaticCount,
			compileCompleteCount = DiagnosticsBridge.CompileCompleteCount,
			toolFrameCount = DiagnosticsBridge.ToolFrameCount,
			loggerSubscriberCount = DiagnosticsBridge.LoggerSubscriberCount,
		};
	}

	private static long GetParamLong( BridgeRequest request, string key, long fallback )
	{
		return GetParamLongOptional( request, key ) ?? fallback;
	}

	private static long? GetParamLongOptional( BridgeRequest request, string key )
	{
		if ( request.Params is not JsonElement el ) return null;
		if ( !el.TryGetProperty( key, out var prop ) ) return null;
		if ( prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64( out var n ) ) return n;
		if ( prop.ValueKind == JsonValueKind.String && long.TryParse( prop.GetString(), out var s ) ) return s;
		return null;
	}
}
