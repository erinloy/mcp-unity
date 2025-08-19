using System;
using System.Threading.Tasks;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for selecting GameObjects in the Unity Editor
    /// </summary>
    public class SelectGameObjectTool : McpToolBase
    {
        public SelectGameObjectTool()
        {
            Name = "select_gameobject";
            Description = "Sets the selected GameObject in the Unity editor by path, name or instance ID";
            
            // Define the input schema for MCP protocol
            InputSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["objectPath"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Full hierarchy path to the GameObject (e.g., 'Canvas/Button')"
                    },
                    ["objectName"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name of the GameObject to select"
                    },
                    ["instanceId"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Instance ID of the GameObject"
                    }
                },
                ["description"] = "Provide either objectPath, objectName, or instanceId to select a GameObject"
            };
        }
        
        /// <summary>
        /// Execute the SelectGameObject tool with the provided parameters synchronously
        /// </summary>
        /// <param name="parameters">Tool parameters as a JObject</param>
        public override JObject Execute(JObject parameters)
        {
            // Extract parameters
            string objectPath = parameters["objectPath"]?.ToObject<string>();
            string objectName = parameters["objectName"]?.ToObject<string>();
            int? instanceId = parameters["instanceId"]?.ToObject<int?>();
            GameObject selectedGameObject = null;
            
            // Validate parameters - require either objectPath or instanceId
            if (string.IsNullOrEmpty(objectPath) && string.IsNullOrEmpty(objectName) && !instanceId.HasValue)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'objectPath', 'objectName' or 'instanceId' not provided", 
                    "validation_error"
                );
            }
            
            // First try to find by instance ID if provided
            if (instanceId.HasValue)
            {
                selectedGameObject = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
            }
            else if (!string.IsNullOrEmpty(objectPath))
            {
                // Try to find the object by path in the hierarchy
                selectedGameObject = GameObject.Find(objectPath);
            }
            else
            {
                // Try to find the object by name in the hierarchy
                selectedGameObject = GameObject.Find(objectName);
            }
            
            Selection.activeGameObject = selectedGameObject;

            // Ping the selected object
            EditorGUIUtility.PingObject(selectedGameObject);
            
            McpLogger.LogInfo($"[MCP Unity] Selected GameObject: {selectedGameObject?.name}");
            
            // Create the response
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Successfully selected GameObject {selectedGameObject?.name}"
            };
        }
    }
}
