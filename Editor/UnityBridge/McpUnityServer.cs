using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using McpUnity.Tools;
using McpUnity.Resources;
using McpUnity.Services;
using McpUnity.Utils;
using WebSocketSharp.Server;
using System.IO;
using System.Diagnostics;
using System.Net.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace McpUnity.Unity
{
    /// <summary>
    /// MCP Unity Server to communicate Node.js MCP server.
    /// Uses WebSockets to communicate with Node.js.
    /// </summary>
    // REMOVED [InitializeOnLoad] to prevent domain reload hangs with HotReload
    public class McpUnityServer : IDisposable
    {
        private static McpUnityServer _instance;
        private static bool _isShuttingDown = false;
        
        private readonly Dictionary<string, McpToolBase> _tools = new Dictionary<string, McpToolBase>();
        private readonly Dictionary<string, McpResourceBase> _resources = new Dictionary<string, McpResourceBase>();
        
        private WebSocketServer _webSocketServer;
        private CancellationTokenSource _cts;
        private TestRunnerService _testRunnerService;
        private ConsoleLogsService _consoleLogsService;

        // Static constructor removed to prevent domain reload issues
        // Use McpUnityMenu.InitializeSystem() to manually start
        
        /// <summary>
        /// Singleton instance accessor
        /// </summary>
        public static McpUnityServer Instance
        {
            get
            {
                if (_instance == null && !_isShuttingDown && !EditorApplication.isCompiling)
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
            // Don't hook into Unity events automatically to prevent domain reload issues
            // These will be managed manually through the menu system
            
            InstallServer();
            InitializeServices();
            RegisterResources();
            RegisterTools();
            
            // Don't auto-start to prevent domain reload issues
            // Use MCP Unity/Initialize System menu item instead
        }

        /// <summary>
        /// Disposes the McpUnityServer instance, stopping the WebSocket server.
        /// This method ensures proper cleanup of resources.
        /// </summary>
        public void Dispose()
        {
            StopServer();
            _instance = null;
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
                var host = McpUnitySettings.Instance.AllowRemoteConnections ? "0.0.0.0" : "localhost";
                _webSocketServer = new WebSocketServer($"ws://{host}:{McpUnitySettings.Instance.Port}");
                _webSocketServer.ReuseAddress = true;
                _webSocketServer.AddWebSocketService("/McpUnity", () => new McpUnitySocketHandler(this));
                _webSocketServer.Start();
                McpLogger.LogInfo($"WebSocket server started successfully on {host}:{McpUnitySettings.Instance.Port}.");
                
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
        /// Push a notification to all connected MCP clients
        /// Server-initiated event (not a response to a request)
        /// </summary>
        public void PushNotification(JObject notification)
        {
            if (!IsListening || _webSocketServer == null)
            {
                McpLogger.LogWarning("[Notifications] Cannot push - server not running");
                return;
            }
            
            try
            {
                var message = notification.ToString(Formatting.None);
                
                // Send directly to each connected session using SendTo
                var service = _webSocketServer.WebSocketServices["/McpUnity"];
                if (service != null && service.Sessions != null)
                {
                    int sentCount = 0;
                    var sessionIds = service.Sessions.IDs;
                    
                    if (sessionIds != null)
                    {
                        foreach (var sessionId in sessionIds)
                        {
                            try
                            {
                                // Use SendTo with session ID
                                service.Sessions.SendTo(message, sessionId);
                                sentCount++;
                                McpLogger.LogInfo($"[Notifications] Sent {notification["method"]} to session {sessionId}");
                            }
                            catch (Exception sessionEx)
                            {
                                McpLogger.LogWarning($"[Notifications] Failed to send to session {sessionId}: {sessionEx.Message}");
                            }
                        }
                    }
                    
                    McpLogger.LogInfo($"[Notifications] Sent notification to {sentCount} of {service.Sessions.Count} clients");
                }
                else
                {
                    McpLogger.LogWarning("[Notifications] No WebSocket service or sessions available");
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"[Notifications] Push failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Stop the WebSocket server
        /// </summary>
        public void StopServer()
        {
            if (_webSocketServer == null)
            {
                return;
            }

            try
            {
                // Use async stop to avoid blocking
                if (_webSocketServer.IsListening)
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            _webSocketServer.Stop();
                        }
                        catch { }
                    });
                    
                    // Don't wait for it to complete
                }
                
                McpLogger.LogInfo("WebSocket server stop initiated");
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error during WebSocketServer.Stop(): {ex.Message}");
            }
            finally
            {
                // Clean up references immediately
                _webSocketServer = null; 
                Clients.Clear(); 
                
                McpLogger.LogInfo("WebSocket server resources released");
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
        /// Try to get a resource by name
        /// </summary>
        public bool TryGetResource(string name, out McpResourceBase resource)
        {
            return _resources.TryGetValue(name, out resource);
        }

        /// <summary>
        /// Verifies the MCP C# server is installed and ready to use.
        /// The C# implementation is pre-built and doesn't require npm.
        /// </summary>
        public void InstallServer()
        {
            string serverPath = McpUtils.GetServerPath();

            if (string.IsNullOrEmpty(serverPath) || !Directory.Exists(serverPath))
            {
                McpLogger.LogError($"Server path not found or invalid: {serverPath}. Make sure that MCP server is installed.");
                return;
            }

            // Check for C# executable instead of node_modules
            string exePath = Path.Combine(serverPath, "build", "unity-mcp.exe");
            if (!File.Exists(exePath))
            {
                McpLogger.LogWarning($"MCP C# server executable not found at: {exePath}");
                
                // The C# executable should be pre-built in the submodule
                // If missing, it needs to be built with dotnet publish
                string csprojPath = Path.Combine(serverPath, "UnityMcp.csproj");
                if (File.Exists(csprojPath))
                {
                    McpLogger.LogInfo("C# project found. Please build with: dotnet publish -c Release -o build");
                }
            }
            else
            {
                McpLogger.LogInfo($"MCP C# server ready at: {exePath}");
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
        }
        
        /// <summary>
        /// Register all available resources
        /// </summary>
        private void RegisterResources()
        {
            // Register GetMenuItemsResource
            GetMenuItemsResource getMenuItemsResource = new GetMenuItemsResource();
            _resources.Add(getMenuItemsResource.Name, getMenuItemsResource);
            
            // Register GetConsoleLogsResource
            GetConsoleLogsResource getConsoleLogsResource = new GetConsoleLogsResource(_consoleLogsService);
            _resources.Add(getConsoleLogsResource.Name, getConsoleLogsResource);
            
            // Register GetScenesHierarchyResource
            GetScenesHierarchyResource getScenesHierarchyResource = new GetScenesHierarchyResource();
            _resources.Add(getScenesHierarchyResource.Name, getScenesHierarchyResource);
            
            // Register GetPackagesResource
            GetPackagesResource getPackagesResource = new GetPackagesResource();
            _resources.Add(getPackagesResource.Name, getPackagesResource);
            
            // Register GetAssetsResource
            GetAssetsResource getAssetsResource = new GetAssetsResource();
            _resources.Add(getAssetsResource.Name, getAssetsResource);
            
            // Register GetTestsResource
            GetTestsResource getTestsResource = new GetTestsResource(_testRunnerService);
            _resources.Add(getTestsResource.Name, getTestsResource);
            
            // Register GetGameObjectResource
            GetGameObjectResource getGameObjectResource = new GetGameObjectResource();
            _resources.Add(getGameObjectResource.Name, getGameObjectResource);
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

        // Event handlers removed to prevent domain reload issues
        // Server lifecycle is now managed manually through the menu system
    }
}
