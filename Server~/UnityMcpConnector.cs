using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace McpUnity.DirectMcp
{
    /// <summary>
    /// MCP to Unity WebSocket bridge - connects to running Unity Editor
    /// Replaces Node.js implementation with pure C#
    /// </summary>
    public class UnityMcpConnector
    {
        private static ClientWebSocket _webSocket;
        private static readonly Dictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
        private static int _requestId = 0;
        private static readonly SemaphoreSlim _sendLock = new(1, 1);

        static async Task<int> Main(string[] args)
        {
            // Project path is optional - we'll try to find Unity automatically
            string projectPath = args.Length > 0 ? args[0] : FindUnityProjectPath();

            try
            {
                // Connect to Unity WebSocket server
                await ConnectToUnity();

                // Start receive loop in background
                var receiveTask = Task.Run(ReceiveLoop);

                // Relay stdin to WebSocket
                await RelayStdinToWebSocket();

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        static string FindUnityProjectPath()
        {
            // Try to find Unity project path from current executable location
            var exePath = AppContext.BaseDirectory;
            var searchPath = exePath;
            
            // Walk up directories looking for Unity project markers
            for (int i = 0; i < 10; i++)
            {
                if (File.Exists(Path.Combine(searchPath, "Assets", "mcp-unity", "package.json")) ||
                    Directory.Exists(Path.Combine(searchPath, "ProjectSettings")))
                {
                    return searchPath;
                }
                
                var parent = Directory.GetParent(searchPath);
                if (parent == null) break;
                searchPath = parent.FullName;
            }
            
            // Default to a known location
            return @"Z:\SOURCE\Ziltch\___\src\Ziltch\Ziltch.Unity";
        }

        static async Task ConnectToUnity()
        {
            int port = 8090; // Default Unity MCP port
            
            // Try to read port from settings if available
            // Use AppContext.BaseDirectory for single-file apps
            var basePath = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(basePath))
            {
                basePath = Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
            }
            
            var settingsPath = Path.Combine(
                basePath,
                "..", "..", "..", "..", "..", "ProjectSettings", "McpUnitySettings.json");
            
            // Also try the default Unity project location
            var altSettingsPath = @"Z:\SOURCE\Ziltch\___\src\Ziltch\Ziltch.Unity\ProjectSettings\McpUnitySettings.json";
            
            if (File.Exists(settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(settingsPath);
                    var settings = JObject.Parse(json);
                    if (settings["Port"] != null)
                    {
                        port = settings["Port"].Value<int>();
                    }
                }
                catch { }
            }
            else if (File.Exists(altSettingsPath))
            {
                try
                {
                    var json = File.ReadAllText(altSettingsPath);
                    var settings = JObject.Parse(json);
                    if (settings["Port"] != null)
                    {
                        port = settings["Port"].Value<int>();
                    }
                }
                catch { }
            }

            _webSocket = new ClientWebSocket();
            var uri = new Uri($"ws://localhost:{port}/McpUnity");
            
            Console.Error.WriteLine($"Attempting to connect to Unity WebSocket at {uri}...");
            
            try
            {
                await _webSocket.ConnectAsync(uri, CancellationToken.None);
                Console.Error.WriteLine($"Successfully connected to Unity WebSocket at {uri}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to connect to Unity WebSocket at {uri}");
                Console.Error.WriteLine($"Make sure Unity Editor is running with the MCP Unity server enabled.");
                Console.Error.WriteLine($"In Unity: Window > McpUnity > Server Control");
                throw;
            }
        }

        static async Task RelayStdinToWebSocket()
        {
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            string line;
            
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    // Parse JSON-RPC request
                    var root = JObject.Parse(line);
                    
                    // Extract method and params
                    string method = root["method"]?.ToString();
                    var id = root["id"];
                    
                    // Transform MCP method to Unity WebSocket format
                    var unityMethod = TransformMethod(method);
                    
                    // Create Unity request
                    var unityRequest = new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["method"] = unityMethod
                    };
                    
                    if (root["params"] != null)
                    {
                        unityRequest["params"] = root["params"];
                    }

                    var requestJson = unityRequest.ToString(Formatting.None);
                    var requestId = id?.ToString();
                    
                    // Track pending request
                    var tcs = new TaskCompletionSource<string>();
                    _pendingRequests[requestId] = tcs;

                    // Send to Unity
                    await _sendLock.WaitAsync();
                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(requestJson);
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

                    try
                    {
                        var response = await tcs.Task;
                        Console.WriteLine(response);
                    }
                    catch (TaskCanceledException)
                    {
                        // Send timeout error response
                        var errorResponse = new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = JToken.Parse(requestId ?? "null"),
                            ["error"] = new JObject
                            {
                                ["code"] = -32000,
                                ["message"] = "Request timeout"
                            }
                        };
                        Console.WriteLine(errorResponse.ToString(Formatting.None));
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error processing request: {ex.Message}");
                }
            }
        }

        static async Task ReceiveLoop()
        {
            var buffer = new ArraySegment<byte>(new byte[4096]);
            var messageBuilder = new List<byte>();

            while (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    messageBuilder.Clear();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _webSocket.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            messageBuilder.AddRange(buffer.Array.Take(result.Count));
                        }
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(messageBuilder.ToArray());
                        HandleResponse(json);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Receive error: {ex.Message}");
                    break;
                }
            }
        }

        static void HandleResponse(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                
                if (root["id"] != null)
                {
                    var id = root["id"].ToString();
                    if (_pendingRequests.TryGetValue(id, out var tcs))
                    {
                        _pendingRequests.Remove(id);
                        
                        // Forward the response as-is
                        tcs.SetResult(json);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error handling response: {ex.Message}");
            }
        }

        static string TransformMethod(string mcpMethod)
        {
            // Transform MCP methods to Unity WebSocket methods
            return mcpMethod switch
            {
                "initialize" => "initialize",
                "tools/list" => "tools/list",
                "tools/call" => "tool/execute",
                "resources/list" => "resources/list",
                "resources/read" => "resource/read",
                _ => mcpMethod
            };
        }
    }
}