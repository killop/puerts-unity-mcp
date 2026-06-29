using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace PuertsUnityMcp.Editor
{
    internal sealed class UnityMcpEditorEndpoint : IUnityMcpEndpoint, IDisposable
    {
        private const int MaxPlayerHeartbeatDirectories = 64;
        private const int LanHttpProbeBatchSize = 64;
        private const int DefaultLanHttpProbeTimeoutMs = 1000;
        private static DateTime lastPlayerHeartbeatCleanupUtc;
        private readonly UnityMcpToolRegistry tools = new UnityMcpToolRegistry();
        private readonly OperationStore operations = new OperationStore();
        private PuertsScriptHost editorScriptHost;
        private readonly List<string> editorResourceScriptToolNames = new List<string>();
        private const string PackageName = "puerts-unity-mcp";
        private const string StartupSceneToolScript = "Editor/JavaScriptTools/editor-build-settings-startup-scene.mjs";
        private UnityMcpHttpServer httpServer;
        private CommandFilePump commandPump;
        private UnityMcpLanDiscoveryService discoveryService;
        private DateTime startedAtUtc;
        private DateTime lastHeartbeatUtc;
        private string discoveryName;
        private string discoveryGroup;

        public UnityMcpEditorEndpoint()
        {
            EndpointId = BuildEditorId();
            EndpointName = UnityMcpInstanceRegistry.GetProjectName();
            editorScriptHost = new PuertsScriptHost("editor:" + EndpointId);
            RegisterTools();
            commandPump = new CommandFilePump(this);
        }

        public string EndpointId { get; }
        public string EndpointKind => "editor";
        public string EndpointName { get; }
        public UnityMcpToolRegistry Tools => tools;
        public int Port => httpServer?.Port ?? UnityMcpEditorSettings.Port;
        public string Url => httpServer?.Url;
        public bool IsRunning => httpServer != null && httpServer.IsRunning;

        public void Start()
        {
            if (IsRunning)
            {
                return;
            }

            startedAtUtc = DateTime.UtcNow;
            var config = UnityMcpProjectConfigStore.LoadOrCreate();
            var bindAddress = string.IsNullOrEmpty(config.editorBindAddress) ? "127.0.0.1" : config.editorBindAddress;
            var startPort = config.editorPort <= 0 ? UnityMcpConstants.DefaultEditorPort : config.editorPort;
            for (var offset = 0; offset < 10; offset++)
            {
                var candidate = startPort + offset;
                UnityMcpHttpServer server = null;
                try
                {
                    server = new UnityMcpHttpServer(this, bindAddress, candidate);
                    server.Start();
                    httpServer = server;
                    UnityMcpEditorSettings.Port = candidate;
                    UnityMcpEditorSettings.WasRunning = true;
                    if (config.editorPort != candidate)
                    {
                        config.editorPort = candidate;
                        UnityMcpProjectConfigStore.Save(config);
                    }

                    WriteHeartbeat();
                    StartDiscovery(config);
                    return;
                }
                catch (Exception ex)
                {
                    server?.Dispose();
                    if (offset == 9)
                    {
                        Debug.LogError("[UnityMCP] Editor HTTP server failed: " + ex.Message);
                    }
                }
            }
        }

        public void Stop()
        {
            discoveryService?.Dispose();
            discoveryService = null;
            httpServer?.Dispose();
            httpServer = null;
        }

        public void Dispose()
        {
            Stop();
            editorScriptHost.Dispose();
            UnityMcpInstanceRegistry.Remove(EndpointId);
        }

        public void Tick()
        {
            editorScriptHost.Tick();
            commandPump?.Tick(4);
            discoveryService?.Tick();

            if ((DateTime.UtcNow - lastHeartbeatUtc).TotalMilliseconds >= UnityMcpConstants.HeartbeatIntervalMs)
            {
                WriteHeartbeat();
            }
        }

        public string BuildHealthJson()
        {
            return UnityJson.ToJson(BuildEditorState());
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

        private void RegisterTools()
        {
            tools.Register(new DelegateUnityMcpTool("mcp.info", "Return endpoint metadata and health.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(BuildHealthJson())));

            tools.Register(new DelegateUnityMcpTool("editor.state", "Return Unity Editor state.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(BuildEditorState()))));

            tools.Register(new DelegateUnityMcpTool("editor.buildSettings.startupScene", "Return the first scene configured in Unity Build Settings. Implemented by package JavaScript.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(ExecutePackageJavaScriptTool(StartupSceneToolScript, "mcp://editor/build-settings/startup-scene.mjs"))));

            tools.Register(new DelegateUnityMcpTool("editor.js.eval", "Execute PuerTS JavaScript in the Unity Editor VM without generating C# or triggering domain reload. Use CS.UnityEditor/CS.UnityEngine when wrapped; use __unity_mcp.invokeStatic(type, method, ...args), getStatic, getStaticPath, setStatic, or typeExists as reflection fallback. Return JSON-serializable data.", JsonSchemas.Object(
                JsonSchemas.StringProperty("code", "PuerTS JavaScript. In script mode use return; in expression mode provide a single expression. Prefer CS.* APIs, fallback to __unity_mcp for reflection."),
                JsonSchemas.StringProperty("mode", "script or expression."),
                JsonSchemas.StringProperty("chunkName", "Optional chunk name for stack traces.")), (ctx, args) =>
            {
                return Task.FromResult(editorScriptHost.Eval(PrepareJavaScript(args.code, args.mode), args.chunkName, true));
            }));

            tools.Register(new DelegateUnityMcpTool("editor.scriptTools.list", "List project JavaScript MCP tools loaded from puerts-unity-mcp-extension/Editor/editor-tools.", JsonSchemas.Object(), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(BuildEditorScriptToolListResult("editor.scriptTools.list")));
            }));

            tools.Register(new DelegateUnityMcpTool("editor.scriptTools.reload", "Reload project JavaScript MCP tools from puerts-unity-mcp-extension/Editor/editor-tools.", JsonSchemas.Object(), (ctx, args) =>
            {
                ResetEditorScriptHost();
                RegisterEditorResourceScriptTools();
                return Task.FromResult(UnityJson.ToJson(BuildEditorScriptToolListResult("editor.scriptTools.reload")));
            }));

            tools.Register(new DelegateUnityMcpTool("editor.skills.list", "List project skills loaded from puerts-unity-mcp-extension/skills.", JsonSchemas.Object(), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(BuildEditorSkillListResult("editor.skills.list")));
            }));

            tools.Register(new DelegateUnityMcpTool("editor.skill.load", "Load one project skill from puerts-unity-mcp-extension/skills by name.", JsonSchemas.Object(
                JsonSchemas.StringProperty("name")), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(BuildEditorSkillLoadResult(args.name)));
            }));

            tools.Register(new DelegateUnityMcpTool("editor.playmode.set", "Enter, exit, or toggle Editor Play Mode.", JsonSchemas.Object(
                JsonSchemas.StringProperty("state", "enter, exit, or toggle")), (ctx, args) =>
            {
                var state = args.state ?? "toggle";
                var targetIsPlaying = ResolvePlayModeTarget(state);
                EditorApplication.delayCall += () =>
                {
                    EditorApplication.isPlaying = targetIsPlaying;
                };

                return Task.FromResult(UnityJson.ToJson(new PlayModeRequestResult
                {
                    requestedState = state,
                    targetIsPlaying = targetIsPlaying,
                    isPlaying = EditorApplication.isPlaying,
                    isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode
                }));
            }));

            tools.Register(new DelegateUnityMcpTool("editor.playmode.state", "Return Editor Play Mode state.", JsonSchemas.Object(), (ctx, args) =>
            {
                return Task.FromResult(UnityJson.ToJson(new PlayModeRequestResult
                {
                    requestedState = "state",
                    targetIsPlaying = EditorApplication.isPlaying,
                    isPlaying = EditorApplication.isPlaying,
                    isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode
                }));
            }));

            tools.Register(new DelegateUnityMcpTool("editor.playmode.set.immediate", "Immediately enter, exit, or toggle Editor Play Mode.", JsonSchemas.Object(
                JsonSchemas.StringProperty("state", "enter, exit, or toggle")), (ctx, args) =>
            {
                var state = args.state ?? "toggle";
                var targetIsPlaying = ResolvePlayModeTarget(state);
                EditorApplication.isPlaying = targetIsPlaying;
                return Task.FromResult(UnityJson.ToJson(new PlayModeRequestResult
                {
                    requestedState = state,
                    targetIsPlaying = targetIsPlaying,
                    isPlaying = EditorApplication.isPlaying,
                    isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode
                }));
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.targets.list", "List local Play Mode and discovered Player MCP targets.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(ListRuntimeTargets()))));

            tools.Register(new DelegateUnityMcpTool("targets.list", "List local Editor, local Play Mode runtime, discovered LAN Editors, and discovered real Player targets.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(ListAllTargets()))));

            tools.Register(new DelegateUnityMcpTool("editor.targets.list", "List this Editor and discovered LAN Editor MCP targets in the same name_group.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(ListEditorTargets()))));

            tools.Register(new DelegateUnityMcpTool("lan.discovery.scan", "Broadcast a LAN discovery query and probe configured LAN Player MCP HTTP ranges in the same name_group.", JsonSchemas.Object(
                JsonSchemas.StringProperty("probeHosts", "Optional comma-separated host IPs to probe on the default player MCP port."),
                JsonSchemas.StringProperty("probeCidrs", "Optional comma-separated CIDRs to probe, for example 192.168.1.0/24."),
                JsonSchemas.NumberProperty("probeTimeoutMs", "Optional per-host HTTP probe timeout in milliseconds. Defaults to editor-mcp-config.json lanHttpProbeTimeoutMs or 1000.")), async (ctx, args) =>
            {
                discoveryService?.SendQuery();
                var config = UnityMcpProjectConfigStore.LoadOrCreate();
                var candidates = BuildLanHttpProbeUrls(config, args);
                var timeoutMs = ResolveLanHttpProbeTimeoutMs(config, args);
                var probeStats = await ProbeLanPlayerTargetsAsync(candidates, timeoutMs);
                return UnityJson.ToJson(new DiscoveryScanResult
                {
                    enabled = discoveryService != null && discoveryService.IsRunning,
                    name = discoveryName,
                    name_group = discoveryGroup,
                    port = UnityMcpConstants.DiscoveryPort,
                    httpProbeEnabled = true,
                    httpProbeCandidates = probeStats.candidates,
                    httpProbeTimeoutMs = probeStats.timeoutMs,
                    httpProbeTcpReachable = probeStats.tcpReachable,
                    httpProbeHealthOk = probeStats.healthOk,
                    httpProbeFound = probeStats.accepted,
                    httpProbeRejected = probeStats.rejected,
                    httpProbeTimeoutOrError = probeStats.timeoutOrError,
                    httpProbeFirstReachableUrl = probeStats.firstReachableUrl,
                    httpProbeFirstRejectedReason = probeStats.firstRejectedReason,
                    httpProbeHint = BuildLanHttpProbeHint(probeStats)
                });
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.js.eval", "Execute PuerTS JavaScript in local Play Mode or a remote Player/phone target. Use runtime-safe CS.UnityEngine APIs when wrapped; use __unity_mcp.invokeStatic(type, method, ...args), getStatic, getStaticPath, setStatic, or typeExists as reflection fallback. Return JSON-serializable data.", JsonSchemas.Object(
                JsonSchemas.StringProperty("targetId"),
                JsonSchemas.StringProperty("httpUrl"),
                JsonSchemas.StringProperty("code", "PuerTS JavaScript. In script mode use return; in expression mode provide a single expression. Prefer CS.* APIs, fallback to __unity_mcp for reflection."),
                JsonSchemas.StringProperty("mode", "script or expression."),
                JsonSchemas.StringProperty("chunkName", "Optional chunk name for stack traces.")), async (ctx, args) =>
            {
                if (string.IsNullOrEmpty(args.httpUrl) && IsLocalRuntimeTarget(args.targetId))
                {
                    var runtime = UnityMcpRuntimeHost.Instance;
                    if (runtime == null)
                    {
                        throw new InvalidOperationException("No local Play Mode runtime MCP host is active.");
                    }

                    return runtime.EvalJavaScript(args.code ?? string.Empty, args.mode ?? "script", args.chunkName);
                }

                return await CallRemotePlayerTool(args.targetId, args.httpUrl, "runtime.js.eval", args);
            }));

            tools.Register(new DelegateUnityMcpTool("runtime.tool.call", "Call a runtime MCP tool in local Play Mode or a remote Player target.", JsonSchemas.Object(
                JsonSchemas.StringProperty("targetId"),
                JsonSchemas.StringProperty("httpUrl"),
                JsonSchemas.StringProperty("toolName")), async (ctx, args) =>
            {
                if (string.IsNullOrEmpty(args.toolName))
                {
                    throw new InvalidOperationException("runtime.tool.call requires toolName.");
                }

                if (string.IsNullOrEmpty(args.httpUrl) && IsLocalRuntimeTarget(args.targetId))
                {
                    var runtime = UnityMcpRuntimeHost.Instance;
                    if (runtime == null)
                    {
                        throw new InvalidOperationException("No local Play Mode runtime MCP host is active.");
                    }

                    return await runtime.CallToolAsync(args.toolName, args);
                }

                return await CallRemotePlayerTool(args.targetId, args.httpUrl, args.toolName, args);
            }));

            tools.Register(new DelegateUnityMcpTool("editor.compile", "Trigger AssetDatabase.Refresh and persist compile result hints.", JsonSchemas.Object(
                JsonSchemas.StringProperty("requestId"),
                JsonSchemas.BooleanProperty("wait")), async (ctx, args) =>
            {
                return await TriggerCompile(args.requestId, args.wait);
            }));

            tools.Register(new DelegateUnityMcpTool("op.status", "Read a persisted operation state/result.", JsonSchemas.Object(
                JsonSchemas.StringProperty("operationId")), (ctx, args) =>
                Task.FromResult(operations.Read(args.operationId))));

            RegisterEditorResourceScriptTools();
        }

        private void RegisterEditorResourceScriptTools()
        {
            for (var i = 0; i < editorResourceScriptToolNames.Count; i++)
            {
                tools.Unregister(editorResourceScriptToolNames[i]);
            }

            editorResourceScriptToolNames.Clear();
            var manifests = UnityMcpResourceScriptTools.LoadManifests(UnityMcpPaths.EditorToolsRoot());
            for (var i = 0; i < manifests.Length; i++)
            {
                var manifest = manifests[i];
                if (manifest == null || string.IsNullOrEmpty(manifest.name))
                {
                    continue;
                }

                tools.Register(new DelegateUnityMcpTool(manifest.name, manifest.description, manifest.inputSchemaJson, (ctx, args) =>
                    Task.FromResult(ExecuteEditorResourceScriptTool(manifest, args))));
                editorResourceScriptToolNames.Add(manifest.name);
            }
        }

        private UnityMcpScriptToolListResult BuildEditorScriptToolListResult(string action)
        {
            var toolRoot = UnityMcpPaths.EditorToolsRoot();
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

        private UnityMcpSkillListResult BuildEditorSkillListResult(string action)
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

        private UnityMcpSkillLoadResult BuildEditorSkillLoadResult(string name)
        {
            var skillsRoot = UnityMcpPaths.SkillsRoot();
            var skill = UnityMcpResourceSkills.FindSkill(skillsRoot, name);
            return new UnityMcpSkillLoadResult
            {
                action = "editor.skill.load",
                targetId = EndpointId,
                resourceRoot = skillsRoot,
                directoryRoot = skillsRoot,
                success = skill != null,
                error = skill == null ? "Skill not found: " + (name ?? string.Empty) : null,
                skill = skill
            };
        }

        private string ExecuteEditorResourceScriptTool(UnityMcpScriptToolManifest manifest, UnityMcpToolArguments args)
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
            return editorScriptHost.ExecuteModuleFunctionJson(manifest.modulePath, manifest.functionName, argsJson, UnityJson.ToJson(context));
        }

        private void ResetEditorScriptHost()
        {
            editorScriptHost?.Dispose();
            editorScriptHost = new PuertsScriptHost("editor:" + EndpointId);
        }

        private UnityMcpHealth BuildEditorState()
        {
            return new UnityMcpHealth
            {
                status = IsRunning ? "ok" : "stopped",
                ready = !EditorApplication.isCompiling,
                endpointKind = EndpointKind,
                endpointId = EndpointId,
                endpointName = EndpointName,
                name_group = UnityMcpLanDiscoveryService.NormalizeGroup(discoveryGroup),
                projectRoot = UnityMcpPaths.ProjectRoot,
                stateRoot = UnityMcpPaths.StateRoot,
                httpUrl = httpServer == null ? null : httpServer.Url,
                httpPort = Port,
                unityVersion = Application.unityVersion,
                platform = "Editor",
                isEditor = true,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                timeSinceStartup = EditorApplication.timeSinceStartup,
                uptimeSeconds = (DateTime.UtcNow - startedAtUtc).TotalSeconds,
                toolRegistryVersion = tools.Version,
                capabilities = new UnityMcpCapabilities
                {
                    editorJsEval = true,
                    runtimeJsEval = true,
                    reflection = true,
                    privateReflection = true,
                    runtimeToolCall = true,
                    fileCommandPump = true,
                    domainReloadRecovery = true,
                    compileLocks = true,
                    http = IsRunning
                }
            };
        }

        private async Task<string> TriggerCompile(string requestId, bool wait)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                requestId = "compile_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + UnityEngine.Random.Range(100000, 999999);
            }

            requestId = UnityMcpPaths.SanitizeId(requestId);
            UnityMcpEditorSettings.ActiveCompileRequestId = requestId;
            UnityMcpEditorLocks.CreateCompilingLock();
            var compileArgs = new UnityMcpToolArguments { requestId = requestId, wait = wait };
            var operationId = operations.Create("editor.compile", EndpointId, compileArgs);
            operations.Update(operationId, "refresh_requested", null);

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            if (wait)
            {
                var started = DateTime.UtcNow;
                while ((EditorApplication.isCompiling || File.Exists(UnityMcpPaths.TempLockPath(UnityMcpConstants.DomainReloadLockName)))
                       && (DateTime.UtcNow - started).TotalSeconds < 120)
                {
                    await Task.Delay(200);
                }
            }

            var result = new CompileRequestResult
            {
                operationId = operationId,
                requestId = requestId,
                isCompiling = EditorApplication.isCompiling,
                compileResultPath = Path.Combine(UnityMcpPaths.CompileResultsRoot(), requestId + ".json")
            };
            var resultJson = UnityJson.ToJson(result);
            operations.Complete(operationId, true, resultJson);
            return resultJson;
        }

        public UnityMcpTargetList ListAllTargets()
        {
            var targets = new List<UnityMcpHeartbeat>();
            AppendTargets(targets, ListEditorTargets());
            AppendTargets(targets, ListRuntimeTargets());
            return new UnityMcpTargetList { targets = targets.ToArray() };
        }

        public UnityMcpTargetList ListRuntimeTargets()
        {
            var targets = new List<UnityMcpHeartbeat>();
            var local = UnityMcpRuntimeHost.Instance;
            if (local != null)
            {
                targets.Add(new UnityMcpHeartbeat
                {
                    endpointId = local.EndpointId,
                    endpointKind = local.EndpointKind,
                    endpointName = local.EndpointName,
                    projectRoot = UnityMcpPaths.ProjectRoot,
                    projectName = local.EndpointName,
                    name = UnityMcpLanDiscoveryService.ResolveName(discoveryName, local.EndpointName, local.EndpointId),
                    name_group = UnityMcpLanDiscoveryService.NormalizeGroup(discoveryGroup),
                    httpUrl = null,
                    port = 0,
                    unityVersion = Application.unityVersion,
                    platform = "EditorPlayMode",
                    isEditor = true,
                    lastUpdatedUtc = DateTime.UtcNow.ToString("o"),
                    source = "local-playmode",
                    capabilities = new UnityMcpCapabilities
                    {
                        runtimeJsEval = true,
                        reflection = true,
                        runtimeToolCall = true,
                        runtimeLogs = true,
                        uiClick = true,
                        uiSnapshot = true,
                        uiFind = true,
                        uiRaycast = true
                    }
                });
            }

            var playersRoot = Path.Combine(UnityMcpPaths.StateRoot, UnityMcpConstants.PlayersDirectoryName);
            if (Directory.Exists(playersRoot))
            {
                CleanupPlayerHeartbeatCache(playersRoot);
                foreach (var heartbeatPath in Directory.GetFiles(playersRoot, UnityMcpConstants.HeartbeatFileName, SearchOption.AllDirectories))
                {
                    if (AtomicFile.TryReadJson<UnityMcpHeartbeat>(heartbeatPath, out var heartbeat) && heartbeat != null)
                    {
                        if (!ShouldIncludePlayerHeartbeat(heartbeat))
                        {
                            continue;
                        }

                        if (heartbeat.isEditor)
                        {
                            continue;
                        }

                        if (local != null && string.Equals(heartbeat.endpointId, local.EndpointId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        targets.Add(heartbeat);
                    }
                }
            }

            return new UnityMcpTargetList { targets = targets.ToArray() };
        }

        public UnityMcpTargetList ListEditorTargets()
        {
            var targets = new List<UnityMcpHeartbeat>
            {
                BuildEditorHeartbeat()
            };

            var editorsRoot = Path.Combine(UnityMcpPaths.StateRoot, UnityMcpConstants.EditorsDirectoryName);
            if (Directory.Exists(editorsRoot))
            {
                foreach (var heartbeatPath in Directory.GetFiles(editorsRoot, UnityMcpConstants.HeartbeatFileName, SearchOption.AllDirectories))
                {
                    if (!AtomicFile.TryReadJson<UnityMcpHeartbeat>(heartbeatPath, out var heartbeat) || heartbeat == null)
                    {
                        continue;
                    }

                    if (string.Equals(heartbeat.endpointId, EndpointId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!ShouldIncludeDiscoveredHeartbeat(heartbeat))
                    {
                        continue;
                    }

                    targets.Add(heartbeat);
                }
            }

            return new UnityMcpTargetList { targets = targets.ToArray() };
        }

        private static void AppendTargets(List<UnityMcpHeartbeat> targets, UnityMcpTargetList source)
        {
            if (targets == null || source == null || source.targets == null)
            {
                return;
            }

            foreach (var heartbeat in source.targets)
            {
                if (heartbeat == null)
                {
                    continue;
                }

                var duplicate = false;
                foreach (var existing in targets)
                {
                    if (existing != null
                        && string.Equals(existing.endpointId, heartbeat.endpointId, StringComparison.Ordinal)
                        && string.Equals(existing.source, heartbeat.source, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    targets.Add(heartbeat);
                }
            }
        }

        private async Task<string> CallRemotePlayerTool(string targetId, string httpUrlOverride, string toolName, UnityMcpToolArguments arguments)
        {
            var heartbeat = FindPlayerHeartbeat(targetId);
            if (heartbeat == null && string.IsNullOrEmpty(httpUrlOverride))
            {
                throw new InvalidOperationException("Player target not found: " + targetId);
            }

            var httpUrl = string.IsNullOrEmpty(httpUrlOverride) ? heartbeat.httpUrl : httpUrlOverride;
            if (string.IsNullOrEmpty(httpUrl))
            {
                throw new InvalidOperationException("Player target has no httpUrl: " + targetId);
            }

            var requestId = Guid.NewGuid().ToString("N");
            var response = await PostJson(httpUrl.TrimEnd('/') + "/mcp", BuildRemoteToolCallJson(requestId, toolName, arguments));
            var parsed = UnityJson.FromJson<UnityMcpJsonRpcResponse>(response);
            if (parsed.error != null && !string.IsNullOrEmpty(parsed.error.message))
            {
                throw new InvalidOperationException(parsed.error.message);
            }

            var structuredJson = parsed.result == null ? null : parsed.result.structuredContentJson;
            if (string.IsNullOrEmpty(structuredJson))
            {
                structuredJson = ExtractRawJsonProperty(response, "structuredContent");
            }

            if (string.IsNullOrEmpty(structuredJson) && parsed.result != null)
            {
                structuredJson = parsed.result.valueJson;
            }

            return string.IsNullOrEmpty(structuredJson) ? "{}" : structuredJson;
        }

        private static string BuildRemoteToolCallJson(string requestId, string toolName, UnityMcpToolArguments arguments)
        {
            var argumentsJson = arguments == null || string.IsNullOrWhiteSpace(arguments.rawArgumentsJson)
                ? UnityJson.ToJson(arguments)
                : arguments.rawArgumentsJson.Trim();
            if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson[0] != '{')
            {
                argumentsJson = "{}";
            }

            return "{\"jsonrpc\":\"2.0\",\"id\":" + UnityJson.ToJsonStringValue(requestId)
                + ",\"method\":\"tools/call\",\"params\":{\"name\":" + UnityJson.ToJsonStringValue(toolName)
                + ",\"arguments\":" + argumentsJson + "}}";
        }

        private UnityMcpHeartbeat FindPlayerHeartbeat(string targetId)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                return null;
            }

            var playersRoot = Path.Combine(UnityMcpPaths.StateRoot, UnityMcpConstants.PlayersDirectoryName);
            if (!Directory.Exists(playersRoot))
            {
                return null;
            }

            CleanupPlayerHeartbeatCache(playersRoot);
            foreach (var heartbeatPath in Directory.GetFiles(playersRoot, UnityMcpConstants.HeartbeatFileName, SearchOption.AllDirectories))
            {
                if (!AtomicFile.TryReadJson<UnityMcpHeartbeat>(heartbeatPath, out var heartbeat) || heartbeat == null)
                {
                    continue;
                }

                if (!ShouldIncludePlayerHeartbeat(heartbeat))
                {
                    continue;
                }

                if (string.Equals(heartbeat.endpointId, targetId, StringComparison.Ordinal))
                {
                    return heartbeat;
                }
            }

            return null;
        }

        private bool IsLocalRuntimeTarget(string targetId)
        {
            var runtime = UnityMcpRuntimeHost.Instance;
            if (runtime == null)
            {
                return false;
            }

            return string.IsNullOrEmpty(targetId)
                || string.Equals(targetId, "playmode", StringComparison.OrdinalIgnoreCase)
                || string.Equals(targetId, runtime.EndpointId, StringComparison.Ordinal);
        }

        private static bool ShouldIncludePlayerHeartbeat(UnityMcpHeartbeat heartbeat)
        {
            return ShouldIncludeDiscoveredHeartbeat(heartbeat);
        }

        private static void CleanupPlayerHeartbeatCache(string playersRoot)
        {
            if (string.IsNullOrEmpty(playersRoot) || !Directory.Exists(playersRoot))
            {
                return;
            }

            if ((DateTime.UtcNow - lastPlayerHeartbeatCleanupUtc).TotalSeconds < 30)
            {
                return;
            }

            lastPlayerHeartbeatCleanupUtc = DateTime.UtcNow;
            var kept = new List<PlayerHeartbeatDirectory>();
            foreach (var directory in Directory.GetDirectories(playersRoot))
            {
                var heartbeatPath = Path.Combine(directory, UnityMcpConstants.HeartbeatFileName);
                if (!AtomicFile.TryReadJson<UnityMcpHeartbeat>(heartbeatPath, out var heartbeat) || heartbeat == null)
                {
                    TryDeleteDirectoryUnder(playersRoot, directory);
                    continue;
                }

                if (!string.Equals(heartbeat.source, "manual", StringComparison.OrdinalIgnoreCase)
                    && !ShouldIncludePlayerHeartbeat(heartbeat))
                {
                    TryDeleteDirectoryUnder(playersRoot, directory);
                    continue;
                }

                kept.Add(new PlayerHeartbeatDirectory
                {
                    directory = directory,
                    heartbeat = heartbeat,
                    lastUpdatedUtc = ParseHeartbeatTime(heartbeat.lastUpdatedUtc)
                });
            }

            kept.Sort((left, right) => right.lastUpdatedUtc.CompareTo(left.lastUpdatedUtc));
            var nonManualCount = 0;
            for (var i = 0; i < kept.Count; i++)
            {
                var heartbeat = kept[i].heartbeat;
                if (heartbeat != null && string.Equals(heartbeat.source, "manual", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                nonManualCount++;
                if (nonManualCount > MaxPlayerHeartbeatDirectories)
                {
                    TryDeleteDirectoryUnder(playersRoot, kept[i].directory);
                }
            }
        }

        private static DateTime ParseHeartbeatTime(string lastUpdatedUtc)
        {
            if (DateTime.TryParse(lastUpdatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                return parsed.ToUniversalTime();
            }

            return DateTime.MinValue;
        }

        private static void TryDeleteDirectoryUnder(string root, string directory)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(directory))
                {
                    return;
                }

                var normalizedRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var normalizedDirectory = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!normalizedDirectory.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Directory.Delete(directory, true);
            }
            catch
            {
            }
        }

        private static bool ShouldIncludeDiscoveredHeartbeat(UnityMcpHeartbeat heartbeat)
        {
            if (heartbeat == null)
            {
                return false;
            }

            if (!string.Equals(heartbeat.source, "manual", StringComparison.OrdinalIgnoreCase)
                && IsStaleHeartbeat(heartbeat.lastUpdatedUtc))
            {
                return false;
            }

            if (heartbeat.processId > 0 && IsLoopbackUrl(heartbeat.httpUrl) && !IsProcessAlive(heartbeat.processId))
            {
                return false;
            }

            return true;
        }

        private static bool IsStaleHeartbeat(string lastUpdatedUtc)
        {
            if (string.IsNullOrEmpty(lastUpdatedUtc))
            {
                return false;
            }

            if (!DateTime.TryParse(lastUpdatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                return false;
            }

            return DateTime.UtcNow - parsed.ToUniversalTime() > UnityMcpConstants.CommandResultRetention;
        }

        private static bool IsLoopbackUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                var process = System.Diagnostics.Process.GetProcessById(processId);
                return process != null && !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private sealed class PlayerHeartbeatDirectory
        {
            public string directory;
            public UnityMcpHeartbeat heartbeat;
            public DateTime lastUpdatedUtc;
        }

        private void WriteHeartbeat()
        {
            lastHeartbeatUtc = DateTime.UtcNow;
            var heartbeat = BuildEditorHeartbeat();
            AtomicFile.WriteJson(Path.Combine(UnityMcpPaths.EditorRoot(EndpointId), UnityMcpConstants.HeartbeatFileName), heartbeat);
            UnityMcpInstanceRegistry.Update(heartbeat);
        }

        private UnityMcpHeartbeat BuildEditorHeartbeat()
        {
            return new UnityMcpHeartbeat
            {
                endpointId = EndpointId,
                endpointKind = EndpointKind,
                endpointName = EndpointName,
                projectRoot = UnityMcpPaths.ProjectRoot,
                projectName = EndpointName,
                name = UnityMcpLanDiscoveryService.ResolveName(discoveryName, EndpointName, EndpointId),
                name_group = UnityMcpLanDiscoveryService.NormalizeGroup(discoveryGroup),
                processId = UnityMcpInstanceRegistry.GetProcessId(),
                httpUrl = httpServer == null ? null : httpServer.Url,
                port = Port,
                unityVersion = Application.unityVersion,
                platform = "Editor",
                isEditor = true,
                lastUpdatedUtc = DateTime.UtcNow.ToString("o"),
                source = "editor",
                capabilities = new UnityMcpCapabilities
                {
                    editorJsEval = true,
                    runtimeJsEval = true,
                    reflection = true,
                    runtimeToolCall = true,
                    domainReloadRecovery = true,
                    http = IsRunning
                }
            };
        }

        private void StartDiscovery(UnityMcpProjectConfig config)
        {
            discoveryService?.Dispose();
            discoveryService = null;
            discoveryName = UnityMcpLanDiscoveryService.ResolveName(config?.name, EndpointName, EndpointId);
            discoveryGroup = UnityMcpLanDiscoveryService.NormalizeGroup(config?.name_group);
            if (config == null || !config.lanDiscoveryEnabled)
            {
                return;
            }

            discoveryService = new UnityMcpLanDiscoveryService(BuildEditorHeartbeat, discoveryGroup);
        }

        private string BuildMcpUrl()
        {
            return string.IsNullOrEmpty(Url) ? null : Url.TrimEnd('/') + "/mcp";
        }

        private static async Task<string> PostJson(string url, string json)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = UnityMcpConstants.DefaultCommandTimeoutMs;
            var bytes = Encoding.UTF8.GetBytes(json);
            using (var stream = await request.GetRequestStreamAsync())
            {
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }

            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return await reader.ReadToEndAsync();
            }
        }

        private async Task<LanHttpProbeStats> ProbeLanPlayerTargetsAsync(List<string> urls, int timeoutMs)
        {
            var stats = new LanHttpProbeStats
            {
                candidates = urls == null ? 0 : urls.Count,
                timeoutMs = timeoutMs
            };

            if (urls == null || urls.Count == 0)
            {
                return stats;
            }

            for (var offset = 0; offset < urls.Count; offset += LanHttpProbeBatchSize)
            {
                var batchCount = Math.Min(LanHttpProbeBatchSize, urls.Count - offset);
                var tasks = new Task<LanHttpProbeResult>[batchCount];
                for (var i = 0; i < batchCount; i++)
                {
                    tasks[i] = TryProbePlayerHealthAsync(urls[offset + i], timeoutMs);
                }

                var results = await Task.WhenAll(tasks);
                for (var i = 0; i < results.Length; i++)
                {
                    var result = results[i];
                    if (result == null)
                    {
                        continue;
                    }

                    if (result.tcpReachable)
                    {
                        stats.tcpReachable++;
                        if (string.IsNullOrEmpty(stats.firstReachableUrl))
                        {
                            stats.firstReachableUrl = result.healthUrl;
                        }
                    }

                    if (result.healthOk)
                    {
                        stats.healthOk++;
                    }

                    if (result.heartbeat != null)
                    {
                        AtomicFile.WriteJson(Path.Combine(UnityMcpPaths.PlayerRoot(result.heartbeat.endpointId), UnityMcpConstants.HeartbeatFileName), result.heartbeat);
                        stats.accepted++;
                        continue;
                    }

                    if (IsLanHttpProbeRejection(result.reason))
                    {
                        stats.rejected++;
                        if (string.IsNullOrEmpty(stats.firstRejectedReason))
                        {
                            stats.firstRejectedReason = result.reason;
                        }
                    }
                    else
                    {
                        stats.timeoutOrError++;
                    }
                }
            }

            if (stats.accepted > 0)
            {
                Debug.Log("[UnityMCP] LAN HTTP probe found player endpoints. count=" + stats.accepted + ".");
            }
            else
            {
                Debug.Log("[UnityMCP] LAN HTTP probe found no matching player endpoints. candidates="
                    + stats.candidates
                    + ", tcpReachable=" + stats.tcpReachable
                    + ", healthOk=" + stats.healthOk
                    + ", rejected=" + stats.rejected
                    + ", timeoutOrError=" + stats.timeoutOrError
                    + ".");
            }

            return stats;
        }

        private async Task<LanHttpProbeResult> TryProbePlayerHealthAsync(string healthUrl, int timeoutMs)
        {
            var result = new LanHttpProbeResult
            {
                healthUrl = healthUrl
            };

            if (string.IsNullOrEmpty(healthUrl))
            {
                result.reason = "empty_url";
                return result;
            }

            try
            {
                if (!Uri.TryCreate(healthUrl, UriKind.Absolute, out var uri))
                {
                    result.reason = "invalid_url";
                    return result;
                }

                if (!await CanConnectAsync(uri.Host, uri.Port, timeoutMs))
                {
                    result.reason = "connect_failed";
                    return result;
                }

                result.tcpReachable = true;
                var json = await GetJsonWithTimeout(healthUrl, timeoutMs);
                if (string.IsNullOrEmpty(json))
                {
                    result.reason = "health_timeout";
                    return result;
                }

                result.healthOk = true;

                UnityMcpHealth health;
                try
                {
                    health = UnityJson.FromJson<UnityMcpHealth>(json);
                }
                catch
                {
                    result.reason = "invalid_health_json";
                    return result;
                }

                if (health == null
                    || string.IsNullOrEmpty(health.endpointId))
                {
                    result.reason = "missing_endpoint";
                    return result;
                }

                if (health.isEditor)
                {
                    result.reason = "editor_endpoint";
                    return result;
                }

                if (string.Equals(health.endpointId, EndpointId, StringComparison.Ordinal))
                {
                    result.reason = "self_endpoint";
                    return result;
                }

                if (!string.IsNullOrEmpty(health.name_group)
                    && !string.Equals(UnityMcpLanDiscoveryService.NormalizeGroup(health.name_group), UnityMcpLanDiscoveryService.NormalizeGroup(discoveryGroup), StringComparison.Ordinal))
                {
                    result.reason = "name_group_mismatch";
                    return result;
                }

                var endpointKind = string.IsNullOrEmpty(health.endpointKind) ? "player" : health.endpointKind;
                if (string.Equals(endpointKind, "editor", StringComparison.OrdinalIgnoreCase))
                {
                    result.reason = "editor_endpoint";
                    return result;
                }

                var endpointUrl = healthUrl.EndsWith("/health", StringComparison.OrdinalIgnoreCase)
                    ? healthUrl.Substring(0, healthUrl.Length - "/health".Length)
                    : healthUrl.TrimEnd('/');
                result.heartbeat = new UnityMcpHeartbeat
                {
                    endpointId = UnityMcpPaths.SanitizeId(health.endpointId),
                    endpointKind = endpointKind,
                    endpointName = health.endpointName,
                    projectRoot = health.projectRoot,
                    projectName = health.productName,
                    name = UnityMcpLanDiscoveryService.ResolveName(health.endpointName, health.productName, health.endpointId),
                    name_group = UnityMcpLanDiscoveryService.NormalizeGroup(string.IsNullOrEmpty(health.name_group) ? discoveryGroup : health.name_group),
                    httpUrl = endpointUrl,
                    port = health.httpPort > 0 ? health.httpPort : UnityMcpConstants.DefaultPlayerPort,
                    unityVersion = health.unityVersion,
                    platform = health.platform,
                    isEditor = false,
                    lastUpdatedUtc = DateTime.UtcNow.ToString("o"),
                    capabilities = health.capabilities ?? new UnityMcpCapabilities(),
                    source = UnityMcpConstants.DiscoverySource
                };
                result.reason = "accepted";
                return result;
            }
            catch (Exception ex)
            {
                result.reason = "probe_error";
                result.error = ex.Message;
                return result;
            }
        }

        private static bool IsLanHttpProbeRejection(string reason)
        {
            return !string.IsNullOrEmpty(reason)
                && !string.Equals(reason, "connect_failed", StringComparison.Ordinal)
                && !string.Equals(reason, "health_timeout", StringComparison.Ordinal)
                && !string.Equals(reason, "probe_error", StringComparison.Ordinal);
        }

        private static int ResolveLanHttpProbeTimeoutMs(UnityMcpProjectConfig config, UnityMcpToolArguments args)
        {
            var value = args != null && args.probeTimeoutMs > 0
                ? args.probeTimeoutMs
                : config != null && config.lanHttpProbeTimeoutMs > 0
                    ? config.lanHttpProbeTimeoutMs
                    : DefaultLanHttpProbeTimeoutMs;
            return Math.Max(100, Math.Min(value, 10000));
        }

        private static string BuildLanHttpProbeHint(LanHttpProbeStats stats)
        {
            if (stats == null)
            {
                return string.Empty;
            }

            if (stats.accepted > 0)
            {
                return "LAN HTTP probe found matching Player MCP endpoints.";
            }

            if (stats.tcpReachable > 0 && stats.healthOk == 0)
            {
                return "TCP reached at least one host, but /health did not respond in time. Check mobile MCP startup, port, game main-thread responsiveness, and whether the port belongs to the current MCP server.";
            }

            if (stats.healthOk > 0 && stats.rejected > 0)
            {
                return "HTTP /health responded, but no endpoint matched this name_group or target selector. Check name_group and selected target settings.";
            }

            return "No TCP/HTTP Player MCP response was found. UDP broadcast/multicast may be blocked by firewall, AP isolation, VLAN routing, or network policy; configure lanHttpProbeHosts/lanHttpProbeCidrs or use a direct target URL.";
        }

        private static async Task<bool> CanConnectAsync(string host, int port, int timeoutMs)
        {
            if (string.IsNullOrEmpty(host) || port <= 0)
            {
                return false;
            }

            var client = new System.Net.Sockets.TcpClient();
            try
            {
                var connectTask = client.ConnectAsync(host, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
                if (completed != connectTask)
                {
                    return false;
                }

                await connectTask;
                return client.Connected;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private static async Task<string> GetJsonWithTimeout(string url, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;

            try
            {
                var responseTask = request.GetResponseAsync();
                var completed = await Task.WhenAny(responseTask, Task.Delay(timeoutMs));
                if (completed != responseTask)
                {
                    request.Abort();
                    return null;
                }

                using (var response = (HttpWebResponse)await responseTask)
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return null;
                    }

                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        return await reader.ReadToEndAsync();
                    }
                }
            }
            catch
            {
                try { request.Abort(); } catch { }
                return null;
            }
        }

        private static List<string> BuildLanHttpProbeUrls(UnityMcpProjectConfig config, UnityMcpToolArguments args)
        {
            var hosts = BuildLanHttpProbeHosts();
            AddProbeHosts(hosts, config == null ? null : config.lanHttpProbeHosts);
            AddProbeCidrs(hosts, config == null ? null : config.lanHttpProbeCidrs);
            AddProbeHosts(hosts, SplitProbeList(args == null ? null : args.probeHosts));
            AddProbeCidrs(hosts, SplitProbeList(args == null ? null : args.probeCidrs));
            var urls = new List<string>(hosts.Count);
            for (var i = 0; i < hosts.Count; i++)
            {
                urls.Add("http://" + hosts[i] + ":" + UnityMcpConstants.DefaultPlayerPort + "/health");
            }

            return urls;
        }

        private static List<string> BuildLanHttpProbeHosts()
        {
            var hosts = new List<string>();
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch
            {
                return hosts;
            }

            for (var i = 0; i < interfaces.Length; i++)
            {
                var networkInterface = interfaces[i];
                if (networkInterface == null || networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                IPInterfaceProperties properties;
                try
                {
                    properties = networkInterface.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                var unicastAddresses = properties.UnicastAddresses;
                for (var j = 0; j < unicastAddresses.Count; j++)
                {
                    var unicast = unicastAddresses[j];
                    if (unicast == null
                        || unicast.Address == null
                        || unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(unicast.Address))
                    {
                        continue;
                    }

                    AddProbeHostsForClassC(hosts, unicast.Address);
                }
            }

            return hosts;
        }

        private static void AddProbeHosts(List<string> hosts, string[] values)
        {
            if (hosts == null || values == null)
            {
                return;
            }

            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                value = value.Trim();
                if (IPAddress.TryParse(value, out var address)
                    && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address)
                    && !ContainsString(hosts, value))
                {
                    hosts.Add(value);
                }
            }
        }

        private static void AddProbeCidrs(List<string> hosts, string[] values)
        {
            if (hosts == null || values == null)
            {
                return;
            }

            for (var i = 0; i < values.Length; i++)
            {
                AddProbeHostsForCidr(hosts, values[i]);
            }
        }

        private static string[] SplitProbeList(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return new string[0];
            }

            return value.Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static void AddProbeHostsForClassC(List<string> hosts, IPAddress localAddress)
        {
            var bytes = localAddress.GetAddressBytes();
            if (bytes.Length != 4)
            {
                return;
            }

            for (var host = 1; host <= 254; host++)
            {
                if (host == bytes[3])
                {
                    continue;
                }

                var candidate = bytes[0] + "." + bytes[1] + "." + bytes[2] + "." + host;
                if (!ContainsString(hosts, candidate))
                {
                    hosts.Add(candidate);
                }
            }
        }

        private static void AddProbeHostsForCidr(List<string> hosts, string cidr)
        {
            if (hosts == null || string.IsNullOrEmpty(cidr))
            {
                return;
            }

            var parts = cidr.Trim().Split('/');
            if (parts.Length != 2
                || !IPAddress.TryParse(parts[0], out var address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || !int.TryParse(parts[1], out var prefixLength)
                || prefixLength < 0
                || prefixLength > 32)
            {
                return;
            }

            var addressValue = ToUInt32(address);
            var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
            var network = addressValue & mask;
            var broadcast = network | ~mask;
            if (broadcast <= network + 1)
            {
                return;
            }

            var hostCount = broadcast - network - 1;
            if (hostCount > 4096)
            {
                Debug.LogWarning("[UnityMCP] LAN HTTP probe CIDR skipped because it is too large: " + cidr + ". Use /20 or smaller ranges.");
                return;
            }

            for (var value = network + 1; value < broadcast; value++)
            {
                var candidate = FromUInt32(value);
                if (!ContainsString(hosts, candidate))
                {
                    hosts.Add(candidate);
                }
            }
        }

        private static uint ToUInt32(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            return ((uint)bytes[0] << 24)
                | ((uint)bytes[1] << 16)
                | ((uint)bytes[2] << 8)
                | bytes[3];
        }

        private static string FromUInt32(uint value)
        {
            return ((value >> 24) & 255) + "."
                + ((value >> 16) & 255) + "."
                + ((value >> 8) & 255) + "."
                + (value & 255);
        }

        private static bool ContainsString(List<string> values, string value)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string PrepareJavaScript(string code, string mode)
        {
            if (string.Equals(mode, "expression", StringComparison.OrdinalIgnoreCase))
            {
                return "return (" + (code ?? string.Empty) + ");";
            }

            return code ?? string.Empty;
        }

        private static string ExtractRawJsonProperty(string json, string propertyName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            var key = "\"" + propertyName + "\"";
            var keyIndex = json.IndexOf(key, StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                return null;
            }

            var colonIndex = json.IndexOf(':', keyIndex + key.Length);
            if (colonIndex < 0)
            {
                return null;
            }

            var start = colonIndex + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start]))
            {
                start++;
            }

            if (start >= json.Length)
            {
                return null;
            }

            var end = FindRawJsonValueEnd(json, start);
            if (end <= start)
            {
                return null;
            }

            return json.Substring(start, end - start).Trim();
        }

        private static int FindRawJsonValueEnd(string json, int start)
        {
            var first = json[start];
            if (first == '"' )
            {
                return FindJsonStringEnd(json, start);
            }

            if (first == '{' || first == '[')
            {
                var depth = 0;
                var inString = false;
                var escaped = false;
                for (var i = start; i < json.Length; i++)
                {
                    var ch = json[i];
                    if (inString)
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (ch == '\\')
                        {
                            escaped = true;
                        }
                        else if (ch == '"')
                        {
                            inString = false;
                        }
                        continue;
                    }

                    if (ch == '"')
                    {
                        inString = true;
                    }
                    else if (ch == '{' || ch == '[')
                    {
                        depth++;
                    }
                    else if (ch == '}' || ch == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            return i + 1;
                        }
                    }
                }

                return json.Length;
            }

            var end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']')
            {
                end++;
            }

            return end;
        }

        private static int FindJsonStringEnd(string json, int start)
        {
            var escaped = false;
            for (var i = start + 1; i < json.Length; i++)
            {
                var ch = json[i];
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    return i + 1;
                }
            }

            return json.Length;
        }

        private string ExecutePackageJavaScriptTool(string relativePath, string chunkName)
        {
            var script = LoadPackageText(relativePath);
            var result = editorScriptHost.Eval(script, chunkName, false);
            return string.IsNullOrEmpty(result) ? "{}" : result;
        }

        private static string LoadPackageText(string relativePath)
        {
            var packageRoot = ResolvePackageRoot();
            var normalizedRelativePath = (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
            var scriptPath = Path.Combine(packageRoot, normalizedRelativePath);
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException("Package JavaScript tool file was not found.", scriptPath);
            }

            return File.ReadAllText(scriptPath, Encoding.UTF8);
        }

        private static string ResolvePackageRoot()
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + PackageName + "/package.json");
            if (packageInfo != null && !string.IsNullOrEmpty(packageInfo.resolvedPath))
            {
                return packageInfo.resolvedPath;
            }

            return Path.GetFullPath("Packages/" + PackageName);
        }

        private static bool ResolvePlayModeTarget(string state)
        {
            if (string.Equals(state, "enter", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(state, "exit", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !EditorApplication.isPlaying;
        }

        private static string BuildEditorId()
        {
            var root = UnityMcpPaths.ProjectRoot ?? Application.dataPath;
            using (var sha1 = SHA1.Create())
            {
                var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()));
                var builder = new StringBuilder();
                for (var i = 0; i < 6 && i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }

                return UnityMcpPaths.SanitizeId(UnityMcpInstanceRegistry.GetProjectName() + "_" + builder);
            }
        }

        [Serializable]
        private sealed class CompileRequestResult
        {
            public string operationId;
            public string requestId;
            public bool isCompiling;
            public string compileResultPath;
        }

        [Serializable]
        private sealed class PlayModeRequestResult
        {
            public string requestedState;
            public bool targetIsPlaying;
            public bool isPlaying;
            public bool isPlayingOrWillChangePlaymode;
        }

        private sealed class LanHttpProbeStats
        {
            public int candidates;
            public int timeoutMs;
            public int tcpReachable;
            public int healthOk;
            public int accepted;
            public int rejected;
            public int timeoutOrError;
            public string firstReachableUrl;
            public string firstRejectedReason;
        }

        private sealed class LanHttpProbeResult
        {
            public string healthUrl;
            public bool tcpReachable;
            public bool healthOk;
            public UnityMcpHeartbeat heartbeat;
            public string reason;
            public string error;
        }

        [Serializable]
        private sealed class DiscoveryScanResult
        {
            public bool enabled;
            public string name;
            public string name_group;
            public int port;
            public bool httpProbeEnabled;
            public int httpProbeCandidates;
            public int httpProbeTimeoutMs;
            public int httpProbeTcpReachable;
            public int httpProbeHealthOk;
            public int httpProbeFound;
            public int httpProbeRejected;
            public int httpProbeTimeoutOrError;
            public string httpProbeFirstReachableUrl;
            public string httpProbeFirstRejectedReason;
            public string httpProbeHint;
        }
    }
}
