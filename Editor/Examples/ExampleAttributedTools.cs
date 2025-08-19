using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using McpUnity.Tools.Attributes;
using Newtonsoft.Json.Linq;

namespace McpUnity.Examples
{
    /// <summary>
    /// Example attributed tools demonstrating how to use the MCP Unity attribute system.
    /// These serve as templates for Unity developers who want to create their own MCP tools.
    /// </summary>
    // Note: This file uses UnityEngine.GameObject, not Resources
    public static class ExampleAttributedTools
    {
        /// <summary>
        /// Simple tool that displays a message in the console
        /// </summary>
        [McpTool("example_hello_world", Description = "Displays a hello world message", Category = "Examples")]
        public static string HelloWorld(
            [McpParameter(Description = "Name to greet", Required = false, DefaultValue = "World")] string name)
        {
            var message = $"Hello, {name}!";
            Debug.Log($"[ExampleTool] {message}");
            return message;
        }
        
        /// <summary>
        /// Tool that performs a simple calculation
        /// </summary>
        [McpTool("example_calculate", Description = "Performs basic arithmetic operations", Category = "Examples")]
        public static object Calculate(
            [McpParameter(Description = "First number")] float a,
            [McpParameter(Description = "Second number")] float b,
            [McpParameter(Description = "Operation (add, subtract, multiply, divide)", DefaultValue = "add")] string operation)
        {
            float result;
            switch (operation.ToLower())
            {
                case "add":
                    result = a + b;
                    break;
                case "subtract":
                    result = a - b;
                    break;
                case "multiply":
                    result = a * b;
                    break;
                case "divide":
                    if (b == 0)
                        throw new System.ArgumentException("Cannot divide by zero");
                    result = a / b;
                    break;
                default:
                    throw new System.ArgumentException($"Unknown operation: {operation}");
            }
            
            Debug.Log($"[ExampleTool] {a} {operation} {b} = {result}");
            
            return new
            {
                operation = operation,
                operand1 = a,
                operand2 = b,
                result = result
            };
        }
        
        /// <summary>
        /// Tool that creates a GameObject with specified properties
        /// </summary>
        [McpTool("example_create_gameobject", Description = "Creates a new GameObject with specified name and position", Category = "Examples", IsAsync = true)]
        public static object CreateGameObject(
            [McpParameter(Description = "Name of the GameObject")] string name,
            [McpParameter(Description = "X position", DefaultValue = 0f)] float x,
            [McpParameter(Description = "Y position", DefaultValue = 0f)] float y,
            [McpParameter(Description = "Z position", DefaultValue = 0f)] float z,
            [McpParameter(Description = "Whether to add a MeshRenderer", Required = false, DefaultValue = false)] bool addRenderer)
        {
            GameObject go;
            
            if (addRenderer)
            {
                // Create primitive cube which includes MeshRenderer and MeshFilter
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = name;
            }
            else
            {
                // Create empty GameObject
                go = new GameObject(name);
            }
            
            // Set position
            go.transform.position = new Vector3(x, y, z);
            
            // Register undo operation
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
            
            Debug.Log($"[ExampleTool] Created GameObject '{name}' at ({x}, {y}, {z})");
            
            return new
            {
                success = true,
                gameObjectName = name,
                position = new { x, y, z },
                instanceId = go.GetInstanceID(),
                hasRenderer = addRenderer
            };
        }
        
        /// <summary>
        /// Tool that gets information about the current scene
        /// </summary>
        [McpTool("example_scene_info", Description = "Gets information about the current scene", Category = "Examples")]
        public static object GetSceneInfo()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var gameObjects = scene.GetRootGameObjects();
            
            var gameObjectInfo = new List<object>();
            foreach (var go in gameObjects)
            {
                gameObjectInfo.Add(new
                {
                    name = go.name,
                    tag = go.tag,
                    layer = go.layer,
                    active = go.activeInHierarchy,
                    componentCount = go.GetComponents<Component>().Length
                });
            }
            
            var info = new
            {
                sceneName = scene.name,
                scenePath = scene.path,
                isLoaded = scene.isLoaded,
                isDirty = scene.isDirty,
                rootGameObjectCount = gameObjects.Length,
                gameObjects = gameObjectInfo
            };
            
            Debug.Log($"[ExampleTool] Scene '{scene.name}' has {gameObjects.Length} root GameObjects");
            
            return info;
        }
        
        /// <summary>
        /// Tool that demonstrates error handling
        /// </summary>
        [McpTool("example_error_demo", Description = "Demonstrates error handling in attributed tools", Category = "Examples")]
        public static string ErrorDemo(
            [McpParameter(Description = "Whether to throw an error")] bool shouldError,
            [McpParameter(Description = "Error message to use", DefaultValue = "This is a test error")] string errorMessage)
        {
            if (shouldError)
            {
                throw new System.InvalidOperationException(errorMessage);
            }
            
            return "No error occurred - tool executed successfully!";
        }
        
        /// <summary>
        /// Tool that works with Unity preferences
        /// </summary>
        [McpTool("example_set_preference", Description = "Sets a Unity editor preference", Category = "Examples")]
        public static object SetPreference(
            [McpParameter(Description = "Preference key")] string key,
            [McpParameter(Description = "Preference value")] string value,
            [McpParameter(Description = "Company name for the preference", DefaultValue = "McpUnity")] string company)
        {
            var fullKey = $"{company}.{key}";
            EditorPrefs.SetString(fullKey, value);
            
            Debug.Log($"[ExampleTool] Set preference '{fullKey}' = '{value}'");
            
            return new
            {
                success = true,
                key = fullKey,
                value = value,
                previousValue = EditorPrefs.GetString(fullKey, null)
            };
        }
        
        /// <summary>
        /// Tool that gets a Unity preference
        /// </summary>
        [McpTool("example_get_preference", Description = "Gets a Unity editor preference", Category = "Examples")]
        public static object GetPreference(
            [McpParameter(Description = "Preference key")] string key,
            [McpParameter(Description = "Company name for the preference", DefaultValue = "McpUnity")] string company,
            [McpParameter(Description = "Default value if preference doesn't exist", DefaultValue = "")] string defaultValue)
        {
            var fullKey = $"{company}.{key}";
            var value = EditorPrefs.GetString(fullKey, defaultValue);
            var exists = EditorPrefs.HasKey(fullKey);
            
            Debug.Log($"[ExampleTool] Preference '{fullKey}' = '{value}' (exists: {exists})");
            
            return new
            {
                key = fullKey,
                value = value,
                exists = exists,
                defaultValue = defaultValue
            };
        }
    }
}