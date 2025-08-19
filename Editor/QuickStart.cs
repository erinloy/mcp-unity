using UnityEngine;
using UnityEditor;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity
{
    /// <summary>
    /// Quick start helper for MCP Unity Server
    /// Provides immediate server startup without complex initialization
    /// </summary>
    public static class QuickStart
    {
        [MenuItem("Tools/MCP Unity/Quick Start Server", priority = 1)]
        public static void StartServerNow()
        {
            McpLogger.LogInfo("=== MCP Unity Quick Start ===");
            
            var server = McpUnityServer.Instance;
            if (server == null)
            {
                McpLogger.LogError("Failed to get McpUnityServer instance");
                EditorUtility.DisplayDialog("MCP Unity Error", 
                    "Failed to initialize MCP Unity Server instance.", 
                    "OK");
                return;
            }
            
            if (server.IsListening)
            {
                McpLogger.LogInfo($"Server is already running on port {McpUnitySettings.Instance.Port}");
                EditorUtility.DisplayDialog("MCP Unity", 
                    $"Server is already running on port {McpUnitySettings.Instance.Port}", 
                    "OK");
                return;
            }
            
            McpLogger.LogInfo($"Starting server on port {McpUnitySettings.Instance.Port}...");
            server.StartServer();
            
            if (server.IsListening)
            {
                McpLogger.LogInfo($"✅ Server started successfully on port {McpUnitySettings.Instance.Port}");
                EditorUtility.DisplayDialog("MCP Unity", 
                    $"Server started successfully!\n\nListening on port {McpUnitySettings.Instance.Port}\nPath: /McpUnity", 
                    "OK");
            }
            else
            {
                McpLogger.LogError("Server failed to start - check Unity Console for errors");
                EditorUtility.DisplayDialog("MCP Unity Error", 
                    "Server failed to start.\n\nCheck the Unity Console for error messages.", 
                    "OK");
            }
        }
        
        [MenuItem("Tools/MCP Unity/Stop Server", priority = 2)]
        public static void StopServerNow()
        {
            var server = McpUnityServer.Instance;
            if (server == null)
            {
                McpLogger.LogError("Failed to get McpUnityServer instance");
                return;
            }
            
            if (!server.IsListening)
            {
                McpLogger.LogInfo("Server is not running");
                EditorUtility.DisplayDialog("MCP Unity", 
                    "Server is not running", 
                    "OK");
                return;
            }
            
            McpLogger.LogInfo("Stopping server...");
            server.StopServer();
            McpLogger.LogInfo("✅ Server stopped");
            EditorUtility.DisplayDialog("MCP Unity", 
                "Server stopped successfully", 
                "OK");
        }
        
        [MenuItem("Tools/MCP Unity/Server Status", priority = 3)]
        public static void ShowServerStatus()
        {
            var server = McpUnityServer.Instance;
            if (server == null)
            {
                EditorUtility.DisplayDialog("MCP Unity Status", 
                    "Server instance not initialized", 
                    "OK");
                return;
            }
            
            var status = server.IsListening ? "RUNNING" : "STOPPED";
            var port = McpUnitySettings.Instance.Port;
            var clients = server.Clients.Count;
            
            var message = $"Status: {status}\n" +
                         $"Port: {port}\n" +
                         $"Connected Clients: {clients}\n" +
                         $"WebSocket Path: /McpUnity\n" +
                         $"Auto-Start: {(McpUnitySettings.Instance.AutoStartServer ? "Enabled" : "Disabled")}";
            
            EditorUtility.DisplayDialog("MCP Unity Server Status", message, "OK");
            
            McpLogger.LogInfo($"[Status] Server: {status}, Port: {port}, Clients: {clients}");
        }
        
        [MenuItem("Tools/MCP Unity/--- Diagnostics ---", priority = 100)]
        public static void Separator() { }
        
        [MenuItem("Tools/MCP Unity/Test WebSocket Connection", priority = 101)]
        public static void TestConnection()
        {
            var server = McpUnityServer.Instance;
            if (server == null || !server.IsListening)
            {
                EditorUtility.DisplayDialog("MCP Unity", 
                    "Server must be running to test connection.\n\nUse 'Quick Start Server' first.", 
                    "OK");
                return;
            }
            
            McpLogger.LogInfo("=== Testing WebSocket Connection ===");
            McpLogger.LogInfo($"Server listening on port {McpUnitySettings.Instance.Port}");
            McpLogger.LogInfo($"WebSocket endpoint: ws://localhost:{McpUnitySettings.Instance.Port}/McpUnity");
            McpLogger.LogInfo("Clients can now connect to this endpoint");
            
            EditorUtility.DisplayDialog("MCP Unity Connection Info", 
                $"Server is ready for connections!\n\n" +
                $"Endpoint: ws://localhost:{McpUnitySettings.Instance.Port}/McpUnity\n" +
                $"Status: LISTENING\n\n" +
                $"Run unity-mcp.exe to connect as MCP client.", 
                "OK");
        }
        
        [MenuItem("Tools/MCP Unity/Check Port Availability", priority = 102)]
        public static void CheckPort()
        {
            var port = McpUnitySettings.Instance.Port;
            
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                
                EditorUtility.DisplayDialog("MCP Unity", 
                    $"Port {port} is available for use", 
                    "OK");
                McpLogger.LogInfo($"Port {port} is available");
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                EditorUtility.DisplayDialog("MCP Unity", 
                    $"Port {port} is already in use!\n\n{ex.Message}\n\nTry a different port in settings.", 
                    "OK");
                McpLogger.LogError($"Port {port} is in use: {ex.Message}");
            }
        }
    }
}