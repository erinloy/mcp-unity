using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using McpUnity.DirectMcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpUnity.DirectMcp.Tools
{
    /// <summary>
    /// Static Unity tools that are registered at server startup.
    /// These forward to Unity via the tool service when connected.
    /// </summary>
    [McpServerToolType]
    public class UnityTools
    {
        private readonly IUnityToolService _toolService;

        public UnityTools(IUnityToolService toolService)
        {
            _toolService = toolService;
        }

        [McpServerTool(Name = "select_gameobject"), Description("Selects a game object in the Unity scene by path or instance ID")]
        public async Task<string> SelectGameObject(
            [Description("Full hierarchy path (e.g., 'Canvas/Button') OR instance ID as a number")] string target,
            CancellationToken ct)
        {
            var args = new Dictionary<string, object>();

            // Check if target is an integer (instance ID) or a path
            if (int.TryParse(target, out int instanceId))
            {
                args["instanceId"] = instanceId;
            }
            else
            {
                args["objectPath"] = target;
            }

            var result = await _toolService.CallToolAsync("select_gameobject", args, ct);
            return FormatResult(result);
        }

        [McpServerTool(Name = "create_ui_element"), Description("Creates a UI element (Button, Toggle, Text, Image, etc.) as a child of a parent object")]
        public async Task<string> CreateUIElement(
            [Description("Type of UI element to create: Button, Toggle, Text, Image, Panel, InputField, Slider, ScrollView, Dropdown")] string elementType,
            [Description("Full hierarchy path to parent (e.g., 'Canvas/Panel') OR instance ID as a number")] string parentPath,
            [Description("Name for the new UI element")] string name = "",
            CancellationToken ct = default)
        {
            var args = new Dictionary<string, object>();
            args["elementType"] = elementType;

            // Check if parentPath is an integer (instance ID) or a path
            if (int.TryParse(parentPath, out int instanceId))
            {
                args["parentInstanceId"] = instanceId;
            }
            else
            {
                args["parentPath"] = parentPath;
            }

            if (!string.IsNullOrEmpty(name))
                args["name"] = name;

            var result = await _toolService.CallToolAsync("create_ui_element", args, ct);
            return FormatResult(result);
        }

        [McpServerTool(Name = "set_object_reference"), Description("Sets a serialized field reference on a component to point to another object")]
        public async Task<string> SetObjectReference(
            [Description("Full hierarchy path (e.g., 'Canvas/Panel') OR instance ID of target object")] string targetPath,
            [Description("Name of the component containing the field")] string componentName,
            [Description("Name of the serialized field to set")] string fieldName,
            [Description("Full hierarchy path OR instance ID of the object to reference")] string referencePath,
            [Description("Optional: Component type if referencing a component instead of GameObject")] string referenceComponent = "",
            CancellationToken ct = default)
        {
            var args = new Dictionary<string, object>();

            // Check if targetPath is an integer (instance ID) or a path
            if (int.TryParse(targetPath, out int targetInstanceId))
            {
                args["targetInstanceId"] = targetInstanceId;
            }
            else
            {
                args["targetPath"] = targetPath;
            }

            args["targetComponent"] = componentName;
            args["fieldName"] = fieldName;

            // Check if referencePath is an integer (instance ID) or a path
            if (int.TryParse(referencePath, out int referenceInstanceId))
            {
                args["referenceInstanceId"] = referenceInstanceId;
            }
            else
            {
                args["referencePath"] = referencePath;
            }

            if (!string.IsNullOrEmpty(referenceComponent))
                args["referenceComponent"] = referenceComponent;

            var result = await _toolService.CallToolAsync("set_object_reference", args, ct);
            return FormatResult(result);
        }

        [McpServerTool(Name = "inspect_gameobject"), Description("Inspects a game object and returns detailed information about its components and serialized fields")]
        public async Task<string> InspectGameObject(
            [Description("Full hierarchy path (e.g., 'Canvas/Button') OR instance ID as a number")] string target,
            [Description("If true, includes all serialized fields on components")] bool includeFields = true,
            CancellationToken ct = default)
        {
            var args = new Dictionary<string, object>();

            // Check if target is an integer (instance ID) or a path
            if (int.TryParse(target, out int instanceId))
            {
                args["instanceId"] = instanceId;
            }
            else
            {
                args["objectPath"] = target;
            }

            args["includeFields"] = includeFields.ToString().ToLower();

            var result = await _toolService.CallToolAsync("inspect_gameobject", args, ct);
            return FormatResult(result);
        }

        [McpServerTool(Name = "execute_menu_item"), Description("Executes a Unity Editor menu item by its path")]
        public async Task<string> ExecuteMenuItem(
            [Description("The menu path to execute, e.g. 'File/Save'")] string menuPath,
            CancellationToken ct = default)
        {
            var result = await _toolService.CallToolAsync("execute_menu_item",
                new() { ["menuPath"] = menuPath }, ct);
            return FormatResult(result);
        }

        [McpServerTool(Name = "run_tests"), Description("Runs Unity tests (EditMode or PlayMode)")]
        public async Task<string> RunTests(
            [Description("Test mode: EditMode or PlayMode")] string testMode = "EditMode",
            [Description("Optional test filter")] string testFilter = "",
            CancellationToken ct = default)
        {
            var args = new Dictionary<string, object>
            {
                ["testMode"] = testMode
            };
            if (!string.IsNullOrEmpty(testFilter))
                args["testFilter"] = testFilter;

            var result = await _toolService.CallToolAsync("run_tests", args, ct);
            return FormatResult(result);
        }

        [McpServerTool(Name = "capture_screenshot"), Description("Captures a screenshot from Unity's Scene or Game view and returns it as base64 image data")]
        public async Task<CallToolResult> CaptureScreenshot(
            [Description("View to capture: 'game', 'scene', or 'both'")] string viewType = "game",
            [Description("Width of the screenshot in pixels")] int width = 800,
            [Description("Height of the screenshot in pixels")] int height = 600,
            CancellationToken ct = default)
        {
            var args = new Dictionary<string, object>
            {
                ["viewType"] = viewType,
                ["width"] = width,
                ["height"] = height,
                ["saveToFile"] = false
            };

            var result = await _toolService.CallToolAsync("capture_screenshot", args, ct);
            return result;
        }

        [McpServerTool(Name = "find_gameobject"), Description("Finds GameObjects by name pattern or lists children of a specified path")]
        public async Task<string> FindGameObject(
            [Description("Name pattern to search for (case-insensitive partial match)")] string namePattern = "",
            [Description("Full hierarchy path to list children of (e.g., 'Canvas/Poma')")] string listChildrenOf = "",
            [Description("If true, returns all descendants recursively")] bool recursive = false,
            [Description("Maximum number of results to return")] int maxResults = 50,
            CancellationToken ct = default)
        {
            var args = new Dictionary<string, object>();

            if (!string.IsNullOrEmpty(namePattern))
                args["namePattern"] = namePattern;
            if (!string.IsNullOrEmpty(listChildrenOf))
                args["listChildrenOf"] = listChildrenOf;
            args["recursive"] = recursive;
            args["maxResults"] = maxResults;

            var result = await _toolService.CallToolAsync("find_gameobject", args, ct);
            return FormatResult(result);
        }

        [McpServerTool(Name = "update_component"), Description("Updates component fields on a GameObject or adds the component if it doesn't exist")]
        public async Task<string> UpdateComponent(
            [Description("Full hierarchy path (e.g., 'Canvas/Panel') OR instance ID of target object")] string target,
            [Description("Name of the component type to update or add")] string componentName,
            [Description("JSON object with field names and values to set (e.g., {\"anchorMin\": {\"x\": 0, \"y\": 0}})")] string componentData = "{}",
            CancellationToken ct = default)
        {
            var args = new Dictionary<string, object>();

            // Check if target is an integer (instance ID) or a path
            if (int.TryParse(target, out int instanceId))
            {
                args["instanceId"] = instanceId;
            }
            else
            {
                args["objectPath"] = target;
            }

            args["componentName"] = componentName;

            // Parse componentData JSON string into a dictionary
            if (!string.IsNullOrEmpty(componentData) && componentData != "{}")
            {
                try
                {
                    var dataDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(componentData);
                    args["componentData"] = dataDict;
                }
                catch
                {
                    return "Error: Invalid JSON in componentData parameter";
                }
            }

            var result = await _toolService.CallToolAsync("update_component", args, ct);
            return FormatResult(result);
        }

        [McpServerTool(Name = "copy_field_value"), Description("Copies a field value from one component to another. Useful for copying sprites, materials, and other asset references.")]
        public async Task<string> CopyFieldValue(
            [Description("Hierarchy path to the source GameObject")] string sourcePath,
            [Description("Name of the component type on the source GameObject")] string sourceComponent,
            [Description("Name of the field to copy from")] string sourceField,
            [Description("Hierarchy path to the target GameObject")] string targetPath,
            [Description("Name of the component type on the target GameObject")] string targetComponent,
            [Description("Name of the field to copy to (defaults to same as sourceField)")] string targetField = "",
            CancellationToken ct = default)
        {
            var args = new Dictionary<string, object>
            {
                ["sourcePath"] = sourcePath,
                ["sourceComponent"] = sourceComponent,
                ["sourceField"] = sourceField,
                ["targetPath"] = targetPath,
                ["targetComponent"] = targetComponent
            };

            if (!string.IsNullOrEmpty(targetField))
                args["targetField"] = targetField;

            var result = await _toolService.CallToolAsync("copy_field_value", args, ct);
            return FormatResult(result);
        }

        private static string FormatResult(CallToolResult result)
        {
            if (result.IsError == true)
            {
                var errorText = result.Content?.Count > 0
                    ? (result.Content[0] as TextContentBlock)?.Text
                    : "Unknown error";
                return $"Error: {errorText}";
            }

            var texts = new List<string>();
            if (result.Content != null)
            {
                foreach (var content in result.Content)
                {
                    if (content is TextContentBlock textBlock)
                    {
                        texts.Add(textBlock.Text);
                    }
                }
            }
            return string.Join("\n", texts);
        }
    }
}
