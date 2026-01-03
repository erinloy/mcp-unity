using System;
using System.Reflection;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for copying field values between components, including asset references like Sprites.
    /// This is essential for copying sprite references, materials, and other Unity Object references.
    /// </summary>
    public class CopyFieldValueTool : McpToolBase
    {
        public CopyFieldValueTool()
        {
            Name = "copy_field_value";
            Description = "Copies a field value from one component to another. Useful for copying sprites, materials, and other asset references.";

            InputSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["sourcePath"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Hierarchy path to the source GameObject"
                    },
                    ["sourceComponent"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name of the component type on the source GameObject"
                    },
                    ["sourceField"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name of the field to copy from"
                    },
                    ["targetPath"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Hierarchy path to the target GameObject"
                    },
                    ["targetComponent"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name of the component type on the target GameObject"
                    },
                    ["targetField"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name of the field to copy to (defaults to same as sourceField)"
                    }
                },
                ["required"] = new JArray { "sourcePath", "sourceComponent", "sourceField", "targetPath", "targetComponent" }
            };
        }

        public override JObject Execute(JObject parameters)
        {
            string sourcePath = parameters["sourcePath"]?.ToObject<string>();
            string sourceComponentName = parameters["sourceComponent"]?.ToObject<string>();
            string sourceFieldName = parameters["sourceField"]?.ToObject<string>();
            string targetPath = parameters["targetPath"]?.ToObject<string>();
            string targetComponentName = parameters["targetComponent"]?.ToObject<string>();
            string targetFieldName = parameters["targetField"]?.ToObject<string>() ?? sourceFieldName;

            // Validate
            if (string.IsNullOrEmpty(sourcePath))
                return McpUnitySocketHandler.CreateErrorResponse("'sourcePath' is required", "validation_error");
            if (string.IsNullOrEmpty(sourceComponentName))
                return McpUnitySocketHandler.CreateErrorResponse("'sourceComponent' is required", "validation_error");
            if (string.IsNullOrEmpty(sourceFieldName))
                return McpUnitySocketHandler.CreateErrorResponse("'sourceField' is required", "validation_error");
            if (string.IsNullOrEmpty(targetPath))
                return McpUnitySocketHandler.CreateErrorResponse("'targetPath' is required", "validation_error");
            if (string.IsNullOrEmpty(targetComponentName))
                return McpUnitySocketHandler.CreateErrorResponse("'targetComponent' is required", "validation_error");

            // Find source GameObject
            GameObject sourceGO = FindGameObjectByPath(sourcePath);
            if (sourceGO == null)
                return McpUnitySocketHandler.CreateErrorResponse($"Source GameObject not found: {sourcePath}", "not_found_error");

            // Find source component
            Component sourceComponent = FindComponent(sourceGO, sourceComponentName);
            if (sourceComponent == null)
                return McpUnitySocketHandler.CreateErrorResponse($"Source component '{sourceComponentName}' not found on: {sourcePath}", "not_found_error");

            // Find target GameObject
            GameObject targetGO = FindGameObjectByPath(targetPath);
            if (targetGO == null)
                return McpUnitySocketHandler.CreateErrorResponse($"Target GameObject not found: {targetPath}", "not_found_error");

            // Find target component
            Component targetComponent = FindComponent(targetGO, targetComponentName);
            if (targetComponent == null)
                return McpUnitySocketHandler.CreateErrorResponse($"Target component '{targetComponentName}' not found on: {targetPath}", "not_found_error");

            // Get source field value
            object sourceValue = GetFieldOrPropertyValue(sourceComponent, sourceFieldName, out string getError);
            if (getError != null)
                return McpUnitySocketHandler.CreateErrorResponse(getError, "field_error");

            // Set target field value
            bool success = SetFieldOrPropertyValue(targetComponent, targetFieldName, sourceValue, out string setError);
            if (!success)
                return McpUnitySocketHandler.CreateErrorResponse(setError, "field_error");

            // Mark dirty
            EditorUtility.SetDirty(targetComponent);
            if (PrefabUtility.IsPartOfAnyPrefab(targetGO))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(targetComponent);
            }

            string valueDesc = sourceValue != null ? sourceValue.ToString() : "null";
            McpLogger.LogInfo($"[MCP Unity] Copied field: {sourcePath}.{sourceComponentName}.{sourceFieldName} -> {targetPath}.{targetComponentName}.{targetFieldName} (value: {valueDesc})");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Successfully copied {sourceFieldName} from {sourcePath}.{sourceComponentName} to {targetPath}.{targetComponentName}.{targetFieldName}"
            };
        }

        private object GetFieldOrPropertyValue(Component component, string name, out string error)
        {
            error = null;
            Type type = component.GetType();

            // Try field first
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
                return field.GetValue(component);

            // Try property
            PropertyInfo prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
                return prop.GetValue(component);

            error = $"Field or property '{name}' not found on component '{type.Name}'";
            return null;
        }

        private bool SetFieldOrPropertyValue(Component component, string name, object value, out string error)
        {
            error = null;
            Type type = component.GetType();

            Undo.RecordObject(component, $"Copy field {name}");

            // Try field first
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                if (value != null && !field.FieldType.IsAssignableFrom(value.GetType()))
                {
                    error = $"Type mismatch: Cannot assign {value.GetType().Name} to field {name} of type {field.FieldType.Name}";
                    return false;
                }
                field.SetValue(component, value);
                return true;
            }

            // Try property
            PropertyInfo prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                if (value != null && !prop.PropertyType.IsAssignableFrom(value.GetType()))
                {
                    error = $"Type mismatch: Cannot assign {value.GetType().Name} to property {name} of type {prop.PropertyType.Name}";
                    return false;
                }
                prop.SetValue(component, value);
                return true;
            }

            error = $"Writable field or property '{name}' not found on component '{type.Name}'";
            return false;
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

        private Component FindComponent(GameObject go, string componentName)
        {
            Component comp = go.GetComponent(componentName);
            if (comp != null) return comp;

            Type compType = FindComponentType(componentName);
            if (compType != null)
                return go.GetComponent(compType);

            return null;
        }

        private Type FindComponentType(string componentName)
        {
            string[] namespaces = { "UnityEngine", "UnityEngine.UI", "TMPro", "" };

            foreach (string ns in namespaces)
            {
                string fullName = string.IsNullOrEmpty(ns) ? componentName : $"{ns}.{componentName}";
                Type type = Type.GetType(fullName + ", UnityEngine");
                if (type != null && typeof(Component).IsAssignableFrom(type)) return type;

                type = Type.GetType(fullName + ", UnityEngine.UI");
                if (type != null && typeof(Component).IsAssignableFrom(type)) return type;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type t in assembly.GetTypes())
                    {
                        if (t.Name == componentName && typeof(Component).IsAssignableFrom(t))
                            return t;
                    }
                }
                catch { continue; }
            }

            return null;
        }
    }
}
