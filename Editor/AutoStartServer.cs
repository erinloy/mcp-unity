using UnityEditor;
using UnityEngine;
using McpUnity.Utils;
using McpUnity.Unity;

namespace McpUnity
{
    /// <summary>
    /// Server start helpers. Automatic start on Editor load, domain reload and play mode changes is
    /// owned by <see cref="McpUnityServer"/> itself (scheduled, non-blocking, retried while the port
    /// is still held); this class only exposes the manual "Force Start" command.
    /// </summary>
    public static class AutoStartServer
    {
        /// <summary>
        /// Force start the server (can be called from menu or other scripts)
        /// </summary>
        [MenuItem("Tools/MCP Unity/Force Start Server", priority = 50)]
        public static void ForceStartServer()
        {
            var server = McpUnityServer.Instance;
            if (server == null)
            {
                McpLogger.LogError("[MCP Unity] The MCP Unity server is disabled in this process (batch mode without AllowBatchModeServer).");
                return;
            }

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
            else if (server.HasScheduledStart)
            {
                EditorUtility.DisplayDialog("MCP Unity",
                    $"Port {McpUnitySettings.Instance.Port} is still in use; the start will be retried automatically.\n\n{server.ScheduledStartStatus}", "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("MCP Unity",
                    "❌ Failed to start server. Check console for errors.", "OK");
            }
        }
    }
}
