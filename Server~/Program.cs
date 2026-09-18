using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using McpUnity.DirectMcp.Services;
using McpUnity.DirectMcp.Tools;
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
            // Set to Information level to see tool discovery
            builder.Logging.SetMinimumLevel(LogLevel.Information);
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

        /// <summary>
        /// Names of the tools declared statically in <see cref="UnityTools"/>.
        /// </summary>
        private static readonly HashSet<string> StaticToolNames = typeof(UnityTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        private static void ConfigureMcpServer(HostApplicationBuilder builder)
        {
            builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "unity-mcp",
                    Version = "3.0.0" // Clean architecture version
                };

                // Enable lazy tool discovery - tools can be updated after connection
                options.Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = true },
                    Resources = new ResourcesCapability { ListChanged = true }
                };
            })
            .WithStdioServerTransport()

            // Register static Unity tools (available at startup)
            .WithTools<UnityTools>()

            // Tool handlers (for dynamic Unity tools)
            .WithListToolsHandler(async (context, ct) =>
            {
                var logger = context.Services!.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("MCP tools/list request received");
                
                var toolService = context.Services!.GetRequiredService<IUnityToolService>();
                // Tools with a static wrapper in UnityTools are listed by the SDK from the tool
                // collection; drop Unity's dynamic entry for those names to avoid duplicates.
                var tools = (await toolService.GetToolsAsync(ct))
                    .Where(tool => !StaticToolNames.Contains(tool.Name))
                    .ToList();
                
                logger.LogInformation("Returning {Count} dynamic Unity tools to MCP client", tools.Count);
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

                // Pass the JsonElement values through unchanged so objects, arrays, numbers and
                // booleans reach Unity with their JSON types intact (UnityToolService converts them).
                Dictionary<string, object>? arguments = null;
                if (request.Arguments != null)
                {
                    arguments = new Dictionary<string, object>();
                    foreach (var kvp in request.Arguments)
                    {
                        arguments[kvp.Key] = kvp.Value;
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