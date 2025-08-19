# Test WebSocket connection to Unity
$uri = "ws://localhost:8090/McpUnity"

try {
    # Create a simple WebSocket client
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.Options.SetRequestHeader("X-Client-Name", "Test Client")
    
    $cts = New-Object System.Threading.CancellationTokenSource
    $cts.CancelAfter(5000) # 5 second timeout
    
    Write-Host "Attempting to connect to $uri..."
    $connectTask = $ws.ConnectAsync([System.Uri]$uri, $cts.Token)
    
    # Wait for connection
    while (-not $connectTask.IsCompleted -and -not $cts.Token.IsCancellationRequested) {
        Start-Sleep -Milliseconds 100
    }
    
    if ($connectTask.IsFaulted) {
        Write-Host "Connection failed with error:" -ForegroundColor Red
        Write-Host $connectTask.Exception.InnerException.Message -ForegroundColor Red
    }
    elseif ($connectTask.IsCanceled) {
        Write-Host "Connection timed out after 5 seconds" -ForegroundColor Yellow
    }
    elseif ($ws.State -eq 'Open') {
        Write-Host "Successfully connected!" -ForegroundColor Green
        Write-Host "WebSocket state: $($ws.State)"
        
        # Try to send a simple message
        $message = '{"method":"tools/list","id":"1","jsonrpc":"2.0"}'
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($message)
        $buffer = New-Object System.ArraySegment[byte] -ArgumentList @(,$bytes)
        
        Write-Host "Sending test message..."
        $sendTask = $ws.SendAsync($buffer, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None)
        $sendTask.Wait(2000)
        
        if ($sendTask.IsCompleted) {
            Write-Host "Message sent successfully" -ForegroundColor Green
        }
        
        # Close connection
        $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "Test complete", [System.Threading.CancellationToken]::None).Wait(2000)
    }
    else {
        Write-Host "Unexpected WebSocket state: $($ws.State)" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "Error: $_" -ForegroundColor Red
}
finally {
    if ($ws) {
        $ws.Dispose()
    }
    if ($cts) {
        $cts.Dispose()
    }
}