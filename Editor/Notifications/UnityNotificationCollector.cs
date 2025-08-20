using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEditor.Compilation;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using McpUnity.Utils;

namespace McpUnity.Notifications
{
    /// <summary>
    /// Collects Unity events and queues them for notification to Claude via MCP
    /// Provides real-time visibility into Unity Editor state and activities
    /// </summary>
    // REMOVED [InitializeOnLoad] to prevent domain reload hangs
    public static class UnityNotificationCollector
    {
        private static readonly ConcurrentQueue<NotificationEvent> _eventQueue = new();
        private static readonly object _lock = new object();
        private static bool _isInitialized = false;
        private static DateTime _lastFlush = DateTime.UtcNow;
        private static readonly TimeSpan _flushInterval = TimeSpan.FromMilliseconds(100);
        
        // Track recent errors to avoid spam
        private static readonly Dictionary<string, DateTime> _recentErrors = new();
        private static readonly TimeSpan _errorCooldown = TimeSpan.FromSeconds(5);

        // Removed static constructor to prevent auto-initialization
        // Use McpUnityMenu.InitializeSystem() to manually start

        public static void Initialize()
        {
            if (_isInitialized) return;
            
            lock (_lock)
            {
                if (_isInitialized) return;
                
                // Console events - Critical for real-time error visibility
                Application.logMessageReceived += OnLogMessageReceived;
                Application.logMessageReceivedThreaded += OnLogMessageReceivedThreaded;
                
                // Editor state events - Know when Unity changes modes
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                EditorApplication.pauseStateChanged += OnPauseStateChanged;
                
                // Scene events - Track what's being worked on
                EditorSceneManager.sceneOpened += OnSceneOpened;
                EditorSceneManager.sceneClosed += OnSceneClosed;
                EditorSceneManager.sceneSaved += OnSceneSaved;
                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.sceneUnloaded += OnSceneUnloaded;
                
                // Selection events - Know what the user is focusing on
                Selection.selectionChanged += OnSelectionChanged;
                
                // Hierarchy changes - Track object creation/deletion
                EditorApplication.hierarchyChanged += OnHierarchyChanged;
                
                // Project changes - Asset modifications
                EditorApplication.projectChanged += OnProjectChanged;
                
                // Compilation events - Know when code is building
                CompilationPipeline.compilationStarted += OnCompilationStarted;
                CompilationPipeline.compilationFinished += OnCompilationFinished;
                
                // Update loop for flushing notifications
                EditorApplication.update += OnEditorUpdate;
                
                _isInitialized = true;
                McpLogger.LogInfo("[Notifications] Unity notification collector initialized");
            }
        }

        private static void OnEditorUpdate()
        {
            // Flush notifications periodically
            if (DateTime.UtcNow - _lastFlush > _flushInterval)
            {
                FlushNotifications();
                _lastFlush = DateTime.UtcNow;
            }
        }

        private static void FlushNotifications()
        {
            if (_eventQueue.IsEmpty) return;
            
            var events = new List<NotificationEvent>();
            while (_eventQueue.TryDequeue(out var evt) && events.Count < 10)
            {
                events.Add(evt);
            }
            
            if (events.Count > 0)
            {
                NotificationSender.SendBatch(events);
            }
        }

        #region Console Event Handlers
        
        private static void OnLogMessageReceived(string logString, string stackTrace, LogType type)
        {
            // Critical path - Claude needs to see errors immediately
            if (type == LogType.Error || type == LogType.Exception)
            {
                // Check cooldown to avoid spam
                var errorKey = $"{type}:{logString.GetHashCode()}";
                lock (_recentErrors)
                {
                    if (_recentErrors.TryGetValue(errorKey, out var lastTime))
                    {
                        if (DateTime.UtcNow - lastTime < _errorCooldown)
                            return; // Skip duplicate error
                    }
                    _recentErrors[errorKey] = DateTime.UtcNow;
                }
                
                QueueNotification("console/error", new JObject
                {
                    ["message"] = logString,
                    ["stackTrace"] = stackTrace,
                    ["type"] = type.ToString(),
                    ["timestamp"] = DateTime.UtcNow.ToString("O"),
                    ["scene"] = GetSafeSceneName()
                }, NotificationPriority.Critical);
            }
            else if (type == LogType.Warning)
            {
                QueueNotification("console/warning", new JObject
                {
                    ["message"] = logString,
                    ["type"] = type.ToString(),
                    ["timestamp"] = DateTime.UtcNow.ToString("O")
                }, NotificationPriority.High);
            }
            else if (logString.Contains("[MCP]") || logString.Contains("Claude"))
            {
                // Always show MCP-related logs for debugging
                QueueNotification("console/log", new JObject
                {
                    ["message"] = logString,
                    ["type"] = type.ToString(),
                    ["timestamp"] = DateTime.UtcNow.ToString("O")
                }, NotificationPriority.Normal);
            }
        }
        
