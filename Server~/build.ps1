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
    
    # Publish as single file
    Write-Host "Publishing as single executable..." -ForegroundColor Yellow
    dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o .\build
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Unity MCP Server built successfully!" -ForegroundColor Green
        Write-Host "Output: .\build\unity-mcp.exe" -ForegroundColor Cyan
        
        # Show file info
        $exe = Get-Item ".\build\unity-mcp.exe"
        Write-Host "File size: $([math]::Round($exe.Length / 1MB, 2)) MB" -ForegroundColor Gray
    } else {
        Write-Host "Publish failed!" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}