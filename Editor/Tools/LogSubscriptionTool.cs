using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEditor;

namespace McpUnity.Tools
{
    /// <summary>
    /// MCP tool for subscribing to Unity console logs in real-time
    /// Provides streaming log updates with filtering capabilities
    /// </summary>
    public class LogSubscriptionTool : McpToolBase
    {
        private static readonly Dictionary<string, LogSubscription> ActiveSubscriptions = new Dictionary<string, LogSubscription>();
        private static readonly object SubscriptionLock = new object();

        private class LogSubscription
        {
            public string Id { get; set; }
            public LogType[] FilterTypes { get; set; }
            public string SearchPattern { get; set; }
            public Queue<LogEntry> Buffer { get; set; } = new Queue<LogEntry>();
            public DateTime LastPolled { get; set; } = DateTime.Now;
            public int MaxBufferSize { get; set; } = 100;
            public bool IncludeStackTrace { get; set; } = true;
        }

        private class LogEntry
        {
            public string Message { get; set; }
            public string StackTrace { get; set; }
            public LogType Type { get; set; }
            public DateTime Timestamp { get; set; }
        }

        static LogSubscriptionTool()
        {
            // Register for Unity log messages globally
            Application.logMessageReceivedThreaded += OnLogMessageReceived;

            // Clean up on editor quit
            EditorApplication.quitting += () =>
            {
                Application.logMessageReceivedThreaded -= OnLogMessageReceived;
                lock (SubscriptionLock)
                {
                    ActiveSubscriptions.Clear();
                }
            };
        }

        public LogSubscriptionTool()
        {
            Name = "subscribe_unity_logs";
            Description = "Subscribe to Unity console logs with real-time updates. Supports filtering by log type and search patterns.";

            // Set the input schema
            InputSchema = JObject.FromObject(new
            {
                type = "object",
                properties = new
                {
                    action = new
                    {
                        type = "string",
                        description = "Action to perform: 'subscribe', 'poll', 'unsubscribe', 'list'",
                        @enum = new[] { "subscribe", "poll", "unsubscribe", "list" }
                    },
                    subscriptionId = new
                    {
                        type = "string",
                        description = "Unique subscription identifier (required for poll/unsubscribe, auto-generated for subscribe)"
                    },
                    logTypes = new
                    {
                        type = "array",
                        description = "Filter by log types (optional, defaults to all)",
                        items = new
                        {
                            type = "string",
                            @enum = new[] { "Log", "Warning", "Error", "Exception", "Assert" }
                        }
                    },
                    searchPattern = new
                    {
                        type = "string",
                        description = "Filter logs by text pattern (case-insensitive substring match)"
                    },
                    maxBufferSize = new
                    {
                        type = "integer",
                        description = "Maximum number of logs to buffer between polls (default: 100)",
                        minimum = 10,
                        maximum = 1000
                    },
                    includeStackTrace = new
                    {
                        type = "boolean",
                        description = "Include stack traces in log entries (default: true)"
                    }
                },
                required = new[] { "action" }
            });
        }

        public override JObject Execute(JObject arguments)
        {
            try
            {
                string action = arguments["action"]?.ToString()?.ToLower();

                switch (action)
                {
                    case "subscribe":
                        return Subscribe(arguments);
                    case "poll":
                        return Poll(arguments);
                    case "unsubscribe":
                        return Unsubscribe(arguments);
                    case "list":
                        return ListSubscriptions();
                    default:
                        throw new ArgumentException($"Unknown action: {action}");
                }
            }
            catch (Exception ex)
            {
                return JObject.FromObject(new
                {
                    content = new object[]
                    {
                        new { type = "text", text = $"Log subscription error: {ex.Message}" }
                    },
                    isError = true
                });
            }
        }

