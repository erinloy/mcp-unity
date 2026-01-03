using System;
using System.Collections.Generic;
using System.Linq;
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
        private bool _isStartingServer;  // Prevent concurrent StartServer calls

        /// <summary>
        /// Static constructor that gets called when Unity loads due to InitializeOnLoad attribute
        /// </summary>
        static McpUnityServer()
        {
            EditorApplication.delayCall += () => {
                // Ensure Instance is created and hooks are set up after initial domain load
                var currentInstance = Instance;

                // Ensure server is started after domain reload (assembly reload events fire before this)
                if (McpUnitySettings.Instance.AutoStartServer && !currentInstance.IsListening)
                {
                    McpLogger.LogInfo("Starting server after domain reload...");
                    currentInstance.StartServer();
                }
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
        /// Time of last successful client connection (for health monitoring)
        /// </summary>
        private DateTime _lastConnectionTime = DateTime.MinValue;

        /// <summary>
        /// Track when server was started for health check grace period
        /// </summary>
        private DateTime _serverStartTime = DateTime.MinValue;

        /// <summary>
        /// Last time we performed a health check
        /// </summary>
        private double _lastHealthCheckTime;

        /// <summary>
        /// How often to check server health (seconds)
        /// </summary>
        private const float HealthCheckIntervalSeconds = 5f;

        /// <summary>
        /// Grace period after server start before health checks begin (seconds)
        /// </summary>
        private const float HealthCheckGracePeriodSeconds = 3f;

        /// <summary>
        /// Count of consecutive health check failures (to avoid false positives)
        /// </summary>
        private int _consecutiveHealthFailures;

        /// <summary>
        /// Number of consecutive failures before auto-recovery triggers
        /// </summary>
        private const int HealthFailureThreshold = 2;

        /// <summary>
        /// Whether auto-healing is enabled
        /// </summary>
        private bool _autoHealingEnabled = true;

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

            // Subscribe to update for periodic health checks
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;

            InstallServer();
            InitializeServices();
            RegisterResources();
            RegisterTools();

            // Initial start if auto-start is enabled
            if (McpUnitySettings.Instance.AutoStartServer)
            {
                try
                {
                    StartServer();
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"Failed to start server in constructor: {ex.Message}");
                    // Schedule retry
                    EditorApplication.delayCall += () =>
                    {
                        if (!IsListening && McpUnitySettings.Instance.AutoStartServer)
                        {
                            McpLogger.LogInfo("Retrying server start...");
                            StartServer();
                        }
                    };
                }
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
            EditorApplication.update -= OnEditorUpdate;

            GC.SuppressFinalize(this);
        }
        
        /// <summary>
        /// Start the WebSocket Server to communicate with Node.js
        /// Includes retry logic for port conflicts during domain reload
        /// </summary>
        public void StartServer()
        {
            if (IsListening)
            {
                McpLogger.LogInfo($"Server start requested, but already listening on port {McpUnitySettings.Instance.Port}.");
                return;
            }

            // Prevent concurrent StartServer calls (can happen during domain reload from multiple sources)
            if (_isStartingServer)
            {
                McpLogger.LogInfo("Server start already in progress, skipping duplicate request.");
                return;
            }
            _isStartingServer = true;

            // Ensure any previous server is fully stopped before starting new one
            if (_webSocketServer != null)
            {
                McpLogger.LogWarning("Previous WebSocket server instance exists - cleaning up before starting new one.");
                try
                {
                    _webSocketServer.Stop();
                }
                catch (Exception ex)
                {
                    McpLogger.LogWarning($"Error stopping previous server: {ex.Message}");
                }
                _webSocketServer = null;
                Clients.Clear();
            }

            const int maxRetries = 3;
            const int retryDelayMs = 200;

            try
            {
                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    try
                    {
                        // Always use 127.0.0.1 for now - WebSocketSharp has issues with 0.0.0.0 binding
                        var host = "127.0.0.1";
                        _webSocketServer = new WebSocketServer($"ws://{host}:{McpUnitySettings.Instance.Port}");

                        // Enable address reuse to handle rapid restart scenarios (domain reload)
                        _webSocketServer.ReuseAddress = true;

                        // Enable keepalive to detect stale connections
                        _webSocketServer.KeepClean = true;  // Auto-clean dead connections
                        _webSocketServer.WaitTime = TimeSpan.FromSeconds(2);  // Short timeout to avoid blocking Unity

                        _webSocketServer.Log.Level = WebSocketSharp.LogLevel.Debug;
                        _webSocketServer.Log.Output = (data, path) => {
                            // Filter out benign connection errors (stale connections, malformed requests, etc.)
                            var message = data.Message;
                            if (message != null && (
                                message.Contains("EndOfStreamException") ||
                                message.Contains("The header cannot be read from the data source") ||
                                message.Contains("An exception has occurred while reading an HTTP request/response")
                            ))
                            {
                                // These are benign errors from stale/malformed connections - ignore them
                                return;
                            }
                            McpLogger.LogInfo($"[WebSocketSharp] {message}");
                        };
                        _webSocketServer.AddWebSocketService("/McpUnity", () => new McpUnitySocketHandler(this));
                        _webSocketServer.Start();

                        // Verify the server is actually listening
                        if (_webSocketServer.IsListening)
                        {
                            _serverStartTime = DateTime.UtcNow;
                            McpLogger.LogInfo($"✅ WebSocket server verified listening on {host}:{McpUnitySettings.Instance.Port}");
                            McpLogger.LogInfo($"   Endpoint: ws://{host}:{McpUnitySettings.Instance.Port}/McpUnity");

                            // Register with service discovery
                            ServiceDiscovery.RegisterService(McpUnitySettings.Instance.Port);
                            return; // Success
                        }
                        else
                        {
                            McpLogger.LogError($"WebSocket server failed to start listening on {host}:{McpUnitySettings.Instance.Port}");
                            throw new System.InvalidOperationException("WebSocket server is not listening after Start() call");
                        }
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    {
                        _webSocketServer = null;

                        if (attempt < maxRetries)
                        {
                            McpLogger.LogWarning($"Port {McpUnitySettings.Instance.Port} still in use (attempt {attempt}/{maxRetries}). Retrying in {retryDelayMs}ms...");
                            System.Threading.Thread.Sleep(retryDelayMs);
                        }
                        else
                        {
                            McpLogger.LogError($"Failed to start WebSocket server after {maxRetries} attempts: Port {McpUnitySettings.Instance.Port} is still in use. {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _webSocketServer = null;
                        McpLogger.LogError($"Failed to start WebSocket server: {ex.Message}\n{ex.StackTrace}");
                        return; // Don't retry on other exceptions
                    }
                }
            }
            finally
            {
                _isStartingServer = false;
            }
        }
        
        /// <summary>
        /// Stop the WebSocket server and properly release the port
        /// </summary>
        public void StopServer()
        {
            if (!IsListening)
            {
                return;
            }

            try
            {
                // Explicitly close all active sessions before stopping server
                if (_webSocketServer?.WebSocketServices != null)
                {
                    var servicePath = "/McpUnity";
                    if (_webSocketServer.WebSocketServices.TryGetServiceHost(servicePath, out var host))
                    {
                        var sessionsCount = host.Sessions.Count;
                        if (sessionsCount > 0)
                        {
                            McpLogger.LogInfo($"Closing {sessionsCount} active WebSocket connection(s) before server stop");
                            // Close each session individually
                            foreach (var sessionId in host.Sessions.IDs.ToArray())
                            {
                                host.Sessions.CloseSession(sessionId, WebSocketSharp.CloseStatusCode.Normal, "Server shutting down");
                            }
                        }
                    }
                }

                // Small delay to let close frames be sent
                System.Threading.Thread.Sleep(100);

                // Stop the server
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
        /// Stop the WebSocket server without blocking the calling thread.
        /// Used during play mode transitions to avoid hanging Unity.
        /// </summary>
        public void StopServerNonBlocking()
        {
            if (!IsListening)
            {
                return;
            }

            var serverToStop = _webSocketServer;
            _webSocketServer = null;
            Clients.Clear();

            // Close sessions on main thread before background stop
            try
            {
                if (serverToStop?.WebSocketServices != null)
                {
                    var servicePath = "/McpUnity";
                    if (serverToStop.WebSocketServices.TryGetServiceHost(servicePath, out var host))
                    {
                        var sessionsCount = host.Sessions.Count;
                        if (sessionsCount > 0)
                        {
                            McpLogger.LogInfo($"Closing {sessionsCount} connection(s) before non-blocking stop");
                            // Close each session individually
                            foreach (var sessionId in host.Sessions.IDs.ToArray())
                            {
                                host.Sessions.CloseSession(sessionId, WebSocketSharp.CloseStatusCode.Normal, "Server shutting down");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogWarning($"Error closing sessions: {ex.Message}");
            }

            // Stop on a background thread to avoid blocking Unity
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    System.Threading.Thread.Sleep(100); // Let close frames complete
                    serverToStop?.Stop();
                    McpLogger.LogInfo("WebSocket server stopped (non-blocking)");
                }
                catch (Exception ex)
                {
                    // Log but don't throw - we're on a background thread
                    McpLogger.LogWarning($"Non-fatal error during non-blocking server stop: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Force restart the server, bypassing IsListening checks.
        /// Use when the server is in a zombie state (port bound but not accepting connections).
        /// </summary>
        public void ForceRestartServer()
        {
            McpLogger.LogWarning("[ForceRestart] Beginning aggressive server restart...");

            // Reset all state flags first
            _isStartingServer = false;
            _consecutiveHealthFailures = 0;

            // Forcefully tear down the existing server regardless of state
            var oldServer = _webSocketServer;
            _webSocketServer = null;
            Clients.Clear();

            if (oldServer != null)
            {
                McpLogger.LogInfo("[ForceRestart] Stopping old server instance...");
                try
                {
                    // Try to close sessions first
                    if (oldServer.WebSocketServices != null)
                    {
                        try
                        {
                            if (oldServer.WebSocketServices.TryGetServiceHost("/McpUnity", out var host))
                            {
                                foreach (var sessionId in host.Sessions.IDs.ToArray())
                                {
                                    try { host.Sessions.CloseSession(sessionId, WebSocketSharp.CloseStatusCode.Normal, "Force restart"); }
                                    catch { /* Ignore individual session close errors */ }
                                }
                            }
                        }
                        catch { /* Ignore session enumeration errors */ }
                    }

                    oldServer.Stop();
                }
                catch (Exception ex)
                {
                    McpLogger.LogWarning($"[ForceRestart] Error stopping old server (continuing anyway): {ex.Message}");
                }

                // Explicitly null to help GC
                oldServer = null;
            }

            // Unregister from service discovery
            try { ServiceDiscovery.UnregisterService(); }
            catch { /* Ignore */ }

            // Wait for port to be released - try multiple times with increasing delay
            McpLogger.LogInfo("[ForceRestart] Waiting for port to be released...");
            int maxWaitAttempts = 5;
            int waitDelayMs = 200;

            for (int i = 0; i < maxWaitAttempts; i++)
            {
                System.Threading.Thread.Sleep(waitDelayMs);

                // Test if port is free
                try
                {
                    var testListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, McpUnitySettings.Instance.Port);
                    testListener.Start();
                    testListener.Stop();
                    McpLogger.LogInfo($"[ForceRestart] Port {McpUnitySettings.Instance.Port} is now available");
                    break;
                }
                catch (SocketException)
                {
                    if (i < maxWaitAttempts - 1)
                    {
                        McpLogger.LogInfo($"[ForceRestart] Port still in use, waiting... ({i + 1}/{maxWaitAttempts})");
                        waitDelayMs += 100; // Increase delay each attempt
                    }
                }
            }

            // Small additional delay for socket cleanup
            System.Threading.Thread.Sleep(100);

            // Now start fresh
            McpLogger.LogInfo("[ForceRestart] Starting fresh server instance...");
            StartServer();

            if (IsListening)
            {
                McpLogger.LogInfo($"[ForceRestart] ✅ Server successfully restarted on port {McpUnitySettings.Instance.Port}");
            }
            else
            {
                McpLogger.LogError("[ForceRestart] ❌ Server failed to restart");
            }
        }

        /// <summary>
        /// Called when a client successfully connects. Updates health tracking.
        /// </summary>
        public void OnClientConnected(string clientId)
        {
            _lastConnectionTime = DateTime.UtcNow;
            // Note: Clients dictionary is managed by socket handler which has the client name
        }

        /// <summary>
        /// Performs a health check on the server. Returns true if healthy, false if needs restart.
        /// </summary>
        public bool PerformHealthCheck()
        {
            if (!IsListening)
            {
                return false;
            }

            // Try a quick TCP connection to verify the server is actually accepting
            try
            {
                using (var testClient = new TcpClient())
                {
                    var connectTask = testClient.ConnectAsync("127.0.0.1", McpUnitySettings.Instance.Port);
                    if (!connectTask.Wait(TimeSpan.FromMilliseconds(500)))
                    {
                        McpLogger.LogWarning("Health check: TCP connect timed out - server may be in zombie state");
                        return false;
                    }
                    return testClient.Connected;
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogWarning($"Health check failed: {ex.Message}");
                return false;
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

            // Register SetObjectReferenceTool - Wire up object references on serialized fields
            SetObjectReferenceTool setObjectReferenceTool = new SetObjectReferenceTool();
            _tools.Add(setObjectReferenceTool.Name, setObjectReferenceTool);

            // Register CreateUIElementTool - Generic UI element factory
            CreateUIElementTool createUIElementTool = new CreateUIElementTool();
            _tools.Add(createUIElementTool.Name, createUIElementTool);

            // Register InspectGameObjectTool - Inspect GameObjects and their components
            InspectGameObjectTool inspectGameObjectTool = new InspectGameObjectTool();
            _tools.Add(inspectGameObjectTool.Name, inspectGameObjectTool);

            // Register additional tools from extension point
            RegisterAdditionalTools.RegisterTo(_tools);
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
        /// Must be non-blocking to avoid hanging Unity's quit sequence.
        /// </summary>
        private static void OnEditorQuitting()
        {
            // Non-blocking cleanup - Unity is quitting anyway, the port will be released by OS
            try
            {
                if (Instance.IsListening)
                {
                    // Stop without waiting for port release - Unity is shutting down
                    Instance._webSocketServer?.Stop();
                    Instance._webSocketServer = null;
                }

                // Release the port allocation for this Unity instance
                PortManager.ReleasePort();
                // Unregister from service discovery
                ServiceDiscovery.UnregisterService();
            }
            catch (Exception ex)
            {
                // Catch all errors - don't block Unity quit
                McpLogger.LogWarning($"Non-fatal error during quit cleanup: {ex.Message}");
            }
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
        /// IMPORTANT: This handler must be non-blocking to avoid hanging Unity's play mode transitions.
        /// </summary>
        /// <param name="state">The current play mode state change.</param>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    // About to enter Play Mode - stop server non-blocking
                    if (Instance.IsListening)
                    {
                        Instance.StopServerNonBlocking();
                    }
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    // Restart server after entering play mode (domain reload completed)
                    EditorApplication.delayCall += () =>
                    {
                        if (!Instance.IsListening && McpUnitySettings.Instance.AutoStartServer)
                        {
                            Instance.StartServer();
                        }
                    };
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    // Keep server running while exiting
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    // Returned to Edit Mode - defer server start to avoid blocking
                    EditorApplication.delayCall += () =>
                    {
                        if (!Instance.IsListening && McpUnitySettings.Instance.AutoStartServer)
                        {
                            Instance.StartServer();
                        }
                    };
                    break;
            }
        }

        /// <summary>
        /// Called every editor frame. Performs periodic health checks and auto-recovery.
        /// </summary>
        private void OnEditorUpdate()
        {
            if (!_autoHealingEnabled || !McpUnitySettings.Instance.AutoStartServer)
                return;

            // Check if enough time has passed since last health check
            double currentTime = EditorApplication.timeSinceStartup;
            if (currentTime - _lastHealthCheckTime < HealthCheckIntervalSeconds)
                return;

            _lastHealthCheckTime = currentTime;

            // Don't check during grace period after server start
            if (_serverStartTime != DateTime.MinValue)
            {
                var timeSinceStart = (DateTime.UtcNow - _serverStartTime).TotalSeconds;
                if (timeSinceStart < HealthCheckGracePeriodSeconds)
                    return;
            }

            // If server should be running but isn't, start it
            if (!IsListening)
            {
                _consecutiveHealthFailures = 0;
                McpLogger.LogInfo("[AutoHeal] Server not listening, starting...");
                StartServer();
                return;
            }

            // Server reports listening - verify it's actually accepting connections
            bool isHealthy = PerformHealthCheckQuiet();

            if (isHealthy)
            {
                // Reset failure counter on success
                if (_consecutiveHealthFailures > 0)
                {
                    McpLogger.LogInfo("[AutoHeal] Server health restored");
                }
                _consecutiveHealthFailures = 0;
            }
            else
            {
                _consecutiveHealthFailures++;
                McpLogger.LogWarning($"[AutoHeal] Health check failed ({_consecutiveHealthFailures}/{HealthFailureThreshold})");

                if (_consecutiveHealthFailures >= HealthFailureThreshold)
                {
                    McpLogger.LogWarning("[AutoHeal] Zombie state detected - triggering auto-recovery...");
                    _consecutiveHealthFailures = 0;

                    // Disable auto-healing temporarily to prevent rapid retries
                    _autoHealingEnabled = false;

                    // Schedule the restart on next frame to avoid issues
                    EditorApplication.delayCall += () =>
                    {
                        ForceRestartServer();
                        _autoHealingEnabled = true;
                    };
                }
            }
        }

        /// <summary>
        /// Quiet version of health check that doesn't log on failure (used by auto-healing)
        /// </summary>
        private bool PerformHealthCheckQuiet()
        {
            if (!IsListening)
                return false;

            try
            {
                using (var testClient = new TcpClient())
                {
                    var connectTask = testClient.ConnectAsync("127.0.0.1", McpUnitySettings.Instance.Port);
                    if (!connectTask.Wait(TimeSpan.FromMilliseconds(500)))
                    {
                        return false; // Timeout - zombie state
                    }
                    return testClient.Connected;
                }
            }
            catch
            {
                return false;
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
