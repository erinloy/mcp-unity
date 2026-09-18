# WebSocket Connection Lifecycle

## Overview

This document maps out the lifecycle of the mcp-unity WebSocket server (Unity side) and the
C# MCP bridge (`Server~`, deployed as `<ProjectRoot>/Tools/unity-mcp/unity-mcp.exe`): startup,
authentication, message dispatch, reloads, play mode, recovery and shutdown.

## Architecture Chain

```
MCP client (Claude Code, Cursor, ...)
    ↓ stdio (MCP protocol)
unity-mcp.exe (C# MCP bridge, Server~)
    ↓ ws://127.0.0.1:<Port>/McpUnity  (HTTP Basic auth: mcp-unity:<token>, no Origin header)
WebSocketSharp server (Unity Editor, McpUnityServer)
    ↓ main-thread dispatch (McpMainThreadDispatcher)
Tools / Resources (Unity Editor API)
```

## Configuration Contract

| Item | Location | Notes |
|------|----------|-------|
| Port and flags | `ProjectSettings/McpUnitySettings.json` | Written by Unity. The bridge reads `Port`; `MCP_UNITY_SETTINGS_PATH` overrides the path. |
| Auth token | `Library/McpUnity/bridge-token` | 64 hex chars, created by Unity on first server start, never committed. Regenerate from the Server Window. |
| Bridge token override | `MCP_UNITY_AUTH_TOKEN`, then `MCP_UNITY_AUTH_TOKEN_PATH` | An explicitly configured token source that is missing or malformed fails; it never falls back. |
| Remote Editor | `UNITY_HOST` (bridge) + `AllowRemoteConnections` (Unity) | Unity binds `0.0.0.0` instead of `127.0.0.1`. Transport is plaintext `ws://`. |
| Headless host | `AllowBatchModeServer` or `MCP_UNITY_ALLOW_BATCH_MODE=true` | Batch mode is off by default so CI/cloud builds are unaffected. The bridge is never built in batch mode. |

## Startup Sequence (Unity)

```
1. Domain load: [InitializeOnLoad] / [DidReloadScripts] touch McpUnityServer.Instance
   (Instance is null in batch mode unless explicitly enabled)
2. Constructor registers quitting / assembly reload / play mode / update (auto-heal) hooks
3. InstallServer(): build unity-mcp.exe with `dotnet publish` if missing (skipped in batch mode)
4. InitializeServices(), RegisterResources(), RegisterTools()
5. AutoStartServer → ScheduleStartServer("auto-start"): start runs from the editor loop after 0.25s
6. StartServerInternal():
   - skipped for Multiplayer Play Mode clones
   - GetOrCreateToken() → Basic auth configured on the WebSocketServer
   - bind 127.0.0.1:<Port> (0.0.0.0 with AllowRemoteConnections); no ReuseAddress, so a port
     held by another process is reported instead of silently shared
   - AddWebSocketService("/McpUnity"), Start()
   - McpBackgroundTick.Start() keeps the editor loop ticking while unfocused (Windows)
   - ServiceDiscovery.RegisterService() → ~/.mcp-unity/mcp-unity-discovery.json
   - liveness probe thread verifies the bound port actually answers (issue #141)
7. Port still in use → retried on a bounded backoff (0.25s, 0.5s, 1s, 2s, 3s, 5s…; 10 attempts)
   driven by EditorApplication.update, never blocking the main thread
```

## Connection Flow

```
1. unity-mcp.exe resolves the token (on every attempt, so a regenerated token is picked up)
2. WebSocket handshake with `Authorization: Basic base64(mcp-unity:<token>)`
   - wrong/missing credentials → HTTP 401; the bridge logs the auth failure and keeps retrying
   - any Origin header → rejected (browsers cannot drive the Editor)
3. McpUnitySocketHandler.OnOpen():
   - connections from a stopped server generation are closed
   - inactive (dead) sessions are closed and dropped from Clients (covers sleep/resume and
     prevents file-descriptor exhaustion, issue #110); other live clients are kept
   - Clients[sessionId] = X-Client-Name
4. The bridge sends tools/list_changed and resources/list_changed to the MCP client
```

## Message Processing Flow

```
1. OnMessage() (WebSocketSharp thread) enqueues the raw message on McpMainThreadDispatcher
2. EditorApplication.update drains the queue on the main thread (works while unfocused)
3. Route by method:
   - tools/list, tools/call, resources/list, resources/read (with URI templates such as
     unity://logs/{logType}) - used by the C# bridge
   - rpc.discover, system.listMethods, system.methodSignature - discovery
   - <tool name> / <resource name> - legacy per-method protocol
4. Tool/resource runs via EditorCoroutineUtility; JSON-RPC 2.0 response sent back
5. The bridge converts the result for the MCP client: `content` blocks pass through; any other
   structured result is returned as JSON text; `success: false` or `isError` marks an error
```

## Assembly Reload

```
beforeAssemblyReload → StopServerInternal(): cancel scheduled start, invalidate the connection
                        generation, cancel the liveness probe, close sessions, Stop()
afterAssemblyReload  → ScheduleStartServer("assembly reload") if AutoStartServer
Bridge               → ConnectionLost → single reconnect loop (fast retries for ~5s, then backoff)
```

## Play Mode

```
ExitingEditMode  → sessions closed with code 4001 (UnityCloseCode.PlayMode) on the main thread,
                   listener stopped on a background thread (never blocks the transition)
EnteredPlayMode  → ScheduleStartServer if not listening (covers "Enter Play Mode Options"
                   without domain reload, where no reload would otherwise restart it)
EnteredEditMode  → ScheduleStartServer if not listening
```

## Recovery

- **Scheduled retries**: port conflicts after reloads resolve through the backoff schedule; the
  Server Window shows "Retrying port N (attempt x/10)".
- **Auto-heal** (every 5s, after a 3s grace period, only with AutoStartServer): if the server
  should be listening but is not, a start is scheduled; if a TCP connect to the port fails twice
  in a row, the server is force-restarted. A server stopped by the user is left stopped.
- **Force Restart** (Tools > MCP Unity > Force Restart Server): tears the server down
  unconditionally and starts again (immediately, or on the retry schedule if the port is held).

## Shutdown

```
EditorApplication.quitting → sessions closed, listener stopped on a background thread,
                             PortManager.ReleasePort(), ServiceDiscovery.UnregisterService()
```

## Implementation Files

- **Editor/UnityBridge/McpUnityServer.cs**: lifecycle, scheduling, auth configuration, liveness probe, auto-heal
- **Editor/UnityBridge/McpUnitySocketHandler.cs**: connection tracking, main-thread dispatch, routing
- **Editor/UnityBridge/McpUnityAuthentication.cs**: token creation/rotation
- **Editor/Utils/McpBackgroundTick.cs**: unfocused editor ticking (Windows)
- **Editor/Utils/ServiceDiscovery.cs**, **Editor/Utils/PortManager.cs**: discovery file, port allocation
- **Server~/Services/UnityBridgeService.cs**: bridge connection/retry loop, settings resolution
- **Server~/Services/UnityBridgeAuthentication.cs**: bridge token resolution
- **Server~/Services/WebSocketConnectionManager.cs**: authenticated WebSocket client
