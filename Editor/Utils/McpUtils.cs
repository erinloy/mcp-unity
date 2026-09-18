using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using McpUnity.Unity;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace McpUnity.Utils
{
    /// <summary>
    /// Controls how the path to the MCP server executable (Tools/unity-mcp/unity-mcp.exe) is rendered in generated MCP configs.
    /// </summary>
    public enum PathMode
    {
        /// <summary>Absolute filesystem path. Required for per-user/global configs (Cursor, Windsurf, etc.).</summary>
        Absolute,
        /// <summary>Path relative to the Unity project root. Used for OpenCode's opencode.json and project-local configs.</summary>
        ProjectRelative,
        /// <summary>Project-relative path prefixed with ${workspaceFolder}/. Used for VS Code / GitHub Copilot's .vscode/mcp.json.</summary>
        VSCodeWorkspaceFolder
    }

    /// <summary>
    /// Utility class for MCP configuration and system operations
    /// </summary>
    public static class McpUtils
    {
        /// <summary>
        /// File name of the C# MCP server project inside Server~.
        /// </summary>
        public const string ServerProjectFileName = "UnityMcp.csproj";

        // Cached result for Multiplayer Play Mode clone detection
        private static bool? _isMultiplayerPlayModeClone;

        /// <summary>
        /// Generates the MCP configuration JSON to setup the Unity MCP server in different AI Clients
        /// </summary>
        public static string GenerateMcpConfigJson(bool useTabsIndentation, PathMode pathMode = PathMode.Absolute)
        {
            var config = new Dictionary<string, object>
            {
                { "mcpServers", new Dictionary<string, object>
                    {
                        { "mcp-unity", new Dictionary<string, object>
                            {
                                { "command", GetServerCommandPath(pathMode) },
                                { "args", new string[] { } },
                                { "env", new Dictionary<string, object>
                                    {
                                        { "MCP_UNITY_SETTINGS_PATH", GetSettingsFilePath() },
                                        { "MCP_UNITY_AUTH_TOKEN_PATH", GetAuthTokenFilePath() }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            // Initialize string writer with proper indentation
            var stringWriter = new StringWriter();
            using (var jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.Formatting = Formatting.Indented;

                // Set indentation character and count
                if (useTabsIndentation)
                {
                    jsonWriter.IndentChar = '\t';
                    jsonWriter.Indentation = 1;
                }
                else
                {
                    jsonWriter.IndentChar = ' ';
                    jsonWriter.Indentation = 2;
                }

                // Serialize directly to the JsonTextWriter
                var serializer = new JsonSerializer();
                serializer.Serialize(jsonWriter, config);
            }

            return stringWriter.ToString().Replace("\\", "/").Replace("//", "/");
        }

        /// <summary>
        /// Generates the MCP configuration JSON for OpenCode (https://opencode.ai/).
        /// OpenCode uses a different schema than the standard `mcpServers` shape:
        ///   { "$schema": ..., "mcp": { "mcp-unity": { "type": "local", "enabled": true, "command": [...], "environment": {} } } }
        /// </summary>
        public static string GenerateOpenCodeConfigJson(bool useTabsIndentation, PathMode pathMode = PathMode.Absolute)
        {
            var config = new Dictionary<string, object>
            {
                { "$schema", "https://opencode.ai/config.json" },
                { "mcp", new Dictionary<string, object>
                    {
                        { "mcp-unity", new Dictionary<string, object>
                            {
                                { "type", "local" },
                                { "enabled", true },
                                { "command", new[] { GetServerCommandPath(pathMode) } },
                                { "environment", new Dictionary<string, object>
                                    {
                                        { "MCP_UNITY_SETTINGS_PATH", GetSettingsFilePath() },
                                        { "MCP_UNITY_AUTH_TOKEN_PATH", GetAuthTokenFilePath() }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            var stringWriter = new StringWriter();
            using (var jsonWriter = new JsonTextWriter(stringWriter))
            {
                jsonWriter.Formatting = Formatting.Indented;

                if (useTabsIndentation)
                {
                    jsonWriter.IndentChar = '\t';
                    jsonWriter.Indentation = 1;
                }
                else
                {
                    jsonWriter.IndentChar = ' ';
                    jsonWriter.Indentation = 2;
                }

                var serializer = new JsonSerializer();
                serializer.Serialize(jsonWriter, config);
            }

            return stringWriter.ToString().Replace("\\", "/").Replace("//", "/");
        }

        /// <summary>
        /// Generates the MCP configuration TOML to setup the Unity MCP server in TOML-based AI Clients (e.g., Codex CLI)
        /// </summary>
        /// <returns>The TOML configuration string for mcp-unity server</returns>
        public static string GenerateMcpConfigToml(PathMode pathMode = PathMode.Absolute)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[mcp_servers.mcp-unity]");
            sb.AppendLine($"command = \"{GetServerCommandPath(pathMode)}\"");
            sb.AppendLine("args = []");
            sb.AppendLine($"env = {{ MCP_UNITY_SETTINGS_PATH = \"{GetSettingsFilePath()}\", MCP_UNITY_AUTH_TOKEN_PATH = \"{GetAuthTokenFilePath()}\" }}");
            return sb.ToString();
        }

        /// <summary>
        /// Returns the project settings path used by the MCP server. Generated configurations pass
        /// it explicitly so the server cannot accidentally select another Unity project by cwd.
        /// </summary>
        private static string GetSettingsFilePath()
        {
            return Path.Combine(GetUnityProjectRoot(), "ProjectSettings", "McpUnitySettings.json").Replace("\\", "/");
        }

        private static string GetAuthTokenFilePath()
        {
            return McpUnityAuthentication.TokenPath.Replace("\\", "/");
        }

        /// <summary>
        /// Gets the Unity project root directory
        /// </summary>
        public static string GetUnityProjectRoot()
        {
            // Get the path to the Assets folder and go up one level
            return Path.GetDirectoryName(Application.dataPath);
        }

        /// <summary>
        /// Absolute path of the deployed C# MCP server executable: &lt;ProjectRoot&gt;/Tools/unity-mcp/unity-mcp.exe.
        /// The Server~ project's post-build step deploys it there.
        /// </summary>
        public static string GetServerExecutablePath()
        {
            return Path.Combine(GetUnityProjectRoot(), "Tools", "unity-mcp", "unity-mcp.exe");
        }

        /// <summary>
        /// Returns the path to the MCP server executable rendered according to the given <see cref="PathMode"/>.
        /// All returned paths use forward slashes.
        /// </summary>
        private static string GetServerCommandPath(PathMode mode)
        {
            string absoluteExePath = GetServerExecutablePath().Replace("\\", "/");

            if (mode == PathMode.Absolute)
            {
                return absoluteExePath;
            }

            string projectRoot = GetUnityProjectRoot().Replace("\\", "/");
            string relativeExePath = Path.GetRelativePath(projectRoot, absoluteExePath).Replace("\\", "/");

            if (mode == PathMode.VSCodeWorkspaceFolder)
            {
                return "${workspaceFolder}/" + relativeExePath;
            }

            return relativeExePath;
        }

        /// <summary>
        /// Gets the absolute path to the Server~ directory containing the C# MCP server project.
        /// Works whether MCP Unity is installed via Package Manager or directly in the Assets folder
        /// </summary>
        public static string GetServerPath()
        {
            // First, try to find the package info via Package Manager
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath($"Packages/{McpUnitySettings.PackageName}");

            if (packageInfo != null && !string.IsNullOrEmpty(packageInfo.resolvedPath))
            {
                string serverPath = Path.Combine(packageInfo.resolvedPath, "Server~");

                return CleanPathPrefix(serverPath);
            }

            // Installed directly in the Assets folder: locate the McpUnity.Editor assembly definition
            var mcpEditorAssets = AssetDatabase.FindAssets("McpUnity.Editor t:AssemblyDefinitionAsset");
            foreach (var guid in mcpEditorAssets)
            {
                string asmdefPath = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(asmdefPath) != "McpUnity.Editor.asmdef")
                {
                    continue;
                }

                // Navigate from Editor/McpUnity.Editor.asmdef to the package root, then to Server~
                string fullAsmdefPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", asmdefPath));
                string mcpUnityRoot = Path.GetDirectoryName(Path.GetDirectoryName(fullAsmdefPath));
                string serverPath = Path.Combine(mcpUnityRoot, "Server~");

                if (File.Exists(Path.Combine(serverPath, ServerProjectFileName)))
                {
                    return CleanPathPrefix(serverPath);
                }
            }

            // Last resort: scan Assets for a Server~ folder containing the server project
            string[] dirs = Directory.GetDirectories("Assets", "Server~", SearchOption.AllDirectories);
            for (int n = 0; n < dirs.Length; n++)
            {
                if (File.Exists(Path.Combine(dirs[n], ServerProjectFileName)))
                {
                    return CleanPathPrefix(Path.GetFullPath(dirs[n]));
                }
            }

            // If we get here, we couldn't find the server path
            var errorString = "[MCP Unity] Could not locate Server directory. Please check the installation of the MCP Unity package.";

            Debug.LogError(errorString);

            return errorString;
        }

        /// <summary>
        /// Cleans the path prefix by removing a leading "~" character if present on macOS.
        /// </summary>
        /// <param name="path">The path to clean.</param>
        /// <returns>The cleaned path.</returns>
        private static string CleanPathPrefix(string path)
        {
            if (path.StartsWith("~"))
            {
                return path.Substring(1);
            }
            return path;
        }

        /// <summary>
        /// Encodes a file path for use in file:// URLs by replacing spaces with %20.
        /// </summary>
        /// <param name="path">The path to encode.</param>
        /// <returns>The encoded path suitable for file:// URLs.</returns>
        public static string EncodePathForFileUrl(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            return path.Replace(" ", "%20");
        }

        /// <summary>
        /// Validates the server path and returns true if it contains the C# MCP server project.
        /// </summary>
        /// <param name="serverPath">The server path to validate.</param>
        /// <returns>True if path is valid and usable, false if path has critical issues.</returns>
        public static bool ValidateServerPath(string serverPath)
        {
            if (string.IsNullOrEmpty(serverPath))
            {
                Debug.LogError("[MCP Unity] Server path is null or empty. Cannot validate.");
                return false;
            }

            // Verify the path exists
            if (!Directory.Exists(serverPath))
            {
                Debug.LogError($"[MCP Unity] Server path does not exist: {serverPath}");
                return false;
            }

            // Verify required files exist
            string projectFilePath = Path.Combine(serverPath, ServerProjectFileName);
            if (!File.Exists(projectFilePath))
            {
                Debug.LogError($"[MCP Unity] {ServerProjectFileName} not found in server path: {serverPath}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Ensures the C# MCP server is built and available
        /// </summary>
        /// <returns>True if the server executable exists or was successfully built</returns>
        public static bool EnsureCSharpServerBuilt()
        {
            string exePath = GetServerExecutablePath();

            if (File.Exists(exePath))
            {
                McpLogger.LogInfo($"[MCP] C# server already built at: {exePath}");
                return true;
            }

            McpLogger.LogWarning($"[MCP] C# server executable not found. Expected at: {exePath}");

            var server = McpUnityServer.Instance;
            if (server == null)
            {
                McpLogger.LogError("[MCP] Cannot build the C# server: the MCP Unity server is disabled in this process (batch mode).");
                return false;
            }

            server.InstallServer();
            if (File.Exists(exePath))
            {
                return true;
            }

            McpLogger.LogError($"[MCP] Please build the server manually with: dotnet publish -c Release -r win-x64 --self-contained (in {GetServerPath()})");
            return false;
        }

        /// <summary>
        /// Adds the MCP configuration to the Windsurf MCP config file
        /// </summary>
        public static bool AddToWindsurfIdeConfig(bool useTabsIndentation)
        {
            string configFilePath = GetWindsurfMcpConfigPath();
            return AddToConfigFile(configFilePath, useTabsIndentation, "Windsurf");
        }
        
        /// <summary>
        /// Adds the MCP configuration to the Claude Desktop config file
        /// </summary>
        public static bool AddToClaudeDesktopConfig(bool useTabsIndentation)
        {
            string configFilePath = GetClaudeDesktopConfigPath();
            return AddToConfigFile(configFilePath, useTabsIndentation, "Claude Desktop");
        }
        
        /// <summary>
        /// Adds the MCP configuration to the Cursor config file
        /// </summary>
        public static bool AddToCursorConfig(bool useTabsIndentation)
        {
            string configFilePath = GetCursorConfigPath();
            return AddToConfigFile(configFilePath, useTabsIndentation, "Cursor");
        }
        
        /// <summary>
        /// Adds the MCP configuration to the Claude Code config file
        /// </summary>
        public static bool AddToClaudeCodeConfig(bool useTabsIndentation)
        {
            string configFilePath = GetClaudeCodeConfigPath();
            return AddToConfigFile(configFilePath, useTabsIndentation, "Claude Code");
        }

        /// <summary>
        /// Adds the MCP configuration to the Google Antigravity config file
        /// </summary>
        public static bool AddToAntigravityConfig(bool useTabsIndentation)
        {
            string configFilePath = GetAntigravityConfigPath();
            return AddToConfigFile(configFilePath, useTabsIndentation, "Google Antigravity");
        }

        /// <summary>
        /// Adds the MCP configuration to the GitHub Copilot config file.
        /// Uses ${workspaceFolder}-prefixed path so the config is portable across machines when committed to git.
        /// </summary>
        public static bool AddToGitHubCopilotConfig(bool useTabsIndentation)
        {
            string configFilePath = GetGitHubCopilotConfigPath();
            return AddToConfigFile(configFilePath, useTabsIndentation, "GitHub Copilot", PathMode.VSCodeWorkspaceFolder);
        }

        /// <summary>
        /// Adds the MCP configuration to the Codex CLI config file (TOML format)
        /// </summary>
        public static bool AddToCodexCliConfig(bool useTabsIndentation)
        {
            string configFilePath = GetCodexCliConfigPath();
            return AddToTomlConfigFile(configFilePath, "Codex CLI");
        }

        /// <summary>
        /// Adds the MCP configuration to the OpenCode config file (opencode.json in project root).
        /// OpenCode uses a custom JSON schema, so this does not reuse the standard mcpServers helpers.
        /// Uses a project-relative path so the config is portable across machines when committed to git.
        /// </summary>
        public static bool AddToOpenCodeConfig(bool useTabsIndentation)
        {
            string configFilePath = GetOpenCodeConfigPath();
            return AddToOpenCodeConfigFile(configFilePath, useTabsIndentation, PathMode.ProjectRelative);
        }

        /// <summary>
        /// Adds the MCP configuration to the project-local Cursor config (<ProjectRoot>/.cursor/mcp.json).
        /// Uses a project-relative path so the config is portable across machines when committed to git.
        /// </summary>
        public static bool AddToCursorProjectConfig(bool useTabsIndentation)
        {
            return AddToConfigFile(GetCursorProjectConfigPath(), useTabsIndentation, "Cursor (Project)", PathMode.ProjectRelative);
        }

        /// <summary>
        /// Adds the MCP configuration to the project-local Claude Code config (<ProjectRoot>/.mcp.json).
        /// This file is Claude Code's team-shared MCP config and is intended to be committed to git.
        /// Uses a project-relative path so the config is portable across machines.
        /// </summary>
        public static bool AddToClaudeCodeProjectConfig(bool useTabsIndentation)
        {
            return AddToConfigFile(GetClaudeCodeProjectConfigPath(), useTabsIndentation, "Claude Code (Project)", PathMode.ProjectRelative);
        }

        /// <summary>
        /// Adds the MCP configuration to the project-local Codex CLI config (<ProjectRoot>/.codex/config.toml).
        /// Codex layers this over the global ~/.codex/config.toml only when the project is marked trusted
        /// (Codex prompts the user the first time they run `codex` from the project root).
        /// Uses a project-relative path so the config is portable across machines.
        /// </summary>
        public static bool AddToCodexCliProjectConfig(bool useTabsIndentation)
        {
            return AddToTomlConfigFile(GetCodexCliProjectConfigPath(), "Codex CLI (Project)", PathMode.ProjectRelative);
        }

        /// <summary>
        /// Returns whether automatic MCP configuration is supported for the given product on the current platform.
        /// </summary>
        public static bool IsAutoConfigSupported(string productName)
        {
            switch (productName)
            {
                case "Claude Code":
                case "Claude Code (Project)":
                case "Codex CLI":
                case "Codex CLI (Project)":
                case "Cursor (Project)":
                case "GitHub Copilot":
                case "OpenCode":
                    return Application.platform == RuntimePlatform.WindowsEditor
                        || Application.platform == RuntimePlatform.OSXEditor
                        || Application.platform == RuntimePlatform.LinuxEditor;
                case "Windsurf":
                case "Claude Desktop":
                case "Cursor":
                case "Google Antigravity":
                    return Application.platform == RuntimePlatform.WindowsEditor
                        || Application.platform == RuntimePlatform.OSXEditor;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns a human-readable reason when automatic MCP configuration is unsupported.
        /// </summary>
        public static string GetAutoConfigUnsupportedReason(string productName)
        {
            if (IsAutoConfigSupported(productName))
            {
                return null;
            }

            if (Application.platform == RuntimePlatform.LinuxEditor)
            {
                return $"Automatic {productName} configuration is currently available on Linux only for Claude Code, Codex CLI, Cursor (Project), GitHub Copilot, and OpenCode.";
            }

            return $"Automatic {productName} configuration is not supported on {Application.platform}.";
        }

        /// <summary>
        /// Common method to add MCP configuration to a specified config file
        /// </summary>
        /// <param name="configFilePath">Path to the config file</param>
        /// <param name="useTabsIndentation">Whether to use tabs for indentation</param>
        /// <param name="productName">Name of the product (for error messages)</param>
        /// <param name="pathMode">How to render the path to the MCP server executable</param>
        /// <returns>True if successfuly added the config, false otherwise</returns>
        private static bool AddToConfigFile(string configFilePath, bool useTabsIndentation, string productName, PathMode pathMode = PathMode.Absolute)
        {
            if (string.IsNullOrEmpty(configFilePath))
            {
                Debug.LogError($"{productName} config file not found. Please make sure {productName} is installed.");
                return false;
            }

            // Generate fresh MCP config JSON
            string mcpConfigJson = GenerateMcpConfigJson(useTabsIndentation, pathMode);
            
            try
            {
                // Parse the MCP config JSON
                JObject mcpConfig = JObject.Parse(mcpConfigJson);

                // Check if the file exists
                if (File.Exists(configFilePath))
                {
                    return TryMergeMcpServers(configFilePath, mcpConfig, productName);
                }
                else if(Directory.Exists(Path.GetDirectoryName(configFilePath)))
                {
                    // Create a new config file with just our config
                    File.WriteAllText(configFilePath, mcpConfigJson);
                    return true;
                }
                else
                {
                    Debug.LogError($"Cannot find {productName} config file or {productName} is currently not installed. Expecting {productName} to be installed in the {configFilePath} path");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to add MCP configuration to {productName}: {ex}");
            }

            return false;
        }
        
        /// <summary>
        /// Gets the path to the Windsurf MCP config file based on the current OS
        /// </summary>
        /// <returns>The path to the Windsurf MCP config file</returns>
        private static string GetWindsurfMcpConfigPath()
        {
            // Base path depends on the OS
            string basePath;
            
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                // Windows: %USERPROFILE%/.codeium/windsurf
                basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codeium/windsurf");
            }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            {
                // macOS: ~/Library/Application Support/.codeium/windsurf
                string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                basePath = Path.Combine(homeDir, ".codeium/windsurf");
            }
            else
            {
                // Unsupported platform
                Debug.LogError("Unsupported platform for Windsurf MCP config");
                return null;
            }
            
            // Return the path to the mcp_config.json file
            return Path.Combine(basePath, "mcp_config.json");
        }
        
        /// <summary>
        /// Gets the path to the Claude Desktop config file based on the current OS
        /// </summary>
        /// <returns>The path to the Claude Desktop config file</returns>
        private static string GetClaudeDesktopConfigPath()
        {
            // Base path depends on the OS
            string basePath;
            
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                // Windows: %USERPROFILE%/AppData/Roaming/Claude
                basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
            }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            {
                // macOS: ~/Library/Application Support/Claude
                string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                basePath = Path.Combine(homeDir, "Library", "Application Support", "Claude");
            }
            else
            {
                // Unsupported platform
                Debug.LogError("Unsupported platform for Claude Desktop config");
                return null;
            }
            
            // Return the path to the claude_desktop_config.json file
            return Path.Combine(basePath, "claude_desktop_config.json");
        }

        /// <summary>
        /// Gets the path to the Cursor config file based on the current OS
        /// </summary>
        /// <returns>The path to the Cursor config file</returns>
        private static string GetCursorConfigPath()
        {
            // Base path depends on the OS
            string basePath;
            
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                // Windows: %USERPROFILE%/.cursor
                basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor");
            }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            {
                // macOS: ~/.cursor
                string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                basePath = Path.Combine(homeDir, ".cursor");
            }
            else
            {
                // Unsupported platform
                Debug.LogError("Unsupported platform for Cursor MCP config");
                return null;
            }
            
            // Return the path to the mcp_config.json file
            return Path.Combine(basePath, "mcp.json");
        }

        /// <summary>
        /// Gets the path to the Claude Code config file based on the current OS
        /// </summary>
        /// <returns>The path to the Claude Code config file</returns>
        private static string GetClaudeCodeConfigPath()
        {
            // Returns the absolute path to the global Claude configuration file.
            // Windows: %USERPROFILE%\.claude.json
            // macOS/Linux: $HOME/.claude.json
            if (!TryGetUserHomeDirectory("Claude Code", out string homeDir))
            {
                return null;
            }

            return Path.Combine(homeDir, ".claude.json");
        }

        /// <summary>
        /// Gets the path to the Google Antigravity MCP config file based on the current OS
        /// </summary>
        /// <returns>The path to the Google Antigravity MCP config file</returns>
        private static string GetAntigravityConfigPath()
        {
            // Base path depends on the OS
            string basePath;

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                // Windows: %USERPROFILE%/.gemini/antigravity/mcp_config.json
                basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity");
            }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            {
                // macOS: ~/Library/Application Support/.gemini/antigravity/mcp_config.json
                string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                basePath = Path.Combine(homeDir, "Library", "Application Support", ".gemini", "antigravity");
            }
            else
            {
                // Unsupported platform
                Debug.LogError("Unsupported platform for Google Antigravity MCP config");
                return null;
            }

            // Return the path to the mcp_config.json file
            return Path.Combine(basePath, "mcp_config.json");
        }

        /// <summary>
        /// Gets the path to the GitHub Copilot config file (workspace .vscode/mcp.json)
        /// </summary>
        /// <returns>The path to the GitHub Copilot config file</returns>
        private static string GetGitHubCopilotConfigPath()
        {
            // Default to current Unity project root/.vscode/mcp.json
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string vscodeDir = Path.Combine(projectRoot, ".vscode");
            return Path.Combine(vscodeDir, "mcp.json");
        }

        /// <summary>
        /// Gets the path to the OpenCode config file (opencode.json in the Unity project root).
        /// OpenCode reads its config per-project, not per-user, so this path is OS-independent.
        /// </summary>
        /// <returns>The path to the OpenCode config file</returns>
        private static string GetOpenCodeConfigPath()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, "opencode.json");
        }

        /// <summary>
        /// Adds the MCP configuration to the OpenCode config file. Preserves existing
        /// `$schema` and any unrelated entries under `mcp`, only upserting `mcp["mcp-unity"]`.
        /// </summary>
        private static bool AddToOpenCodeConfigFile(string configFilePath, bool useTabsIndentation, PathMode pathMode = PathMode.Absolute)
        {
            const string productName = "OpenCode";

            if (string.IsNullOrEmpty(configFilePath))
            {
                Debug.LogError($"{productName} config file path could not be resolved.");
                return false;
            }

            try
            {
                string mcpConfigJson = GenerateOpenCodeConfigJson(useTabsIndentation, pathMode);
                JObject mcpConfig = JObject.Parse(mcpConfigJson);
                JToken newServerEntry = mcpConfig["mcp"]?["mcp-unity"];

                if (newServerEntry == null)
                {
                    Debug.LogError($"Failed to generate {productName} configuration: missing mcp-unity entry.");
                    return false;
                }

                if (!File.Exists(configFilePath))
                {
                    File.WriteAllText(configFilePath, mcpConfigJson);
                    return true;
                }

                string existingJson = File.ReadAllText(configFilePath);
                JObject existingConfig = string.IsNullOrWhiteSpace(existingJson)
                    ? new JObject()
                    : JObject.Parse(existingJson);

                JObject mcpSection = existingConfig["mcp"] as JObject;
                if (mcpSection == null)
                {
                    mcpSection = new JObject();
                    existingConfig["mcp"] = mcpSection;
                }

                mcpSection["mcp-unity"] = newServerEntry;

                File.WriteAllText(configFilePath, existingConfig.ToString(Formatting.Indented));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to add MCP configuration to {productName}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Gets the path to the Codex CLI config file based on the current OS
        /// </summary>
        /// <returns>The path to the Codex CLI config file</returns>
        private static string GetCodexCliConfigPath()
        {
            // Codex CLI uses ~/.codex/config.toml on all platforms
            if (!TryGetUserHomeDirectory("Codex CLI", out string homeDir))
            {
                return null;
            }

            return Path.Combine(homeDir, ".codex", "config.toml");
        }

        /// <summary>
        /// Gets the path to the project-local Cursor MCP config (<ProjectRoot>/.cursor/mcp.json).
        /// </summary>
        private static string GetCursorProjectConfigPath()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, ".cursor", "mcp.json");
        }

        /// <summary>
        /// Gets the path to the project-local Claude Code MCP config (<ProjectRoot>/.mcp.json).
        /// This is the team-shared config that Claude Code reads in addition to ~/.claude.json.
        /// </summary>
        private static string GetClaudeCodeProjectConfigPath()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, ".mcp.json");
        }

        /// <summary>
        /// Gets the path to the project-local Codex CLI config (<ProjectRoot>/.codex/config.toml).
        /// Codex layers this over ~/.codex/config.toml only when the project is marked trusted.
        /// </summary>
        private static string GetCodexCliProjectConfigPath()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, ".codex", "config.toml");
        }

        /// <summary>
        /// Resolves the current user's home directory across supported Unity Editor platforms.
        /// </summary>
        private static bool TryGetUserHomeDirectory(string productName, out string homeDir)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return true;
            }

            if (Application.platform == RuntimePlatform.OSXEditor
                || Application.platform == RuntimePlatform.LinuxEditor)
            {
                homeDir = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                return true;
            }

            Debug.LogError($"Unsupported platform for {productName} config");
            homeDir = null;
            return false;
        }

        /// <summary>
        /// Common method to add MCP configuration to a TOML-based config file
        /// </summary>
        /// <param name="configFilePath">Path to the TOML config file</param>
        /// <param name="productName">Name of the product (for error messages)</param>
        /// <param name="pathMode">How to render the path to the MCP server executable</param>
        /// <returns>True if successfully added the config, false otherwise</returns>
        private static bool AddToTomlConfigFile(string configFilePath, string productName, PathMode pathMode = PathMode.Absolute)
        {
            if (string.IsNullOrEmpty(configFilePath))
            {
                Debug.LogError($"{productName} config file path not found. Please make sure {productName} is installed.");
                return false;
            }

            try
            {
                // Generate fresh MCP config TOML
                string mcpServerConfig = "\n" + GenerateMcpConfigToml(pathMode);
                
                string directoryPath = Path.GetDirectoryName(configFilePath);
                
                // Check if the config file exists
                if (File.Exists(configFilePath))
                {
                    return TryMergeMcpServersToml(configFilePath, mcpServerConfig, productName);
                }
                else if (Directory.Exists(directoryPath))
                {
                    // Create a new config file
                    File.WriteAllText(configFilePath, mcpServerConfig.TrimStart());
                    return true;
                }
                else
                {
                    // Create directory and file
                    Directory.CreateDirectory(directoryPath);
                    File.WriteAllText(configFilePath, mcpServerConfig.TrimStart());
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to add MCP configuration to {productName}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Helper to merge mcp_servers.mcp-unity section into an existing TOML config file.
        /// </summary>
        /// <param name="configFilePath">Path to the existing TOML config file</param>
        /// <param name="mcpServerConfig">The new mcp-unity TOML configuration to merge</param>
        /// <param name="productName">Name of the product (for error messages)</param>
        /// <returns>True if successfully merged, false otherwise</returns>
        private static bool TryMergeMcpServersToml(string configFilePath, string mcpServerConfig, string productName)
        {
            string existingContent = File.ReadAllText(configFilePath);
            
            // Check if mcp-unity is already configured
            if (existingContent.Contains("[mcp_servers.mcp-unity]"))
            {
                // Update existing configuration
                // Find the start of the mcp-unity section
                int startIndex = existingContent.IndexOf("[mcp_servers.mcp-unity]", StringComparison.Ordinal);
                
                // Find the end of this section (next section header or end of file)
                int endIndex = FindNextTomlSectionIndex(existingContent, startIndex + 23);
                
                string newContent = existingContent.Substring(0, startIndex) + 
                                  mcpServerConfig.TrimStart() + 
                                  existingContent.Substring(endIndex);
                File.WriteAllText(configFilePath, newContent);
            }
            else
            {
                // Append the new configuration
                File.AppendAllText(configFilePath, mcpServerConfig);
            }
            
            return true;
        }

        /// <summary>
        /// Finds the index of the next TOML section header starting from the given position.
        /// Returns the length of the content if no next section is found.
        /// </summary>
        /// <param name="content">The TOML content to search</param>
        /// <param name="startPosition">The position to start searching from</param>
        /// <returns>The index of the next section header, or content length if not found</returns>
        private static int FindNextTomlSectionIndex(string content, int startPosition)
        {
            // Look for patterns like [section] or [section.subsection]
            int nextSectionIndex = content.IndexOf("\n[", startPosition, StringComparison.Ordinal);
            
            if (nextSectionIndex == -1)
            {
                // No more sections, return end of content
                return content.Length;
            }
            
            return nextSectionIndex;
        }

        /// <summary>
        /// Returns the appropriate config JObject for merging MCP server settings,
        /// with special handling for "Claude Code":
        /// - For most products, returns the root config object.
        /// - For "Claude Code", returns the project-specific config under "projects/[serverPathParent]".
        /// Throws a MissingMemberException if the expected project entry does not exist.
        /// </summary>
        private static JObject GetMcpServersConfig(JObject existingConfig, string productName)
        {
            // For most products, use the root config object.
            if (productName != "Claude Code")
            {
                return existingConfig;
            }

            // For Claude Code, use the project-specific config.
            if (existingConfig["projects"] == null)
            {
                throw new MissingMemberException("Claude Code config error: Could not find 'projects' entry in existing config.");
            }

            string serverPath = GetServerPath();
            string serverPathParent = Path.GetDirectoryName(serverPath)?.Replace("\\", "/");
            var projectConfig = existingConfig["projects"][serverPathParent];

            if (projectConfig == null)
            {
                throw new MissingMemberException(
                    $"Claude Code config error: Could not find project entry for parent directory '{serverPathParent}' in existing config."
                );
            }

            return (JObject)projectConfig;
        }

        /// <summary>
        /// Helper to merge mcpServers from mcpConfig into the existing config file.
        /// </summary>
        private static bool TryMergeMcpServers(string configFilePath, JObject mcpConfig, string productName)
        {
            // Read the existing config
            string existingConfigJson = File.ReadAllText(configFilePath);
            JObject existingConfig = string.IsNullOrEmpty(existingConfigJson) ? new JObject() : JObject.Parse(existingConfigJson);
            JObject mcpServersConfig = GetMcpServersConfig(existingConfig, productName);

            // Merge the mcpServers from our config into the existing config
            if (mcpConfig["mcpServers"] != null && mcpConfig["mcpServers"] is JObject mcpServers)
            {
                // Create mcpServers object if it doesn't exist
                if (mcpServersConfig["mcpServers"] == null)
                {
                    mcpServersConfig["mcpServers"] = new JObject();
                }

                // Add or update the mcp-unity server config
                if (mcpServers["mcp-unity"] != null)
                {
                    ((JObject)mcpServersConfig["mcpServers"])["mcp-unity"] = mcpServers["mcp-unity"];
                }

                // Write the updated config back to the file
                File.WriteAllText(configFilePath, existingConfig.ToString(Formatting.Indented));
                return true;
            }

            return false;
        }

        /// <summary>
        /// Detects if the current Unity Editor instance is a Multiplayer Play Mode clone (additional editor).
        /// Uses multiple detection methods in order of reliability:
        /// 1. Command line arguments (-name Player2/3/4 indicates clone)
        /// 2. Reflection on CurrentPlayer.IsMainEditor property
        /// 3. Library path heuristics
        /// Returns false if not a clone or detection fails (allowing normal operation).
        /// </summary>
        /// <returns>True if running as a clone instance, false if main editor or detection fails</returns>
        public static bool IsMultiplayerPlayModeClone()
        {
            // Return cached result if available
            if (_isMultiplayerPlayModeClone.HasValue)
            {
                return _isMultiplayerPlayModeClone.Value;
            }

            try
            {
                // Method 1: Check command line arguments (most reliable)
                // Unity MPPM passes "-name PlayerX" where X > 1 for clones
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "-name" || args[i] == "--name")
                    {
                        string playerName = args[i + 1];
                        // Player1 is the main editor, Player2/3/4 are clones
                        if (playerName.StartsWith("Player") && playerName != "Player1")
                        {
                            _isMultiplayerPlayModeClone = true;
                            return true;
                        }
                        // Found -name argument but it's Player1 (main editor)
                        if (playerName == "Player1")
                        {
                            _isMultiplayerPlayModeClone = false;
                            return false;
                        }
                    }
                }

                // Method 2: Check for MPPM-specific command line flags
                foreach (string arg in args)
                {
                    // Check for clone-specific flags that Unity might pass
                    if (arg.Contains("mppm") && arg.Contains("clone"))
                    {
                        _isMultiplayerPlayModeClone = true;
                        return true;
                    }
                }

                // Method 3: Try reflection on CurrentPlayer.IsMainEditor (MPPM 1.4+)
                Assembly mppmAssembly = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string assemblyName = assembly.GetName().Name;
                    if (assemblyName == "Unity.Multiplayer.Playmode" || 
                        assemblyName == "Unity.Multiplayer.Playmode.Editor")
                    {
                        mppmAssembly = assembly;
                        break;
                    }
                }

                if (mppmAssembly != null)
                {
                    // Try to find CurrentPlayer class
                    Type currentPlayerType = mppmAssembly.GetType("Unity.Multiplayer.Playmode.CurrentPlayer");
                    if (currentPlayerType != null)
                    {
                        // Try IsMainEditor property
                        PropertyInfo isMainEditorProperty = currentPlayerType.GetProperty(
                            "IsMainEditor", 
                            BindingFlags.Public | BindingFlags.Static);
                        
                        if (isMainEditorProperty != null)
                        {
                            bool isMainEditor = (bool)isMainEditorProperty.GetValue(null);
                            _isMultiplayerPlayModeClone = !isMainEditor;
                            return !isMainEditor;
                        }
                    }
                }

                // Method 4: Check if Unity's Library path indicates a VP (Virtual Player) subfolder
                // Clone instances may use a modified library path
                string libraryPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library"));
                if (IsVirtualPlayerLibraryPath(libraryPath))
                {
                    // Looks like we're in a virtual player's library folder
                    _isMultiplayerPlayModeClone = true;
                    return true;
                }

                // Default: not a clone (or couldn't detect MPPM)
                _isMultiplayerPlayModeClone = false;
                return false;
            }
            catch (Exception ex)
            {
                // On any error, assume not a clone to avoid breaking functionality
                Debug.LogWarning($"[MCP Unity] Error detecting Multiplayer Play Mode clone status: {ex.Message}");
                _isMultiplayerPlayModeClone = false;
                return false;
            }
        }

        /// <summary>
        /// Returns true when the path contains a "Library" segment followed by a "VP" segment.
        /// This avoids false positives from names like "MVP" or "CountyLibraryApp".
        /// </summary>
        private static bool IsVirtualPlayerLibraryPath(string libraryPath)
        {
            if (string.IsNullOrEmpty(libraryPath))
            {
                return false;
            }

            string normalizedPath = libraryPath.Replace('\\', '/');
            string[] segments = normalizedPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            int libraryIndex = -1;
            for (int i = 0; i < segments.Length; i++)
            {
                if (string.Equals(segments[i], "Library", StringComparison.OrdinalIgnoreCase))
                {
                    libraryIndex = i;
                    break;
                }
            }

            if (libraryIndex < 0)
            {
                return false;
            }

            for (int i = libraryIndex + 1; i < segments.Length; i++)
            {
                if (string.Equals(segments[i], "VP", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Resets the cached Multiplayer Play Mode clone detection result.
        /// Useful for testing or when the state might have changed.
        /// </summary>
        public static void ResetMultiplayerPlayModeCloneCache()
        {
            _isMultiplayerPlayModeClone = null;
        }
    }
}
