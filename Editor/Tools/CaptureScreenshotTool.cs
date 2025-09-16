using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using McpUnity.Unity;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    /// <summary>
    /// MCP tool for capturing screenshots from Unity
    /// </summary>
    public class CaptureScreenshotTool : McpToolBase
    {
        public CaptureScreenshotTool()
        {
            Name = "capture_screenshot";
            Description = "Captures a screenshot from Unity's Scene or Game view";
        }
        
        /// <summary>
        /// Execute the screenshot capture tool with the provided parameters synchronously
        /// </summary>
        /// <param name="parameters">Tool parameters as a JObject</param>
        public override JObject Execute(JObject parameters)
        {
            try
            {
                // Parse parameters
                string viewType = parameters["viewType"]?.ToObject<string>() ?? "game";
                int width = parameters["width"]?.ToObject<int>() ?? 0;
                int height = parameters["height"]?.ToObject<int>() ?? 0;
                bool saveToFile = parameters["saveToFile"]?.ToObject<bool>() ?? false;
                string filePath = parameters["filePath"]?.ToObject<string>();
                
                // Validate view type
                if (!IsValidViewType(viewType))
                {
                    return JObject.FromObject(new
                    {
                        content = new[]
                        {
                            new
                            {
                                type = "text",
                                text = $"Invalid viewType: {viewType}. Must be 'scene', 'game', or 'both'"
                            }
                        },
                        isError = true
                    });
                }
                
                Dictionary<string, object> result;
                
                if (saveToFile)
                {
                    result = ScreenshotCapture.SaveScreenshot(viewType, filePath);
                }
                else
                {
                    result = ScreenshotCapture.CaptureScreenshot(viewType, width, height);
                }
                
                if ((bool)result["success"])
                {
                    // Return proper MCP format with content array
                    if (result.ContainsKey("screenshots"))
                    {
                        var screenshots = result["screenshots"] as List<Dictionary<string, object>>;
                        if (screenshots != null && screenshots.Count > 0)
                        {
                            // Create MCP-compliant content array
                            var contentArray = new List<object>();

                            foreach (var screenshot in screenshots)
                            {
                                var dataUrl = screenshot["data"] as string;
                                if (!string.IsNullOrEmpty(dataUrl))
                                {
                                    // Extract base64 data from data URL
                                    string base64Data = dataUrl;
                                    if (dataUrl.StartsWith("data:image/png;base64,"))
                                    {
                                        base64Data = dataUrl.Substring("data:image/png;base64,".Length);
                                    }

                                    contentArray.Add(new
                                    {
                                        type = "image",
                                        data = base64Data,
                                        mimeType = "image/png"
                                    });
                                }

                                // Also add text description
                                contentArray.Add(new
                                {
                                    type = "text",
                                    text = $"Screenshot captured from {screenshot["type"]} view " +
                                           $"({screenshot["width"]}x{screenshot["height"]}) at {screenshot.GetValueOrDefault("timestamp", DateTime.UtcNow.ToString("o"))}"
                                });
                            }

                            return JObject.FromObject(new
                            {
                                content = contentArray,
                                isError = false
                            });
                        }
                    }
                    else if (result.ContainsKey("files"))
                    {
                        return JObject.FromObject(new
                        {
                            content = new[]
                            {
                                new
                                {
                                    type = "text",
                                    text = result["message"] as string
                                }
                            },
                            isError = false
                        });
                    }
                }
                
                return JObject.FromObject(new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = result.ContainsKey("error") ? result["error"] as string : "Screenshot capture failed"
                        }
                    },
                    isError = true
                });
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Screenshot tool error: {ex}");
                return JObject.FromObject(new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = $"Screenshot capture failed: {ex.Message}"
                        }
                    },
                    isError = true
                });
            }
        }
        
        private bool IsValidViewType(string viewType)
        {
            return viewType == "scene" || viewType == "game" || viewType == "both";
        }
    }
}