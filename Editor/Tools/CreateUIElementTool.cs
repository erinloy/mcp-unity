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
    /// Generic UI element factory tool. Creates any standard Unity UI element by type name.
    /// </summary>
    public class CreateUIElementTool : McpToolBase
    {
        public CreateUIElementTool()
        {
            Name = "create_ui_element";
            Description = "Creates a UI element of any type (Toggle, Slider, InputField, Dropdown, ScrollView, Image, RawImage, etc.) with optional parent and properties.";

            InputSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["elementType"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Type of UI element: Toggle, Slider, InputField, Dropdown, ScrollRect, Image, RawImage, Button, Text, Panel, ScrollView"
                    },
                    ["name"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Name for the created GameObject"
                    },
                    ["parentPath"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Hierarchy path to parent GameObject (must have Canvas in hierarchy)"
                    },
                    ["parentInstanceId"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Instance ID of parent GameObject (alternative to parentPath)"
                    },
                    ["position"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "RectTransform anchored position {x, y}",
                        ["properties"] = new JObject
                        {
                            ["x"] = new JObject { ["type"] = "number" },
                            ["y"] = new JObject { ["type"] = "number" }
                        }
                    },
                    ["size"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "RectTransform size delta {width, height}",
                        ["properties"] = new JObject
                        {
                            ["width"] = new JObject { ["type"] = "number" },
                            ["height"] = new JObject { ["type"] = "number" }
                        }
                    },
                    ["label"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Label text for elements that have labels (Toggle, Button)"
                    },
                    ["isOn"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Initial state for Toggle elements"
                    }
                },
                ["required"] = new JArray { "elementType", "name" } // parentPath or parentInstanceId must be provided
            };
        }

        public override JObject Execute(JObject parameters)
        {
            string elementType = parameters["elementType"]?.ToObject<string>();
            string name = parameters["name"]?.ToObject<string>();
            string parentPath = parameters["parentPath"]?.ToObject<string>();
            int? parentInstanceId = parameters["parentInstanceId"]?.ToObject<int?>();
            JObject position = parameters["position"] as JObject;
            JObject size = parameters["size"] as JObject;
            string label = parameters["label"]?.ToObject<string>();
            bool? isOn = parameters["isOn"]?.ToObject<bool?>();

            if (string.IsNullOrEmpty(elementType))
                return McpUnitySocketHandler.CreateErrorResponse("'elementType' is required", "validation_error");
            if (string.IsNullOrEmpty(name))
                return McpUnitySocketHandler.CreateErrorResponse("'name' is required", "validation_error");
            if (string.IsNullOrEmpty(parentPath) && !parentInstanceId.HasValue)
                return McpUnitySocketHandler.CreateErrorResponse("'parentPath' or 'parentInstanceId' is required", "validation_error");

            // Find parent by instance ID or path
            GameObject parent = null;
            if (parentInstanceId.HasValue)
            {
                parent = EditorUtility.InstanceIDToObject(parentInstanceId.Value) as GameObject;
            }
            else
            {
                parent = FindGameObjectByPath(parentPath);
            }

            if (parent == null)
                return McpUnitySocketHandler.CreateErrorResponse($"Parent not found: {(parentInstanceId.HasValue ? $"instanceId={parentInstanceId}" : $"path={parentPath}")}", "not_found_error");

            // Ensure there's a Canvas in the hierarchy
            Canvas canvas = parent.GetComponentInParent<Canvas>();
            if (canvas == null)
                return McpUnitySocketHandler.CreateErrorResponse($"No Canvas found in hierarchy of {parentPath}. UI elements require a Canvas.", "validation_error");

            GameObject createdElement = null;

            try
            {
                switch (elementType.ToLower())
                {
                    case "toggle":
                        createdElement = CreateToggle(name, parent, label, isOn ?? false);
                        break;
                    case "slider":
                        createdElement = CreateSlider(name, parent);
                        break;
                    case "inputfield":
                        createdElement = CreateInputField(name, parent);
                        break;
                    case "dropdown":
                        createdElement = CreateDropdown(name, parent);
                        break;
                    case "image":
                        createdElement = CreateImage(name, parent);
                        break;
                    case "rawimage":
                        createdElement = CreateRawImage(name, parent);
                        break;
                    case "button":
                        createdElement = CreateButton(name, parent, label ?? "Button");
                        break;
                    case "text":
                        createdElement = CreateText(name, parent, label ?? "Text");
                        break;
                    case "panel":
                        createdElement = CreatePanel(name, parent);
                        break;
                    case "scrollview":
                    case "scrollrect":
                        createdElement = CreateScrollView(name, parent);
                        break;
                    default:
                        return McpUnitySocketHandler.CreateErrorResponse($"Unknown UI element type: {elementType}. Supported: Toggle, Slider, InputField, Dropdown, Image, RawImage, Button, Text, Panel, ScrollView", "validation_error");
                }

                // Apply position if specified
                if (position != null)
                {
                    RectTransform rect = createdElement.GetComponent<RectTransform>();
                    if (rect != null)
                    {
                        float x = position["x"]?.ToObject<float>() ?? 0;
                        float y = position["y"]?.ToObject<float>() ?? 0;
                        rect.anchoredPosition = new Vector2(x, y);
                    }
                }

                // Apply size if specified
                if (size != null)
                {
                    RectTransform rect = createdElement.GetComponent<RectTransform>();
                    if (rect != null)
                    {
                        float width = size["width"]?.ToObject<float>() ?? rect.sizeDelta.x;
                        float height = size["height"]?.ToObject<float>() ?? rect.sizeDelta.y;
                        rect.sizeDelta = new Vector2(width, height);
                    }
                }

                Undo.RegisterCreatedObjectUndo(createdElement, $"Create {elementType}");
                Selection.activeGameObject = createdElement;

                string path = GetGameObjectPath(createdElement);
                McpLogger.LogInfo($"[MCP Unity] Created UI element: {elementType} at {path}");

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Created {elementType}: {name}",
                    ["path"] = path,
                    ["instanceId"] = createdElement.GetInstanceID()
                };
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"[MCP Unity] Failed to create UI element: {ex.Message}");
                return McpUnitySocketHandler.CreateErrorResponse($"Failed to create UI element: {ex.Message}", "creation_error");
            }
        }

        private GameObject CreateToggle(string name, GameObject parent, string label, bool isOn)
        {
            GameObject toggleGO = new GameObject(name);
            toggleGO.transform.SetParent(parent.transform, false);

            RectTransform rect = toggleGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(160, 20);

            Toggle toggle = toggleGO.AddComponent<Toggle>();
            toggle.isOn = isOn;

            // Background
            GameObject background = new GameObject("Background");
            background.transform.SetParent(toggleGO.transform, false);
            RectTransform bgRect = background.AddComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.5f);
            bgRect.anchorMax = new Vector2(0, 0.5f);
            bgRect.sizeDelta = new Vector2(20, 20);
            bgRect.anchoredPosition = new Vector2(10, 0);
            Image bgImage = background.AddComponent<Image>();
            bgImage.color = new Color(0.2f, 0.2f, 0.2f);

            // Checkmark
            GameObject checkmark = new GameObject("Checkmark");
            checkmark.transform.SetParent(background.transform, false);
            RectTransform checkRect = checkmark.AddComponent<RectTransform>();
            checkRect.anchorMin = Vector2.zero;
            checkRect.anchorMax = Vector2.one;
            checkRect.sizeDelta = new Vector2(-4, -4);
            checkRect.anchoredPosition = Vector2.zero;
            Image checkImage = checkmark.AddComponent<Image>();
            checkImage.color = new Color(0.3f, 0.7f, 0.3f);

            toggle.targetGraphic = bgImage;
            toggle.graphic = checkImage;

            // Label
            if (!string.IsNullOrEmpty(label))
            {
                GameObject labelGO = new GameObject("Label");
                labelGO.transform.SetParent(toggleGO.transform, false);
                RectTransform labelRect = labelGO.AddComponent<RectTransform>();
                labelRect.anchorMin = new Vector2(0, 0);
                labelRect.anchorMax = new Vector2(1, 1);
                labelRect.offsetMin = new Vector2(25, 0);
                labelRect.offsetMax = new Vector2(0, 0);
                Text labelText = labelGO.AddComponent<Text>();
                labelText.text = label;
                labelText.fontSize = 14;
                labelText.color = Color.white;
                labelText.alignment = TextAnchor.MiddleLeft;
                labelText.font = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            return toggleGO;
        }

        private GameObject CreateSlider(string name, GameObject parent)
        {
            GameObject sliderGO = new GameObject(name);
            sliderGO.transform.SetParent(parent.transform, false);

            RectTransform rect = sliderGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(160, 20);

            Slider slider = sliderGO.AddComponent<Slider>();

            // Background
            GameObject background = new GameObject("Background");
            background.transform.SetParent(sliderGO.transform, false);
            RectTransform bgRect = background.AddComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.25f);
            bgRect.anchorMax = new Vector2(1, 0.75f);
            bgRect.sizeDelta = Vector2.zero;
            bgRect.anchoredPosition = Vector2.zero;
            Image bgImage = background.AddComponent<Image>();
            bgImage.color = new Color(0.2f, 0.2f, 0.2f);

            // Fill Area
            GameObject fillArea = new GameObject("Fill Area");
            fillArea.transform.SetParent(sliderGO.transform, false);
            RectTransform fillAreaRect = fillArea.AddComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0, 0.25f);
            fillAreaRect.anchorMax = new Vector2(1, 0.75f);
            fillAreaRect.offsetMin = new Vector2(5, 0);
            fillAreaRect.offsetMax = new Vector2(-5, 0);

            GameObject fill = new GameObject("Fill");
            fill.transform.SetParent(fillArea.transform, false);
            RectTransform fillRect = fill.AddComponent<RectTransform>();
            fillRect.sizeDelta = Vector2.zero;
            Image fillImage = fill.AddComponent<Image>();
            fillImage.color = new Color(0.3f, 0.5f, 0.8f);

            // Handle
            GameObject handleArea = new GameObject("Handle Slide Area");
            handleArea.transform.SetParent(sliderGO.transform, false);
            RectTransform handleAreaRect = handleArea.AddComponent<RectTransform>();
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.offsetMin = new Vector2(10, 0);
            handleAreaRect.offsetMax = new Vector2(-10, 0);

            GameObject handle = new GameObject("Handle");
            handle.transform.SetParent(handleArea.transform, false);
            RectTransform handleRect = handle.AddComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(20, 0);
            Image handleImage = handle.AddComponent<Image>();
            handleImage.color = Color.white;

            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handleImage;

            return sliderGO;
        }

        private GameObject CreateInputField(string name, GameObject parent)
        {
            GameObject inputGO = new GameObject(name);
            inputGO.transform.SetParent(parent.transform, false);

            RectTransform rect = inputGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(160, 30);

            Image image = inputGO.AddComponent<Image>();
            image.color = new Color(0.2f, 0.2f, 0.2f);

            InputField inputField = inputGO.AddComponent<InputField>();

            // Placeholder
            GameObject placeholder = new GameObject("Placeholder");
            placeholder.transform.SetParent(inputGO.transform, false);
            RectTransform phRect = placeholder.AddComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = new Vector2(5, 0);
            phRect.offsetMax = new Vector2(-5, 0);
            Text phText = placeholder.AddComponent<Text>();
            phText.text = "Enter text...";
            phText.fontSize = 14;
            phText.fontStyle = FontStyle.Italic;
            phText.color = new Color(0.5f, 0.5f, 0.5f);
            phText.alignment = TextAnchor.MiddleLeft;
            phText.font = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // Text
            GameObject text = new GameObject("Text");
            text.transform.SetParent(inputGO.transform, false);
            RectTransform textRect = text.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(5, 0);
            textRect.offsetMax = new Vector2(-5, 0);
            Text textComp = text.AddComponent<Text>();
            textComp.fontSize = 14;
            textComp.color = Color.white;
            textComp.alignment = TextAnchor.MiddleLeft;
            textComp.font = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            textComp.supportRichText = false;

            inputField.textComponent = textComp;
            inputField.placeholder = phText;

            return inputGO;
        }

        private GameObject CreateDropdown(string name, GameObject parent)
        {
            // Use Unity's built-in dropdown creation for proper structure
            GameObject dropdownGO = new GameObject(name);
            dropdownGO.transform.SetParent(parent.transform, false);

            RectTransform rect = dropdownGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(160, 30);

            Image image = dropdownGO.AddComponent<Image>();
            image.color = new Color(0.2f, 0.2f, 0.2f);

            Dropdown dropdown = dropdownGO.AddComponent<Dropdown>();

            // Label
            GameObject labelGO = new GameObject("Label");
            labelGO.transform.SetParent(dropdownGO.transform, false);
            RectTransform labelRect = labelGO.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(10, 0);
            labelRect.offsetMax = new Vector2(-25, 0);
            Text labelText = labelGO.AddComponent<Text>();
            labelText.text = "Option A";
            labelText.fontSize = 14;
            labelText.color = Color.white;
            labelText.alignment = TextAnchor.MiddleLeft;
            labelText.font = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            dropdown.captionText = labelText;
            dropdown.options = new List<Dropdown.OptionData>
            {
                new Dropdown.OptionData("Option A"),
                new Dropdown.OptionData("Option B"),
                new Dropdown.OptionData("Option C")
            };

            return dropdownGO;
        }

        private GameObject CreateImage(string name, GameObject parent)
        {
            GameObject imageGO = new GameObject(name);
            imageGO.transform.SetParent(parent.transform, false);

            RectTransform rect = imageGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(100, 100);

            Image image = imageGO.AddComponent<Image>();
            image.color = Color.white;

            return imageGO;
        }

        private GameObject CreateRawImage(string name, GameObject parent)
        {
            GameObject imageGO = new GameObject(name);
            imageGO.transform.SetParent(parent.transform, false);

            RectTransform rect = imageGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(100, 100);

            RawImage image = imageGO.AddComponent<RawImage>();
            image.color = Color.white;

            return imageGO;
        }

        private GameObject CreateButton(string name, GameObject parent, string buttonText)
        {
            GameObject buttonGO = new GameObject(name);
            buttonGO.transform.SetParent(parent.transform, false);

            RectTransform rect = buttonGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(160, 30);

            Image image = buttonGO.AddComponent<Image>();
            image.color = new Color(0.3f, 0.5f, 0.8f);

            Button button = buttonGO.AddComponent<Button>();
            button.targetGraphic = image;

            // Text
            GameObject textGO = new GameObject("Text");
            textGO.transform.SetParent(buttonGO.transform, false);
            RectTransform textRect = textGO.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            Text text = textGO.AddComponent<Text>();
            text.text = buttonText;
            text.fontSize = 14;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.font = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            return buttonGO;
        }

        private GameObject CreateText(string name, GameObject parent, string textContent)
        {
            GameObject textGO = new GameObject(name);
            textGO.transform.SetParent(parent.transform, false);

            RectTransform rect = textGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(160, 30);

            Text text = textGO.AddComponent<Text>();
            text.text = textContent;
            text.fontSize = 14;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.font = UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            return textGO;
        }

        private GameObject CreatePanel(string name, GameObject parent)
        {
            GameObject panelGO = new GameObject(name);
            panelGO.transform.SetParent(parent.transform, false);

            RectTransform rect = panelGO.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;

            Image image = panelGO.AddComponent<Image>();
            image.color = new Color(0.1f, 0.1f, 0.1f, 0.8f);

            return panelGO;
        }

        private GameObject CreateScrollView(string name, GameObject parent)
        {
            GameObject scrollGO = new GameObject(name);
            scrollGO.transform.SetParent(parent.transform, false);

            RectTransform rect = scrollGO.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(200, 200);

            Image image = scrollGO.AddComponent<Image>();
            image.color = new Color(0.1f, 0.1f, 0.1f, 0.5f);

            ScrollRect scrollRect = scrollGO.AddComponent<ScrollRect>();

            // Viewport
            GameObject viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollGO.transform, false);
            RectTransform vpRect = viewport.AddComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.sizeDelta = Vector2.zero;
            vpRect.pivot = new Vector2(0, 1);
            viewport.AddComponent<Image>().color = Color.clear;
            viewport.AddComponent<Mask>().showMaskGraphic = false;

            // Content
            GameObject content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            RectTransform contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0, 1);
            contentRect.sizeDelta = new Vector2(0, 300);

            scrollRect.viewport = vpRect;
            scrollRect.content = contentRect;

            return scrollGO;
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
