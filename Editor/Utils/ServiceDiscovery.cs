using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using McpUnity.Unity;

namespace McpUnity.Utils
{
    /// <summary>
    /// Service discovery for MCP Unity instances
    /// Allows MCP clients to discover which Unity instances are running and on which ports
    /// </summary>
    public static class ServiceDiscovery
    {
        private const string DISCOVERY_FILE = "mcp-unity-discovery.json";
        
        [Serializable]
        public class ServiceInfo
        {
            public string projectName;
            public string projectPath;
            public int port;
            public string unityVersion;
            public string mcpUnityVersion;
            public long timestamp;
            public int processId;
            public string hostname;
            
            // For identifying the MCP instance
            public string mcpInstanceId; // e.g., "unity-ziltch", "unity-poma"
        }
        
        [Serializable]
        public class ServiceRegistry
        {
            public List<ServiceInfo> services = new List<ServiceInfo>();
        }
        
        /// <summary>
        /// Get the discovery file path - stored in a well-known location
        /// </summary>
        private static string GetDiscoveryPath()
        {
            // Use user's home directory for cross-project discovery
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string mcpDir = Path.Combine(userHome, ".mcp-unity");
            
            if (!Directory.Exists(mcpDir))
            {
                Directory.CreateDirectory(mcpDir);
            }
            
            return Path.Combine(mcpDir, DISCOVERY_FILE);
        }
        
        /// <summary>
        /// Register this Unity instance for discovery
        /// </summary>
        public static void RegisterService(int port)
        {
            var registry = LoadRegistry();
            CleanupStaleServices(registry);
            
            // Generate MCP instance ID based on project name
            string projectName = Application.productName;
            string mcpInstanceId = GenerateMcpInstanceId(projectName);
            
            // Remove any existing entry for this project
            registry.services.RemoveAll(s => s.projectPath == Application.dataPath);
            
            // Add new service info
            var serviceInfo = new ServiceInfo
            {
                projectName = projectName,
                projectPath = Application.dataPath,
                port = port,
                unityVersion = Application.unityVersion,
                mcpUnityVersion = McpUnitySettings.ServerVersion,
                timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                hostname = System.Environment.MachineName,
                mcpInstanceId = mcpInstanceId
            };
            
            registry.services.Add(serviceInfo);
            SaveRegistry(registry);
            
            Debug.Log($"[MCP Unity] Registered service discovery: {mcpInstanceId} on port {port}");
        }
        
        /// <summary>
        /// Generate a consistent MCP instance ID from project name
        /// </summary>
        private static string GenerateMcpInstanceId(string projectName)
        {
            // Convert to lowercase and replace spaces/special chars
            string baseId = projectName.ToLower()
                .Replace(" ", "-")
                .Replace(".", "-")
                .Replace("_", "-");
            
            // Prefix with "unity-" if not already
            if (!baseId.StartsWith("unity-"))
            {
                baseId = "unity-" + baseId;
            }
            
            return baseId;
        }
        
        /// <summary>
        /// Unregister this Unity instance
        /// </summary>
        public static void UnregisterService()
        {
            var registry = LoadRegistry();
            registry.services.RemoveAll(s => s.projectPath == Application.dataPath);
            SaveRegistry(registry);
        }
        
        /// <summary>
        /// Get all active Unity MCP services
        /// </summary>
        public static List<ServiceInfo> GetActiveServices()
        {
            var registry = LoadRegistry();
            CleanupStaleServices(registry);
            SaveRegistry(registry);
            return registry.services;
        }
        
        /// <summary>
        /// Remove services from dead processes
        /// </summary>
        private static void CleanupStaleServices(ServiceRegistry registry)
        {
            registry.services.RemoveAll(s => !IsProcessAlive(s.processId));
        }
        
        /// <summary>
        /// Check if a process is still alive
        /// </summary>
        private static bool IsProcessAlive(int processId)
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Load the service registry
        /// </summary>
        private static ServiceRegistry LoadRegistry()
        {
            try
            {
                string path = GetDiscoveryPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    return JsonUtility.FromJson<ServiceRegistry>(json) ?? new ServiceRegistry();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MCP Unity] Could not load service registry: {e.Message}");
            }
            
            return new ServiceRegistry();
        }
        
        /// <summary>
        /// Save the service registry
        /// </summary>
        private static void SaveRegistry(ServiceRegistry registry)
        {
            try
            {
                string path = GetDiscoveryPath();
                string json = JsonUtility.ToJson(registry, true);
                File.WriteAllText(path, json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MCP Unity] Could not save service registry: {e.Message}");
            }
        }
        
        /// <summary>
        /// Generate a configuration snippet for MCP clients
        /// </summary>
        public static string GenerateMcpConfiguration()
        {
            var services = GetActiveServices();
            var configs = new Dictionary<string, object>();
            
            foreach (var service in services)
            {
                // Use the predictable location at project root
                string exePath = Path.Combine(service.projectPath, "..", "Tools", "unity-mcp", "unity-mcp.exe");
                configs[service.mcpInstanceId] = new
                {
                    command = exePath,
                    args = new string[] { },  // No args needed for C# exe
                    env = new Dictionary<string, string>
                    {
                        ["UNITY_PORT"] = service.port.ToString(),
                        ["UNITY_PROJECT"] = service.projectName
                    }
                };
            }
            
            return JsonUtility.ToJson(new { mcpServers = configs }, true);
        }
    }
}