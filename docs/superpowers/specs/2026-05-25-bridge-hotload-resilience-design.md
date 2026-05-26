# Bridge Hotload Resilience — Design

**Date:** 2026-05-25
**Author:** Claude (CC session, sbox-mcp repo)
**Spawned by:** `tower_defense/docs/Investigations/2026-05-25-hotload-instability-report.md`
**Status:** Implemented; see Addendum for a post-soak correction.

## Background

The s&box editor hot-reloads code-side changes by swapping assemblies in place. Some failure modes — engine-side panel-tree corruption, Razor closure substitution failures — are not ours to fix. But the sbox-mcp bridge stops responding after most hotload cycles, and that *is* fixable.

The investigation report's H2 attributes the bridge's death to an HttpListener substitution failure. The `_server._clients[]._ws._innerStream._context.Request._memoryBlob._result._asyncCallback` chain it cites belongs to the **bridge's own** `McpBridgeServer._clients[]` — post-transport-inversion (`dd7ef4f`) the bridge is the WebSocket *server* and owns an `HttpListener`. The original draft of this doc claimed the chain was engine-internal because earlier code used `ClientWebSocket`; that claim was wrong. See the Addendum for the remediation.

The bridge actually dies for three different reasons:

1. **Async state-machine substitution failure.** `McpBridgeClient.RunConnectionLoop` runs fire-and-forget (`McpBridgeClient.cs:81`). On hotload, the bridge assembly is swapped; if any in-flight state machine has its method body changed (or transitively references a changed method), the hotload upgrader can't substitute it. The Task hangs in the old assembly's code. The new assembly's instance — migrated, not re-constructed — never restarts the loop.

2. **Main-thread starvation.** Every bridge command flows through `MainThread.Queue` (`CommandRouter.cs:96`) with no timeout. When the hotload error cascade spams the main thread (engine logging, ConsoleOverlay repaint storms), queued work stalls. The WebSocket stays "Connected"; commands hang forever.

3. **ConsoleOverlay engine bug** (documented in `reference_sbox_console_overlay_panel_bug.md`) amplifies modes 1 and 2 by spamming exceptions. Engine code, not ours.

The bridge currently has no hotload subscription, no main-thread watchdog, and no responsiveness reporting — so when these modes hit, the user sees "bridge connected, every command hangs" with no recourse but to restart the editor.

## Goals

- After a hotload, the bridge re-establishes its WebSocket connection without user intervention.
- A hung main thread surfaces as a fast command failure, not an indefinite hang.
- `get_bridge_status` reports *responsiveness* (last successful round-trip), not just connection state.
- Changes are scoped to be upstream-able as a single coherent PR to the original sbox-mcp repo.

## Non-goals

- Filing or fixing engine bugs (ConsoleOverlay, Razor closure substitution). Out of scope.
- Project-side Razor lambda refactor — handled separately in tower_defense (see Companion Work).
- Fork sync / FUNDING.yml cherry-pick — separate task #8.
- Changing the inverted-transport architecture (`dd7ef4f`) or log-capture rewrite (`57c1178`). The fix builds on top.

## Approach: Defense in depth

Three additive changes, each independently useful:

### 1. Hotload event handler on `McpBridgeDock`

`McpBridgeDock` extends `Widget`, which inherits from `QObject`. Engine source confirms `QObject..ctor` calls `EditorEvent.Register(this)` (`engine/Sandbox.Tools/Qt/QObject.cs:30`). So instance `[Event]` methods on the dock auto-register and survive hotload via `OrphanedInstances` (`engine/Sandbox.Event/EventSystem.cs:280-331`).

Add:

```csharp
[Event( "hotloaded" )]
public void OnHotloaded()
{
    if ( !this.IsValid() ) return;
    AddLog( "Hotload detected — restarting client." );
    StopClient();
    StartClient();
}
```

Effect: after every hotload, the bridge tears down the (potentially stale) `McpBridgeClient`, cancels its CTS, disposes its `ClientWebSocket`, and starts a fresh client that dials the server again. The server side (`EditorBridgeServer.ReceiveLoopAsync`) already handles the old socket closing and a new socket connecting cleanly (`EditorBridgeServer.cs:46-83`); pending requests fail fast with `"Bridge disconnected."` (`EditorBridgeServer.cs:74-79`). No server-side change needed for this part.

### 2. Main-thread watchdog on `CommandRouter.Route`

The current dispatch:

```csharp
var tcs = new TaskCompletionSource<object>();
MainThread.Queue( async () => { ... } );
data = await tcs.Task;
```

`MainThread.Queue` enqueues work but never times out. If the main thread is busy (or wedged by an engine cascade), the bridge response to the MCP server is just... never sent. The server's own 30-second `cts.CancelAfter` (`EditorBridgeServer.cs:183`) eventually throws on its side, but Claude Code's tool call sees a 30-second hang followed by a generic timeout error, not "bridge is wedged."