        private JObject Subscribe(JObject arguments)
        {
            string subscriptionId = arguments["subscriptionId"]?.ToString() ?? Guid.NewGuid().ToString();

            // Parse log type filters
            var logTypesArray = arguments["logTypes"] as JArray;
            LogType[] filterTypes = null;
            if (logTypesArray != null && logTypesArray.Count > 0)
            {
                var typeList = new List<LogType>();
                foreach (var typeStr in logTypesArray)
                {
                    if (Enum.TryParse<LogType>(typeStr.ToString(), out var logType))
                    {
                        typeList.Add(logType);
                    }
                }
                filterTypes = typeList.ToArray();
            }

            var subscription = new LogSubscription
            {
                Id = subscriptionId,
                FilterTypes = filterTypes,
                SearchPattern = arguments["searchPattern"]?.ToString(),
                MaxBufferSize = arguments["maxBufferSize"]?.Value<int>() ?? 100,
                IncludeStackTrace = arguments["includeStackTrace"]?.Value<bool>() ?? true
            };

            lock (SubscriptionLock)
            {
                if (ActiveSubscriptions.ContainsKey(subscriptionId))
                {
                    return JObject.FromObject(new
                    {
                        content = new object[]
                        {
                            new { type = "text", text = $"Subscription '{subscriptionId}' already exists. Use a different ID or unsubscribe first." }
                        },
                        isError = true
                    });
                }

                ActiveSubscriptions[subscriptionId] = subscription;
            }

            // Get current Unity log statistics
            int errorCount = 0, warningCount = 0, logCount = 0;
#if UNITY_6000_0_OR_NEWER
            ConsoleWindowUtility.GetConsoleLogCounts(out errorCount, out warningCount, out logCount);
#endif

            return JObject.FromObject(new
            {
                content = new object[]
                {
                    new { type = "text", text = $"✓ Log subscription created: {subscriptionId}" },
                    new { type = "text", text = $"Filters: {(filterTypes != null ? string.Join(", ", filterTypes) : "All types")}" },
                    new { type = "text", text = $"Search: {subscription.SearchPattern ?? "None"}" },
                    new { type = "text", text = $"Buffer size: {subscription.MaxBufferSize}" },
                    new { type = "text", text = $"Current console state - Errors: {errorCount}, Warnings: {warningCount}, Logs: {logCount}" },
                    new { type = "text", text = "Use 'poll' action with this ID to retrieve buffered logs" }
                },
                subscriptionId = subscriptionId,
                isError = false
            });
        }

        private JObject Poll(JObject arguments)
        {
            string subscriptionId = arguments["subscriptionId"]?.ToString();
            if (string.IsNullOrEmpty(subscriptionId))
            {
                return JObject.FromObject(new
                {
                    content = new object[]
                    {
                        new { type = "text", text = "subscriptionId is required for polling" }
                    },
                    isError = true
                });
            }

            lock (SubscriptionLock)
            {
                if (!ActiveSubscriptions.TryGetValue(subscriptionId, out var subscription))
                {
                    return JObject.FromObject(new
                    {
                        content = new object[]
                        {
                            new { type = "text", text = $"Subscription '{subscriptionId}' not found" }
                        },
                        isError = true
                    });
                }

                var logs = new JArray();
                int count = 0;

                while (subscription.Buffer.Count > 0 && count < subscription.MaxBufferSize)
                {
                    var entry = subscription.Buffer.Dequeue();
                    var logObj = new JObject
                    {
                        ["timestamp"] = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        ["type"] = entry.Type.ToString(),
                        ["message"] = entry.Message
                    };

                    if (subscription.IncludeStackTrace && !string.IsNullOrEmpty(entry.StackTrace))
                    {
                        logObj["stackTrace"] = entry.StackTrace;
                    }

                    logs.Add(logObj);
                    count++;
                }

                subscription.LastPolled = DateTime.Now;

                return JObject.FromObject(new
                {
                    content = new object[]
                    {
                        new {
                            type = "text",
                            text = count > 0
                                ? $"Retrieved {count} log entries from subscription '{subscriptionId}'"
                                : $"No new logs for subscription '{subscriptionId}'"
                        },
                        new { type = "json", data = logs }
                    },
                    logs = logs,
                    count = count,
                    subscriptionId = subscriptionId,
                    hasMore = subscription.Buffer.Count > 0,
                    isError = false
                });
            }
        }