        private static void OnLogMessageReceivedThreaded(string logString, string stackTrace, LogType type)
        {
            // Defer to main thread to avoid Unity API access from background thread
            EditorApplication.delayCall += () => OnLogMessageReceived(logString, stackTrace, type);
        }
        
        #endregion
        
        #region Editor State Event Handlers
        
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            QueueNotification("editor/playModeChanged", new JObject
            {
                ["state"] = state.ToString(),
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }, NotificationPriority.High);
        }
        
        private static void OnPauseStateChanged(PauseState state)
        {
            QueueNotification("editor/pauseChanged", new JObject
            {
                ["isPaused"] = state == PauseState.Paused,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }, NotificationPriority.Normal);
        }
        
        #endregion
        
        #region Scene Event Handlers
        
        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            QueueNotification("scene/opened", new JObject
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["mode"] = mode.ToString(),
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }, NotificationPriority.Normal);
        }
        
        private static void OnSceneClosed(Scene scene)
        {
            QueueNotification("scene/closed", new JObject
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }, NotificationPriority.Low);
        }
        
        private static void OnSceneSaved(Scene scene)
        {
            QueueNotification("scene/saved", new JObject
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }, NotificationPriority.Normal);
        }
        
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            QueueNotification("scene/loaded", new JObject
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["mode"] = mode.ToString(),
                ["objectCount"] = scene.rootCount,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }, NotificationPriority.Normal);
        }
        
        private static void OnSceneUnloaded(Scene scene)
        {
            QueueNotification("scene/unloaded", new JObject
            {
                ["name"] = scene.name,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }, NotificationPriority.Low);
        }
        
        #endregion
        
        #region Selection and Hierarchy Event Handlers
        
        private static void OnSelectionChanged()
        {
            var selectedObjects = Selection.objects;
            var selectedNames = new List<string>();
            
            foreach (var obj in selectedObjects)
            {
                if (obj != null)
                    selectedNames.Add(obj.name);
            }
            
            if (selectedNames.Count > 0)
            {
                QueueNotification("editor/selectionChanged", new JObject
                {
                    ["count"] = selectedObjects.Length,
                    ["objects"] = JArray.FromObject(selectedNames),
                    ["activeObject"] = Selection.activeObject?.name,
                    ["timestamp"] = DateTime.UtcNow.ToString("O")
                }, NotificationPriority.Low);
            }
        }
        
        private static void OnHierarchyChanged()
        {
            // Throttle hierarchy changes as they can be frequent
            QueueNotification("scene/hierarchyChanged", new JObject
            {
                ["scene"] = GetSafeSceneName(),
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }, NotificationPriority.VeryLow);
        }
        
        private static void OnProjectChanged()
        {
            QueueNotification("project/changed", new JObject
            {
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }, NotificationPriority.VeryLow);
        }
        
        #endregion
        
        #region Compilation Event Handlers
        
        private static void OnCompilationStarted(object obj)
        {
            QueueNotification("compilation/started", new JObject
            {
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }, NotificationPriority.Normal);
        }
        
        private static void OnCompilationFinished(object obj)
        {
            // CompilationPipeline doesn't directly expose error state
            // The object parameter contains compilation result info
            QueueNotification("compilation/finished", new JObject
            {
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            }, NotificationPriority.Normal);
        }
        
        #endregion
        
        #region Helper Methods
        
        private static string GetSafeSceneName()
        {
            try
            {
                return SceneManager.GetActiveScene().name;
            }
            catch
            {
                return "Unknown";
            }
        }
        
        #endregion
        
        private static void QueueNotification(string eventType, JObject data, NotificationPriority priority)
        {
            _eventQueue.Enqueue(new NotificationEvent
            {
                Type = eventType,
                Data = data,
                Priority = priority,
                Timestamp = DateTime.UtcNow
            });
            
            // Immediate flush for critical events
            if (priority == NotificationPriority.Critical)
            {
                FlushNotifications();
            }
        }
        
        public static void Cleanup()
        {
            if (!_isInitialized) return;
            
            Application.logMessageReceived -= OnLogMessageReceived;
            Application.logMessageReceivedThreaded -= OnLogMessageReceivedThreaded;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.pauseStateChanged -= OnPauseStateChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.projectChanged -= OnProjectChanged;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            EditorApplication.update -= OnEditorUpdate;
            
            _isInitialized = false;
        }
    }
    
    public class NotificationEvent
    {
        public string Type { get; set; }
        public JObject Data { get; set; }
        public NotificationPriority Priority { get; set; }
        public DateTime Timestamp { get; set; }
    }
    
    public enum NotificationPriority
    {
        Critical = 0,  // Errors, exceptions
        High = 1,      // Warnings, state changes
        Normal = 2,    // General events
        Low = 3,       // Selection changes
        VeryLow = 4    // Frequent updates
    }
}