using System;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace McpUnity.DirectMcp
{
    /// <summary>
    /// Unity-side executor for MCP commands
    /// These methods are called by the standalone MCP server via Unity batch mode
    /// </summary>
    public static class UnityRemoteExecutor
    {
        [MenuItem("Tools/MCP Unity/Test Connection")]
        public static void TestConnection()
        {
            Debug.Log("[MCP] Unity MCP Remote Executor is working!");
        }

        /// <summary>
        /// Capture screenshot and return result
        /// Called via: Unity.exe -executeMethod McpUnity.DirectMcp.UnityRemoteExecutor.CaptureScreenshot
        /// </summary>
        public static void CaptureScreenshot(string viewType = "game", int width = 0, int height = 0)
        {
            try
            {
                string fileName = $"screenshot_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
                string filePath = System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "Screenshots", fileName);
                
                // Ensure screenshots directory exists
                string directory = System.IO.Path.GetDirectoryName(filePath);
                if (!System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                // Use Unity's built-in screenshot capture
                ScreenCapture.CaptureScreenshot(filePath);
                
                Console.WriteLine($"Screenshot captured: {viewType} view");
                Console.WriteLine($"Saved to: {filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error capturing screenshot: {ex.Message}");
            }
        }

        /// <summary>
        /// Execute Unity menu item
        /// </summary>
        public static void ExecuteMenuItem(string menuPath)
        {
            try
            {
                bool success = EditorApplication.ExecuteMenuItem(menuPath);
                Console.WriteLine(success 
                    ? $"Successfully executed menu item: {menuPath}"
                    : $"Failed to execute menu item: {menuPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing menu item: {ex.Message}");
            }
        }

        /// <summary>
        /// Select GameObject in editor
        /// </summary>
        public static void SelectGameObject(string objectName)
        {
            try
            {
                var target = GameObject.Find(objectName);
                if (target != null)
                {
                    Selection.activeGameObject = target;
                    Console.WriteLine($"Selected GameObject: {target.name}");
                }
                else
                {
                    Console.WriteLine($"GameObject not found: {objectName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error selecting GameObject: {ex.Message}");
            }
        }

        /// <summary>
        /// Get Unity project information
        /// </summary>
        public static void GetProjectInfo()
        {
            try
            {
                var info = $"Project: {Application.productName}\n" +
                           $"Version: {Application.version}\n" +
                           $"Unity Version: {Application.unityVersion}\n" +
                           $"Platform: {Application.platform}\n" +
                           $"Data Path: {Application.dataPath}";
                
                Console.WriteLine(info);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting project info: {ex.Message}");
            }
        }

        /// <summary>
        /// List GameObjects in current scene
        /// </summary>
        public static void ListGameObjects()
        {
            try
            {
                var gameObjects = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>()
                    .Where(go => go.scene.isLoaded)
                    .Select(go => $"{go.name} ({go.transform.position})")
                    .Take(50);
                
                var result = string.Join("\n", gameObjects);
                Console.WriteLine($"GameObjects in scene:\n{result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error listing GameObjects: {ex.Message}");
            }
        }
    }
}