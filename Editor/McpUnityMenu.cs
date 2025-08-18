using UnityEngine;
using UnityEditor;
using McpUnity.Unity;
using McpUnity.Notifications;
using McpUnity.Utils;

namespace McpUnity
{
    /// <summary>
    /// Manual controls for MCP Unity to avoid domain reload issues
    /// </summary>
    public static class McpUnityMenu
    {
        [MenuItem("MCP Unity/Initialize System")]
        public static void InitializeSystem()
        {
            Debug.Log("[MCP] Initializing MCP Unity System manually...");
            
            // Initialize the server
            var server = McpUnityServer.Instance;
            if (server != null && !server.IsListening)
            {
                server.StartServer();
            }
            
            // Initialize notifications if needed
            UnityNotificationCollector.Initialize();
            
            Debug.Log("[MCP] MCP Unity System initialized");
        }
        
        [MenuItem("MCP Unity/Shutdown System")]
        public static void ShutdownSystem()
        {
            Debug.Log("[MCP] Shutting down MCP Unity System...");
            
            // Clean up notifications
            UnityNotificationCollector.Cleanup();
            
            // Stop the server
            var server = McpUnityServer.Instance;
            if (server != null)
            {
                server.StopServer();
                server.Dispose();
            }
            
            Debug.Log("[MCP] MCP Unity System shut down");
        }
        
        [MenuItem("MCP Unity/Server Status")]
        public static void CheckServerStatus()
        {
            var server = McpUnityServer.Instance;
            if (server != null && server.IsListening)
            {
                Debug.Log($"[MCP] Server is RUNNING on port {McpUnitySettings.Instance.Port}");
                Debug.Log($"[MCP] Connected clients: {server.Clients.Count}");
            }
            else
            {
                Debug.Log("[MCP] Server is STOPPED");
            }
        }
    }
}