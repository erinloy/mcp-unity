# WebSocket Connection Lifecycle

## Overview

This document maps out the complete lifecycle of the mcp-unity WebSocket server, including normal operation, assembly reload, and recovery scenarios.

## Architecture Chain

```
Nexus (MCP Router)
    ↓
MCP Protocol Layer
    ↓
unity-mcp.exe (C# MCP Bridge)
    ↓
WebSocket Client (connects to ws://localhost:8090/McpUnity)
    ↓
WebSocketSharp Server (Unity)
    ↓
mcp-unity Package (Unity Editor)
    ↓
Unity Editor API
```

## Normal Startup/Shutdown Flow

### Startup Sequence
```
1. Unity Editor Loads
   ↓
2. [InitializeOnLoad] triggers McpUnityServer static constructor
   ↓
3. Singleton Instance created
   ↓
4. Event handlers registered:
   - EditorApplication.quitting
   - AssemblyReloadEvents.beforeAssemblyReload
   - AssemblyReloadEvents.afterAssemblyReload
   - EditorApplication.playModeStateChanged
   ↓
5. InstallServer() - verify/build unity-mcp.exe
   ↓
6. InitializeServices() - create TestRunnerService, ConsoleLogsService
   ↓
7. RegisterResources() - register MCP resources
   ↓
8. RegisterTools() - register MCP tools
   ↓
9. StartServer() (if AutoStartServer enabled)
   ↓
10. WebSocketServer created on 127.0.0.1:8090
   ↓
11. KeepClean = true (auto-clean dead connections)
   ↓
12. WaitTime = 60s (connection timeout)
   ↓
13. AddWebSocketService("/McpUnity", handler)
   ↓
14. Server.Start()
   ↓
15. ServiceDiscovery.RegisterService() - write to ~/.mcp-unity/mcp-unity-discovery.json
   ↓
16. ✅ Server listening on ws://127.0.0.1:8090/McpUnity
```

### Connection Flow
```
1. unity-mcp.exe connects to ws://localhost:8090/McpUnity
   ↓
2. WebSocket handshake
   ↓
3. McpUnitySocketHandler.OnOpen() triggered
   ↓
4. Extract X-Client-Name header (e.g., "unity-mcp-bridge")
   ↓
5. Check for stale connections with same client name
   ↓
6. Clean up any existing connection with same name (handles reconnection after sleep/resume)
   ↓
7. Add client to Clients dictionary: { SessionID → ClientName }
   ↓
8. Log: "WebSocket client '{clientName}' connected (ID: {ID})"
   ↓
9. ✅ Connection established - ready for MCP protocol messages
```

### Message Processing Flow
```
1. Client sends JSON-RPC 2.0 message
   ↓
2. McpUnitySocketHandler.OnMessage() triggered
   ↓
3. Parse JSON request
   ↓
4. Extract: method, params, id
   ↓
5. Route based on method:
   - tools/list → Return registered tools
   - tools/call → Execute tool via EditorCoroutineUtility
   - resources/list → Return registered resources
   - resources/read → Fetch resource via EditorCoroutineUtility
   - rpc.discover → Return OpenRPC discovery document
   ↓
6. Tool/Resource execution on Unity main thread
   ↓
7. CreateResponse(requestId, result) → JSON-RPC 2.0 format
   ↓
8. Send(responseJson)
   ↓
9. ✅ Response sent to client
```

### Shutdown Sequence
```
1. EditorApplication.quitting event
   ↓
2. OnEditorQuitting() called
   ↓
3. Instance.Dispose()
   ↓
4. StopServer()
   ↓
5. Log active connection count
   ↓
6. _webSocketServer.Stop() - closes all active connections
   ↓
7. Thread.Sleep(100) - wait for socket to fully close
   ↓
8. _webSocketServer = null
   ↓
9. Clients.Clear()
   ↓
10. PortManager.ReleasePort()
   ↓
11. ServiceDiscovery.UnregisterService()
   ↓
12. Event handlers unregistered
   ↓
13. ✅ Clean shutdown complete
```

## Assembly Reload Lifecycle

### THE RACE CONDITION PROBLEM (Before Fix)

When Unity recompiles scripts, it triggers assembly reload:

```
1. Script changes detected
   ↓
2. Unity triggers domain reload
   ↓
3. AssemblyReloadEvents.beforeAssemblyReload fires
   ↓
4. OnBeforeAssemblyReload() calls StopServer()
   ↓
5. StopServer() calls _webSocketServer.Stop()  [ASYNC - doesn't block!]
   ↓
6. StopServer() immediately sets _webSocketServer = null  [RACE CONDITION!]
   ↓
7. StopServer() returns
   ↓
8. Domain reload happens (new AppDomain)
   ↓
9. AssemblyReloadEvents.afterAssemblyReload fires [IMMEDIATELY - old socket still closing!]
   ↓
10. OnAfterAssemblyReload() calls StartServer()
   ↓
11. NEW WebSocketServer created on port 8090 [While old socket still active!]
   ↓
12. NEW Server.Start() called
   ↓
13. ⚠️ ZOMBIE STATE CREATED:
    - Port 8090 shows LISTENING (old socket)
    - New WebSocketServer thinks it's started
    - But service endpoint not properly initialized
    - Old socket still bound, new socket confused
   ↓
14. Client connection attempts timeout:
    "Connection to ws://localhost:8090/McpUnity timed out after 10 seconds"
   ↓
15. ❌ Server in zombie state - requires Unity restart to recover
```

