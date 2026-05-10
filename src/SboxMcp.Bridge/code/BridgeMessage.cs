using System.Text.Json;
using System.Text.Json.Serialization;

namespace SboxMcp.Bridge;

/// <summary>
/// Incoming request from a connected MCP server client.
/// </summary>
public class BridgeRequest
{
	[JsonPropertyName( "id" )]
	public string Id { get; set; } = "";

	[JsonPropertyName( "command" )]
	public string Command { get; set; } = "";

	[JsonPropertyName( "params" )]
	public JsonElement? Params { get; set; }
}

/// <summary>
/// Outgoing response sent back to the MCP server client that issued the request.
/// </summary>
public class BridgeResponse
{
	[JsonPropertyName( "id" )]
	public string Id { get; set; } = "";

	[JsonPropertyName( "success" )]
	public bool Success { get; set; }

	[JsonPropertyName( "data" )]
	[JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
	public object Data { get; set; }

	[JsonPropertyName( "error" )]
	[JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
	public string Error { get; set; }

	public static BridgeResponse Ok( string id, object data = null ) =>
		new() { Id = id, Success = true, Data = data };

	public static BridgeResponse Fail( string id, string error ) =>
		new() { Id = id, Success = false, Error = error };
}
