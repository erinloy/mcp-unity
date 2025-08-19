# Build script for Unity MCP Server (C# implementation)

Write-Host "Building Unity MCP Server..." -ForegroundColor Cyan

# Clean previous build
if (Test-Path ".\bin") {
    Remove-Item -Path ".\bin" -Recurse -Force
}
if (Test-Path ".\obj") {
    Remove-Item -Path ".\obj" -Recurse -Force
}

# Build the project
Write-Host "Compiling C# MCP server..." -ForegroundColor Yellow
dotnet build -c Release

if ($LASTEXITCODE -eq 0) {
    Write-Host "Build successful!" -ForegroundColor Green
    Write-Host "Unity MCP Server built successfully!" -ForegroundColor Green
    Write-Host "Output: .\bin\Release\net8.0\win-x64\unity-mcp.exe" -ForegroundColor Cyan
    
    # Show file info if it exists
    $exePath = ".\bin\Release\net8.0\win-x64\unity-mcp.exe"
    if (Test-Path $exePath) {
        $exe = Get-Item $exePath
        Write-Host "File size: $([math]::Round($exe.Length / 1MB, 2)) MB" -ForegroundColor Gray
    }
} else {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}