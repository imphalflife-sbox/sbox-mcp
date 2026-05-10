using System.ComponentModel;
using ModelContextProtocol.Server;
using SboxMcp.Server.Bridge;

namespace SboxMcp.Server.Tools;

[McpServerToolType]
public static class EditorTools
{
    [McpServerTool(Name = "editor_get_selection")]
    [Description("Get the currently selected GameObjects in the editor")]
    public static async Task<string> GetSelection(EditorBridgeServer bridge, CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("editor.get_selection", null, ct);
        return response.Success
            ? response.Data?.ToString() ?? "(empty)"
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "editor_select_object")]
    [Description("Select a GameObject in the editor by ID")]
    public static async Task<string> SelectObject(
        EditorBridgeServer bridge,
        [Description("The ID of the GameObject to select")] string objectId,
        CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("editor.select", new { objectId }, ct);
        return response.Success
            ? response.Data?.ToString() ?? "Selected."
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "editor_undo")]
    [Description("Undo the last editor action")]
    public static async Task<string> Undo(EditorBridgeServer bridge, CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("editor.undo", null, ct);
        return response.Success
            ? response.Data?.ToString() ?? "Undone."
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "editor_redo")]
    [Description("Redo the last undone editor action")]
    public static async Task<string> Redo(EditorBridgeServer bridge, CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("editor.redo", null, ct);
        return response.Success
            ? response.Data?.ToString() ?? "Redone."
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "editor_save_scene")]
    [Description("Save the current scene")]
    public static async Task<string> SaveScene(EditorBridgeServer bridge, CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("editor.save_scene", null, ct);
        return response.Success
            ? response.Data?.ToString() ?? "Scene saved."
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "editor_take_screenshot")]
    [Description("Take a screenshot of the editor viewport. Returns the file path of the saved screenshot.")]
    public static async Task<string> TakeScreenshot(EditorBridgeServer bridge, CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("editor.screenshot", null, ct);
        return response.Success
            ? response.Data?.ToString() ?? "(no path returned)"
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "scene_get_hierarchy")]
    [Description("Get the full scene hierarchy as indented text for easy reading")]
    public static async Task<string> GetHierarchy(EditorBridgeServer bridge, CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("scene.hierarchy", null, ct);
        return response.Success
            ? response.Data?.ToString() ?? "(empty scene)"
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "editor_play")]
    [Description("Start playing the current scene in the editor")]
    public static async Task<string> Play(EditorBridgeServer bridge, CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("editor.play", null, ct);
        return response.Success
            ? response.Data?.ToString() ?? "Playing."
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "editor_stop")]
    [Description("Stop playing the current scene in the editor")]
    public static async Task<string> Stop(EditorBridgeServer bridge, CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("editor.stop", null, ct);
        return response.Success
            ? response.Data?.ToString() ?? "Stopped."
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "editor_is_playing")]
    [Description("Check whether the editor is currently in play mode")]
    public static async Task<string> IsPlaying(EditorBridgeServer bridge, CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("editor.is_playing", null, ct);
        return response.Success
            ? response.Data?.ToString() ?? "(empty)"
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "editor_scene_info")]
    [Description("Get information about the current scene (name, path, dirty state)")]
    public static async Task<string> SceneInfo(EditorBridgeServer bridge, CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("editor.scene_info", null, ct);
        return response.Success
            ? response.Data?.ToString() ?? "(empty)"
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "editor_console_output")]
    [Description("Get the most recent console log entries from the s&box editor. " +
        "Backed by an in-editor ring buffer (capacity 5000) populated from EditorUtility.AddLogger — " +
        "returns engine + game-code Log.Info/Warning/Error output, oldest-first within the slice. " +
        "Each entry has Seq, Timestamp, Level, Logger, Message, Exception. " +
        "Response also includes a `bridge` block with editor uptime, last compile age, and ring buffer fill.")]
    public static async Task<string> ConsoleOutput(
        EditorBridgeServer bridge,
        [Description("How many most-recent entries to return (1..5000). Default 50.")] int tail,
        CancellationToken ct)
    {
        var clamped = tail <= 0 ? 50 : Math.Min(tail, 5000);
        var response = await bridge.SendCommandAsync("diagnostics.get_logs", new { tail = clamped }, ct);
        return response.Success
            ? response.Data?.ToString() ?? "(no output)"
            : $"Error: {response.Error}";
    }

    [McpServerTool(Name = "editor_compile_diagnostics")]
    [Description("Get the snapshot from the most recent compile that finished after editor startup. " +
        "Returns { available, snapshot?, note?, bridge }. When `available` is false, no compile has " +
        "completed since editor start. The `snapshot` contains CompletedAt, Success, ErrorCount, " +
        "WarningCount, and a Diagnostics array of { Severity, Id, Message, FilePath, Line, Column }. " +
        "Severity includes Hidden (CS8019 unused-using etc.) — filter client-side as needed.")]
    public static async Task<string> CompileDiagnostics(EditorBridgeServer bridge, CancellationToken ct)
    {
        var response = await bridge.SendCommandAsync("diagnostics.get_compile", null, ct);
        return response.Success
            ? response.Data?.ToString() ?? "(no diagnostics)"
            : $"Error: {response.Error}";
    }
}
