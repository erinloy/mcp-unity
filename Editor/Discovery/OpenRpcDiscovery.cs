using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using McpUnity.Tools;
using McpUnity.Resources;

namespace McpUnity.Discovery
{
    /// <summary>
    /// Implements OpenRPC discovery specification for the Unity MCP server
    /// </summary>
    public static class OpenRpcDiscovery
    {
        private const string OPENRPC_VERSION = "1.3.2";
        private const string JSONRPC_VERSION = "2.0";
        
        /// <summary>
        /// Generate OpenRPC discovery document
        /// </summary>
        public static JObject GenerateOpenRpcDocument(
            Dictionary<string, McpToolBase> tools,
            Dictionary<string, McpResourceBase> resources)
        {
            var methods = new JArray();
            
            // Add standard discovery methods
            methods.Add(CreateDiscoveryMethod());
            methods.Add(CreateListMethodsMethod());
            methods.Add(CreateMethodSignatureMethod());
            
            // Add all registered tools
            foreach (var tool in tools.Values)
            {
                methods.Add(CreateMethodFromTool(tool));
            }
            
            // Add all registered resources  
            foreach (var resource in resources.Values)
            {
                methods.Add(CreateMethodFromResource(resource));
            }
            
            return new JObject
            {
                ["openrpc"] = OPENRPC_VERSION,
                ["info"] = new JObject
                {
                    ["title"] = "Unity MCP Server",
                    ["description"] = "Unity Editor control via Model Context Protocol",
                    ["version"] = "1.0.0",
                    ["contact"] = new JObject
                    {
                        ["name"] = "Unity MCP Bridge"
                    }
                },
                ["servers"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "Unity WebSocket Server",
                        ["url"] = "ws://localhost:{port}",
                        ["variables"] = new JObject
                        {
                            ["port"] = new JObject
                            {
                                ["default"] = "9980",
                                ["description"] = "WebSocket port (dynamically allocated)"
                            }
                        }
                    }
                },
                ["methods"] = methods,
                ["components"] = new JObject
                {
                    ["schemas"] = GenerateSchemas()
                }
            };
        }
        
        private static JObject CreateDiscoveryMethod()
        {
            return new JObject
            {
                ["name"] = "rpc.discover",
                ["summary"] = "OpenRPC discovery endpoint",
                ["description"] = "Returns the OpenRPC specification document",
                ["params"] = new JArray(),
                ["result"] = new JObject
                {
                    ["name"] = "OpenRPC Document",
                    ["schema"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "OpenRPC specification document"
                    }
                }
            };
        }
        
        private static JObject CreateListMethodsMethod()
        {
            return new JObject
            {
                ["name"] = "system.listMethods",
                ["summary"] = "List available methods",
                ["description"] = "Returns an array of available method names",
                ["params"] = new JArray(),
                ["result"] = new JObject
                {
                    ["name"] = "methods",
                    ["schema"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject
                        {
                            ["type"] = "string"
                        }
                    }
                }
            };
        }
        
        private static JObject CreateMethodSignatureMethod()
        {
            return new JObject
            {
                ["name"] = "system.methodSignature",
                ["summary"] = "Get method signature",
                ["description"] = "Returns the parameter schema for a specific method",
                ["params"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "methodName",
                        ["schema"] = new JObject
                        {
                            ["type"] = "string"
                        },
                        ["required"] = true
                    }
                },
                ["result"] = new JObject
                {
                    ["name"] = "signature",
                    ["schema"] = new JObject
                    {
                        ["type"] = "object"
                    }
                }
            };
        }
        
        private static JObject CreateMethodFromTool(McpToolBase tool)
        {
            var method = new JObject
            {
                ["name"] = tool.Name,
                ["summary"] = tool.Description ?? $"Execute {tool.Name} tool",
                ["description"] = GetToolDescription(tool),
                ["params"] = GenerateToolParameters(tool),
                ["result"] = new JObject
                {
                    ["name"] = "result",
                    ["schema"] = new JObject
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
                }
            };
            
            if (tool.IsAsync)
            {
                method["x-async"] = true;
            }
            
            return method;
        }
        
        private static JObject CreateMethodFromResource(McpResourceBase resource)
        {
            return new JObject
            {
                ["name"] = resource.Name,
                ["summary"] = resource.Description ?? $"Fetch {resource.Name} resource",
                ["description"] = GetResourceDescription(resource),
                ["params"] = GenerateResourceParameters(resource),
                ["result"] = new JObject
                {
                    ["name"] = "result",
                    ["schema"] = new JObject
                    {
                        ["type"] = "object"
                    }
                }
            };
        }
        
        private static JArray GenerateToolParameters(McpToolBase tool)
        {
            // Use reflection to discover parameters from Execute method
            var executeMethod = tool.GetType().GetMethod("Execute");
            if (executeMethod == null) return new JArray();
            
            // For now, return a generic params object
            // In a real implementation, we'd parse parameter attributes or schema
            return new JArray
            {
                new JObject
                {
                    ["name"] = "params",
                    ["schema"] = new JObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true
                    }
                }
            };
        }
        
        private static JArray GenerateResourceParameters(McpResourceBase resource)
        {
            return new JArray
            {
                new JObject
                {
                    ["name"] = "params",
                    ["schema"] = new JObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true
                    }
                }
            };
        }
        
        private static string GetToolDescription(McpToolBase tool)
        {
            // Could use attributes or documentation to enhance this
            return tool.Description ?? $"Unity tool: {tool.Name}";
        }
        
        private static string GetResourceDescription(McpResourceBase resource)
        {
            return resource.Description ?? $"Unity resource: {resource.Name}";
        }
        
        private static JObject GenerateSchemas()
        {
            // Define common schemas used across methods
            return new JObject
            {
                ["Error"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["code"] = new JObject { ["type"] = "integer" },
                        ["message"] = new JObject { ["type"] = "string" },
                        ["data"] = new JObject { ["type"] = "object" }
                    }
                },
                ["ToolResult"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["success"] = new JObject { ["type"] = "boolean" },
                        ["type"] = new JObject 
                        { 
                            ["type"] = "string",
                            ["enum"] = new JArray { "text", "image", "images", "error", "files" }
                        },
                        ["data"] = new JObject { ["type"] = "object" },
                        ["message"] = new JObject { ["type"] = "string" }
                    }
                }
            };
        }
    }
}