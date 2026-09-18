using McpUnity.Resources;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for retrieving detailed information about a specific GameObject.
    /// This tool provides the same functionality as the get_gameobject resource,
    /// but as a tool that can be invoked directly without URI template parameters.
    /// </summary>
    public class GetGameObjectTool : McpToolBase
    {
        public GetGameObjectTool()
        {
            Name = "get_gameobject";
            Description = "Retrieves detailed information about a specific GameObject by instance ID, name, or hierarchical path (e.g., \"Parent/Child/MyObject\"). Returns component properties (Transform position/rotation/scale, etc.) plus a scoped hierarchy of children. Use optional 'maxDepth' (default 2), 'includeComponents', and 'includeComponentProperties' to control response size; when limits are hit, nodes carry a '_truncated' marker so you can re-query a narrower subtree.";
        }

        /// <summary>
        /// Execute the GetGameObject tool with the provided parameters
        /// </summary>
        /// <param name="parameters">Tool parameters as a JObject. Required 'idOrName' (instance ID, name, or path).
        /// Optional 'maxDepth' (int, default 2), 'includeComponents' (bool, default true),
        /// 'includeComponentProperties' (bool, default true).</param>
        /// <returns>A JObject containing the GameObject data</returns>
        public override JObject Execute(JObject parameters)
        {
            // Validate parameters
            if (parameters == null || !parameters.ContainsKey("idOrName"))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Missing required parameter: idOrName",
                    "validation_error"
                );
            }

            string idOrName = parameters["idOrName"]?.ToObject<string>();

            if (string.IsNullOrEmpty(idOrName))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Parameter 'idOrName' cannot be null or empty",
                    "validation_error"
                );
            }

            GameObject gameObject = null;

            // Try to parse as an instance ID first
            if (int.TryParse(idOrName, out int instanceId))
            {
                // Unity Instance IDs are typically negative, but we'll accept any integer
                UnityEngine.Object unityObject = UnityObjectId.ObjectFromId(instanceId);
                gameObject = unityObject as GameObject;
            }
            else
            {
                // Otherwise, treat it as a name or hierarchical path
                gameObject = GameObject.Find(idOrName);
            }

            // Check if the GameObject was found
            if (gameObject == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject with '{idOrName}' reference not found. Make sure the GameObject exists and is loaded in the current scene(s).",
                    "not_found_error"
                );
            }

            int maxDepth = parameters["maxDepth"]?.ToObject<int?>() ?? GetGameObjectResource.DefaultMaxChildDepth;
            bool includeComponents = parameters["includeComponents"]?.ToObject<bool?>() ?? true;
            bool includeComponentProperties = parameters["includeComponentProperties"]?.ToObject<bool?>() ?? true;

            // Convert the GameObject to a JObject using the resource's static method
            JObject gameObjectData = GetGameObjectResource.GameObjectToJObject(
                gameObject, true, maxDepth, includeComponents, includeComponentProperties);

            // Create the response
            return new JObject
            {
                ["success"] = true,
                ["message"] = $"Retrieved GameObject data for '{gameObject.name}'",
                ["gameObject"] = gameObjectData,
                ["instanceId"] = UnityObjectId.GetObjectId(gameObject)
            };
        }
    }
}

