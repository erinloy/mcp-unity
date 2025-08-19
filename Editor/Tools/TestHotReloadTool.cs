using System;
using System.Threading.Tasks;
using McpUnity.Unity;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Test tool to verify hot-reload functionality
    /// Created: 2025-01-19
    /// </summary>
    public class TestHotReloadTool : McpToolBase
    {
        public TestHotReloadTool()
        {
            Name = "test_hot_reload";
            Description = "Test tool to verify hot-reload is working";
            
            // Define the input schema for MCP protocol
            InputSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["message"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Test message to echo back"
                    },
                    ["timestamp"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Include timestamp in response",
                        ["default"] = true
                    }
                },
                ["required"] = new JArray { "message" }
            };
        }
        
        /// <summary>
        /// Execute the test tool
        /// </summary>
        public override JObject Execute(JObject parameters)
        {
            string message = parameters["message"]?.ToObject<string>() ?? "No message";
            bool includeTimestamp = parameters["timestamp"]?.ToObject<bool>() ?? true;
            
            string response = $"🔥 Hot-Reload Test: {message}";
            
            if (includeTimestamp)
            {
                response += $" [Time: {DateTime.Now:HH:mm:ss}]";
            }
            
            // Log to Unity console
            Debug.Log($"[MCP Hot-Reload Test]: {response}");
            
            // Return success response
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = response,
                ["data"] = new JObject
                {
                    ["originalMessage"] = message,
                    ["processedAt"] = DateTime.Now.ToString("o"),
                    ["unityVersion"] = Application.unityVersion,
                    ["hotReloadTest"] = true
                }
            };
        }
    }
}