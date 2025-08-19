# C# MCP Unity Implementation

This is a C# implementation of MCP Unity, based on the original work at https://github.com/CoderGamester/mcp-unity.

## Architecture

```
MCP Client (Claude/Cursor/etc.) → unity-mcp.exe → Unity Editor
```

- **Communication**: stdio protocol  
- **Process Model**: Standalone launcher that starts Unity in batch mode
- **Language**: Pure C#
- **Configuration**: Auto-detects Unity project root

## File Structure

```
Editor/DirectMcp/
├── unity-mcp.exe           # Compiled MCP server executable
├── UnityMcpServer.cs       # Main server implementation
├── UnityMcpLauncher.cs     # Server launcher and process management
├── ARCHITECTURE.md         # Technical architecture details
└── IMPLEMENTATION.md       # This file
```

## How to Use

Add to your MCP client configuration:

```json
{
  "mcpServers": {
    "unity-mcp": {
      "command": "path/to/mcp-unity/Editor/DirectMcp/unity-mcp.exe"
    }
  }
}
```

## Technical Details

- **MCP Protocol**: Full MCP compliance via stdio transport
- **Tool Registration**: Attribute-based discovery `[McpServerTool]`
- **Async Support**: Full async/await throughout
- **Unity Integration**: Launches Unity in batch mode with project auto-detection
- **Error Handling**: Comprehensive try-catch with Unity console logging

## Development

To modify or extend this implementation:

1. Edit the C# source files in `Editor/DirectMcp/`
2. Unity will automatically recompile the changes
3. Use standard C# debugging tools with Unity

## Credits

This implementation is based on the original MCP Unity project by CoderGamester, reimplemented in C# for developers who prefer working in a single-language environment.