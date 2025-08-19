using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using System.Diagnostics;
using System.Text;

// Note: These would come from the MCP C# SDK NuGet package
// For Unity, we'd need to import the DLLs or use Unity Package Manager
// Placeholder interfaces until we set up proper package references
namespace ModelContextProtocol.Core.Server
{
    // These are simplified placeholders - real SDK has full implementations
    public interface IMcpServer { }
    public class McpServer : IMcpServer 
    { 
        public McpServer(string name, string version) { }
        public Task ConnectAsync(IServerTransport transport) => Task.CompletedTask;
    }
    
    public interface IServerTransport { }
    public class StdioServerTransport : IServerTransport { }
    
    public abstract class McpServerTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract Task<object> ExecuteAsync(object request, CancellationToken ct);
    }
}

namespace McpUnity.DirectMcp
{
    /// <summary>
    /// Direct Unity MCP Server using the C# SDK
    /// Eliminates the need for Node.js bridge
    /// </summary>
    public class UnityMcpServer : IDisposable
    {
        private static UnityMcpServer _instance;
        private Process _proxyProcess;
        private bool _isRunning;
        private readonly Dictionary<string, IUnityMcpTool> _tools = new();
        private CancellationTokenSource _cancellationTokenSource;
        
        public static UnityMcpServer Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new UnityMcpServer();
                }
                return _instance;
            }
        }
        
        private UnityMcpServer()
        {
            RegisterTools();
            EditorApplication.quitting += Dispose;
        }
        
        /// <summary>
        /// Start the MCP server as a subprocess for McpProxy to connect to
        /// </summary>
        public void StartAsSubprocess()
        {
            if (_isRunning) return;
            
            _cancellationTokenSource = new CancellationTokenSource();
            
            // Start a separate process that acts as the MCP server
            var startInfo = new ProcessStartInfo
            {
                FileName = EditorApplication.applicationPath,
                Arguments = $"-batchmode -nographics -projectPath \"{Application.dataPath}/..\" -executeMethod McpUnity.DirectMcp.UnityMcpServer.RunStdioServer",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            _proxyProcess = Process.Start(startInfo);
            _isRunning = true;
            
            UnityEngine.Debug.Log("[UnityMcpServer] Started MCP server subprocess");
        }
        
        /// <summary>
        /// Run as stdio MCP server (called by subprocess)
        /// </summary>
        public static async void RunStdioServer()
        {
            try
            {
                var server = new ModelContextProtocol.Core.Server.McpServer(
                    "Unity MCP Server",
                    "1.0.0"
                );
                
                // Register all tools with the MCP server
                var instance = Instance;
                foreach (var tool in instance._tools.Values)
                {
                    // In real implementation, register with MCP SDK
                    // server.RegisterTool(tool.ToMcpTool());
                }
                
                // Create stdio transport
                var transport = new ModelContextProtocol.Core.Server.StdioServerTransport();
                
                // Connect and run
                await server.ConnectAsync(transport);
                
                // Keep running until terminated
                await Task.Delay(Timeout.Infinite);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"MCP Server error: {ex}");
                EditorApplication.Exit(1);
            }
        }
        
        /// <summary>
        /// Register all Unity tools
        /// </summary>
        private void RegisterTools()
        {
            // Register built-in tools
            RegisterTool(new DirectCaptureScreenshotTool());
            RegisterTool(new DirectExecuteMenuItemTool());
            RegisterTool(new DirectSelectGameObjectTool());
            // Add more tools as needed
        }
        
        /// <summary>
        /// Register a tool with the server
        /// </summary>
        public void RegisterTool(IUnityMcpTool tool)
        {
            _tools[tool.Name] = tool;
            UnityEngine.Debug.Log($"[UnityMcpServer] Registered tool: {tool.Name}");
        }
        
        public void Stop()
        {
            if (!_isRunning) return;
            
            _cancellationTokenSource?.Cancel();
            _proxyProcess?.Kill();
            _proxyProcess?.Dispose();
            _proxyProcess = null;
            _isRunning = false;
            
            UnityEngine.Debug.Log("[UnityMcpServer] Stopped MCP server");
        }
        
        public void Dispose()
        {
            Stop();
            EditorApplication.quitting -= Dispose;
            _instance = null;
        }
    }
    
    /// <summary>
    /// Base interface for Unity MCP tools
    /// </summary>
    public interface IUnityMcpTool
    {
        string Name { get; }
        string Description { get; }
        Task<object> ExecuteAsync(Dictionary<string, object> parameters, CancellationToken cancellationToken);
    }
    
    /// <summary>
    /// Direct implementation of screenshot tool for MCP
    /// </summary>
    public class DirectCaptureScreenshotTool : IUnityMcpTool
    {
        public string Name => "capture_screenshot";
        public string Description => "Captures a screenshot from Unity's Scene or Game view";
        
        public async Task<object> ExecuteAsync(Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            // Run on Unity main thread
            var tcs = new TaskCompletionSource<object>();
            
            await Task.Run(() =>
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    try
                    {
                        var viewType = parameters.ContainsKey("viewType") 
                            ? parameters["viewType"].ToString() 
                            : "game";
                        
                        var result = Tools.ScreenshotCapture.CaptureScreenshot(viewType, 0, 0);
                        
                        // Return MCP-formatted response
                        tcs.SetResult(new
                        {
                            content = new[]
                            {
                                new
                                {
                                    type = "image",
                                    data = result["data"],
                                    mimeType = "image/png"
                                }
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                };
            }, cancellationToken);
            
            return await tcs.Task;
        }
    }
    
    /// <summary>
    /// Direct implementation of menu item execution tool
    /// </summary>
    public class DirectExecuteMenuItemTool : IUnityMcpTool
    {
        public string Name => "execute_menu_item";
        public string Description => "Executes a Unity menu item by path";
        
        public async Task<object> ExecuteAsync(Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<object>();
            
            await Task.Run(() =>
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    try
                    {
                        var menuPath = parameters["menuPath"].ToString();
                        var success = EditorApplication.ExecuteMenuItem(menuPath);
                        
                        tcs.SetResult(new
                        {
                            content = new[]
                            {
                                new
                                {
                                    type = "text",
                                    text = success 
                                        ? $"Successfully executed menu item: {menuPath}"
                                        : $"Failed to execute menu item: {menuPath}"
                                }
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                };
            }, cancellationToken);
            
            return await tcs.Task;
        }
    }
    
    /// <summary>
    /// Direct implementation of GameObject selection tool
    /// </summary>
    public class DirectSelectGameObjectTool : IUnityMcpTool
    {
        public string Name => "select_gameobject";
        public string Description => "Selects a GameObject in the Unity editor";
        
        public async Task<object> ExecuteAsync(Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<object>();
            
            await Task.Run(() =>
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    try
                    {
                        GameObject target = null;
                        
                        if (parameters.ContainsKey("objectPath"))
                        {
                            target = GameObject.Find(parameters["objectPath"].ToString());
                        }
                        else if (parameters.ContainsKey("objectName"))
                        {
                            target = GameObject.Find(parameters["objectName"].ToString());
                        }
                        
                        if (target != null)
                        {
                            Selection.activeGameObject = target;
                            EditorGUIUtility.PingObject(target);
                            
                            tcs.SetResult(new
                            {
                                content = new[]
                                {
                                    new
                                    {
                                        type = "text",
                                        text = $"Selected GameObject: {target.name}"
                                    }
                                }
                            });
                        }
                        else
                        {
                            tcs.SetResult(new
                            {
                                content = new[]
                                {
                                    new
                                    {
                                        type = "text",
                                        text = "GameObject not found"
                                    }
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                };
            }, cancellationToken);
            
            return await tcs.Task;
        }
    }
    
    /// <summary>
    /// Editor window for managing the Unity MCP Server
    /// </summary>
    public class UnityMcpServerWindow : EditorWindow
    {
        [MenuItem("Tools/MCP Unity/Direct Server Control")]
        public static void ShowWindow()
        {
            GetWindow<UnityMcpServerWindow>("Unity MCP Server");
        }
        
        private void OnGUI()
        {
            EditorGUILayout.LabelField("Unity MCP Server (Direct)", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            
            EditorGUILayout.HelpBox(
                "This server connects directly to MCP without Node.js.\n" +
                "It provides better performance and simpler architecture.",
                MessageType.Info
            );
            
            EditorGUILayout.Space();
            
            if (GUILayout.Button("Start Server"))
            {
                UnityMcpServer.Instance.StartAsSubprocess();
            }
            
            if (GUILayout.Button("Stop Server"))
            {
                UnityMcpServer.Instance.Stop();
            }
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Registered Tools:", EditorStyles.boldLabel);
            
            // List registered tools
            EditorGUILayout.HelpBox(
                "• capture_screenshot\n" +
                "• execute_menu_item\n" +
                "• select_gameobject",
                MessageType.None
            );
        }
    }
}