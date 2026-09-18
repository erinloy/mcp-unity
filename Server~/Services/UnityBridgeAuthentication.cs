using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace McpUnity.DirectMcp.Services
{
    /// <summary>
    /// Resolves the per-project secret the Unity Editor requires on the WebSocket handshake
    /// (HTTP Basic, user "mcp-unity", password = token). The Editor creates the token at
    /// Library/McpUnity/bridge-token when its server starts.
    ///
    /// Resolution order (fails closed - an explicitly configured source never falls back):
    ///   1. MCP_UNITY_AUTH_TOKEN
    ///   2. the file named by MCP_UNITY_AUTH_TOKEN_PATH
    ///   3. Library/McpUnity/bridge-token beside the located Unity project
    /// </summary>
    public static class UnityBridgeAuthentication
    {
        public const string Username = "mcp-unity";
        public const string TokenEnvironmentVariable = "MCP_UNITY_AUTH_TOKEN";
        public const string TokenPathEnvironmentVariable = "MCP_UNITY_AUTH_TOKEN_PATH";

        private static readonly Regex TokenPattern = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

        /// <summary>
        /// Returns the token, or throws <see cref="UnityBridgeAuthenticationException"/> describing
        /// exactly which source was used and why it is unusable.
        /// </summary>
        public static string ResolveToken(string unityProjectPath)
        {
            var explicitToken = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
            if (explicitToken != null)
            {
                var trimmed = explicitToken.Trim();
                if (!IsValidToken(trimmed))
                {
                    throw new UnityBridgeAuthenticationException(
                        $"{TokenEnvironmentVariable} is set but is not a 64-character hexadecimal token.");
                }
                return trimmed;
            }

            var explicitPath = Environment.GetEnvironmentVariable(TokenPathEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return ReadTokenFile(explicitPath, $"{TokenPathEnvironmentVariable} ({explicitPath})");
            }

            var defaultPath = GetDefaultTokenPath(unityProjectPath);
            return ReadTokenFile(defaultPath, $"project token file ({defaultPath})");
        }

        public static string GetDefaultTokenPath(string unityProjectPath)
        {
            return Path.Combine(unityProjectPath, "Library", "McpUnity", "bridge-token");
        }

        /// <summary>
        /// Value for the HTTP Authorization header.
        /// </summary>
        public static string CreateAuthorizationHeader(string token)
        {
            var credentials = Encoding.UTF8.GetBytes($"{Username}:{token}");
            return "Basic " + Convert.ToBase64String(credentials);
        }

        public static bool IsValidToken(string? token)
        {
            return token != null && TokenPattern.IsMatch(token);
        }

        private static string ReadTokenFile(string path, string sourceDescription)
        {
            if (!File.Exists(path))
            {
                throw new UnityBridgeAuthenticationException(
                    $"Authentication token not found at {sourceDescription}. The Unity Editor creates it when the MCP Unity server starts.");
            }

            var token = File.ReadAllText(path).Trim();
            if (!IsValidToken(token))
            {
                throw new UnityBridgeAuthenticationException(
                    $"Authentication token at {sourceDescription} is malformed. Regenerate it from Tools > MCP Unity > Server Window.");
            }

            return token;
        }
    }

    /// <summary>
    /// The bridge token is missing, malformed, or was rejected by the Unity Editor.
    /// </summary>
    public sealed class UnityBridgeAuthenticationException : Exception
    {
        public UnityBridgeAuthenticationException(string message) : base(message)
        {
        }
    }
}
