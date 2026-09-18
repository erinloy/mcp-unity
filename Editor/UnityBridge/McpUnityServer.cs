using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using McpUnity.Tools;
using McpUnity.Resources;
using McpUnity.Services;
using McpUnity.Utils;
using WebSocketSharp.Net;
using WebSocketSharp.Server;
using System.IO;
using System.Net.Sockets;
using UnityEditor.Callbacks;

namespace McpUnity.Unity
{
    /// <summary>
    /// Custom WebSocket close codes for Unity-specific events.
    /// Range 4000-4999 is reserved for application use.
    /// </summary>
    public static class UnityCloseCode
    {
        /// <summary>
        /// Unity is entering Play mode - clients should use fast polling instead of backoff
        /// </summary>
        public const ushort PlayMode = 4001;
    }

    /// <summary>
    /// MCP Unity Server: the Unity-side WebSocket endpoint that the C# MCP server (Server~) connects to.
    /// </summary>
    [InitializeOnLoad]
    public class McpUnityServer : IDisposable
    {
        private static McpUnityServer _instance;

        private const string ServicePath = "/McpUnity";

        private readonly Dictionary<string, McpToolBase> _tools = new Dictionary<string, McpToolBase>();
        private readonly Dictionary<string, McpResourceBase> _resources = new Dictionary<string, McpResourceBase>();

        private const int DelayedStartMaxAttempts = 10;
        private static readonly double[] DelayedStartRetryDelaySeconds = { 0.25d, 0.5d, 1d, 2d, 3d, 5d };
        private const string AllowBatchModeEnvironmentVariable = "MCP_UNITY_ALLOW_BATCH_MODE";

        // Connect + read budget for the post-start liveness probe. Generous enough not to trip on a
        // busy editor, short enough that the probe thread is always gone long before the next start.
        private const int LivenessProbeTimeoutMs = 2000;

        // Periodic auto-heal health checks (fork feature): detect a server that reports listening but no
        // longer accepts connections, and restart it.
        private const float HealthCheckIntervalSeconds = 5f;
        private const float HealthCheckGracePeriodSeconds = 3f;
        private const int HealthFailureThreshold = 2;

        private WebSocketServer _webSocketServer;
        private CancellationTokenSource _cts;
        private TestRunnerService _testRunnerService;
        private ConsoleLogsService _consoleLogsService;
        private bool _delayedStartScheduled;
        private bool _delayedStartRequiresAutoStart;
        private int _delayedStartAttempt;
        private double _delayedStartEarliestTime;
        private string _delayedStartReason;
        private int _connectionGeneration;
        private int _activeConnectionGeneration;

        // Cancelled by StopServer() so a liveness probe can never outlive the server generation
        // it was started for. Deliberately never Disposed: the probe thread may still poll the
        // token after replacement, a CTS without a timer holds no critical resources, and churn
        // is bounded at one per server start.
        private CancellationTokenSource _livenessProbeCancellation;

        // Auto-heal state
        private DateTime _lastConnectionTime = DateTime.MinValue;
        private DateTime _serverStartTime = DateTime.MinValue;
        private double _lastHealthCheckTime;
        private int _consecutiveHealthFailures;
        private bool _autoHealingEnabled = true;

        // Set when a user explicitly stops the server so auto-heal does not immediately restart it.
        private bool _stoppedByUser;

        private enum StartServerResult
        {
            Started,
            AlreadyListening,
            Skipped,
            AddressAlreadyInUse,
            Failed
        }

        /// <summary>
        /// Static constructor that gets called when Unity loads due to InitializeOnLoad attribute.
        /// Creating the instance registers the lifecycle hooks and schedules the auto-start.
        /// </summary>
        static McpUnityServer()
        {
            EditorApplication.delayCall += () =>
            {
                var currentInstance = Instance;
            };
        }

        /// <summary>
        /// Singleton instance accessor. Returns null when batch mode has not explicitly enabled the server.
        /// </summary>
        public static McpUnityServer Instance
        {
            get
            {
                if (!ShouldRunInCurrentProcess())
                {
                    return null;
                }

                if (_instance == null)
                {
                    _instance = new McpUnityServer();
                }
                return _instance;
            }
        }

        /// <summary>
        /// Current Listening state
        /// </summary>
        public bool IsListening => _webSocketServer?.IsListening ?? false;

        /// <summary>
        /// True when a delayed start or retry is waiting for Unity/editor socket cleanup.
        /// </summary>
        public bool HasScheduledStart => _delayedStartScheduled;

        /// <summary>
        /// Human-readable status for scheduled restart attempts.
        /// </summary>
        public string ScheduledStartStatus
        {
            get
            {
                if (!_delayedStartScheduled)
                {
                    return string.Empty;
                }

                return $"Retrying port {McpUnitySettings.Instance.Port} (attempt {_delayedStartAttempt}/{DelayedStartMaxAttempts})";
            }
        }

        /// <summary>
        /// Time of the last successful client connection (for health monitoring)
        /// </summary>
        public DateTime LastConnectionTime => _lastConnectionTime;

        /// <summary>
        /// Thread-safe dictionary of connected clients with this server.
        /// WebSocketSharp dispatches OnOpen/OnClose on thread pool threads,
        /// so concurrent access must be safe.
        /// </summary>
        public ConcurrentDictionary<string, string> Clients { get; } = new ConcurrentDictionary<string, string>();

