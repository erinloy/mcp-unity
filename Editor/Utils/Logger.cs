using UnityEngine;
using UnityEditorInternal;
using McpUnity.Unity;

namespace McpUnity.Utils
{
    /// <summary>
    /// Special logger to use inside the MCP Unity Editor project.
    /// Messages are written synchronously from any thread. Off-main-thread messages (WebSocketSharp
    /// callbacks, probe threads) are written without a stack trace, since a background-thread
    /// stack trace carries no useful information and only clutters the console.
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
                Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}{1}", LogPrefix, message);
            }
        }

        /// <summary>
        /// Log a warning message
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void LogWarning(string message)
        {
            if (InternalEditorUtility.CurrentThreadIsMainThread())
            {
                Debug.LogWarning($"{LogPrefix}{message}");
            }
            else
            {
                Debug.LogFormat(LogType.Warning, LogOption.NoStacktrace, null, "{0}{1}", LogPrefix, message);
            }
        }

        /// <summary>
        /// Log an error message
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void LogError(string message)
        {
            if (InternalEditorUtility.CurrentThreadIsMainThread())
            {
                Debug.LogError($"{LogPrefix}{message}");
            }
            else
            {
                Debug.LogFormat(LogType.Error, LogOption.NoStacktrace, null, "{0}{1}", LogPrefix, message);
            }
        }
    }
}
