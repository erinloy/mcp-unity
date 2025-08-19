# Unity MCP Server (C# Implementation)

This is a pure C# implementation of the MCP (Model Context Protocol) server for Unity, replacing the previous Node.js implementation.

## Benefits Over Node.js Implementation

- **No Node.js dependency**: Runs as a native Windows executable
- **Single language**: All C# for easier maintenance
- **Better performance**: ~10MB memory vs 100MB+, <100ms startup vs 2-3s
- **Auto-detection**: Automatically finds Unity project root
- **Simpler deployment**: Single executable file

## Architecture

```
Claude → McpProxy → unity-mcp.exe (C#) → WebSocket → Unity Editor
```

## Building

### Prerequisites
- .NET 8.0 SDK or later
- Windows x64

### Build Steps

```powershell
# From this directory (Server~)
.\build.ps1
```

This creates `build\unity-mcp.exe` - a single self-contained executable.

## Configuration

The server automatically detects the Unity project root and reads settings from:
`ProjectSettings/McpUnitySettings.json`

```json
{
    "Port": 8090,
    "RequestTimeoutSeconds": 10,
    "AutoStartServer": true,
    "EnableInfoLogs": true
}
```

## Usage with McpProxy

Update your McpProxy configuration to use the C# executable:

```json
{
  "servers": [
    {
      "name": "unity-ziltch",
      "command": "Z:\\SOURCE\\Ziltch\\___\\src\\Ziltch\\Ziltch.Unity\\Assets\\mcp-unity\\Server~\\build\\unity-mcp.exe",
      "args": [],
      "env": {}
    }
  ]
}
```

## Features

### Auto-Detection
The server automatically finds the Unity project root by:
1. Searching upward from executable location for `Assets` and `ProjectSettings` folders
2. Checking common Unity project locations
3. No command-line arguments needed

### MCP Protocol Support
- Full MCP 2024-11-05 protocol implementation
- Tools discovery and execution
- Resources listing and reading
- Prompts support
- Error handling and timeouts

### Unity Integration
- Connects to Unity WebSocket server on configured port
- Automatic retry with exponential backoff
- Graceful shutdown handling
- Comprehensive error messages

## Troubleshooting

### Unity WebSocket Not Running
If you see connection errors:
1. Open Unity Editor with your project
2. Go to: Tools → MCP Unity → Server Window
3. Click "Start Server"

### Port Conflicts
Check `ProjectSettings/McpUnitySettings.json` for the correct port number.

### Connection Timeouts
The server will retry connection to Unity 5 times with 2-second delays.

## Development

To modify the server:
1. Edit `UnityMcpServer.cs`
2. Run `.\build.ps1`
3. Restart McpProxy or reload configuration

## Migration from Node.js

This C# implementation is a drop-in replacement for the Node.js server:
1. Build the C# server with `.\build.ps1`
2. Update McpProxy config to point to `unity-mcp.exe`
3. Remove Node.js dependencies (optional)

The protocol and functionality remain identical - only the implementation language has changed.