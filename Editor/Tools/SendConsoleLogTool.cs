using System.Threading.Tasks;
using McpUnity.Unity;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for sending notification messages to the Unity console
    /// </summary>
    public class SendConsoleLogTool : McpToolBase
    {
        public SendConsoleLogTool()
        {
            Name = "send_console_log";
            Description = "Sends a message to the Unity console";
            
            // Define the input schema for MCP protocol
            InputSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["message"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "The message to log to the Unity console"
                    },
                    ["logLevel"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Log level: Info, Warning, or Error",
                        ["enum"] = new JArray { "Info", "Warning", "Error" },
                        ["default"] = "Info"
                    }
                },
                ["required"] = new JArray { "message" }
            };
        }
        
        /// <summary>
        /// Execute the NotifyMessage tool with the provided parameters synchronously
        /// </summary>
        /// <param name="parameters">Tool parameters as a JObject</param>
        public override JObject Execute(JObject parameters)
        {
            // Extract parameters (support both 'type' and 'logLevel' for compatibility)
            string message = parameters["message"]?.ToObject<string>();
            string type = (parameters["logLevel"] ?? parameters["type"])?.ToObject<string>()?.ToLower() ?? "info";
 
            if (string.IsNullOrEmpty(message))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'message' not provided", 
                    "validation_error"
                );
            }
 
            // Log the message based on type
            switch (type)
            {
                case "error":
                    Debug.LogError($"[MCP]: {message}");
                    break;
                case "warning":
                    Debug.LogWarning($"[MCP]: {message}");
                    break;
                default:
                    Debug.Log($"[MCP]: {message}");
                    break;
            }
 
            // Create the response
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Message displayed: {message}"
            };
        }
    }
}
