using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;

namespace McpUnity.DirectMcp.Services
{
    /// <summary>
    /// Service that bridges Unity Editor with MCP protocol
    /// </summary>
    public class UnityBridgeService : IHostedService
    {
        private readonly IUnityProjectLocator _projectLocator;
        private readonly IWebSocketConnectionManager _connectionManager;
        private readonly IUnityRpcClient _rpcClient;
        private readonly ILogger<UnityBridgeService> _logger;
        private readonly IUnityToolService _toolService;
        private readonly McpServer _mcpServer;

        private string? _unityProjectPath;
        private string? _webSocketUri;
        private Timer? _reconnectTimer;
        private readonly TimeSpan _reconnectInterval = TimeSpan.FromSeconds(2); // Faster reconnection
        private bool _isReconnecting;

        public UnityBridgeService(
            IUnityProjectLocator projectLocator,
            IWebSocketConnectionManager connectionManager,
            IUnityRpcClient rpcClient,
            IUnityToolService toolService,
            McpServer mcpServer,
            ILogger<UnityBridgeService> logger)
        {
            _projectLocator = projectLocator;
            _connectionManager = connectionManager;
            _rpcClient = rpcClient;
            _toolService = toolService;
            _mcpServer = mcpServer;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _unityProjectPath = _projectLocator.FindUnityProject();
            if (_unityProjectPath == null)
            {
                _logger.LogError("Could not find Unity project");
                return;
            }

            _logger.LogInformation("Unity project: {ProjectPath}", _unityProjectPath);

            // Read port from this Unity instance's settings file - FAIL if missing
            var settingsPath = Path.Combine(_unityProjectPath, "ProjectSettings", "McpUnitySettings.json");

            if (!File.Exists(settingsPath))
            {
                _logger.LogError("Settings file not found at: {SettingsPath}. Cannot determine WebSocket port for this Unity instance.", settingsPath);
                return;
            }

            int port;
            try
            {
                var settingsJson = await File.ReadAllTextAsync(settingsPath, cancellationToken);
                var settings = JObject.Parse(settingsJson);

                if (settings["Port"] == null)
                {
                    _logger.LogError("Port not specified in settings file: {SettingsPath}", settingsPath);
                    return;
                }

                port = settings["Port"]!.Value<int>();
                _logger.LogInformation("Loaded port {Port} from settings file", port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read settings file: {SettingsPath}", settingsPath);
                return;
            }

            _webSocketUri = $"ws://127.0.0.1:{port}/McpUnity";
            _logger.LogInformation("Connecting to Unity at: {Uri}", _webSocketUri);

            // Subscribe to connection events for immediate reconnection
            _connectionManager.ConnectionLost += OnConnectionLost;
            _connectionManager.ConnectionRestored += OnConnectionRestored;

            // Attempt initial connection in background to avoid blocking MCP initialization
            _ = Task.Run(EnsureConnectedAsync);

            // Start reconnection timer for maintaining connection
            _reconnectTimer = new Timer(
                async _ => await MonitorConnectionAsync(),
                null,
                _reconnectInterval,
                _reconnectInterval
            );
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _connectionManager.ConnectionLost -= OnConnectionLost;
            _connectionManager.ConnectionRestored -= OnConnectionRestored;
            _reconnectTimer?.Dispose();
            return _connectionManager.DisconnectAsync();
        }

        private void OnConnectionLost(object? sender, EventArgs e)
        {
            _logger.LogWarning("Unity connection lost - triggering immediate reconnection");
            if (!_isReconnecting)
            {
                _isReconnecting = true;
                _ = Task.Run(async () =>
                {
                    // Small delay to let Unity finish its recompile
                    await Task.Delay(500);
                    await EnsureConnectedAsync();
                    _isReconnecting = false;
                });
            }
        }

        private async void OnConnectionRestored(object? sender, EventArgs e)
        {
            _logger.LogInformation("Unity connection restored - sending tool list update notification");
            try
            {
                // Give MCP session time to stabilize
                await Task.Delay(200);
                await _mcpServer.SendNotificationAsync(NotificationMethods.ToolListChangedNotification);
                await _mcpServer.SendNotificationAsync(NotificationMethods.ResourceListChangedNotification);
                _logger.LogInformation("Tool/resource list change notifications sent");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send list_changed notifications after reconnection");
            }
        }

        private async Task EnsureConnectedAsync()
        {
            if (_connectionManager.IsConnected)
            {
                return;
            }

            int retryCount = 0;
            const int maxRetries = 30;  // More retries with faster initial attempts

            while (retryCount < maxRetries)
            {
                try
                {
                    _logger.LogDebug("Connecting to Unity at {Uri}...", _webSocketUri);
                    await _connectionManager.ConnectAsync(_webSocketUri);
                    _logger.LogInformation("Connected to Unity");

                    // Give MCP session time to initialize before sending notifications
                    await Task.Delay(300);

                    // Notify MCP client that tools are now available
                    try
                    {
                        _logger.LogInformation("Sending tools/list_changed notification to MCP client");
                        await _mcpServer.SendNotificationAsync(NotificationMethods.ToolListChangedNotification);
                        await _mcpServer.SendNotificationAsync(NotificationMethods.ResourceListChangedNotification);
                        _logger.LogInformation("Notifications sent successfully");
                    }
                    catch (Exception notifyEx)
                    {
                        _logger.LogWarning(notifyEx, "Failed to send list_changed notifications - MCP session may not be ready yet");
                    }
                    return;
                }
                catch (TimeoutException tex)
                {
                    _logger.LogWarning("Connection attempt {Attempt} timed out: {Message}",
                        retryCount + 1, tex.Message);
                    retryCount++;

                    if (retryCount < maxRetries)
                    {
                        // Fast retries for first 10 attempts (domain reload typically ~5-10 seconds)
                        var delay = retryCount <= 10
                            ? TimeSpan.FromMilliseconds(500)
                            : TimeSpan.FromSeconds(Math.Min(retryCount - 10, 10));
                        await Task.Delay(delay);
                    }
                }
                catch (WebSocketException wsEx)
                {
                    _logger.LogDebug("WebSocket connection failed at attempt {Attempt}: {Message}",
                        retryCount + 1, wsEx.Message);
                    retryCount++;

                    if (retryCount < maxRetries)
                    {
                        // Fast retries for first 10 attempts, then back off
                        var delay = retryCount <= 10
                            ? TimeSpan.FromMilliseconds(500)
                            : TimeSpan.FromSeconds(Math.Min(retryCount - 10, 10));
                        await Task.Delay(delay);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error during connection attempt {Attempt}", retryCount + 1);
                    retryCount++;

                    if (retryCount < maxRetries)
                    {
                        var delay = retryCount <= 10
                            ? TimeSpan.FromMilliseconds(500)
                            : TimeSpan.FromSeconds(Math.Min(retryCount - 10, 10));
                        await Task.Delay(delay);
                    }
                }
            }

            _logger.LogError("Failed to connect to Unity after {MaxRetries} attempts", maxRetries);
        }

        private async Task MonitorConnectionAsync()
        {
            if (!_connectionManager.IsConnected)
            {
                _logger.LogDebug("Connection lost, attempting to reconnect...");
                await EnsureConnectedAsync();
            }
        }
    }
}