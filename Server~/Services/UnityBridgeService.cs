using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
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
        
        private string? _unityProjectPath;
        private string _webSocketUri = "ws://localhost:8090/McpUnity";
        private Timer? _reconnectTimer;
        private readonly TimeSpan _reconnectInterval = TimeSpan.FromSeconds(5);

        public UnityBridgeService(
            IUnityProjectLocator projectLocator,
            IWebSocketConnectionManager connectionManager,
            IUnityRpcClient rpcClient,
            IUnityToolService toolService,
            ILogger<UnityBridgeService> logger)
        {
            _projectLocator = projectLocator;
            _connectionManager = connectionManager;
            _rpcClient = rpcClient;
            _toolService = toolService;
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
            
            // Load settings
            var settingsPath = Path.Combine(_unityProjectPath, "ProjectSettings", "McpUnitySettings.json");
            int port = 8090;
            
            if (File.Exists(settingsPath))
            {
                var settingsJson = await File.ReadAllTextAsync(settingsPath, cancellationToken);
                var settings = JObject.Parse(settingsJson);
                port = settings["Port"]?.Value<int>() ?? 8090;
                _logger.LogInformation("Loaded settings from {SettingsPath}", settingsPath);
            }
            
            _webSocketUri = $"ws://localhost:{port}/McpUnity";
            
            // Start initial connection
            _ = Task.Run(async () => await EnsureConnectedAsync(), cancellationToken);
            
            // Start reconnection timer
            _reconnectTimer = new Timer(
                async _ => await MonitorConnectionAsync(),
                null,
                _reconnectInterval,
                _reconnectInterval
            );
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _reconnectTimer?.Dispose();
            return _connectionManager.DisconnectAsync();
        }

        private async Task EnsureConnectedAsync()
        {
            if (_connectionManager.IsConnected)
            {
                return;
            }

            int retryCount = 0;
            const int maxRetries = 10;
            
            while (retryCount < maxRetries)
            {
                try
                {
                    _logger.LogInformation("Connecting to Unity at {Uri}...", _webSocketUri);
                    await _connectionManager.ConnectAsync(_webSocketUri);
                    _logger.LogInformation("Connected to Unity");
                    return;
                }
                catch (TimeoutException tex)
                {
                    _logger.LogWarning("Connection attempt {Attempt} timed out: {Message}", 
                        retryCount + 1, tex.Message);
                    retryCount++;
                    
                    if (retryCount < maxRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Min(retryCount * 2, 30));
                        _logger.LogInformation("Waiting {Seconds} seconds before retry...", delay.TotalSeconds);
                        await Task.Delay(delay);
                    }
                }
                catch (WebSocketException wsEx)
                {
                    _logger.LogError("WebSocket connection failed at attempt {Attempt}: {Message}", 
                        retryCount + 1, wsEx.Message);
                    
                    // If Unity isn't running or the server isn't started, wait longer
                    retryCount++;
                    if (retryCount < maxRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Min(retryCount * 5, 60));
                        _logger.LogInformation("Unity may not be running. Waiting {Seconds} seconds before retry...", delay.TotalSeconds);
                        await Task.Delay(delay);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error during connection attempt {Attempt}", retryCount + 1);
                    retryCount++;
                    
                    if (retryCount < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(retryCount * 2, 30)));
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