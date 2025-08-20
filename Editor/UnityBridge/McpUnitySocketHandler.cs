using System;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebSocketSharp;
using WebSocketSharp.Server;
using McpUnity.Tools;
using McpUnity.Resources;
using McpUnity.Discovery;
using Unity.EditorCoroutines.Editor;
// Force Unity recompile - fixed tools/list response format
using System.Collections;
using System.Collections.Specialized;
using McpUnity.Utils;

namespace McpUnity.Unity
{
    /// <summary>
    /// WebSocket handler for MCP Unity communications
    /// </summary>
    public class McpUnitySocketHandler : WebSocketBehavior
    {
        private readonly McpUnityServer _server;
        
        /// <summary>
        /// Default constructor required by WebSocketSharp
        /// </summary>
        public McpUnitySocketHandler(McpUnityServer server)
        {
            _server = server;
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
        /// Handle incoming messages from WebSocket clients
        /// </summary>
        protected override async void OnMessage(MessageEventArgs e)
        {
            try
            {
                // Only log if verbose logging is enabled or if it's not a routine message
                var data = e.Data;
                bool isRoutineMessage = data.Contains("tools/list") || data.Contains("resources/list");
                
                if (McpUnitySettings.Instance.VerboseLogging || !isRoutineMessage)
                {
                    McpLogger.LogInfo($"WebSocket message received: {e.Data}");
                }
                JObject requestJson;
                try
                {
                    requestJson = JObject.Parse(e.Data);
                }
                catch (JsonReaderException jre)
                {
                    McpLogger.LogError($"Invalid JSON received: {jre.Message}. Data: {e.Data}");
                    // Attempt to send a parse error response. No requestId is available yet.
                    Send(CreateResponse(null, CreateErrorResponse($"Invalid JSON format: {jre.Message}", "invalid_json")).ToString(Formatting.None));
                    return;
                }

                var method = requestJson["method"]?.ToString();
                var parameters = requestJson["params"] as JObject ?? new JObject();
                var requestId = requestJson["id"]?.ToString();
                // We need to dispatch to Unity's main thread and wait for completion
                var tcs = new TaskCompletionSource<JObject>();
                
                if (string.IsNullOrEmpty(method))
                {
                    tcs.SetResult(CreateErrorResponse("Missing method in request", "invalid_request"));
                }
                // Handle standard MCP protocol methods
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
                    var result = new JObject
                    {
                        ["tools"] = toolsArray
                    };
                    tcs.SetResult(result);
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
                    var result = new JObject
                    {
                        ["resources"] = resourcesArray
                    };
                    tcs.SetResult(result);
                }
                else if (method == "resources/read" || method == "resource/read") // Support both singular and plural
                {
                    var uri = parameters["uri"]?.ToString();
                    if (string.IsNullOrEmpty(uri))
                    {
                        tcs.SetResult(CreateErrorResponse("Missing uri parameter", "invalid_params"));
                    }
                    else if (_server.TryGetResource(uri, out var resource))
                    {
                        EditorCoroutineUtility.StartCoroutineOwnerless(FetchResourceCoroutine(resource, parameters, tcs));
                    }
                    else
                    {
                        tcs.SetResult(CreateErrorResponse($"Resource not found: {uri}", "resource_not_found"));
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
                        // Convert arguments format for tool execution
                        var toolParams = new JObject();
                        foreach (var arg in arguments)
                        {
                            toolParams[arg.Key] = arg.Value;
                        }
                        EditorCoroutineUtility.StartCoroutineOwnerless(ExecuteTool(tool, toolParams, tcs));
                    }
                    else
                    {
                        tcs.SetResult(CreateErrorResponse($"Tool not found: {name}", "tool_not_found"));
                    }
                }
                // Handle discovery methods (legacy support)
                else if (method == "rpc.discover")
                {
                    var openRpcDoc = OpenRpcDiscovery.GenerateOpenRpcDocument(
                        _server.GetTools(), 
                        _server.GetResources()
                    );
                    tcs.SetResult(openRpcDoc);
                }
                else if (method == "system.listMethods")
                {
                    var methods = new List<string> { "rpc.discover", "system.listMethods", "system.methodSignature", "tools/list", "tools/call", "resources/list", "resources/read" };
                    methods.AddRange(_server.GetTools().Keys);
                    methods.AddRange(_server.GetResources().Keys);
                    tcs.SetResult(JObject.FromObject(methods));
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
                else if (_server.TryGetTool(method, out var tool))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(ExecuteTool(tool, parameters, tcs));
                }
                else if (_server.TryGetResource(method, out var resource))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(FetchResourceCoroutine(resource, parameters, tcs));
                }
                else
                {
                    tcs.SetResult(CreateErrorResponse($"Unknown method: {method}", "unknown_method"));
                }
                
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
        /// Handle WebSocket connection open
        /// </summary>
        protected override void OnOpen()
        {
            // Extract client name from the X-Client-Name header
            string clientName = "MCP Client";
            NameValueCollection headers = Context.Headers;
            if (headers != null && headers.Contains("X-Client-Name"))
            {
                clientName = headers["X-Client-Name"];
            }
            
            // Add the client to the server (always track, even without header)
            _server.Clients.Add(ID, clientName);
            
            McpLogger.LogInfo($"WebSocket client '{clientName}' connected (ID: {ID})");
        }
        
        /// <summary>
        /// Handle WebSocket connection close
        /// </summary>
        protected override void OnClose(CloseEventArgs e)
        {
            _server.Clients.TryGetValue(ID, out string clientName);
            
            // Remove the client from the server
            _server.Clients.Remove(ID);
            
            McpLogger.LogInfo($"WebSocket client '{clientName}' disconnected: {e.Reason}");
        }
        
        /// <summary>
        /// Handle WebSocket errors
        /// </summary>
        protected override void OnError(ErrorEventArgs e)
        {
            McpLogger.LogError($"WebSocket error: {e.Message}");
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
                ["jsonrpc"] = "2.0",  // Add proper JSON-RPC version
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
                    ["params"] = new JObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true
                    },
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
