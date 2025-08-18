# MCP Unity Attributed Tools Guide

This guide explains how to create MCP tools using C# attributes, allowing Unity developers to easily expose their methods as MCP tools without manual registration.

## Quick Start

1. **Add the attribute to any public static method:**
```csharp
[McpTool("my_tool_name", Description = "What this tool does")]
public static string MyTool(string parameter)
{
    return "Hello from my tool!";
}
```

2. **Restart the MCP server** or use "Tools > MCP Unity > Refresh Attributed Tools" menu item

3. **Your tool is now available** via MCP calls as `my_tool_name`

## Basic Example

```csharp
using McpUnity.Tools.Attributes;
using UnityEngine;

public static class MyTools
{
    [McpTool("say_hello", Description = "Says hello to someone")]
    public static string SayHello(string name)
    {
        Debug.Log($"Hello, {name}!");
        return $"Hello, {name}!";
    }
}
```

## Attribute Properties

### McpToolAttribute

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `Name` | string | Yes | Tool name used in MCP API calls |
| `Description` | string | No | Tool description for documentation |
| `IsAsync` | bool | No | Whether tool runs on Unity main thread (default: false) |
| `Category` | string | No | Tool category for organization (default: "General") |

### McpParameterAttribute

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `Description` | string | No | Parameter description |
| `Required` | bool | No | Whether parameter is required (default: true) |
| `DefaultValue` | object | No | Default value if parameter not provided |
| `JsonName` | string | No | JSON property name (default: parameter name) |

## Parameter Types

Supported parameter and return types:
- **Primitives**: `int`, `float`, `double`, `bool`, `string`
- **Unity Types**: `Vector3`, `Vector2`, `Color`, etc.
- **Collections**: `List<T>`, `Dictionary<string, T>`, arrays
- **Custom Objects**: Any serializable class/struct
- **Newtonsoft.Json**: `JObject`, `JArray`, `JToken`

## Advanced Examples

### Tool with Optional Parameters

```csharp
[McpTool("create_cube", Description = "Creates a cube GameObject")]
public static object CreateCube(
    [McpParameter(Description = "Cube name")] string name,
    [McpParameter(Description = "Scale factor", Required = false, DefaultValue = 1.0f)] float scale,
    [McpParameter(Description = "Position", Required = false)] Vector3 position = default)
{
    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
    cube.name = name;
    cube.transform.localScale = Vector3.one * scale;
    cube.transform.position = position;
    
    return new { success = true, instanceId = cube.GetInstanceID() };
}
```

### Async Tool (Main Thread)

```csharp
[McpTool("capture_screenshot", Description = "Captures a screenshot", IsAsync = true)]
public static object CaptureScreenshot(
    [McpParameter(Description = "Screenshot path")] string path)
{
    // This runs on Unity's main thread
    ScreenCapture.CaptureScreenshot(path);
    
    return new { success = true, path = path };
}
```

### Tool with Custom JSON Names

```csharp
[McpTool("transform_object", Description = "Transforms a GameObject")]
public static object TransformObject(
    [McpParameter(JsonName = "obj_name", Description = "GameObject name")] string objectName,
    [McpParameter(JsonName = "new_pos", Description = "New position")] Vector3 position)
{
    var go = GameObject.Find(objectName);
    if (go == null) 
        throw new System.ArgumentException($"GameObject '{objectName}' not found");
    
    go.transform.position = position;
    return new { success = true };
}
```

### Error Handling

```csharp
[McpTool("safe_operation", Description = "Demonstrates error handling")]
public static object SafeOperation(string input)
{
    if (string.IsNullOrEmpty(input))
        throw new System.ArgumentException("Input cannot be null or empty");
    
    try
    {
        // Your operation here
        return new { success = true, result = input.ToUpper() };
    }
    catch (System.Exception ex)
    {
        // Exceptions are automatically caught and returned as error responses
        throw new System.InvalidOperationException($"Operation failed: {ex.Message}");
    }
}
```

## Best Practices

### 1. Use Descriptive Names and Descriptions
```csharp
// Good
[McpTool("create_player_character", Description = "Creates a new player character with default equipment")]

// Avoid
[McpTool("create", Description = "Creates something")]
```

### 2. Validate Parameters
```csharp
[McpTool("move_object", Description = "Moves a GameObject")]
public static object MoveObject(string objectName, Vector3 position)
{
    if (string.IsNullOrEmpty(objectName))
        throw new System.ArgumentException("Object name cannot be empty");
    
    var go = GameObject.Find(objectName);
    if (go == null)
        throw new System.ArgumentException($"GameObject '{objectName}' not found");
    
    go.transform.position = position;
    return new { success = true };
}
```