        /// <summary>
        /// Disposes the McpUnityServer instance, stopping the WebSocket server and unsubscribing from Unity Editor events.
        /// This method ensures proper cleanup of resources and prevents memory leaks or unexpected behavior during domain reloads or editor shutdown.
        /// </summary>
        public void Dispose()
        {
            StopServerInternal(null, null);

            EditorApplication.quitting -= OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.update -= OnEditorUpdate;

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Start the WebSocket server. Attempts to bind immediately; if the port is still held
        /// (e.g. by a server that is shutting down) the start is retried on a bounded backoff
        /// schedule driven by the editor loop, so the main thread is never blocked.
        /// </summary>
        public void StartServer()
        {
            _stoppedByUser = false;
            CancelScheduledStart();

            StartServerResult result = StartServerInternal(false, 1, GetDelayedStartDelaySeconds(2));
            if (result == StartServerResult.AddressAlreadyInUse)
            {
                ScheduleStartServer(requireAutoStart: false, reason: "port still in use", attempt: 2);
            }
        }

        /// <summary>
        /// Stop the current server and start it again after Unity has had a chance to release the socket.
        /// </summary>
        public void RestartServer()
        {
            _stoppedByUser = false;
            StopServerInternal(null, null);
            ScheduleStartServer(requireAutoStart: false, reason: "manual restart");
        }

        /// <summary>
        /// Stop the WebSocket server. Auto-heal will not restart a server stopped through this method
        /// until <see cref="StartServer"/> or <see cref="RestartServer"/> is called.
        /// </summary>
        /// <param name="closeCode">Optional custom close code to send to clients before stopping</param>
        /// <param name="closeReason">Optional reason message for the close</param>
        public void StopServer(ushort? closeCode = null, string closeReason = null)
        {
            _stoppedByUser = true;
            StopServerInternal(closeCode, closeReason);
        }

        /// <summary>
        /// Force restart the server, bypassing IsListening checks.
        /// Use when the server is in a zombie state (port bound but not accepting connections).
        /// The old instance is torn down unconditionally; the new start retries on the backoff
        /// schedule until the port is released.
        /// </summary>
        public void ForceRestartServer()
        {
            McpLogger.LogWarning("[ForceRestart] Beginning forced server restart...");

            _consecutiveHealthFailures = 0;

            try { ServiceDiscovery.UnregisterService(); }
            catch (Exception ex) { McpLogger.LogWarning($"[ForceRestart] Error unregistering service discovery entry: {ex.Message}"); }

            StopServerInternal(null, "Force restart");
            StartServer();

            if (IsListening)
            {
                McpLogger.LogInfo($"[ForceRestart] Server restarted on port {McpUnitySettings.Instance.Port}");
            }
            else if (HasScheduledStart)
            {
                McpLogger.LogWarning($"[ForceRestart] Port {McpUnitySettings.Instance.Port} not yet released; {ScheduledStartStatus}");
            }
            else
            {
                McpLogger.LogError("[ForceRestart] Server failed to restart");
            }
        }

        /// <summary>
        /// Called when a client successfully connects. Updates health tracking.
        /// </summary>
        public void OnClientConnected(string clientId)
        {
            _lastConnectionTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Performs a health check on the server. Returns true if healthy, false if needs restart.
        /// </summary>
        public bool PerformHealthCheck()
        {
            if (!IsListening)
            {
                return false;
            }

            try
            {
                return TryConnectToServerPort();
            }
            catch (Exception ex)
            {
                McpLogger.LogWarning($"Health check failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Try to get a tool by name
        /// </summary>
        public bool TryGetTool(string name, out McpToolBase tool)
        {
            return _tools.TryGetValue(name, out tool);
        }

        /// <summary>
        /// Try to get a resource by name
        /// </summary>
        public bool TryGetResource(string name, out McpResourceBase resource)
        {
            return _resources.TryGetValue(name, out resource);
        }

        /// <summary>
        /// Try to get a resource by its exact URI (templated URIs are matched by the socket handler)
        /// </summary>
        public bool TryGetResourceByUri(string uri, out McpResourceBase resource)
        {
            foreach (var candidate in _resources.Values)
            {
                if (string.Equals(candidate.Uri, uri, StringComparison.Ordinal))
                {
                    resource = candidate;
                    return true;
                }
            }

            resource = null;
            return false;
        }

        /// <summary>
        /// Get all registered tools for discovery, keyed by tool name
        /// </summary>
        public Dictionary<string, McpToolBase> GetTools()
        {
            return new Dictionary<string, McpToolBase>(_tools);
        }

        /// <summary>
        /// Get all registered resources for discovery, keyed by resource name
        /// </summary>
        public Dictionary<string, McpResourceBase> GetResources()
        {
            return new Dictionary<string, McpResourceBase>(_resources);
        }

        /// <summary>
        /// Verifies the MCP C# server executable is available and builds it if necessary.
        /// </summary>
        public void InstallServer()
        {
            string projectRoot = McpUtils.GetUnityProjectRoot();
            string exePath = McpUtils.GetServerExecutablePath();

            if (File.Exists(exePath))
            {
                McpLogger.LogInfo($"Unity MCP C# server found at: {exePath}");
                return;
            }

            McpLogger.LogWarning($"Unity MCP executable not found at: {exePath}. Attempting to build...");

            string serverPath = McpUtils.GetServerPath();
            if (!McpUtils.ValidateServerPath(serverPath))
            {
                McpLogger.LogError("Server path validation failed. Cannot build the MCP server. See previous errors for details.");
                return;
            }

            string projectPath = Path.Combine(serverPath, McpUtils.ServerProjectFileName);
            BuildMcpServer(serverPath, projectPath);

            if (File.Exists(exePath))
            {
                McpLogger.LogInfo($"Unity MCP C# server successfully built at: {exePath}");
            }
            else
            {
                McpLogger.LogError($"Failed to build Unity MCP server or copy to: {exePath}");
            }
        }

        /// <summary>
        /// Builds the MCP server using the dotnet CLI. The project's post-build step deploys the
        /// executable to &lt;ProjectRoot&gt;/Tools/unity-mcp/unity-mcp.exe.
        /// </summary>
        private void BuildMcpServer(string serverPath, string projectPath)
        {
            try
            {
                McpLogger.LogInfo("Building Unity MCP server...");

                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"publish \"{projectPath}\" -c Release -r win-x64 --self-contained",
                    WorkingDirectory = serverPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(processInfo))
                {
                    if (process == null)
                    {
                        McpLogger.LogError("Failed to start the dotnet process to build the Unity MCP server.");
                        return;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        McpLogger.LogInfo("Unity MCP server build completed successfully.");
                        if (!string.IsNullOrEmpty(output))
                        {
                            McpLogger.LogInfo($"Build output: {output}");
                        }
                    }
                    else
                    {
                        McpLogger.LogError($"Unity MCP server build failed with exit code: {process.ExitCode}");
                        if (!string.IsNullOrEmpty(error))
                        {
                            McpLogger.LogError($"Build error: {error}");
                        }
                        if (!string.IsNullOrEmpty(output))
                        {
                            McpLogger.LogError($"Build output: {output}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Exception while building Unity MCP server: {ex.Message}. Make sure the .NET SDK is installed and available in PATH.");
            }
        }

        internal bool ShouldTrackClient(int connectionGeneration)
        {
            return connectionGeneration == _activeConnectionGeneration && IsListening;
        }

        private static bool ShouldRunInCurrentProcess()
        {
            if (!Application.isBatchMode)
            {
                return true;
            }

            string batchModeOverride = Environment.GetEnvironmentVariable(AllowBatchModeEnvironmentVariable);
            if (IsEnabledEnvironmentFlag(batchModeOverride))
            {
                return true;
            }

            // Do not instantiate settings in a default batch-mode build: loading settings creates
            // the file when absent, which would reintroduce initialization side effects in CI.
            if (!McpUnitySettings.HasPersistedSettings)
            {
                return false;
            }

            return ShouldRunInCurrentProcess(
                true,
                McpUnitySettings.Instance.AllowBatchModeServer,
                batchModeOverride);
        }

        /// <summary>
        /// Determines whether the bridge is allowed to run in the current Unity process.
        /// Batch mode remains disabled by default so cloud builds and CI keep their existing behavior.
        /// A persistent headless host can opt in via project settings or MCP_UNITY_ALLOW_BATCH_MODE=true.
        /// </summary>
        private static bool ShouldRunInCurrentProcess(bool isBatchMode, bool allowBatchModeServer, string batchModeOverride)
        {
            if (!isBatchMode)
            {
                return true;
            }

            return allowBatchModeServer || IsEnabledEnvironmentFlag(batchModeOverride);
        }

        private static bool IsEnabledEnvironmentFlag(string value)
        {
            return string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value?.Trim(), "1", StringComparison.Ordinal);
        }

        /// <summary>
        /// Private constructor to enforce singleton pattern
        /// </summary>
        private McpUnityServer()
        {
            if (!ShouldRunInCurrentProcess())
            {
                McpLogger.LogInfo("MCP Unity server disabled: Running in batch mode (Unity Cloud Build or CI)");
                return;
            }

            EditorApplication.quitting -= OnEditorQuitting; // Prevent multiple subscriptions on domain reload
            EditorApplication.quitting += OnEditorQuitting;

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            // Periodic health checks / auto-heal
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;

            // Building the C# server (dotnet publish) is what would make batch-mode builds hang. A
            // headless MCP host only needs the Unity WebSocket server, so it must provide a prebuilt
            // server executable itself.
            if (!Application.isBatchMode)
            {
                InstallServer();
            }
            else
            {
                McpLogger.LogInfo("Skipping MCP C# server build in batch mode.");
            }
            InitializeServices();
            RegisterResources();
            RegisterTools();

            // Initial start if auto-start is enabled
            if (McpUnitySettings.Instance.AutoStartServer)
            {
                ScheduleStartServer(requireAutoStart: true, reason: "auto-start");
            }
        }

        private StartServerResult StartServerInternal(bool logAddressInUseAsError, int attempt = 1, double nextRetryDelaySeconds = 0)
        {
            // Skip starting server if this is a Multiplayer Play Mode clone instance
            // Only the main editor should run the WebSocket server to avoid port conflicts
            if (McpUtils.IsMultiplayerPlayModeClone())
            {
                McpLogger.LogInfo("Server startup skipped: Running as Multiplayer Play Mode clone instance. Only the main editor runs the MCP server.");
                return StartServerResult.Skipped;
            }

            if (IsListening)
            {
                McpLogger.LogInfo($"Server start requested, but already listening on port {McpUnitySettings.Instance.Port}.");
                return StartServerResult.AlreadyListening;
            }

            if (_webSocketServer != null)
            {
                StopServerInternal(null, null);
            }

            WebSocketServer webSocketServer = null;
            try
            {
                string authenticationToken = McpUnityAuthentication.GetOrCreateToken();
                int connectionGeneration = Interlocked.Increment(ref _connectionGeneration);
                // Bind the IPv4 loopback explicitly: "localhost" can resolve to an IPv6-only (::1)
                // listener, which the C# MCP server (connecting to 127.0.0.1) cannot reach.
                var host = McpUnitySettings.Instance.AllowRemoteConnections ? "0.0.0.0" : "127.0.0.1";
                webSocketServer = new WebSocketServer($"ws://{host}:{McpUnitySettings.Instance.Port}");
                ConfigureWebSocketSecurity(webSocketServer, authenticationToken);
                webSocketServer.Log.Level = WebSocketSharp.LogLevel.Debug;
                webSocketServer.Log.Output = (data, path) =>
                {
                    // Filter out benign connection errors (stale connections, health-check probes,
                    // malformed requests). Everything else goes to the info log.
                    var message = data.Message;
                    if (message != null && (
                        message.Contains("EndOfStreamException") ||
                        message.Contains("The header cannot be read from the data source") ||
                        message.Contains("An exception has occurred while reading an HTTP request/response")
                    ))
                    {
                        return;
                    }
                    McpLogger.LogInfo($"[WebSocketSharp] {message}");
                };
                webSocketServer.AddWebSocketService(ServicePath, () => new McpUnitySocketHandler(this, connectionGeneration));
                webSocketServer.Start();
                _webSocketServer = webSocketServer;
                _activeConnectionGeneration = connectionGeneration;
                _serverStartTime = DateTime.UtcNow;
                _consecutiveHealthFailures = 0;
                McpBackgroundTick.Start();
                McpLogger.LogInfo($"WebSocket server started successfully on ws://{host}:{McpUnitySettings.Instance.Port}{ServicePath}.");

                // Register with service discovery. A registry write failure must not take down a
                // server that is already listening, so it is reported on its own.
                try
                {
                    ServiceDiscovery.RegisterService(McpUnitySettings.Instance.Port);
                }
                catch (Exception discoveryException)
                {
                    McpLogger.LogError($"Service discovery registration failed: {discoveryException.Message}");
                }

                // Start() succeeding only proves the bind succeeded, not that the port is being
                // serviced. Confirm the latter out-of-band (see the method's remarks). The probe
                // carries this start's generation and a token cancelled on StopServer(), so a
                // probe from a replaced server can never report against its successor.
                _livenessProbeCancellation?.Cancel();
                _livenessProbeCancellation = new CancellationTokenSource();
                VerifyServerIsActuallyServing(host, McpUnitySettings.Instance.Port,
                    connectionGeneration, _livenessProbeCancellation.Token);

                return StartServerResult.Started;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                CleanupFailedStart(webSocketServer);
                string message = $"Failed to start WebSocket server: Port {McpUnitySettings.Instance.Port} is already in use. {ex.Message}";
                if (logAddressInUseAsError)
                {
                    McpLogger.LogError(message);
                }
                else
                {
                    McpLogger.LogWarning($"{message} Attempt {attempt}/{DelayedStartMaxAttempts}; retrying in {nextRetryDelaySeconds:0.##}s.");
                }

                return StartServerResult.AddressAlreadyInUse;
            }
            catch (Exception ex)
            {
                CleanupFailedStart(webSocketServer);
                McpLogger.LogError($"Failed to start WebSocket server: {ex.Message}\n{ex.StackTrace}");
                return StartServerResult.Failed;
            }
        }

        private static void ConfigureWebSocketSecurity(WebSocketServer webSocketServer, string authenticationToken)
        {
            if (webSocketServer == null)
            {
                throw new ArgumentNullException(nameof(webSocketServer));
            }

            if (!McpUnityAuthentication.IsValidToken(authenticationToken))
            {
                throw new InvalidDataException("Cannot start the MCP WebSocket server with an invalid authentication token.");
            }

            webSocketServer.AuthenticationSchemes = AuthenticationSchemes.Basic;
            webSocketServer.Realm = McpUnityAuthentication.Realm;
            webSocketServer.UserCredentialsFinder = identity =>
                identity != null && string.Equals(identity.Name, McpUnityAuthentication.Username, StringComparison.Ordinal)
                    ? new NetworkCredential(McpUnityAuthentication.Username, authenticationToken)
                    : null;
        }

        private static double GetDelayedStartDelaySeconds(int attempt)
        {
            int normalizedAttempt = Math.Max(attempt, 1);
            int delayIndex = Math.Min(normalizedAttempt - 1, DelayedStartRetryDelaySeconds.Length - 1);
            return DelayedStartRetryDelaySeconds[delayIndex];
        }

        /// <summary>
        /// Confirms out-of-band that the port just bound is actually being serviced.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A successful <c>WebSocketServer.Start()</c> proves only that the bind succeeded. On
        /// Windows it is possible to end up bound to a port that then accepts TCP connections and
        /// never answers them, so the editor reports a healthy server while every client hangs.
        /// Reported as issue #141 ("Port N is already in use", cleared only by restarting Unity).
        /// </para>
        /// <para>
        /// Silence with the connection held open is the distinguishing signature: a healthy server
        /// either answers or closes the socket. So "data received" AND "peer closed" both count as
        /// alive, and only a read TIMEOUT counts as dead - <see cref="IOException"/> alone does
        /// not: <c>NetworkStream.Read</c> also raises it for connection resets and aborted
        /// sockets (an ordinary shutdown/restart race), so the inner
        /// <see cref="SocketException"/> is inspected and only <see cref="SocketError.TimedOut"/>
        /// triggers the dead-server report. Other socket failures stay silent, matching the
        /// no-connect case below: ambiguous evidence never raises the severe diagnostic.
        /// </para>
        /// <para>
        /// The probe is tied to the server generation that launched it: it carries that
        /// generation plus a token cancelled by <c>StopServer()</c>, and re-checks both before
        /// every side effect - so a probe that slept or blocked in <c>Read</c> across a server
        /// replacement exits silently instead of reporting a stale verdict against a healthy
        /// successor.
        /// </para>
        /// <para>
        /// This only DETECTS and reports the condition; recovery is left to the auto-heal loop and
        /// the Force Restart command. The message therefore describes what was observed rather
        /// than asserting a cause.
        /// </para>
        /// <para>
        /// Every resolved address must be tried. Binding "localhost" can yield an IPv6-only
        /// listener (::1), against which 127.0.0.1 is refused - so a default <c>new TcpClient()</c>
        /// (which is IPv4) cannot reach a perfectly healthy server and would false-alarm.
        /// </para>
        /// <para>
        /// Runs off the main thread, and uses a deliberately non-existent path so it can never
        /// create a /McpUnity session. Only LogWarning/LogError are called from the worker.
        /// </para>
        /// </remarks>
        private void VerifyServerIsActuallyServing(string host, int port, int connectionGeneration,
            CancellationToken cancellation)
        {
            var probeHost = host == "0.0.0.0" ? "127.0.0.1" : host;

            // True while the server generation this probe was launched for is still the active
            // one and no stop has been requested. Checked before every side effect: a stale
            // probe must exit silently, never report against a replacement server.
            bool ProbeIsCurrent() =>
                !cancellation.IsCancellationRequested &&
                Volatile.Read(ref _activeConnectionGeneration) == connectionGeneration;

            var probe = new Thread(() =>
            {
                try
                {
                    // Let the accept loop settle; Start() can return marginally early. The wait
                    // doubles as the cancellation point for a stop during the settle window.
                    if (cancellation.WaitHandle.WaitOne(250))
                    {
                        return;
                    }

                    System.Net.IPAddress[] addresses;
                    try
                    {
                        addresses = System.Net.Dns.GetHostAddresses(probeHost);
                    }
                    catch
                    {
                        return;
                    }

                    foreach (var address in addresses)
                    {
                        if (!ProbeIsCurrent())
                        {
                            return;
                        }

                        using (var client = new TcpClient(address.AddressFamily))
                        {
                            try
                            {
                                if (!client.ConnectAsync(address, port).Wait(LivenessProbeTimeoutMs, cancellation))
                                {
                                    continue;
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                return;
                            }
                            catch
                            {
                                // Refused/unreachable on this family - try the next address.
                                continue;
                            }

                            var stream = client.GetStream();
                            stream.ReadTimeout = LivenessProbeTimeoutMs;

                            // A healthy websocket-sharp answers an unknown path with an HTTP error
                            // (or an auth challenge) and closes, which proves the accept loop is live
                            // without ever creating a /McpUnity session.
                            var request = System.Text.Encoding.ASCII.GetBytes(
                                "GET /__mcp_liveness__ HTTP/1.1\r\n" +
                                $"Host: {probeHost}:{port}\r\n" +
                                "Upgrade: websocket\r\n" +
                                "Connection: Upgrade\r\n" +
                                "Sec-WebSocket-Key: AAAAAAAAAAAAAAAAAAAAAA==\r\n" +
                                "Sec-WebSocket-Version: 13\r\n\r\n");
                            stream.Write(request, 0, request.Length);

                            var buffer = new byte[64];
                            try
                            {
                                // Returns >0 (answered) or 0 (peer closed) on a live server. A
                                // throw is NOT proof of death by itself - see the catch below.
                                stream.Read(buffer, 0, buffer.Length);
                            }
                            catch (IOException ex)
                            {
                                // Read() raises IOException for more than the timeout: connection
                                // resets and aborted sockets surface here too, and those are the
                                // signature of an ordinary shutdown/restart race, not of the
                                // silent port (which holds the connection OPEN in silence). Only
                                // the timeout code is the dead-server signal; everything else
                                // stays silent, like the no-connect case below.
                                var socketError = (ex.InnerException as SocketException)?.SocketErrorCode;
                                if (socketError == SocketError.TimedOut && ProbeIsCurrent())
                                {
                                    McpLogger.LogError(
                                        $"MCP bridge is not serving: port {port} accepted a connection but " +
                                        "returned nothing and did not close it. Clients will hang rather than " +
                                        "fail. Use Tools > MCP Unity > Force Restart Server; if that does not " +
                                        "clear it, restart the Unity Editor.");
                                }
                            }

                            // One successful connect is a conclusive test either way.
                            return;
                        }
                    }

                    // No address accepted a connection at all. That is ambiguous - the listener may
                    // still be settling - and the MCP server's own connect is the authoritative
                    // test, so stay silent rather than false-alarm.
                }
                catch (Exception ex)
                {
                    if (ProbeIsCurrent())
                    {
                        McpLogger.LogWarning($"MCP liveness probe failed to run: {ex.Message}");
                    }
                }
            });

            probe.IsBackground = true;
            probe.Name = "McpUnity Liveness Probe";
            probe.Start();
        }

        private void ScheduleStartServer(bool requireAutoStart, string reason, int attempt = 1)
        {
            int normalizedAttempt = Math.Min(Math.Max(attempt, 1), DelayedStartMaxAttempts);
            if (_delayedStartScheduled)
            {
                _delayedStartRequiresAutoStart = _delayedStartRequiresAutoStart && requireAutoStart;
                _delayedStartAttempt = Math.Max(_delayedStartAttempt, normalizedAttempt);
                _delayedStartReason = reason;
                return;
            }

            _delayedStartScheduled = true;
            _delayedStartRequiresAutoStart = requireAutoStart;
            _delayedStartAttempt = normalizedAttempt;
            _delayedStartReason = reason;
            double delaySeconds = GetDelayedStartDelaySeconds(normalizedAttempt);
            _delayedStartEarliestTime = EditorApplication.timeSinceStartup + delaySeconds;
            McpLogger.LogInfo($"WebSocket server start scheduled in {delaySeconds:0.##}s ({reason}, attempt {normalizedAttempt}/{DelayedStartMaxAttempts}).");
            EditorApplication.delayCall += StartServerAfterDelay;
            EditorApplication.update += StartServerAfterDelayOnUpdate;
        }

        private void CancelScheduledStart()
        {
            if (!_delayedStartScheduled)
            {
                return;
            }

            EditorApplication.delayCall -= StartServerAfterDelay;
            EditorApplication.update -= StartServerAfterDelayOnUpdate;
            _delayedStartScheduled = false;
            _delayedStartRequiresAutoStart = false;
            _delayedStartAttempt = 0;
            _delayedStartEarliestTime = 0;
            _delayedStartReason = null;
        }

        private void StartServerAfterDelay()
        {
            if (!_delayedStartScheduled)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup < _delayedStartEarliestTime)
            {
                EditorApplication.delayCall += StartServerAfterDelay;
                return;
            }

            RunScheduledStart();
        }

        private void StartServerAfterDelayOnUpdate()
        {
            if (!_delayedStartScheduled)
            {
                EditorApplication.update -= StartServerAfterDelayOnUpdate;
                return;
            }

            if (EditorApplication.timeSinceStartup < _delayedStartEarliestTime)
            {
                return;
            }

            RunScheduledStart();
        }

        private void RunScheduledStart()
        {
            _delayedStartScheduled = false;
            EditorApplication.delayCall -= StartServerAfterDelay;
            EditorApplication.update -= StartServerAfterDelayOnUpdate;

            bool requireAutoStart = _delayedStartRequiresAutoStart;
            int attempt = Math.Min(Math.Max(_delayedStartAttempt, 1), DelayedStartMaxAttempts);
            string reason = _delayedStartReason;
            _delayedStartRequiresAutoStart = false;
            _delayedStartAttempt = 0;
            _delayedStartEarliestTime = 0;
            _delayedStartReason = null;

            if (!ShouldRunInCurrentProcess() || _instance != this)
            {
                return;
            }

            if (requireAutoStart && !McpUnitySettings.Instance.AutoStartServer)
            {
                McpLogger.LogInfo("Scheduled WebSocket server start skipped because auto-start is disabled.");
                return;
            }

            if (IsListening)
            {
                return;
            }

            bool isFinalAttempt = attempt >= DelayedStartMaxAttempts;
            double nextRetryDelaySeconds = isFinalAttempt ? 0 : GetDelayedStartDelaySeconds(attempt + 1);
            StartServerResult result = StartServerInternal(isFinalAttempt, attempt, nextRetryDelaySeconds);
            if (result == StartServerResult.AddressAlreadyInUse && !isFinalAttempt)
            {
                ScheduleStartServer(requireAutoStart, reason ?? "port still in use", attempt + 1);
            }
        }

        private void CleanupFailedStart(WebSocketServer webSocketServer)
        {
            McpBackgroundTick.Stop();

            if (webSocketServer == null)
            {
                Clients.Clear();
                return;
            }

            try
            {
                if (webSocketServer.IsListening)
                {
                    webSocketServer.Stop();
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogWarning($"Error cleaning up failed WebSocket server start: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_webSocketServer, webSocketServer))
                {
                    _webSocketServer = null;
                }

                Clients.Clear();
            }
        }

        /// <summary>
        /// Stops the server as part of the editor lifecycle (reloads, play mode, restarts) without
        /// marking it as stopped by the user.
        /// </summary>
        private void StopServerInternal(ushort? closeCode, string closeReason)
        {
            CancelScheduledStart();
            _activeConnectionGeneration = 0;
            _livenessProbeCancellation?.Cancel();
            McpBackgroundTick.Stop();

            if (_webSocketServer == null)
            {
                Clients.Clear();
                return;
            }

            try
            {
                CloseAllClients(_webSocketServer, closeCode ?? 1000, closeReason ?? "Server stopping");

                _webSocketServer.Stop();

                McpLogger.LogInfo("WebSocket server stopped");
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error during WebSocketServer.Stop(): {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                _webSocketServer = null;
                Clients.Clear();
                McpLogger.LogInfo("WebSocket server stopped and resources cleaned up.");
            }
        }

        /// <summary>
        /// Stop the WebSocket server without blocking the calling thread. Sessions are closed on the
        /// calling thread (so clients receive the close code immediately) and the listener is stopped
        /// on a background thread. Used during play mode transitions to avoid hanging Unity.
        /// </summary>
        private void StopServerNonBlocking(ushort? closeCode, string closeReason)
        {
            CancelScheduledStart();
            _activeConnectionGeneration = 0;
            _livenessProbeCancellation?.Cancel();
            McpBackgroundTick.Stop();

            var serverToStop = _webSocketServer;
            _webSocketServer = null;
            Clients.Clear();

            if (serverToStop == null)
            {
                return;
            }

            CloseAllClients(serverToStop, closeCode ?? 1000, closeReason ?? "Server stopping");

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    serverToStop.Stop();
                    McpLogger.LogInfo("WebSocket server stopped (non-blocking)");
                }
                catch (Exception ex)
                {
                    McpLogger.LogWarning($"Error during non-blocking server stop: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Close all connected clients with a specific close code
        /// </summary>
        /// <param name="server">Server whose sessions should be closed</param>
        /// <param name="closeCode">WebSocket close code</param>
        /// <param name="reason">Reason message for the close</param>
        private static void CloseAllClients(WebSocketServer server, ushort closeCode, string reason)
        {
            if (server == null)
            {
                return;
            }

            try
            {
                if (server.WebSocketServices.TryGetServiceHost(ServicePath, out var service) && service?.Sessions != null)
                {
                    // Get all active session IDs and close each with the custom code
                    var sessionIds = new List<string>(service.Sessions.IDs);
                    foreach (var sessionId in sessionIds)
                    {
                        service.Sessions.CloseSession(sessionId, closeCode, reason);
                    }
                    if (sessionIds.Count > 0)
                    {
                        McpLogger.LogInfo($"Closed {sessionIds.Count} client connection(s) with code {closeCode}: {reason}");
                    }
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error closing client connections: {ex.Message}");
            }
        }

        /// <summary>
        /// Register all available tools
        /// </summary>
        private void RegisterTools()
        {
            // Register MenuItemTool
            MenuItemTool menuItemTool = new MenuItemTool();
            _tools.Add(menuItemTool.Name, menuItemTool);

            // Register SelectGameObjectTool
            SelectGameObjectTool selectGameObjectTool = new SelectGameObjectTool();
            _tools.Add(selectGameObjectTool.Name, selectGameObjectTool);

            // Register UpdateGameObjectTool
            UpdateGameObjectTool updateGameObjectTool = new UpdateGameObjectTool();
            _tools.Add(updateGameObjectTool.Name, updateGameObjectTool);

            // Register PackageManagerTool
            AddPackageTool addPackageTool = new AddPackageTool();
            _tools.Add(addPackageTool.Name, addPackageTool);

            // Register RunTestsTool
            RunTestsTool runTestsTool = new RunTestsTool(_testRunnerService);
            _tools.Add(runTestsTool.Name, runTestsTool);

            // Register SendConsoleLogTool
            SendConsoleLogTool sendConsoleLogTool = new SendConsoleLogTool();
            _tools.Add(sendConsoleLogTool.Name, sendConsoleLogTool);

            // Register GetConsoleLogsTool
            GetConsoleLogsTool getConsoleLogsTool = new GetConsoleLogsTool(_consoleLogsService);
            _tools.Add(getConsoleLogsTool.Name, getConsoleLogsTool);

            // Register UpdateComponentTool
            UpdateComponentTool updateComponentTool = new UpdateComponentTool();
            _tools.Add(updateComponentTool.Name, updateComponentTool);

            // Register AddAssetToSceneTool
            AddAssetToSceneTool addAssetToSceneTool = new AddAssetToSceneTool();
            _tools.Add(addAssetToSceneTool.Name, addAssetToSceneTool);

            // Register CreatePrefabTool
            CreatePrefabTool createPrefabTool = new CreatePrefabTool();
            _tools.Add(createPrefabTool.Name, createPrefabTool);

            // Register CreateSceneTool
            CreateSceneTool createSceneTool = new CreateSceneTool();
            _tools.Add(createSceneTool.Name, createSceneTool);

            // Register DeleteSceneTool
            DeleteSceneTool deleteSceneTool = new DeleteSceneTool();
            _tools.Add(deleteSceneTool.Name, deleteSceneTool);

            // Register LoadSceneTool
            LoadSceneTool loadSceneTool = new LoadSceneTool();
            _tools.Add(loadSceneTool.Name, loadSceneTool);

            // Register SaveSceneTool
            SaveSceneTool saveSceneTool = new SaveSceneTool();
            _tools.Add(saveSceneTool.Name, saveSceneTool);

            // Register GetSceneInfoTool
            GetSceneInfoTool getSceneInfoTool = new GetSceneInfoTool();
            _tools.Add(getSceneInfoTool.Name, getSceneInfoTool);

            // Register GetPlayModeStatusTool
            GetPlayModeStatusTool getPlayModeStatusTool = new GetPlayModeStatusTool();
            _tools.Add(getPlayModeStatusTool.Name, getPlayModeStatusTool);

            // Register SetPlayModeStatusTool
            SetPlayModeStatusTool setPlayModeStatusTool = new SetPlayModeStatusTool();
            _tools.Add(setPlayModeStatusTool.Name, setPlayModeStatusTool);

            // Register UnloadSceneTool
            UnloadSceneTool unloadSceneTool = new UnloadSceneTool();
            _tools.Add(unloadSceneTool.Name, unloadSceneTool);

            // Register RecompileScriptsTool
            RecompileScriptsTool recompileScriptsTool = new RecompileScriptsTool();
            _tools.Add(recompileScriptsTool.Name, recompileScriptsTool);

            // Register GetGameObjectTool
            GetGameObjectTool getGameObjectTool = new GetGameObjectTool();
            _tools.Add(getGameObjectTool.Name, getGameObjectTool);

            // Register DuplicateGameObjectTool
            DuplicateGameObjectTool duplicateGameObjectTool = new DuplicateGameObjectTool();
            _tools.Add(duplicateGameObjectTool.Name, duplicateGameObjectTool);

            // Register DeleteGameObjectTool
            DeleteGameObjectTool deleteGameObjectTool = new DeleteGameObjectTool();
            _tools.Add(deleteGameObjectTool.Name, deleteGameObjectTool);

            // Register ReparentGameObjectTool
            ReparentGameObjectTool reparentGameObjectTool = new ReparentGameObjectTool();
            _tools.Add(reparentGameObjectTool.Name, reparentGameObjectTool);

            // Register Transform Tools
            MoveGameObjectTool moveGameObjectTool = new MoveGameObjectTool();
            _tools.Add(moveGameObjectTool.Name, moveGameObjectTool);

            RotateGameObjectTool rotateGameObjectTool = new RotateGameObjectTool();
            _tools.Add(rotateGameObjectTool.Name, rotateGameObjectTool);

            ScaleGameObjectTool scaleGameObjectTool = new ScaleGameObjectTool();
            _tools.Add(scaleGameObjectTool.Name, scaleGameObjectTool);

            SetTransformTool setTransformTool = new SetTransformTool();
            _tools.Add(setTransformTool.Name, setTransformTool);

            // Register Material Tools
            CreateMaterialTool createMaterialTool = new CreateMaterialTool();
            _tools.Add(createMaterialTool.Name, createMaterialTool);

            AssignMaterialTool assignMaterialTool = new AssignMaterialTool();
            _tools.Add(assignMaterialTool.Name, assignMaterialTool);

            ModifyMaterialTool modifyMaterialTool = new ModifyMaterialTool();
            _tools.Add(modifyMaterialTool.Name, modifyMaterialTool);

            GetMaterialInfoTool getMaterialInfoTool = new GetMaterialInfoTool();
            _tools.Add(getMaterialInfoTool.Name, getMaterialInfoTool);

            // Register CaptureScreenshotTool
            CaptureScreenshotTool captureScreenshotTool = new CaptureScreenshotTool();
            _tools.Add(captureScreenshotTool.Name, captureScreenshotTool);

            // Register TestHotReloadTool - Added for testing hot-reload functionality
            TestHotReloadTool testHotReloadTool = new TestHotReloadTool();
            _tools.Add(testHotReloadTool.Name, testHotReloadTool);

            // Register SimpleUITool - Basic UI control (replaces complex UIManipulationTool)
            SimpleUITool simpleUITool = new SimpleUITool();
            _tools.Add(simpleUITool.Name, simpleUITool);

            // Register CompilationStatusTool
            CompilationStatusTool compilationStatusTool = new CompilationStatusTool();
            _tools.Add(compilationStatusTool.Name, compilationStatusTool);

            // Register SetObjectReferenceTool - Wire up object references on serialized fields
            SetObjectReferenceTool setObjectReferenceTool = new SetObjectReferenceTool();
            _tools.Add(setObjectReferenceTool.Name, setObjectReferenceTool);

            // Register CreateUIElementTool - Generic UI element factory
            CreateUIElementTool createUIElementTool = new CreateUIElementTool();
            _tools.Add(createUIElementTool.Name, createUIElementTool);

            // Register InspectGameObjectTool - Inspect GameObjects and their components
            InspectGameObjectTool inspectGameObjectTool = new InspectGameObjectTool();
            _tools.Add(inspectGameObjectTool.Name, inspectGameObjectTool);

            // Register additional tools from extension point
            RegisterAdditionalTools.RegisterTo(_tools);

            // Register BatchExecuteTool last: it dispatches to the other registered tools
            BatchExecuteTool batchExecuteTool = new BatchExecuteTool(this);
            _tools.Add(batchExecuteTool.Name, batchExecuteTool);
        }

        /// <summary>
        /// Register all available resources (keyed by name; URIs are resolved via TryGetResourceByUri)
        /// </summary>
        private void RegisterResources()
        {
            // Register GetMenuItemsResource
            GetMenuItemsResource getMenuItemsResource = new GetMenuItemsResource();
            _resources.Add(getMenuItemsResource.Name, getMenuItemsResource);

            // Register GetConsoleLogsResource
            GetConsoleLogsResource getConsoleLogsResource = new GetConsoleLogsResource(_consoleLogsService);
            _resources.Add(getConsoleLogsResource.Name, getConsoleLogsResource);

            // Register GetScenesHierarchyResource
            GetScenesHierarchyResource getScenesHierarchyResource = new GetScenesHierarchyResource();
            _resources.Add(getScenesHierarchyResource.Name, getScenesHierarchyResource);

            // Register GetPackagesResource
            GetPackagesResource getPackagesResource = new GetPackagesResource();
            _resources.Add(getPackagesResource.Name, getPackagesResource);

            // Register GetAssetsResource
            GetAssetsResource getAssetsResource = new GetAssetsResource();
            _resources.Add(getAssetsResource.Name, getAssetsResource);

            // Register GetTestsResource
            GetTestsResource getTestsResource = new GetTestsResource(_testRunnerService);
            _resources.Add(getTestsResource.Name, getTestsResource);

            // Register GetGameObjectResource
            GetGameObjectResource getGameObjectResource = new GetGameObjectResource();
            _resources.Add(getGameObjectResource.Name, getGameObjectResource);
        }

        /// <summary>
        /// Initialize services used by the server
        /// </summary>
        private void InitializeServices()
        {
            // Initialize the test runner service
            _testRunnerService = new TestRunnerService();

            // Initialize the console logs service
            _consoleLogsService = new ConsoleLogsService();
        }

        /// <summary>
        /// Discovers methods decorated with [McpTool] and registers them, replacing any
        /// previously registered attributed tools. Built-in tools always take precedence.
        /// </summary>
        public void RefreshAttributedTools()
        {
            McpLogger.LogInfo("Refreshing attributed tools...");

            var attributedToolKeys = _tools.Where(kvp => kvp.Value is AttributedMethodTool).Select(kvp => kvp.Key).ToList();
            foreach (var key in attributedToolKeys)
            {
                _tools.Remove(key);
            }

            int registeredCount = 0;
            foreach (var tool in AttributedToolDiscovery.DiscoverAttributedTools())
            {
                if (_tools.ContainsKey(tool.Name))
                {
                    McpLogger.LogWarning($"[RefreshAttributedTools] Tool '{tool.Name}' already exists, skipping attributed version");
                    continue;
                }

                _tools.Add(tool.Name, tool);
                registeredCount++;
            }

            McpLogger.LogInfo($"Attributed tools refreshed: removed {attributedToolKeys.Count}, registered {registeredCount}. Total tools: {_tools.Count}");
        }

        /// <summary>
        /// Pushes a notification to connected MCP clients
        /// </summary>
        /// <param name="notification">The notification to send</param>
        /// <returns>True if notification was sent successfully</returns>
        public bool PushNotification(object notification)
        {
            if (_webSocketServer == null || !IsListening)
            {
                return false;
            }

            if (!_webSocketServer.WebSocketServices.TryGetServiceHost(ServicePath, out var host) || host.Sessions.Count == 0)
            {
                // No connected clients - this is normal, not an error
                return false;
            }

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(notification);
            host.Sessions.Broadcast(json);

            if (DevelopmentMode.Settings.VerboseLogging)
            {
                McpLogger.LogInfo($"Notification sent to {host.Sessions.Count} connected clients");
            }
            return true;
        }

        /// <summary>
        /// Called after every domain reload
        /// </summary>
        [DidReloadScripts]
        private static void AfterReload()
        {
            if (!ShouldRunInCurrentProcess())
            {
                return;
            }

            // Ensure Instance is created and hooks are set up after initial domain load
            var currentInstance = Instance;
        }

        /// <summary>
        /// Handles the Unity Editor quitting event. Stops the server without blocking the quit
        /// sequence and releases this instance's port allocation and discovery entry.
        /// </summary>
        private static void OnEditorQuitting()
        {
            if (_instance == null) return;

            McpLogger.LogInfo("Editor is quitting. Ensuring server is stopped.");
            try
            {
                _instance.StopServerNonBlocking(null, "Unity Editor quitting");
                _instance.Dispose();

                // Release the port allocation for this Unity instance
                PortManager.ReleasePort();
                // Unregister from service discovery
                ServiceDiscovery.UnregisterService();
            }
            catch (Exception ex)
            {
                // Never block Unity's quit sequence
                McpLogger.LogWarning($"Non-fatal error during quit cleanup: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the Unity Editor's 'before assembly reload' event.
        /// Stops the WebSocket server to prevent port conflicts and ensure a clean state before scripts are recompiled.
        /// </summary>
        private static void OnBeforeAssemblyReload()
        {
            if (_instance == null) return;

            _instance.StopServerInternal(null, "Unity assembly reload");
        }

        /// <summary>
        /// Handles the Unity Editor's 'after assembly reload' event.
        /// If auto-start is enabled, attempts to restart the WebSocket server if it's not already listening.
        /// This ensures the server is operational after script recompilation.
        /// </summary>
        private static void OnAfterAssemblyReload()
        {
            if (!ShouldRunInCurrentProcess() || _instance == null) return;

            if (McpUnitySettings.Instance.AutoStartServer && !_instance.IsListening)
            {
                _instance.ScheduleStartServer(requireAutoStart: true, reason: "assembly reload");
            }
        }

        /// <summary>
        /// Handles changes in Unity Editor's play mode state.
        /// Stops the server when exiting Edit Mode, and restarts it when entering Play Mode or returning to Edit Mode if auto-start is enabled.
        /// This handler must be non-blocking to avoid hanging Unity's play mode transitions.
        /// </summary>
        /// <param name="state">The current play mode state change.</param>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!ShouldRunInCurrentProcess() || _instance == null) return;

            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    // About to enter Play Mode - use custom close code so clients use fast polling.
                    // The listener is stopped off the main thread so the transition never blocks.
                    _instance.StopServerNonBlocking(UnityCloseCode.PlayMode, "Unity entering Play mode");
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    // The assumption above (server stays down through Play, a domain reload on exit
                    // will restart it) only holds when Enter Play Mode Options are OFF or don't disable
                    // domain reload. With "Reload Scene without Reload Domain" -- Project Settings >
                    // Editor > Enter Play Mode Options, a real, supported Unity profile some projects
                    // pick specifically for iteration speed -- NO domain reload happens on either side
                    // of Play Mode, so nothing was ever going to bring the server back. That leaves it
                    // down for the entire Play session, with no way for a client to even ask Unity to
                    // exit Play, since that request needs this same server. Confirmed live: a client
                    // got locked in Play with no way out until the Editor was closed by hand.
                    // Restarting here covers both profiles: if a domain reload already restarted it via
                    // OnAfterAssemblyReload, IsListening is already true and this is a no-op; if it
                    // didn't, this is the only thing that brings it back.
                    if (!_instance.IsListening && McpUnitySettings.Instance.AutoStartServer)
                    {
                        _instance.ScheduleStartServer(requireAutoStart: true, reason: "entered play mode");
                    }
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    // Returned to Edit Mode
                    if (!_instance.IsListening && McpUnitySettings.Instance.AutoStartServer)
                    {
                        _instance.ScheduleStartServer(requireAutoStart: true, reason: "entered edit mode");
                    }
                    break;
            }
        }

        /// <summary>
        /// Called every editor frame. Performs periodic health checks and auto-recovery.
        /// </summary>
        private void OnEditorUpdate()
        {
            if (!_autoHealingEnabled || _stoppedByUser || !McpUnitySettings.Instance.AutoStartServer)
                return;

            // Scheduled starts own the recovery while they are pending; clones never run a server.
            if (_delayedStartScheduled || EditorApplication.isCompiling || McpUtils.IsMultiplayerPlayModeClone())
                return;

            // Check if enough time has passed since last health check
            double currentTime = EditorApplication.timeSinceStartup;
            if (currentTime - _lastHealthCheckTime < HealthCheckIntervalSeconds)
                return;

            _lastHealthCheckTime = currentTime;

            // Don't check during grace period after server start
            if (_serverStartTime != DateTime.MinValue &&
                (DateTime.UtcNow - _serverStartTime).TotalSeconds < HealthCheckGracePeriodSeconds)
                return;

            // If server should be running but isn't, schedule a start
            if (!IsListening)
            {
                _consecutiveHealthFailures = 0;
                McpLogger.LogInfo("[AutoHeal] Server not listening, scheduling start...");
                ScheduleStartServer(requireAutoStart: true, reason: "auto-heal");
                return;
            }

            // Server reports listening - verify it's actually accepting connections
            bool isHealthy;
            try
            {
                isHealthy = TryConnectToServerPort();
            }
            catch
            {
                isHealthy = false;
            }

            if (isHealthy)
            {
                if (_consecutiveHealthFailures > 0)
                {
                    McpLogger.LogInfo("[AutoHeal] Server health restored");
                }
                _consecutiveHealthFailures = 0;
                return;
            }

            _consecutiveHealthFailures++;
            McpLogger.LogWarning($"[AutoHeal] Health check failed ({_consecutiveHealthFailures}/{HealthFailureThreshold})");

            if (_consecutiveHealthFailures >= HealthFailureThreshold)
            {
                McpLogger.LogWarning("[AutoHeal] Zombie state detected - triggering auto-recovery...");
                _consecutiveHealthFailures = 0;

                // Disable auto-healing temporarily to prevent rapid retries
                _autoHealingEnabled = false;

                // Schedule the restart on next frame to avoid issues
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        ForceRestartServer();
                    }
                    finally
                    {
                        _autoHealingEnabled = true;
                    }
                };
            }
        }

        /// <summary>
        /// Opens (and immediately closes) a TCP connection to the server port on the loopback address.
        /// Returns false when the connect times out, which indicates a zombie listener.
        /// </summary>
        private static bool TryConnectToServerPort()
        {
            using (var testClient = new TcpClient())
            {
                var connectTask = testClient.ConnectAsync("127.0.0.1", McpUnitySettings.Instance.Port);
                if (!connectTask.Wait(TimeSpan.FromMilliseconds(500)))
                {
                    return false;
                }
                return testClient.Connected;
            }
        }
    }
}
