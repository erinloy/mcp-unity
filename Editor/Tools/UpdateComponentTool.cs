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
    /// Tool for updating component data in the Unity Editor
    /// </summary>
    public class UpdateComponentTool : McpToolBase
    {
        public UpdateComponentTool()
        {
            Name = "update_component";
            Description = "Updates component fields on a GameObject or adds it to the GameObject if it does not contain the component";
        }
        
        /// <summary>
        /// Execute the UpdateComponent tool with the provided parameters synchronously
        /// </summary>
        /// <param name="parameters">Tool parameters as a JObject</param>
        public override JObject Execute(JObject parameters)
        {
            // Extract parameters
            int? instanceId = parameters["instanceId"]?.ToObject<int?>();
            string objectPath = parameters["objectPath"]?.ToObject<string>();
            string componentName = parameters["componentName"]?.ToObject<string>();
            JObject componentData = parameters["componentData"] as JObject;
            
            // Validate parameters - require either instanceId or objectPath
            if (!instanceId.HasValue && string.IsNullOrEmpty(objectPath))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Either 'instanceId' or 'objectPath' must be provided", 
                    "validation_error"
                );
            }
            
            if (string.IsNullOrEmpty(componentName))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'componentName' not provided", 
                    "validation_error"
                );
            }
            
            // Find the GameObject by instance ID or path
            GameObject gameObject = null;
            string identifier = "unknown";
            
            if (instanceId.HasValue)
            {
                gameObject = UnityObjectId.ObjectFromId(instanceId.Value) as GameObject;
                identifier = $"ID {instanceId.Value}";
            }
            else
            {
                // Find by path
                gameObject = GameObject.Find(objectPath);
                identifier = $"path '{objectPath}'";
                
                if (gameObject == null)
                {
                    // Try to find using the Unity Scene hierarchy path
                    gameObject = FindGameObjectByPath(objectPath);
                }
            }
                    
            if (gameObject == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject with path '{objectPath}' or instance ID {instanceId} not found", 
                    "not_found_error"
                );
            }
            
            McpLogger.LogInfo($"[MCP Unity] Updating component '{componentName}' on GameObject '{gameObject.name}' (found by {identifier})");
            
            // Try to find the component by name
            Component component = gameObject.GetComponent(componentName);
            
            // If component not found, try to add it
            if (component == null)
            {
                Type componentType = FindComponentType(componentName);
                if (componentType == null)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Component type '{componentName}' not found in Unity", 
                        "component_error"
                    );
                }
                
                component = Undo.AddComponent(gameObject, componentType);

                // Ensure changes are saved
                EditorUtility.SetDirty(gameObject);
                if (PrefabUtility.IsPartOfAnyPrefab(gameObject))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }
                
                McpLogger.LogInfo($"[MCP Unity] Added component '{componentName}' to GameObject '{gameObject.name}'");
            }
            // Update component fields
            if (componentData != null && componentData.Count > 0)
            {
                bool success = UpdateComponentData(component, componentData, out string errorMessage);
                // If update failed, return error
                if (!success)
                {
                    return McpUnitySocketHandler.CreateErrorResponse(errorMessage, "update_error");
                }

                // Ensure field changes are saved
                EditorUtility.SetDirty(gameObject);
                if (PrefabUtility.IsPartOfAnyPrefab(gameObject))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }

            }

            // Create the response
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Successfully updated component '{componentName}' on GameObject '{gameObject.name}'"
            };
        }
        
        /// <summary>
        /// Find a GameObject by its hierarchy path
        /// </summary>
        /// <param name="path">The path to the GameObject (e.g. "Canvas/Panel/Button")</param>
        /// <returns>The GameObject if found, null otherwise</returns>
        private GameObject FindGameObjectByPath(string path)
        {
            // Split the path by '/'
            string[] pathParts = path.Split('/');
            GameObject[] rootGameObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            
            // If the path is empty, return null
            if (pathParts.Length == 0)
            {
                return null;
            }
            
            // Search through all root GameObjects in all scenes
            foreach (GameObject rootObj in rootGameObjects)
            {
                if (rootObj.name == pathParts[0])
                {
                    // Found the root object, now traverse down the path
                    GameObject current = rootObj;
                    
                    // Start from index 1 since we've already matched the root
                    for (int i = 1; i < pathParts.Length; i++)
                    {
                        Transform child = current.transform.Find(pathParts[i]);
                        if (child == null)
                        {
                            // Path segment not found
                            return null;
                        }
                        
                        // Move to the next level
                        current = child.gameObject;
                    }
                    
                    // If we got here, we found the full path
                    return current;
                }
            }
            
            // Not found
            return null;
        }
        
        /// <summary>
        /// Find a component type by name
        /// </summary>
        /// <param name="componentName">The name of the component type</param>
        /// <returns>The component type, or null if not found</returns>
        private Type FindComponentType(string componentName)
        {
            // First try direct match
            Type type = Type.GetType(componentName);
            if (type != null && typeof(Component).IsAssignableFrom(type))
            {
                return type;
            }
            
            // Try common Unity namespaces
            string[] commonNamespaces = new string[] 
            {
                "UnityEngine",
                "UnityEngine.UI",
                "UnityEngine.EventSystems",
                "UnityEngine.Animations",
                "UnityEngine.Rendering",
                "TMPro"
            };
            
            foreach (string ns in commonNamespaces)
            {
                type = Type.GetType($"{ns}.{componentName}, UnityEngine");
                if (type != null && typeof(Component).IsAssignableFrom(type))
                {
                    return type;
                }
            }
            
            // Try assemblies search
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type t in assembly.GetTypes())
                    {
                        if (t.Name == componentName && typeof(Component).IsAssignableFrom(t))
                        {
                            return t;
                        }
                    }
                }
                catch (Exception)
                {
                    // Some assemblies might throw exceptions when getting types
                    continue;
                }
            }
            
            return null;
        }

        private FieldInfo GetFieldRecursive(Type type, string fieldName)
        {
            while (type != null && type != typeof(object))
            {
                FieldInfo field = type.GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field;
                }

                type = type.BaseType;
            }

            return null;
        }

        private PropertyInfo GetPropertyRecursive(Type type, string propertyName)
        {
            while (type != null && type != typeof(object))
            {
                PropertyInfo prop = type.GetProperty(propertyName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (prop != null)
                {
                    return prop;
                }

                type = type.BaseType;
            }

            return null;
        }

        /// <summary>
        /// Update component data based on the provided JObject
        /// Uses SerializedObject API as primary method (handles base class fields and nested paths),
        /// with reflection as fallback for non-serialized properties.
        /// </summary>
        /// <param name="component">The component to update</param>
        /// <param name="componentData">The data to apply to the component</param>
        /// <param name="errorMessage">Error message if any fields failed to update</param>
        /// <returns>True if all fields were updated successfully</returns>
        private bool UpdateComponentData(Component component, JObject componentData, out string errorMessage)
        {
            errorMessage = "";
            
            if (component == null || componentData == null)
            {
                errorMessage = "Component or component data is null";
                return false;
            }

            Type componentType = component.GetType();
            bool fullSuccess = true;

            // Record object for undo
            Undo.RecordObject(component, $"Update {componentType.Name} fields");

            SerializedObject serializedObject = new SerializedObject(component);

            foreach (var property in componentData.Properties())
            {
                string fieldName = property.Name;
                JToken fieldValue = property.Value;

                if (string.IsNullOrEmpty(fieldName))
                {
                    continue;
                }

                SerializedProperty serializedProperty = serializedObject.FindProperty(fieldName);
                if (serializedProperty != null)
                {
                    if (!TrySetSerializedPropertyValue(serializedProperty, fieldValue, out string setError))
                    {
                        fullSuccess = false;
                        errorMessage = setError;
                        McpLogger.LogWarning($"[MCP Unity] {errorMessage}");
                        break;
                    }

                    continue;
                }

                FieldInfo fieldInfo = GetFieldRecursive(componentType, fieldName);
                if (fieldInfo != null)
                {
                    if (!TryConvertJTokenToValue(fieldValue, fieldInfo.FieldType, out object value, out string convertError))
                    {
                        fullSuccess = false;
                        errorMessage = $"Could not convert field '{fieldName}' on component '{componentType.Name}': {convertError}";
                        McpLogger.LogWarning($"[MCP Unity] {errorMessage}");
                        break;
                    }

                    fieldInfo.SetValue(component, value);
                    continue;
                }

                PropertyInfo propertyInfo = GetPropertyRecursive(componentType, fieldName);
                if (propertyInfo != null)
                {
                    if (!propertyInfo.CanWrite)
                    {
                        fullSuccess = false;
                        errorMessage = $"Property '{fieldName}' on component '{componentType.Name}' is read-only";
                        McpLogger.LogWarning($"[MCP Unity] {errorMessage}");
                        break;
                    }

                    if (!TryConvertJTokenToValue(fieldValue, propertyInfo.PropertyType, out object value, out string convertError))
                    {
                        fullSuccess = false;
                        errorMessage = $"Could not convert property '{fieldName}' on component '{componentType.Name}': {convertError}";
                        McpLogger.LogWarning($"[MCP Unity] {errorMessage}");
                        break;
                    }

                    propertyInfo.SetValue(component, value);
                    continue;
                }

                fullSuccess = false;
                errorMessage = $"Field or Property with name '{fieldName}' not found on component '{componentType.Name}'";
                McpLogger.LogWarning($"[MCP Unity] {errorMessage}");
                break;
            }

            if (fullSuccess)
            {
                serializedObject.ApplyModifiedProperties();
            }

            return fullSuccess;
        }

        private bool TrySetSerializedPropertyValue(SerializedProperty prop, JToken value, out string errorMessage)
        {
            errorMessage = "";

            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        prop.intValue = value.ToObject<int>();
                        return true;
                    case SerializedPropertyType.Boolean:
                        prop.boolValue = value.ToObject<bool>();
                        return true;
                    case SerializedPropertyType.Float:
                        prop.floatValue = value.ToObject<float>();
                        return true;
                    case SerializedPropertyType.String:
                        prop.stringValue = value.Type == JTokenType.Null ? null : value.ToObject<string>();
                        return true;
                    case SerializedPropertyType.Color:
                        if (!TryReadColor(value, out Color color, out errorMessage)) return false;
                        prop.colorValue = color;
                        return true;
                    case SerializedPropertyType.Vector2:
                        if (!TryReadVector2(value, out Vector2 vector2, out errorMessage)) return false;
                        prop.vector2Value = vector2;
                        return true;
                    case SerializedPropertyType.Vector3:
                        if (!TryReadVector3(value, out Vector3 vector3, out errorMessage)) return false;
                        prop.vector3Value = vector3;
                        return true;
                    case SerializedPropertyType.Vector4:
                        if (!TryReadVector4(value, out Vector4 vector4, out errorMessage)) return false;
                        prop.vector4Value = vector4;
                        return true;
                    case SerializedPropertyType.Quaternion:
                        if (!TryReadQuaternion(value, out Quaternion quaternion, out errorMessage)) return false;
                        prop.quaternionValue = quaternion;
                        return true;
                    case SerializedPropertyType.Rect:
                        if (!TryReadRect(value, out Rect rect, out errorMessage)) return false;
                        prop.rectValue = rect;
                        return true;
                    case SerializedPropertyType.Bounds:
                        if (!TryReadBounds(value, out Bounds bounds, out errorMessage)) return false;
                        prop.boundsValue = bounds;
                        return true;
                    case SerializedPropertyType.Enum:
                        return TrySetEnumProperty(prop, value, out errorMessage);
                    case SerializedPropertyType.ObjectReference:
                        if (!TryLoadObjectReference(value, typeof(UnityEngine.Object), out UnityEngine.Object asset, out errorMessage))
                        {
                            return false;
                        }

                        prop.objectReferenceValue = asset;
                        return true;
                    case SerializedPropertyType.LayerMask:
                        prop.intValue = value.ToObject<int>();
                        return true;
                    case SerializedPropertyType.Vector2Int:
                        if (!TryReadVector2Int(value, out Vector2Int vector2Int, out errorMessage)) return false;
                        prop.vector2IntValue = vector2Int;
                        return true;
                    case SerializedPropertyType.Vector3Int:
                        if (!TryReadVector3Int(value, out Vector3Int vector3Int, out errorMessage)) return false;
                        prop.vector3IntValue = vector3Int;
                        return true;
                    case SerializedPropertyType.RectInt:
                        if (!TryReadRectInt(value, out RectInt rectInt, out errorMessage)) return false;
                        prop.rectIntValue = rectInt;
                        return true;
                    case SerializedPropertyType.BoundsInt:
                        if (!TryReadBoundsInt(value, out BoundsInt boundsInt, out errorMessage)) return false;
                        prop.boundsIntValue = boundsInt;
                        return true;
                    case SerializedPropertyType.Generic:
                        return TrySetGenericProperty(prop, value, out errorMessage);
                    case SerializedPropertyType.ArraySize:
                        prop.intValue = value.ToObject<int>();
                        return true;
                    default:
                        errorMessage = $"Unsupported property type '{prop.propertyType}' for '{prop.propertyPath}'";
                        return false;
                }
            }
            catch (Exception ex)
            {
                errorMessage = $"Could not set '{prop.propertyPath}' ({prop.propertyType}): {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Convert a JToken to a value of the specified type
        /// </summary>
        /// <param name="token">The JToken to convert</param>
        /// <param name="targetType">The target type to convert to</param>
        /// <returns>The converted value</returns>
        private bool TryConvertJTokenToValue(JToken token, Type targetType, out object value, out string errorMessage)
        {
            value = null;
            errorMessage = "";

            if (token == null || token.Type == JTokenType.Null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                {
                    errorMessage = $"Cannot assign null to value type '{targetType.Name}'";
                    return false;
                }

                return true;
            }

            // Handle Unity Vector types
            if (targetType == typeof(Vector2) && token.Type == JTokenType.Object)
            {
                bool success = TryReadVector2(token, out Vector2 vector, out errorMessage);
                value = vector;
                return success;
            }

            if (targetType == typeof(Vector3) && token.Type == JTokenType.Object)
            {
                bool success = TryReadVector3(token, out Vector3 vector, out errorMessage);
                value = vector;
                return success;
            }

            if (targetType == typeof(Vector4) && token.Type == JTokenType.Object)
            {
                bool success = TryReadVector4(token, out Vector4 vector, out errorMessage);
                value = vector;
                return success;
            }

            if (targetType == typeof(Quaternion) && token.Type == JTokenType.Object)
            {
                bool success = TryReadQuaternion(token, out Quaternion quaternion, out errorMessage);
                value = quaternion;
                return success;
            }

            if (targetType == typeof(Color) && token.Type == JTokenType.Object)
            {
                bool success = TryReadColor(token, out Color color, out errorMessage);
                value = color;
                return success;
            }

            if (targetType == typeof(Bounds) && token.Type == JTokenType.Object)
            {
                bool success = TryReadBounds(token, out Bounds bounds, out errorMessage);
                value = bounds;
                return success;
            }

            if (targetType == typeof(Rect) && token.Type == JTokenType.Object)
            {
                bool success = TryReadRect(token, out Rect rect, out errorMessage);
                value = rect;
                return success;
            }

            // Handle UnityEngine.Object types (assets) by path or GUID
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                bool success = TryLoadObjectReference(token, targetType, out UnityEngine.Object asset, out errorMessage);
                value = asset;
                return success;
            }

            // Handle enum types
            if (targetType.IsEnum)
            {
                if (token.Type == JTokenType.String)
                {
                    string enumName = token.ToObject<string>();
                    if (Enum.TryParse(targetType, enumName, true, out object result))
                    {
                        value = result;
                        return true;
                    }

                    if (int.TryParse(enumName, out int enumValue))
                    {
                        value = Enum.ToObject(targetType, enumValue);
                        return true;
                    }

                    errorMessage = $"'{enumName}' is not a valid value for enum '{targetType.Name}'";
                    return false;
                }

                if (token.Type == JTokenType.Integer)
                {
                    value = Enum.ToObject(targetType, token.ToObject<int>());
                    return true;
                }

                errorMessage = $"Expected string or integer for enum '{targetType.Name}'";
                return false;
            }

            try
            {
                value = token.ToObject(targetType);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Error converting value to type {targetType.Name}: {ex.Message}";
                return false;
            }
        }

        private bool TrySetGenericProperty(SerializedProperty prop, JToken value, out string errorMessage)
        {
            errorMessage = "";

            if (value.Type != JTokenType.Object)
            {
                errorMessage = $"Expected object value for '{prop.propertyPath}'";
                return false;
            }

            foreach (var child in ((JObject)value).Properties())
            {
                SerializedProperty childProp = prop.FindPropertyRelative(child.Name);
                if (childProp == null)
                {
                    errorMessage = $"Nested property '{child.Name}' not found under '{prop.propertyPath}'";
                    return false;
                }

                if (!TrySetSerializedPropertyValue(childProp, child.Value, out errorMessage))
                {
                    return false;
                }
            }

            return true;
        }

        private bool TrySetEnumProperty(SerializedProperty prop, JToken value, out string errorMessage)
        {
            errorMessage = "";

            if (value.Type == JTokenType.String)
            {
                string enumValue = value.ToObject<string>();
                string[] enumNames = prop.enumNames;

                for (int i = 0; i < enumNames.Length; i++)
                {
                    if (string.Equals(enumNames[i], enumValue, StringComparison.OrdinalIgnoreCase))
                    {
                        prop.enumValueIndex = i;
                        return true;
                    }
                }

                errorMessage = $"'{enumValue}' is not a valid value for '{prop.propertyPath}'";
                return false;
            }

            if (value.Type == JTokenType.Integer)
            {
                int index = value.ToObject<int>();
                if (index < 0 || index >= prop.enumNames.Length)
                {
                    errorMessage = $"Enum index {index} is out of range for '{prop.propertyPath}'";
                    return false;
                }

                prop.enumValueIndex = index;
                return true;
            }

            errorMessage = $"Expected string or integer enum value for '{prop.propertyPath}'";
            return false;
        }

        private bool TryLoadObjectReference(JToken token, Type targetType, out UnityEngine.Object asset, out string errorMessage)
        {
            asset = null;
            errorMessage = "";

            if (token == null || token.Type == JTokenType.Null)
            {
                return true;
            }

            string assetPath = null;

            if (token.Type == JTokenType.String)
            {
                string input = token.ToObject<string>();
                if (string.IsNullOrEmpty(input))
                {
                    return true;
                }

                if (input.StartsWith("Assets/") || input.StartsWith("Packages/"))
                {
                    assetPath = input;
                }
                else
                {
                    string[] guids = AssetDatabase.FindAssets($"{input} t:{targetType.Name}");
                    if (guids.Length == 0)
                    {
                        guids = AssetDatabase.FindAssets(input);
                    }

                    if (guids.Length > 0)
                    {
                        assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                    }
                }
            }
            else if (token.Type == JTokenType.Object)
            {
                JObject obj = (JObject)token;
                string guid = obj["guid"]?.ToObject<string>();
                string path = obj["path"]?.ToObject<string>();

                if (!string.IsNullOrEmpty(guid))
                {
                    assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        errorMessage = $"Could not find asset with GUID '{guid}'";
                        return false;
                    }
                }
                else
                {
                    assetPath = path;
                }
            }
            else
            {
                errorMessage = "Object references must be null, an asset path, an asset name, or an object with guid/path";
                return false;
            }

            if (string.IsNullOrEmpty(assetPath))
            {
                errorMessage = $"Could not find asset '{token}'";
                return false;
            }

            asset = AssetDatabase.LoadAssetAtPath(assetPath, targetType);
            if (asset == null)
            {
                errorMessage = $"Could not find asset at path '{assetPath}'";
                return false;
            }

            return true;
        }

        private static bool TryReadObject(JToken value, string expectedType, out JObject obj, out string errorMessage)
        {
            obj = value as JObject;
            errorMessage = "";

            if (obj != null)
            {
                return true;
            }

            errorMessage = $"Expected object value for {expectedType}";
            return false;
        }

        private static bool TryReadVector2(JToken value, out Vector2 vector, out string errorMessage)
        {
            vector = Vector2.zero;
            if (!TryReadObject(value, "Vector2", out JObject obj, out errorMessage)) return false;
            vector = new Vector2(obj["x"]?.ToObject<float>() ?? 0f, obj["y"]?.ToObject<float>() ?? 0f);
            return true;
        }

        private static bool TryReadVector3(JToken value, out Vector3 vector, out string errorMessage)
        {
            vector = Vector3.zero;
            if (!TryReadObject(value, "Vector3", out JObject obj, out errorMessage)) return false;
            vector = new Vector3(obj["x"]?.ToObject<float>() ?? 0f, obj["y"]?.ToObject<float>() ?? 0f, obj["z"]?.ToObject<float>() ?? 0f);
            return true;
        }

        private static bool TryReadVector4(JToken value, out Vector4 vector, out string errorMessage)
        {
            vector = Vector4.zero;
            if (!TryReadObject(value, "Vector4", out JObject obj, out errorMessage)) return false;
            vector = new Vector4(obj["x"]?.ToObject<float>() ?? 0f, obj["y"]?.ToObject<float>() ?? 0f, obj["z"]?.ToObject<float>() ?? 0f, obj["w"]?.ToObject<float>() ?? 0f);
            return true;
        }

        private static bool TryReadQuaternion(JToken value, out Quaternion quaternion, out string errorMessage)
        {
            quaternion = Quaternion.identity;
            if (!TryReadObject(value, "Quaternion", out JObject obj, out errorMessage)) return false;
            quaternion = new Quaternion(obj["x"]?.ToObject<float>() ?? 0f, obj["y"]?.ToObject<float>() ?? 0f, obj["z"]?.ToObject<float>() ?? 0f, obj["w"]?.ToObject<float>() ?? 1f);
            return true;
        }

        private static bool TryReadColor(JToken value, out Color color, out string errorMessage)
        {
            color = Color.clear;
            if (!TryReadObject(value, "Color", out JObject obj, out errorMessage)) return false;
            color = new Color(obj["r"]?.ToObject<float>() ?? 0f, obj["g"]?.ToObject<float>() ?? 0f, obj["b"]?.ToObject<float>() ?? 0f, obj["a"]?.ToObject<float>() ?? 1f);
            return true;
        }

        private static bool TryReadRect(JToken value, out Rect rect, out string errorMessage)
        {
            rect = new Rect();
            if (!TryReadObject(value, "Rect", out JObject obj, out errorMessage)) return false;
            rect = new Rect(obj["x"]?.ToObject<float>() ?? 0f, obj["y"]?.ToObject<float>() ?? 0f, obj["width"]?.ToObject<float>() ?? 0f, obj["height"]?.ToObject<float>() ?? 0f);
            return true;
        }

        private static bool TryReadBounds(JToken value, out Bounds bounds, out string errorMessage)
        {
            bounds = new Bounds(Vector3.zero, Vector3.one);
            if (!TryReadObject(value, "Bounds", out JObject obj, out errorMessage)) return false;

            Vector3 center = Vector3.zero;
            Vector3 size = Vector3.one;
            if (obj["center"] != null && !TryReadVector3(obj["center"], out center, out errorMessage)) return false;
            if (obj["size"] != null && !TryReadVector3(obj["size"], out size, out errorMessage)) return false;

            bounds = new Bounds(center, size);
            return true;
        }

        private static bool TryReadVector2Int(JToken value, out Vector2Int vector, out string errorMessage)
        {
            vector = Vector2Int.zero;
            if (!TryReadObject(value, "Vector2Int", out JObject obj, out errorMessage)) return false;
            vector = new Vector2Int(obj["x"]?.ToObject<int>() ?? 0, obj["y"]?.ToObject<int>() ?? 0);
            return true;
        }

        private static bool TryReadVector3Int(JToken value, out Vector3Int vector, out string errorMessage)
        {
            vector = Vector3Int.zero;
            if (!TryReadObject(value, "Vector3Int", out JObject obj, out errorMessage)) return false;
            vector = new Vector3Int(obj["x"]?.ToObject<int>() ?? 0, obj["y"]?.ToObject<int>() ?? 0, obj["z"]?.ToObject<int>() ?? 0);
            return true;
        }

        private static bool TryReadRectInt(JToken value, out RectInt rect, out string errorMessage)
        {
            rect = new RectInt();
            if (!TryReadObject(value, "RectInt", out JObject obj, out errorMessage)) return false;
            rect = new RectInt(obj["x"]?.ToObject<int>() ?? 0, obj["y"]?.ToObject<int>() ?? 0, obj["width"]?.ToObject<int>() ?? 0, obj["height"]?.ToObject<int>() ?? 0);
            return true;
        }

        private static bool TryReadBoundsInt(JToken value, out BoundsInt bounds, out string errorMessage)
        {
            bounds = new BoundsInt(Vector3Int.zero, Vector3Int.one);
            if (!TryReadObject(value, "BoundsInt", out JObject obj, out errorMessage)) return false;

            Vector3Int position = Vector3Int.zero;
            Vector3Int size = Vector3Int.one;
            if (obj["position"] != null && !TryReadVector3Int(obj["position"], out position, out errorMessage)) return false;
            if (obj["size"] != null && !TryReadVector3Int(obj["size"], out size, out errorMessage)) return false;

            bounds = new BoundsInt(position, size);
            return true;
        }
    }
}
