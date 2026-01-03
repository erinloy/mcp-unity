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
    /// Tool for setting object references on serialized fields.
    /// This enables wiring up references between components (e.g., setting a Toggle reference on a MonoBehaviour).
    /// </summary>
    public class SetObjectReferenceTool : McpToolBase
    {
        public SetObjectReferenceTool()
        {
            Name = "set_object_reference";
            Description = "Sets an object reference on a serialized field of a component. Use this to wire up references between GameObjects/Components.";

            InputSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["targetPath"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Hierarchy path to the GameObject containing the target component"
                    },
                    ["targetInstanceId"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Instance ID of target GameObject (alternative to targetPath)"
                    },
                    ["targetComponent"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name of the component type on the target GameObject"
                    },
                    ["fieldName"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name of the serialized field to set"
                    },
                    ["referencePath"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Hierarchy path to the GameObject to reference (or the GameObject containing the component to reference)"
                    },
                    ["referenceInstanceId"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Instance ID of reference GameObject (alternative to referencePath)"
                    },
                    ["referenceComponent"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional: Component type name if referencing a specific component rather than the GameObject itself"
                    }
                },
                ["required"] = new JArray { "targetComponent", "fieldName" } // targetPath/targetInstanceId and referencePath/referenceInstanceId must be provided
            };
        }

        public override JObject Execute(JObject parameters)
        {
            string targetPath = parameters["targetPath"]?.ToObject<string>();
            int? targetInstanceId = parameters["targetInstanceId"]?.ToObject<int?>();
            string targetComponentName = parameters["targetComponent"]?.ToObject<string>();
            string fieldName = parameters["fieldName"]?.ToObject<string>();
            string referencePath = parameters["referencePath"]?.ToObject<string>();
            int? referenceInstanceId = parameters["referenceInstanceId"]?.ToObject<int?>();
            string referenceComponentName = parameters["referenceComponent"]?.ToObject<string>();

            // Validate required parameters
            if (string.IsNullOrEmpty(targetPath) && !targetInstanceId.HasValue)
                return McpUnitySocketHandler.CreateErrorResponse("'targetPath' or 'targetInstanceId' is required", "validation_error");
            if (string.IsNullOrEmpty(targetComponentName))
                return McpUnitySocketHandler.CreateErrorResponse("'targetComponent' is required", "validation_error");
            if (string.IsNullOrEmpty(fieldName))
                return McpUnitySocketHandler.CreateErrorResponse("'fieldName' is required", "validation_error");
            if (string.IsNullOrEmpty(referencePath) && !referenceInstanceId.HasValue)
                return McpUnitySocketHandler.CreateErrorResponse("'referencePath' or 'referenceInstanceId' is required", "validation_error");

            // Find target GameObject by instance ID or path
            GameObject targetGO = null;
            if (targetInstanceId.HasValue)
            {
                targetGO = EditorUtility.InstanceIDToObject(targetInstanceId.Value) as GameObject;
            }
            else
            {
                targetGO = FindGameObjectByPath(targetPath);
            }

            if (targetGO == null)
                return McpUnitySocketHandler.CreateErrorResponse($"Target GameObject not found: {(targetInstanceId.HasValue ? $"instanceId={targetInstanceId}" : $"path={targetPath}")}", "not_found_error");

            // Find target component
            Component targetComponent = targetGO.GetComponent(targetComponentName);
            if (targetComponent == null)
            {
                // Try to find by searching all assemblies
                Type componentType = FindComponentType(targetComponentName);
                if (componentType != null)
                    targetComponent = targetGO.GetComponent(componentType);
            }

            if (targetComponent == null)
                return McpUnitySocketHandler.CreateErrorResponse($"Component '{targetComponentName}' not found on GameObject at path: {targetPath}", "not_found_error");

            // Find reference GameObject by instance ID or path
            GameObject referenceGO = null;
            if (referenceInstanceId.HasValue)
            {
                referenceGO = EditorUtility.InstanceIDToObject(referenceInstanceId.Value) as GameObject;
            }
            else
            {
                referenceGO = FindGameObjectByPath(referencePath);
            }

            if (referenceGO == null)
                return McpUnitySocketHandler.CreateErrorResponse($"Reference GameObject not found: {(referenceInstanceId.HasValue ? $"instanceId={referenceInstanceId}" : $"path={referencePath}")}", "not_found_error");

            // Determine what to reference (GameObject or specific Component)
            UnityEngine.Object referenceObject;
            if (!string.IsNullOrEmpty(referenceComponentName))
            {
                Component refComponent = referenceGO.GetComponent(referenceComponentName);
                if (refComponent == null)
                {
                    Type refComponentType = FindComponentType(referenceComponentName);
                    if (refComponentType != null)
                        refComponent = referenceGO.GetComponent(refComponentType);
                }

                if (refComponent == null)
                    return McpUnitySocketHandler.CreateErrorResponse($"Reference component '{referenceComponentName}' not found on GameObject at path: {referencePath}", "not_found_error");

                referenceObject = refComponent;
            }
            else
            {
                referenceObject = referenceGO;
            }

            // Find the field on the target component
            Type targetType = targetComponent.GetType();
            FieldInfo fieldInfo = targetType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (fieldInfo == null)
            {
                // Try property
                PropertyInfo propInfo = targetType.GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (propInfo != null && propInfo.CanWrite)
                {
                    // Check type compatibility
                    if (!propInfo.PropertyType.IsAssignableFrom(referenceObject.GetType()))
                        return McpUnitySocketHandler.CreateErrorResponse($"Type mismatch: Cannot assign {referenceObject.GetType().Name} to property {fieldName} of type {propInfo.PropertyType.Name}", "type_error");

                    Undo.RecordObject(targetComponent, $"Set {fieldName} reference");
                    propInfo.SetValue(targetComponent, referenceObject);
                    EditorUtility.SetDirty(targetComponent);

                    return CreateSuccessResponse(targetPath, targetComponentName, fieldName, referencePath, referenceComponentName);
                }

                return McpUnitySocketHandler.CreateErrorResponse($"Field or writable property '{fieldName}' not found on component '{targetComponentName}'", "not_found_error");
            }

            // Check type compatibility
            if (!fieldInfo.FieldType.IsAssignableFrom(referenceObject.GetType()))
                return McpUnitySocketHandler.CreateErrorResponse($"Type mismatch: Cannot assign {referenceObject.GetType().Name} to field {fieldName} of type {fieldInfo.FieldType.Name}", "type_error");

            // Set the reference
            Undo.RecordObject(targetComponent, $"Set {fieldName} reference");
            fieldInfo.SetValue(targetComponent, referenceObject);
            EditorUtility.SetDirty(targetComponent);

            // Handle prefab modifications
            if (PrefabUtility.IsPartOfAnyPrefab(targetGO))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(targetComponent);
            }

            McpLogger.LogInfo($"[MCP Unity] Set reference: {targetPath}.{targetComponentName}.{fieldName} = {referencePath}" +
                (string.IsNullOrEmpty(referenceComponentName) ? "" : $".{referenceComponentName}"));

            return CreateSuccessResponse(targetPath, targetComponentName, fieldName, referencePath, referenceComponentName);
        }

        private JObject CreateSuccessResponse(string targetPath, string targetComponent, string fieldName, string referencePath, string referenceComponent)
        {
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Successfully set {targetPath}.{targetComponent}.{fieldName} to reference {referencePath}" +
                    (string.IsNullOrEmpty(referenceComponent) ? "" : $".{referenceComponent}")
            };
        }

        private GameObject FindGameObjectByPath(string path)
        {
            // Try direct find first
            GameObject go = GameObject.Find(path);
            if (go != null) return go;

            // Try hierarchy traversal
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

        private Type FindComponentType(string componentName)
        {
            // Try common Unity namespaces
            string[] namespaces = { "UnityEngine", "UnityEngine.UI", "TMPro", "" };

            foreach (string ns in namespaces)
            {
                string fullName = string.IsNullOrEmpty(ns) ? componentName : $"{ns}.{componentName}";
                Type type = Type.GetType(fullName + ", UnityEngine");
                if (type != null && typeof(Component).IsAssignableFrom(type)) return type;

                type = Type.GetType(fullName + ", UnityEngine.UI");
                if (type != null && typeof(Component).IsAssignableFrom(type)) return type;
            }

            // Search all assemblies
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
