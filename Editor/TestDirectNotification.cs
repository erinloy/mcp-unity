using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using System;

namespace McpUnity.Test
{
    public static class TestDirectNotification
    {
        [MenuItem("MCP Unity/Test/Send Direct MCP Notification")]
        public static void SendDirectNotification()
        {
            Debug.Log("[MCP Test] Attempting to send direct MCP notification...");
            
            // Create a proper MCP notification (no 'id' field)
            var notification = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/unity/test",
                ["params"] = new JObject
                {
                    ["message"] = "Direct test notification from Unity",
                    ["timestamp"] = DateTime.UtcNow.ToString("O"),
                    ["type"] = "test"
                }
            };
            
            // Send directly through the WebSocket server
            try
            {
                var server = Unity.McpUnityServer.Instance;
                if (server != null && server.IsListening)
                {
                    // Get the WebSocket handler to send to connected clients
                    var json = notification.ToString(Newtonsoft.Json.Formatting.None);
                    
                    // Log what we're sending
                    Debug.Log($"[MCP Test] Sending notification: {json}");
                    
                    // Use the server's broadcast capability
                    server.PushNotification(notification);
                    
                    Debug.Log("[MCP Test] Notification sent successfully!");
                }
                else
                {
                    Debug.LogError("[MCP Test] Server not running or not listening!");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP Test] Failed to send notification: {ex.Message}");
            }
        }
    }
}