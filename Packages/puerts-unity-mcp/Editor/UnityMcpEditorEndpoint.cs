using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace PuertsUnityMcp.Editor
{
    internal sealed partial class UnityMcpEditorEndpoint : IUnityMcpEndpoint, IDisposable
    {
        private readonly UnityMcpToolRegistry tools = new UnityMcpToolRegistry();
        private readonly OperationStore operations = new OperationStore();
        private PuertsScriptHost editorScriptHost;
        private readonly List<string> editorResourceScriptToolNames = new List<string>();
        private const string PackageName = "puerts-unity-mcp";
        private const string StartupSceneToolScript = "Editor/JavaScriptTools/editor-build-settings-startup-scene.mjs";
        private UnityMcpHttpServer httpServer;
        private CommandFilePump commandPump;
        private DateTime startedAtUtc;
        private DateTime lastHeartbeatUtc;
        private string endpointDisplayName;

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

                    endpointDisplayName = UnityMcpConstants.ResolveEndpointName(config.name, EndpointName, EndpointId);
                    WriteHeartbeat();
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

            tools.Register(new DelegateUnityMcpTool("runtime.targets.list", "List local Play Mode runtime and the configured direct Player target, if selectedTargetUrl is set.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(ListRuntimeTargets()))));

            tools.Register(new DelegateUnityMcpTool("targets.list", "List local Editor, local Play Mode runtime, and configured direct remote targets.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(ListAllTargets()))));

            tools.Register(new DelegateUnityMcpTool("editor.targets.list", "List this Editor MCP endpoint and the configured direct remote Editor target, if selectedTargetUrl is set.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(ListEditorTargets()))));

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

            RegisterEditorSceneAndWindowTools();
            RegisterPerformanceTools();
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

            var compileResultPath = Path.Combine(UnityMcpPaths.CompileResultsRoot(), requestId + ".json");
            if (!EditorApplication.isCompiling && !File.Exists(compileResultPath))
            {
                AtomicFile.WriteJson(compileResultPath, new CompileResultRecord
                {
                    requestId = requestId,
                    success = true,
                    completedAtUtc = DateTime.UtcNow.ToString("o"),
                    compilerMessages = new CompileMessageRecord[0]
                });
                UnityMcpEditorSettings.ActiveCompileRequestId = string.Empty;
                UnityMcpEditorLocks.DeleteCompilingLock();
            }

            var result = new CompileRequestResult
            {
                operationId = operationId,
                requestId = requestId,
                isCompiling = EditorApplication.isCompiling,
                compileResultPath = compileResultPath
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
                    name = UnityMcpConstants.ResolveEndpointName(null, local.EndpointName, local.EndpointId),
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

            var configured = BuildConfiguredDirectTarget("player");
            if (configured != null)
            {
                targets.Add(configured);
            }

            return new UnityMcpTargetList { targets = targets.ToArray() };
        }

        public UnityMcpTargetList ListEditorTargets()
        {
            var targets = new List<UnityMcpHeartbeat>
            {
                BuildEditorHeartbeat()
            };

            var configured = BuildConfiguredDirectTarget("editor");
            if (configured != null && !string.Equals(configured.endpointId, EndpointId, StringComparison.Ordinal))
            {
                targets.Add(configured);
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
            var heartbeat = FindConfiguredDirectTarget(targetId, "player");
            if (heartbeat == null && string.IsNullOrEmpty(httpUrlOverride))
            {
                throw new InvalidOperationException("Remote Player target requires an explicit httpUrl or selectedTargetUrl in editor-mcp-config.json.");
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

        private UnityMcpHeartbeat FindConfiguredDirectTarget(string targetId, string endpointKind)
        {
            var target = BuildConfiguredDirectTarget(endpointKind);
            if (target == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(targetId)
                || string.Equals(target.endpointId, targetId, StringComparison.Ordinal))
            {
                return target;
            }

            return null;
        }

        private UnityMcpHeartbeat BuildConfiguredDirectTarget(string endpointKind)
        {
            var config = UnityMcpProjectConfigStore.Load();
            if (config == null || string.IsNullOrEmpty(config.selectedTargetUrl))
            {
                return null;
            }

            var selectedKind = string.IsNullOrEmpty(config.selectedTargetKind) ? "editor" : config.selectedTargetKind;
            if (!string.Equals(selectedKind, endpointKind, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var endpointId = string.IsNullOrEmpty(config.selectedTargetId)
                ? UnityMcpPaths.SanitizeId(selectedKind + "_" + config.selectedTargetUrl)
                : UnityMcpPaths.SanitizeId(config.selectedTargetId);
            var isEditor = string.Equals(selectedKind, "editor", StringComparison.OrdinalIgnoreCase);
            return new UnityMcpHeartbeat
            {
                endpointId = endpointId,
                endpointKind = isEditor ? "editor" : "player",
                endpointName = config.selectedTargetName,
                projectRoot = string.Empty,
                projectName = config.selectedTargetName,
                name = UnityMcpConstants.ResolveEndpointName(config.selectedTargetName, config.name, endpointId),
                httpUrl = config.selectedTargetUrl.TrimEnd('/'),
                port = TryGetPort(config.selectedTargetUrl, isEditor ? UnityMcpConstants.DefaultEditorPort : UnityMcpConstants.DefaultPlayerPort),
                unityVersion = string.Empty,
                platform = isEditor ? "RemoteEditor" : "RemotePlayer",
                isEditor = isEditor,
                lastUpdatedUtc = DateTime.UtcNow.ToString("o"),
                source = "configured-direct",
                capabilities = new UnityMcpCapabilities
                {
                    editorJsEval = isEditor,
                    runtimeJsEval = !isEditor,
                    runtimeToolCall = !isEditor,
                    reflection = true,
                    http = true
                }
            };
        }

        private static int TryGetPort(string httpUrl, int fallback)
        {
            if (Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }

            return fallback;
        }

        private static bool ContainsString(List<string> values, string value)
        {
            if (values == null)
            {
                return false;
            }

            for (var i = 0; i < values.Count; i++)
            {
                if (string.Equals(values[i], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
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
                name = UnityMcpConstants.ResolveEndpointName(endpointDisplayName, EndpointName, EndpointId),
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
        private sealed class CompileResultRecord
        {
            public string requestId;
            public bool success;
            public string completedAtUtc;
            public CompileMessageRecord[] compilerMessages = new CompileMessageRecord[0];
        }

        [Serializable]
        private sealed class CompileMessageRecord
        {
            public string assemblyPath;
            public string message;
            public string file;
            public int line;
            public int column;
            public string type;
        }

        [Serializable]
        private sealed class PlayModeRequestResult
        {
            public string requestedState;
            public bool targetIsPlaying;
            public bool isPlaying;
            public bool isPlayingOrWillChangePlaymode;
        }

    }
}
