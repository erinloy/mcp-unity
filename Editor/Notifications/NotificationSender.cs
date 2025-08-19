using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebSocketSharp;
using McpUnity.Utils;
using McpUnity.Unity;

namespace McpUnity.Notifications
{
    /// <summary>
    /// Sends notifications from Unity to Claude via WebSocket
    /// Handles connection resilience and automatic reconnection
    /// </summary>
    public static class NotificationSender
    {
        private static WebSocket _clientConnection;
        private static readonly object _connectionLock = new object();
        private static bool _isConnected = false;
        private static DateTime _lastConnectionAttempt = DateTime.MinValue;
        private static readonly TimeSpan _reconnectCooldown = TimeSpan.FromSeconds(5);
        private static readonly Queue<JObject> _pendingNotifications = new();
        private static readonly int _maxPendingNotifications = 100;
        
        /// <summary>
        /// Send a batch of notifications to Claude
        /// </summary>
        public static void SendBatch(List<NotificationEvent> events)
        {
            if (events == null || events.Count == 0) return;
            
            // Convert to MCP notification format
            foreach (var evt in events)
            {
                var notification = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = $"notifications/{evt.Type}",
                    ["params"] = evt.Data
                };
                
                SendNotification(notification);
            }
        }
        
        /// <summary>
        /// Send a single notification
        /// </summary>
        private static void SendNotification(JObject notification)
        {
            lock (_connectionLock)
            {
                // Queue notification if not connected
                if (!_isConnected)
                {
                    QueueNotification(notification);
                    TryReconnect();
                    return;
                }
                
                try
                {
                    // Get active WebSocket connections from the Unity MCP server
                    var server = McpUnityServer.Instance;
                    if (server != null && server.IsListening)
                    {
                        // Push notification through WebSocket to MCP clients
                        bool sent = server.PushNotification(notification);
                        
                        if (!sent)
                        {
                            // No connected clients or service not ready - queue for later
                            QueueNotification(notification);
                        }
                        else if (DevelopmentMode.Settings.VerboseLogging)
                        {
                            McpLogger.LogInfo($"[Notifications] Pushed: {notification["method"]}");
                        }
                    }
                    else
                    {
                        // Server not ready, queue for later
                        QueueNotification(notification);
                        _isConnected = false; // Mark as not connected
                    }
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"[Notifications] Send failed: {ex.Message}");
                    _isConnected = false;
                    QueueNotification(notification);
                }
            }
        }
        
        private static void QueueNotification(JObject notification)
        {
            lock (_pendingNotifications)
            {
                if (_pendingNotifications.Count >= _maxPendingNotifications)
                {
                    // Remove oldest notification to make room
                    _pendingNotifications.Dequeue();
                }
                
                _pendingNotifications.Enqueue(notification);
                McpLogger.LogInfo($"[Notifications] Queued notification. Queue size: {_pendingNotifications.Count}");
            }
        }
        
        private static void TryReconnect()
        {
            // Avoid rapid reconnection attempts
            if (DateTime.UtcNow - _lastConnectionAttempt < _reconnectCooldown)
                return;
            
            _lastConnectionAttempt = DateTime.UtcNow;
            
            // Don't reconnect during compilation or shutdown
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            
            // Run reconnection on main thread to avoid Unity API issues
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var server = McpUnityServer.Instance;
                    if (server != null && !server.IsListening)
                    {
                        server.StartServer();
                    }
                    
                    _isConnected = server?.IsListening ?? false;
                    
                    if (_isConnected)
                    {
                        McpLogger.LogInfo("[Notifications] Reconnected to MCP server");
                        FlushPendingNotifications();
                    }
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"[Notifications] Reconnection failed: {ex.Message}");
                    _isConnected = false; // Ensure we're marked as disconnected
                }
            };
        }
        
        private static void FlushPendingNotifications()
        {
            List<JObject> toSend = new List<JObject>();
            
            lock (_pendingNotifications)
            {
                // Take all pending notifications out of the queue first
                while (_pendingNotifications.Count > 0)
                {
                    toSend.Add(_pendingNotifications.Dequeue());
                }
            }
            
            // Send them without re-queuing on failure
            foreach (var notification in toSend)
            {
                SendNotificationDirect(notification);
            }
            
            if (toSend.Count > 0)
            {
                McpLogger.LogInfo($"[Notifications] Flushed {toSend.Count} pending notifications");
            }
        }
        
        /// <summary>
        /// Send notification without re-queuing on failure
        /// </summary>
        private static void SendNotificationDirect(JObject notification)
        {
            try
            {
                var server = McpUnityServer.Instance;
                if (server != null && server.IsListening)
                {
                    server.PushNotification(notification);
                    
                    if (DevelopmentMode.Settings.VerboseLogging)
                    {
                        McpLogger.LogInfo($"[Notifications] Pushed: {notification["method"]}");
                    }
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"[Notifications] Direct send failed: {ex.Message}");
            }
        }
        
        public static void OnConnectionEstablished()
        {
            lock (_connectionLock)
            {
                _isConnected = true;
                McpLogger.LogInfo("[Notifications] Connection established");
            }
            
            FlushPendingNotifications();
        }
        
        public static void OnConnectionLost()
        {
            lock (_connectionLock)
            {
                _isConnected = false;
                McpLogger.LogWarning("[Notifications] Connection lost");
            }
        }
    }
}