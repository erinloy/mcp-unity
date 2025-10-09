using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using McpUnity.DirectMcp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpUnity.DirectMcp
{
    /// <summary>
    /// Unity MCP Server - Clean architecture implementation
    /// Bridges Unity Editor with MCP protocol through well-defined services
    /// </summary>
    public class Program
    {
        static async Task Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);

            // Configure logging for MCP compatibility (stderr)
            ConfigureLogging(builder);

            // Register Unity services with single responsibilities
            RegisterServices(builder);

            // Configure MCP server
            ConfigureMcpServer(builder);

            var host = builder.Build();
            await host.RunAsync();
        }

        private static void ConfigureLogging(HostApplicationBuilder builder)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });
            // Set to Warning level to reduce verbosity for connection attempts
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
        }

        private static void RegisterServices(HostApplicationBuilder builder)
        {
            // Core services
            builder.Services.AddSingleton<IUnityProjectLocator, UnityProjectLocator>();
            builder.Services.AddSingleton<IWebSocketConnectionManager, WebSocketConnectionManager>();
            builder.Services.AddSingleton<IUnityRpcClient, UnityRpcClient>();

            // Business services
            builder.Services.AddSingleton<IUnityToolService, UnityToolService>();
            builder.Services.AddSingleton<IUnityResourceService, UnityResourceService>();

            // Coordination service
            builder.Services.AddSingleton<UnityBridgeService>();
            builder.Services.AddHostedService<UnityBridgeService>(provider =>
                provider.GetRequiredService<UnityBridgeService>());
        }

        private static void ConfigureMcpServer(HostApplicationBuilder builder)
        {
            builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "unity-mcp",
                    Version = "3.0.0" // Clean architecture version
                };
            })
            .WithStdioServerTransport()
            
            // Tool handlers
            .WithListToolsHandler(async (context, ct) =>
            {
                var logger = context.Services!.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("MCP tools/list request received");
                
                var toolService = context.Services!.GetRequiredService<IUnityToolService>();
                var tools = await toolService.GetToolsAsync(ct);
                
                logger.LogInformation("Returning {Count} tools to MCP client", tools.Count);
                foreach (var tool in tools)
                {
                    logger.LogDebug("Tool: {Name} - {Description}", tool.Name, tool.Description);
                }
                
                return new ListToolsResult
                {
                    Tools = tools
                };
            })
            .WithCallToolHandler(async (context, ct) =>
            {
                var toolService = context.Services!.GetRequiredService<IUnityToolService>();
                var request = context.Params;
                
                if (request == null || string.IsNullOrEmpty(request.Name))
                {
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = new List<ContentBlock>
                        {
                            new TextContentBlock { Text = "Tool name is required" }
                        }
                    };
                }

                // Convert JsonElement dictionary to regular dictionary
                Dictionary<string, object>? arguments = null;
                if (request.Arguments != null)
                {
                    arguments = new Dictionary<string, object>();
                    foreach (var kvp in request.Arguments)
                    {
                        arguments[kvp.Key] = kvp.Value.ToString();
                    }
                }
                return await toolService.CallToolAsync(request.Name, arguments, ct);
            })
            
            // Resource handlers
            .WithListResourcesHandler(async (context, ct) =>
            {
                var resourceService = context.Services!.GetRequiredService<IUnityResourceService>();
                return new ListResourcesResult
                {
                    Resources = await resourceService.GetResourcesAsync(ct)
                };
            })
            .WithReadResourceHandler(async (context, ct) =>
            {
                var resourceService = context.Services!.GetRequiredService<IUnityResourceService>();
                var request = context.Params;
                
                if (request == null || string.IsNullOrEmpty(request.Uri))
                {
                    return new ReadResourceResult
                    {
                        Contents = new List<ResourceContents>
                        {
                            new TextResourceContents 
                            { 
                                Uri = "error",
                                Text = "Resource URI is required" 
                            }
                        }
                    };
                }

                return await resourceService.ReadResourceAsync(request.Uri, ct);
            })
            
            // Optional: Prompt handlers (if Unity provides prompt templates)
            .WithListPromptsHandler(async (context, ct) =>
            {
                // Currently no prompts from Unity
                return await Task.FromResult(new ListPromptsResult
                {
                    Prompts = new List<Prompt>()
                });
            })
            .WithGetPromptHandler(async (context, ct) =>
            {
                // Currently no prompts from Unity
                return await Task.FromResult(new GetPromptResult
                {
                    Messages = new List<PromptMessage>()
                });
            });
        }
    }
}