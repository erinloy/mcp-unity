# Test Unity MCP C# Server Connection
Write-Host "Unity MCP C# Server Connection Test" -ForegroundColor Cyan
Write-Host ""

# Test 1: Check if Unity is running
Write-Host "[1] Checking Unity Editor..." -ForegroundColor Yellow
$unityProcess = Get-Process Unity -ErrorAction SilentlyContinue | Where-Object {$_.MainWindowTitle -like "*Ziltch.Unity*"}
if ($unityProcess) {
    Write-Host "  Unity Editor is running (PID: $($unityProcess.Id))" -ForegroundColor Green
} else {
    Write-Host "  Unity Editor not found" -ForegroundColor Red
    exit 1
}

# Test 2: Test direct executable
Write-Host ""
Write-Host "[2] Testing unity-mcp.exe directly..." -ForegroundColor Yellow
$testProcess = Start-Process -FilePath ".\build\unity-mcp.exe" -NoNewWindow -PassThru -RedirectStandardError "test-error.log" -RedirectStandardOutput "test-output.log"
Start-Sleep -Seconds 2

if ($testProcess.HasExited) {
    Write-Host "  Process exited with code: $($testProcess.ExitCode)" -ForegroundColor Red
    Write-Host "  Error log:" -ForegroundColor Yellow
    if (Test-Path "test-error.log") {
        Get-Content "test-error.log" | Write-Host
    }
} else {
    Write-Host "  Process is running (PID: $($testProcess.Id))" -ForegroundColor Green
    Write-Host "  Process is ready to receive MCP commands" -ForegroundColor Green
    Stop-Process -Id $testProcess.Id -Force
}

# Test 3: Check WebSocket connectivity
Write-Host ""
Write-Host "[3] Testing WebSocket port 8090..." -ForegroundColor Yellow
$connection = Test-NetConnection -ComputerName localhost -Port 8090 -WarningAction SilentlyContinue
if ($connection.TcpTestSucceeded) {
    Write-Host "  Port 8090 is open (Unity WebSocket server)" -ForegroundColor Green
} else {
    Write-Host "  Port 8090 is not accessible" -ForegroundColor Red
    Write-Host "  Please start Unity WebSocket server:" -ForegroundColor Yellow
    Write-Host "  Tools - MCP Unity - Server Window - Start Server" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "Test Complete" -ForegroundColor Cyan