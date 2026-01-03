using System;
using System.Collections.Generic;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for finding GameObjects in the Unity scene by name pattern or listing children
    /// </summary>
    public class FindGameObjectTool : McpToolBase
    {
        public FindGameObjectTool()
        {
            Name = "find_gameobject";
            Description = "Finds GameObjects by name pattern (searches all scene objects) or lists children of a specified path";

            InputSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["namePattern"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name pattern to search for (case-insensitive, partial match). Searches all GameObjects in the scene."
                    },
                    ["listChildrenOf"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Full hierarchy path to list children of (e.g., 'Canvas/Poma'). Returns immediate children."
                    },
                    ["recursive"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "If true and listChildrenOf is specified, returns all descendants recursively. Default: false"
                    },
                    ["maxResults"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Maximum number of results to return. Default: 50"
                    }
                },
                ["description"] = "Provide namePattern to search, or listChildrenOf to list children of a specific GameObject"
            };
        }

        public override JObject Execute(JObject parameters)
        {
            string namePattern = parameters["namePattern"]?.ToObject<string>();
            string listChildrenOf = parameters["listChildrenOf"]?.ToObject<string>();
            bool recursive = parameters["recursive"]?.ToObject<bool>() ?? false;
            int maxResults = parameters["maxResults"]?.ToObject<int>() ?? 50;

            if (string.IsNullOrEmpty(namePattern) && string.IsNullOrEmpty(listChildrenOf))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'namePattern' or 'listChildrenOf' not provided",
                    "validation_error"
                );
            }

            var results = new List<JObject>();

            if (!string.IsNullOrEmpty(listChildrenOf))
            {
                // List children of specified path
                GameObject parent = GameObject.Find(listChildrenOf);
                if (parent == null)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"GameObject not found at path: {listChildrenOf}",
                        "not_found"
                    );
                }

                if (recursive)
                {
                    CollectChildrenRecursive(parent.transform, GetFullPath(parent.transform), results, maxResults);
                }
                else
                {
                    foreach (Transform child in parent.transform)
                    {
                        if (results.Count >= maxResults) break;
                        results.Add(CreateGameObjectInfo(child.gameObject, GetFullPath(child)));
                    }
                }
            }
            else if (!string.IsNullOrEmpty(namePattern))
            {
                // Search all GameObjects by name pattern
                string patternLower = namePattern.ToLowerInvariant();

                // Find all root GameObjects in all scenes
                var rootObjects = new List<GameObject>();
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (scene.isLoaded)
                    {
                        rootObjects.AddRange(scene.GetRootGameObjects());
                    }
                }

                // Search through all objects
                foreach (var root in rootObjects)
                {
                    if (results.Count >= maxResults) break;
                    SearchByName(root.transform, patternLower, results, maxResults);
                }
            }

            // Format as MCP content for proper text output
            var resultsJson = new JArray(results).ToString(Newtonsoft.Json.Formatting.Indented);
            var summary = !string.IsNullOrEmpty(listChildrenOf)
                ? $"Children of '{listChildrenOf}': {results.Count} found"
                : $"Search for '{namePattern}': {results.Count} match(es)";

            return new JObject
            {
                ["success"] = true,
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = $"{summary}\n\n{resultsJson}"
                    }
                }
            };
        }

        private void SearchByName(Transform transform, string patternLower, List<JObject> results, int maxResults)
        {
            if (results.Count >= maxResults) return;

            if (transform.name.ToLowerInvariant().Contains(patternLower))
            {
                results.Add(CreateGameObjectInfo(transform.gameObject, GetFullPath(transform)));
            }

            foreach (Transform child in transform)
            {
                if (results.Count >= maxResults) break;
                SearchByName(child, patternLower, results, maxResults);
            }
        }

        private void CollectChildrenRecursive(Transform transform, string parentPath, List<JObject> results, int maxResults)
        {
            foreach (Transform child in transform)
            {
                if (results.Count >= maxResults) break;

                string childPath = string.IsNullOrEmpty(parentPath) ? child.name : $"{parentPath}/{child.name}";
                results.Add(CreateGameObjectInfo(child.gameObject, childPath));

                CollectChildrenRecursive(child, childPath, results, maxResults);
            }
        }

        private string GetFullPath(Transform transform)
        {
            if (transform.parent == null)
                return transform.name;
            return GetFullPath(transform.parent) + "/" + transform.name;
        }

        private JObject CreateGameObjectInfo(GameObject go, string path)
        {
            var components = new JArray();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp != null)
                    components.Add(comp.GetType().Name);
            }

            return new JObject
            {
                ["name"] = go.name,
                ["path"] = path,
                ["instanceId"] = go.GetInstanceID(),
                ["active"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["childCount"] = go.transform.childCount,
                ["components"] = components
            };
        }
    }
}
