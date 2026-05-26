using SboxMcp.Bridge.Handlers;

namespace SboxMcp.Bridge;

/// <summary>
/// Routes incoming BridgeRequests to the appropriate handler based on command prefix.
/// All handler calls are dispatched to the main thread since s&box editor APIs
/// must run on the main thread.
/// </summary>
public static class CommandRouter
{
	[ConVar( "sbox_mcp_main_thread_timeout_ms" )]
	public static int MainThreadTimeoutMs { get; set; } = 15_000;

	/// <summary>UTC timestamp of the last successfully-completed command. null if no command has completed yet (or since hotload reset it).</summary>
	public static DateTime? LastCommandCompletedAt { get; private set; }

	/// <summary>The command name currently dispatching, if any. Null when idle.</summary>
	public static string CurrentCommand { get; private set; }

	/// <summary>
	/// Dispatches a request to its handler via a switch expression.
	/// Using a switch expression (instead of a static Dictionary of delegates) means
	/// no static state holding delegate references, so editor hotload cannot break
	/// dispatch by failing to cast old-assembly delegate types to new-assembly types.
	/// Returns null for unknown commands.
	/// </summary>
	private static Task<object> Dispatch( BridgeRequest request )
	{
		return request.Command switch
		{
			// Scene commands
			"scene.list"              => SceneHandler.ListObjects( request ),
			"scene.get"               => SceneHandler.GetObject( request ),
			"scene.create"            => SceneHandler.CreateObject( request ),
			"scene.delete"            => SceneHandler.DeleteObject( request ),
			"scene.find"              => SceneHandler.FindObjects( request ),
			"scene.set_transform"     => SceneHandler.SetTransform( request ),
			"scene.hierarchy"         => SceneHandler.GetHierarchy( request ),
			"scene.clone"             => SceneHandler.CloneObject( request ),
			"scene.reparent"          => SceneHandler.ReparentObject( request ),
			"scene.find_by_component" => SceneHandler.FindByComponent( request ),
			"scene.find_by_tag"       => SceneHandler.FindByTag( request ),
			"scene.load"              => SceneHandler.LoadScene( request ),

			// Tag commands
			"tag.add"    => SceneHandler.TagAdd( request ),
			"tag.remove" => SceneHandler.TagRemove( request ),
			"tag.list"   => SceneHandler.TagList( request ),

			// Component commands
			"component.list"   => ComponentHandler.ListComponents( request ),
			"component.get"    => ComponentHandler.GetComponent( request ),
			"component.set"    => ComponentHandler.SetComponent( request ),
			"component.add"    => ComponentHandler.AddComponent( request ),
			"component.remove" => ComponentHandler.RemoveComponent( request ),

			// File commands
			"file.read"  => FileHandler.ReadFile( request ),
			"file.write" => FileHandler.WriteFile( request ),
			"file.list"  => FileHandler.ListFiles( request ),

			// Project commands
			"project.info" => FileHandler.ProjectInfo( request ),

			// Editor commands
			"editor.get_selection" => EditorHandler.HandleGetSelection( request ),
			"editor.select"        => EditorHandler.HandleSelectObject( request ),
			"editor.undo"          => EditorHandler.HandleUndo( request ),
			"editor.redo"          => EditorHandler.HandleRedo( request ),
			"editor.save_scene"    => EditorHandler.HandleSaveScene( request ),
			"editor.screenshot"    => EditorHandler.HandleScreenshot( request ),
			"editor.play"          => EditorHandler.HandlePlay( request ),
			"editor.stop"          => EditorHandler.HandleStop( request ),
			"editor.is_playing"    => EditorHandler.HandleIsPlaying( request ),
			"editor.scene_info"    => EditorHandler.HandleSceneInfo( request ),

			// Diagnostics commands — backed by DiagnosticsBridge (compile.complete +
			// EditorUtility.AddLogger subscriptions inside the editor).
			// editor.console_output is an alias for diagnostics.get_logs so the existing
			// MCP server tool name stays valid; both routes hit the same ring buffer.
			"editor.console_output"   => DiagnosticsHandler.HandleGetLogs( request ),
			"diagnostics.get_logs"    => DiagnosticsHandler.HandleGetLogs( request ),
			"diagnostics.get_compile" => DiagnosticsHandler.HandleGetCompile( request ),

			// Asset commands
			"asset.search"       => AssetHandler.SearchAssets( request ),
			"asset.fetch"        => AssetHandler.FetchAsset( request ),
			"asset.mount"        => AssetHandler.MountAsset( request ),
			"asset.browse_local" => AssetHandler.BrowseLocalAssets( request ),

			// Execution commands
			"execute.csharp" => ExecutionHandler.ExecuteCSharp( request ),
			"console.run"    => ExecutionHandler.RunConsoleCommand( request ),

			_ => null
		};
	}

	/// <summary>
	/// Routes a request to its handler. Returns an error response for unknown commands.
	/// Handlers are called directly — the editor main thread dispatches incoming messages.
	/// </summary>
	public static async Task<BridgeResponse> Route( BridgeRequest request )
	{
		// Show toast and log immediately (before main-thread dispatch, so unknown commands also log)
		McpCommandToast.Show( request.Command );
		McpBridgeDock.Current?.AddLog( $"→ {request.Command}" );
		CurrentCommand = request.Command;

		try
		{
			object data = null;

			// Dispatch to main thread — s&box editor APIs must run on the main thread.
			var tcs = new System.Threading.Tasks.TaskCompletionSource<object>();
			MainThread.Queue( async () =>
			{
				try
				{
					var handlerTask = Dispatch( request );
					if ( handlerTask is null )
					{
						tcs.SetException( new InvalidOperationException( $"Unknown command: {request.Command}" ) );
						return;
					}
					var result = await handlerTask;
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
				CurrentCommand = null;
				return BridgeResponse.Fail( request.Id,
					$"Main thread did not dispatch '{request.Command}' within {MainThreadTimeoutMs}ms. " +
					$"The editor is likely processing a hotload cascade — wait a few seconds and retry." );
			}

			// Update toast and dock on success
			LastCommandCompletedAt = DateTime.UtcNow;
			CurrentCommand = null;
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
			CurrentCommand = null;
			Log.Error( $"[MCP Bridge] Handler error for '{request.Command}': {ex.Message}" );

			// Update toast and dock on failure
			McpCommandToast.Complete( request.Command, false );
			McpBridgeDock.Current?.AddLog( $"✗ {request.Command}: {ex.Message}" );

			return BridgeResponse.Fail( request.Id, ex.Message );
		}
	}
}