        private JObject Unsubscribe(JObject arguments)
        {
            string subscriptionId = arguments["subscriptionId"]?.ToString();
            if (string.IsNullOrEmpty(subscriptionId))
            {
                return JObject.FromObject(new
                {
                    content = new object[]
                    {
                        new { type = "text", text = "subscriptionId is required for unsubscribe" }
                    },
                    isError = true
                });
            }

            lock (SubscriptionLock)
            {
                if (ActiveSubscriptions.Remove(subscriptionId))
                {
                    return JObject.FromObject(new
                    {
                        content = new object[]
                        {
                            new { type = "text", text = $"✓ Subscription '{subscriptionId}' removed" }
                        },
                        isError = false
                    });
                }
                else
                {
                    return JObject.FromObject(new
                    {
                        content = new object[]
                        {
                            new { type = "text", text = $"Subscription '{subscriptionId}' not found" }
                        },
                        isError = true
                    });
                }
            }
        }

        private JObject ListSubscriptions()
        {
            lock (SubscriptionLock)
            {
                var subscriptions = new JArray();
                foreach (var kvp in ActiveSubscriptions)
                {
                    var sub = kvp.Value;
                    subscriptions.Add(new JObject
                    {
                        ["id"] = sub.Id,
                        ["filterTypes"] = sub.FilterTypes != null ? new JArray(sub.FilterTypes) : null,
                        ["searchPattern"] = sub.SearchPattern,
                        ["bufferCount"] = sub.Buffer.Count,
                        ["maxBufferSize"] = sub.MaxBufferSize,
                        ["lastPolled"] = sub.LastPolled.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["includeStackTrace"] = sub.IncludeStackTrace
                    });
                }

                return JObject.FromObject(new
                {
                    content = new object[]
                    {
                        new { type = "text", text = $"Active subscriptions: {ActiveSubscriptions.Count}" },
                        new { type = "json", data = subscriptions }
                    },
                    subscriptions = subscriptions,
                    count = ActiveSubscriptions.Count,
                    isError = false
                });
            }
        }

        private static void OnLogMessageReceived(string message, string stackTrace, LogType type)
        {
            lock (SubscriptionLock)
            {
                if (ActiveSubscriptions.Count == 0) return;

                var entry = new LogEntry
                {
                    Message = message,
                    StackTrace = stackTrace,
                    Type = type,
                    Timestamp = DateTime.Now
                };

                foreach (var subscription in ActiveSubscriptions.Values)
                {
                    // Check type filter
                    if (subscription.FilterTypes != null && subscription.FilterTypes.Length > 0)
                    {
                        if (Array.IndexOf(subscription.FilterTypes, type) == -1)
                            continue;
                    }

                    // Check search pattern
                    if (!string.IsNullOrEmpty(subscription.SearchPattern))
                    {
                        if (!message.Contains(subscription.SearchPattern, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }

                    // Add to buffer (with overflow protection)
                    if (subscription.Buffer.Count >= subscription.MaxBufferSize)
                    {
                        subscription.Buffer.Dequeue(); // Remove oldest
                    }
                    subscription.Buffer.Enqueue(entry);
                }

                // Clean up stale subscriptions (not polled for > 5 minutes)
                var staleTime = DateTime.Now.AddMinutes(-5);
                var toRemove = ActiveSubscriptions
                    .Where(kvp => kvp.Value.LastPolled < staleTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var id in toRemove)
                {
                    ActiveSubscriptions.Remove(id);
                    Debug.Log($"[MCP Unity] Removed stale log subscription: {id}");
                }
            }
        }
    }
}