# Bridge Hotload Resilience Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the sbox-mcp bridge survive s&box editor hotloads — detect them, recover the WebSocket cleanly, time-bound stuck main-thread dispatches, and surface responsiveness so users can tell connected-vs-wedged apart.

**Architecture:** Three additive changes on the bridge side and one on the server side, plus a small companion refactor in tower_defense. Each change is independently revertable. Implementation lives on a `feature/hotload-resilience` branch off our cleaned `main`. Verification is manual (no test framework exists for s&box addon code); each phase ends with explicit MCP-tool-call checks.

**Tech Stack:** C# / .NET 9 (server side), s&box editor addon C# (bridge side, compiled by s&box's Roslyn pipeline — `dotnet build` does NOT apply), `dotnet build` for server only.

**Spec:** `docs/superpowers/specs/2026-05-25-bridge-hotload-resilience-design.md`

---

## Conventions for this plan

- "Bridge files" = anything under `src/SboxMcp.Bridge/code/` — these are s&box addon source. After editing, sync to `<sbox-install>/addons/tools/Code/McpBridge/` (s&box hot-reloads automatically).
- "Server files" = anything under `src/SboxMcp.Server/` — normal .NET 9. Rebuild with `dotnet build src/SboxMcp.Server -c Release`. The MCP server is launched by Claude Code as a subprocess per session, so to pick up changes you'll need to restart the CC session OR kill the existing `SboxMcp.Server.exe`.
- "Verify with editor" = use MCP tools (`mcp__sbox__get_bridge_status`, `mcp__sbox__editor_console_output`, etc.) from the running CC session.
- Sync command (run from repo root in PowerShell): `Copy-Item src/SboxMcp.Bridge/code/* "C:/Program Files (x86)/Steam/steamapps/common/sbox/addons/tools/Code/McpBridge/" -Recurse -Force`

## File Structure

**Files modified or created across all phases:**

| Path | Purpose | Status |
|---|---|---|
| `src/SboxMcp.Bridge/code/McpEditorTool.cs` | Add hotload event handler; small log additions | Modify |
| `src/SboxMcp.Bridge/code/CommandRouter.cs` | Watchdog timeout, bypass-queue path for `bridge.*` commands, track responsiveness state | Modify |
| `src/SboxMcp.Bridge/code/Handlers/BridgeHandler.cs` | New `bridge.health` handler — does NOT dispatch through main thread | Create |
| `src/SboxMcp.Server/Bridge/BridgeMessage.cs` | Add `BridgeHealthData` record for response shape | Modify |
| `src/SboxMcp.Server/Tools/ExecutionTools.cs` | Extend `get_bridge_status` to fold in `bridge.health` | Modify |
| `README.md` | Note hotload resilience under Architecture | Modify |
| `CLAUDE.md` | Document watchdog ConVar + `bridge.health` for future contributors | Modify |
| `~/.../tower_defense/Code/UI/SwapModal.razor` | Replace inline onclick lambdas with named handlers | Modify (companion) |
| `~/.../tower_defense/Code/UI/ShopMenu.razor` | Replace inline onclick lambdas with named handlers | Modify (companion) |
| `~/.../tower-defense/memory/reference_sbox_mcp_lifecycle.md` | Document new resilience surface | Modify (memory) |
| `~/.../tower-defense/memory/reference_sbox_razor_lambda_hotload.md` | New memory: Razor inline lambdas + hotload | Create (memory) |

**Boundaries:** The hotload handler stays in `McpEditorTool.cs` (lifecycle owner). The watchdog logic is in `CommandRouter.cs` (already the dispatch funnel). The new `bridge.health` handler is its own file (`BridgeHandler.cs`) to keep handlers focused. Server-side wire format goes in `BridgeMessage.cs` (already where shared shapes live).

---

## Phase 0: Fork cleanup

### Task 0.1: Cherry-pick FUNDING.yml from upstream

**Files:**
- Modify: `.github/FUNDING.yml` (created)

- [ ] **Step 1: Verify upstream/main is fetched**

Run: `git fetch upstream`
Expected: `From https://github.com/StephenSHorton/sbox-mcp` (or "Already up to date")

- [ ] **Step 2: Verify FUNDING.yml commit SHA**

Run: `git log upstream/main --oneline -5`
Expected: includes `727421d Create FUNDING.yml`. If the SHA differs (upstream advanced), use the new SHA in step 4.

- [ ] **Step 3: Check out main**

Run: `git checkout main`
Expected: `Switched to branch 'main'`

