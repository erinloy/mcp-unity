using UnityEngine;
using UnityEditor;
using McpUnity.Unity;
using McpUnity.Notifications;
using McpUnity.Utils;

namespace McpUnity
{
    /// <summary>
    /// Development and testing utilities for MCP Unity
    /// Note: Main server controls are in Tools/MCP Unity/Server Window
    /// </summary>
    public static class McpUnityMenu
    {
        // Removed redundant menu items - use Tools/MCP Unity/Server Window instead:
        // - Initialize/Shutdown System (use Start/Stop Server buttons in UI)
        // - Server Status (displayed in UI) 
        // - Build C# Server (use Force Install Server button in UI)
        
        [MenuItem("Tools/MCP Unity/Refresh Attributed Tools", priority = 200)]
        public static void RefreshAttributedTools()
        {
            var server = McpUnityServer.Instance;
            if (server == null)
            {
                McpLogger.LogWarning("[RefreshAttributedTools] MCP Unity server is not running");
                EditorUtility.DisplayDialog("MCP Unity", 
                    "MCP Unity server is not running. Please start the server first using Tools > MCP Unity > Server Window.", 
                    "OK");
                return;
            }
            
            try
            {
                server.RefreshAttributedTools();
                EditorUtility.DisplayDialog("MCP Unity", 
                    "Attributed tools have been refreshed successfully. Check the Console for details.", 
                    "OK");
            }
            catch (System.Exception ex)
            {
                McpLogger.LogError($"[RefreshAttributedTools] Failed to refresh attributed tools: {ex.Message}");
                EditorUtility.DisplayDialog("MCP Unity Error", 
                    $"Failed to refresh attributed tools:\n{ex.Message}", 
                    "OK");
            }
        }
    }
}