### 3. Use Categories for Organization
```csharp
[McpTool("spawn_enemy", Category = "Gameplay")]
[McpTool("create_ui_panel", Category = "UI")]
[McpTool("debug_memory", Category = "Debug")]
```

### 4. Mark Async When Needed
```csharp
// Use IsAsync = true for operations that must run on Unity's main thread
[McpTool("instantiate_prefab", IsAsync = true)]
public static object InstantiatePrefab(string prefabPath)
{
    // Unity operations like Instantiate must run on main thread
    var prefab = Resources.Load<GameObject>(prefabPath);
    var instance = Object.Instantiate(prefab);
    return new { success = true, instanceId = instance.GetInstanceID() };
}
```

### 5. Return Meaningful Data
```csharp
[McpTool("analyze_scene", Description = "Analyzes current scene")]
public static object AnalyzeScene()
{
    var scene = SceneManager.GetActiveScene();
    var gameObjects = scene.GetRootGameObjects();
    
    return new
    {
        sceneName = scene.name,
        objectCount = gameObjects.Length,
        isDirty = scene.isDirty,
        analysis = new
        {
            totalComponents = gameObjects.Sum(go => go.GetComponentsInChildren<Component>().Length),
            activeObjects = gameObjects.Count(go => go.activeInHierarchy)
        }
    };
}
```

## Tool Discovery and Registration

### Automatic Discovery
- Tools are automatically discovered when MCP Unity starts
- Scans all loaded assemblies for methods with `[McpTool]` attribute
- Built-in tools have priority over attributed tools with same names

### Manual Refresh
Use the menu item "Tools > MCP Unity > Refresh Attributed Tools" to re-scan for attributed tools after:
- Adding new attributed methods
- Assembly domain reload
- Code changes

### Assembly Scanning
The system scans these assemblies:
- ✅ `Assembly-CSharp` (your project code)
- ✅ `Assembly-CSharp-Editor` (your editor code)
- ❌ Unity engine assemblies (skipped for performance)
- ❌ System assemblies (skipped for performance)

## Debugging

### Enable Logging
Check Unity Console for MCP tool registration messages:
```
[AttributedToolDiscovery] Discovered tool 'my_tool' from MyClass.MyMethod
[RegisterAttributedTools] Registered attributed tool 'my_tool' (Category: General)
```

### Common Issues

**Tool not appearing:**
- Check method is `public static`
- Verify `[McpTool]` attribute is present
- Ensure no duplicate tool names
- Check Unity Console for error messages

**Parameter conversion errors:**
- Verify parameter types are JSON-compatible
- Check `JsonName` matches MCP call parameters
- Ensure required parameters are provided

**Execution errors:**
- Use try-catch for error handling
- Check `IsAsync` setting for Unity API calls
- Verify GameObject/asset references exist

## Integration with Existing Code

### Retrofitting Existing Methods
```csharp
// Before: Regular Unity method
public static void ResetScene()
{
    SceneManager.LoadScene(SceneManager.GetActiveScene().name);
}

// After: MCP-enabled with attribute
[McpTool("reset_scene", Description = "Resets the current scene")]
public static object ResetScene()
{
    var sceneName = SceneManager.GetActiveScene().name;
    SceneManager.LoadScene(sceneName);
    return new { success = true, sceneName };
}
```

### Wrapping Complex Operations
```csharp
[McpTool("batch_process_prefabs", Description = "Processes multiple prefabs")]
public static object BatchProcessPrefabs(
    [McpParameter(Description = "Prefab paths")] string[] prefabPaths,
    [McpParameter(Description = "Processing operation")] string operation)
{
    var results = new List<object>();
    
    foreach (var path in prefabPaths)
    {
        try
        {
            // Your existing prefab processing logic
            var result = ProcessSinglePrefab(path, operation);
            results.Add(new { path, success = true, result });
        }
        catch (System.Exception ex)
        {
            results.Add(new { path, success = false, error = ex.Message });
        }
    }
    
    return new { processed = results.Count, results };
}

// Your existing method stays unchanged
private static object ProcessSinglePrefab(string path, string operation)
{
    // Existing implementation
    return null;
}
```

This attribute system provides a powerful, flexible way to expose Unity functionality through MCP while maintaining clean, maintainable code.