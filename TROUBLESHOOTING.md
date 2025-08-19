# Unity MCP Server Troubleshooting Guide

## Current Issue: WebSocket Connection Failed

The Unity MCP Server is unable to connect to Unity Editor's WebSocket server.

### Symptoms:
- Unity MCP Server shows: "WebSocket connection failed: Unable to connect to the remote server"
- Port 8090 shows as LISTENING in netstat
- Connection attempts result in SYN_SENT state (connection not accepted)

### Diagnostic Steps Completed:

1. **Port Status**: Port 8090 is listening (confirmed via netstat)
2. **Unity Status**: Unity Editor is running (confirmed via tasklist)
3. **Connection Test**: Direct WebSocket connection fails (tested with PowerShell script)

### Likely Causes:

1. **WebSocket Server Not Started in Unity**
   - The McpUnityServer.StartServer() may not have been called
   - Auto-start may be disabled in settings

2. **WebSocketSharp Configuration Issue**
   - The server might be bound to a specific interface (not localhost)
   - WebSocketSharp might have compatibility issues with standard WebSocket clients

### Resolution Steps:

#### In Unity Editor:

1. **Check if MCP Unity Server is Started:**
   - Open Unity Editor
   - Go to **Tools > MCP Unity > Server Window**
   - Check if the server shows as "Running" or "Stopped"
   - If stopped, click "Start Server"

2. **Verify Settings:**
   - In the Server Window, check the port number (should be 8090)
   - Ensure "Auto Start Server" is enabled if you want it to start automatically
   - Check "Allow Remote Connections" setting

3. **Check Unity Console for Errors:**
   - Look for any red error messages related to WebSocket or MCP Unity
   - Common errors:
     - "Failed to start WebSocket server: Port 8090 is already in use"
     - "WebSocket server not initialized"

4. **Manual Start via Menu:**
   - Try **Tools > MCP Unity > Initialize System**
   - This should start the WebSocket server if it's not running

#### If Server Won't Start:

1. **Port Conflict:**
   ```powershell
   # Check what's using port 8090
   netstat -ano | findstr :8090
   ```
   
2. **Kill Conflicting Process:**
   ```powershell
   # Find and kill the process using the port
   # Replace PID with the actual process ID
   Stop-Process -Id PID -Force
   ```

3. **Restart Unity Editor:**
   - Sometimes Unity needs a full restart to properly initialize WebSocketSharp

#### Testing the Connection:

Once the server is started in Unity, test with:

1. **From Unity MCP Server:**
   ```bash
   cd "Z:\SOURCE\Ziltch\___\src\Ziltch\Ziltch.Unity\Assets\mcp-unity\Server~"
   .\bin\Release\net8.0\win-x64\unity-mcp.exe
   ```

2. **Using Test Script:**
   ```powershell
   powershell -ExecutionPolicy Bypass -File TestWebSocket.ps1
   ```

### Architecture Notes:

The system uses a dual-server architecture:
- **Unity Side**: WebSocketSharp server listening on port 8090
- **MCP Side**: Unity MCP Server (C#) connects as a WebSocket client to Unity

The Unity MCP Server acts as a bridge:
```
Claude/MCP Client <--stdio--> Unity MCP Server <--WebSocket--> Unity Editor
```

### Common Issues and Solutions:

| Issue | Solution |
|-------|----------|
| Port already in use | Change port in Unity settings or kill conflicting process |
| Server not auto-starting | Enable "Auto Start Server" in settings |
| WebSocket connection refused | Ensure Unity Editor server is started |
| Connection timeouts | Check firewall settings, ensure localhost connections allowed |
| Assembly reload stops server | Normal behavior, server should restart after reload |

### Debug Logging:

To enable verbose logging in Unity:
1. Open Server Window
2. Enable "Verbose Logging"
3. Check Unity Console for detailed WebSocket messages

### Next Steps if Still Not Working:

1. **Check WebSocketSharp Version:**
   - Ensure compatible version is installed in Unity project
   - May need to update via Package Manager

2. **Firewall/Antivirus:**
   - Add exception for Unity Editor
   - Add exception for unity-mcp.exe

3. **Alternative Connection Method:**
   - Consider using named pipes instead of WebSocket
   - Implement direct stdio communication

### Contact for Help:

If issues persist after following this guide:
1. Collect Unity Console logs
2. Collect unity-mcp.exe output
3. Note Unity version and OS version
4. File issue at the project repository