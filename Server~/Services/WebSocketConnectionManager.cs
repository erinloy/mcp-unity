using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace McpUnity.DirectMcp.Services
{
    /// <summary>
    /// Manages WebSocket connection lifecycle to Unity Editor
    /// </summary>
    public interface IWebSocketConnectionManager
    {
        bool IsConnected { get; }
        event EventHandler<JObject>? MessageReceived;
        event EventHandler? ConnectionLost;
        event EventHandler? ConnectionRestored;
        Task ConnectAsync(string uri, CancellationToken cancellationToken = default);
        Task DisconnectAsync();
        Task SendAsync(JObject message, CancellationToken cancellationToken = default);
    }

    public class WebSocketConnectionManager : IWebSocketConnectionManager, IDisposable
    {
        private readonly ILogger<WebSocketConnectionManager> _logger;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _receiveCts;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public bool IsConnected => _webSocket?.State == WebSocketState.Open;
        public event EventHandler<JObject>? MessageReceived;
        public event EventHandler? ConnectionLost;
        public event EventHandler? ConnectionRestored;
        private bool _wasConnected;

        public WebSocketConnectionManager(ILogger<WebSocketConnectionManager> logger)
        {
            _logger = logger;
        }

        public async Task ConnectAsync(string uri, CancellationToken cancellationToken = default)
        {
            await _connectionLock.WaitAsync(cancellationToken);
            try
            {
                if (IsConnected)
                {
                    _logger.LogDebug("Already connected to {Uri}", uri);
                    return;
                }

                await DisconnectInternalAsync();

                _webSocket = new ClientWebSocket();
                _webSocket.Options.SetRequestHeader("X-Client-Name", "Unity MCP Server");
                
                // Set a reasonable timeout
                _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                _logger.LogDebug("Connecting to Unity at {Uri}...", uri);
                
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(10)); // 10 second timeout
                    try
                    {
                        await _webSocket.ConnectAsync(new Uri(uri), cts.Token);
                        _logger.LogInformation("Successfully connected to Unity WebSocket");
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TimeoutException($"Connection to {uri} timed out after 10 seconds");
                    }
                    catch (WebSocketException wsEx)
                    {
                        _logger.LogDebug("WebSocket connection failed: {Message}", wsEx.Message);
                        throw;
                    }
                }

                // Start receive loop
                _receiveCts = new CancellationTokenSource();
                _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));

                // Raise connection restored event if we were previously connected
                if (_wasConnected)
                {
                    _logger.LogInformation("Connection restored to Unity");
                    ConnectionRestored?.Invoke(this, EventArgs.Empty);
                }
                _wasConnected = true;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            await _connectionLock.WaitAsync();
            try
            {
                await DisconnectInternalAsync();
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private async Task DisconnectInternalAsync()
        {
            _receiveCts?.Cancel();
            
            if (_webSocket != null)
            {
                try
                {
                    if (_webSocket.State == WebSocketState.Open)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error closing WebSocket: {Message}", ex.Message);
                }
                finally
                {
                    _webSocket.Dispose();
                    _webSocket = null;
                }
            }
        }

        public async Task SendAsync(JObject message, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("WebSocket is not connected");
            }

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                var json = message.ToString(Newtonsoft.Json.Formatting.None);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
                _logger.LogTrace("Sent message to Unity: {Message}", json);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[65536]; // Increased from 4KB to 64KB for large tool responses
            var messageBuilder = new StringBuilder();

            try
            {
                while (!cancellationToken.IsCancellationRequested && IsConnected)
                {
                    var result = await _webSocket!.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                        if (result.EndOfMessage)
                        {
                            var message = messageBuilder.ToString();
                            messageBuilder.Clear();

                            try
                            {
                                var json = JObject.Parse(message);
                                MessageReceived?.Invoke(this, json);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to parse message from Unity: {Message}", message);
                            }
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Unity closed the connection");
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in receive loop");
            }
            finally
            {
                // Raise connection lost event
                if (_wasConnected)
                {
                    _logger.LogWarning("Connection to Unity lost");
                    ConnectionLost?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public void Dispose()
        {
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _webSocket?.Dispose();
            _connectionLock?.Dispose();
            _sendLock?.Dispose();
        }
    }
}