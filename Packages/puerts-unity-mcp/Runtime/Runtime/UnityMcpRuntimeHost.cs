using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PuertsUnityMcp
{
    public sealed partial class UnityMcpRuntimeHost : MonoBehaviour, IUnityMcpEndpoint
    {
        private const string RuntimePortEnvironment = "PUERTS_UNITY_MCP_RUNTIME_PORT";
        private const string TargetIdEnvironment = "PUERTS_UNITY_MCP_TARGET_ID";
        private const string DisableEnvironment = "PUERTS_UNITY_MCP_DISABLE";
        private const string PlayerIdPrefsKey = "PuertsUnityMcp.PlayerId";

        public static UnityMcpRuntimeHost Instance { get; private set; }

        public UnityMcpRuntimeSettings settings = UnityMcpRuntimeSettings.CreateDefault();

        private readonly UnityMcpToolRegistry tools = new UnityMcpToolRegistry();
        private readonly OperationStore operations = new OperationStore();
        private readonly UnityMcpRuntimeLogBuffer runtimeLogBuffer = new UnityMcpRuntimeLogBuffer();
        private readonly List<string> runtimeResourceScriptToolNames = new List<string>();
        private PuertsScriptHost scriptHost;
        private UnityMcpHttpServer httpServer;
        private CommandFilePump commandPump;
        private UnityMcpLanDiscoveryService discoveryService;
        private DateTime startedAtUtc;
        private DateTime lastHeartbeatUtc;
        private string endpointId;
        private int actualPort;
        private long lastMainThreadTickUtcTicks;
        private bool initialized;
        private bool lanDiscoveryEnabled;
        private string discoveryName;
        private string discoveryGroup;
        private bool enableFileCommandPump;
        private bool enableDiskHeartbeat;
        private bool enableDiscoveredEndpointCache;
        private string screenshotWriteMode = "memory";
        private int heartbeatIntervalMs = 30000;

        public string EndpointId => endpointId;
        public string EndpointKind => "player";
        public string EndpointName => Application.productName;
        public UnityMcpToolRegistry Tools => tools;
        public PuertsScriptHost ScriptHost => scriptHost;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoCreateRuntime()
        {
            Debug.Log("[UnityMCP] Runtime bootstrap invoked. isEditor=" + Application.isEditor
                + ", productName=" + Application.productName
                + ", streamingAssetsPath=" + Application.streamingAssetsPath);
            try
            {
                EnsureAutoStarted();
            }
            catch (Exception ex)
            {
                Debug.LogError("[UnityMCP] Runtime bootstrap failed: " + ex);
            }
        }

        public static bool EnsureAutoStarted()
        {
            if (IsDisabledByEnvironment())
            {
                Debug.Log("[UnityMCP] Runtime auto-start skipped: disabled by " + DisableEnvironment + ".");
                return false;
            }

            if (Instance != null)
            {
                Debug.Log("[UnityMCP] Runtime auto-start skipped: existing instance " + Instance.EndpointId + ".");
                return false;
            }

            var config = UnityMcpRuntimeConfigStore.Load();
            Debug.Log("[UnityMCP] Runtime config resolved. source=" + UnityMcpRuntimeConfigStore.LastLoadSource
                + ", autoStart=" + config.runtimeAutoStart
                + ", bind=" + config.runtimeBindAddress
                + ", port=" + config.runtimePort
                + ", lanDiscovery=" + config.lanDiscoveryEnabled
                + ", name_group=" + config.name_group
                + ", allowJsEval=" + config.allowJsEval
                + ", allowReflection=" + config.allowReflection);
            if (!config.runtimeAutoStart)
            {
                Debug.Log("[UnityMCP] Runtime auto-start skipped: runtimeAutoStart=false.");
                return false;
            }

            var go = new GameObject("[PuertsUnityMcpRuntime]");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<UnityMcpRuntimeHost>();
            Debug.Log("[UnityMCP] Runtime host GameObject created.");
            return true;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            UnityMcpMainThread.Initialize();
            Initialize();
        }

        private void OnEnable()
        {
            if (initialized)
            {
                StartHttpServerIfNeeded();
                WriteHeartbeat();
                StartDiscoveryIfNeeded();
            }
        }

        private void OnDisable()
        {
            StopDiscovery();
            StopHttpServer();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                StopHttpServer();
                StopDiscovery();
                runtimeLogBuffer.Dispose();
                scriptHost?.Dispose();
                scriptHost = null;
                Instance = null;
            }
        }

        private void Update()
        {
            UnityMcpMainThread.Drain();
            MarkMainThreadTick();
            scriptHost?.Tick();
            commandPump?.Tick(Math.Max(1, settings == null ? 4 : settings.maxCommandsPerFrame));
            discoveryService?.Tick();

            if ((DateTime.UtcNow - lastHeartbeatUtc).TotalMilliseconds >= Math.Max(1000, heartbeatIntervalMs))
            {
                WriteHeartbeat();
            }
        }

        private void Initialize()
        {
            if (initialized)
            {
                return;
            }

            Debug.Log("[UnityMCP] Runtime Initialize start. platform=" + Application.platform
                + ", unityVersion=" + Application.unityVersion
                + ", applicationVersion=" + Application.version
                + ", dataPath=" + Application.dataPath
                + ", persistentDataPath=" + Application.persistentDataPath);
            startedAtUtc = DateTime.UtcNow;
            settings = settings ?? UnityMcpRuntimeSettings.CreateDefault();
            var config = UnityMcpRuntimeConfigStore.Load();
            ApplyRuntimeConfig(config);
            ApplyEnvironmentOverrides();
            endpointId = ResolveTargetId();
            Debug.Log("[UnityMCP] Runtime settings applied. endpointId=" + endpointId
                + ", configSource=" + UnityMcpRuntimeConfigStore.LastLoadSource
                + ", mcpEnabled=" + settings.mcpEnabled
                + ", bind=" + settings.bindAddress
                + ", port=" + settings.httpPort
                + ", lanDiscovery=" + lanDiscoveryEnabled
                + ", discoveryName=" + discoveryName
                + ", discoveryGroup=" + discoveryGroup
                + ", runInBackground=" + settings.runInBackground);
            scriptHost = new PuertsScriptHost("runtime:" + endpointId);
            runtimeLogBuffer.Initialize(settings.logBufferSize <= 0 ? 500 : settings.logBufferSize);
            RegisterTools();
            if (enableFileCommandPump)
            {
                commandPump = new CommandFilePump(this);
            }

            if (NeedsPlayerStateDirectory())
            {
                AtomicFile.EnsurePrivateDirectory(UnityMcpPaths.PlayerRoot(endpointId));
            }

            MarkMainThreadTick();

            if (settings.runInBackground)
            {
                Application.runInBackground = true;
            }

            StartHttpServerIfNeeded();
            WriteHeartbeat();
            StartDiscoveryIfNeeded();
            initialized = true;
        }

        public string BuildHealthJson()
        {
            return UnityJson.ToJson(BuildHealth());
        }

        public UnityMcpHealth BuildHealth()
        {
            var readyAge = GetLastMainThreadTickAgeMs();
            return new UnityMcpHealth
            {
                status = "ok",
                ready = readyAge < 3000,
                runtimeState = initialized ? "running" : "starting",
                endpointKind = EndpointKind,
                endpointId = EndpointId,
                endpointName = EndpointName,
                name_group = UnityMcpLanDiscoveryService.NormalizeGroup(discoveryGroup),
                projectRoot = UnityMcpPaths.ProjectRoot,
                stateRoot = UnityMcpPaths.StateRoot,
                httpUrl = httpServer == null ? null : httpServer.Url,
                httpPort = actualPort,
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                productName = Application.productName,
                applicationVersion = Application.version,
                isEditor = Application.isEditor,
                isDebugBuild = Debug.isDebugBuild,
                uptimeSeconds = (DateTime.UtcNow - startedAtUtc).TotalSeconds,
                lastMainThreadTickAgeMs = readyAge,
                toolRegistryVersion = tools.Version,
                capabilities = BuildCapabilities()
            };
        }

        public Task<string> CallToolAsync(string name, UnityMcpToolArguments arguments)
        {
            if (UnityMcpMainThread.IsMainThread)
            {
                return tools.ExecuteAsync(new UnityMcpToolContext(this), name, arguments ?? new UnityMcpToolArguments());
            }

            return UnityMcpMainThread.InvokeAsync<string>(() =>
                tools.ExecuteAsync(new UnityMcpToolContext(this), name, arguments ?? new UnityMcpToolArguments()));
        }

        public string EvalJavaScript(string code, string mode, string chunkName)
        {
            if (settings == null || !settings.allowJsEval)
            {
                throw new InvalidOperationException("Runtime JS eval is disabled.");
            }

            return scriptHost.Eval(PrepareJavaScript(code, mode), chunkName, true);
        }

        private void RegisterTools()
        {
            tools.Register(new DelegateUnityMcpTool("mcp.info", "Return endpoint metadata and health.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(BuildHealthJson())));

            tools.Register(new DelegateUnityMcpTool("runtime.status", "Return runtime endpoint state.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(BuildHealthJson())));

            tools.Register(new DelegateUnityMcpTool("runtime.targets.list", "List this Player MCP endpoint and LAN endpoints discovered by this player.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(ListTargets()))));

            tools.Register(new DelegateUnityMcpTool("targets.list", "List this Player MCP endpoint and LAN endpoints discovered by this player.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(ListTargets()))));

            tools.Register(new DelegateUnityMcpTool("lan.discovery.scan", "Broadcast a LAN discovery query for endpoints in the same name_group.", JsonSchemas.Object(), (ctx, args) =>
            {
                discoveryService?.SendQuery();
                return Task.FromResult(UnityJson.ToJson(new DiscoveryScanResult
                {
                    enabled = discoveryService != null && discoveryService.IsRunning,
                    endpointKind = EndpointKind,
                    name = discoveryName,
                    name_group = discoveryGroup,
                    port = UnityMcpConstants.DiscoveryPort
                }));
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.js.eval", "Execute PuerTS JavaScript in the runtime VM, including Play Mode and phone/player builds. Use runtime-safe CS.UnityEngine APIs when wrapped; use __unity_mcp.invokeStatic(type, method, ...args), getStatic, getStaticPath, setStatic, or typeExists as reflection fallback. Return JSON-serializable data.", JsonSchemas.Object(
                JsonSchemas.StringProperty("code", "PuerTS JavaScript. In script mode use return; in expression mode provide a single expression. Prefer CS.* APIs, fallback to __unity_mcp for reflection."),
                JsonSchemas.StringProperty("mode", "script or expression."),
                JsonSchemas.StringProperty("chunkName", "Optional chunk name for stack traces.")), (ctx, args) =>
            {
                return Task.FromResult(EvalJavaScript(args.code ?? string.Empty, args.mode ?? "script", args.chunkName));
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.reflection.invoke", "Invoke a static C# method through the reflection gateway.", JsonSchemas.Object(
                JsonSchemas.StringProperty("typeName"),
                JsonSchemas.StringProperty("methodName"),
                JsonSchemas.StringProperty("argsJson")), (ctx, args) =>
            {
                var gateway = new ReflectionGateway();
                return Task.FromResult(gateway.InvokeStaticJson(args.typeName, args.methodName, args.argsJson));
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.scriptTools.list", "List project JavaScript MCP tools loaded from puerts-unity-mcp-extension/Runtime/runtime-tools.", JsonSchemas.Object(), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(BuildRuntimeScriptToolListResult("runtime.scriptTools.list")));
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.scriptTools.reload", "Reload project JavaScript MCP tools from puerts-unity-mcp-extension/Runtime/runtime-tools.", JsonSchemas.Object(), (ctx, args) =>
            {
                ResetScriptHost();
                RegisterRuntimeResourceScriptTools();
                return Task.FromResult(UnityJson.ToJson(BuildRuntimeScriptToolListResult("runtime.scriptTools.reload")));
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.skills.list", "List project skills loaded from puerts-unity-mcp-extension/skills.", JsonSchemas.Object(), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(BuildRuntimeSkillListResult("runtime.skills.list")));
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.skill.load", "Load one project skill from puerts-unity-mcp-extension/skills by name.", JsonSchemas.Object(
                JsonSchemas.StringProperty("name")), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(BuildRuntimeSkillLoadResult(args.name)));
            }));

            tools.Register(new DelegateUnityMcpTool("op.status", "Read a persisted operation state/result.", JsonSchemas.Object(
                JsonSchemas.StringProperty("operationId")), (ctx, args) =>
            {
                return Task.FromResult(operations.Read(args.operationId));
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.logs", "Return recent Player logs captured in the runtime log ring buffer.", JsonSchemas.Object(
                JsonSchemas.NumberProperty("count"),
                JsonSchemas.StringProperty("logType"),
                JsonSchemas.StringProperty("regex"),
                JsonSchemas.BooleanProperty("includeStackTrace")), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(BuildRuntimeLogsResult(args)));
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.logs.clear", "Clear the runtime log ring buffer.", JsonSchemas.Object(), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(ClearRuntimeLogs()));
            }));

            tools.Register(new DelegateUnityMcpTool("screen.screenshot", "Capture a Player screenshot from the Runtime MCP endpoint.", JsonSchemas.Object(
                JsonSchemas.StringProperty("fileName")), (ctx, args) =>
                CaptureScreenshotAsync(args)));

            tools.Register(new DelegateUnityMcpTool("runtime.ui.snapshot", "Return a structured snapshot of visible runtime UGUI controls.", JsonSchemas.Object(
                JsonSchemas.NumberProperty("maxResults"),
                JsonSchemas.BooleanProperty("includeDisabled")), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(BuildUiSnapshotResult(args)));
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.ui.find", "Find runtime UGUI controls by text, name, path, or canvas.", JsonSchemas.Object(
                JsonSchemas.StringProperty("keyword"),
                JsonSchemas.NumberProperty("maxResults"),
                JsonSchemas.BooleanProperty("includeDisabled")), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(BuildUiFindResult(args)));
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.ui.raycast", "Raycast runtime UI at a screen point or resolved target.", JsonSchemas.Object(
                JsonSchemas.NumberProperty("x"),
                JsonSchemas.NumberProperty("y"),
                JsonSchemas.StringProperty("path"),
                JsonSchemas.NumberProperty("instanceId"),
                JsonSchemas.NumberProperty("maxResults")), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(BuildUiRaycastResult(args)));
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.ui.click", "Click a Unity UI element at screen coordinates, path, or instanceId.", JsonSchemas.Object(
                JsonSchemas.NumberProperty("x"),
                JsonSchemas.NumberProperty("y"),
                JsonSchemas.StringProperty("path"),
                JsonSchemas.NumberProperty("instanceId")), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(BuildUiClickResult(args)));
            }));

            tools.Register(new DelegateUnityMcpTool("input.tap", "Alias for runtime.ui.click.", JsonSchemas.Object(
                JsonSchemas.NumberProperty("x"),
                JsonSchemas.NumberProperty("y"),
                JsonSchemas.StringProperty("path"),
                JsonSchemas.NumberProperty("instanceId")), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(BuildUiClickResult(args)));
            }));

            RegisterRuntimeResourceScriptTools();
        }

        private void RegisterRuntimeResourceScriptTools()
        {
            for (var i = 0; i < runtimeResourceScriptToolNames.Count; i++)
            {
                tools.Unregister(runtimeResourceScriptToolNames[i]);
            }

            runtimeResourceScriptToolNames.Clear();
            var manifests = UnityMcpResourceScriptTools.LoadManifests(UnityMcpPaths.RuntimeToolsRoot());
            for (var i = 0; i < manifests.Length; i++)
            {
                var manifest = manifests[i];
                if (manifest == null || string.IsNullOrEmpty(manifest.name))
                {
                    continue;
                }

                tools.Register(new DelegateUnityMcpTool(manifest.name, manifest.description, manifest.inputSchemaJson, (ctx, args) =>
                    Task.FromResult(ExecuteRuntimeResourceScriptTool(manifest, args))));
                runtimeResourceScriptToolNames.Add(manifest.name);
            }
        }

        private UnityMcpScriptToolListResult BuildRuntimeScriptToolListResult(string action)
        {
            var toolRoot = UnityMcpPaths.RuntimeToolsRoot();
            var manifests = UnityMcpResourceScriptTools.LoadManifests(toolRoot);
            return new UnityMcpScriptToolListResult
            {
                action = action,
                targetId = EndpointId,
                resourceRoot = toolRoot,
                directoryRoot = toolRoot,
                tools = manifests,
                count = manifests.Length
            };
        }

        private UnityMcpSkillListResult BuildRuntimeSkillListResult(string action)
        {
            var skillsRoot = UnityMcpPaths.SkillsRoot();
            var skills = UnityMcpResourceSkills.LoadSkills(skillsRoot);
            return new UnityMcpSkillListResult
            {
                action = action,
                targetId = EndpointId,
                resourceRoot = skillsRoot,
                directoryRoot = skillsRoot,
                skills = skills,
                count = skills.Length
            };
        }

        private UnityMcpSkillLoadResult BuildRuntimeSkillLoadResult(string name)
        {
            var skillsRoot = UnityMcpPaths.SkillsRoot();
            var skill = UnityMcpResourceSkills.FindSkill(skillsRoot, name);
            return new UnityMcpSkillLoadResult
            {
                action = "runtime.skill.load",
                targetId = EndpointId,
                resourceRoot = skillsRoot,
                directoryRoot = skillsRoot,
                success = skill != null,
                error = skill == null ? "Skill not found: " + (name ?? string.Empty) : null,
                skill = skill
            };
        }

        private string ExecuteRuntimeResourceScriptTool(UnityMcpScriptToolManifest manifest, UnityMcpToolArguments args)
        {
            var context = new UnityMcpScriptToolContext
            {
                endpointKind = EndpointKind,
                endpointId = EndpointId,
                endpointName = EndpointName,
                toolName = manifest.name,
                modulePath = manifest.modulePath,
                projectRoot = UnityMcpPaths.ProjectRoot,
                stateRoot = UnityMcpPaths.StateRoot,
                extensionRoot = UnityMcpPaths.ProjectExtensionRoot
            };
            var argsJson = args == null || string.IsNullOrWhiteSpace(args.rawArgumentsJson)
                ? UnityJson.ToJson(args)
                : args.rawArgumentsJson;
            return scriptHost.ExecuteModuleFunctionJson(manifest.modulePath, manifest.functionName, argsJson, UnityJson.ToJson(context));
        }

        private void ResetScriptHost()
        {
            scriptHost?.Dispose();
            scriptHost = new PuertsScriptHost("runtime:" + endpointId);
        }

        private void StartHttpServerIfNeeded()
        {
            if (settings == null)
            {
                Debug.LogWarning("[UnityMCP] Runtime HTTP server skipped: settings is null.");
                return;
            }

            if (!settings.mcpEnabled)
            {
                Debug.Log("[UnityMCP] Runtime HTTP server skipped: mcpEnabled=false.");
                return;
            }

            if (httpServer != null)
            {
                Debug.Log("[UnityMCP] Runtime HTTP server skipped: already running on " + httpServer.Url + ".");
                return;
            }

            var startPort = ResolvePort();
            Debug.Log("[UnityMCP] Runtime HTTP server starting. bind=" + settings.bindAddress + ", startPort=" + startPort + ".");
            for (var offset = 0; offset < 10; offset++)
            {
                var candidate = startPort + offset;
                UnityMcpHttpServer server = null;
                try
                {
                    server = new UnityMcpHttpServer(this, settings.bindAddress, candidate);
                    server.Start();
                    httpServer = server;
                    actualPort = candidate;
                    return;
                }
                catch (Exception ex)
                {
                    server?.Dispose();
                    Debug.LogWarning("[UnityMCP] Runtime HTTP bind failed. bind=" + settings.bindAddress
                        + ", port=" + candidate
                        + ", error=" + ex.Message);
                    if (offset == 9)
                    {
                        Debug.LogError("[UnityMCP] Runtime HTTP server failed: " + ex.Message);
                    }
                }
            }
        }

        private void ApplyRuntimeConfig(UnityMcpRuntimeConfig config)
        {
            if (settings == null || config == null)
            {
                return;
            }

            config.Normalize();
            settings.mcpEnabled = config.runtimeAutoStart;
            settings.bindAddress = string.IsNullOrEmpty(config.runtimeBindAddress) ? "0.0.0.0" : config.runtimeBindAddress;
            settings.httpPort = config.runtimePort <= 0 ? UnityMcpConstants.DefaultPlayerPort : config.runtimePort;
            settings.logBufferSize = config.runtimeLogBufferSize <= 0 ? 500 : config.runtimeLogBufferSize;
            settings.allowJsEval = config.allowJsEval;
            settings.allowReflection = config.allowReflection;
            settings.allowPrivateReflection = config.allowPrivateReflection;
            settings.allowFileAccess = config.allowFileAccess;
            settings.allowNetworkAccess = config.allowNetworkAccess;
            settings.allowRuntimeCodeLoad = config.allowRuntimeCodeLoad;
            settings.maxCommandsPerFrame = config.maxCommandsPerFrame <= 0 ? 4 : config.maxCommandsPerFrame;
            settings.runInBackground = config.runInBackground;
            lanDiscoveryEnabled = config.lanDiscoveryEnabled;
            discoveryName = UnityMcpLanDiscoveryService.ResolveName(config.name, Application.productName);
            discoveryGroup = UnityMcpLanDiscoveryService.NormalizeGroup(config.name_group);
            enableFileCommandPump = config.enableFileCommandPump;
            enableDiskHeartbeat = config.enableDiskHeartbeat;
            enableDiscoveredEndpointCache = config.enableDiscoveredEndpointCache;
            screenshotWriteMode = config.screenshotWriteMode;
            heartbeatIntervalMs = Math.Max(1000, config.heartbeatIntervalMs);
            ReflectionGateway.EnableAotMissLog = config.enableAotMissLog;
            if (!string.IsNullOrEmpty(config.targetId))
            {
                settings.targetId = config.targetId;
            }
        }

        private void StopHttpServer()
        {
            httpServer?.Dispose();
            httpServer = null;
        }

        private void StartDiscoveryIfNeeded()
        {
            if (Application.isEditor)
            {
                Debug.Log("[UnityMCP] LAN discovery skipped: running in Editor.");
                return;
            }

            if (!lanDiscoveryEnabled)
            {
                Debug.Log("[UnityMCP] LAN discovery skipped: lanDiscoveryEnabled=false.");
                return;
            }

            if (discoveryService != null)
            {
                Debug.Log("[UnityMCP] LAN discovery skipped: already running.");
                return;
            }

            if (httpServer == null)
            {
                Debug.LogWarning("[UnityMCP] LAN discovery skipped: HTTP server is not running.");
                return;
            }

            Debug.Log("[UnityMCP] LAN discovery starting. name=" + discoveryName
                + ", group=" + discoveryGroup
                + ", port=" + UnityMcpConstants.DiscoveryPort
                + ", httpUrl=" + httpServer.Url + ".");
            discoveryService = new UnityMcpLanDiscoveryService(BuildHeartbeat, discoveryGroup, enableDiscoveredEndpointCache);
        }

        private void StopDiscovery()
        {
            discoveryService?.Dispose();
            discoveryService = null;
        }

        private void WriteHeartbeat()
        {
            lastHeartbeatUtc = DateTime.UtcNow;
            if (!enableDiskHeartbeat)
            {
                return;
            }

            AtomicFile.WriteJson(Path.Combine(UnityMcpPaths.PlayerRoot(EndpointId), UnityMcpConstants.HeartbeatFileName), BuildHeartbeat());
        }

        private UnityMcpHeartbeat BuildHeartbeat()
        {
            return new UnityMcpHeartbeat
            {
                endpointId = EndpointId,
                endpointKind = EndpointKind,
                endpointName = EndpointName,
                projectRoot = UnityMcpPaths.ProjectRoot,
                projectName = Application.productName,
                name = UnityMcpLanDiscoveryService.ResolveName(discoveryName, EndpointName, EndpointId),
                name_group = UnityMcpLanDiscoveryService.NormalizeGroup(discoveryGroup),
                processId = GetProcessId(),
                httpUrl = httpServer == null ? null : httpServer.Url,
                port = actualPort,
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                isEditor = Application.isEditor,
                lastUpdatedUtc = ResolveHeartbeatTimestamp().ToString("o"),
                source = Application.isEditor ? "local-playmode" : "player",
                capabilities = BuildCapabilities()
            };
        }

        private UnityMcpTargetList ListTargets()
        {
            var targets = new List<UnityMcpHeartbeat>
            {
                BuildHeartbeat()
            };

            AppendDiscoveredTargets(targets, discoveryService == null ? null : discoveryService.GetDiscoveredHeartbeats());
            if (enableDiscoveredEndpointCache)
            {
                AppendDiscoveredTargets(targets, Path.Combine(UnityMcpPaths.StateRoot, UnityMcpConstants.EditorsDirectoryName));
                AppendDiscoveredTargets(targets, Path.Combine(UnityMcpPaths.StateRoot, UnityMcpConstants.PlayersDirectoryName));
            }

            return new UnityMcpTargetList { targets = targets.ToArray() };
        }

        private void AppendDiscoveredTargets(List<UnityMcpHeartbeat> targets, UnityMcpHeartbeat[] heartbeats)
        {
            if (targets == null || heartbeats == null)
            {
                return;
            }

            for (var i = 0; i < heartbeats.Length; i++)
            {
                AppendDiscoveredTarget(targets, heartbeats[i]);
            }
        }

        private void AppendDiscoveredTargets(List<UnityMcpHeartbeat> targets, string root)
        {
            if (targets == null || string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return;
            }

            foreach (var heartbeatPath in Directory.GetFiles(root, UnityMcpConstants.HeartbeatFileName, SearchOption.AllDirectories))
            {
                if (!AtomicFile.TryReadJson<UnityMcpHeartbeat>(heartbeatPath, out var heartbeat) || heartbeat == null)
                {
                    continue;
                }

                if (!ShouldIncludeDiscoveredHeartbeat(heartbeat))
                {
                    continue;
                }

                AppendDiscoveredTarget(targets, heartbeat);
            }
        }

        private void AppendDiscoveredTarget(List<UnityMcpHeartbeat> targets, UnityMcpHeartbeat heartbeat)
        {
            if (targets == null || !ShouldIncludeDiscoveredHeartbeat(heartbeat))
            {
                return;
            }

            if (string.Equals(heartbeat.endpointId, EndpointId, StringComparison.Ordinal))
            {
                return;
            }

            for (var i = 0; i < targets.Count; i++)
            {
                if (targets[i] != null && string.Equals(targets[i].endpointId, heartbeat.endpointId, StringComparison.Ordinal))
                {
                    return;
                }
            }

            targets.Add(heartbeat);
        }

        private bool ShouldIncludeDiscoveredHeartbeat(UnityMcpHeartbeat heartbeat)
        {
            if (heartbeat == null)
            {
                return false;
            }

            if (!string.Equals(UnityMcpLanDiscoveryService.NormalizeGroup(heartbeat.name_group), UnityMcpLanDiscoveryService.NormalizeGroup(discoveryGroup), StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(heartbeat.source, "manual", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrEmpty(heartbeat.lastUpdatedUtc))
            {
                return true;
            }

            if (!DateTime.TryParse(heartbeat.lastUpdatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                return true;
            }

            return DateTime.UtcNow - parsed.ToUniversalTime() <= UnityMcpConstants.CommandResultRetention;
        }

        private UnityMcpCapabilities BuildCapabilities()
        {
            return new UnityMcpCapabilities
            {
                jsEval = settings == null || settings.allowJsEval,
                runtimeJsEval = settings == null || settings.allowJsEval,
                reflection = settings == null || settings.allowReflection,
                privateReflection = settings == null || settings.allowPrivateReflection,
                fileAccess = settings == null || settings.allowFileAccess,
                networkAccess = settings == null || settings.allowNetworkAccess,
                runtimeCodeLoad = settings == null || settings.allowRuntimeCodeLoad,
                screenshot = IsScreenshotEnabled(),
                uiClick = true,
                uiSnapshot = true,
                uiFind = true,
                uiRaycast = true,
                runtimeLogs = true,
                runtimeToolCall = true,
                fileCommandPump = commandPump != null,
                http = httpServer != null && httpServer.IsRunning
            };
        }

        private void ApplyEnvironmentOverrides()
        {
            var envTarget = Environment.GetEnvironmentVariable(TargetIdEnvironment);
            if (!string.IsNullOrEmpty(envTarget))
            {
                settings.targetId = envTarget;
            }

            var envPort = Environment.GetEnvironmentVariable(RuntimePortEnvironment);
            if (int.TryParse(envPort, out var port) && port > 0)
            {
                settings.httpPort = port;
            }
        }

        private string ResolveTargetId()
        {
            if (settings != null && !string.IsNullOrEmpty(settings.targetId))
            {
                return UnityMcpPaths.SanitizeId(settings.targetId);
            }

            var productName = string.IsNullOrEmpty(Application.productName) ? "player" : Application.productName;
            if (Application.isEditor)
            {
                return UnityMcpPaths.SanitizeId(productName + "_" + GetProcessId());
            }

            return UnityMcpPaths.SanitizeId(productName + "_" + Application.platform + "_" + ResolveDeviceIdComponent());
        }

        private static string ResolveDeviceIdComponent()
        {
            string raw = null;
            try
            {
                raw = SystemInfo.deviceUniqueIdentifier;
            }
            catch
            {
                raw = null;
            }

            if (!string.IsNullOrEmpty(raw) && !string.Equals(raw, SystemInfo.unsupportedIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                return "device_" + StableHash(raw);
            }

            var stored = PlayerPrefs.GetString(PlayerIdPrefsKey, string.Empty);
            if (string.IsNullOrEmpty(stored))
            {
                stored = "install_" + Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(PlayerIdPrefsKey, stored);
                PlayerPrefs.Save();
            }

            return stored;
        }

        private static string StableHash(string value)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                for (var i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 1099511628211UL;
                }

                return hash.ToString("x16");
            }
        }

        private int ResolvePort()
        {
            return settings == null || settings.httpPort <= 0 ? UnityMcpConstants.DefaultPlayerPort : settings.httpPort;
        }

        private static bool IsDisabledByEnvironment()
        {
            var value = Environment.GetEnvironmentVariable(DisableEnvironment);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string PrepareJavaScript(string code, string mode)
        {
            if (string.Equals(mode, "expression", StringComparison.OrdinalIgnoreCase))
            {
                return "return (" + (code ?? string.Empty) + ");";
            }

            return code ?? string.Empty;
        }

        private void MarkMainThreadTick()
        {
            Interlocked.Exchange(ref lastMainThreadTickUtcTicks, DateTime.UtcNow.Ticks);
        }

        private long GetLastMainThreadTickAgeMs()
        {
            var ticks = Interlocked.CompareExchange(ref lastMainThreadTickUtcTicks, 0L, 0L);
            if (ticks <= 0)
            {
                return long.MaxValue;
            }

            return Math.Max(0, (long)(DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalMilliseconds);
        }

        private static int GetProcessId()
        {
            try
            {
                return Process.GetCurrentProcess().Id;
            }
            catch
            {
                return 0;
            }
        }

        private bool NeedsPlayerStateDirectory()
        {
            return enableDiskHeartbeat
                || enableFileCommandPump
                || string.Equals(screenshotWriteMode, "file", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsScreenshotEnabled()
        {
            return !string.Equals(screenshotWriteMode, "disabled", StringComparison.OrdinalIgnoreCase);
        }

        private DateTime ResolveHeartbeatTimestamp()
        {
            if (!enableDiskHeartbeat || lastHeartbeatUtc == default(DateTime))
            {
                return DateTime.UtcNow;
            }

            return lastHeartbeatUtc;
        }

        private Task<string> CaptureScreenshotAsync(UnityMcpToolArguments args)
        {
            if (!IsScreenshotEnabled())
            {
                return Task.FromResult(UnityJson.ToJson(new ScreenshotResult
                {
                    success = false,
                    writeMode = "disabled",
                    error = "Runtime screenshot capture is disabled by config."
                }));
            }

            if (string.Equals(screenshotWriteMode, "file", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(CaptureScreenshotToFile(args));
            }

            var completion = new TaskCompletionSource<string>();
            try
            {
                StartCoroutine(CaptureScreenshotToMemoryCoroutine(completion));
            }
            catch (Exception ex)
            {
                completion.TrySetResult(UnityJson.ToJson(new ScreenshotResult
                {
                    success = false,
                    writeMode = "memory",
                    error = ex.GetType().Name + ": " + ex.Message
                }));
            }

            return completion.Task;
        }

        private string CaptureScreenshotToFile(UnityMcpToolArguments args)
        {
            var fileName = string.IsNullOrEmpty(args.fileName)
                ? "screenshot_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ".png"
                : Path.GetFileName(args.fileName);
            var artifacts = UnityMcpPaths.ArtifactsRoot(EndpointKind, EndpointId);
            Directory.CreateDirectory(artifacts);
            var path = Path.Combine(artifacts, fileName);
            ScreenCapture.CaptureScreenshot(path);
            return UnityJson.ToJson(new ScreenshotResult
            {
                success = true,
                writeMode = "file",
                artifactPath = path,
                mimeType = "image/png",
                note = "CaptureScreenshot writes after the current frame on some platforms."
            });
        }

        private IEnumerator CaptureScreenshotToMemoryCoroutine(TaskCompletionSource<string> completion)
        {
            yield return new WaitForEndOfFrame();

            Texture2D texture = null;
            try
            {
                var width = Math.Max(1, Screen.width);
                var height = Math.Max(1, Screen.height);
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                texture.Apply(false);
                var bytes = texture.EncodeToPNG();
                completion.TrySetResult(UnityJson.ToJson(new ScreenshotResult
                {
                    success = true,
                    writeMode = "memory",
                    mimeType = "image/png",
                    width = width,
                    height = height,
                    byteLength = bytes == null ? 0 : bytes.Length,
                    base64 = bytes == null ? null : Convert.ToBase64String(bytes),
                    note = "Captured in memory; no artifact file was written."
                }));
            }
            catch (Exception ex)
            {
                completion.TrySetResult(UnityJson.ToJson(new ScreenshotResult
                {
                    success = false,
                    writeMode = "memory",
                    error = ex.GetType().Name + ": " + ex.Message
                }));
            }
            finally
            {
                if (texture != null)
                {
                    Destroy(texture);
                }
            }
        }

        private static string BuildTransformPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            var stack = new System.Collections.Generic.Stack<string>();
            var current = transform;
            while (current != null)
            {
                stack.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", stack.ToArray());
        }

        [Serializable]
        private sealed class ScreenshotResult
        {
            public bool success;
            public string writeMode;
            public string artifactPath;
            public string mimeType;
            public int width;
            public int height;
            public int byteLength;
            public string base64;
            public string note;
            public string error;
        }

        [Serializable]
        private sealed class DiscoveryScanResult
        {
            public bool enabled;
            public string endpointKind;
            public string name;
            public string name_group;
            public int port;
        }
    }
}
