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
                        success = false,
                        type = "error",
                        message = $"Invalid viewType: {viewType}. Must be 'scene', 'game', or 'both'"
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
                    // For MCP, we should return the image data in the proper format
                    // MCP supports images as base64-encoded data URLs or binary content
                    
                    if (result.ContainsKey("screenshots"))
                    {
                        var screenshots = result["screenshots"] as List<Dictionary<string, object>>;
                        if (screenshots != null && screenshots.Count > 0)
                        {
                            // For single screenshot, return directly
                            if (screenshots.Count == 1)
                            {
                                var screenshot = screenshots[0];
                                return JObject.FromObject(new
                                {
                                    success = true,
                                    type = "image",
                                    data = screenshot,
                                    message = $"Captured {screenshot["type"]} view screenshot"
                                });
                            }
                            else
                            {
                                // For multiple screenshots, return as array
                                return JObject.FromObject(new
                                {
                                    success = true,
                                    type = "images",
                                    data = result,
                                    message = $"Captured {screenshots.Count} screenshots"
                                });
                            }
                        }
                    }
                    else if (result.ContainsKey("files"))
                    {
                        return JObject.FromObject(new
                        {
                            success = true,
                            type = "files",
                            data = result,
                            message = result["message"] as string
                        });
                    }
                }
                
                return JObject.FromObject(new
                {
                    success = false,
                    type = "error",
                    message = result.ContainsKey("error") ? result["error"] as string : "Screenshot capture failed"
                });
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Screenshot tool error: {ex}");
                return JObject.FromObject(new
                {
                    success = false,
                    type = "error",
                    message = $"Screenshot capture failed: {ex.Message}"
                });
            }
        }
        
        private bool IsValidViewType(string viewType)
        {
            return viewType == "scene" || viewType == "game" || viewType == "both";
        }
    }
}