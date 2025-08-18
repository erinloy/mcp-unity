using System;
using UnityEngine;

namespace McpUnity.Utils
{
    /// <summary>
    /// Development mode configuration for faster iteration and debugging
    /// Enable with environment variable: MCP_DEV_MODE=true
    /// </summary>
    public static class DevelopmentMode
    {
        private static bool? _isEnabled;
        
        /// <summary>
        /// Check if development mode is enabled
        /// </summary>
        public static bool IsEnabled
        {
            get
            {
                if (!_isEnabled.HasValue)
                {
                    // Only check environment variable to avoid Unity API calls from background threads
                    _isEnabled = Environment.GetEnvironmentVariable("MCP_DEV_MODE") == "true";
                }
                return _isEnabled.Value;
            }
        }
        
        /// <summary>
        /// Development mode optimized settings
        /// </summary>
        public static class Settings
        {
            // Connection Settings - Aggressive for development
            public static int ReconnectDelayMs => IsEnabled ? 100 : 5000;
            public static int MonitorIntervalMs => IsEnabled ? 250 : 2000;
            public static int HeartbeatIntervalMs => IsEnabled ? 1000 : 5000;
            public static int ConnectionTimeoutMs => IsEnabled ? 2000 : 10000;
            
            // Notification Settings - Immediate delivery in dev
            public static bool ImmediateNotifications => IsEnabled;
            public static int NotificationBatchSize => IsEnabled ? 1 : 10;
            public static int NotificationFlushDelayMs => IsEnabled ? 0 : 100;
            
            // Logging Settings - Verbose in development
            public static bool VerboseLogging => IsEnabled;
            public static bool LogWebSocketTraffic => IsEnabled;
            public static bool LogPerformanceMetrics => IsEnabled;
            
            // State Preservation - Enabled in development
            public static bool PreserveStateOnReload => IsEnabled;
            public static bool PersistNotificationQueue => IsEnabled;
            public static int StateExpirationSeconds => IsEnabled ? 30 : 10;
            
            // Error Handling - More forgiving in development
            public static int MaxRetryAttempts => IsEnabled ? 10 : 3;
            public static bool ContinueOnError => IsEnabled;
            public static bool ShowDetailedErrors => IsEnabled;
        }
        
        /// <summary>
        /// Log a development mode message
        /// </summary>
        public static void Log(string message)
        {
            if (Settings.VerboseLogging)
            {
                McpLogger.LogInfo($"[DEV] {message}");
            }
        }
        
        /// <summary>
        /// Log performance metrics
        /// </summary>
        public static void LogPerformance(string operation, long milliseconds)
        {
            if (Settings.LogPerformanceMetrics)
            {
                var color = milliseconds < 50 ? "green" : 
                           milliseconds < 200 ? "yellow" : "red";
                McpLogger.LogInfo($"[PERF] {operation}: <color={color}>{milliseconds}ms</color>");
            }
        }
        
        /// <summary>
        /// Initialize development mode
        /// </summary>
        public static void Initialize()
        {
            if (IsEnabled)
            {
                McpLogger.LogInfo("🚀 MCP Development Mode ENABLED - Fast reconnect, verbose logging, immediate notifications");
                McpLogger.LogInfo($"  Reconnect: {Settings.ReconnectDelayMs}ms | Monitor: {Settings.MonitorIntervalMs}ms | Heartbeat: {Settings.HeartbeatIntervalMs}ms");
            }
        }
    }
}