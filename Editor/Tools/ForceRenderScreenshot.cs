using System;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace McpUnity.Tools
{
    /// <summary>
    /// Experimental forced rendering for screenshots when Unity is not focused
    /// </summary>
    public static class ForceRenderScreenshot
    {
        [MenuItem("Tools/MCP Unity/Test Force Render Screenshot")]
        public static void TestForceRender()
        {
            Debug.Log("[Force Render] Testing forced screenshot capture...");

            try
            {
                // Method 1: Force Game View render using internal APIs
                var gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
                if (gameViewType != null)
                {
                    var gameView = EditorWindow.GetWindow(gameViewType, false);

                    // Try to force enable rendering
                    var renderingEnabledProp = gameViewType.GetProperty("renderingEnabled",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (renderingEnabledProp != null)
                    {
                        renderingEnabledProp.SetValue(gameView, true);
                        Debug.Log("[Force Render] Set renderingEnabled = true");
                    }

                    // Try to force maximize on play
                    var maximizeOnPlayProp = gameViewType.GetProperty("maximizeOnPlay",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (maximizeOnPlayProp != null)
                    {
                        var currentValue = (bool)maximizeOnPlayProp.GetValue(gameView);
                        Debug.Log($"[Force Render] maximizeOnPlay = {currentValue}");
                    }

                    // Force repaint all views
                    gameView.Focus();
                    gameView.Repaint();
                    gameView.SendEvent(EditorGUIUtility.CommandEvent("FrameSelected"));

                    // Try to force render via internal method
                    var renderViewMethod = gameViewType.GetMethod("RenderView",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (renderViewMethod != null)
                    {
                        renderViewMethod.Invoke(gameView, new object[] { true, true });
                        Debug.Log("[Force Render] Called RenderView(true, true)");
                    }

                    // Try ConfigureTargetTexture to force texture creation
                    var configureTargetTextureMethod = gameViewType.GetMethod("ConfigureTargetTexture",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (configureTargetTextureMethod != null)
                    {
                        // Get current width/height
                        var position = gameView.position;
                        configureTargetTextureMethod.Invoke(gameView, new object[] { (int)position.width, (int)position.height });
                        Debug.Log("[Force Render] Called ConfigureTargetTexture");
                    }

                    // Now try to get the RenderTexture
                    RenderTexture renderTexture = GetGameViewRenderTexture(gameViewType, gameView);
                    if (renderTexture != null)
                    {
                        Debug.Log($"[Force Render] Got RenderTexture: {renderTexture.width}x{renderTexture.height}");

                        // Capture it
                        var screenshot = CaptureFromRenderTexture(renderTexture);
                        if (screenshot != null)
                        {
                            Debug.Log($"[Force Render] ✓ Screenshot captured: {screenshot.width}x{screenshot.height}");

                            // Save test file
                            var bytes = screenshot.EncodeToPNG();
                            var path = System.IO.Path.Combine(Application.dataPath, "..", "force_render_test.png");
                            System.IO.File.WriteAllBytes(path, bytes);
                            Debug.Log($"[Force Render] Saved to: {path}");

                            UnityEngine.Object.DestroyImmediate(screenshot);
                        }
                    }
                    else
                    {
                        Debug.LogWarning("[Force Render] Could not get RenderTexture");
                    }
                }

                // Method 2: Try using Camera.Render() directly
                TestCameraForceRender();

                // Method 3: Try using RenderPipeline
                TestRenderPipelineForce();

            }
            catch (Exception ex)
            {
                Debug.LogError($"[Force Render] Error: {ex}");
            }
        }

        private static void TestCameraForceRender()
        {
            Debug.Log("[Force Render] Testing Camera.Render() method...");

            // Find or create a camera
            Camera camera = Camera.main;
            if (camera == null)
            {
                camera = UnityEngine.Object.FindFirstObjectByType<Camera>();
            }

            if (camera != null)
            {
                // Create a RenderTexture
                var rt = RenderTexture.GetTemporary(1920, 1080, 24);
                var prevTarget = camera.targetTexture;
                var prevActive = RenderTexture.active;

                try
                {
                    camera.targetTexture = rt;

                    // Force the camera to render NOW
                    camera.Render();

                    RenderTexture.active = rt;

                    var screenshot = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    screenshot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    screenshot.Apply();

                    Debug.Log($"[Force Render] Camera.Render() captured: {screenshot.width}x{screenshot.height}");

                    // Check if it's not just black
                    var pixels = screenshot.GetPixels32();
                    bool hasContent = false;
                    foreach (var pixel in pixels)
                    {
                        if (pixel.r > 0 || pixel.g > 0 || pixel.b > 0)
                        {
                            hasContent = true;
                            break;
                        }
                    }

                    Debug.Log($"[Force Render] Camera render has content: {hasContent}");

                    UnityEngine.Object.DestroyImmediate(screenshot);
                }
                finally
                {
                    camera.targetTexture = prevTarget;
                    RenderTexture.active = prevActive;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
        }

        private static void TestRenderPipelineForce()
        {
            Debug.Log("[Force Render] Testing RenderPipeline.Render()...");

            var rpAsset = GraphicsSettings.currentRenderPipeline;
            if (rpAsset != null)
            {
                Debug.Log($"[Force Render] Current render pipeline: {rpAsset.GetType().Name}");

                // Try to force a render via RenderPipelineManager
                try
                {
                    // This approach requires specific parameters that aren't available here
                    Debug.Log("[Force Render] RenderPipelineManager approach not available with current API");
                }
                catch (Exception ex)
                {
                    Debug.Log($"[Force Render] RenderPipelineManager error: {ex.Message}");
                }
            }

            // Try RequestAsyncReadback to force GPU sync
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                Debug.Log("[Force Render] System supports AsyncGPUReadback");
            }
        }

        private static RenderTexture GetGameViewRenderTexture(Type gameViewType, EditorWindow gameView)
        {
            // Try multiple methods to get the RenderTexture
            var methods = new[]
            {
                "GetMainGameViewTargetTexture",
                "GetMainGameViewRenderTexture",
                "GetRenderTexture",
                "targetTexture"
            };

            foreach (var methodName in methods)
            {
                try
                {
                    // Try as static method
                    var staticMethod = gameViewType.GetMethod(methodName,
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                    if (staticMethod != null)
                    {
                        var result = staticMethod.Invoke(null, null) as RenderTexture;
                        if (result != null)
                        {
                            Debug.Log($"[Force Render] Got RenderTexture via static {methodName}");
                            return result;
                        }
                    }

                    // Try as instance method
                    var instanceMethod = gameViewType.GetMethod(methodName,
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (instanceMethod != null)
                    {
                        var result = instanceMethod.Invoke(gameView, null) as RenderTexture;
                        if (result != null)
                        {
                            Debug.Log($"[Force Render] Got RenderTexture via instance {methodName}");
                            return result;
                        }
                    }

                    // Try as property
                    var prop = gameViewType.GetProperty(methodName,
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null)
                    {
                        var result = prop.GetValue(gameView) as RenderTexture;
                        if (result != null)
                        {
                            Debug.Log($"[Force Render] Got RenderTexture via property {methodName}");
                            return result;
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        private static Texture2D CaptureFromRenderTexture(RenderTexture rt)
        {
            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;

            try
            {
                var screenshot = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                screenshot.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                screenshot.Apply();
                return screenshot;
            }
            finally
            {
                RenderTexture.active = prevActive;
            }
        }
    }
}