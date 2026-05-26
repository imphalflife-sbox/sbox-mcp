using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SboxMcp.Server.Bridge;

namespace SboxMcp.Server.Tools;

[McpServerToolType]
public static class ExecutionTools
{
    [McpServerTool(Name = "execute_csharp")]
    [Description("Execute a C# expression or statement in the s&box editor context. Returns the result or output.")]
    public static async Task<string> ExecuteCSharp(
        EditorBridgeServer bridge,
        [Description("C# code to execute")] string code,
        CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("execute.csharp", new { code }, ct);
        return response.Success
            ? response.Data?.ToString() ?? "(no output)"
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "console_run")]
    [Description("Run a console command in the s&box console")]
    public static async Task<string> ConsoleRun(
        EditorBridgeServer bridge,
        [Description("Console command to run")] string command,
        CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("console.run", new { command }, ct);
        return response.Success
            ? response.Data?.ToString() ?? "(no output)"
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "get_bridge_status")]
    [Description("Check if the s&box editor bridge is connected, and how responsive it is.")]
    public static async Task<string> GetBridgeStatus(EditorBridgeServer bridge, CancellationToken ct)
    {
        if (!bridge.IsConnected)
        {
            return $"Bridge status: not connected\nBridge URL: ws://localhost:{bridge.Port}/";
        }

        // Probe responsiveness via bridge.health — this bypasses MainThread.Queue on
        // the bridge side so it answers even when the editor's main thread is wedged.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            var response = await bridge.SendCommandAsync("bridge.health", null, probeCts.Token);
            if (!response.Success)
            {
                return $"Bridge status: connected but unhealthy\nBridge URL: ws://localhost:{bridge.Port}/\nError: {response.Error}";
            }

            // Pretty-print the health data so a human reader can scan it.
            var json = response.Data is JsonElement el ? el.GetRawText() : "{}";
            return $"Bridge status: connected and responsive\nBridge URL: ws://localhost:{bridge.Port}/\nHealth: {json}";
        }
        catch (OperationCanceledException) when (probeCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return $"Bridge status: connected but unresponsive (health probe timed out after 2s)\nBridge URL: ws://localhost:{bridge.Port}/";
        }
        catch (TimeoutException)
        {
            return $"Bridge status: connected but unresponsive (health probe timed out after 2s)\nBridge URL: ws://localhost:{bridge.Port}/";
        }
    }
}
