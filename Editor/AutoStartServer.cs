using UnityEditor;
using UnityEngine;
using McpUnity.Utils;
using McpUnity.Unity;

namespace McpUnity
{
    /// <summary>
    /// Automatically starts the MCP Unity WebSocket server when Unity Editor loads
    /// </summary>
    [InitializeOnLoad]
    public static class AutoStartServer
    {
        static AutoStartServer()
        {
            // Use EditorApplication.delayCall to ensure Unity is fully initialized
            EditorApplication.delayCall += StartServerIfNeeded;
        }

        private static void StartServerIfNeeded()
        {
            if (McpUnitySettings.Instance.AutoStartServer)
            {
                var server = McpUnityServer.Instance;
                if (!server.IsListening)
                {
                    McpLogger.LogInfo("[MCP Unity] Auto-starting WebSocket server on port " + McpUnitySettings.Instance.Port);
                    server.StartServer();
                    
                    if (server.IsListening)
                    {
                        McpLogger.LogInfo("[MCP Unity] ✅ WebSocket server started successfully and listening on port " + McpUnitySettings.Instance.Port);
                    }
                    else
                    {
                        McpLogger.LogError("[MCP Unity] ❌ Failed to start WebSocket server");
                    }
                }
                else
                {
                    McpLogger.LogInfo("[MCP Unity] WebSocket server already running on port " + McpUnitySettings.Instance.Port);
                }
            }
        }
        
        /// <summary>
        /// Force start the server (can be called from menu or other scripts)
        /// </summary>
        [MenuItem("Tools/MCP Unity/Force Start Server", priority = 50)]
        public static void ForceStartServer()
        {
            var server = McpUnityServer.Instance;
            if (server.IsListening)
            {
                McpLogger.LogInfo("[MCP Unity] Stopping existing server...");
                server.StopServer();
            }
            
            McpLogger.LogInfo("[MCP Unity] Starting server...");
            server.StartServer();
            
            if (server.IsListening)
            {
                EditorUtility.DisplayDialog("MCP Unity", 
                    $"✅ Server started on port {McpUnitySettings.Instance.Port}", "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("MCP Unity", 
                    "❌ Failed to start server. Check console for errors.", "OK");
            }
        }
    }
}