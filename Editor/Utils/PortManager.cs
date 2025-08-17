using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using UnityEditor;

namespace McpUnity.Utils
{
    /// <summary>
    /// Manages port allocation for multiple Unity MCP instances to avoid conflicts
    /// </summary>
    public static class PortManager
    {
        private const int BASE_PORT = 8090;
        private const int MAX_PORT_RANGE = 100;
        private const string PORT_REGISTRY_FILE = "mcp-unity-ports.json";
        
        [Serializable]
        private class PortRegistryEntry
        {
            public string projectPath;
            public string projectName;
            public int port;
            public long timestamp;
            public int processId;
        }
        
        [Serializable]
        private class PortRegistry
        {
            public List<PortRegistryEntry> entries = new List<PortRegistryEntry>();
        }
        
        private static string GetRegistryPath()
        {
            // Store in user's temp directory so it's shared across all Unity instances
            return Path.Combine(Path.GetTempPath(), PORT_REGISTRY_FILE);
        }
        
        /// <summary>
        /// Get or allocate a port for the current Unity project
        /// </summary>
        public static int GetOrAllocatePort()
        {
            string projectPath = Application.dataPath;
            string projectName = Application.productName;
            int currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
            
            // Load existing registry
            var registry = LoadRegistry();
            
            // Clean up stale entries (processes that no longer exist)
            CleanupStaleEntries(registry);
            
            // Check if this project already has a port
            var existingEntry = registry.entries.FirstOrDefault(e => 
                e.projectPath == projectPath && IsProcessAlive(e.processId));
            
            if (existingEntry != null)
            {
                // Update timestamp and process ID
                existingEntry.timestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
                existingEntry.processId = currentProcessId;
                SaveRegistry(registry);
                return existingEntry.port;
            }
            
            // Allocate a new port
            int newPort = FindAvailablePort(registry);
            
            // Register the new port
            registry.entries.Add(new PortRegistryEntry
            {
                projectPath = projectPath,
                projectName = projectName,
                port = newPort,
                timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                processId = currentProcessId
            });
            
            SaveRegistry(registry);
            
            Debug.Log($"[MCP Unity] Allocated port {newPort} for project {projectName}");
            return newPort;
        }
        
        /// <summary>
        /// Find an available port that's not in use
        /// </summary>
        private static int FindAvailablePort(PortRegistry registry)
        {
            var usedPorts = new HashSet<int>(registry.entries.Select(e => e.port));
            
            for (int port = BASE_PORT; port < BASE_PORT + MAX_PORT_RANGE; port++)
            {
                if (!usedPorts.Contains(port) && IsPortAvailable(port))
                {
                    return port;
                }
            }
            
            throw new Exception($"No available ports found in range {BASE_PORT}-{BASE_PORT + MAX_PORT_RANGE}");
        }
        
        /// <summary>
        /// Check if a port is available for binding
        /// </summary>
        private static bool IsPortAvailable(int port)
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    return true;
                }
            }
            catch
            {
                return false;
            }
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
        /// Remove entries for dead processes
        /// </summary>
        private static void CleanupStaleEntries(PortRegistry registry)
        {
            registry.entries.RemoveAll(e => !IsProcessAlive(e.processId));
        }
        
        /// <summary>
        /// Load the port registry from disk
        /// </summary>
        private static PortRegistry LoadRegistry()
        {
            try
            {
                string path = GetRegistryPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    return JsonUtility.FromJson<PortRegistry>(json) ?? new PortRegistry();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MCP Unity] Could not load port registry: {e.Message}");
            }
            
            return new PortRegistry();
        }
        
        /// <summary>
        /// Save the port registry to disk
        /// </summary>
        private static void SaveRegistry(PortRegistry registry)
        {
            try
            {
                string path = GetRegistryPath();
                string json = JsonUtility.ToJson(registry, true);
                File.WriteAllText(path, json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MCP Unity] Could not save port registry: {e.Message}");
            }
        }
        
        /// <summary>
        /// Get information about all active Unity MCP instances
        /// </summary>
        public static List<string> GetActiveInstances()
        {
            var registry = LoadRegistry();
            CleanupStaleEntries(registry);
            
            var instances = new List<string>();
            foreach (var entry in registry.entries)
            {
                instances.Add($"{entry.projectName} on port {entry.port} (PID: {entry.processId})");
            }
            
            return instances;
        }
        
        /// <summary>
        /// Release the port for the current project (called on shutdown)
        /// </summary>
        public static void ReleasePort()
        {
            string projectPath = Application.dataPath;
            var registry = LoadRegistry();
            
            registry.entries.RemoveAll(e => e.projectPath == projectPath);
            SaveRegistry(registry);
        }
    }
}