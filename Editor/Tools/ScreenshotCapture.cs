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
        // Force a repaint before capture to ensure views are up to date
        private static void ForceRepaintAllViews()
        {
            SceneView.RepaintAll();

            // Force Game view repaint
            System.Type gameViewType = System.Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType != null)
            {
                EditorWindow gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
                if (gameView != null)
                {
                    gameView.Repaint();
                }
            }

            // Force Unity to process the repaint immediately
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }
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
                // Force views to repaint before capture
                ForceRepaintAllViews();
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
                // Try to get or create a scene view
                sceneView = SceneView.sceneViews.Count > 0 ?
                    SceneView.sceneViews[0] as SceneView :
                    EditorWindow.GetWindow<SceneView>();

                if (sceneView == null)
                {
                    Debug.LogWarning("[MCP Unity] No Scene view available");
                    return null;
                }
            }

            try
            {
                // Force scene view to update
                sceneView.Focus();
                sceneView.Repaint();

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
                var gameViewType = System.Type.GetType("UnityEditor.GameView,UnityEditor");
                if (gameViewType == null)
                {
                    Debug.LogError("[MCP Unity] Cannot find GameView type");
                    return null;
                }

                var gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
                if (gameView == null)
                {
                    Debug.LogError("[MCP Unity] Cannot get Game view window");
                    return null;
                }

                // Force focus and repaint
                gameView.Focus();
                gameView.Repaint();

                // Wait a frame for the repaint to complete
                System.Threading.Thread.Sleep(50);

                // Try to get the RenderTexture using reflection
                RenderTexture renderTexture = null;

                // Method 1: Try to get via targetRenderTexture property
                var targetRenderTextureProp = gameViewType.GetProperty("targetRenderTexture",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (targetRenderTextureProp != null)
                {
                    renderTexture = targetRenderTextureProp.GetValue(gameView) as RenderTexture;
                }

                // Method 2: Try via GetMainGameViewRenderTexture static method
                if (renderTexture == null)
                {
                    var getMainRenderTextureMethod = gameViewType.GetMethod("GetMainGameViewRenderTexture",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (getMainRenderTextureMethod != null)
                    {
                        renderTexture = getMainRenderTextureMethod.Invoke(null, null) as RenderTexture;
                    }
                }

                // Method 3: Try via GetRenderTexture instance method
                if (renderTexture == null)
                {
                    var getRenderTextureMethod = gameViewType.GetMethod("GetRenderTexture",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (getRenderTextureMethod != null)
                    {
                        renderTexture = getRenderTextureMethod.Invoke(gameView, null) as RenderTexture;
                    }
                }

                if (renderTexture == null)
                {
                    Debug.LogError("[MCP Unity] Cannot access Game view render texture. Make sure the Game view is visible and rendering.");
                    return null;
                }

                // Capture from the render texture
                int captureWidth = width > 0 ? width : renderTexture.width;
                int captureHeight = height > 0 ? height : renderTexture.height;

                var previousActive = RenderTexture.active;
                RenderTexture.active = renderTexture;

                try
                {
                    var texture2D = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);
                    texture2D.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
                    texture2D.Apply();

                    byte[] pngData = texture2D.EncodeToPNG();
                    string base64Data = Convert.ToBase64String(pngData);

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
                    RenderTexture.active = previousActive;
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[MCP Unity] Editor capture method failed (expected in some Unity versions): {ex.Message}");
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