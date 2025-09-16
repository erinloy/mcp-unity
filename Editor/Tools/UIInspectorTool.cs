using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    /// <summary>
    /// Comprehensive UI inspection and manipulation tool
    /// </summary>
    public class UIInspectorTool : McpToolBase
    {
        public UIInspectorTool()
        {
            Name = "ui";
            Description = "Inspect and manipulate Unity UI elements dynamically";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                string action = parameters["action"]?.ToObject<string>();

                switch (action?.ToLower())
                {
                    case "find":
                        return FindUIElements(parameters);
                    case "inspect":
                        return InspectElement(parameters);
                    case "set":
                        return SetProperty(parameters);
                    case "invoke":
                        return InvokeMethod(parameters);
                    case "hierarchy":
                        return GetHierarchy(parameters);
                    case "create":
                        return CreateElement(parameters);
                    case "destroy":
                        return DestroyElement(parameters);
                    default:
                        return CreateErrorResponse($"Unknown action: {action}. Use: find, inspect, set, invoke, hierarchy, create, destroy");
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"UI Inspector error: {ex}");
                return CreateErrorResponse($"UI operation failed: {ex.Message}");
            }
        }

        private JObject FindUIElements(JObject parameters)
        {
            string query = parameters["query"]?.ToObject<string>();
            string componentType = parameters["type"]?.ToObject<string>();

            List<GameObject> results = new List<GameObject>();

            if (!string.IsNullOrEmpty(componentType))
            {
                // Find by component type
                Type type = GetUIComponentType(componentType);
                if (type != null)
                {
                    UnityEngine.Object[] objects = GameObject.FindObjectsByType(type, FindObjectsSortMode.None);
                    Component[] components = objects.Cast<Component>().ToArray();
                    results.AddRange(components.Select(c => c.gameObject));
                }
            }
            else if (!string.IsNullOrEmpty(query))
            {
                // Find by name (supports wildcards)
                GameObject[] allObjects = GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
                foreach (var obj in allObjects)
                {
                    if (query.Contains("*"))
                    {
                        string pattern = query.Replace("*", ".*");
                        if (System.Text.RegularExpressions.Regex.IsMatch(obj.name, pattern))
                        {
                            results.Add(obj);
                        }
                    }
                    else if (obj.name.Contains(query))
                    {
                        results.Add(obj);
                    }
                }
            }
            else
            {
                // Find all UI elements
                Canvas[] canvases = GameObject.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
                foreach (var canvas in canvases)
                {
                    results.AddRange(canvas.GetComponentsInChildren<Transform>().Select(t => t.gameObject));
                }
            }

            JArray elements = new JArray();
            foreach (var obj in results.Take(50)) // Limit to prevent huge responses
            {
                elements.Add(GetElementInfo(obj));
            }

            return JObject.FromObject(new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = $"Found {results.Count} UI elements" + (results.Count > 50 ? " (showing first 50)" : "")
                    },
                    new
                    {
                        type = "text",
                        text = elements.ToString()
                    }
                },
                isError = false,
                elements = elements
            });
        }

        private JObject InspectElement(JObject parameters)
        {
            string path = parameters["path"]?.ToObject<string>();
            GameObject obj = GameObject.Find(path);

            if (obj == null)
            {
                return CreateErrorResponse($"GameObject not found: {path}");
            }

            JObject info = new JObject
            {
                ["name"] = obj.name,
                ["path"] = GetPath(obj),
                ["active"] = obj.activeSelf,
                ["layer"] = LayerMask.LayerToName(obj.layer),
                ["tag"] = obj.tag
            };

            // Transform info
            RectTransform rect = obj.GetComponent<RectTransform>();
            if (rect != null)
            {
                info["rect"] = JObject.FromObject(new
                {
                    position = new { x = rect.anchoredPosition.x, y = rect.anchoredPosition.y },
                    size = new { width = rect.sizeDelta.x, height = rect.sizeDelta.y },
                    anchorMin = new { x = rect.anchorMin.x, y = rect.anchorMin.y },
                    anchorMax = new { x = rect.anchorMax.x, y = rect.anchorMax.y },
                    pivot = new { x = rect.pivot.x, y = rect.pivot.y },
                    rotation = rect.localEulerAngles.z,
                    scale = new { x = rect.localScale.x, y = rect.localScale.y, z = rect.localScale.z }
                });
            }

            // Components
            JArray components = new JArray();
            foreach (Component comp in obj.GetComponents<Component>())
            {
                if (comp == null) continue;

                JObject compInfo = new JObject
                {
                    ["type"] = comp.GetType().Name,
                    ["enabled"] = comp is Behaviour behaviour ? behaviour.enabled : true
                };

                // Get key properties for common UI components
                AddComponentProperties(comp, compInfo);
                components.Add(compInfo);
            }
            info["components"] = components;

            // Children
            if (obj.transform.childCount > 0)
            {
                JArray children = new JArray();
                for (int i = 0; i < obj.transform.childCount && i < 20; i++)
                {
                    Transform child = obj.transform.GetChild(i);
                    children.Add(child.name);
                }
                info["children"] = children;
                if (obj.transform.childCount > 20)
                {
                    info["childrenCount"] = obj.transform.childCount;
                }
            }

            return JObject.FromObject(new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = $"Inspecting: {obj.name}"
                    },
                    new
                    {
                        type = "text",
                        text = info.ToString()
                    }
                },
                isError = false,
                data = info
            });
        }

        private JObject SetProperty(JObject parameters)
        {
            string path = parameters["path"]?.ToObject<string>();
            string component = parameters["component"]?.ToObject<string>();
            string property = parameters["property"]?.ToObject<string>();
            JToken value = parameters["value"];

            GameObject obj = GameObject.Find(path);
            if (obj == null)
            {
                return CreateErrorResponse($"GameObject not found: {path}");
            }

            Component comp = null;
            if (!string.IsNullOrEmpty(component))
            {
                Type compType = GetUIComponentType(component);
                if (compType != null)
                {
                    comp = obj.GetComponent(compType);
                }
            }

            if (comp == null && !string.IsNullOrEmpty(component))
            {
                return CreateErrorResponse($"Component not found: {component}");
            }

            // Set GameObject properties
            if (comp == null || component == "GameObject")
            {
                switch (property)
                {
                    case "active":
                        obj.SetActive(value.ToObject<bool>());
                        break;
                    case "name":
                        obj.name = value.ToObject<string>();
                        break;
                    case "layer":
                        obj.layer = LayerMask.NameToLayer(value.ToObject<string>());
                        break;
                    case "tag":
                        obj.tag = value.ToObject<string>();
                        break;
                    default:
                        return CreateErrorResponse($"Unknown GameObject property: {property}");
                }
            }
            else
            {
                // Set component properties using reflection
                Type type = comp.GetType();
                PropertyInfo propInfo = type.GetProperty(property);
                FieldInfo fieldInfo = type.GetField(property);

                if (propInfo != null && propInfo.CanWrite)
                {
                    object convertedValue = ConvertValue(value, propInfo.PropertyType);
                    propInfo.SetValue(comp, convertedValue);
                }
                else if (fieldInfo != null)
                {
                    object convertedValue = ConvertValue(value, fieldInfo.FieldType);
                    fieldInfo.SetValue(comp, convertedValue);
                }
                else
                {
                    return CreateErrorResponse($"Property not found or not writable: {property}");
                }
            }

            EditorUtility.SetDirty(obj);

            return JObject.FromObject(new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = $"Set {component ?? "GameObject"}.{property} = {value} on {obj.name}"
                    }
                },
                isError = false
            });
        }

        private JObject InvokeMethod(JObject parameters)
        {
            string path = parameters["path"]?.ToObject<string>();
            string component = parameters["component"]?.ToObject<string>();
            string method = parameters["method"]?.ToObject<string>();
            JArray args = parameters["args"] as JArray;

            GameObject obj = GameObject.Find(path);
            if (obj == null)
            {
                return CreateErrorResponse($"GameObject not found: {path}");
            }

            Component comp = null;
            if (!string.IsNullOrEmpty(component))
            {
                Type compType = GetUIComponentType(component);
                if (compType != null)
                {
                    comp = obj.GetComponent(compType);
                }
            }

            if (comp == null)
            {
                return CreateErrorResponse($"Component not found: {component}");
            }

            Type type = comp.GetType();
            MethodInfo methodInfo = type.GetMethod(method);

            if (methodInfo == null)
            {
                return CreateErrorResponse($"Method not found: {method}");
            }

            try
            {
                object[] methodArgs = null;
                if (args != null && args.Count > 0)
                {
                    ParameterInfo[] paramInfos = methodInfo.GetParameters();
                    methodArgs = new object[args.Count];
                    for (int i = 0; i < args.Count; i++)
                    {
                        methodArgs[i] = ConvertValue(args[i], paramInfos[i].ParameterType);
                    }
                }

                object result = methodInfo.Invoke(comp, methodArgs);

                return JObject.FromObject(new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = $"Invoked {component}.{method} on {obj.name}" +
                                   (result != null ? $" - Result: {result}" : "")
                        }
                    },
                    isError = false,
                    result = result?.ToString()
                });
            }
            catch (Exception ex)
            {
                return CreateErrorResponse($"Method invocation failed: {ex.Message}");
            }
        }

        private JObject GetHierarchy(JObject parameters)
        {
            string root = parameters["root"]?.ToObject<string>();
            int maxDepth = parameters["depth"]?.ToObject<int>() ?? 3;

            GameObject rootObj = null;
            if (!string.IsNullOrEmpty(root))
            {
                rootObj = GameObject.Find(root);
                if (rootObj == null)
                {
                    return CreateErrorResponse($"Root object not found: {root}");
                }
            }

            JArray hierarchy = new JArray();

            if (rootObj != null)
            {
                hierarchy.Add(BuildHierarchy(rootObj, 0, maxDepth));
            }
            else
            {
                // Get all canvases as roots
                Canvas[] canvases = GameObject.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
                foreach (var canvas in canvases)
                {
                    hierarchy.Add(BuildHierarchy(canvas.gameObject, 0, maxDepth));
                }
            }

            return JObject.FromObject(new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = "UI Hierarchy:"
                    },
                    new
                    {
                        type = "text",
                        text = hierarchy.ToString()
                    }
                },
                isError = false,
                hierarchy = hierarchy
            });
        }

        private JObject CreateElement(JObject parameters)
        {
            string type = parameters["type"]?.ToObject<string>();
            string name = parameters["name"]?.ToObject<string>() ?? $"New{type}";
            string parent = parameters["parent"]?.ToObject<string>();

            GameObject parentObj = null;
            if (!string.IsNullOrEmpty(parent))
            {
                parentObj = GameObject.Find(parent);
                if (parentObj == null)
                {
                    return CreateErrorResponse($"Parent not found: {parent}");
                }
            }

            GameObject newObj = new GameObject(name);
            if (parentObj != null)
            {
                newObj.transform.SetParent(parentObj.transform, false);
            }

            // Add RectTransform for UI elements
            if (parentObj != null && parentObj.GetComponent<Canvas>() != null)
            {
                newObj.AddComponent<RectTransform>();
            }

            // Add requested component type
            if (!string.IsNullOrEmpty(type))
            {
                Type compType = GetUIComponentType(type);
                if (compType != null)
                {
                    newObj.AddComponent(compType);
                }
            }

            Undo.RegisterCreatedObjectUndo(newObj, $"Create {name}");
            Selection.activeGameObject = newObj;

            return JObject.FromObject(new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = $"Created {name} with {type ?? "no"} component"
                    }
                },
                isError = false,
                path = GetPath(newObj)
            });
        }

        private JObject DestroyElement(JObject parameters)
        {
            string path = parameters["path"]?.ToObject<string>();

            GameObject obj = GameObject.Find(path);
            if (obj == null)
            {
                return CreateErrorResponse($"GameObject not found: {path}");
            }

            string name = obj.name;
            Undo.DestroyObjectImmediate(obj);

            return JObject.FromObject(new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = $"Destroyed {name}"
                    }
                },
                isError = false
            });
        }

        // Helper methods

        private Type GetUIComponentType(string typeName)
        {
            // Try common UI components first
            Type type = Type.GetType($"UnityEngine.UI.{typeName}, UnityEngine.UI");
            if (type != null) return type;

            // Try UnityEngine components
            type = Type.GetType($"UnityEngine.{typeName}, UnityEngine");
            if (type != null) return type;

            // Try without namespace
            type = Type.GetType(typeName);
            if (type != null) return type;

            // Search all assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetTypes().FirstOrDefault(t => t.Name == typeName);
                if (type != null) return type;
            }

            return null;
        }

        private object ConvertValue(JToken value, Type targetType)
        {
            if (targetType == typeof(Vector2))
            {
                return new Vector2(
                    value["x"]?.ToObject<float>() ?? 0,
                    value["y"]?.ToObject<float>() ?? 0
                );
            }
            else if (targetType == typeof(Vector3))
            {
                return new Vector3(
                    value["x"]?.ToObject<float>() ?? 0,
                    value["y"]?.ToObject<float>() ?? 0,
                    value["z"]?.ToObject<float>() ?? 0
                );
            }
            else if (targetType == typeof(Color))
            {
                return new Color(
                    value["r"]?.ToObject<float>() ?? 0,
                    value["g"]?.ToObject<float>() ?? 0,
                    value["b"]?.ToObject<float>() ?? 0,
                    value["a"]?.ToObject<float>() ?? 1
                );
            }
            else
            {
                return value.ToObject(targetType);
            }
        }

        private void AddComponentProperties(Component comp, JObject info)
        {
            switch (comp)
            {
                case Text text:
                    info["text"] = text.text;
                    info["fontSize"] = text.fontSize;
                    info["color"] = ColorToHex(text.color);
                    info["alignment"] = text.alignment.ToString();
                    break;

                case Button button:
                    info["interactable"] = button.interactable;
                    break;

                case Image image:
                    info["color"] = ColorToHex(image.color);
                    info["sprite"] = image.sprite?.name;
                    info["type"] = image.type.ToString();
                    break;

                case Slider slider:
                    info["value"] = slider.value;
                    info["minValue"] = slider.minValue;
                    info["maxValue"] = slider.maxValue;
                    break;

                case Toggle toggle:
                    info["isOn"] = toggle.isOn;
                    info["interactable"] = toggle.interactable;
                    break;

                case InputField inputField:
                    info["text"] = inputField.text;
                    info["placeholder"] = inputField.placeholder?.GetComponent<Text>()?.text;
                    info["interactable"] = inputField.interactable;
                    break;

                case Dropdown dropdown:
                    info["value"] = dropdown.value;
                    info["options"] = new JArray(dropdown.options.Select(o => o.text));
                    break;

                case ScrollRect scrollRect:
                    info["horizontal"] = scrollRect.horizontal;
                    info["vertical"] = scrollRect.vertical;
                    info["normalizedPosition"] = JObject.FromObject(new { x = scrollRect.normalizedPosition.x, y = scrollRect.normalizedPosition.y });
                    break;
            }
        }

        private JObject GetElementInfo(GameObject obj)
        {
            JObject info = new JObject
            {
                ["name"] = obj.name,
                ["path"] = GetPath(obj),
                ["active"] = obj.activeSelf
            };

            // Add main UI component if present
            Component[] uiComponents = obj.GetComponents<Component>()
                .Where(c => c != null && c.GetType().Namespace == "UnityEngine.UI")
                .ToArray();

            if (uiComponents.Length > 0)
            {
                info["components"] = new JArray(uiComponents.Select(c => c.GetType().Name));
            }

            return info;
        }

        private JObject BuildHierarchy(GameObject obj, int depth, int maxDepth)
        {
            if (depth >= maxDepth) return null;

            JObject node = GetElementInfo(obj);

            if (obj.transform.childCount > 0)
            {
                JArray children = new JArray();
                for (int i = 0; i < obj.transform.childCount; i++)
                {
                    var child = BuildHierarchy(obj.transform.GetChild(i).gameObject, depth + 1, maxDepth);
                    if (child != null)
                    {
                        children.Add(child);
                    }
                }
                if (children.Count > 0)
                {
                    node["children"] = children;
                }
            }

            return node;
        }

        private string GetPath(GameObject obj)
        {
            string path = obj.name;
            Transform parent = obj.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private string ColorToHex(Color color)
        {
            return "#" + ColorUtility.ToHtmlStringRGBA(color);
        }

        private JObject CreateErrorResponse(string message)
        {
            return JObject.FromObject(new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = $"Error: {message}"
                    }
                },
                isError = true
            });
        }
    }
}