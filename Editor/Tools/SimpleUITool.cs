using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    /// <summary>
    /// Simplified UI manipulation tool for Unity MCP
    /// </summary>
    public class SimpleUITool : McpToolBase
    {
        public SimpleUITool()
        {
            Name = "simple_ui";
            Description = "Simple UI element creation and manipulation in Unity";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                string action = parameters["action"]?.ToObject<string>();

                switch (action?.ToLower())
                {
                    case "create_canvas":
                        return CreateCanvas(parameters);
                    case "create_text":
                        return CreateText(parameters);
                    case "create_button":
                        return CreateButton(parameters);
                    case "update_text":
                        return UpdateText(parameters);
                    case "list_ui":
                        return ListUI();
                    default:
                        return CreateErrorResponse($"Unknown action: {action}");
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Simple UI Tool error: {ex}");
                return CreateErrorResponse($"UI operation failed: {ex.Message}");
            }
        }

        private JObject CreateCanvas(JObject parameters)
        {
            string name = parameters["name"]?.ToObject<string>() ?? "Canvas";

            GameObject canvas = new GameObject(name);
            canvas.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.AddComponent<CanvasScaler>();
            canvas.AddComponent<GraphicRaycaster>();

            Undo.RegisterCreatedObjectUndo(canvas, "Create Canvas");
            Selection.activeGameObject = canvas;

            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = $"Created Canvas: {name}"
                    }
                },
                ["isError"] = false
            };
        }

        private JObject CreateText(JObject parameters)
        {
            string name = parameters["name"]?.ToObject<string>() ?? "Text";
            string text = parameters["text"]?.ToObject<string>() ?? "Sample Text";
            string parentName = parameters["parent"]?.ToObject<string>();

            GameObject parent = null;
            if (!string.IsNullOrEmpty(parentName))
            {
                parent = GameObject.Find(parentName);
                if (parent == null)
                {
                    return CreateErrorResponse($"Parent not found: {parentName}");
                }
            }

            GameObject textObj = new GameObject(name);
            if (parent != null)
            {
                textObj.transform.SetParent(parent.transform, false);
            }

            textObj.AddComponent<RectTransform>();
            textObj.AddComponent<CanvasRenderer>();

            Text textComponent = textObj.AddComponent<Text>();
            textComponent.text = text;
            textComponent.fontSize = 24;
            textComponent.color = Color.white;
            textComponent.alignment = TextAnchor.MiddleCenter;
            textComponent.font = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // Set default size
            RectTransform rect = textObj.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(200, 50);

            Undo.RegisterCreatedObjectUndo(textObj, "Create Text");
            Selection.activeGameObject = textObj;

            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = $"Created Text: {name} with content '{text}'"
                    }
                },
                ["isError"] = false
            };
        }

        private JObject CreateButton(JObject parameters)
        {
            string name = parameters["name"]?.ToObject<string>() ?? "Button";
            string buttonText = parameters["text"]?.ToObject<string>() ?? "Button";
            string parentName = parameters["parent"]?.ToObject<string>();

            GameObject parent = null;
            if (!string.IsNullOrEmpty(parentName))
            {
                parent = GameObject.Find(parentName);
                if (parent == null)
                {
                    return CreateErrorResponse($"Parent not found: {parentName}");
                }
            }

            GameObject button = new GameObject(name);
            if (parent != null)
            {
                button.transform.SetParent(parent.transform, false);
            }

            button.AddComponent<RectTransform>();
            button.AddComponent<CanvasRenderer>();
            button.AddComponent<Image>().color = new Color(0.3f, 0.5f, 0.8f);
            button.AddComponent<Button>();

            // Set button size
            RectTransform buttonRect = button.GetComponent<RectTransform>();
            buttonRect.sizeDelta = new Vector2(160, 30);

            // Create text child
            GameObject textChild = new GameObject("Text");
            textChild.transform.SetParent(button.transform, false);
            textChild.AddComponent<RectTransform>();
            textChild.AddComponent<CanvasRenderer>();

            Text text = textChild.AddComponent<Text>();
            text.text = buttonText;
            text.fontSize = 14;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.font = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // Fit text to button
            RectTransform textRect = textChild.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            textRect.anchoredPosition = Vector2.zero;

            Undo.RegisterCreatedObjectUndo(button, "Create Button");
            Selection.activeGameObject = button;

            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = $"Created Button: {name} with text '{buttonText}'"
                    }
                },
                ["isError"] = false
            };
        }

        private JObject UpdateText(JObject parameters)
        {
            string targetName = parameters["target"]?.ToObject<string>();
            string newText = parameters["text"]?.ToObject<string>();

            if (string.IsNullOrEmpty(targetName))
            {
                return CreateErrorResponse("Target name is required");
            }

            GameObject target = GameObject.Find(targetName);
            if (target == null)
            {
                return CreateErrorResponse($"Target not found: {targetName}");
            }

            Text textComponent = target.GetComponent<Text>();
            if (textComponent == null)
            {
                return CreateErrorResponse($"Target does not have a Text component: {targetName}");
            }

            Undo.RecordObject(textComponent, "Update Text");
            textComponent.text = newText;
            EditorUtility.SetDirty(textComponent);

            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = $"Updated text on {targetName} to '{newText}'"
                    }
                },
                ["isError"] = false
            };
        }

        private JObject ListUI()
        {
            Canvas[] canvases = GameObject.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            List<string> uiElements = new List<string>();

            foreach (Canvas canvas in canvases)
            {
                uiElements.Add($"Canvas: {canvas.name}");

                Text[] texts = canvas.GetComponentsInChildren<Text>();
                foreach (Text text in texts)
                {
                    uiElements.Add($"  Text: {text.gameObject.name} - '{text.text}'");
                }

                Button[] buttons = canvas.GetComponentsInChildren<Button>();
                foreach (Button button in buttons)
                {
                    uiElements.Add($"  Button: {button.gameObject.name}");
                }
            }

            string result = uiElements.Count > 0 ?
                string.Join("\n", uiElements) :
                "No UI elements found";

            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = $"UI Elements in scene:\n{result}"
                    }
                },
                ["isError"] = false
            };
        }

        private JObject CreateErrorResponse(string message)
        {
            return new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = $"Error: {message}"
                    }
                },
                ["isError"] = true
            };
        }
    }
}