Change: wrap `tcs.Task` in a `WaitAsync` with a configurable timeout (default 15s). On timeout, return a structured error to the server so the tool reports "main thread unresponsive — likely hotload cascade in progress" instead of hanging.

```csharp
const int MainThreadTimeoutMs = 15_000;
try
{
    data = await tcs.Task.WaitAsync( TimeSpan.FromMilliseconds( MainThreadTimeoutMs ) );
}
catch ( TimeoutException )
{
    return BridgeResponse.Fail( request.Id,
        $"Main thread did not dispatch '{request.Command}' within {MainThreadTimeoutMs}ms. " +
        "The editor is likely processing a hotload cascade — wait a few seconds and retry." );
}
```

Note: this does NOT cancel the queued work (no clean cancellation primitive in `MainThread.Queue`). When the main thread frees up, the late work runs and discards its result. That's fine — the response was already sent.

### 3. Responsiveness reporting via `bridge.health`

Add a new bridge command `bridge.health` that responds without going through `MainThread.Queue` (so it works even when the main thread is wedged). It returns:

- `lastCommandCompletedAt` (ISO 8601)
- `secondsSinceLastCommand`
- `currentlyDispatching` (the command name, if any is mid-flight)
- `pendingMainThreadQueueDepth` (best-effort; engine API permitting — `MainThread` does not expose depth, so this may be omitted)
- `assemblyBuildVersion` (from `Assembly.GetExecutingAssembly().GetName().Version` — changes on every hotload)

Server-side: extend `get_bridge_status` to call `bridge.health` (with a short timeout, e.g., 2s) and merge the result into its response. If `bridge.health` times out, report `responsive: false` and explain why.

Track `_lastCommandCompletedAt` and `_currentCommand` as static fields on `CommandRouter` so they're hotload-preserved by Sandbox.Hotload's static-field migration.

### What we are NOT doing

- **No static-facade rewrite** (option C from brainstorm). Widget auto-registration handles event survival; the rewrite adds complexity without clear benefit.
- **No retry-on-timeout in the server.** Surfacing the timeout to the caller is more honest than masking it. The caller (Claude) can retry if it wants.
- **No `BridgeClient` lambda extraction.** The fire-and-forget Task is rebuilt by change 1 on every hotload, so substituting it during the hotload itself is no longer load-bearing.

## Files touched (sbox-mcp)

| File | Change |
|---|---|
| `src/SboxMcp.Bridge/code/McpEditorTool.cs` | Add `[Event("hotloaded")]` handler; small log additions |
| `src/SboxMcp.Bridge/code/CommandRouter.cs` | Add 15s timeout on `MainThread.Queue`; track `_lastCommandCompletedAt` and `_currentCommand` static fields |
| `src/SboxMcp.Bridge/code/Handlers/EditorHandler.cs` *or* new `Handlers/BridgeHandler.cs` | Add `bridge.health` handler that does NOT dispatch through `MainThread.Queue` |
| `src/SboxMcp.Bridge/code/CommandRouter.cs` | Register `bridge.health` route; route bypasses main-thread dispatch |
| `src/SboxMcp.Server/Tools/ExecutionTools.cs` (or wherever `get_bridge_status` lives) | Extend response with `bridge.health` data |
| `README.md` | Add a brief "hotload resilience" note to the architecture section |
| `CLAUDE.md` | Document the watchdog timeout + `bridge.health` for future contributors |

Estimated diff: ~150-200 LOC including tests/comments.

## Companion work (out of repo)

- **tower_defense Razor refactor**: replace inline `onclick="@(() => ...)"` in `Code/UI/SwapModal.razor` and `Code/UI/ShopMenu.razor` with named handler methods in `@code` blocks. Reduces lambda-closure surface for hotload substitution. Committed separately on tower_defense.
- **Memory updates** in `~/.claude/projects/C--Users-Jimmy-Documents-s-box-projects-tower-defense/memory/`:
  - Update `reference_sbox_mcp_lifecycle.md` with the watchdog + `bridge.health` pattern.
  - New memory: Razor inline lambda pattern + hotload surface (cross-link H1 from the report).

## Fork hygiene & implementation home

The fork stays divergent — we are not currently planning to PR the inverted-transport architecture upstream. The implementation lives on our `main`. Two cleanup steps first so `main` is a clean baseline:

1. **Cherry-pick `727421d` (FUNDING.yml)** from `upstream/main` onto our `main`.
2. **Drop the duplicate `b7f28ae`** editor.play fix from our `main` — upstream merged it as `bd06d19` via PR #2. Use `git rebase --interactive` or `git rebase --onto` to excise just that commit without disturbing `dd7ef4f`/`57c1178`/`3ca7381`.

Then branch `feature/hotload-resilience` off the cleaned `main` and implement the design there.

**Keep the new commits upstream-clean** in case we PR later:

