using SboxMcp.Bridge.Handlers;

namespace SboxMcp.Bridge;

/// <summary>
/// Routes incoming BridgeRequests to the appropriate handler based on command prefix.
/// All handler calls are dispatched to the main thread since s&box editor APIs
/// must run on the main thread.
/// </summary>
public static class CommandRouter
{
	private delegate Task<object> HandlerFunc( BridgeRequest request );

	private static readonly Dictionary<string, HandlerFunc> Handlers = new()
	{
		// Scene commands
		["scene.list"]             = r => SceneHandler.ListObjects( r ),
		["scene.get"]              = r => SceneHandler.GetObject( r ),
		["scene.create"]           = r => SceneHandler.CreateObject( r ),
		["scene.delete"]           = r => SceneHandler.DeleteObject( r ),
		["scene.find"]             = r => SceneHandler.FindObjects( r ),
		["scene.set_transform"]    = r => SceneHandler.SetTransform( r ),
		["scene.hierarchy"]        = r => SceneHandler.GetHierarchy( r ),
		["scene.clone"]            = r => SceneHandler.CloneObject( r ),
		["scene.reparent"]         = r => SceneHandler.ReparentObject( r ),
		["scene.find_by_component"] = r => SceneHandler.FindByComponent( r ),
		["scene.find_by_tag"]      = r => SceneHandler.FindByTag( r ),
		["scene.load"]             = r => SceneHandler.LoadScene( r ),

		// Tag commands
		["tag.add"]    = r => SceneHandler.TagAdd( r ),
		["tag.remove"] = r => SceneHandler.TagRemove( r ),
		["tag.list"]   = r => SceneHandler.TagList( r ),

		// Component commands
		["component.list"]   = r => ComponentHandler.ListComponents( r ),
		["component.get"]    = r => ComponentHandler.GetComponent( r ),
		["component.set"]    = r => ComponentHandler.SetComponent( r ),
		["component.add"]    = r => ComponentHandler.AddComponent( r ),
		["component.remove"] = r => ComponentHandler.RemoveComponent( r ),

		// File commands
		["file.read"]  = r => FileHandler.ReadFile( r ),
		["file.write"] = r => FileHandler.WriteFile( r ),
		["file.list"]  = r => FileHandler.ListFiles( r ),

		// Project commands
		["project.info"] = r => FileHandler.ProjectInfo( r ),

		// Editor commands
		["editor.get_selection"] = r => EditorHandler.HandleGetSelection( r ),
		["editor.select"]        = r => EditorHandler.HandleSelectObject( r ),
		["editor.undo"]          = r => EditorHandler.HandleUndo( r ),
		["editor.redo"]          = r => EditorHandler.HandleRedo( r ),
		["editor.save_scene"]    = r => EditorHandler.HandleSaveScene( r ),
		["editor.screenshot"]    = r => EditorHandler.HandleScreenshot( r ),
		["editor.play"]          = r => EditorHandler.HandlePlay( r ),
		["editor.stop"]          = r => EditorHandler.HandleStop( r ),
		["editor.is_playing"]    = r => EditorHandler.HandleIsPlaying( r ),
		["editor.scene_info"]    = r => EditorHandler.HandleSceneInfo( r ),

		// Diagnostics commands — backed by DiagnosticsBridge (compile.complete +
		// EditorUtility.AddLogger subscriptions inside the editor).
		// editor.console_output is an alias for diagnostics.get_logs so the existing
		// MCP server tool name stays valid; both routes hit the same ring buffer.
		["editor.console_output"]   = r => DiagnosticsHandler.HandleGetLogs( r ),
		["diagnostics.get_logs"]    = r => DiagnosticsHandler.HandleGetLogs( r ),
		["diagnostics.get_compile"] = r => DiagnosticsHandler.HandleGetCompile( r ),

		// Asset commands
		["asset.search"]       = r => AssetHandler.SearchAssets( r ),
		["asset.fetch"]        = r => AssetHandler.FetchAsset( r ),
		["asset.mount"]        = r => AssetHandler.MountAsset( r ),
		["asset.browse_local"] = r => AssetHandler.BrowseLocalAssets( r ),

		// Execution commands
		["execute.csharp"] = r => ExecutionHandler.ExecuteCSharp( r ),
		["console.run"]    = r => ExecutionHandler.RunConsoleCommand( r ),
	};

	[ConVar( "sbox_mcp_main_thread_timeout_ms" )]
	public static int MainThreadTimeoutMs { get; set; } = 15_000;

	/// <summary>
	/// Routes a request to its handler. Returns an error response for unknown commands.
	/// Handlers are called directly — the editor main thread dispatches incoming messages.
	/// </summary>
	public static async Task<BridgeResponse> Route( BridgeRequest request )
	{
		if ( !Handlers.TryGetValue( request.Command, out var handler ) )
		{
			Log.Warning( $"[MCP Bridge] Unknown command: {request.Command}" );
			return BridgeResponse.Fail( request.Id, $"Unknown command: {request.Command}" );
		}

		try
		{
			object data = null;

			// Show toast and log
			McpCommandToast.Show( request.Command );
			McpBridgeDock.Current?.AddLog( $"→ {request.Command}" );

			// Dispatch to main thread — s&box editor APIs must run on the main thread.
			var tcs = new System.Threading.Tasks.TaskCompletionSource<object>();
			MainThread.Queue( async () =>
			{
				try
				{
					var result = await handler( request );
					tcs.SetResult( result );
				}
				catch ( Exception ex )
				{
					tcs.SetException( ex );
				}
			} );

			try
			{
				data = await tcs.Task.WaitAsync( TimeSpan.FromMilliseconds( MainThreadTimeoutMs ) );
			}
			catch ( TimeoutException )
			{
				McpCommandToast.Complete( request.Command, false );
				McpBridgeDock.Current?.AddLog( $"⏱ {request.Command}: main thread did not dispatch within {MainThreadTimeoutMs}ms" );
				return BridgeResponse.Fail( request.Id,
					$"Main thread did not dispatch '{request.Command}' within {MainThreadTimeoutMs}ms. " +
					$"The editor is likely processing a hotload cascade — wait a few seconds and retry." );
			}

			// Update toast and dock on success
			McpCommandToast.Complete( request.Command, true );
			if ( McpBridgeDock.Current != null )
			{
				McpBridgeDock.Current.CommandCount++;
				McpBridgeDock.Current.AddLog( $"✓ {request.Command}" );
			}

			return BridgeResponse.Ok( request.Id, data );
		}
		catch ( Exception ex )
		{
			Log.Error( $"[MCP Bridge] Handler error for '{request.Command}': {ex.Message}" );

			// Update toast and dock on failure
			McpCommandToast.Complete( request.Command, false );
			McpBridgeDock.Current?.AddLog( $"✗ {request.Command}: {ex.Message}" );

			return BridgeResponse.Fail( request.Id, ex.Message );
		}
	}
}
