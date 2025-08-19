using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace McpUnity.DirectMcp
{
    /// <summary>
    /// Unity MCP Server - Bridges Unity Editor with MCP protocol
    /// Features:
    /// - Automatic Unity project detection
    /// - Dynamic tool/resource discovery from Unity via WebSocket
    /// - Resilient connection with auto-reconnect
    /// - Hot-reload support with ToolListChangedNotification
    /// </summary>
    public class Program
    {
        static async Task Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);

            // Configure logging to stderr for MCP compatibility
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });

            // Register Unity bridge service
            builder.Services.AddSingleton<UnityBridge>();
            builder.Services.AddHostedService<UnityBridge>(provider => provider.GetRequiredService<UnityBridge>());
            builder.Services.AddSingleton<DynamicToolNotifier>();
            builder.Services.AddHostedService<DynamicToolNotifier>(provider => provider.GetRequiredService<DynamicToolNotifier>());

            // Configure MCP server with full protocol support
            builder.Services.AddMcpServer(options =>
            {
                // Server identification
                options.ServerInfo = new Implementation
                {
                    Name = "unity-mcp",
                    Version = "2.0.0"
                };
            })
                .WithStdioServerTransport()
                
                // Core tool handlers
                .WithListToolsHandler(async (context, ct) =>
                {
                    var bridge = context.Services!.GetRequiredService<UnityBridge>();
                    return await bridge.GetToolListAsync(context, ct);
                })
                .WithCallToolHandler(async (context, ct) =>
                {
                    var bridge = context.Services!.GetRequiredService<UnityBridge>();
                    return await bridge.CallToolAsync(context, ct);
                })
                
                // Resource handlers
                .WithListResourcesHandler(async (context, ct) =>
                {
                    var bridge = context.Services!.GetRequiredService<UnityBridge>();
                    return await bridge.GetResourceListAsync(context, ct);
                })
                .WithReadResourceHandler(async (context, ct) =>
                {
                    var bridge = context.Services!.GetRequiredService<UnityBridge>();
                    return await bridge.ReadResourceAsync(context, ct);
                })
                .WithSubscribeToResourcesHandler(async (context, ct) =>
                {
                    var bridge = context.Services!.GetRequiredService<UnityBridge>();
                    return await bridge.SubscribeToResourceAsync(context, ct);
                })
                .WithUnsubscribeFromResourcesHandler(async (context, ct) =>
                {
                    var bridge = context.Services!.GetRequiredService<UnityBridge>();
                    return await bridge.UnsubscribeFromResourceAsync(context, ct);
                })
                
                // Prompt handlers (Unity can provide prompt templates)
                .WithListPromptsHandler(async (context, ct) =>
                {
                    var bridge = context.Services!.GetRequiredService<UnityBridge>();
                    return await bridge.GetPromptListAsync(context, ct);
                })
                .WithGetPromptHandler(async (context, ct) =>
                {
                    var bridge = context.Services!.GetRequiredService<UnityBridge>();
                    return await bridge.GetPromptAsync(context, ct);
                })
                
                // Completion handler for autocomplete
                .WithCompleteHandler(async (context, ct) =>
                {
                    var bridge = context.Services!.GetRequiredService<UnityBridge>();
                    return await bridge.CompleteAsync(context, ct);
                })
                
                // Logging control
                .WithSetLoggingLevelHandler(async (context, ct) =>
                {
                    var bridge = context.Services!.GetRequiredService<UnityBridge>();
                    return await bridge.SetLoggingLevelAsync(context, ct);
                })
                
                // Note: Ping is handled automatically by the MCP SDK
                ;

            var host = builder.Build();
            await host.RunAsync();
        }
    }

    /// <summary>
    /// Service to send tool change notifications
    /// </summary>
    public class DynamicToolNotifier : IHostedService
    {
        private readonly ILogger<DynamicToolNotifier> _logger;
        private readonly UnityBridge _bridge;
        private readonly IMcpServer _mcpServer;
        private Timer? _toolChangeMonitor;
        private string? _lastToolsHash;
        private string? _lastResourcesHash;
        private string? _lastPromptsHash;

        public DynamicToolNotifier(ILogger<DynamicToolNotifier> logger, UnityBridge bridge, IMcpServer mcpServer)
        {
            _logger = logger;
            _bridge = bridge;
            _mcpServer = mcpServer;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Start tool change monitor (checks for changes every 3 seconds)
            _toolChangeMonitor = new Timer(
                async _ => await CheckForChangesAsync(),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(3)
            );
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _toolChangeMonitor?.Dispose();
            return Task.CompletedTask;
        }

        private async Task CheckForChangesAsync()
        {
            try
            {
                // Check for tool changes
                var toolsData = await _bridge.GetRawToolsFromUnityAsync();
                if (toolsData != null)
                {
                    var currentToolsHash = ComputeHash(toolsData);
                    
                    if (_lastToolsHash != null && _lastToolsHash != currentToolsHash)
                    {
                        _logger.LogDebug("Tools have changed, sending notification");
                        _bridge.ClearToolCache();
                        
                        // Send ToolListChangedNotification to Claude
                        await _mcpServer.SendNotificationAsync(
                            NotificationMethods.ToolListChangedNotification,
                            new ToolListChangedNotificationParams()
                        );
                    }
                    
                    _lastToolsHash = currentToolsHash;
                }
                
                // Check for resource changes
                var resourcesData = await _bridge.GetRawResourcesFromUnityAsync();
                if (resourcesData != null)
                {
                    var currentResourcesHash = ComputeHash(resourcesData);
                    
                    if (_lastResourcesHash != null && _lastResourcesHash != currentResourcesHash)
                    {
                        _logger.LogDebug("Resources have changed, sending notification");
                        _bridge.ClearResourceCache();
                        
                        // Send ResourceListChangedNotification to Claude
                        await _mcpServer.SendNotificationAsync(
                            NotificationMethods.ResourceListChangedNotification,
                            new ResourceListChangedNotificationParams()
                        );
                    }
                    
                    _lastResourcesHash = currentResourcesHash;
                }
                
                // Check for prompt changes
                var promptsData = await _bridge.GetRawPromptsFromUnityAsync();
                if (promptsData != null)
                {
                    var currentPromptsHash = ComputeHash(promptsData);
                    
                    if (_lastPromptsHash != null && _lastPromptsHash != currentPromptsHash)
                    {
                        _logger.LogDebug("Prompts have changed, sending notification");
                        _bridge.ClearPromptCache();
                        
                        // Send PromptListChangedNotification to Claude
                        await _mcpServer.SendNotificationAsync(
                            NotificationMethods.PromptListChangedNotification,
                            new PromptListChangedNotificationParams()
                        );
                    }
                    
                    _lastPromptsHash = currentPromptsHash;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error checking for changes");
            }
        }

        private string ComputeHash(JArray items)
        {
            var itemsString = JsonConvert.SerializeObject(items, Formatting.None);
            return Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(itemsString)));
        }
    }

    /// <summary>
    /// Manages WebSocket connection to Unity Editor and provides dynamic tool/resource discovery
    /// </summary>
    public class UnityBridge : IHostedService
    {
        private ClientWebSocket? _webSocket;
        private readonly ILogger<UnityBridge> _logger;
        private readonly IMcpServer? _mcpServer;
        private readonly Dictionary<string, TaskCompletionSource<JObject>> _pendingRequests = new();
        private int _requestIdCounter = 1;
        private string? _unityProjectPath;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private CancellationTokenSource? _receiveCts;
        private string _webSocketUri = "ws://localhost:8090/McpUnity";
        
        // Dynamic cache with short TTL for hot-reload support
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(2);
        private DateTime _lastToolRefresh = DateTime.MinValue;
        private DateTime _lastResourceRefresh = DateTime.MinValue;
        private DateTime _lastPromptRefresh = DateTime.MinValue;
        private List<Tool>? _cachedTools;
        private List<Resource>? _cachedResources;
        private List<Prompt>? _cachedPrompts;
        
        // Resource subscriptions
        private readonly HashSet<string> _resourceSubscriptions = new();
        
        // Logging level
        private LoggingLevel _currentLoggingLevel = LoggingLevel.Info;
        
        // Background reconnection management
        private Timer? _reconnectTimer;
        private bool _isReconnecting = false;
        private DateTime _lastConnectionAttempt = DateTime.MinValue;
        private int _connectionFailures = 0;

        public UnityBridge(ILogger<UnityBridge> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            // IMcpServer might not be available during construction
            _mcpServer = serviceProvider.GetService<IMcpServer>();
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _unityProjectPath = FindUnityProject();
            if (_unityProjectPath == null)
            {
                _logger.LogError("Could not find Unity project");
                return;
            }
            
            _logger.LogInformation("Unity project: {ProjectPath}", _unityProjectPath);
            
            // Load settings
            var settingsPath = Path.Combine(_unityProjectPath, "ProjectSettings", "McpUnitySettings.json");
            int port = 8090;
            
            if (File.Exists(settingsPath))
            {
                var settingsJson = await File.ReadAllTextAsync(settingsPath, cancellationToken);
                var settings = JObject.Parse(settingsJson);
                port = settings["Port"]?.Value<int>() ?? 8090;
                _logger.LogInformation("Loaded settings from {SettingsPath}", settingsPath);
            }
            
            _webSocketUri = $"ws://localhost:{port}/McpUnity";
            
            // Start initial connection
            _ = Task.Run(async () => await EnsureConnectedAsync(), cancellationToken);
            
            // Start background reconnection timer
            _reconnectTimer = new Timer(
                async _ => await MonitorConnectionAsync(),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5)
            );
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _reconnectTimer?.Dispose();
            _receiveCts?.Cancel();
            _webSocket?.Dispose();
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// Clear the tool cache to force refresh on next request
        /// </summary>
        public void ClearToolCache()
        {
            _cachedTools = null;
            _lastToolRefresh = DateTime.MinValue;
        }
        
        /// <summary>
        /// Clear the resource cache to force refresh on next request
        /// </summary>
        public void ClearResourceCache()
        {
            _cachedResources = null;
            _lastResourceRefresh = DateTime.MinValue;
        }
        
        /// <summary>
        /// Get raw tools data from Unity for change detection
        /// </summary>
        public async Task<JArray?> GetRawToolsFromUnityAsync()
        {
            if (_webSocket?.State != WebSocketState.Open)
                return null;
                
            try
            {
                var response = await SendToUnityAsync(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "tools/list",
                    ["params"] = new JObject()
                });
                
                return response?["result"]?["tools"] as JArray;
            }
            catch
            {
                return null;
            }
        }
        
        /// <summary>
        /// Get raw resources data from Unity for change detection
        /// </summary>
        public async Task<JArray?> GetRawResourcesFromUnityAsync()
        {
            if (_webSocket?.State != WebSocketState.Open)
                return null;
                
            try
            {
                var response = await SendToUnityAsync(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "resources/list",
                    ["params"] = new JObject()
                });
                
                return response?["result"]?["resources"] as JArray;
            }
            catch
            {
                return null;
            }
        }
        
        /// <summary>
        /// Get raw prompts data from Unity for change detection
        /// </summary>
        public async Task<JArray?> GetRawPromptsFromUnityAsync()
        {
            if (_webSocket?.State != WebSocketState.Open)
                return null;
                
            try
            {
                var response = await SendToUnityAsync(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "prompts/list",
                    ["params"] = new JObject()
                });
                
                return response?["result"]?["prompts"] as JArray;
            }
            catch
            {
                return null;
            }
        }
        
        /// <summary>
        /// Clear the prompt cache to force refresh on next request
        /// </summary>
        public void ClearPromptCache()
        {
            _cachedPrompts = null;
            _lastPromptRefresh = DateTime.MinValue;
        }
        
        private async Task MonitorConnectionAsync()
        {
            // Skip if already reconnecting
            if (_isReconnecting)
                return;
                
            try
            {
                // Check if connection is still alive
                if (_webSocket?.State == WebSocketState.Open)
                {
                    // Connection is good, reset failure counter
                    if (_connectionFailures > 0)
                    {
                        _logger.LogInformation("Unity connection restored");
                        _connectionFailures = 0;
                    }
                    return;
                }
                
                // Connection lost, try to reconnect
                _isReconnecting = true;
                _connectionFailures++;
                
                if (_connectionFailures == 1)
                {
                    _logger.LogInformation("Unity connection lost (likely recompiling), attempting to reconnect...");
                }
                
                // Don't spam logs for repeated failures
                if (_connectionFailures % 10 == 0)
                {
                    _logger.LogDebug("Reconnection attempt {Count}", _connectionFailures);
                }
                
                await EnsureConnectedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in connection monitor");
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        /// <summary>
        /// Handles ListTools requests from MCP clients
        /// </summary>
        public async ValueTask<ListToolsResult> GetToolListAsync(
            RequestContext<ListToolsRequestParams> context, 
            CancellationToken cancellationToken)
        {
            // Check cache
            if (_cachedTools != null && DateTime.Now - _lastToolRefresh < _cacheExpiration)
            {
                return new ListToolsResult { Tools = _cachedTools };
            }
            
            await EnsureConnectedAsync();
            
            try
            {
                var response = await SendToUnityAsync(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "tools/list",
                    ["params"] = new JObject()
                });
                
                var tools = new List<Tool>();
                
                if (response?["result"]?["tools"] is JArray toolArray)
                {
                    foreach (var tool in toolArray)
                    {
                        var name = tool["name"]?.ToString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            var schema = ConvertToJsonElement(tool["inputSchema"]);
                            tools.Add(new Tool
                            {
                                Name = name,
                                Description = tool["description"]?.ToString(),
                                InputSchema = schema
                            });
                        }
                    }
                }
                
                _cachedTools = tools;
                _lastToolRefresh = DateTime.Now;
                
                return new ListToolsResult { Tools = tools };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get tools from Unity");
                return new ListToolsResult { Tools = _cachedTools ?? new List<Tool>() };
            }
        }

        /// <summary>
        /// Handles CallTool requests from MCP clients
        /// </summary>
        public async ValueTask<CallToolResult> CallToolAsync(
            RequestContext<CallToolRequestParams> context, 
            CancellationToken cancellationToken)
        {
            await EnsureConnectedAsync();
            
            var toolName = context.Params!.Name;
            var arguments = ConvertToJObject(context.Params.Arguments);
            
            try
            {
                var response = await SendToUnityAsync(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = toolName,
                        ["arguments"] = arguments
                    }
                });
                
                if (response?["result"] != null)
                {
                    // Unity tools return success/message/data format
                    var result = response["result"] as JObject;
                    if (result != null)
                    {
                        var content = new List<ContentBlock>();
                        
                        // Convert Unity's response to MCP content blocks
                        if (result["data"] != null)
                        {
                            content.Add(new TextContentBlock 
                            { 
                                Text = result["data"].ToString() 
                            });
                        }
                        else if (result["message"] != null)
                        {
                            content.Add(new TextContentBlock 
                            { 
                                Text = result["message"].ToString() 
                            });
                        }
                        
                        return new CallToolResult { Content = content };
                    }
                }
                
                if (response?["error"] != null)
                {
                    throw new Exception($"Tool call failed: {response["error"]["message"]}");
                }
                
                return new CallToolResult 
                { 
                    Content = [new TextContentBlock { Text = "{}" }] 
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Unity tool {ToolName}", toolName);
                return new CallToolResult 
                { 
                    Content = [new TextContentBlock { Text = JsonConvert.SerializeObject(new 
                    { 
                        success = false, 
                        message = ex.Message 
                    })}] 
                };
            }
        }

        /// <summary>
        /// Handles ListResources requests from MCP clients
        /// </summary>
        public async ValueTask<ListResourcesResult> GetResourceListAsync(
            RequestContext<ListResourcesRequestParams> context, 
            CancellationToken cancellationToken)
        {
            // Check cache
            if (_cachedResources != null && DateTime.Now - _lastResourceRefresh < _cacheExpiration)
            {
                return new ListResourcesResult { Resources = _cachedResources };
            }
            
            await EnsureConnectedAsync();
            
            try
            {
                var response = await SendToUnityAsync(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "resources/list",
                    ["params"] = new JObject()
                });
                
                var resources = new List<Resource>();
                
                if (response?["result"]?["resources"] is JArray resourceArray)
                {
                    foreach (var resource in resourceArray)
                    {
                        var uri = resource["uri"]?.ToString();
                        if (!string.IsNullOrEmpty(uri))
                        {
                            resources.Add(new Resource
                            {
                                Uri = uri,
                                Name = resource["name"]?.ToString() ?? "",
                                Description = resource["description"]?.ToString(),
                                MimeType = resource["mimeType"]?.ToString()
                            });
                        }
                    }
                }
                
                _cachedResources = resources;
                _lastResourceRefresh = DateTime.Now;
                
                return new ListResourcesResult { Resources = resources };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get resources from Unity");
                return new ListResourcesResult { Resources = _cachedResources ?? new List<Resource>() };
            }
        }

        /// <summary>
        /// Handles ReadResource requests from MCP clients
        /// WORKAROUND: Return Unity's response directly to avoid MCP SDK serialization issues
        /// </summary>
        public async ValueTask<ReadResourceResult> ReadResourceAsync(
            RequestContext<ReadResourceRequestParams> context, 
            CancellationToken cancellationToken)
        {
            await EnsureConnectedAsync();
            
            var uri = context.Params!.Uri;
            
            try
            {
                var response = await SendToUnityAsync(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "resources/read",
                    ["params"] = new JObject
                    {
                        ["uri"] = uri
                    }
                });
                
                if (response?["result"] != null)
                {
                    var result = response["result"];
                    
                    _logger.LogDebug("Unity response for {Uri}: {Response}", uri, result.ToString());
                    
                    // Unity already returns proper MCP format, extract the contents directly
                    if (result["contents"] is JArray contents)
                    {
                        var resourceContents = new List<ResourceContents>();
                        foreach (var content in contents)
                        {
                            var text = content["text"]?.ToString() ?? "";
                            var mimeType = content["mimeType"]?.ToString() ?? "text/plain";
                            var contentUri = content["uri"]?.ToString() ?? uri;
                            
                            resourceContents.Add(new TextResourceContents 
                            { 
                                Uri = contentUri,
                                Text = text,
                                MimeType = mimeType
                            });
                        }
                        
                        _logger.LogDebug("Created {Count} resource contents for {Uri}", resourceContents.Count, uri);
                        return new ReadResourceResult { Contents = resourceContents };
                    }
                    
                    // If Unity didn't return expected format, try to wrap it
                    _logger.LogDebug("Using fallback for {Uri}, result type: {Type}", uri, result.GetType().Name);
                    return new ReadResourceResult 
                    { 
                        Contents = [new TextResourceContents { Uri = uri, Text = result.ToString(), MimeType = "application/json" }] 
                    };
                }
                
                if (response?["error"] != null)
                {
                    throw new Exception($"Resource read failed: {response["error"]["message"]}");
                }
                
                return new ReadResourceResult 
                { 
                    Contents = [new TextResourceContents { Uri = uri, Text = "{}", MimeType = "application/json" }] 
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading Unity resource {Uri}", uri);
                return new ReadResourceResult 
                { 
                    Contents = [new TextResourceContents { Uri = uri, Text = JsonConvert.SerializeObject(new 
                    { 
                        error = ex.Message 
                    }), MimeType = "application/json" }] 
                };
            }
        }

        private async Task EnsureConnectedAsync()
        {
            await _connectionLock.WaitAsync();
            try
            {
                if (_webSocket?.State == WebSocketState.Open)
                {
                    return;
                }
                
                _receiveCts?.Cancel();
                _webSocket?.Dispose();
                
                _webSocket = new ClientWebSocket();
                _webSocket.Options.SetRequestHeader("X-Client-Name", "Unity MCP Server");
                
                int retryCount = 0;
                const int maxRetries = 10;
                
                while (retryCount < maxRetries)
                {
                    try
                    {
                        _logger.LogInformation("Connecting to Unity at {Uri}...", _webSocketUri);
                        await _webSocket.ConnectAsync(new Uri(_webSocketUri), CancellationToken.None);
                        _logger.LogInformation("Connected to Unity");
                        
                        // Start receive loop
                        _receiveCts = new CancellationTokenSource();
                        _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
                        
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Connection attempt {Attempt} failed: {Message}", 
                            retryCount + 1, ex.Message);
                        retryCount++;
                        
                        if (retryCount < maxRetries)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Min(retryCount * 2, 30)));
                        }
                    }
                }
                
                _logger.LogError("Failed to connect to Unity after {MaxRetries} attempts", maxRetries);
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();
            
            try
            {
                while (!cancellationToken.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        
                        if (result.EndOfMessage)
                        {
                            var message = messageBuilder.ToString();
                            messageBuilder.Clear();
                            ProcessUnityResponse(message);
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Unity closed the connection");
                        break;
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error in receive loop");
            }
        }

        private void ProcessUnityResponse(string message)
        {
            try
            {
                var response = JObject.Parse(message);
                var requestId = response["id"]?.ToString();
                
                if (requestId != null && _pendingRequests.TryGetValue(requestId, out var tcs))
                {
                    _pendingRequests.Remove(requestId);
                    tcs.SetResult(response);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Unity response");
            }
        }

        private async Task<JObject?> SendToUnityAsync(JObject request)
        {
            if (_webSocket?.State != WebSocketState.Open)
            {
                await EnsureConnectedAsync();
                if (_webSocket?.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("Not connected to Unity");
                }
            }
            
            var requestId = (_requestIdCounter++).ToString();
            request["id"] = requestId;
            
            var tcs = new TaskCompletionSource<JObject>();
            _pendingRequests[requestId] = tcs;
            
            try
            {
                var messageBytes = Encoding.UTF8.GetBytes(request.ToString(Formatting.None));
                await _webSocket.SendAsync(new ArraySegment<byte>(messageBytes), 
                    WebSocketMessageType.Text, true, CancellationToken.None);
                
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token));
                
                if (completedTask == tcs.Task)
                {
                    return await tcs.Task;
                }
                
                _pendingRequests.Remove(requestId);
                throw new TimeoutException("Request to Unity timed out");
            }
            catch
            {
                _pendingRequests.Remove(requestId);
                throw;
            }
        }

        private string? FindUnityProject()
        {
            var currentDir = Directory.GetCurrentDirectory();
            var exePath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
            
            var dirsToCheck = new List<string> { currentDir };
            if (!string.IsNullOrEmpty(exePath))
            {
                dirsToCheck.Add(exePath);
            }
            
            foreach (var startDir in dirsToCheck)
            {
                var dir = startDir;
                for (int i = 0; i < 10; i++)
                {
                    if (IsUnityProject(dir))
                    {
                        return dir;
                    }
                    
                    var parent = Directory.GetParent(dir);
                    if (parent == null) break;
                    dir = parent.FullName;
                }
            }
            
            return null;
        }

        private bool IsUnityProject(string path)
        {
            return Directory.Exists(Path.Combine(path, "Assets")) &&
                   Directory.Exists(Path.Combine(path, "ProjectSettings"));
        }
        
        /// <summary>
        /// Convert Newtonsoft.Json JObject to System.Text.Json JsonElement
        /// </summary>
        private System.Text.Json.JsonElement ConvertToJsonElement(JToken? token)
        {
            if (token == null) 
                return System.Text.Json.JsonDocument.Parse("{}").RootElement;
            var json = token.ToString(Formatting.None);
            return System.Text.Json.JsonDocument.Parse(json).RootElement;
        }
        
        /// <summary>
        /// Convert System.Text.Json JsonElement dictionary to Newtonsoft.Json JObject
        /// </summary>
        private JObject ConvertToJObject(IReadOnlyDictionary<string, System.Text.Json.JsonElement>? arguments)
        {
            if (arguments == null) return new JObject();
            
            var jObject = new JObject();
            foreach (var kvp in arguments)
            {
                var jsonElement = kvp.Value;
                if (jsonElement.ValueKind != System.Text.Json.JsonValueKind.Null &&
                    jsonElement.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                {
                    var json = jsonElement.GetRawText();
                    jObject[kvp.Key] = JToken.Parse(json);
                }
            }
            return jObject;
        }
        
        /// <summary>
        /// Handles prompt list requests
        /// </summary>
        public async ValueTask<ListPromptsResult> GetPromptListAsync(
            RequestContext<ListPromptsRequestParams> context,
            CancellationToken cancellationToken)
        {
            // Check cache
            if (_cachedPrompts != null && DateTime.Now - _lastPromptRefresh < _cacheExpiration)
            {
                return new ListPromptsResult { Prompts = _cachedPrompts };
            }
            
            await EnsureConnectedAsync();
            
            try
            {
                var response = await SendToUnityAsync(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "prompts/list",
                    ["params"] = new JObject()
                });
                
                var prompts = new List<Prompt>();
                
                if (response?["result"]?["prompts"] is JArray promptArray)
                {
                    foreach (var prompt in promptArray)
                    {
                        var name = prompt["name"]?.ToString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            prompts.Add(new Prompt
                            {
                                Name = name,
                                Description = prompt["description"]?.ToString(),
                                Arguments = ParsePromptArguments(prompt["arguments"] as JArray)
                            });
                        }
                    }
                }
                
                _cachedPrompts = prompts;
                _lastPromptRefresh = DateTime.Now;
                
                return new ListPromptsResult { Prompts = prompts };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get prompts from Unity");
                return new ListPromptsResult { Prompts = _cachedPrompts ?? new List<Prompt>() };
            }
        }
        
        /// <summary>
        /// Handles get prompt requests
        /// </summary>
        public async ValueTask<GetPromptResult> GetPromptAsync(
            RequestContext<GetPromptRequestParams> context,
            CancellationToken cancellationToken)
        {
            await EnsureConnectedAsync();
            
            var promptName = context.Params!.Name;
            var arguments = context.Params.Arguments;
            
            try
            {
                var response = await SendToUnityAsync(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "prompts/get",
                    ["params"] = new JObject
                    {
                        ["name"] = promptName,
                        ["arguments"] = arguments != null ? JObject.FromObject(arguments) : new JObject()
                    }
                });
                
                if (response?["result"] != null)
                {
                    var result = response["result"];
                    var messages = new List<PromptMessage>();
                    
                    if (result["messages"] is JArray messageArray)
                    {
                        foreach (var msg in messageArray)
                        {
                            var role = ParseRole(msg["role"]?.ToString() ?? "user");
                            var content = ParseContent(msg["content"]);
                            messages.Add(new PromptMessage { Role = role, Content = content });
                        }
                    }
                    
                    return new GetPromptResult 
                    { 
                        Description = result["description"]?.ToString(),
                        Messages = messages 
                    };
                }
                
                throw new Exception($"Prompt not found: {promptName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Unity prompt {PromptName}", promptName);
                throw;
            }
        }
        
        /// <summary>
        /// Handles completion requests for autocomplete
        /// </summary>
        public async ValueTask<CompleteResult> CompleteAsync(
            RequestContext<CompleteRequestParams> context,
            CancellationToken cancellationToken)
        {
            await EnsureConnectedAsync();
            
            try
            {
                var response = await SendToUnityAsync(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "completion/complete",
                    ["params"] = JObject.FromObject(new
                    {
                        @ref = context.Params?.Ref,
                        argument = context.Params?.Argument
                    })
                });
                
                if (response?["result"]?["completion"] != null)
                {
                    var completion = response["result"]["completion"];
                    return new CompleteResult
                    {
                        Completion = new Completion
                        {
                            Values = completion["values"]?.ToObject<List<string>>() ?? new List<string>(),
                            Total = completion["total"]?.Value<int>() ?? 0,
                            HasMore = completion["hasMore"]?.Value<bool>() ?? false
                        }
                    };
                }
                
                return new CompleteResult { Completion = new Completion { Values = new List<string>() } };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting completions from Unity");
                return new CompleteResult { Completion = new Completion { Values = new List<string>() } };
            }
        }
        
        /// <summary>
        /// Handles resource subscription requests
        /// </summary>
        public async ValueTask<EmptyResult> SubscribeToResourceAsync(
            RequestContext<SubscribeRequestParams> context,
            CancellationToken cancellationToken)
        {
            var uri = context.Params?.Uri;
            if (!string.IsNullOrEmpty(uri))
            {
                _resourceSubscriptions.Add(uri);
                
                // Notify Unity about the subscription
                await EnsureConnectedAsync();
                try
                {
                    await SendToUnityAsync(new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "resources/subscribe",
                        ["params"] = new JObject { ["uri"] = uri }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error subscribing to resource {Uri}", uri);
                }
            }
            
            return new EmptyResult();
        }
        
        /// <summary>
        /// Handles resource unsubscription requests
        /// </summary>
        public async ValueTask<EmptyResult> UnsubscribeFromResourceAsync(
            RequestContext<UnsubscribeRequestParams> context,
            CancellationToken cancellationToken)
        {
            var uri = context.Params?.Uri;
            if (!string.IsNullOrEmpty(uri))
            {
                _resourceSubscriptions.Remove(uri);
                
                // Notify Unity about the unsubscription
                await EnsureConnectedAsync();
                try
                {
                    await SendToUnityAsync(new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "resources/unsubscribe",
                        ["params"] = new JObject { ["uri"] = uri }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error unsubscribing from resource {Uri}", uri);
                }
            }
            
            return new EmptyResult();
        }
        
        /// <summary>
        /// Handles logging level change requests
        /// </summary>
        public async ValueTask<EmptyResult> SetLoggingLevelAsync(
            RequestContext<SetLevelRequestParams> context,
            CancellationToken cancellationToken)
        {
            if (context.Params?.Level != null)
            {
                _currentLoggingLevel = context.Params.Level;
                
                // Send logging level to Unity
                await EnsureConnectedAsync();
                try
                {
                    await SendToUnityAsync(new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["method"] = "logging/setLevel",
                        ["params"] = new JObject { ["level"] = _currentLoggingLevel.ToString().ToLowerInvariant() }
                    });
                    
                    // Send confirmation via logging notification
                    if (_mcpServer != null)
                    {
                        await _mcpServer.SendNotificationAsync(
                            NotificationMethods.LoggingMessageNotification,
                            new LoggingMessageNotificationParams
                            {
                                Level = LoggingLevel.Info,
                                Logger = "unity-mcp",
                                Data = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize($"Logging level set to {_currentLoggingLevel}")).RootElement
                            }
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error setting logging level");
                }
            }
            
            return new EmptyResult();
        }
        
        /// <summary>
        /// Send progress notification to client
        /// </summary>
        public async Task SendProgressNotificationAsync(string progressToken, float progress, float? total = null, string? message = null)
        {
            if (_mcpServer != null)
            {
                await _mcpServer.SendNotificationAsync(
                    NotificationMethods.ProgressNotification,
                    new ProgressNotificationParams
                    {
                        ProgressToken = new ProgressToken(progressToken),
                        Progress = new ProgressNotificationValue
                        {
                            Progress = progress,
                            Total = total,
                            Message = message
                        }
                    }
                );
            }
        }
        
        /// <summary>
        /// Send logging notification to client
        /// </summary>
        public async Task SendLoggingNotificationAsync(LoggingLevel level, string message, string? logger = null)
        {
            if (_mcpServer != null && level >= _currentLoggingLevel)
            {
                await _mcpServer.SendNotificationAsync(
                    NotificationMethods.LoggingMessageNotification,
                    new LoggingMessageNotificationParams
                    {
                        Level = level,
                        Logger = logger ?? "unity-mcp",
                        Data = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(message)).RootElement
                    }
                );
            }
        }
        
        /// <summary>
        /// Send resource updated notification for subscribed resources
        /// </summary>
        public async Task SendResourceUpdatedNotificationAsync(string uri, JObject contents)
        {
            if (_mcpServer != null && _resourceSubscriptions.Contains(uri))
            {
                await _mcpServer.SendNotificationAsync(
                    NotificationMethods.ResourceUpdatedNotification,
                    new ResourceUpdatedNotificationParams
                    {
                        Uri = uri,
                        // Note: ResourceUpdatedNotificationParams doesn't have Contents property in the SDK
                        // This is just a notification that the resource changed
                    }
                );
            }
        }
        
        // Helper methods
        private List<PromptArgument>? ParsePromptArguments(JArray? arguments)
        {
            if (arguments == null) return null;
            
            var result = new List<PromptArgument>();
            foreach (var arg in arguments)
            {
                result.Add(new PromptArgument
                {
                    Name = arg["name"]?.ToString() ?? "",
                    Description = arg["description"]?.ToString(),
                    Required = arg["required"]?.Value<bool>() ?? false
                });
            }
            return result;
        }
        
        private Role ParseRole(string role)
        {
            return role?.ToLowerInvariant() switch
            {
                "assistant" => Role.Assistant,
                "user" => Role.User,
                _ => Role.User
            };
        }
        
        private ContentBlock ParseContent(JToken? content)
        {
            if (content == null) return new TextContentBlock { Text = "" };
            
            if (content.Type == JTokenType.String)
            {
                return new TextContentBlock { Text = content.ToString() };
            }
            
            // Handle complex content types if needed
            return new TextContentBlock { Text = content.ToString() };
        }
    }
}