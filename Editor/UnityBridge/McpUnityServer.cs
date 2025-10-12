using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEditor;
using McpUnity.Tools;
using McpUnity.Tools.Attributes;
using McpUnity.Resources;
using McpUnity.Services;
using McpUnity.Utils;
using WebSocketSharp.Server;
using System.IO;
using System.Diagnostics;
using System.Net.Sockets;

namespace McpUnity.Unity
{
    /// <summary>
    /// MCP Unity Server to communicate Node.js MCP server.
    /// Uses WebSockets to communicate with Node.js.
    /// </summary>
    [InitializeOnLoad]
    public class McpUnityServer : IDisposable
    {
        private static McpUnityServer _instance;
        
        private readonly Dictionary<string, McpToolBase> _tools = new Dictionary<string, McpToolBase>();
        private readonly Dictionary<string, McpResourceBase> _resources = new Dictionary<string, McpResourceBase>();
        
        private WebSocketServer _webSocketServer;
        private CancellationTokenSource _cts;
        private TestRunnerService _testRunnerService;
        private ConsoleLogsService _consoleLogsService;

        /// <summary>
        /// Static constructor that gets called when Unity loads due to InitializeOnLoad attribute
        /// </summary>
        static McpUnityServer()
        {
            EditorApplication.delayCall += () => {
                // Ensure Instance is created and hooks are set up after initial domain load
                var currentInstance = Instance;
            };
        }
        
