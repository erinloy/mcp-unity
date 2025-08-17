using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace McpUnity.DirectMcp
{
    /// <summary>
    /// Standalone launcher for Unity MCP Server
    /// Compile with: csc UnityMcpLauncher.cs -out:unity-mcp.exe
    /// </summary>
    public class UnityMcpLauncher
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: unity-mcp.exe <project-path>");
                return 1;
            }

            string projectPath = args[0];
            
            // Validate project path
            if (!Directory.Exists(projectPath))
            {
                Console.Error.WriteLine($"Project path not found: {projectPath}");
                return 1;
            }

            // Find Unity installation
            string unityExe = FindUnityExecutable();
            if (string.IsNullOrEmpty(unityExe))
            {
                Console.Error.WriteLine("Unity installation not found");
                return 1;
            }

            // Build arguments
            var processArgs = string.Join(" ", new[]
            {
                "-batchmode",
                "-nographics",
                $"-projectPath \"{projectPath}\"",
                "-executeMethod McpUnity.DirectMcp.UnityMcpServer.RunStdioServer",
                "-logFile -"  // Log to stdout
            });

            // Start Unity process
            var startInfo = new ProcessStartInfo
            {
                FileName = unityExe,
                Arguments = processArgs,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    // Create threads to relay stdio
                    var stdinThread = new Thread(() => RelayStream(Console.OpenStandardInput(), process.StandardInput.BaseStream))
                    {
                        IsBackground = true
                    };
                    stdinThread.Start();

                    var stdoutThread = new Thread(() => RelayStream(process.StandardOutput.BaseStream, Console.OpenStandardOutput()))
                    {
                        IsBackground = true
                    };
                    stdoutThread.Start();

                    var stderrThread = new Thread(() => RelayStream(process.StandardError.BaseStream, Console.OpenStandardError()))
                    {
                        IsBackground = true
                    };
                    stderrThread.Start();

                    // Wait for Unity to exit
                    process.WaitForExit();
                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to start Unity: {ex.Message}");
                return 1;
            }
        }

        static string FindUnityExecutable()
        {
            var searchPaths = new[]
            {
                @"C:\Program Files\Unity\Hub\Editor",
                @"C:\Program Files\Unity\Editor",
                @"C:\Unity\Editor"
            };

            foreach (var basePath in searchPaths)
            {
                if (!Directory.Exists(basePath))
                    continue;

                // Look for Unity.exe in version folders
                var unityExe = Directory.GetDirectories(basePath, "*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(d => d) // Latest version first
                    .Select(d => Path.Combine(d, "Editor", "Unity.exe"))
                    .FirstOrDefault(File.Exists);

                if (!string.IsNullOrEmpty(unityExe))
                    return unityExe;

                // Check direct path
                var directPath = Path.Combine(basePath, "Unity.exe");
                if (File.Exists(directPath))
                    return directPath;
            }

            // Check PATH environment variable
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var path in pathEnv.Split(';'))
                {
                    var unityPath = Path.Combine(path, "Unity.exe");
                    if (File.Exists(unityPath))
                        return unityPath;
                }
            }

            return null;
        }

        static void RelayStream(Stream input, Stream output)
        {
            try
            {
                byte[] buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, bytesRead);
                    output.Flush();
                }
            }
            catch
            {
                // Stream closed, exit gracefully
            }
        }
    }
}