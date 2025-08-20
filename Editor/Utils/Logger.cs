using UnityEngine;
using McpUnity.Unity;

namespace McpUnity.Utils
{
    /// <summary>
    /// Special logger to use inside the MCP Unity Editor project
    /// </summary>
    public static class McpLogger
    {
        private const string LogPrefix = "[MCP Unity] ";
        
        /// <summary>
        /// Log an info message if info logs are enabled
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void LogInfo(string message)
        {
            if (McpUnitySettings.Instance.EnableInfoLogs)
            {
                // Defer to main thread to avoid stack traces in Unity console
                if (System.Threading.Thread.CurrentThread.ManagedThreadId == 1)
                {
                    Debug.Log($"{LogPrefix}{message}");
                }
                else
                {
                    UnityEditor.EditorApplication.delayCall += () => Debug.Log($"{LogPrefix}{message}");
                }
            }
        }
        
        /// <summary>
        /// Log a warning message
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void LogWarning(string message)
        {
            // Defer to main thread to avoid stack traces in Unity console
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == 1)
            {
                Debug.LogWarning($"{LogPrefix}{message}");
            }
            else
            {
                UnityEditor.EditorApplication.delayCall += () => Debug.LogWarning($"{LogPrefix}{message}");
            }
        }
        
        /// <summary>
        /// Log an error message
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void LogError(string message)
        {
            // Defer to main thread to avoid stack traces in Unity console
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == 1)
            {
                Debug.LogError($"{LogPrefix}{message}");
            }
            else
            {
                UnityEditor.EditorApplication.delayCall += () => Debug.LogError($"{LogPrefix}{message}");
            }
        }
    }
}
