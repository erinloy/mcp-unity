using UnityEngine;
using UnityEditor;
using System;
using System.Threading.Tasks;
using McpUnity.Unity;
using McpUnity.Notifications;
using McpUnity.Utils;

namespace McpUnity
{
    /// <summary>
    /// Safe initialization of MCP Unity that avoids domain reload hangs
    /// </summary>
    [InitializeOnLoad]
    public static class McpUnityInitializer
    {
        private static bool _isInitializing = false;
        private static bool _hasInitialized = false;
        
        static McpUnityInitializer()
        {
            // Use multiple delayed calls to ensure Unity is fully ready
            // This avoids conflicts with HotReload domain reloads
            EditorApplication.delayCall += () =>
            {
                EditorApplication.delayCall += () =>
                {
                    EditorApplication.delayCall += SafeInitialize;
                };
            };
            
            // Register for cleanup before domain reload
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
        }
        
        private static async void SafeInitialize()
        {
            if (_isInitializing || _hasInitialized)
                return;
                
            _isInitializing = true;
            
            try
            {
                // Wait a bit more to ensure Unity is fully ready
                await Task.Delay(500);
                
                // Check if we should auto-start
                if (!McpUnitySettings.Instance.AutoStartServer)
                {
                    McpLogger.LogInfo("[MCP] Auto-start disabled in settings");
                    return;
                }
                
                McpLogger.LogInfo("[MCP] Starting MCP Unity System (delayed initialization)...");
                
                // Ensure C# server is built
                if (!McpUtils.EnsureCSharpServerBuilt())
                {
                    McpLogger.LogWarning("[MCP] C# server build failed or executable not found. Server may not start properly.");
                }
                
                // Initialize the server
                var server = McpUnityServer.Instance;
                if (server != null && !server.IsListening)
                {
                    server.StartServer();
                    
                    // Wait for server to be ready
                    await Task.Delay(100);
                }
                
                // Initialize notifications
                UnityNotificationCollector.Initialize();
                
                _hasInitialized = true;
                McpLogger.LogInfo("[MCP] MCP Unity System initialized successfully");
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"[MCP] Failed to initialize: {ex.Message}");
            }
            finally
            {
                _isInitializing = false;
            }
        }
        
        private static void OnBeforeReload()
        {
            if (!_hasInitialized)
                return;
                
            try
            {
                McpLogger.LogInfo("[MCP] Domain reload detected - performing safe cleanup...");
                
                // Clean up notifications first (lightweight)
                UnityNotificationCollector.Cleanup();
                
                // Stop server without blocking
                var server = McpUnityServer.Instance;
                if (server != null)
                {
                    // Don't dispose, just stop - let GC handle cleanup
                    server.StopServer();
                }
                
                _hasInitialized = false;
                McpLogger.LogInfo("[MCP] Cleanup complete");
            }
            catch (Exception ex)
            {
                // Log but don't throw - we don't want to break domain reload
                Debug.LogError($"[MCP] Error during cleanup: {ex.Message}");
            }
        }
    }
}