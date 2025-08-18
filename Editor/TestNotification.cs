using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Notifications;

namespace McpUnity.Test
{
    public static class TestNotification
    {
        [MenuItem("Tools/MCP Unity/Test/Send Test Notification")]
        public static void SendTestNotification()
        {
            Debug.Log("[MCP Test] === Testing MCP Notification System ===");
            
            var notification = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/test",
                ["params"] = new JObject
                {
                    ["message"] = "Test MCP notification from Unity",
                    ["timestamp"] = System.DateTime.UtcNow.ToString("O"),
                    ["source"] = "TestNotification"
                }
            };
            
            // Create a NotificationEvent to send via NotificationSender
            var notificationEvent = new NotificationEvent
            {
                Type = "test",
                Data = new JObject
                {
                    ["message"] = "Test MCP notification from Unity",
                    ["timestamp"] = System.DateTime.UtcNow.ToString("O"),
                    ["source"] = "TestNotification"
                }
            };
            
            // Send via NotificationSender batch method
            NotificationSender.SendBatch(new List<NotificationEvent> { notificationEvent });
            Debug.Log("[MCP Test] ✓ Sent via NotificationSender.SendBatch");
            
            // Also try direct server push
            var server = McpUnityServer.Instance;
            Debug.Log($"[MCP Test] Server != null: {server != null}");
            Debug.Log($"[MCP Test] Server.IsListening: {server?.IsListening}");
            
            if (server != null && server.IsListening)
            {
                server.PushNotification(notification);
                Debug.Log($"[MCP Test] ✓ Sent via server.PushNotification");
                Debug.Log($"[MCP Test] Connected clients: {server.Clients.Count}");
                foreach (var client in server.Clients)
                {
                    Debug.Log($"[MCP Test]   Client {client.Key}: {client.Value}");
                }
            }
            else
            {
                Debug.LogError("[MCP Test] ✗ Server not running!");
            }
            
            Debug.Log("[MCP Test] === Test Complete ===");
        }
        
        [MenuItem("Tools/MCP Unity/Test/Trigger Console Error")]
        public static void TriggerConsoleError()
        {
            Debug.LogError("[MCP Test] This is a test error to trigger an error notification");
        }
        
        [MenuItem("Tools/MCP Unity/Test/Check Connection Status")]
        public static void CheckConnectionStatus()
        {
            Debug.Log("[MCP Test] === Connection Status ===");
            
            var server = McpUnityServer.Instance;
            if (server != null)
            {
                Debug.Log($"[MCP Test] Server.IsListening: {server.IsListening}");
                Debug.Log($"[MCP Test] Connected Clients: {server.Clients.Count}");
                foreach (var client in server.Clients)
                {
                    Debug.Log($"[MCP Test]   - {client.Key}: {client.Value}");
                }
            }
            else
            {
                Debug.Log("[MCP Test] Server instance is null");
            }
            
            Debug.Log("[MCP Test] === Status Check Complete ===");
        }
    }
}