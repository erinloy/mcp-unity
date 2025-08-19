using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace McpUnity.Tools
{
    /// <summary>
    /// Handles screenshot capture for both Scene view and Game view
    /// </summary>
    public static class ScreenshotCapture
    {
        /// <summary>
        /// Captures a screenshot from the specified view
        /// </summary>
        /// <param name="viewType">Type of view to capture: "scene", "game", or "both"</param>
        /// <param name="width">Width of the screenshot (0 for current size)</param>
        /// <param name="height">Height of the screenshot (0 for current size)</param>
        /// <returns>Base64 encoded screenshot data with metadata</returns>
        public static Dictionary<string, object> CaptureScreenshot(string viewType = "game", int width = 0, int height = 0)
        {
            var result = new Dictionary<string, object>();
            var screenshots = new List<Dictionary<string, object>>();

            try
            {
                if (viewType == "scene" || viewType == "both")
                {
                    var sceneScreenshot = CaptureSceneView(width, height);
                    if (sceneScreenshot != null)
                    {
                        screenshots.Add(new Dictionary<string, object>
                        {
                            ["type"] = "scene",
                            ["data"] = sceneScreenshot["data"],
                            ["width"] = sceneScreenshot["width"],
                            ["height"] = sceneScreenshot["height"],
                            ["timestamp"] = DateTime.UtcNow.ToString("o")
                        });
                    }
                }

                if (viewType == "game" || viewType == "both")
                {
                    var gameScreenshot = CaptureGameView(width, height);
                    if (gameScreenshot != null)
                    {
                        screenshots.Add(new Dictionary<string, object>
                        {
                            ["type"] = "game",
                            ["data"] = gameScreenshot["data"],
                            ["width"] = gameScreenshot["width"],
                            ["height"] = gameScreenshot["height"],
                            ["timestamp"] = DateTime.UtcNow.ToString("o")
                        });
                    }
                }

                if (screenshots.Count == 0)
                {
                    result["success"] = false;
                    result["error"] = "No screenshots could be captured. Ensure the requested view is open.";
                }
                else
                {
                    result["success"] = true;
                    result["screenshots"] = screenshots;
                    result["count"] = screenshots.Count;
                }
            }
            catch (Exception ex)
            {
                result["success"] = false;
                result["error"] = $"Screenshot capture failed: {ex.Message}";
                Debug.LogError($"[MCP Unity] Screenshot capture error: {ex}");
            }

            return result;
        }

        private static Dictionary<string, object> CaptureSceneView(int width, int height)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                Debug.LogWarning("[MCP Unity] No active Scene view found");
                return null;
            }

            try
            {
                // Get the scene camera
                var camera = sceneView.camera;
                if (camera == null)
                {
                    Debug.LogWarning("[MCP Unity] Scene camera not available");
                    return null;
                }

                // Determine capture dimensions
                int captureWidth = width > 0 ? width : (int)sceneView.position.width;
                int captureHeight = height > 0 ? height : (int)sceneView.position.height;

                // Create render texture
                var renderTexture = RenderTexture.GetTemporary(captureWidth, captureHeight, 24);
                var previousRT = camera.targetTexture;
                var previousActive = RenderTexture.active;

                try
                {
                    camera.targetTexture = renderTexture;
                    camera.Render();

                    RenderTexture.active = renderTexture;
                    var texture2D = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);
                    texture2D.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
                    texture2D.Apply();

                    // Convert to PNG and then to base64
                    byte[] pngData = texture2D.EncodeToPNG();
                    string base64Data = Convert.ToBase64String(pngData);

                    // Clean up
                    UnityEngine.Object.DestroyImmediate(texture2D);

                    return new Dictionary<string, object>
                    {
                        ["data"] = $"data:image/png;base64,{base64Data}",
                        ["width"] = captureWidth,
                        ["height"] = captureHeight
                    };
                }
                finally
                {
                    camera.targetTexture = previousRT;
                    RenderTexture.active = previousActive;
                    RenderTexture.ReleaseTemporary(renderTexture);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP Unity] Failed to capture Scene view: {ex.Message}");
                return null;
            }
        }

        private static Dictionary<string, object> CaptureGameView(int width, int height)
        {
            try
            {
                // Use ScreenCapture for Game view
                string tempPath = Path.Combine(Path.GetTempPath(), $"unity_mcp_screenshot_{Guid.NewGuid()}.png");
                
                // Capture the game view
                ScreenCapture.CaptureScreenshot(tempPath);
                
                // Wait for the file to be written (Unity writes it asynchronously)
                // Try multiple times with increasing delays
                int maxAttempts = 10;
                for (int i = 0; i < maxAttempts; i++)
                {
                    System.Threading.Thread.Sleep(100);
                    if (File.Exists(tempPath))
                        break;
                }
                
                if (!File.Exists(tempPath))
                {
                    Debug.LogWarning("[MCP Unity] Screenshot file was not created after waiting 1 second");
                    // Fallback: try to capture using a different method
                    return CaptureGameViewAlternative();
                }

                // Read the file and convert to base64
                byte[] pngData = File.ReadAllBytes(tempPath);
                string base64Data = Convert.ToBase64String(pngData);
                
                // Get dimensions from the PNG data
                var dimensions = GetPngDimensions(pngData);
                
                // Clean up temp file
                File.Delete(tempPath);

                return new Dictionary<string, object>
                {
                    ["data"] = $"data:image/png;base64,{base64Data}",
                    ["width"] = dimensions.Item1,
                    ["height"] = dimensions.Item2
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP Unity] Failed to capture Game view: {ex.Message}");
                return null;
            }
        }

        private static Dictionary<string, object> CaptureGameViewAlternative()
        {
            try
            {
                // Alternative method: capture the Game view using RenderTexture
                var gameView = EditorWindow.GetWindow(System.Type.GetType("UnityEditor.GameView,UnityEditor"));
                if (gameView == null)
                {
                    Debug.LogWarning("[MCP Unity] Game view is not open");
                    return null;
                }
                
                // Use reflection to get the game view's render texture
                var gameViewType = gameView.GetType();
                var renderTextureProperty = gameViewType.GetProperty("targetTexture", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (renderTextureProperty == null)
                {
                    // Fallback: return a simple error placeholder
                    return new Dictionary<string, object>
                    {
                        ["type"] = "game",
                        ["data"] = "data:image/png;base64,",  // Empty image
                        ["width"] = 0,
                        ["height"] = 0,
                        ["error"] = "Could not capture Game view"
                    };
                }
                
                // Return placeholder for now
                return new Dictionary<string, object>
                {
                    ["type"] = "game",
                    ["data"] = "data:image/png;base64,",  // Empty image
                    ["width"] = 0,
                    ["height"] = 0,
                    ["error"] = "Game view capture requires Unity to be in Play mode or focused"
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP Unity] Failed to capture Game view (alternative): {ex.Message}");
                return null;
            }
        }
        
        private static (int, int) GetPngDimensions(byte[] pngData)
        {
            // PNG dimensions are stored at bytes 16-23
            if (pngData.Length < 24) return (0, 0);
            
            int width = (pngData[16] << 24) | (pngData[17] << 16) | (pngData[18] << 8) | pngData[19];
            int height = (pngData[20] << 24) | (pngData[21] << 16) | (pngData[22] << 8) | pngData[23];
            
            return (width, height);
        }

        /// <summary>
        /// Saves a screenshot to disk
        /// </summary>
        public static Dictionary<string, object> SaveScreenshot(string viewType = "game", string filePath = null)
        {
            var result = new Dictionary<string, object>();
            
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    string fileName = $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
                    filePath = Path.Combine(Application.dataPath, "..", "Screenshots", fileName);
                }

                // Ensure directory exists
                string directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Capture the screenshot
                var screenshot = CaptureScreenshot(viewType, 0, 0);
                if (!(bool)screenshot["success"])
                {
                    return screenshot;
                }

                var screenshots = screenshot["screenshots"] as List<Dictionary<string, object>>;
                var savedFiles = new List<string>();

                foreach (var shot in screenshots)
                {
                    string dataUrl = shot["data"] as string;
                    string base64Data = dataUrl.Substring(dataUrl.IndexOf(',') + 1);
                    byte[] pngData = Convert.FromBase64String(base64Data);
                    
                    string shotType = shot["type"] as string;
                    string shotPath = filePath;
                    
                    if (screenshots.Count > 1)
                    {
                        // Add type suffix if capturing both views
                        shotPath = filePath.Replace(".png", $"_{shotType}.png");
                    }
                    
                    File.WriteAllBytes(shotPath, pngData);
                    savedFiles.Add(shotPath);
                    
                    Debug.Log($"[MCP Unity] Screenshot saved: {shotPath}");
                }

                result["success"] = true;
                result["files"] = savedFiles;
                result["message"] = $"Saved {savedFiles.Count} screenshot(s)";
            }
            catch (Exception ex)
            {
                result["success"] = false;
                result["error"] = $"Failed to save screenshot: {ex.Message}";
            }

            return result;
        }
    }
}