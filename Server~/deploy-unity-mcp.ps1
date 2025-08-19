param(
    [switch]$SkipBuild,
    [switch]$Watch,
    [switch]$TestHotReload
)

$ErrorActionPreference = "Stop"

# Configuration
$projectPath = "Z:\SOURCE\Ziltch\___\src\Ziltch\Ziltch.Unity\Assets\mcp-unity\Server~"
$projectFile = "$projectPath\UnityMcp.csproj"
$sourcePath = "$projectPath\bin\Release\net8.0\win-x64"
$targetPath = "$projectPath\bin\Release\net8.0\win-x64"  # Deploy in place for Unity to find

# Color-coded output
function Write-Status($message) { Write-Host $message -ForegroundColor Cyan }
function Write-Action($message) { Write-Host $message -ForegroundColor Yellow }
function Write-Success($message) { Write-Host $message -ForegroundColor Green }
function Write-Info($message) { Write-Host $message -ForegroundColor Gray }

Write-Status "Unity MCP Deployment Script"
Write-Host ""

# Function to deploy
function Deploy-UnityMcp {
    # Kill existing processes
    Write-Action "Terminating unity-mcp processes..."
    $processes = Get-Process -Name "unity-mcp" -ErrorAction SilentlyContinue
    if ($processes) {
        $processes | Stop-Process -Force
        Write-Info "  Killed $($processes.Count) process(es)"
        Start-Sleep -Milliseconds 500
    }
    
    # Build if not skipped
    if (-not $SkipBuild) {
        Write-Action "Building unity-mcp.exe..."
        $buildArgs = @(
            "build", $projectFile,
            "--configuration", "Release",
            "-r", "win-x64",
            "--self-contained"
        )
        
        $result = & dotnet $buildArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Build failed:`n$result"
            return $false
        }
        Write-Success "  Build successful"
    }
    
    # Signal Unity to reconnect (touch a marker file)
    $markerFile = "$projectPath\.unity-mcp-updated"
    "" | Out-File -FilePath $markerFile -Force
    Write-Info "  Signaled Unity to reconnect"
    
    # Clean up marker after a delay
    Start-Job -ScriptBlock {
        Start-Sleep -Seconds 2
        Remove-Item $using:markerFile -Force -ErrorAction SilentlyContinue
    } | Out-Null
    
    return $true
}

# Test hot-reload functionality
if ($TestHotReload) {
    Write-Status "Testing Hot-Reload Functionality"
    Write-Host ""
    
    Write-Action "Monitoring Unity MCP connection..."
    Write-Info "  1. Make sure Unity Editor is running"
    Write-Info "  2. Make a change to a tool in Unity"
    Write-Info "  3. The tool should be available in Claude within 2 seconds"
    Write-Host ""
    
    # Monitor the process
    while ($true) {
        $proc = Get-Process -Name "unity-mcp" -ErrorAction SilentlyContinue
        if ($proc) {
            Write-Info "Unity MCP is running (PID: $($proc.Id), Memory: $([math]::Round($proc.WorkingSet64/1MB))MB)"
        } else {
            Write-Action "Unity MCP is not running - waiting for Unity to start it..."
        }
        Start-Sleep -Seconds 2
        
        if ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            if ($key.Key -eq 'Q') { 
                Write-Info "Exiting monitor mode"
                break 
            }
        }
    }
    exit 0
}

# Watch mode for continuous deployment
if ($Watch) {
    Write-Status "Starting Watch Mode"
    Write-Info "  Watching: $projectPath\*.cs"
    Write-Info "  Press Ctrl+C to stop"
    Write-Host ""
    
    # Initial deploy
    if (Deploy-UnityMcp) {
        Write-Success "Initial deployment complete"
    }
    
    # Set up file watcher
    $watcher = New-Object System.IO.FileSystemWatcher
    $watcher.Path = $projectPath
    $watcher.Filter = "*.cs"
    $watcher.IncludeSubdirectories = $false
    $watcher.EnableRaisingEvents = $true
    
    $action = {
        $path = $Event.SourceEventArgs.FullPath
        $changeType = $Event.SourceEventArgs.ChangeType
        Write-Host ""
        Write-Action "Detected change: $($Event.SourceEventArgs.Name) ($changeType)"
        
        # Debounce - wait a bit for multiple changes
        Start-Sleep -Milliseconds 500
        
        # Deploy
        if (Deploy-UnityMcp) {
            Write-Success "Hot deployment complete - Unity will reconnect automatically"
        }
    }
    
    # Register events
    Register-ObjectEvent -InputObject $watcher -EventName "Changed" -Action $action | Out-Null
    Register-ObjectEvent -InputObject $watcher -EventName "Created" -Action $action | Out-Null
    
    try {
        Write-Info "Watching for changes..."
        while ($true) { Start-Sleep -Seconds 1 }
    }
    finally {
        $watcher.EnableRaisingEvents = $false
        $watcher.Dispose()
        Get-EventSubscriber | Unregister-Event
    }
}
else {
    # Single deployment
    if (Deploy-UnityMcp) {
        Write-Host ""
        Write-Success "Deployment complete!"
        Write-Info "Unity MCP will restart when Unity reconnects."
        Write-Host ""
        Write-Info "Quick commands:"
        Write-Info "  .\deploy-unity-mcp.ps1 -Watch        # Watch mode for auto-deploy"
        Write-Info "  .\deploy-unity-mcp.ps1 -SkipBuild    # Deploy without building"
        Write-Info "  .\deploy-unity-mcp.ps1 -TestHotReload # Test hot-reload functionality"
    }
}