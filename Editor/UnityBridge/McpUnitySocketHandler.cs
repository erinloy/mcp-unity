using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebSocketSharp;
using WebSocketSharp.Server;
using McpUnity.Tools;
using McpUnity.Resources;
using McpUnity.Discovery;
using Unity.EditorCoroutines.Editor;
using System.Collections;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using McpUnity.Utils;

namespace McpUnity.Unity
{
    /// <summary>
    /// Drains work queued from background WebSocket threads on the Unity main thread via
    /// EditorApplication.update, which keeps firing even when the Editor is unfocused.
    ///
    /// This replaces dispatching through EditorApplication.delayCall: delayCall is a plain
    /// static delegate, and a "+=" performed from the WebSocketSharp background thread is not
    /// reliably observed/drained by the main thread while the Editor is idle in the
    /// background. The result was that requests received while Unity was not the foreground
    /// app were never processed, and the MCP client timed out. Draining a thread-safe queue
    /// from EditorApplication.update fixes this without relying on cross-thread delegate
    /// mutation.
    /// </summary>
    [InitializeOnLoad]
    internal static class McpMainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        static McpMainThreadDispatcher()
        {
            EditorApplication.update -= Drain;
            EditorApplication.update += Drain;
        }

        public static void Enqueue(Action action)
        {
            if (action != null) _queue.Enqueue(action);
        }

