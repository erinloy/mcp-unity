using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace McpUnity.DirectMcp.Services
{
    /// <summary>
    /// Service responsible for locating Unity project directories
    /// </summary>
    public interface IUnityProjectLocator
    {
        string? FindUnityProject();
    }

    public class UnityProjectLocator : IUnityProjectLocator
    {
        private readonly ILogger<UnityProjectLocator> _logger;
        private const int MaxSearchDepth = 15;

        public UnityProjectLocator(ILogger<UnityProjectLocator> logger)
        {
            _logger = logger;
        }

        public string? FindUnityProject()
        {
            var currentDir = Directory.GetCurrentDirectory();
            var exePath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
            
            // First check if we're already in a Unity project
            if (IsUnityProject(currentDir))
            {
                _logger.LogInformation("Found Unity project at current directory: {Path}", currentDir);
                return currentDir;
            }
            
            // Search from exe path upwards
            if (!string.IsNullOrEmpty(exePath))
            {
                var dir = exePath;
                _logger.LogDebug("Starting Unity project search from exe path: {Path}", dir);
                
                for (int i = 0; i < MaxSearchDepth; i++)
                {
                    _logger.LogDebug("Checking directory level {Level}: {Path}", i, dir);
                    
                    if (IsUnityProject(dir))
                    {
                        _logger.LogInformation("Found Unity project at: {Path} (level {Level} from exe)", dir, i);
                        return dir;
                    }
                    
                    var parent = Directory.GetParent(dir);
                    if (parent == null) 
                    {
                        _logger.LogDebug("Reached root directory without finding Unity project");
                        break;
                    }
                    dir = parent.FullName;
                }
            }
            
            // Also check from current directory upwards
            var currentDirSearch = currentDir;
            _logger.LogDebug("Starting Unity project search from current directory: {Path}", currentDirSearch);
            for (int i = 0; i < MaxSearchDepth; i++)
            {
                if (IsUnityProject(currentDirSearch))
                {
                    _logger.LogInformation("Found Unity project at: {Path} (level {Level} from current dir)", currentDirSearch, i);
                    return currentDirSearch;
                }
                
                var parent = Directory.GetParent(currentDirSearch);
                if (parent == null) break;
                currentDirSearch = parent.FullName;
            }
            
            _logger.LogError("Could not find Unity project. Searched from exe path: {ExePath} and current dir: {CurrentDir}", exePath, currentDir);
            return null;
        }

        private bool IsUnityProject(string path)
        {
            return Directory.Exists(Path.Combine(path, "Assets")) &&
                   Directory.Exists(Path.Combine(path, "ProjectSettings"));
        }
    }
}