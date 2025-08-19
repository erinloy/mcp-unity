param(
    [switch]$Watch,
    [switch]$NoKill
)

$ErrorActionPreference = "Stop"

# Configuration
$projectPath = "Z:\SOURCE\Ziltch\___\src\Ziltch\Ziltch.Unity\Assets\mcp-unity\Server~"
$projectFile = "$projectPath\UnityMcp.csproj"
$mcpProxyPath = "Z:\SOURCE\Ziltch\___\tools\mcpproxy"

# Color-coded output
function Write-Status($message) { Write-Host $message -ForegroundColor Cyan }
function Write-Action($message) { Write-Host $message -ForegroundColor Yellow }
function Write-Success($message) { Write-Host $message -ForegroundColor Green }
function Write-Info($message) { Write-Host $message -ForegroundColor Gray }

Write-Status "Unity MCP Hot-Reload Tool"
Write-Host ""

function Trigger-HotReload {
    Write-Action "Triggering hot-reload..."
    
    # Option 1: Signal McpProxy to reconnect without killing unity-mcp
    if (-not $NoKill) {
        # Kill unity-mcp processes to force restart
        $processes = Get-Process -Name "unity-mcp" -ErrorAction SilentlyContinue
        if ($processes) {
            Write-Info "  Stopping $($processes.Count) unity-mcp process(es)"
            $processes | Stop-Process -Force
        }
    }
    
    # Create reconnect signal for McpProxy
    $reconnectSignal = "$mcpProxyPath\.reconnect-now"
    "" | Out-File -FilePath $reconnectSignal -Force
    Write-Success "  Hot-reload triggered - McpProxy will reconnect"
    
    # Clean up signal after a moment
    Start-Job -ScriptBlock {
        Start-Sleep -Seconds 2
        Remove-Item $using:reconnectSignal -Force -ErrorAction SilentlyContinue
    } | Out-Null
}

function Build-UnityMcp {
    Write-Action "Building unity-mcp.exe..."
    
    $buildArgs = @(
        "build", $projectFile,
        "--configuration", "Release",
        "-r", "win-x64",
        "--self-contained",
        "--nologo",
        "-v", "q"
    )
    
    $result = & dotnet $buildArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed:`n$result"
        return $false
    }
    
    Write-Success "  Build successful"
    return $true
}

if ($Watch) {
    Write-Status "Starting Watch Mode for Hot-Reload"
    Write-Info "  Watching: $projectPath\*.cs"
    Write-Info "  Press Ctrl+C to stop"
    Write-Host ""
    
    # Initial build
    if (Build-UnityMcp) {
        Trigger-HotReload
    }
    
    # Set up file watcher
    $watcher = New-Object System.IO.FileSystemWatcher
    $watcher.Path = $projectPath
    $watcher.Filter = "*.cs"
    $watcher.IncludeSubdirectories = $false
    $watcher.EnableRaisingEvents = $true
    
    $lastChange = [DateTime]::MinValue
    $debounceMs = 1000
    
    $action = {
        $now = [DateTime]::Now
        $timeSince = ($now - $script:lastChange).TotalMilliseconds
        
        # Debounce multiple changes
        if ($timeSince -lt $debounceMs) {
            return
        }
        
        $script:lastChange = $now
        $path = $Event.SourceEventArgs.FullPath
        $changeType = $Event.SourceEventArgs.ChangeType
        
        Write-Host ""
        Write-Action "Detected change: $($Event.SourceEventArgs.Name) ($changeType)"
        
        # Build and reload
        if (Build-UnityMcp) {
            Trigger-HotReload
        }
    }
    
    # Register events
    Register-ObjectEvent -InputObject $watcher -EventName "Changed" -Action $action | Out-Null
    Register-ObjectEvent -InputObject $watcher -EventName "Created" -Action $action | Out-Null
    
    try {
        Write-Info "Watching for changes..."
        while ($true) { 
            Start-Sleep -Seconds 1
            
            # Check if McpProxy is running
            $mcpProxy = Get-Process -Name "mcpproxy" -ErrorAction SilentlyContinue
            if (-not $mcpProxy) {
                Write-Warning "McpProxy is not running"
            }
        }
    }
    finally {
        $watcher.EnableRaisingEvents = $false
        $watcher.Dispose()
        Get-EventSubscriber | Unregister-Event
    }
}
else {
    # Single hot-reload
    if (Build-UnityMcp) {
        Trigger-HotReload
        Write-Host ""
        Write-Success "Hot-reload complete!"
        Write-Info "Unity MCP will reconnect through McpProxy"
        Write-Host ""
        Write-Info "Usage:"
        Write-Info "  .\hot-reload.ps1           # Single hot-reload"
        Write-Info "  .\hot-reload.ps1 -Watch    # Watch mode"
        Write-Info "  .\hot-reload.ps1 -NoKill   # Don't kill unity-mcp (signal only)"
    }
}