        private static void Drain()
        {
            while (_queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { McpLogger.LogError($"MainThreadDispatcher action failed: {ex}"); }
            }
        }
    }

    /// <summary>
    /// WebSocket handler for MCP Unity communications.
    /// Speaks both the legacy per-method protocol (method = tool/resource name) and the
    /// MCP-style protocol used by the C# bridge server (tools/list, tools/call, resources/list,
    /// resources/read) plus OpenRPC discovery methods.
    /// </summary>
    public class McpUnitySocketHandler : WebSocketBehavior
    {
        private const string DefaultClientName = "MCP Client";

        private readonly McpUnityServer _server;
        private readonly int _connectionGeneration;

        /// <summary>
        /// Creates a WebSocket handler for the active server generation.
        /// </summary>
        public McpUnitySocketHandler(McpUnityServer server, int connectionGeneration)
        {
            _server = server;
            _connectionGeneration = connectionGeneration;

            // Native bridge clients do not send Origin. Browsers always do, and must never be
            // allowed to drive the Editor through a cross-site WebSocket connection.
            OriginValidator = origin => origin == null;
        }

        /// <summary>
        /// Create a standardized error response
        /// </summary>
        /// <param name="message">Error message</param>
        /// <param name="errorType">Type of error</param>
        /// <returns>A JObject containing the error information</returns>
        public static JObject CreateErrorResponse(string message, string errorType)
        {
            return new JObject
            {
                ["error"] = new JObject
                {
                    ["type"] = errorType,
                    ["message"] = message
                }
            };
        }

        /// <summary>
        /// Handle incoming messages from WebSocket clients.
        /// WebSocketSharp invokes this on a background thread; the entire message-handling body
        /// is marshalled onto Unity's main thread before touching any Editor APIs.
        ///
        /// Why this matters: accessing EditorStyles or scheduling EditorCoroutines from
        /// a background thread can NRE inside PropertyEditor+Styles..cctor, which under
        /// CLR rules permanently bricks that type for the rest of the AppDomain and
        /// turns the Inspector black until Unity is restarted.
        /// </summary>
        protected override void OnMessage(MessageEventArgs e)
        {
            if (!_server.ShouldTrackClient(_connectionGeneration))
            {
                CloseUntrackedConnection();
                return;
            }

            string data = e.Data;
            // Dispatch via a thread-safe queue drained in EditorApplication.update rather than
            // EditorApplication.delayCall. A delayCall "+=" from this background thread is not
            // reliably drained by the main thread while the Editor is unfocused/idle, so the
            // request would never run and the client would time out. See McpMainThreadDispatcher.
            McpMainThreadDispatcher.Enqueue(() => HandleMessageAsync(data));
        }

        /// <summary>
        /// Handle WebSocket connection open.
        /// Supports multiple concurrent MCP clients (e.g. multiple Claude Code instances).
        /// Cleans up only inactive (dead) sessions to prevent file descriptor accumulation
        /// while keeping other active clients connected. This also covers stale connections left
        /// behind by sleep/resume of the client machine.
        /// websocket-sharp uses Mono's IOSelector/select(), which can crash when FD
        /// values exceed ~1024, so stale session cleanup is important.
        /// See: https://github.com/CoderGamester/mcp-unity/issues/110
        /// </summary>
        protected override void OnOpen()
        {
            if (!_server.ShouldTrackClient(_connectionGeneration))
            {
                CloseUntrackedConnection();
                return;
            }

            // Clean up inactive (dead) sessions to prevent file descriptor accumulation.
            // Only removes sessions that are no longer connected — active clients are preserved.
            // Note: Do NOT use ActiveIDs here — it pings every client and blocks.
            var inactiveIds = Sessions.InactiveIDs.ToList();
            if (inactiveIds.Count > 0)
            {
                foreach (var oldId in inactiveIds)
                {
                    // Also remove from our tracking dictionary
                    _server.Clients.TryRemove(oldId, out _);
                    try
                    {
                        Sessions.CloseSession(oldId, CloseStatusCode.Normal, "Stale session cleanup");
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogWarning($"Error closing stale session {oldId}: {ex.Message}");
                    }
                }
                McpLogger.LogInfo($"Cleaned up {inactiveIds.Count} inactive session(s)");
            }

            // Extract client name from the X-Client-Name header (if available)
            string clientName = DefaultClientName;
            NameValueCollection headers = Context.Headers;
            if (headers != null && headers.Contains("X-Client-Name") && !string.IsNullOrEmpty(headers["X-Client-Name"]))
            {
                clientName = headers["X-Client-Name"];
            }

            if (!_server.ShouldTrackClient(_connectionGeneration))
            {
                CloseUntrackedConnection();
                return;
            }

            // Add the client to the server's tracking dictionary
            _server.Clients[ID] = clientName;

            // Notify server of successful connection (for health tracking)
            _server.OnClientConnected(ID);

            McpLogger.LogInfo($"WebSocket client '{clientName}' connected (ID: {ID}, Total clients: {_server.Clients.Count})");
        }

        /// <summary>
        /// Handle WebSocket connection close
        /// </summary>
        protected override void OnClose(CloseEventArgs e)
        {
            _server.Clients.TryGetValue(ID, out string clientName);

            // Remove the client from the server
            _server.Clients.TryRemove(ID, out _);

            string reason = e.Reason;
            if (reason == "An exception has occurred while receiving.")
            {
                reason = "connection closed by client";
            }

            McpLogger.LogInfo($"WebSocket client '{clientName}' disconnected: {reason} (Remaining clients: {_server.Clients.Count})");
        }

        /// <summary>
        /// Handle WebSocket errors
        /// </summary>
        protected override void OnError(ErrorEventArgs e)
        {
            McpLogger.LogError($"WebSocket error: {e.Message}");
        }

        /// <summary>
        /// Process a WebSocket message on the Unity main thread.
        /// Safe to call EditorCoroutineUtility, Selection, and other Editor APIs from here.
        /// </summary>
        private async void HandleMessageAsync(string data)
        {
            try
            {
                if (!_server.ShouldTrackClient(_connectionGeneration))
                {
                    CloseUntrackedConnection();
                    return;
                }

                // Routine discovery polling is only logged when verbose logging is enabled
                bool isRoutineMessage = data.Contains("tools/list") || data.Contains("resources/list");
                if (McpUnitySettings.Instance.VerboseLogging || !isRoutineMessage)
                {
                    McpLogger.LogInfo($"WebSocket message received: {data}");
                }

                JObject requestJson;
                try
                {
                    requestJson = JObject.Parse(data);
                }
                catch (JsonReaderException jre)
                {
                    McpLogger.LogError($"Invalid JSON received: {jre.Message}. Data: {data}");
                    // Attempt to send a parse error response. No requestId is available yet.
                    Send(CreateResponse(null, CreateErrorResponse($"Invalid JSON format: {jre.Message}", "invalid_json")).ToString(Formatting.None));
                    return;
                }

                var method = requestJson["method"]?.ToString();
                var parameters = requestJson["params"] as JObject ?? new JObject();
                var requestId = requestJson["id"]?.ToString();
                var tcs = new TaskCompletionSource<JObject>();

                DispatchRequest(method, parameters, tcs);

                JObject responseJson = await tcs.Task;
                JObject jsonRpcResponse = CreateResponse(requestId, responseJson);
                string responseStr = jsonRpcResponse.ToString(Formatting.None);

                // Log based on verbose logging setting and message type
                bool isRoutineRequest = method == "tools/list" || method == "resources/list" || method == "resource/list";
                bool shouldLog = McpUnitySettings.Instance.VerboseLogging ||
                                 responseJson.ContainsKey("error") ||
                                 !isRoutineRequest;

                if (shouldLog)
                {
                    McpLogger.LogInfo($"WebSocket message response for request ID '{requestId}': {responseStr}");
                }

                // Send the response back to the client
                Send(responseStr);
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error processing message: {ex.Message}");

                Send(CreateErrorResponse($"Internal server error: {ex.Message}", "internal_error").ToString(Formatting.None));
            }
        }

        /// <summary>
        /// Routes a request to the matching MCP method, discovery method, tool, or resource.
        /// Always completes <paramref name="tcs"/> (directly or via the started coroutine).
        /// </summary>
        private void DispatchRequest(string method, JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            if (string.IsNullOrEmpty(method))
            {
                tcs.SetResult(CreateErrorResponse("Missing method in request", "invalid_request"));
            }
            // Standard MCP protocol methods (used by the C# bridge server)
            else if (method == "tools/list")
            {
                var toolsArray = new JArray(
                    _server.GetTools().Values.Select(tool => new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["inputSchema"] = tool.InputSchema
                    })
                );

                // Return just the result - CreateResponse will wrap it properly
                tcs.SetResult(new JObject
                {
                    ["tools"] = toolsArray
                });
            }
            else if (method == "resources/list" || method == "resource/list") // Support both singular and plural
            {
                var resourcesArray = new JArray(
                    _server.GetResources().Values.Select(resource => new JObject
                    {
                        ["uri"] = resource.Uri,
                        ["name"] = resource.Name,
                        ["description"] = resource.Description,
                        ["mimeType"] = "text/plain"
                    })
                );

                // Return just the result - CreateResponse will wrap it properly
                tcs.SetResult(new JObject
                {
                    ["resources"] = resourcesArray
                });
            }
            else if (method == "resources/read" || method == "resource/read") // Support both singular and plural
            {
                var uri = parameters["uri"]?.ToString();
                if (string.IsNullOrEmpty(uri))
                {
                    tcs.SetResult(CreateErrorResponse("Missing uri parameter", "invalid_params"));
                }
                else if (_server.TryGetResourceByUri(uri, out var resource))
                {
                    // Exact URI match
                    EditorCoroutineUtility.StartCoroutineOwnerless(FetchResourceCoroutine(resource, parameters, tcs));
                }
                else
                {
                    // Try URI template matching (e.g., unity://logs/all matches unity://logs/{logType})
                    var matchedResource = TryMatchResourcePattern(uri, out var templateParams);
                    if (matchedResource != null)
                    {
                        // Merge template parameters into the parameters object
                        var mergedParams = new JObject(parameters);
                        foreach (var kvp in templateParams)
                        {
                            mergedParams[kvp.Key] = kvp.Value;
                        }
                        EditorCoroutineUtility.StartCoroutineOwnerless(FetchResourceCoroutine(matchedResource, mergedParams, tcs));
                    }
                    else
                    {
                        tcs.SetResult(CreateErrorResponse($"Resource not found: {uri}", "resource_not_found"));
                    }
                }
            }
            else if (method == "tools/call")
            {
                var name = parameters["name"]?.ToString();
                var arguments = parameters["arguments"] as JObject ?? new JObject();

                if (string.IsNullOrEmpty(name))
                {
                    tcs.SetResult(CreateErrorResponse("Missing name parameter", "invalid_params"));
                }
                else if (_server.TryGetTool(name, out var tool))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(ExecuteTool(tool, new JObject(arguments), tcs));
                }
                else
                {
                    tcs.SetResult(CreateErrorResponse($"Tool not found: {name}", "tool_not_found"));
                }
            }
            // Discovery methods
            else if (method == "rpc.discover")
            {
                tcs.SetResult(OpenRpcDiscovery.GenerateOpenRpcDocument(
                    _server.GetTools(),
                    _server.GetResources()
                ));
            }
            else if (method == "system.listMethods")
            {
                var methods = new List<string> { "rpc.discover", "system.listMethods", "system.methodSignature", "tools/list", "tools/call", "resources/list", "resources/read" };
                methods.AddRange(_server.GetTools().Keys);
                methods.AddRange(_server.GetResources().Keys);
                tcs.SetResult(new JObject { ["methods"] = new JArray(methods) });
            }
            else if (method == "system.methodSignature")
            {
                var methodName = parameters["methodName"]?.ToString();
                if (string.IsNullOrEmpty(methodName))
                {
                    tcs.SetResult(CreateErrorResponse("Missing methodName parameter", "invalid_params"));
                }
                else
                {
                    var signature = GetMethodSignature(methodName);
                    tcs.SetResult(signature ?? CreateErrorResponse($"Method {methodName} not found", "method_not_found"));
                }
            }
            // Legacy protocol: method is the tool or resource name
            else if (_server.TryGetTool(method, out var namedTool))
            {
                EditorCoroutineUtility.StartCoroutineOwnerless(ExecuteTool(namedTool, parameters, tcs));
            }
            else if (_server.TryGetResource(method, out var namedResource))
            {
                EditorCoroutineUtility.StartCoroutineOwnerless(FetchResourceCoroutine(namedResource, parameters, tcs));
            }
            else
            {
                tcs.SetResult(CreateErrorResponse($"Unknown method: {method}", "unknown_method"));
            }
        }

        private void CloseUntrackedConnection()
        {
            try
            {
                WebSocket webSocket = Context?.WebSocket;
                if (webSocket?.ReadyState == WebSocketState.Open)
                {
                    webSocket.Close(CloseStatusCode.Away, "Server is restarting");
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogWarning($"Error closing untracked WebSocket connection: {ex.Message}");
            }
        }

        /// <summary>
        /// Execute a tool with the provided parameters
        /// </summary>
        private IEnumerator ExecuteTool(McpToolBase tool, JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            try
            {
                if (tool.IsAsync)
                {
                    tool.ExecuteAsync(parameters, tcs);
                }
                else
                {
                    var result = tool.Execute(parameters);
                    tcs.SetResult(result);
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error executing tool {tool.Name}: {ex.Message}\n{ex.StackTrace}");
                tcs.SetResult(CreateErrorResponse(
                    $"Failed to execute tool {tool.Name}: {ex.Message}",
                    "tool_execution_error"
                ));
            }

            yield return null;
        }

        /// <summary>
        /// Fetch a resource with the provided parameters
        /// </summary>
        private IEnumerator FetchResourceCoroutine(McpResourceBase resource, JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            try
            {
                if (resource.IsAsync)
                {
                    resource.FetchAsync(parameters, tcs);
                }
                else
                {
                    var result = resource.Fetch(parameters);
                    tcs.SetResult(result);
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error fetching resource {resource.Name}: {ex.Message}\n{ex.StackTrace}");
                tcs.SetResult(CreateErrorResponse(
                    $"Failed to fetch resource {resource.Name}: {ex.Message}",
                    "resource_fetch_error"
                ));
            }
            yield return null;
        }

        /// <summary>
        /// Create a JSON-RPC 2.0 response
        /// </summary>
        /// <param name="requestId">Request ID</param>
        /// <param name="result">Result object</param>
        /// <returns>JSON-RPC 2.0 response</returns>
        private JObject CreateResponse(string requestId, JObject result)
        {
            // Format as JSON-RPC 2.0 response
            JObject jsonRpcResponse = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId
            };

            // Add result or error
            if (result.TryGetValue("error", out var errorObj))
            {
                jsonRpcResponse["error"] = errorObj;
            }
            else
            {
                jsonRpcResponse["result"] = result;
            }

            return jsonRpcResponse;
        }

        /// <summary>
        /// Try to match a URI against resource patterns and extract template parameters
        /// Example: unity://logs/all matches unity://logs/{logType} and extracts logType=all
        /// </summary>
        private McpResourceBase TryMatchResourcePattern(string uri, out Dictionary<string, string> templateParams)
        {
            templateParams = new Dictionary<string, string>();

            foreach (var resourceEntry in _server.GetResources().Values)
            {
                var pattern = resourceEntry.Uri;
                if (string.IsNullOrEmpty(pattern))
                {
                    continue;
                }

                // Split both URI and pattern into segments
                var uriParts = uri.Split('/');
                var patternParts = pattern.Split('/');

                // Must have same number of segments
                if (uriParts.Length != patternParts.Length)
                    continue;

                bool isMatch = true;
                var tempParams = new Dictionary<string, string>();

                // Compare each segment
                for (int i = 0; i < uriParts.Length; i++)
                {
                    var patternPart = patternParts[i];
                    var uriPart = uriParts[i];

                    // Check if pattern part is a template parameter {param}
                    if (patternPart.StartsWith("{") && patternPart.EndsWith("}"))
                    {
                        // Extract parameter name and value
                        var paramName = patternPart.Substring(1, patternPart.Length - 2);
                        tempParams[paramName] = Uri.UnescapeDataString(uriPart);
                    }
                    else if (patternPart != uriPart)
                    {
                        // Literal segments must match exactly
                        isMatch = false;
                        break;
                    }
                }

                if (isMatch)
                {
                    templateParams = tempParams;
                    return resourceEntry;
                }
            }

            return null;
        }

        /// <summary>
        /// Get method signature for discovery
        /// </summary>
        private JObject GetMethodSignature(string methodName)
        {
            // Check if it's a tool
            if (_server.TryGetTool(methodName, out var tool))
            {
                return new JObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["params"] = tool.InputSchema,
                    ["returns"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["success"] = new JObject { ["type"] = "boolean" },
                            ["type"] = new JObject { ["type"] = "string" },
                            ["data"] = new JObject { ["type"] = "object" },
                            ["message"] = new JObject { ["type"] = "string" }
                        }
                    }
                };
            }

            // Check if it's a resource
            if (_server.TryGetResource(methodName, out var resource))
            {
                return new JObject
                {
                    ["name"] = resource.Name,
                    ["description"] = resource.Description,
                    ["params"] = new JObject
                    {
                        ["type"] = "object"
                    },
                    ["returns"] = new JObject
                    {
                        ["type"] = "object"
                    }
                };
            }

            // Check if it's a discovery method
            switch (methodName)
            {
                case "rpc.discover":
                    return new JObject
                    {
                        ["name"] = "rpc.discover",
                        ["description"] = "OpenRPC discovery endpoint",
                        ["params"] = new JArray(),
                        ["returns"] = new JObject { ["type"] = "object" }
                    };
                case "system.listMethods":
                    return new JObject
                    {
                        ["name"] = "system.listMethods",
                        ["description"] = "List available methods",
                        ["params"] = new JArray(),
                        ["returns"] = new JObject
                        {
                            ["type"] = "array",
                            ["items"] = new JObject { ["type"] = "string" }
                        }
                    };
                case "system.methodSignature":
                    return new JObject
                    {
                        ["name"] = "system.methodSignature",
                        ["description"] = "Get method signature",
                        ["params"] = new JArray
                        {
                            new JObject
                            {
                                ["name"] = "methodName",
                                ["type"] = "string",
                                ["required"] = true
                            }
                        },
                        ["returns"] = new JObject { ["type"] = "object" }
                    };
            }

            return null;
        }
    }
}
