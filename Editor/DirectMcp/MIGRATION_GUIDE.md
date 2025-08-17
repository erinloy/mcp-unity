# Migration Guide: Node.js Bridge → Direct Unity MCP

## Quick Comparison

### Before (Node.js Bridge)
```
Claude → McpProxy → Node.js → Unity WebSocket
- 3 processes
- 2 languages (JS/C#)
- Protocol translation
- ~100MB memory overhead
- 2+ second startup
```

### After (Direct MCP)
```
Claude → Unity MCP Server
- 1 process
- 1 language (C#)
- Native MCP
- ~10MB memory overhead
- <100ms startup
```

## Migration Steps

### Step 1: Add MCP C# SDK to Unity

```xml
<!-- In your Unity project's Packages/manifest.json -->
{
  "dependencies": {
    "com.modelcontextprotocol.core": "1.0.0"
  }
}
```

Or manually copy DLLs from NuGet:
- ModelContextProtocol.Core.dll
- System.Text.Json.dll (if not present)
- System.Threading.Channels.dll (if not present)

### Step 2: Update McpProxy Configuration

Change from:
```json
{
  "servers": [
    {
      "name": "unity-mcp",
      "command": "node",
      "args": ["path/to/server/index.js"]
    }
  ]
}
```

To:
```json
{
  "servers": [
    {
      "name": "unity-mcp-direct",  
      "command": "Unity.exe",
      "args": [
        "-batchmode",
        "-executeMethod", 
        "McpUnity.DirectMcp.UnityMcpServer.RunStdioServer"
      ]
    }
  ]
}
```

### Step 3: Port Tools

#### Old Format (Node.js + Unity WebSocket)

**Node.js Side:**
```typescript
export function registerCaptureScreenshotTool(server, mcpUnity, logger) {
  server.tool(
    "capture_screenshot",
    "Captures a screenshot",
    paramsSchema.shape,
    async (args) => {
      const result = await mcpUnity.sendRequest({
        method: "capture_screenshot",
        params: args
      });
      return {
        content: [{
          type: "image",
          data: result.data,
          mimeType: "image/png"
        }]
      };
    }
  );
}
```

**Unity Side:**
```csharp
public class CaptureScreenshotTool : McpToolBase
{
    public override JObject Execute(JObject parameters)
    {
        var screenshot = ScreenshotCapture.CaptureScreenshot(
            parameters["viewType"]?.ToString()
        );
        return JObject.FromObject(new { 
            success = true,
            data = screenshot
        });
    }
}
```

#### New Format (Direct MCP)

**Unity Only:**
```csharp
[McpServerTool("capture_screenshot")]
public class CaptureScreenshotTool : McpServerTool
{
    public override string Description => "Captures a screenshot";
    
    public override async Task<CallToolResult> ExecuteAsync(
        CallToolRequestParams request,
        CancellationToken ct)
    {
        var viewType = request.Arguments["viewType"]?.ToString() ?? "game";
        var screenshot = await CaptureScreenshotAsync(viewType);
        
        return new CallToolResult
        {
            Content = new[] {
                new ImageContent {
                    Type = "image",
                    Data = screenshot.Data,
                    MimeType = "image/png"
                }
            }
        };
    }
}
```

### Step 4: Update Discovery

Old discovery through custom OpenRPC is replaced with built-in MCP discovery:

```csharp
// Automatic with MCP C# SDK
server.RegisterTool<CaptureScreenshotTool>();
// Tool is automatically discoverable via MCP protocol
```

### Step 5: Testing

1. **Parallel Testing**: Run both systems side-by-side
2. **Tool Validation**: Verify each tool works correctly
3. **Performance Check**: Measure latency improvements
4. **Memory Check**: Verify reduced memory usage

### Step 6: Cleanup

Once migrated:
1. Remove `Assets/mcp-unity/Server~` directory
2. Remove Node.js dependencies
3. Remove WebSocket handler code
4. Update documentation

## Benefits After Migration

### Development Experience
- **Single Debug Session**: Debug directly in Unity
- **Hot Reload**: Unity's domain reload instead of npm rebuild
- **Type Safety**: Full C# IntelliSense and compile-time checking
- **No Build Step**: No TypeScript compilation needed

### Performance
- **Latency**: ~10ms vs ~50ms per call
- **Memory**: 10MB vs 100MB+ overhead
- **Startup**: <1s vs 2-3s
- **CPU**: Single process vs multiple

### Maintenance
- **Dependencies**: Only Unity and MCP SDK
- **Updates**: Single package update
- **Testing**: Standard Unity test framework
- **CI/CD**: Simpler pipeline without Node.js

## Rollback Plan

If issues arise:
1. McpProxy config supports multiple servers
2. Can run both in parallel
3. Switch back by changing server name in config
4. No code changes required in Claude

## Common Issues & Solutions

### Issue: "MCP SDK not found"
**Solution**: Ensure ModelContextProtocol.Core.dll is in Assets/Plugins

### Issue: "Unity crashes on server start"
**Solution**: Check Unity is running with -batchmode flag

### Issue: "Tools not discovered"
**Solution**: Verify [McpServerTool] attributes are present

### Issue: "Threading exceptions"
**Solution**: Use EditorApplication.delayCall for main thread operations

## Timeline Estimate

- **Day 1**: Set up MCP SDK, create basic server
- **Day 2**: Port 3-5 core tools
- **Day 3**: Port remaining tools
- **Day 4**: Testing and validation
- **Day 5**: Documentation and cleanup

Total: **1 week for complete migration**

## Questions?

The direct MCP approach simplifies the entire stack while improving performance and maintainability. The migration can be done incrementally with minimal risk.