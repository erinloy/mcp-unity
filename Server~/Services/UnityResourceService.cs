using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;

namespace McpUnity.DirectMcp.Services
{
    /// <summary>
    /// Manages resources exposed from Unity Editor
    /// </summary>
    public interface IUnityResourceService
    {
        Task<List<Resource>> GetResourcesAsync(CancellationToken cancellationToken = default);
        Task<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken cancellationToken = default);
        void InvalidateCache();
    }

    public class UnityResourceService : IUnityResourceService
    {
        private readonly IUnityRpcClient _rpcClient;
        private readonly ILogger<UnityResourceService> _logger;
        
        // Cache with TTL
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(2);
        private DateTime _lastRefresh = DateTime.MinValue;
        private List<Resource>? _cachedResources;
        private readonly SemaphoreSlim _cacheLock = new(1, 1);

        public UnityResourceService(IUnityRpcClient rpcClient, ILogger<UnityResourceService> logger)
        {
            _rpcClient = rpcClient;
            _logger = logger;
        }

        public async Task<List<Resource>> GetResourcesAsync(CancellationToken cancellationToken = default)
        {
            await _cacheLock.WaitAsync(cancellationToken);
            try
            {
                // Check cache
                if (_cachedResources != null && DateTime.UtcNow - _lastRefresh < _cacheExpiration)
                {
                    return _cachedResources;
                }

                // Fetch from Unity
                var response = await _rpcClient.SendRequestAsync("resources/list", null, cancellationToken);
                if (response == null || response["result"] == null)
                {
                    _logger.LogWarning("Failed to get resources from Unity");
                    return _cachedResources ?? new List<Resource>();
                }

                var resources = new List<Resource>();
                var resourcesArray = response["result"]?["resources"] as JArray;
                
                if (resourcesArray != null)
                {
                    foreach (var resourceJson in resourcesArray)
                    {
                        try
                        {
                            var resource = new Resource
                            {
                                Uri = resourceJson["uri"]?.ToString() ?? "unknown",
                                Name = resourceJson["name"]?.ToString() ?? "unknown",
                                Description = resourceJson["description"]?.ToString(),
                                MimeType = resourceJson["mimeType"]?.ToString()
                            };
                            resources.Add(resource);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to parse resource: {Resource}", resourceJson);
                        }
                    }
                }

                _cachedResources = resources;
                _lastRefresh = DateTime.UtcNow;
                _logger.LogDebug("Refreshed resources cache with {Count} resources", resources.Count);
                
                return resources;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        public async Task<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
        {
            var parameters = new JObject
            {
                ["uri"] = uri
            };

            var response = await _rpcClient.SendRequestAsync("resources/read", parameters, cancellationToken);
            
            if (response == null)
            {
                return new ReadResourceResult
                {
                    Contents = new List<ResourceContents>
                    {
                        new TextResourceContents 
                        { 
                            Uri = uri,
                            Text = "Failed to read resource - not connected to Unity" 
                        }
                    }
                };
            }

            if (response["error"] != null)
            {
                return new ReadResourceResult
                {
                    Contents = new List<ResourceContents>
                    {
                        new TextResourceContents 
                        { 
                            Uri = uri,
                            Text = $"Error: {response["error"]?["message"]?.ToString() ?? "Unknown error"}" 
                        }
                    }
                };
            }

            var result = response["result"];
            if (result == null)
            {
                return new ReadResourceResult
                {
                    Contents = new List<ResourceContents>()
                };
            }

            var contents = new List<ResourceContents>();
            var contentsArray = result["contents"] as JArray;
            
            if (contentsArray != null)
            {
                foreach (var item in contentsArray)
                {
                    var itemUri = item["uri"]?.ToString() ?? uri;
                    var mimeType = item["mimeType"]?.ToString();
                    
                    if (mimeType?.StartsWith("text/") == true || string.IsNullOrEmpty(mimeType))
                    {
                        contents.Add(new TextResourceContents
                        {
                            Uri = itemUri,
                            MimeType = mimeType,
                            Text = item["text"]?.ToString() ?? ""
                        });
                    }
                    else
                    {
                        contents.Add(new BlobResourceContents
                        {
                            Uri = itemUri,
                            MimeType = mimeType,
                            Blob = item["blob"]?.ToString() ?? ""
                        });
                    }
                }
            }

            return new ReadResourceResult
            {
                Contents = contents
            };
        }

        public void InvalidateCache()
        {
            _cachedResources = null;
            _lastRefresh = DateTime.MinValue;
            _logger.LogDebug("Resource cache invalidated");
        }
    }
}