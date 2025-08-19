using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;

namespace McpUnity.DirectMcp.Services
{
    /// <summary>
    /// Manages tools exposed from Unity Editor
    /// </summary>
    public interface IUnityToolService
    {
        Task<List<Tool>> GetToolsAsync(CancellationToken cancellationToken = default);
        Task<CallToolResult> CallToolAsync(string name, Dictionary<string, object>? arguments, CancellationToken cancellationToken = default);
        void InvalidateCache();
    }

    public class UnityToolService : IUnityToolService
    {
        private readonly IUnityRpcClient _rpcClient;
        private readonly ILogger<UnityToolService> _logger;
        
        // Cache with TTL for hot-reload support
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(2);
        private DateTime _lastRefresh = DateTime.MinValue;
        private List<Tool>? _cachedTools;
        private readonly SemaphoreSlim _cacheLock = new(1, 1);

        public UnityToolService(IUnityRpcClient rpcClient, ILogger<UnityToolService> logger)
        {
            _rpcClient = rpcClient;
            _logger = logger;
        }

        public async Task<List<Tool>> GetToolsAsync(CancellationToken cancellationToken = default)
        {
            await _cacheLock.WaitAsync(cancellationToken);
            try
            {
                // Check cache
                if (_cachedTools != null && DateTime.UtcNow - _lastRefresh < _cacheExpiration)
                {
                    return _cachedTools;
                }

                // Fetch from Unity
                var response = await _rpcClient.SendRequestAsync("tools/list", null, cancellationToken);
                if (response == null || response["result"] == null)
                {
                    _logger.LogWarning("Failed to get tools from Unity");
                    return _cachedTools ?? new List<Tool>();
                }

                var tools = new List<Tool>();
                var toolsArray = response["result"]?["tools"] as JArray;
                
                if (toolsArray != null)
                {
                    foreach (var toolJson in toolsArray)
                    {
                        try
                        {
                            var tool = new Tool
                            {
                                Name = toolJson["name"]?.ToString() ?? "unknown",
                                Description = toolJson["description"]?.ToString(),
                                InputSchema = ConvertToJsonElement(toolJson["inputSchema"]) ?? default
                            };
                            tools.Add(tool);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to parse tool: {Tool}", toolJson);
                        }
                    }
                }

                _cachedTools = tools;
                _lastRefresh = DateTime.UtcNow;
                _logger.LogDebug("Refreshed tools cache with {Count} tools", tools.Count);
                
                return tools;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        public async Task<CallToolResult> CallToolAsync(string name, Dictionary<string, object>? arguments, CancellationToken cancellationToken = default)
        {
            var parameters = new JObject
            {
                ["name"] = name,
                ["arguments"] = arguments != null ? JObject.FromObject(arguments) : new JObject()
            };

            var response = await _rpcClient.SendRequestAsync("tools/call", parameters, cancellationToken);
            
            if (response == null)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock>
                    {
                        new TextContentBlock { Text = "Failed to call tool - not connected to Unity" }
                    }
                };
            }

            if (response["error"] != null)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock>
                    {
                        new TextContentBlock { Text = response["error"]?["message"]?.ToString() ?? "Unknown error" }
                    }
                };
            }

            var result = response["result"];
            if (result == null)
            {
                return new CallToolResult
                {
                    Content = new List<ContentBlock>()
                };
            }

            var content = new List<ContentBlock>();
            var contentArray = result["content"] as JArray;
            
            if (contentArray != null)
            {
                foreach (var item in contentArray)
                {
                    var type = item["type"]?.ToString();
                    if (type == "text")
                    {
                        content.Add(new TextContentBlock
                        {
                            Text = item["text"]?.ToString() ?? ""
                        });
                    }
                    else if (type == "image")
                    {
                        content.Add(new ImageContentBlock
                        {
                            Data = item["data"]?.ToString() ?? "",
                            MimeType = item["mimeType"]?.ToString() ?? "image/png"
                        });
                    }
                }
            }

            return new CallToolResult
            {
                IsError = result["isError"]?.Value<bool>() ?? false,
                Content = content
            };
        }

        public void InvalidateCache()
        {
            _cachedTools = null;
            _lastRefresh = DateTime.MinValue;
            _logger.LogDebug("Tool cache invalidated");
        }

        private JsonElement? ConvertToJsonElement(JToken? token)
        {
            if (token == null) return null;
            
            var json = token.ToString(Newtonsoft.Json.Formatting.None);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
    }
}