- Each commit is self-contained and reviewable on its own (one of: hotload event, watchdog, health endpoint, docs).
- Commit messages explain the failure mode + the fix, not just the change.
- No incidental refactors mixed in.
- README/CLAUDE.md changes go in their own commit at the end of the series.

If we later decide to PR upstream, the PR would have to include `dd7ef4f` + `57c1178` + `3ca7381` as prerequisites — those are the load-bearing architecture. Our hotload-resilience commits would sit on top, individually cherry-pickable.

## Risk & rollback

- **Risk 1: Hotload event handler fires too aggressively.** If `[Event("hotloaded")]` fires for hotloads of OTHER assemblies (not the bridge's), we'd needlessly tear down a working connection. Engine source (`HotloadManager.DoSwap`) fires `hotloaded` per HotloadManager (typically one per project assembly). The bridge addon is in `addons/tools/` and gets its own hotload events. Worst case: we reconnect on every hotload, which adds ~3 seconds of "disconnected" time. Acceptable.
- **Risk 2: Watchdog timeout false positives.** Some commands (e.g. `asset.search`) legitimately take longer than 15s when waiting on package fetch. **Mitigation**: make the timeout per-command-class (a) `15s` default, (b) `60s` for asset/network commands. Keep simple for v1; expose as constant; tune from feedback.
- **Risk 3: `bridge.health` bypassing `MainThread.Queue` accesses thread-unsafe state.** Only access static atomic fields (`_lastCommandCompletedAt` etc.) and assembly metadata. No `Scene` or editor API touches. Safe.

Rollback: each of the 3 changes is independently revertable. If the watchdog turns out flaky, revert just that change; the hotload event and health endpoint stand on their own.

## Verification

Manual, since hotload behavior is editor-stateful:

1. **Baseline**: confirm `get_bridge_status` returns `connected` before any edit.
2. **Trigger hotload**: edit any `.cs` file in tower_defense. Save.
3. **During cascade**: call `mcp__sbox__editor_console_output` from CC.
   - **Before fix**: hangs ~30s, returns "An error occurred".
   - **After fix**: either succeeds (reconnect happened fast) or returns the watchdog timeout error within ~15s.
4. **Post-cascade**: call `get_bridge_status` again. Expect `connected: true`, `responsive: true`, `secondsSinceLastCommand` reflecting the recent call.
5. **Repeat steps 2-4** five times across different file types (.cs, .razor, kit file via Library/ junction). Bridge should self-recover every time.

Pass criteria: zero editor restarts required across a 30-minute session of normal coding.

## Open questions

- Does `MainThread` expose a queue-depth API? If not, `bridge.health.pendingMainThreadQueueDepth` is omitted. Confirm during implementation.
- Should `bridge.health` be exposed as its own MCP tool (`get_bridge_health`), or only folded into `get_bridge_status`? Default to folded; promote later if useful.
- Should the watchdog timeout be a convar (editable at runtime) rather than a const? Probably yes for the upstream PR. Use `[ConVar(...)] int sbox_mcp_main_thread_timeout_ms { get; set; } = 15_000;`.

## Addendum — SkipHotload remediation (2026-05-25, post-soak)

End-to-end soak after the initial five commits revealed that the `Unable to find matching substitution for static method` errors *did* originate from the bridge after all — specifically, from the upgrader walking `McpBridgeServer._clients[]._ws._innerStream._context.Request._memoryBlob._result._asyncCallback` and failing to migrate the BCL-internal `TaskFactory<HttpListenerContext>.<>c__DisplayClass35_0.<FromAsyncImpl>b__0` closure across the assembly substitution. The `[Event("hotloaded")]` handler fires *after* the upgrader runs, so it can't prevent the on-screen error spam.

Fix (`McpBridgeServer.cs`):

- Annotate `_clients` with `[SkipHotload]` so the upgrader stops descending into the per-client WebSocket task graph.
- Drop `readonly` from the field (Start() must reinitialize it post-migration).
- `Start()` performs `_clients ??= new List<ClientConnection>();` — SkipHotload nulls the field on the migrated instance, so re-init is required.
- `Stop()` null-guards `_clients?.ToArray()` / `_clients?.Clear()` for the same reason.

`_listener` and `_cts` are *not* SkipHotload'd — they must migrate so `Stop()` can release the port before `Start()` rebinds. A briefly-tested wider variant that SkipHotload'd all three orphaned the listener on port 29015 across hotload (the field went null but the underlying `HttpListener` lived on, blocking rebind until editor restart).

**Trade-off:** active `ClientConnection` instances are unreachable after hotload (field is null); their sockets die on the next OS-level read, and the MCP server reconnects via its 3-second retry loop. Equivalent in practice to the existing post-hotload disconnect/reconnect behavior.

Verified across two consecutive hotloads (asm v0.0.103 → v0.0.113 → v0.0.121): no `[hotload/GameMenu]` chain errors, no port-bind conflict, single LISTENING entry on 29015.