- [ ] **Step 4: Cherry-pick FUNDING.yml**

Run: `git cherry-pick 727421d`
Expected: `[main <newsha>] Create FUNDING.yml` with no conflicts (it's a new file).

- [ ] **Step 5: Verify**

Run: `git log --oneline -3`
Expected: top commit is the FUNDING.yml cherry-pick; below it is `b7f28ae Fix editor.play / editor.stop to use full F5 flow` (still present — dropped in next task).

### Task 0.2: Drop duplicate b7f28ae editor.play commit

The local `b7f28ae` and upstream's `bd06d19` have the same diff content but different SHAs. Upstream merged ours via PR #2. We drop the local one so our main doesn't carry the duplicate.

**Files:** none (history rewrite only).

- [ ] **Step 1: Confirm b7f28ae content matches bd06d19**

Run: `git diff b7f28ae~..b7f28ae > /tmp/local.diff; git diff bd06d19~..bd06d19 > /tmp/upstream.diff; git diff /tmp/local.diff /tmp/upstream.diff`
Expected: empty output (diffs identical).

If different, STOP and check with the user — content drift means we can't safely drop.

- [ ] **Step 2: Identify b7f28ae's parent**

Run: `git log --oneline b7f28ae~1 -1`
Expected: shows `3ca7381 Drop self-emitted bridge chatter from diagnostics ring` (this is the commit we want to keep; b7f28ae sits on top of it).

- [ ] **Step 3: Rebase to drop b7f28ae**

Run: `git rebase --onto b7f28ae~1 b7f28ae main`
Expected: rebases everything after b7f28ae onto its parent, dropping b7f28ae. With only the FUNDING.yml commit after it, the rebase result is: `<FUNDING.yml sha> Create FUNDING.yml` on top of `3ca7381`.

- [ ] **Step 4: Verify**

Run: `git log --oneline -5`
Expected:
```
<sha>      Create FUNDING.yml
3ca7381    Drop self-emitted bridge chatter from diagnostics ring
57c1178    Add compile diagnostics + real log capture (replace ConsoleCapture stub)
dd7ef4f    Invert bridge transport so multiple MCP clients can share one editor
7a06fbb    Use install-dir placeholder instead of opinionated path
```

- [ ] **Step 5: Push main (after user confirmation)**

PROMPT THE USER: "Main has been cleaned (FUNDING.yml cherry-picked, duplicate b7f28ae dropped). Push to origin/main? `git push origin main --force-with-lease`"

Only run after the user confirms. `--force-with-lease` because we rewrote history.

### Task 0.3: Create feature branch

- [ ] **Step 1: Create and switch to feature branch**

Run: `git checkout -b feature/hotload-resilience`
Expected: `Switched to a new branch 'feature/hotload-resilience'`

- [ ] **Step 2: Cherry-pick the design doc commit**

The design doc was committed on `fix/editor-play-full-flow` as `ee01a7f` (see `git log fix/editor-play-full-flow -3`). Bring it onto the new branch.

Run: `git cherry-pick ee01a7f`
Expected: design doc applied cleanly.

- [ ] **Step 3: Verify**

Run: `git log --oneline -3`
Expected: design doc on top, then FUNDING.yml, then 3ca7381.

---

## Phase 1: Hotload event handler

### Task 1.1: Add `[Event("hotloaded")]` to McpBridgeDock

**Files:**
- Modify: `src/SboxMcp.Bridge/code/McpEditorTool.cs`

- [ ] **Step 1: Read current McpEditorTool.cs**

Read the file. Locate `StopClient()` and `StartClient()` methods near the bottom. Locate the `[EditorEvent.Frame]` `Frame()` method (it's already there as an example of event-attribute usage on this class).

- [ ] **Step 2: Add the handler after `Frame()`**

Insert this method right after the closing brace of `Frame()`:

```csharp
/// <summary>
/// Fires after every editor hotload. We tear down the (possibly stale) client
/// and reconnect. The Sandbox.Hotload upgrader migrates this Widget instance's
/// type, but in-flight async Tasks captured by McpBridgeClient may not survive
/// the substitution — recreating the client guarantees a fresh state machine.
/// </summary>
[Event( "hotloaded" )]
public void OnHotloaded()
{
    if ( !this.IsValid() )
        return;

    AddLog( "Hotload detected — restarting client." );
    Log.Info( "[MCP Bridge] Hotload detected — restarting client." );

    StopClient();
    StartClient();
}
```

The `this.IsValid()` guard is critical — after hotload, this method may briefly fire on a stale instance before the type migration completes. See `engine/Sandbox.Tools/Editor/ConsoleWidget.cs:350` for the canonical IsValid-guard pattern.

- [ ] **Step 3: Sync bridge files to s&box**

Run:
```powershell
Copy-Item src/SboxMcp.Bridge/code/* "C:/Program Files (x86)/Steam/steamapps/common/sbox/addons/tools/Code/McpBridge/" -Recurse -Force
```

Expected: no output.

- [ ] **Step 4: Verify s&box hot-reloaded the addon**

Wait ~3 seconds. Then:

Run: `mcp__sbox__editor_console_output`
Expected: recent log includes `[MCP Bridge] Hotload detected — restarting client.` (if a hotload was triggered by the sync) OR no errors. If you see substitution failure errors mentioning `McpBridgeDock`, the build failed — fix and re-sync.

- [ ] **Step 5: Trigger a hotload to verify the handler fires**

Edit any `.cs` file in tower_defense (or the bridge itself). Save.

Run: `mcp__sbox__editor_console_output`
Expected: log includes `[MCP Bridge] Hotload detected — restarting client.` followed by `[MCP Bridge] Connecting to ws://localhost:29015/...` and `[MCP Bridge] Connected to MCP server.` within ~5 seconds.

Then: `mcp__sbox__get_bridge_status`
Expected: `Bridge status: connected`.

- [ ] **Step 6: Commit**

```bash
git add src/SboxMcp.Bridge/code/McpEditorTool.cs
git commit -m "$(cat <<'EOF'
Add [Event(\"hotloaded\")] handler to bridge dock

After an editor hotload, the McpBridgeClient's in-flight async Task may
hold a state machine the hotload upgrader can't substitute. The
WebSocket connection stays \"connected\" but no work happens.

Subscribe to the editor's hotloaded event, tear down the stale client,
and start a fresh one. Widget auto-registers via QObject..ctor's
EditorEvent.Register, so the instance method routes correctly.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase 2: Main-thread watchdog

### Task 2.1: Add ConVar and timeout constant

**Files:**
- Modify: `src/SboxMcp.Bridge/code/CommandRouter.cs`

- [ ] **Step 1: Read CommandRouter.cs**

Locate the `Route` method (~line 78). The dispatch block uses `MainThread.Queue(async () => { ... })` with no timeout — that's the line we're hardening.

- [ ] **Step 2: Add the ConVar at the top of the class**

Insert these members at the top of the `CommandRouter` class, immediately after the `Handlers` dictionary closes (after the `}` of `private static readonly Dictionary<string, HandlerFunc> Handlers = new() { ... };`):

```csharp
[ConVar( "sbox_mcp_main_thread_timeout_ms", ConVarFlags.Protected, Min = 1000, Max = 600_000,
    Help = "Max time the bridge waits for the editor main thread to dispatch a command before failing the request." )]
public static int MainThreadTimeoutMs { get; set; } = 15_000;
```

- [ ] **Step 3: Wrap the await in WaitAsync**

In `Route`, find:

```csharp
data = await tcs.Task;
```

Replace with:

```csharp
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
```

Note: the queued lambda still runs whenever the main thread frees up; its result is discarded (the TCS is already resolved/abandoned, and `BridgeResponse` was already sent). That's intentional — `MainThread.Queue` has no cancellation primitive.

- [ ] **Step 4: Sync bridge files**

Run:
```powershell
Copy-Item src/SboxMcp.Bridge/code/* "C:/Program Files (x86)/Steam/steamapps/common/sbox/addons/tools/Code/McpBridge/" -Recurse -Force
```

- [ ] **Step 5: Verify the ConVar registered**

Run: `mcp__sbox__console_run` with command `sbox_mcp_main_thread_timeout_ms`
Expected: response shows current value `15000`.

- [ ] **Step 6: Verify normal commands still work**

Run: `mcp__sbox__editor_scene_info`
Expected: returns scene info within timeout. No "did not dispatch" error.

- [ ] **Step 7: Stress-test (optional, manual)**

Lower the ConVar to simulate stress:

Run: `mcp__sbox__console_run` with command `sbox_mcp_main_thread_timeout_ms 50`

Then trigger a long main-thread operation while immediately calling another bridge command. The second call should return the watchdog error rather than hanging. Restore: `sbox_mcp_main_thread_timeout_ms 15000`.

Skip this step if no obvious way to stress the main thread is available.

- [ ] **Step 8: Commit**

```bash
git add src/SboxMcp.Bridge/code/CommandRouter.cs
git commit -m "$(cat <<'EOF'
Time-bound MainThread.Queue dispatch in CommandRouter

When the editor's main thread is wedged (hotload cascade, ConsoleOverlay
crash, etc.), MainThread.Queue accepts the work but never runs it.
Bridge commands hung indefinitely; the server-side 30s timeout reported
a generic failure.

Wrap the TaskCompletionSource await in WaitAsync with a configurable
timeout (ConVar sbox_mcp_main_thread_timeout_ms, default 15s). On
timeout, return a structured error naming the cause so the caller can
distinguish hotload-cascade hangs from genuine failures.

The queued work is not cancelled — it runs when the main thread frees
up and its result is discarded, which is fine since the response has
already been sent.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

### Task 2.2: Track responsiveness state on CommandRouter

We need `_lastCommandCompletedAt` and `_currentCommand` to power the `bridge.health` endpoint in Phase 3. Track them in CommandRouter since it's the funnel; they're static so they survive hotload via Sandbox.Hotload's static-field migration.

**Files:**
- Modify: `src/SboxMcp.Bridge/code/CommandRouter.cs`

- [ ] **Step 1: Add static tracking fields**

After the ConVar from Task 2.1, add:

```csharp
/// <summary>UTC timestamp of the last successfully-completed command. null if no command has completed yet (or since hotload reset it).</summary>
public static DateTime? LastCommandCompletedAt { get; private set; }

/// <summary>The command name currently dispatching, if any. Null when idle.</summary>
public static string CurrentCommand { get; private set; }
```

- [ ] **Step 2: Update CurrentCommand on dispatch and clear on completion**

In `Route`, immediately after the line `McpCommandToast.Show( request.Command );`, add:

```csharp
CurrentCommand = request.Command;
```

In the `try` block of `Route`, after the line `data = await tcs.Task.WaitAsync( ... );` succeeds (i.e., right before `McpCommandToast.Complete( request.Command, true );`), add:

```csharp
LastCommandCompletedAt = DateTime.UtcNow;
CurrentCommand = null;
```

In the `catch ( TimeoutException )` block (from Task 2.1), after the `AddLog` line, add:

```csharp
CurrentCommand = null;
```

In the outer `catch ( Exception ex )` block (handler error), at the top, add:

```csharp
CurrentCommand = null;
```

- [ ] **Step 3: Sync bridge files**

Same `Copy-Item` command as before.

- [ ] **Step 4: Verify state advances**

Run: `mcp__sbox__editor_scene_info`

(No way to verify the static state directly from CC yet — that's what `bridge.health` in Phase 3 enables. Just confirm the command still works.)

- [ ] **Step 5: Commit**

```bash
git add src/SboxMcp.Bridge/code/CommandRouter.cs
git commit -m "$(cat <<'EOF'
Track last-command-completed and current-command in CommandRouter

Static fields so they survive hotload via Sandbox.Hotload's static
field migration. Powers the bridge.health endpoint added in the next
commit so callers can distinguish \"bridge connected\" from \"bridge
actively serving requests\".

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase 3: bridge.health command

### Task 3.1: Create BridgeHandler with the health command

`bridge.health` must NOT dispatch through `MainThread.Queue` — its whole point is to answer when the main thread is wedged. So we introduce a special path in `Route` that calls handlers directly on the receiving thread for any command starting with `bridge.`.

**Files:**
- Create: `src/SboxMcp.Bridge/code/Handlers/BridgeHandler.cs`

- [ ] **Step 1: Create the file**

Create `src/SboxMcp.Bridge/code/Handlers/BridgeHandler.cs`:

```csharp
using System.Reflection;

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
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        object data = new
        {
            connected = true,
            currentCommand = CommandRouter.CurrentCommand,
            lastCommandCompletedAt = lastCompleted?.ToString( "o" ),
            secondsSinceLastCommand = lastCompleted.HasValue
                ? (now - lastCompleted.Value).TotalSeconds
                : (double?) null,
            mainThreadTimeoutMs = CommandRouter.MainThreadTimeoutMs,
            bridgeAssemblyVersion = assemblyVersion,
            commandCount = McpBridgeDock.Current?.CommandCount ?? 0,
        };

        return Task.FromResult( data );
    }
}
```

### Task 3.2: Route bridge.health bypassing MainThread.Queue

**Files:**
- Modify: `src/SboxMcp.Bridge/code/CommandRouter.cs`

- [ ] **Step 1: Register the route**

Add to the `Handlers` dictionary:

```csharp
// Bridge meta-commands (do NOT dispatch through MainThread.Queue — see BridgeHandler)
["bridge.health"] = r => BridgeHandler.Health( r ),
```

- [ ] **Step 2: Add the bypass-queue branch in Route**

In `Route`, immediately after the `if ( !Handlers.TryGetValue(...) )` guard, before the main `try` block, add:

```csharp
// Bridge meta-commands answer directly without queueing onto the main thread.
// This is what lets `bridge.health` respond when the main thread is wedged.
if ( request.Command.StartsWith( "bridge.", StringComparison.Ordinal ) )
{
    try
    {
        var data = await handler( request );
        return BridgeResponse.Ok( request.Id, data );
    }
    catch ( Exception ex )
    {
        Log.Error( $"[MCP Bridge] Bridge meta-handler error for '{request.Command}': {ex.Message}" );
        return BridgeResponse.Fail( request.Id, ex.Message );
    }
}
```

These commands skip the toast and dock-log updates (they'd touch UI, which needs the main thread).

- [ ] **Step 3: Sync bridge files**

Same `Copy-Item` command.

- [ ] **Step 4: Verify bridge.health works**

There's no direct MCP tool for `bridge.health` yet (that's Task 3.3). For now, exercise it via `execute.csharp` or check the server response via wire-level inspection. Skip if neither is convenient — the next task plumbs it through `get_bridge_status` and verifies end-to-end.

- [ ] **Step 5: Commit**

```bash
git add src/SboxMcp.Bridge/code/Handlers/BridgeHandler.cs src/SboxMcp.Bridge/code/CommandRouter.cs
git commit -m "$(cat <<'EOF'
Add bridge.health command that bypasses MainThread.Queue

The whole point of a health endpoint is to answer when the main thread
is wedged. Add a special-case route for any \"bridge.*\" command that
calls the handler directly on the WebSocket receive thread, skipping
MainThread.Queue.

BridgeHandler.Health returns:
- currentCommand: command name mid-flight, if any
- lastCommandCompletedAt + secondsSinceLastCommand: responsiveness signal
- mainThreadTimeoutMs: current watchdog setting
- bridgeAssemblyVersion: changes on every hotload
- commandCount: total commands served

Handlers in BridgeHandler MUST NOT touch editor APIs (no Scene, no
Component, no Widget mutations) — they run on the WebSocket thread.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

### Task 3.3: Extend server-side get_bridge_status

**Files:**
- Modify: `src/SboxMcp.Server/Bridge/BridgeMessage.cs`
- Modify: `src/SboxMcp.Server/Tools/ExecutionTools.cs`

- [ ] **Step 1: Read both files**

Confirm the shape of `BridgeResponse.Data` — it's `object?` deserialized to `JsonElement` typically. Confirm where `get_bridge_status` is defined in `ExecutionTools.cs` (search for `"get_bridge_status"`).

- [ ] **Step 2: Update get_bridge_status to call bridge.health**

In `ExecutionTools.cs`, find the `get_bridge_status` method. The current implementation likely returns just the connection state. Replace its body with something like:

```csharp
[McpServerTool(Name = "get_bridge_status")]
[Description("Check if the s&box editor bridge is connected, and how responsive it is.")]
public static async Task<string> GetBridgeStatus(
    EditorBridgeServer bridge,
    CancellationToken ct)
{
    if (!bridge.IsConnected)
    {
        return $"Bridge status: not connected\nBridge URL: ws://localhost:{bridge.Port}/";
    }

    // Probe responsiveness via bridge.health — this bypasses MainThread.Queue
    // so it answers even when the editor's main thread is wedged.
    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    probeCts.CancelAfter(TimeSpan.FromSeconds(2));

    try
    {
        var response = await bridge.SendCommandAsync("bridge.health", null, probeCts.Token);
        if (!response.Success)
        {
            return $"Bridge status: connected but unhealthy\nBridge URL: ws://localhost:{bridge.Port}/\nError: {response.Error}";
        }

        var json = response.Data is JsonElement el ? el.GetRawText() : "{}";
        return $"Bridge status: connected and responsive\nBridge URL: ws://localhost:{bridge.Port}/\nHealth: {json}";
    }
    catch (TimeoutException)
    {
        return $"Bridge status: connected but unresponsive (health probe timed out)\nBridge URL: ws://localhost:{bridge.Port}/";
    }
    catch (OperationCanceledException) when (probeCts.IsCancellationRequested && !ct.IsCancellationRequested)
    {
        return $"Bridge status: connected but unresponsive (health probe timed out)\nBridge URL: ws://localhost:{bridge.Port}/";
    }
}
```

Adjust the exact parameter list and DI shape based on what the existing method uses — keep the same signature pattern so other tools that depend on it continue to work.

- [ ] **Step 3: Add `using System.Text.Json;`** if not already imported.

- [ ] **Step 4: Build the server**

Run: `dotnet build src/SboxMcp.Server -c Release`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 5: Restart the MCP server**

PROMPT THE USER: "I need to restart the MCP server to pick up the changes. Either: (a) `/exit` and start a new CC session here, OR (b) I can kill the existing `SboxMcp.Server.exe` process and the next tool call will respawn it. Which?"

Wait for user response. If (b), run: `Get-Process SboxMcp.Server -ErrorAction SilentlyContinue | Stop-Process -Force`

- [ ] **Step 6: Verify end-to-end**

Run: `mcp__sbox__get_bridge_status`
Expected output now includes a `Health: {...}` JSON line with `currentCommand`, `lastCommandCompletedAt`, `secondsSinceLastCommand`, `mainThreadTimeoutMs`, `bridgeAssemblyVersion`, `commandCount`.

- [ ] **Step 7: Trigger a hotload and re-check**

Edit any `.cs` file in tower_defense. Save. Wait ~5s.

Run: `mcp__sbox__get_bridge_status`
Expected: still `connected and responsive`. The `bridgeAssemblyVersion` may have changed if the bridge was rebuilt; `commandCount` may have reset (if static state was rebuilt) — either is OK.

- [ ] **Step 8: Commit**

```bash
git add src/SboxMcp.Server/
git commit -m "$(cat <<'EOF'
Fold bridge.health into get_bridge_status

get_bridge_status now reports not just \"is the WebSocket connected\"
but \"is the bridge actually answering requests\". Probes bridge.health
with a 2s timeout. Distinguishes three states: not connected, connected
but unresponsive (health probe timed out), connected and responsive
(with last-command-completed timestamps).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase 4: Docs

### Task 4.1: Update README and CLAUDE.md

**Files:**
- Modify: `README.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Read both files**

Locate the Architecture section in README.md and find a natural place to add a "Hotload resilience" subsection. Locate "Common Pitfalls" in CLAUDE.md.

- [ ] **Step 2: Add to README.md**

Under the Architecture section (or wherever transport behavior is described), add:

```markdown
### Hotload resilience

The bridge subscribes to the editor's `hotloaded` event and tears down + reconnects its WebSocket after every hotload. Bridge command dispatches are time-bounded by the `sbox_mcp_main_thread_timeout_ms` ConVar (default 15s); if the editor's main thread is wedged, commands fail with a structured error rather than hanging. `get_bridge_status` probes `bridge.health` (which bypasses the main thread) to report whether the bridge is actually responsive, not just connected.
```

- [ ] **Step 3: Add to CLAUDE.md under "Common Pitfalls"**

Add:

```markdown
- **Bridge appears connected but commands hang**: usually a hotload cascade has wedged the editor's main thread. `get_bridge_status` now distinguishes this — look for "connected but unresponsive" in the output. The watchdog in `CommandRouter.Route` will return a structured error after `sbox_mcp_main_thread_timeout_ms` (default 15000); tune via the ConVar if needed. Bridge auto-reconnects on hotload via `[Event("hotloaded")]` in `McpEditorTool.cs`.
```

- [ ] **Step 4: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "$(cat <<'EOF'
Document hotload resilience in README and CLAUDE.md

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Phase 5: Companion work — tower_defense Razor refactor

This work lives in the sibling repo at `C:\Users\Jimmy\Documents\s&box projects\tower_defense\` and is committed there, NOT here.

### Task 5.1: Refactor SwapModal.razor inline lambdas

**Files:**
- Modify: `C:\Users\Jimmy\Documents\s&box projects\tower_defense\Code\UI\SwapModal.razor`

- [ ] **Step 1: Read the file**

Identify every `onclick="@(() => ...)"` pattern. Each one is an inline lambda that generates a `<>c__DisplayClass*.<BuildRenderTree>b__*` closure the hotload upgrader struggles with.

- [ ] **Step 2: For each inline lambda, extract to a named method in the `@code` block**

Pattern — replace:

```razor
<button onclick="@(() => DoThing(arg))">Click</button>
```

With:

```razor
<button onclick="@OnDoThingClicked">Click</button>

@code {
    void OnDoThingClicked() => DoThing(SomeFieldOrProperty);
}
```

If the lambda captures a loop variable (`@foreach (var x in items) { <button onclick="@(() => F(x))"> })`, hoist with a per-iteration handler factory:

```razor
@foreach (var x in items)
{
    <button onclick="@(MakeOnClick(x))">...</button>
}

@code {
    Action MakeOnClick(MyType x) => () => F(x);
}
```

This still uses a lambda but moves it OUT of `BuildRenderTree` — the generated closure is now in user code, which the hotload upgrader handles more reliably. (It's an improvement, not a complete fix; the deeper bug is engine-side.)

- [ ] **Step 3: Save, let s&box hot-reload, verify the modal still works**

Open the swap modal in-game (or in editor play mode). Verify clicks register the expected actions.

- [ ] **Step 4: Commit in tower_defense**

```bash
cd "C:\Users\Jimmy\Documents\s&box projects\tower_defense"
git add Code/UI/SwapModal.razor
git commit -m "$(cat <<'EOF'
SwapModal: extract inline onclick lambdas to named handlers

Reduces the Razor BuildRenderTree closure surface that the s&box
hotload upgrader struggles to substitute. See sbox-mcp design doc
docs/superpowers/specs/2026-05-25-bridge-hotload-resilience-design.md
for context on the engine-side substitution failures these closures
trigger.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

### Task 5.2: Refactor ShopMenu.razor inline lambdas

Same pattern as Task 5.1, applied to `C:\Users\Jimmy\Documents\s&box projects\tower_defense\Code\UI\ShopMenu.razor`.

- [ ] **Step 1: Read the file**
- [ ] **Step 2: Extract each inline lambda to a named handler (using the patterns from Task 5.1)**
- [ ] **Step 3: Save, let s&box hot-reload, verify the shop menu still works**
- [ ] **Step 4: Commit in tower_defense with an analogous message**

---

## Phase 6: Memory updates

These live in `C:\Users\Jimmy\.claude\projects\C--Users-Jimmy-Documents-s-box-projects-tower-defense\memory\` — the tower-defense conversation memory directory.

### Task 6.1: Update reference_sbox_mcp_lifecycle.md

**Files:**
- Modify: `C:\Users\Jimmy\.claude\projects\C--Users-Jimmy-Documents-s-box-projects-tower-defense\memory\reference_sbox_mcp_lifecycle.md`

- [ ] **Step 1: Read current content**

- [ ] **Step 2: Add a new section near the bottom**

Append:

```markdown
## Hotload resilience (added 2026-05-25)

Bridge now subscribes to `[Event("hotloaded")]` in `McpEditorTool.cs` and tears down + reconnects the client after every editor hotload. Command dispatch is time-bounded by `sbox_mcp_main_thread_timeout_ms` ConVar (default 15s); when the main thread is wedged (hotload cascade, ConsoleOverlay crash), commands fail fast with a structured error instead of hanging.

`get_bridge_status` now probes `bridge.health` (which bypasses MainThread.Queue) and reports three distinct states: not connected, connected but unresponsive, connected and responsive. The third path includes `secondsSinceLastCommand` and `currentCommand` for liveness signal.

**How to apply:** If the bridge appears wedged, check `get_bridge_status` first — if it says "connected but unresponsive", the editor's main thread is the problem (hotload cascade likely), not the bridge connection. Wait a few seconds; the watchdog will free the request.
```

### Task 6.2: Create reference_sbox_razor_lambda_hotload.md memory

**Files:**
- Create: `C:\Users\Jimmy\.claude\projects\C--Users-Jimmy-Documents-s-box-projects-tower-defense\memory\reference_sbox_razor_lambda_hotload.md`
- Modify: `C:\Users\Jimmy\.claude\projects\C--Users-Jimmy-Documents-s-box-projects-tower-defense\memory\MEMORY.md`

- [ ] **Step 1: Create the memory file**

Write:

```markdown
---
name: sbox-razor-lambda-hotload
description: Inline @(() => ...) onclick lambdas in Razor templates generate BuildRenderTree closures the s&box hotload upgrader can't substitute reliably — prefer named handlers
metadata:
  type: reference
---

Razor templates that embed `onclick="@(() => Foo(x))"` generate compiler-synthesized closures (e.g., `Sandbox.SwapModal.<>c__DisplayClass1_5.<BuildRenderTree>b__6`). When the editor hotloads a project that contains these, the Sandbox.Hotload upgrader walks engine static state — including `EventSystem.AllTargets._weakTable` — and tries to substitute the closure method to the new assembly's version. The substitution fails because the synthesized class name is unstable across compilations, producing log spam like:

```
Unable to find matching substitution for a lambda method.
  Member: Sandbox.SwapModal.<>c__DisplayClass1_5.<BuildRenderTree>b__6
```

The spam alone is mostly cosmetic, but it amplifies into the ConsoleOverlay panel-tree bug ([[reference_sbox_console_overlay_panel_bug]]) and contributes to the cascade that wedges the editor's main thread (see [[reference_sbox_mcp_lifecycle]] hotload section).

**Mitigation:** prefer named handler methods in `@code` blocks. Replace:

```razor
<button onclick="@(() => Foo(x))">
```

with:

```razor
<button onclick="@OnFooClicked">
@code { void OnFooClicked() => Foo(_x); }
```

For loop-captured variables, hoist via a handler factory in `@code` — moves the closure out of `BuildRenderTree`, which the upgrader handles better:

```razor
@foreach (var x in items)
{
    <button onclick="@(MakeClick(x))">
}
@code { Action MakeClick(Thing x) => () => Foo(x); }
```

This is a **mitigation, not a fix**. The engine-side substitution failure also fires for engine code (e.g., `TaskFactory<HttpListenerContext>.FromAsyncImpl`), which we can't refactor. Refactoring our Razor reduces the surface area and the cascade severity.

**How to apply:** When adding interactive Razor elements, default to named handlers in the `@code` block instead of inline lambdas. Audit existing files with `Grep "onclick=\"@\(\(\)" Code/UI/`.
```

- [ ] **Step 2: Add the index entry to MEMORY.md**

Add a line in the appropriate section (probably under "Reference" or "s&box engine quirks"):

```markdown
- [Razor inline lambdas + hotload](reference_sbox_razor_lambda_hotload.md) — inline @(() => ...) generates closures the hotload upgrader can't substitute; use named handlers
```

---

## Phase 7: Final verification

### Task 7.1: End-to-end soak

- [ ] **Step 1: Confirm starting state**

Run: `mcp__sbox__get_bridge_status`
Expected: `connected and responsive` with health JSON.

- [ ] **Step 2: 30-minute working session**

Use the bridge for normal work — edit files, save, trigger hotloads. After every save:

- Wait 5s for hotload to settle.
- Call any MCP bridge tool (e.g., `editor_console_output`, `editor_scene_info`).
- Expected: tool responds within 15s. No need to restart the editor.

Repeat across at least: a `.cs` file edit, a `.razor` file edit, an edit in the `imp-ui-framework` junction (if available).

- [ ] **Step 3: Failure-mode check**

If any tool call returns "Main thread did not dispatch ... within 15000ms" — that's the watchdog firing. Expected during heavy hotload cascades. Wait a few seconds and retry; should succeed.

If any tool call returns "Bridge status: connected but unresponsive" from `get_bridge_status` — `bridge.health` itself timed out. That's worse — means even the bypass-queue path is wedged. If this happens, capture the editor log (`<sbox-install>/logs/sbox-dev.log` tail) and file a follow-up investigation; the bridge itself may still need static-facade rescue work (option C from the brainstorm).

- [ ] **Step 4: Push the feature branch (after user confirms)**

PROMPT THE USER: "Hotload resilience verified across a working session. Push the branch to origin? `git push -u origin feature/hotload-resilience`"

Do NOT merge into main yet — leave that to the user.

---

## Self-review notes

- All spec sections covered: hotload event (P1), watchdog (P2), bridge.health (P3), README/CLAUDE.md (P4), Razor refactor companion (P5), memory updates (P6), verification (P7).
- No TBDs or "implement later" stubs; every step shows actual code.
- Types consistent: `LastCommandCompletedAt` and `CurrentCommand` defined in Task 2.2, consumed in Task 3.1 with matching names.
- File path consistent: `BridgeHandler.cs` referenced in all 3 places it's needed.
- Verification is manual throughout (no test framework for s&box addon code) — this is honest, not a placeholder.
- Phase 0 (fork cleanup) and Phase 5 (tower_defense companion) explicitly call out that they happen in other repos / on other branches; their commits don't go on `feature/hotload-resilience`.
