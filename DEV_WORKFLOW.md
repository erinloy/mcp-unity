# Unity MCP Development Workflow

## Optimized Claude → McpProxy → UnityMcp → Unity → Tooling/Resources Loop

### Quick Start

1. **Deploy Unity MCP** (one-time setup)
   ```powershell
   cd Z:\SOURCE\Ziltch\___\src\Ziltch\Ziltch.Unity\Assets\mcp-unity\Server~
   .\deploy-unity-mcp.ps1
   ```

2. **Start Watch Mode** (for development)
   ```powershell
   .\deploy-unity-mcp.ps1 -Watch
   ```

3. **Unity Editor** will automatically connect and reconnect as needed

### Architecture Overview

```
Claude (AI Assistant)
    ↓ [MCP Protocol]
McpProxy (tools\mcpproxy)
    ↓ [Stdio/Pipes]
unity-mcp.exe (C# MCP Server)
    ↓ [WebSocket:8090 + JSON-RPC]
Unity Editor (McpUnityServer)
    ↓ [Direct Calls]
Tools & Resources (C# Classes)
```

### Hot-Reload Without Claude Restarts ✨

**The development loop works WITHOUT Claude restarts!**

Use the optimized `hot-reload.ps1` script:
```powershell
# Single hot-reload (rebuilds and signals McpProxy)
.\hot-reload.ps1

# Watch mode for automatic hot-reload on file changes
.\hot-reload.ps1 -Watch

# Signal-only reload (no process kill)
.\hot-reload.ps1 -NoKill
```

**How it works:**
1. Builds unity-mcp.exe with latest changes
2. Creates `.reconnect-now` signal for McpProxy
3. McpProxy reconnects without dropping Claude connection
4. Tools remain available throughout the process
5. No `/mcp` command or Claude restart needed!

### Development Workflows

#### 1. Adding a New Unity Tool

**Unity Side** (`Assets/mcp-unity/Editor/Tools/`)
1. Create new tool class inheriting from `McpToolBase`
2. Register in `McpUnityServer.RegisterTools()`
3. Unity will compile automatically

**MCP Side** (`Server~/Program.cs`)
1. Add corresponding method in `UnityTools` class with `[McpServerTool]`
2. Save file → Watch mode auto-rebuilds → Unity reconnects

**Testing in Claude**
```
mcp__unity-ziltch__<tool_name>
```

#### 2. Hot-Reload Development Loop

**Automatic Hot-Reload** (2-second cache)
- Unity tool/resource changes are detected within 2 seconds
- No restart required for unity-mcp.exe
- No restart required for McpProxy

**Manual Reload** (if needed)
```powershell
# Quick rebuild and deploy
.\deploy-unity-mcp.ps1 -SkipBuild

# Full rebuild
.\deploy-unity-mcp.ps1
```

#### 3. Testing & Debugging

**Monitor Unity MCP Status**
```powershell
.\deploy-unity-mcp.ps1 -TestHotReload
```

**Check McpProxy Status**
```bash
mcp__unity-ziltch__control status
```

**View Unity Console Logs**
- Through MCP: `mcp__unity-ziltch__get_console_logs`
- In Unity: Window → General → Console

### File Locations

| Component | Location |
|-----------|----------|
| Unity MCP Source | `Assets/mcp-unity/Server~/Program.cs` |
| Unity MCP Binary | `Tools/unity-mcp/unity-mcp.exe` (at Unity project root) |
| Unity Tools | `Assets/mcp-unity/Editor/Tools/` |
| Unity Resources | `Assets/mcp-unity/Editor/Resources/` |
| Deploy Script | `Assets/mcp-unity/Server~/deploy-unity-mcp.ps1` |
| McpProxy | `tools/mcpproxy/mcpproxy.exe` |

### Troubleshooting

#### Unity MCP Not Connecting
1. Check Unity Editor is running
2. Check WebSocket port 8090 is free
3. Rebuild: `.\deploy-unity-mcp.ps1`

#### Changes Not Reflected
1. Wait 2 seconds (cache expiration)
2. Check Unity compilation succeeded
3. Check watch mode is running

#### Process Locked Files
```powershell
# Kill all Unity MCP processes
Get-Process -Name "unity-mcp" | Stop-Process -Force

# Rebuild
.\deploy-unity-mcp.ps1
```

### Performance Optimizations

1. **2-Second Cache** - Prevents excessive Unity queries
2. **WebSocket Persistence** - Maintains connection through Unity recompiles
3. **Auto-Reconnect** - Exponential backoff prevents connection storms
4. **In-Place Deployment** - No file copying, direct binary execution
5. **Watch Mode** - Automatic rebuild on source changes

### Best Practices

1. **Keep Watch Mode Running** during development
2. **Use Hot-Reload** for rapid iteration
3. **Test in Claude** immediately after changes
4. **Monitor Logs** for connection issues
5. **Commit Working State** before major changes

### Common Commands

```powershell
# Deploy and build
.\deploy-unity-mcp.ps1

# Deploy without build (faster)
.\deploy-unity-mcp.ps1 -SkipBuild

# Watch mode (auto-rebuild on changes)
.\deploy-unity-mcp.ps1 -Watch

# Test hot-reload
.\deploy-unity-mcp.ps1 -TestHotReload

# Deploy McpProxy
Z:\SOURCE\Ziltch\___\src\Ziltch\Ziltch.McpProxy\deploy-mcpproxy.ps1
```

### Integration with Claude

After deployment, Unity tools are available in Claude as:
- `mcp__unity-ziltch__execute_menu_item`
- `mcp__unity-ziltch__select_gameobject`
- `mcp__unity-ziltch__update_gameobject`
- `mcp__unity-ziltch__add_package`
- `mcp__unity-ziltch__run_tests`
- `mcp__unity-ziltch__send_console_log`
- `mcp__unity-ziltch__update_component`
- `mcp__unity-ziltch__add_asset_to_scene`
- `mcp__unity-ziltch__create_prefab`
- `mcp__unity-ziltch__capture_screenshot`

Resources are available as:
- Unity project information
- Scene hierarchy
- Console logs
- Asset lists

### Version Information

- **Unity MCP**: C# implementation using MCP SDK
- **Protocol**: MCP over stdio + WebSocket JSON-RPC to Unity
- **Unity Support**: 2022.3+
- **.NET Version**: 8.0
- **Platform**: Windows x64