### THE FIX: Synchronous Shutdown

```
1. Script changes detected
   ↓
2. Unity triggers domain reload
   ↓
3. AssemblyReloadEvents.beforeAssemblyReload fires
   ↓
4. OnBeforeAssemblyReload() calls StopServer()
   ↓
5. StopServer() logs active connection count
   ↓
6. StopServer() calls _webSocketServer.Stop()  [ASYNC but we wait...]
   ↓
7. Thread.Sleep(100)  [KEY FIX: Wait for socket to fully close]
   ↓
8. Only NOW set _webSocketServer = null
   ↓
9. StopServer() returns [Socket is FULLY closed]
   ↓
10. Domain reload happens (new AppDomain)
   ↓
11. AssemblyReloadEvents.afterAssemblyReload fires
   ↓
12. OnAfterAssemblyReload() calls StartServer()
   ↓
13. NEW WebSocketServer created on port 8090 [Old socket is GONE]
   ↓
14. NEW Server.Start() succeeds
   ↓
15. ✅ Clean state - no zombie server
   ↓
16. Client can connect successfully
```

## Sleep/Resume Recovery

### THE PROBLEM

When PC sleeps/resumes, TCP connections get stale:

```
1. PC enters sleep mode
   ↓
2. TCP connection state frozen
   ↓
3. PC resumes from sleep
   ↓
4. unity-mcp.exe tries to use existing WebSocket connection
   ↓
5. Connection appears open but is actually dead
   ↓
6. MCP initialization messages sent but never received
   ↓
7. Connection timeout after 10 seconds
   ↓
8. unity-mcp.exe retries connection
   ↓
9. WebSocket handshake succeeds
   ↓
10. But Unity still has OLD session ID in Clients dictionary
   ↓
11. ⚠️ Duplicate client entries - messages sent to wrong session
   ↓
12. ❌ Connection never properly initializes - requires Unity restart
```

### THE FIX: Stale Connection Cleanup + Keepalive

**Part 1: Keepalive (in StartServer)**
```
_webSocketServer.KeepClean = true;  // Auto-clean dead connections
_webSocketServer.WaitTime = TimeSpan.FromSeconds(60);  // Connection timeout
```

**Part 2: Stale Connection Cleanup (in OnOpen)**
```
1. New connection attempt from unity-mcp.exe
   ↓
2. WebSocket handshake succeeds
   ↓
3. OnOpen() triggered with new Session ID
   ↓
4. Extract X-Client-Name header: "unity-mcp-bridge"
   ↓
5. Check Clients dictionary for existing entries with same name:
   var existingIds = Clients.Where(kvp => kvp.Value == clientName && kvp.Key != ID)
   ↓
6. Find stale connection(s) with same client name but different session ID
   ↓
7. Log: "Cleaning up {count} stale connection(s) for client '{clientName}'"
   ↓
8. Remove stale session IDs from Clients dictionary
   ↓
9. Add NEW session ID to Clients dictionary
   ↓
10. ✅ Clean state - only current connection tracked
   ↓
11. MCP initialization can proceed normally
```

## Play Mode State Changes

```
ExitingEditMode (entering Play Mode):
   - StopServer() if listening
   - Prevents conflicts during domain reload

EnteredPlayMode:
   - Do nothing (server disabled during play mode)

ExitingPlayMode:
   - Do nothing (domain reload will happen)

EnteredEditMode (back to Edit Mode):
   - StartServer() if AutoStartServer enabled
   - Resume normal operation
```

## Key Takeaways

### Before Fixes
- ❌ Assembly reload race condition created zombie server state
- ❌ Stale connections after sleep/resume never recovered
- ❌ Required Unity restart to fix

### After Fixes
1. **Assembly Reload**: Thread.Sleep(100) ensures socket fully closes before restart
2. **Stale Connections**: OnOpen() cleanup removes duplicate client entries
3. **Keepalive**: Auto-detects and cleans dead connections
4. **Result**: ✅ Graceful recovery without Unity restart

## Implementation Files

- **McpUnityServer.cs**: Server lifecycle, assembly reload handling, keepalive
- **McpUnitySocketHandler.cs**: Connection handling, stale cleanup, message routing
- **ServiceDiscovery.cs**: Discovery file management
- **PortManager.cs**: Port allocation

## Testing Scenarios

1. **Normal Operation**: Connect → tools/list → tools/call → Disconnect
2. **Assembly Reload**: Make code change → Unity recompiles → Connection recovers
3. **Sleep/Resume**: Sleep PC → Resume → Connection recovers automatically
4. **Multiple Reconnects**: Disconnect/reconnect rapidly → No duplicate sessions
5. **Editor Quit**: Clean shutdown → Port released → Discovery unregistered
