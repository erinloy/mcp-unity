using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for inspecting GameObjects - lists components, serialized fields, and their current values.
    /// Essential for understanding scene structure and available wiring points.
    /// </summary>
    public class InspectGameObjectTool : McpToolBase
    {
        public InspectGameObjectTool()
        {
            Name = "inspect_gameobject";
            Description = "Inspects a GameObject and returns its components, serialized fields, and hierarchy. Use this to discover what can be modified or wired up.";

            InputSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["path"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Hierarchy path to the GameObject"
                    },
                    ["instanceId"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Instance ID of the GameObject (alternative to path)"
                    },
                    ["includeChildren"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "If true, also lists immediate children"
                    },
                    ["includeFieldValues"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "If true, includes current field values (more verbose)"
                    }
                }
            };
        }

        public override JObject Execute(JObject parameters)
        {
            // Accept both "path" and "objectPath" for compatibility with server-side naming
            string path = parameters["path"]?.ToObject<string>() ?? parameters["objectPath"]?.ToObject<string>();
            int? instanceId = parameters["instanceId"]?.ToObject<int?>();
            bool includeChildren = parameters["includeChildren"]?.ToObject<bool>() ?? false;
            // Accept both "includeFieldValues" and "includeFields" for compatibility
            bool includeFieldValues = parameters["includeFieldValues"]?.ToObject<bool>()
                ?? parameters["includeFields"]?.ToObject<bool>()
                ?? false;

            GameObject target = null;

            if (instanceId.HasValue)
            {
                target = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
            }
            else if (!string.IsNullOrEmpty(path))
            {
                target = FindGameObjectByPath(path);
            }

            if (target == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject not found. Path: {path ?? "not specified"}, InstanceId: {instanceId?.ToString() ?? "not specified"}",
                    "not_found_error");
            }

            JObject gameObjectData = InspectObject(target, includeFieldValues);

            if (includeChildren)
            {
                JArray children = new JArray();
                foreach (Transform child in target.transform)
                {
                    children.Add(new JObject
                    {
                        ["name"] = child.name,
                        ["path"] = GetGameObjectPath(child.gameObject),
                        ["instanceId"] = child.gameObject.GetInstanceID(),
                        ["active"] = child.gameObject.activeSelf,
                        ["componentCount"] = child.GetComponents<Component>().Length
                    });
                }
                gameObjectData["children"] = children;
            }

            // Format result with MCP content array for proper text output
            string summary = GenerateSummary(target, includeChildren);
            string jsonData = gameObjectData.ToString(Newtonsoft.Json.Formatting.Indented);

            return new JObject
            {
                ["success"] = true,
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = $"{summary}\n\nDetailed JSON:\n{jsonData}"
                    }
                }
            };
        }

        private JObject InspectObject(GameObject go, bool includeFieldValues)
        {
            JObject obj = new JObject
            {
                ["name"] = go.name,
                ["path"] = GetGameObjectPath(go),
                ["instanceId"] = go.GetInstanceID(),
                ["active"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["tag"] = go.tag,
                ["layer"] = go.layer,
                ["layerName"] = LayerMask.LayerToName(go.layer),
                ["isStatic"] = go.isStatic
            };

            // Components
            JArray components = new JArray();
            foreach (Component comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;

                JObject compObj = new JObject
                {
                    ["type"] = comp.GetType().Name,
                    ["fullType"] = comp.GetType().FullName,
                    ["instanceId"] = comp.GetInstanceID()
                };

                // Get serialized fields
                JArray fields = new JArray();
                Type compType = comp.GetType();

                foreach (FieldInfo field in compType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    // Check if serialized (public, or has SerializeField attribute)
                    bool isSerialized = field.IsPublic ||
                        field.GetCustomAttribute<SerializeField>() != null;

                    if (!isSerialized) continue;

                    // Skip fields marked with HideInInspector or NonSerialized
                    if (field.GetCustomAttribute<HideInInspector>() != null) continue;
                    if (field.GetCustomAttribute<NonSerializedAttribute>() != null) continue;

                    JObject fieldObj = new JObject
                    {
                        ["name"] = field.Name,
                        ["type"] = GetFriendlyTypeName(field.FieldType),
                        ["fullType"] = field.FieldType.FullName,
                        ["isPublic"] = field.IsPublic,
                        ["isUnityObject"] = typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType)
                    };

                    if (includeFieldValues)
                    {
                        try
                        {
                            object value = field.GetValue(comp);
                            fieldObj["value"] = SerializeValue(value);
                            fieldObj["isNull"] = value == null || (value is UnityEngine.Object uobj && uobj == null);
                        }
                        catch
                        {
                            fieldObj["value"] = "<error reading value>";
                        }
                    }

                    fields.Add(fieldObj);
                }

                if (fields.Count > 0)
                {
                    compObj["fields"] = fields;
                }

                components.Add(compObj);
            }

            obj["components"] = components;
            return obj;
        }

        private string SerializeValue(object value)
        {
            if (value == null) return "null";

            if (value is UnityEngine.Object uobj)
            {
                if (uobj == null) return "null (missing reference)";
                return $"{uobj.GetType().Name}: {uobj.name}";
            }

            if (value is Vector2 v2) return $"({v2.x:F2}, {v2.y:F2})";
            if (value is Vector3 v3) return $"({v3.x:F2}, {v3.y:F2}, {v3.z:F2})";
            if (value is Quaternion q) return $"({q.x:F2}, {q.y:F2}, {q.z:F2}, {q.w:F2})";
            if (value is Color c) return $"RGBA({c.r:F2}, {c.g:F2}, {c.b:F2}, {c.a:F2})";
            if (value is bool b) return b.ToString().ToLower();
            if (value is string s) return $"\"{s}\"";
            if (value is Enum e) return e.ToString();

            return value.ToString();
        }

        private string GetFriendlyTypeName(Type type)
        {
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(string)) return "string";
            if (type == typeof(Vector2)) return "Vector2";
            if (type == typeof(Vector3)) return "Vector3";
            if (type == typeof(Quaternion)) return "Quaternion";
            if (type == typeof(Color)) return "Color";
            if (type == typeof(GameObject)) return "GameObject";

            return type.Name;
        }

        private string GenerateSummary(GameObject go, bool includeChildren)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"GameObject: {go.name}");
            sb.AppendLine($"Path: {GetGameObjectPath(go)}");
            sb.AppendLine($"Active: {go.activeSelf}");
            sb.AppendLine();
            sb.AppendLine("Components:");

            foreach (Component comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                sb.AppendLine($"  - {comp.GetType().Name}");
            }

            if (includeChildren && go.transform.childCount > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Children ({go.transform.childCount}):");
                foreach (Transform child in go.transform)
                {
                    sb.AppendLine($"  - {child.name}");
                }
            }

            return sb.ToString();
        }

        private GameObject FindGameObjectByPath(string path)
        {
            GameObject go = GameObject.Find(path);
            if (go != null) return go;

            string[] pathParts = path.TrimStart('/').Split('/');
            if (pathParts.Length == 0) return null;

            GameObject[] rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();

            foreach (GameObject root in rootObjects)
            {
                if (root.name == pathParts[0])
                {
                    if (pathParts.Length == 1) return root;

                    Transform current = root.transform;
                    for (int i = 1; i < pathParts.Length; i++)
                    {
                        Transform child = current.Find(pathParts[i]);
                        if (child == null) break;
                        if (i == pathParts.Length - 1) return child.gameObject;
                        current = child;
                    }
                }
            }

            return null;
        }

        private string GetGameObjectPath(GameObject obj)
        {
            if (obj == null) return null;
            string path = obj.name;
            while (obj.transform.parent != null)
            {
                obj = obj.transform.parent.gameObject;
                path = obj.name + "/" + path;
            }
            return path;
        }
    }
}
