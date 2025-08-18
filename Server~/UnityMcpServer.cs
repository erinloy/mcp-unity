using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace McpUnity.DirectMcp
{
    /// <summary>
    /// Full MCP server implementation that bridges to Unity via WebSocket
    /// Replaces Node.js server with pure C# implementation
    /// </summary>
    public class UnityMcpServer
    {
        private static ClientWebSocket _webSocket;
        private static readonly Dictionary<string, TaskCompletionSource<JObject>> _pendingRequests = new();
        private static int _requestIdCounter = 0;
        private static readonly SemaphoreSlim _sendLock = new(1, 1);
        private static CancellationTokenSource _shutdownCts = new();
        private static string _projectPath;
        private static int _unityPort = 8090;

        // MCP protocol constants
        private const string PROTOCOL_VERSION = "2024-11-05";
        private const string SERVER_NAME = "unity-mcp";
        private const string SERVER_VERSION = "1.0.0";

        static async Task<int> Main(string[] args)
        {
            try
            {
                Console.Error.WriteLine($"[UnityMCP] Starting Unity MCP Server v{SERVER_VERSION}");
                
                // Auto-detect Unity project path
                _projectPath = FindUnityProjectPath();
                Console.Error.WriteLine($"[UnityMCP] Unity project: {_projectPath}");
                
                // Load Unity port from settings
                LoadUnitySettings();
                
                // Connect to Unity WebSocket
                await ConnectToUnity();
                
                // Start background receive loop
                var receiveTask = Task.Run(ReceiveLoop);
                
                // Process MCP protocol on stdin/stdout
                await ProcessMcpProtocol();
                
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UnityMCP] Fatal error: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
            finally
            {
                _shutdownCts?.Cancel();
                if (_webSocket?.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutting down", CancellationToken.None);
                }
            }
        }

        static string FindUnityProjectPath()
        {
            // Start from executable location and search upward for Unity project markers
            var searchPath = AppContext.BaseDirectory;
            
            for (int i = 0; i < 10; i++)
            {
                // Check for Unity project markers
                if (Directory.Exists(Path.Combine(searchPath, "Assets")) &&
                    Directory.Exists(Path.Combine(searchPath, "ProjectSettings")))
                {
                    return searchPath;
                }
                
                // Also check if we're inside an Assets folder structure
                if (Path.GetFileName(searchPath) == "DirectMcp~" ||
                    searchPath.Contains(@"Assets\mcp-unity"))
                {
                    // Navigate up to project root
                    var testPath = searchPath;
                    while (!string.IsNullOrEmpty(testPath))
                    {
                        if (Directory.Exists(Path.Combine(testPath, "ProjectSettings")))
                        {
                            return testPath;
                        }
                        var parent = Directory.GetParent(testPath);
                        if (parent == null) break;
                        testPath = parent.FullName;
                    }
                }
                
                var parentDir = Directory.GetParent(searchPath);
                if (parentDir == null) break;
                searchPath = parentDir.FullName;
            }
            
            // Try common Unity project locations
            var commonPaths = new[]
            {
                @"Z:\SOURCE\Ziltch\___\src\Ziltch\Ziltch.Unity",
                Environment.CurrentDirectory,
                Path.Combine(Environment.CurrentDirectory, "..", "..", "..", "..", "..")
            };
            
            foreach (var path in commonPaths)
            {
                var fullPath = Path.GetFullPath(path);
                if (Directory.Exists(Path.Combine(fullPath, "Assets")) &&
                    Directory.Exists(Path.Combine(fullPath, "ProjectSettings")))
                {
                    return fullPath;
                }
            }
            
            throw new Exception("Could not auto-detect Unity project path. Ensure this is run from within a Unity project structure.");
        }

        static void LoadUnitySettings()
        {
            var settingsPath = Path.Combine(_projectPath, "ProjectSettings", "McpUnitySettings.json");
            
            if (File.Exists(settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(settingsPath);
                    var settings = JObject.Parse(json);
                    if (settings["Port"] != null)
                    {
                        _unityPort = settings["Port"].Value<int>();
                    }
                    Console.Error.WriteLine($"[UnityMCP] Loaded settings from {settingsPath}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[UnityMCP] Warning: Could not load settings: {ex.Message}");
                }
            }
        }

        static async Task ConnectToUnity()
        {
            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            
            var uri = new Uri($"ws://localhost:{_unityPort}/McpUnity");
            
            Console.Error.WriteLine($"[UnityMCP] Connecting to Unity WebSocket at {uri}...");
            
            int retryCount = 0;
            while (retryCount < 5)
            {
                try
                {
                    await _webSocket.ConnectAsync(uri, CancellationToken.None);
                    Console.Error.WriteLine($"[UnityMCP] Successfully connected to Unity");
                    return;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    Console.Error.WriteLine($"[UnityMCP] Connection attempt {retryCount} failed: {ex.Message}");
                    
                    if (retryCount >= 5)
                    {
                        Console.Error.WriteLine("[UnityMCP] Failed to connect after 5 attempts.");
                        Console.Error.WriteLine("[UnityMCP] Ensure Unity Editor is running with MCP Unity server enabled.");
                        Console.Error.WriteLine("[UnityMCP] In Unity: Tools → MCP Unity → Server Window → Start Server");
                        throw;
                    }
                    
                    await Task.Delay(2000); // Wait 2 seconds before retry
                }
            }
        }

        static async Task ProcessMcpProtocol()
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            string line;
            
            while ((line = await reader.ReadLineAsync()) != null && !_shutdownCts.Token.IsCancellationRequested)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                
                try
                {
                    var request = JObject.Parse(line);
                    var response = await HandleMcpRequest(request);
                    
                    if (response != null)
                    {
                        Console.WriteLine(response.ToString(Formatting.None));
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[UnityMCP] Error processing request: {ex.Message}");
                    
                    // Send error response
                    var errorResponse = new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["error"] = new JObject
                        {
                            ["code"] = -32603,
                            ["message"] = ex.Message
                        }
                    };
                    Console.WriteLine(errorResponse.ToString(Formatting.None));
                }
            }
        }

        static async Task<JObject> HandleMcpRequest(JObject request)
        {
            var method = request["method"]?.ToString();
            var id = request["id"];
            var parameters = request["params"] as JObject;
            
            Console.Error.WriteLine($"[UnityMCP] Handling MCP request: {method}");
            
            switch (method)
            {
                case "initialize":
                    return HandleInitialize(id, parameters);
                    
                case "notifications/initialized":
                    // No response needed for notifications
                    Console.Error.WriteLine("[UnityMCP] Client initialized");
                    return null;
                    
                case "tools/list":
                    return await HandleToolsList(id);
                    
                case "tools/call":
                    return await HandleToolCall(id, parameters);
                    
                case "resources/list":
                    return await HandleResourcesList(id);
                    
                case "resources/read":
                    return await HandleResourceRead(id, parameters);
                    
                case "prompts/list":
                    return HandlePromptsList(id);
                    
                case "prompts/get":
                    return HandlePromptGet(id, parameters);
                    
                case "completion/complete":
                    return HandleCompletion(id, parameters);
                    
                default:
                    // Try to forward unknown methods to Unity
                    return await ForwardToUnity(id, method, parameters);
            }
        }

        static JObject HandleInitialize(JToken id, JObject parameters)
        {
            var clientInfo = parameters?["clientInfo"] as JObject;
            Console.Error.WriteLine($"[UnityMCP] Client: {clientInfo?["name"]} v{clientInfo?["version"]}");
            
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JObject
                {
                    ["protocolVersion"] = PROTOCOL_VERSION,
                    ["serverInfo"] = new JObject
                    {
                        ["name"] = SERVER_NAME,
                        ["version"] = SERVER_VERSION
                    },
                    ["capabilities"] = new JObject
                    {
                        ["tools"] = new JObject(),
                        ["resources"] = new JObject
                        {
                            ["subscribe"] = false,
                            ["list"] = true
                        },
                        ["prompts"] = new JObject
                        {
                            ["list"] = true
                        },
                        ["logging"] = new JObject()
                    }
                }
            };
        }

        static async Task<JObject> HandleToolsList(JToken id)
        {
            // Request tools list from Unity
            var unityResponse = await SendToUnity("tools/list", null);
            
            // Transform Unity response to MCP format
            var tools = new JArray();
            
            if (unityResponse?["result"]?["tools"] is JArray unityTools)
            {
                foreach (var tool in unityTools)
                {
                    tools.Add(new JObject
                    {
                        ["name"] = tool["name"],
                        ["description"] = tool["description"],
                        ["inputSchema"] = tool["parameters"] ?? new JObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JObject()
                        }
                    });
                }
            }
            else
            {
                // Provide default tools if Unity doesn't respond
                tools = GetDefaultTools();
            }
            
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JObject
                {
                    ["tools"] = tools
                }
            };
        }

        static async Task<JObject> HandleToolCall(JToken id, JObject parameters)
        {
            var toolName = parameters?["name"]?.ToString();
            var toolArgs = parameters?["arguments"] as JObject ?? new JObject();
            
            Console.Error.WriteLine($"[UnityMCP] Calling tool: {toolName}");
            
            // Send tool execution request to Unity
            var unityResponse = await SendToUnity(toolName, toolArgs);
            
            if (unityResponse?["error"] != null)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = unityResponse["error"]
                };
            }
            
            // Format response based on tool type
            var content = new JArray();
            var result = unityResponse?["result"];
            
            if (result?["type"]?.ToString() == "image" && result["data"] != null)
            {
                // Image content
                content.Add(new JObject
                {
                    ["type"] = "image",
                    ["data"] = result["data"],
                    ["mimeType"] = result["mimeType"] ?? "image/png"
                });
            }
            else if (result?["text"] != null)
            {
                // Text content
                content.Add(new JObject
                {
                    ["type"] = "text",
                    ["text"] = result["text"]
                });
            }
            else if (result?["content"] is JArray)
            {
                // Already formatted content
                content = result["content"] as JArray;
            }
            else
            {
                // Default text response
                content.Add(new JObject
                {
                    ["type"] = "text",
                    ["text"] = result?.ToString() ?? "Tool executed successfully"
                });
            }
            
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JObject
                {
                    ["content"] = content,
                    ["isError"] = false
                }
            };
        }

        static async Task<JObject> HandleResourcesList(JToken id)
        {
            var unityResponse = await SendToUnity("resources/list", null);
            
            var resources = new JArray();
            
            if (unityResponse?["result"]?["resources"] is JArray unityResources)
            {
                foreach (var resource in unityResources)
                {
                    resources.Add(new JObject
                    {
                        ["uri"] = resource["uri"],
                        ["name"] = resource["name"],
                        ["description"] = resource["description"],
                        ["mimeType"] = resource["mimeType"] ?? "application/json"
                    });
                }
            }
            
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JObject
                {
                    ["resources"] = resources
                }
            };
        }

        static async Task<JObject> HandleResourceRead(JToken id, JObject parameters)
        {
            var uri = parameters?["uri"]?.ToString();
            
            var unityResponse = await SendToUnity("resource/read", new JObject { ["uri"] = uri });
            
            if (unityResponse?["error"] != null)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = unityResponse["error"]
                };
            }
            
            var contents = new JArray();
            var result = unityResponse?["result"];
            
            if (result?["content"] != null)
            {
                contents.Add(new JObject
                {
                    ["uri"] = uri,
                    ["mimeType"] = result["mimeType"] ?? "application/json",
                    ["text"] = result["content"].ToString()
                });
            }
            
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JObject
                {
                    ["contents"] = contents
                }
            };
        }

        static JObject HandlePromptsList(JToken id)
        {
            var prompts = new JArray
            {
                new JObject
                {
                    ["name"] = "create-gameobject",
                    ["description"] = "Create a new GameObject with components"
                },
                new JObject
                {
                    ["name"] = "modify-scene",
                    ["description"] = "Modify the current Unity scene"
                }
            };
            
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JObject
                {
                    ["prompts"] = prompts
                }
            };
        }

        static JObject HandlePromptGet(JToken id, JObject parameters)
        {
            var name = parameters?["name"]?.ToString();
            
            var messages = new JArray();
            
            switch (name)
            {
                case "create-gameobject":
                    messages.Add(new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JObject
                        {
                            ["type"] = "text",
                            ["text"] = "Create a GameObject with the specified components and properties"
                        }
                    });
                    break;
                    
                case "modify-scene":
                    messages.Add(new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JObject
                        {
                            ["type"] = "text",
                            ["text"] = "Modify the Unity scene based on the requirements"
                        }
                    });
                    break;
            }
            
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JObject
                {
                    ["description"] = $"Prompt for {name}",
                    ["messages"] = messages
                }
            };
        }

        static JObject HandleCompletion(JToken id, JObject parameters)
        {
            // Basic completion support
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JObject
                {
                    ["completion"] = new JObject
                    {
                        ["values"] = new JArray()
                    }
                }
            };
        }

        static async Task<JObject> ForwardToUnity(JToken id, string method, JObject parameters)
        {
            Console.Error.WriteLine($"[UnityMCP] Forwarding unknown method to Unity: {method}");
            var response = await SendToUnity(method, parameters);
            
            if (response != null)
            {
                response["id"] = id;
                return response;
            }
            
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JObject
                {
                    ["code"] = -32601,
                    ["message"] = $"Method not found: {method}"
                }
            };
        }

        static async Task<JObject> SendToUnity(string method, JObject parameters)
        {
            if (_webSocket?.State != WebSocketState.Open)
            {
                Console.Error.WriteLine("[UnityMCP] WebSocket not connected");
                return null;
            }
            
            var requestId = Interlocked.Increment(ref _requestIdCounter).ToString();
            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = method
            };
            
            if (parameters != null)
            {
                request["params"] = parameters;
            }
            
            var tcs = new TaskCompletionSource<JObject>();
            _pendingRequests[requestId] = tcs;
            
            try
            {
                await _sendLock.WaitAsync();
                try
                {
                    var json = request.ToString(Formatting.None);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
                finally
                {
                    _sendLock.Release();
                }
                
                // Wait for response with timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                cts.Token.Register(() => tcs.TrySetCanceled());
                
                return await tcs.Task;
            }
            catch (TaskCanceledException)
            {
                Console.Error.WriteLine($"[UnityMCP] Request timeout for method: {method}");
                _pendingRequests.Remove(requestId);
                return null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UnityMCP] Error sending to Unity: {ex.Message}");
                _pendingRequests.Remove(requestId);
                return null;
            }
        }

        static async Task ReceiveLoop()
        {
            var buffer = new ArraySegment<byte>(new byte[8192]);
            var messageBuilder = new List<byte>();
            
            while (_webSocket?.State == WebSocketState.Open && !_shutdownCts.Token.IsCancellationRequested)
            {
                try
                {
                    messageBuilder.Clear();
                    WebSocketReceiveResult result;
                    
                    do
                    {
                        result = await _webSocket.ReceiveAsync(buffer, _shutdownCts.Token);
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            messageBuilder.AddRange(buffer.Array.Take(result.Count));
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Console.Error.WriteLine("[UnityMCP] Unity WebSocket closed connection");
                            return;
                        }
                    } while (!result.EndOfMessage);
                    
                    if (messageBuilder.Count > 0)
                    {
                        var json = Encoding.UTF8.GetString(messageBuilder.ToArray());
                        HandleUnityResponse(json);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[UnityMCP] Receive error: {ex.Message}");
                    break;
                }
            }
        }

        static void HandleUnityResponse(string json)
        {
            try
            {
                // Log raw data from Unity for debugging
                try
                {
                    var rawLogPath = @"C:\temp\mcp-notifications-raw.log";
                    var logDir = Path.GetDirectoryName(rawLogPath);
                    if (!Directory.Exists(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }
                    File.AppendAllText(rawLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] RAW FROM UNITY:\n{json}\n\n");
                }
                catch { /* Ignore logging errors */ }
                
                var response = JObject.Parse(json);
                var id = response["id"]?.ToString();
                
                // Check if this is a notification (no id field) or a response (has id field)
                if (string.IsNullOrEmpty(id))
                {
                    // This is a notification from Unity - forward it upstream to Claude
                    var method = response["method"]?.ToString();
                    if (!string.IsNullOrEmpty(method))
                    {
                        Console.Error.WriteLine($"[UnityMCP] Forwarding notification: {method}");
                        
                        // Ensure the notification is properly formatted for MCP
                        // Remove any 'id' field and ensure jsonrpc is present
                        var notification = new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["method"] = method
                        };
                        
                        // Copy params if present
                        if (response["params"] != null)
                        {
                            notification["params"] = response["params"];
                        }
                        
                        // Write notification to stdout for MCP client (Claude)
                        var notificationJson = notification.ToString(Formatting.None);
                        Console.WriteLine(notificationJson);
                        Console.Out.Flush();
                        
                        // Also log to file for debugging
                        try
                        {
                            var logPath = @"C:\temp\mcp-notifications.log";
                            var logDir = Path.GetDirectoryName(logPath);
                            if (!Directory.Exists(logDir))
                            {
                                Directory.CreateDirectory(logDir);
                            }
                            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {notificationJson}\n");
                        }
                        catch (Exception logEx)
                        {
                            Console.Error.WriteLine($"[UnityMCP] Failed to log notification: {logEx.Message}");
                        }
                        
                        Console.Error.WriteLine($"[UnityMCP] Sent notification: {notificationJson}");
                    }
                }
                else if (_pendingRequests.TryGetValue(id, out var tcs))
                {
                    // This is a response to our request
                    _pendingRequests.Remove(id);
                    tcs.SetResult(response);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UnityMCP] Error handling Unity message: {ex.Message}");
            }
        }

        static JArray GetDefaultTools()
        {
            return new JArray
            {
                new JObject
                {
                    ["name"] = "send_console_log",
                    ["description"] = "Send a log message to Unity console",
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["message"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "Message to log"
                            },
                            ["type"] = new JObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JArray { "info", "warning", "error" },
                                ["description"] = "Log level"
                            }
                        },
                        ["required"] = new JArray { "message" }
                    }
                },
                new JObject
                {
                    ["name"] = "execute_menu_item",
                    ["description"] = "Execute a Unity menu item",
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["menuPath"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "Menu path (e.g., 'GameObject/Create Empty')"
                            }
                        },
                        ["required"] = new JArray { "menuPath" }
                    }
                },
                new JObject
                {
                    ["name"] = "capture_screenshot",
                    ["description"] = "Capture a screenshot of Unity editor",
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["viewType"] = new JObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JArray { "game", "scene", "inspector" },
                                ["description"] = "Type of view to capture"
                            }
                        }
                    }
                },
                new JObject
                {
                    ["name"] = "select_gameobject",
                    ["description"] = "Select a GameObject in the scene",
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["path"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "GameObject path or name"
                            }
                        },
                        ["required"] = new JArray { "path" }
                    }
                },
                new JObject
                {
                    ["name"] = "get_console_logs",
                    ["description"] = "Get recent Unity console logs",
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["count"] = new JObject
                            {
                                ["type"] = "integer",
                                ["description"] = "Number of logs to retrieve"
                            }
                        }
                    }
                }
            };
        }
    }
}