        /// <summary>
        /// Singleton instance accessor
        /// </summary>
        public static McpUnityServer Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new McpUnityServer();
                }
                return _instance;
            }
        }

        /// <summary>
        /// Current Listening state
        /// </summary>
        public bool IsListening => _webSocketServer?.IsListening ?? false;

        /// <summary>
        /// Dictionary of connected clients with this server
        /// </summary>
        public Dictionary<string, string> Clients { get; } = new Dictionary<string, string>();

        /// <summary>
        /// Private constructor to enforce singleton pattern
        /// </summary>
        private McpUnityServer()
        {
            EditorApplication.quitting -= OnEditorQuitting; // Prevent multiple subscriptions on domain reload
            EditorApplication.quitting += OnEditorQuitting;

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            InstallServer();
            InitializeServices();
            RegisterResources();
            RegisterTools();

            // Initial start if auto-start is enabled and not recovering from a reload where it was off
            if (McpUnitySettings.Instance.AutoStartServer)
            {
                 StartServer();
            }
        }

        /// <summary>
        /// Disposes the McpUnityServer instance, stopping the WebSocket server and unsubscribing from Unity Editor events.
        /// This method ensures proper cleanup of resources and prevents memory leaks or unexpected behavior during domain reloads or editor shutdown.
        /// </summary>
        public void Dispose()
        {
            StopServer();

            EditorApplication.quitting -= OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

            GC.SuppressFinalize(this);
        }
        
        /// <summary>
        /// Start the WebSocket Server to communicate with Node.js
        /// </summary>
        public void StartServer()
        {
            if (IsListening)
            {
                McpLogger.LogInfo($"Server start requested, but already listening on port {McpUnitySettings.Instance.Port}.");
                return;
            }

            try
            {
                // Always use 127.0.0.1 for now - WebSocketSharp has issues with 0.0.0.0 binding
                var host = "127.0.0.1";
                _webSocketServer = new WebSocketServer($"ws://{host}:{McpUnitySettings.Instance.Port}");
                _webSocketServer.ReuseAddress = true;

                // Enable keepalive to detect stale connections (ping every 30s, timeout after 60s)
                _webSocketServer.KeepClean = true;  // Auto-clean dead connections
                _webSocketServer.WaitTime = TimeSpan.FromSeconds(60);  // Connection timeout

                _webSocketServer.Log.Level = WebSocketSharp.LogLevel.Debug;
                _webSocketServer.Log.Output = (data, path) => {
                    McpLogger.LogInfo($"[WebSocketSharp] {data.Message}");
                };
                _webSocketServer.AddWebSocketService("/McpUnity", () => new McpUnitySocketHandler(this));
                _webSocketServer.Start();
                
                // Verify the server is actually listening
                if (_webSocketServer.IsListening)
                {
                    McpLogger.LogInfo($"✅ WebSocket server verified listening on {host}:{McpUnitySettings.Instance.Port}");
                    McpLogger.LogInfo($"   Endpoint: ws://{host}:{McpUnitySettings.Instance.Port}/McpUnity");
                }
                else
                {
                    McpLogger.LogError($"WebSocket server failed to start listening on {host}:{McpUnitySettings.Instance.Port}");
                    throw new System.InvalidOperationException("WebSocket server is not listening after Start() call");
                }
                
                // Register with service discovery
                ServiceDiscovery.RegisterService(McpUnitySettings.Instance.Port);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                McpLogger.LogError($"Failed to start WebSocket server: Port {McpUnitySettings.Instance.Port} is already in use. {ex.Message}");
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Failed to start WebSocket server: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Stop the WebSocket server (non-blocking for domain reload safety)
        /// </summary>
        public void StopServer()
        {
            if (!IsListening)
            {
                return;
            }

            try
            {
                // Log active connections before stopping
                if (_webSocketServer?.WebSocketServices != null)
                {
                    var servicePath = "/McpUnity";
                    if (_webSocketServer.WebSocketServices.TryGetServiceHost(servicePath, out var host))
                    {
                        var sessionsCount = host.Sessions.Count;
                        if (sessionsCount > 0)
                        {
                            McpLogger.LogInfo($"Stopping server with {sessionsCount} active WebSocket connection(s)");
                        }
                    }
                }

                // Stop the server (this will close all active connections)
                // Don't wait for completion - domain reload has strict time limits
                _webSocketServer?.Stop();

                McpLogger.LogInfo("WebSocket server stopped");
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error during WebSocketServer.Stop(): {ex.Message}");
            }
            finally
            {
                _webSocketServer = null;
                Clients.Clear();
            }
        }
        
        /// <summary>
        /// Try to get a tool by name
        /// </summary>
        public bool TryGetTool(string name, out McpToolBase tool)
        {
            return _tools.TryGetValue(name, out tool);
        }
        
        /// <summary>
        /// Try to get a resource by URI
        /// </summary>
        public bool TryGetResource(string uri, out McpResourceBase resource)
        {
            return _resources.TryGetValue(uri, out resource);
        }
        
        /// <summary>
        /// Get all registered tools for discovery
        /// </summary>
        public Dictionary<string, McpToolBase> GetTools()
        {
            return new Dictionary<string, McpToolBase>(_tools);
        }
        
        /// <summary>
        /// Get all registered resources for discovery
        /// </summary>
        public Dictionary<string, McpResourceBase> GetResources()
        {
            return new Dictionary<string, McpResourceBase>(_resources);
        }

        /// <summary>
        /// Verifies the MCP C# server is available and builds it if necessary.
        /// </summary>
        public void InstallServer()
        {
            string projectRoot = McpUtils.GetUnityProjectRoot();
            string exePath = Path.Combine(projectRoot, "Tools", "unity-mcp", "unity-mcp.exe");

            // Check if executable exists at the new predictable location
            if (File.Exists(exePath))
            {
                McpLogger.LogInfo($"Unity MCP C# server found at: {exePath}");
                return;
            }

            McpLogger.LogWarning($"Unity MCP executable not found at: {exePath}. Attempting to build...");
            
            // Get the source path to build from
            string serverPath = McpUtils.GetServerPath();
            if (string.IsNullOrEmpty(serverPath) || !Directory.Exists(serverPath))
            {
                McpLogger.LogError($"Server source path not found: {serverPath}. Cannot build MCP server.");
                return;
            }
            
            // Check if the project file exists
            string projectPath = Path.Combine(serverPath, "UnityMcp.csproj");
            if (File.Exists(projectPath))
            {
                BuildMcpServer(serverPath, projectPath);
                
                // Check if build succeeded and exe was copied to new location
                if (File.Exists(exePath))
                {
                    McpLogger.LogInfo($"Unity MCP C# server successfully built at: {exePath}");
                }
                else
                {
                    McpLogger.LogError($"Failed to build Unity MCP server or copy to: {exePath}");
                }
            }
            else
            {
                McpLogger.LogError($"Unity MCP project file not found at: {projectPath}");
            }
        }
        
        /// <summary>
        /// Builds the MCP server using dotnet CLI
        /// </summary>
        private void BuildMcpServer(string serverPath, string projectPath)
        {
            try
            {
                McpLogger.LogInfo("Building Unity MCP server...");
                
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"publish \"{projectPath}\" -c Release -r win-x64 --self-contained",
                    WorkingDirectory = serverPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(processInfo))
                {
                    if (process != null)
                    {
                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        if (process.ExitCode == 0)
                        {
                            McpLogger.LogInfo("Unity MCP server build completed successfully.");
                            if (!string.IsNullOrEmpty(output))
                            {
                                McpLogger.LogInfo($"Build output: {output}");
                            }
                        }
                        else
                        {
                            McpLogger.LogError($"Unity MCP server build failed with exit code: {process.ExitCode}");
                            if (!string.IsNullOrEmpty(error))
                            {
                                McpLogger.LogError($"Build error: {error}");
                            }
                            if (!string.IsNullOrEmpty(output))
                            {
                                McpLogger.LogError($"Build output: {output}");
                            }
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                McpLogger.LogError($"Exception while building Unity MCP server: {ex.Message}");
                McpLogger.LogError($"Make sure .NET SDK is installed and available in PATH.");
            }
        }
        
        /// <summary>
        /// Register all available tools
        /// </summary>
        private void RegisterTools()
        {
            // Register MenuItemTool
            MenuItemTool menuItemTool = new MenuItemTool();
            _tools.Add(menuItemTool.Name, menuItemTool);
            
            // Register SelectGameObjectTool
            SelectGameObjectTool selectGameObjectTool = new SelectGameObjectTool();
            _tools.Add(selectGameObjectTool.Name, selectGameObjectTool);

            // Register UpdateGameObjectTool
            UpdateGameObjectTool updateGameObjectTool = new UpdateGameObjectTool();
            _tools.Add(updateGameObjectTool.Name, updateGameObjectTool);
            
            // Register PackageManagerTool
            AddPackageTool addPackageTool = new AddPackageTool();
            _tools.Add(addPackageTool.Name, addPackageTool);
            
            // Register RunTestsTool
            RunTestsTool runTestsTool = new RunTestsTool(_testRunnerService);
            _tools.Add(runTestsTool.Name, runTestsTool);
            
            // Register SendConsoleLogTool
            SendConsoleLogTool sendConsoleLogTool = new SendConsoleLogTool();
            _tools.Add(sendConsoleLogTool.Name, sendConsoleLogTool);
            
            // Register UpdateComponentTool
            UpdateComponentTool updateComponentTool = new UpdateComponentTool();
            _tools.Add(updateComponentTool.Name, updateComponentTool);
            
            // Register AddAssetToSceneTool
            AddAssetToSceneTool addAssetToSceneTool = new AddAssetToSceneTool();
            _tools.Add(addAssetToSceneTool.Name, addAssetToSceneTool);
            
            // Register CreatePrefabTool
            CreatePrefabTool createPrefabTool = new CreatePrefabTool();
            _tools.Add(createPrefabTool.Name, createPrefabTool);
            
            // Register CaptureScreenshotTool
            CaptureScreenshotTool captureScreenshotTool = new CaptureScreenshotTool();
            _tools.Add(captureScreenshotTool.Name, captureScreenshotTool);
            
            // Register TestHotReloadTool - Added for testing hot-reload functionality
            TestHotReloadTool testHotReloadTool = new TestHotReloadTool();
            _tools.Add(testHotReloadTool.Name, testHotReloadTool);

            // Register SimpleUITool - Basic UI control (replaces complex UIManipulationTool)
            SimpleUITool simpleUITool = new SimpleUITool();
            _tools.Add(simpleUITool.Name, simpleUITool);

            // Register CompilationStatusTool
            CompilationStatusTool compilationStatusTool = new CompilationStatusTool();
            _tools.Add(compilationStatusTool.Name, compilationStatusTool);
        }
        
        /// <summary>
        /// Register all available resources
        /// </summary>
        private void RegisterResources()
        {
            // Register GetMenuItemsResource (by URI for proper lookup)
            GetMenuItemsResource getMenuItemsResource = new GetMenuItemsResource();
            _resources.Add(getMenuItemsResource.Uri, getMenuItemsResource);
            
            // Register GetConsoleLogsResource
            GetConsoleLogsResource getConsoleLogsResource = new GetConsoleLogsResource(_consoleLogsService);
            _resources.Add(getConsoleLogsResource.Uri, getConsoleLogsResource);
            
            // Register GetScenesHierarchyResource
            GetScenesHierarchyResource getScenesHierarchyResource = new GetScenesHierarchyResource();
            _resources.Add(getScenesHierarchyResource.Uri, getScenesHierarchyResource);
            
            // Register GetPackagesResource
            GetPackagesResource getPackagesResource = new GetPackagesResource();
            _resources.Add(getPackagesResource.Uri, getPackagesResource);
            
            // Register GetAssetsResource
            GetAssetsResource getAssetsResource = new GetAssetsResource();
            _resources.Add(getAssetsResource.Uri, getAssetsResource);
            
            // Register GetTestsResource
            GetTestsResource getTestsResource = new GetTestsResource(_testRunnerService);
            _resources.Add(getTestsResource.Uri, getTestsResource);
            
            // Register GetGameObjectResource
            GetGameObjectResource getGameObjectResource = new GetGameObjectResource();
            _resources.Add(getGameObjectResource.Uri, getGameObjectResource);
        }
        
        /// <summary>
        /// Initialize services used by the server
        /// </summary>
        private void InitializeServices()
        {
            // Initialize the test runner service
            _testRunnerService = new TestRunnerService();
            
            // Initialize the console logs service
            _consoleLogsService = new ConsoleLogsService();
        }

        /// <summary>
        /// Handles the Unity Editor quitting event. Ensures the server is properly stopped and disposed.
        /// </summary>
        private static void OnEditorQuitting()
        {
            McpLogger.LogInfo("Editor is quitting. Ensuring server is stopped.");
            Instance.Dispose();
            // Release the port allocation for this Unity instance
            PortManager.ReleasePort();
            // Unregister from service discovery
            ServiceDiscovery.UnregisterService();
        }

        /// <summary>
        /// Handles the Unity Editor's 'before assembly reload' event.
        /// Stops the WebSocket server to prevent port conflicts and ensure a clean state before scripts are recompiled.
        /// </summary>
        private static void OnBeforeAssemblyReload()
        {
            if (Instance.IsListening)
            {
                Instance.StopServer();
            }
        }

        /// <summary>
        /// Handles the Unity Editor's 'after assembly reload' event.
        /// If auto-start is enabled, attempts to restart the WebSocket server if it's not already listening.
        /// This ensures the server is operational after script recompilation.
        /// </summary>
        private static void OnAfterAssemblyReload()
        {
            if (McpUnitySettings.Instance.AutoStartServer && !Instance.IsListening)
            {
                Instance.StartServer();
            }
        }

        /// <summary>
        /// Handles changes in Unity Editor's play mode state.
        /// Stops the server when exiting Edit Mode if configured, and restarts it when entering Play Mode or returning to Edit Mode if auto-start is enabled.
        /// </summary>
        /// <param name="state">The current play mode state change.</param>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    // About to enter Play Mode
                    if (Instance.IsListening)
                    {
                        Instance.StopServer();
                    }
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                case PlayModeStateChange.ExitingPlayMode:
                    // Server is disabled during play mode as domain reload will be triggered again when stopped.
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    // Returned to Edit Mode
                    if (!Instance.IsListening && McpUnitySettings.Instance.AutoStartServer)
                    {
                        Instance.StartServer();
                    }
                    break;
            }
        }

        /// <summary>
        /// Refreshes the list of attributed tools by scanning for McpToolAttribute annotations
        /// </summary>
        public void RefreshAttributedTools()
        {
            McpLogger.LogInfo("Refreshing attributed tools...");
            
            // Clear existing attributed tools (preserve manually registered ones)
            var attributedToolKeys = new List<string>();
            foreach (var kvp in _tools)
            {
                if (kvp.Value.GetType().GetCustomAttributes(typeof(McpToolAttribute), false).Length > 0)
                {
                    attributedToolKeys.Add(kvp.Key);
                }
            }
            
            foreach (var key in attributedToolKeys)
            {
                _tools.Remove(key);
            }
            
            // Re-register attributed tools
            RegisterTools();
            
            McpLogger.LogInfo($"Attributed tools refreshed. Total tools: {_tools.Count}");
        }

        /// <summary>
        /// Pushes a notification to connected MCP clients
        /// </summary>
        /// <param name="notification">The notification to send</param>
        /// <returns>True if notification was sent successfully</returns>
        public bool PushNotification(object notification)
        {
            if (_webSocketServer == null || !IsListening)
            {
                // Don't log warnings for expected conditions - just return false
                return false;
            }
            
            // Get the WebSocket service behavior
            var servicePath = "/McpUnity";
            if (_webSocketServer.WebSocketServices.TryGetServiceHost(servicePath, out var host))
            {
                if (host.Sessions.Count == 0)
                {
                    // No connected clients - this is normal, not an error
                    return false;
                }
                
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(notification);
                host.Sessions.Broadcast(json);
                
                if (DevelopmentMode.Settings.VerboseLogging)
                {
                    McpLogger.LogInfo($"Notification sent to {host.Sessions.Count} connected clients");
                }
                return true;
            }
            else
            {
                // Service not found - this shouldn't happen but don't spam logs
                return false;
            }
        }
    }
}
