using SboxMcp.Bridge.Diagnostics;

namespace SboxMcp.Bridge.Handlers;

/// <summary>
/// Bridge meta-commands. These do NOT dispatch through MainThread.Queue —
/// they run on whatever thread the WebSocket receive loop calls them on,
/// so they remain responsive even when the editor main thread is wedged
/// (e.g., during a hotload cascade).
///
/// Handlers here MUST NOT touch Scene, Component, or any editor API that
/// requires the main thread. They may only read thread-safe static state.
/// </summary>
public static class BridgeHandler
{
	public static Task<object> Health( BridgeRequest request )
	{
		var now = DateTime.UtcNow;
		var lastCompleted = CommandRouter.LastCommandCompletedAt;

		object data = new
		{
			connected = true,
			currentCommand = CommandRouter.CurrentCommand,
			lastCommandCompletedAt = lastCompleted?.ToString( "o" ),
			secondsSinceLastCommand = lastCompleted.HasValue
				? (double?) (now - lastCompleted.Value).TotalSeconds
				: (double?) null,
			mainThreadTimeoutMs = CommandRouter.MainThreadTimeoutMs,
			bridgeAssemblyVersion = DiagnosticsBridge.AssemblyVersion,
			commandCount = McpBridgeDock.Current?.CommandCount ?? 0,
		};

		return Task.FromResult( data );
	}
}
