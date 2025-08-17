# Script to update Claude desktop config with new C# Unity MCP server

$configPath = Join-Path $env:APPDATA "Claude\claude_desktop_config.json"

Write-Host "Updating Claude config for Unity-Ziltch MCP server..." -ForegroundColor Cyan

# Read current config
$config = Get-Content $configPath -Raw | ConvertFrom-Json

# Add or update unity-ziltch configuration
$unityZiltchConfig = @{
    command = "Z:/SOURCE/Ziltch/___/tools/mcpproxy/mcpproxy.exe"
    args = @(
        "--downstream",
        "Z:/SOURCE/Ziltch/___/src/Ziltch/Ziltch.Unity/Assets/mcp-unity/Server~/build/unity-mcp.exe",
        "--reconnect-delay",
        "30000",
        "--log-level",
        "Information"
    )
}

# Add to config
$config.mcpServers | Add-Member -MemberType NoteProperty -Name "unity-ziltch" -Value $unityZiltchConfig -Force

# Save updated config
$config | ConvertTo-Json -Depth 10 | Set-Content $configPath

Write-Host "Configuration updated successfully!" -ForegroundColor Green
Write-Host "Please restart Claude to apply changes." -ForegroundColor Yellow

# Display the new configuration
Write-Host "`nNew unity-ziltch configuration:" -ForegroundColor Cyan
$unityZiltchConfig | ConvertTo-Json -Depth 5