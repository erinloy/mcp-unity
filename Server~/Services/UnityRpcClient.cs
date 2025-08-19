using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace McpUnity.DirectMcp.Services
{
    /// <summary>
    /// Handles RPC communication with Unity Editor via WebSocket
    /// </summary>
    public interface IUnityRpcClient
    {
        Task<JObject?> SendRequestAsync(string method, JObject? parameters = null, CancellationToken cancellationToken = default);
        Task SendNotificationAsync(string method, JObject? parameters = null);
    }

    public class UnityRpcClient : IUnityRpcClient
    {
        private readonly IWebSocketConnectionManager _connectionManager;
        private readonly ILogger<UnityRpcClient> _logger;
        private readonly Dictionary<string, TaskCompletionSource<JObject>> _pendingRequests = new();
        private int _requestIdCounter = 1;
        private readonly SemaphoreSlim _requestLock = new(1, 1);

        public UnityRpcClient(
            IWebSocketConnectionManager connectionManager,
            ILogger<UnityRpcClient> logger)
        {
            _connectionManager = connectionManager;
            _logger = logger;
            
            // Subscribe to messages
            _connectionManager.MessageReceived += OnMessageReceived;
        }

        public async Task<JObject?> SendRequestAsync(string method, JObject? parameters = null, CancellationToken cancellationToken = default)
        {
            if (!_connectionManager.IsConnected)
            {
                _logger.LogWarning("Cannot send request - not connected to Unity");
                return null;
            }

            await _requestLock.WaitAsync(cancellationToken);
            string requestId;
            TaskCompletionSource<JObject> tcs;
            
            try
            {
                requestId = (_requestIdCounter++).ToString();
                tcs = new TaskCompletionSource<JObject>();
                _pendingRequests[requestId] = tcs;
            }
            finally
            {
                _requestLock.Release();
            }

            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = method
            };

            if (parameters != null)
            {
                request["params"] = parameters;
            }

            try
            {
                await _connectionManager.SendAsync(request, cancellationToken);
                
                // Wait for response with timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                
                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Request {Method} timed out or was cancelled", method);
                return null;
            }
            finally
            {
                _pendingRequests.Remove(requestId);
            }
        }

        public async Task SendNotificationAsync(string method, JObject? parameters = null)
        {
            if (!_connectionManager.IsConnected)
            {
                _logger.LogWarning("Cannot send notification - not connected to Unity");
                return;
            }

            var notification = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method
            };

            if (parameters != null)
            {
                notification["params"] = parameters;
            }

            await _connectionManager.SendAsync(notification);
        }

        private void OnMessageReceived(object? sender, JObject message)
        {
            try
            {
                // Check if it's a response to one of our requests
                var id = message["id"]?.ToString();
                if (!string.IsNullOrEmpty(id) && _pendingRequests.TryGetValue(id, out var tcs))
                {
                    if (message["error"] != null)
                    {
                        _logger.LogError("Unity returned error for request {Id}: {Error}", id, message["error"]);
                        tcs.TrySetResult(message); // Still return the message so caller can handle the error
                    }
                    else
                    {
                        tcs.TrySetResult(message);
                    }
                    _pendingRequests.Remove(id);
                }
                else if (message["method"] != null)
                {
                    // It's a notification or request from Unity (not expected in current design)
                    _logger.LogDebug("Received unsolicited message from Unity: {Method}", message["method"]);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message from Unity");
            }
        }
    }
}