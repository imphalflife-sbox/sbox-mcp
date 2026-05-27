# sbox-mcp

MCP server for the s&box game engine editor.

## Architecture

Two-component system:

- **SboxMcp.Server** (`src/SboxMcp.Server/`) — .NET 9 MCP server. Speaks stdio to AI clients, hosts a WebSocket server on port 29015 for the editor bridge. Build with `dotnet build`. Nullable reference types are **enabled** here.
- **SboxMcp.Bridge** (`src/SboxMcp.Bridge/`) — s&box editor addon (C# source compiled by s&box's Roslyn pipeline). NOT a .NET project — do not try to `dotnet build` it. Install by copying files into `addons/tools/Code/McpBridge/` inside the s&box install directory. Nullable reference types are **disabled** here.

The two trees mirror each other 1:1 by area: `Server/Tools/{Area}Tools.cs` (MCP-facing) pairs with `Bridge/code/Handlers/{Area}Handler.cs` (scene-facing). Keep both in sync when adding/changing per-area functionality.

### stdio transport — never write to stdout

The server uses `WithStdioServerTransport()`. **Anything written to stdout from server code corrupts the MCP protocol.** Never use `Console.WriteLine` / `Console.Write` in `SboxMcp.Server`. Use `ILogger` (routes to stderr via the default .NET host) for all diagnostics.

## Communication Protocol

WebSocket JSON messages between server and bridge:

```
Request:  { "id": "uuid", "command": "scene.list", "params": {} }
Response: { "id": "uuid", "success": true, "data": { ... } }
Error:    { "id": "uuid", "success": false, "error": "message" }
```

### Parameter Name Mapping

The MCP server tool parameters use camelCase names (e.g. `objectId`, `componentType`), but the bridge handlers expect shorter names. Always map in `SendCommandAsync`:

- `objectId` → `id` (bridge expects `"id"`)
- `componentType` → `type` (bridge expects `"type"`)
- `query` → `pattern` (bridge expects `"pattern"` for scene.find)

## s&box API Notes

### General

- The bridge compiles as part of `local.toolbase` — files go in `addons/tools/Code/McpBridge/`
- Global imports (`Editor`, `Sandbox`, `System`, etc.) are provided by `Imports.cs` — do not add using statements for these
- Do not use nullable reference annotations (`string?`) in bridge code — s&box compiles it without `#nullable enable` (server code is opposite — annotations required)
- `MathF` does not exist — use `float.Sin()`, `float.Pi`, etc. for math operations in game project code
- `Log.OnEntry` does not exist — there is no event-based log capture API

### Scene Access

- Use `SceneEditorSession.Active.Scene` for the editor scene — NOT `Game.ActiveScene` (that's play-mode only)
- Use `scene.GetAllObjects(false)` to enumerate objects — NOT `scene.Children` (not a valid Scene API)
- Use `scene.Directory.FindByGuid(guid)` or `scene.Directory.FindByName(name)` for lookups
- Root objects in a scene have `go.Parent is null`

### Editor APIs

- Use `EditorTypeLibrary` (not `TypeLibrary`) for editor-context type lookups — `TypeLibrary` won't find game project types from editor addon code
- Use `SceneEditorSession.Active` for selection, undo, save operations
- `session.HasUnsavedChanges` (not `IsDirty`) for dirty state
- `scene.Source.ResourcePath` (not `session.SourcePath`) for scene file path
- `Scene.CreateEditorScene()` exists but `Scene.CreateGameScene()` does not
- s&box editor widgets extend `Widget` with `[Dock("Editor", "Name", "icon")]`, NOT `EditorWindow`
- Widget lifecycle: constructor for setup, `[EditorEvent.Frame]` for updates, `OnDestroyed()` for cleanup
- Use `.IsValid()` to check if a widget is still alive, not null checks
- Use `Rotation.From(pitch, yaw, roll)` not `FromEulerAngles`
- Disambiguate `Sandbox.FileSystem` and `Sandbox.ConsoleSystem` explicitly

### Component Property Setting

When setting component properties via string values, `Convert.ChangeType` only handles primitives. Special handling needed for:

- `Model` → `Model.Load(path)` for `.vmdl` paths, or `Package.Fetch + MountAsync + Model.Load` for cloud idents
- `Material` → `Material.Load(path)` for `.vmat` paths, or fetch+mount for cloud idents
- `Color` → `Color.Parse(value)`
- `Vector3` → parse `"x,y,z"` format
- `Angles` → parse `"pitch,yaw,roll"` format

Check `targetType.Name` (not `typeof()` comparison) since types from `TypeLibrary` may not match direct CLR types.

### Cloud Assets

- `Package.FindAsync(query, take: N)` returns a `FindResult` — iterate `.Packages`, not the result directly
- `Package.Fetch(ident, true)` fetches with asset download; `false` without
- `pkg.MountAsync()` must be called on the **main thread** — do not wrap in `Task.Run`
- `pkg.TypeName` (not `pkg.PackageType` which is obsolete)
- `pkg.GetMeta("PrimaryAsset", "")` returns the main asset path after mounting
- Mount is runtime-only — assets are lost on restart unless the ident is in `.sbproj` `PackageReferences`
- Auto-add cloud idents to `PackageReferences` when mounting or setting on components
- `Project.GetProjectFile()` does not exist — find `.sbproj` by scanning `Project.Current.GetAssetsPath()` parent

### File System

- `Sandbox.FileSystem.Mounted` is read-only for project files
- `Project.Current.GetAssetsPath()` returns the `Assets/` directory
- Game code (`.cs` files) lives in `code/` not `Assets/` — route writes accordingly
- `Project.Current.GetRootPath()` may not exist — derive root from `GetAssetsPath()` parent

## Build & Test

```bash
# Build the MCP server (solution-level — the bridge is not a .NET project, so the .sln only contains the server)
dotnet build sbox-mcp.sln -c Release
# or just the server project
dotnet build src/SboxMcp.Server -c Release

# Run the MCP server (for testing — normally launched by Claude Code)
dotnet run --project src/SboxMcp.Server
```

**Sync bridge files to s&box** (adjust path to your s&box install):

```bash
# Git Bash / WSL
cp -r src/SboxMcp.Bridge/code/* "/c/Program Files (x86)/Steam/steamapps/common/sbox/addons/tools/Code/McpBridge/"
```

```powershell
# PowerShell
Copy-Item -Recurse -Force src\SboxMcp.Bridge\code\* "C:\Program Files (x86)\Steam\steamapps\common\sbox\addons\tools\Code\McpBridge\"
```

**No automated test suite exists.** There is no `dotnet test` target. Verify changes by running the server, connecting the bridge in s&box, and exercising the affected tool from an MCP client.

## Documentation

Keep `README.md` up-to-date with any user-facing changes. The README is the first thing someone sees when they visit the repo. If a change adds/removes tools, modifies setup steps, changes configuration, or alters the architecture, update the README in the same commit. The tools table and setup instructions are the most common sections that need updating.

## Adding New Tools

1. Add the MCP tool method in `src/SboxMcp.Server/Tools/` (use `[McpServerTool]` attribute)
2. Add the bridge handler in `src/SboxMcp.Bridge/code/Handlers/`
3. Register the command in `CommandRouter.cs`
4. Map parameter names correctly (see Parameter Name Mapping above)
5. Copy updated bridge files to the s&box install directory
6. s&box hot-reloads C# changes — restart only needed if compilation fails

## Common Pitfalls

- **Port conflict on startup**: Server auto-kills stale `SboxMcp.Server` processes, but if another app holds port 29015 it will fail
- **Hot-reload not triggering**: After a failed compile, s&box may not retry automatically — restart s&box
- **Cloud models disappear on restart**: Ensure the package ident is in `.sbproj` `PackageReferences`
- **Type not found for game components**: Use `EditorTypeLibrary` first, fall back to `TypeLibrary`
- **Bridge compiles but tools fail**: Check the s&box console/log at `<sbox-install>/logs/sbox-dev.log`
- **Bridge appears connected but commands hang or return "Unknown command"**: usually a hotload-related issue. `get_bridge_status` now distinguishes "connected but unresponsive" (main thread wedged — wait a few seconds, the watchdog will return the error) from "connected and responsive" (with `bridgeAssemblyVersion` and `secondsSinceLastCommand` for diagnosis). If you see "Unknown command", check the bridge build — the dispatch table should be a switch expression in `CommandRouter.Dispatch`, not a static delegate dictionary (the dictionary form breaks across hotload because the project-local delegate type can't cast between assembly versions). Watchdog timeout: `sbox_mcp_main_thread_timeout_ms` ConVar.
- **`[Event]` handlers run on the editor main thread**: never sync-wait on async I/O from inside one (`.GetAwaiter().GetResult()`, `.Result`, `.Wait()`). The watchdog only covers `CommandRouter.Route` dispatches — it cannot save you from a deadlock in an event handler. WebSocket close in particular: use `_ws.Abort()` (synchronous, lock-free) rather than awaiting `CloseAsync` — `CloseAsync` blocks on locks held by `ReceiveAsync`, which can only release after a handshake the wedged main thread can't pump. See `docs/superpowers/specs/2026-05-25-bridge-hotload-resilience-design.md` Addendum 2.
