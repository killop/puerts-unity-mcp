using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PuertsUnityMcp.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace PuertsUnityMcp.Tests
{
    public sealed class UnityMcpProtocolTests
    {
        [Test]
        public void ToolsListSuccessDoesNotSerializeUnusedResponseFields()
        {
            var endpoint = new FakeEndpoint();
            endpoint.Tools.Register(new DelegateUnityMcpTool("z.tool", "Test tool.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult("{\"ok\":true}")));

            var response = Handle(endpoint, "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"tools/list\",\"params\":{}}");

            StringAssert.Contains("\"jsonrpc\":\"2.0\"", response);
            StringAssert.Contains("\"tools\":[", response);
            StringAssert.Contains("\"name\":\"z.tool\"", response);
            StringAssert.Contains("\"inputSchema\":{", response);
            Assert.False(response.Contains("\"error\""), response);
            Assert.False(response.Contains("\"serverInfo\""), response);
            Assert.False(response.Contains("\"inputSchemaJson\""), response);
            Assert.False(response.Contains("\"structuredContentJson\""), response);
        }

        [Test]
        public void ToolCallSuccessCarriesStructuredContent()
        {
            var endpoint = new FakeEndpoint();
            endpoint.Tools.Register(new DelegateUnityMcpTool("echo.tool", "Echo.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult("{\"echo\":\"ok\"}")));

            var response = Handle(endpoint, "{\"jsonrpc\":\"2.0\",\"id\":\"2\",\"method\":\"tools/call\",\"params\":{\"name\":\"echo.tool\",\"arguments\":{}}}");

            StringAssert.Contains("\"content\":[", response);
            StringAssert.Contains("\"structuredContent\":{\"echo\":\"ok\"}", response);
            Assert.False(response.Contains("\"structuredContentJson\""), response);
            Assert.False(response.Contains("\"valueJson\""), response);
            Assert.False(response.Contains("\"error\""), response);
            Assert.False(response.Contains("\"tools\""), response);
        }

        [Test]
        public void ErrorResponseDoesNotSerializeResult()
        {
            var response = Handle(new FakeEndpoint(), "{\"jsonrpc\":\"2.0\",\"id\":\"bad\",\"params\":{}}");

            StringAssert.Contains("\"error\":", response);
            StringAssert.Contains("\"code\":-32600", response);
            Assert.False(response.Contains("\"result\""), response);
        }

        [Test]
        public void InitializeResponseHasServerInfoAndNoError()
        {
            var response = Handle(new FakeEndpoint(), "{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"test-protocol\"}}");

            StringAssert.Contains("\"protocolVersion\":\"test-protocol\"", response);
            StringAssert.Contains("\"serverInfo\":", response);
            StringAssert.Contains("\"endpointKind\":\"editor\"", response);
            StringAssert.Contains("\"listChanged\":true", response);
            StringAssert.Contains("\"instructions\":", response);
            StringAssert.Contains("editor.js.eval", response);
            StringAssert.Contains("runtime.js.eval", response);
            StringAssert.Contains("__unity_mcp", response);
            StringAssert.Contains("screen.screenshot", response);
            StringAssert.Contains("puerts-unity-mcp-extension", response);
            Assert.False(response.Contains("\"error\""), response);
        }

        [Test]
        public void StdioProxyBackfillsAgentInstructionsForOlderEndpoints()
        {
            var packageRoot = ResolvePackageRootForTest();
            var proxy = File.ReadAllText(Path.Combine(packageRoot, "Tools~", "puerts-unity-mcp-stdio-proxy.js"));

            StringAssert.Contains("const AGENT_INSTRUCTIONS", proxy);
            StringAssert.Contains("response.result.serverInfo && !response.result.instructions", proxy);
            StringAssert.Contains("editor.js.eval", proxy);
            StringAssert.Contains("runtime.js.eval", proxy);
            StringAssert.Contains("__unity_mcp", proxy);
            StringAssert.Contains("screen.screenshot", proxy);
        }

        [Test]
        public void StdioProxyDefaultsToLocalEditorUnlessRemoteTargetIsExplicit()
        {
            var packageRoot = ResolvePackageRootForTest();
            var proxy = File.ReadAllText(Path.Combine(packageRoot, "Tools~", "puerts-unity-mcp-stdio-proxy.js"));

            StringAssert.Contains("const explicitEditorSelection = selector.kind === \"editor\"", proxy);
            StringAssert.Contains("if (selector.kind === \"editor\" && !explicitEditorSelection)", proxy);
            StringAssert.Contains("resolveFromInstances(stateRoot, unityProjectPath)", proxy);
            StringAssert.Contains("Set selectedTargetId, selectedTargetName, selectedTargetUrl", proxy);
            StringAssert.Contains("if (selector.kind === \"editor\" && explicitEditorSelection)", proxy);
            Assert.False(proxy.Contains("const fallbackPlayer = await resolvePlayerEndpoint(stateRoot, selector, config);"));
        }

        [Test]
        public void EditorDiscoveryDoesNotPersistLanEditorHeartbeats()
        {
            var packageRoot = ResolvePackageRootForTest();
            var editorEndpoint = File.ReadAllText(Path.Combine(packageRoot, "Editor", "UnityMcpEditorEndpoint.cs"));

            StringAssert.Contains("new UnityMcpLanDiscoveryService(BuildEditorHeartbeat, discoveryGroup, false)", editorEndpoint);
            Assert.False(editorEndpoint.Contains("new UnityMcpLanDiscoveryService(BuildEditorHeartbeat, discoveryGroup);"));
        }

        [Test]
        public void InitializeResponsePreservesNumericId()
        {
            var response = Handle(new FakeEndpoint(), "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"test-protocol\"}}");

            StringAssert.Contains("\"id\":0", response);
            Assert.False(response.Contains("\"id\":\"0\""), response);
        }

        [Test]
        public void DirectToolNameCallsEndpoint()
        {
            var endpoint = new FakeEndpoint();
            endpoint.Tools.Register(new DelegateUnityMcpTool("direct.tool", "Direct.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult("{\"direct\":true}")));

            var response = Handle(endpoint, "{\"jsonrpc\":\"2.0\",\"id\":\"direct\",\"method\":\"direct.tool\",\"params\":{\"arguments\":{}}}");

            StringAssert.Contains("\"valueJson\":\"{\\\"direct\\\":true}\"", response);
            Assert.False(response.Contains("\"error\""), response);
        }

        [Test]
        public void NotificationsReturnEmptyResponse()
        {
            var response = Handle(new FakeEndpoint(), "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}");

            Assert.AreEqual(string.Empty, response);
        }

        [Test]
        public void ToolDescriptorUsesJsonUtilityFieldNames()
        {
            var registry = new UnityMcpToolRegistry();
            registry.Register(new DelegateUnityMcpTool("sample.tool", "Sample.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult("{}")));

            var box = new ToolDescriptorBox
            {
                tools = registry.List() as UnityMcpToolDescriptor[]
            };
            if (box.tools == null)
            {
                box.tools = new System.Collections.Generic.List<UnityMcpToolDescriptor>(registry.List()).ToArray();
            }

            var json = UnityJson.ToJson(box);

            StringAssert.Contains("\"name\":\"sample.tool\"", json);
            StringAssert.Contains("\"description\":\"Sample.\"", json);
            StringAssert.Contains("\"inputSchemaJson\":", json);
            Assert.False(json.Contains("\"Name\""), json);
            Assert.False(json.Contains("\"Description\""), json);
            Assert.False(json.Contains("\"InputSchemaJson\""), json);
        }

        [Test]
        public void UnityJsonEscapesStringsForSchemaFragments()
        {
            var jsonValue = UnityJson.ToJsonStringValue("a\"b\\c\n");

            Assert.AreEqual("\"a\\\"b\\\\c\\n\"", jsonValue);
        }

        [Test]
        public void AtomicFileWritesAndReadsJson()
        {
            var root = Path.Combine(Application.temporaryCachePath, "puerts-unity-mcp-tests", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "value.json");
            try
            {
                AtomicFile.WriteJson(path, new AtomicTestDto { name = "ok", count = 7 }, false);

                Assert.True(File.Exists(path));
                var bytes = File.ReadAllBytes(path);
                Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
                Assert.True(AtomicFile.TryReadJson<AtomicTestDto>(path, out var dto));
                Assert.AreEqual("ok", dto.name);
                Assert.AreEqual(7, dto.count);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void StateRootPrefersProjectLocalDirectory()
        {
            var previous = Environment.GetEnvironmentVariable("PUERTS_UNITY_MCP_DIR");
            var previousAllowExternal = Environment.GetEnvironmentVariable("PUERTS_UNITY_MCP_ALLOW_EXTERNAL_STATE");
            var outsideRoot = Path.Combine(Application.temporaryCachePath, "puerts-unity-mcp-outside", Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("PUERTS_UNITY_MCP_DIR", outsideRoot);
                Environment.SetEnvironmentVariable("PUERTS_UNITY_MCP_ALLOW_EXTERNAL_STATE", null);

                Assert.AreEqual(
                    Path.Combine(UnityMcpPaths.ProjectRoot, ".puerts-unity-mcp"),
                    UnityMcpPaths.StateRoot);
                Assert.AreEqual(
                    Path.Combine(UnityMcpPaths.ProjectRoot, ".puerts-unity-mcp", "temp"),
                    UnityMcpPaths.TempRoot);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PUERTS_UNITY_MCP_DIR", previous);
                Environment.SetEnvironmentVariable("PUERTS_UNITY_MCP_ALLOW_EXTERNAL_STATE", previousAllowExternal);
            }
        }

        [Test]
        public void ProjectExtensionPathsStayOutsideAssets()
        {
            var projectRoot = UnityMcpPaths.ProjectRoot;
            var extensionRoot = Path.Combine(projectRoot, "puerts-unity-mcp-extension");

            Assert.AreEqual(
                extensionRoot,
                UnityMcpPaths.ProjectExtensionRoot);
            Assert.AreEqual(
                Path.Combine(extensionRoot, "editor-mcp-config.json"),
                UnityMcpPaths.ProjectConfigPath);
            Assert.AreEqual(
                Path.Combine(extensionRoot, "mobile-mcp-config.json"),
                UnityMcpPaths.RuntimeConfigPath);
            Assert.AreEqual(
                Path.Combine(extensionRoot, "Editor", "config.json"),
                UnityMcpPaths.LegacyProjectExtensionConfigPath);
            Assert.AreEqual(
                Path.Combine(extensionRoot, "Runtime", "runtime-config.json"),
                UnityMcpPaths.LegacyRuntimeConfigPath);
            Assert.AreEqual(
                Path.Combine(extensionRoot, "Editor", "editor-tools"),
                UnityMcpPaths.EditorToolsRoot());
            Assert.AreEqual(
                Path.Combine(extensionRoot, "Runtime", "runtime-tools"),
                UnityMcpPaths.RuntimeToolsRoot());
            Assert.AreEqual(
                Path.Combine(extensionRoot, "skills"),
                UnityMcpPaths.SkillsRoot());
            Assert.AreEqual(UnityMcpPaths.ProjectExtensionRoot, UnityMcpPaths.ProjectAssetsRoot);
            Assert.AreEqual(UnityMcpPaths.EditorExtensionRoot, UnityMcpPaths.EditorResourcesRoot);
            Assert.AreEqual(UnityMcpPaths.RuntimeExtensionRoot, UnityMcpPaths.RuntimeResourcesRoot);
            Assert.False(UnityMcpPaths.ProjectConfigPath.Contains(Path.Combine("Assets", "PuertsUnityMcp")));
            Assert.False(UnityMcpPaths.RuntimeConfigPath.Contains(Path.Combine("Assets", "PuertsUnityMcp")));
        }

        [Test]
        public void MobileConfigBuildHookUsesStreamingAssetsDestination()
        {
            Assert.True(UnityMcpMobileConfigBuildHook.DestinationPath.EndsWith(
                Path.Combine("StreamingAssets", "PuertsUnityMcp", "mobile-mcp-config.json"),
                StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public void AndroidPluginAssetsUseUpstreamPuertsLibrariesAndBundledPermissionLibrary()
        {
            var packageRoot = ResolvePackageRootForTest();
            var androidRoot = Path.Combine(packageRoot, "Runtime", "Plugins", "Android");
            Assert.False(Directory.Exists(Path.Combine(androidRoot, "libs")));
            Assert.True(File.Exists(Path.Combine(androidRoot, "puerts-unity-mcp.androidlib", "AndroidManifest.xml")));

            var repoRoot = Path.GetFullPath(Path.Combine(packageRoot, "..", ".."));
            Assert.True(File.Exists(Path.Combine(repoRoot, "third_party", "puerts", "unity", "upms", "v8", "Plugins", "Android", "libs", "arm64-v8a", "libPapiV8.so")));
            Assert.True(File.Exists(Path.Combine(repoRoot, "third_party", "puerts", "unity", "upms", "core", "Plugins", "Android", "libs", "arm64-v8a", "libPuertsCore.so")));
            Assert.True(File.Exists(Path.Combine(repoRoot, "third_party", "puerts", "unity", "upms", "core", "Plugins", "Android", "libs", "arm64-v8a", "libWSPPAddon.so")));

            var script = File.ReadAllText(Path.Combine(packageRoot, "Tools~", "pum-cli-lib.mjs"));
            StringAssert.Contains("resolvePumAndroidPluginRoot", script);
            StringAssert.Contains("return path.join(resolvePumPackageRoot(projectRoot, options), \"Runtime\", \"Plugins\", \"Android\")", script);
            StringAssert.Contains("verifyPuerTsAndroidNativeLibraries", script);
            var start = script.IndexOf("export function removePumFromBuild", StringComparison.Ordinal);
            var end = script.IndexOf("export function syncLocalPackage", start, StringComparison.Ordinal);

            Assert.GreaterOrEqual(start, 0);
            Assert.Greater(end, start);

            var body = script.Substring(start, end - start);

            Assert.AreEqual(-1, body.IndexOf("removeAndroidPermissions(projectRoot, options)", StringComparison.Ordinal));
        }

        [Test]
        public void SyncScriptDoesNotCopyLocalAgentStateAndCleansLegacyGeneratedFiles()
        {
            var packageRoot = ResolvePackageRootForTest();
            var script = File.ReadAllText(Path.Combine(packageRoot, "Tools~", "pum-cli-lib.mjs"));

            StringAssert.Contains("\".git\"", script);
            StringAssert.Contains("\".puerts-unity-mcp\"", script);
            StringAssert.Contains("copyDirectoryMirror(repoRoot, localPackageRoot, localPackageSyncIgnoredNames)", script);
            StringAssert.Contains("copyDirectoryOverlay(localPackageRoot, repoRoot, localPackageSyncIgnoredNames)", script);

            var start = script.IndexOf("function removeLegacyProjectGeneratedPluginArtifacts", StringComparison.Ordinal);
            var end = script.IndexOf("function defaultMobileConfig", start, StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0);
            Assert.Greater(end, start);

            var body = script.Substring(start, end - start);
            StringAssert.Contains("path.join(\"Assets\", \"Gen\", \"Plugins\", \"puerts_il2cpp\")", body);
            StringAssert.Contains("path.join(\"Assets\", \"puerts-unity-mcp\", \"Runtime\", \"Plugins\", \"puerts_il2cpp\")", body);
            StringAssert.Contains("path.join(\"puerts-unity-mcp-extension\", \"Runtime\", \"Generated\")", body);
            StringAssert.Contains("path.join(\"puerts-unity-mcp-extension\", \"Runtime\", \"Plugins\", \"puerts_il2cpp\")", body);
        }

        [Test]
        public void RuntimeAssemblyOwnsEditorPlayModeAndPlayerTargets()
        {
            var packageRoot = ResolvePackageRootForTest();
            var runtimeAsmdef = File.ReadAllText(Path.Combine(packageRoot, "Runtime", "PuertsUnityMcp.Runtime.asmdef"));

            Assert.False(Directory.Exists(Path.Combine(packageRoot, "Player")));
            StringAssert.Contains("\"name\": \"PuertsUnityMcp.Runtime\"", runtimeAsmdef);
            StringAssert.Contains("\"defineConstraints\"", runtimeAsmdef);
            Assert.False(runtimeAsmdef.Contains("\"UNITY_EDITOR\""));
            Assert.False(runtimeAsmdef.Contains("PuertsUnityMcp.Shared"));
            Assert.False(runtimeAsmdef.Contains("\"includePlatforms\": [\n    \"Editor\""));
        }

        [Test]
        public void PuertsGeneratedFilesUsePackageConfigureUnderAssets()
        {
            var packageRoot = ResolvePackageRootForTest();
            var thirdPartyRoot = Path.GetFullPath(Path.Combine(packageRoot, "..", "..", "third_party", "puerts"));
            var pathHelper = File.ReadAllText(Path.Combine(thirdPartyRoot, "unity", "upms", "core", "Editor", "Src", "Generator", "PathHelper.cs"));
            var configure = File.ReadAllText(Path.Combine(thirdPartyRoot, "unity", "upms", "core", "Editor", "Src", "Configure.cs"));
            var pumConfigure = File.ReadAllText(Path.Combine(packageRoot, "Editor", "PuertsUnityMcpPuertsConfigure.cs"));
            var editorAsmdef = File.ReadAllText(Path.Combine(packageRoot, "Editor", "PuertsUnityMcp.Editor.asmdef"));

            StringAssert.Contains("return Path.Combine(Puerts.Configure.GetCodeOutputDirectory(), \"Plugins/puerts_il2cpp/\")", pathHelper);
            StringAssert.Contains("return UnityEngine.Application.dataPath + \"/Gen/\";", configure);
            StringAssert.Contains("[CodeOutputDirectory]", pumConfigure);
            StringAssert.Contains("\"puerts-unity-mcp\", \"Runtime\", \"Generated\"", pumConfigure);
            StringAssert.Contains("\"com.tencent.puerts.core.Editor\"", editorAsmdef);
            Assert.False(pathHelper.Contains("\"puerts-unity-mcp-extension\", \"Plugins\", \"puerts_il2cpp\""));
            Assert.False(configure.Contains("\"puerts-unity-mcp-extension\", \"Generated\""));
            Assert.False(pumConfigure.Contains("\"puerts-unity-mcp-extension\""));
            Assert.False(pathHelper.Contains("GetPuertsUnityMcpExtensionPluginPath"));
            Assert.False(pathHelper.Contains("\"puerts-unity-mcp-extension\", \"Runtime\", \"Plugins\", \"puerts_il2cpp\""));
            Assert.False(configure.Contains("\"puerts-unity-mcp-extension\", \"Runtime\", \"Generated\""));
        }

        [Test]
        public void ScriptToolManifestsLoadFromFilesystemDirectory()
        {
            var root = Path.Combine(Application.temporaryCachePath, "puerts-unity-mcp-tests", Guid.NewGuid().ToString("N"));
            var toolRoot = Path.Combine(root, "Editor", "editor-tools");
            try
            {
                Directory.CreateDirectory(toolRoot);
                var manifestPath = Path.Combine(toolRoot, "sample.tool.json");
                var modulePath = Path.Combine(toolRoot, "sample.mjs");
                File.WriteAllText(
                    manifestPath,
                    "{\"name\":\"sample.tool\",\"description\":\"Sample tool.\",\"inputSchemaJson\":\"{\\\"type\\\":\\\"object\\\"}\",\"modulePath\":\"sample.mjs\"}",
                    System.Text.Encoding.UTF8);
                File.WriteAllText(modulePath, "export function execute(argsJson, contextJson) { return \"{}\"; }", System.Text.Encoding.UTF8);

                var manifests = UnityMcpResourceScriptTools.LoadManifests(toolRoot);

                Assert.AreEqual(1, manifests.Length);
                Assert.AreEqual("sample.tool", manifests[0].name);
                Assert.AreEqual("execute", manifests[0].functionName);
                Assert.AreEqual(Path.GetFullPath(modulePath), manifests[0].modulePath);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void SkillsLoadFromFilesystemDirectory()
        {
            var root = Path.Combine(Application.temporaryCachePath, "puerts-unity-mcp-tests", Guid.NewGuid().ToString("N"));
            var skillsRoot = Path.Combine(root, "skills");
            try
            {
                Directory.CreateDirectory(skillsRoot);
                var skillPath = Path.Combine(skillsRoot, "qa.md");
                File.WriteAllText(
                    skillPath,
                    "---\nname: qa-helper\ndescription: QA helper\n---\nUse this skill from the extension directory.\n",
                    System.Text.Encoding.UTF8);

                var skills = UnityMcpResourceSkills.LoadSkills(skillsRoot);
                var skill = UnityMcpResourceSkills.FindSkill(skillsRoot, "qa-helper");

                Assert.AreEqual(1, skills.Length);
                Assert.NotNull(skill);
                Assert.AreEqual("QA helper", skill.description);
                Assert.AreEqual("qa.md", skill.assetName);
                Assert.AreEqual(Path.GetFullPath(skillPath), skill.filePath);
                StringAssert.Contains("extension directory", skill.content);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void ProjectConfigRoundTripsThroughUnityJson()
        {
            var root = Path.Combine(Application.temporaryCachePath, "puerts-unity-mcp-tests", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "config.json");
            try
            {
                var config = new UnityMcpProjectConfig
                {
                    editorAutoStart = false,
                    editorBindAddress = "0.0.0.0",
                    editorPort = 23456,
                    runtimeAutoStart = true,
                    runtimeBindAddress = "127.0.0.1",
                    runtimePort = 23457,
                    runtimeLogBufferSize = 123,
                    lanDiscoveryEnabled = true,
                    name = "editor-a",
                    name_group = "qa",
                    selectedTargetKind = "player",
                    selectedTargetId = "phone-1",
                    selectedTargetName = "qa-phone",
                    selectedTargetUrl = "http://192.168.1.20:18991",
                    serverName = "test server"
                };

                UnityMcpProjectConfigStore.SaveToPath(path, config);
                var loaded = UnityMcpProjectConfigStore.LoadFromPath(path);

                Assert.AreEqual(UnityMcpProjectConfig.LatestVersion, loaded.version);
                Assert.False(loaded.editorAutoStart);
                Assert.AreEqual("0.0.0.0", loaded.editorBindAddress);
                Assert.AreEqual(23456, loaded.editorPort);
                Assert.True(loaded.runtimeAutoStart);
                Assert.AreEqual("127.0.0.1", loaded.runtimeBindAddress);
                Assert.AreEqual(23457, loaded.runtimePort);
                Assert.AreEqual(123, loaded.runtimeLogBufferSize);
                Assert.True(loaded.lanDiscoveryEnabled);
                Assert.AreEqual("editor-a", loaded.name);
                Assert.AreEqual("qa", loaded.name_group);
                Assert.AreEqual("player", loaded.selectedTargetKind);
                Assert.AreEqual("phone-1", loaded.selectedTargetId);
                Assert.AreEqual("qa-phone", loaded.selectedTargetName);
                Assert.AreEqual("http://192.168.1.20:18991", loaded.selectedTargetUrl);
                Assert.AreEqual("test_server", loaded.serverName);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void RuntimeConfigPartialJsonKeepsDefaultRuntimePermissions()
        {
            var ok = UnityMcpRuntimeConfigStore.TryLoadFromJson(
                "{\"runtimeAutoStart\":false,\"runtimePort\":24680,\"runtimeLogBufferSize\":77,\"name\":\"phone-a\",\"name_group\":\"qa\"}",
                out var config);

            Assert.True(ok);
            Assert.False(config.runtimeAutoStart);
            Assert.AreEqual(24680, config.runtimePort);
            Assert.AreEqual(77, config.runtimeLogBufferSize);
            Assert.True(config.allowJsEval);
            Assert.True(config.allowReflection);
            Assert.True(config.allowPrivateReflection);
            Assert.True(config.allowFileAccess);
            Assert.True(config.allowNetworkAccess);
            Assert.True(config.allowRuntimeCodeLoad);
            Assert.True(config.runInBackground);
            Assert.False(config.enableFileCommandPump);
            Assert.False(config.enableDiskHeartbeat);
            Assert.False(config.enableDiscoveredEndpointCache);
            Assert.False(config.enableAotMissLog);
            Assert.AreEqual("memory", config.screenshotWriteMode);
            Assert.AreEqual(30000, config.heartbeatIntervalMs);
            Assert.AreEqual(4, config.maxCommandsPerFrame);
            Assert.AreEqual("phone-a", config.name);
            Assert.AreEqual("qa", config.name_group);
        }

        [Test]
        public void RuntimeConfigNormalizesRuntimeIoPolicy()
        {
            var ok = UnityMcpRuntimeConfigStore.TryLoadFromJson(
                "{\"screenshotWriteMode\":\"FILE\",\"heartbeatIntervalMs\":0,\"enableDiskHeartbeat\":true,\"enableFileCommandPump\":true,\"enableDiscoveredEndpointCache\":true,\"enableAotMissLog\":true}",
                out var config);

            Assert.True(ok);
            Assert.True(config.enableFileCommandPump);
            Assert.True(config.enableDiskHeartbeat);
            Assert.True(config.enableDiscoveredEndpointCache);
            Assert.True(config.enableAotMissLog);
            Assert.AreEqual("file", config.screenshotWriteMode);
            Assert.AreEqual(30000, config.heartbeatIntervalMs);
        }

        [Test]
        public void ProjectConfigPartialJsonUsesLatestDefaults()
        {
            var root = Path.Combine(Application.temporaryCachePath, "puerts-unity-mcp-tests", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(root, "config.json");
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(path, "{\"editorPort\":25001,\"runtimePort\":25002}", System.Text.Encoding.UTF8);

                var config = UnityMcpProjectConfigStore.LoadFromPath(path);

                Assert.AreEqual(UnityMcpProjectConfig.LatestVersion, config.version);
                Assert.True(config.lanDiscoveryEnabled);
                Assert.AreEqual("default", config.name_group);
                Assert.AreEqual("editor", config.selectedTargetKind);
                Assert.AreEqual(string.Empty, config.selectedTargetId);
                Assert.AreEqual(25001, config.editorPort);
                Assert.AreEqual(25002, config.runtimePort);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void RuntimeConfigCanBeDerivedFromProjectConfig()
        {
            var projectConfig = new UnityMcpProjectConfig
            {
                runtimeAutoStart = false,
                runtimeBindAddress = "127.0.0.1",
                runtimePort = 24681,
                runtimeLogBufferSize = 88,
                lanDiscoveryEnabled = true,
                name = "runtime-from-project",
                name_group = "qa",
                serverName = "editor-only"
            };

            var runtimeConfig = UnityMcpRuntimeConfig.FromProjectConfig(projectConfig);

            Assert.AreEqual(UnityMcpRuntimeConfig.LatestVersion, runtimeConfig.version);
            Assert.False(runtimeConfig.runtimeAutoStart);
            Assert.AreEqual("127.0.0.1", runtimeConfig.runtimeBindAddress);
            Assert.AreEqual(24681, runtimeConfig.runtimePort);
            Assert.AreEqual(88, runtimeConfig.runtimeLogBufferSize);
            Assert.True(runtimeConfig.allowJsEval);
            Assert.True(runtimeConfig.allowReflection);
            Assert.True(runtimeConfig.allowPrivateReflection);
            Assert.True(runtimeConfig.allowFileAccess);
            Assert.True(runtimeConfig.allowNetworkAccess);
            Assert.True(runtimeConfig.allowRuntimeCodeLoad);
            Assert.True(runtimeConfig.lanDiscoveryEnabled);
            Assert.False(runtimeConfig.enableFileCommandPump);
            Assert.False(runtimeConfig.enableDiskHeartbeat);
            Assert.False(runtimeConfig.enableDiscoveredEndpointCache);
            Assert.False(runtimeConfig.enableAotMissLog);
            Assert.AreEqual("memory", runtimeConfig.screenshotWriteMode);
            Assert.AreEqual(30000, runtimeConfig.heartbeatIntervalMs);
            Assert.AreEqual("runtime-from-project", runtimeConfig.name);
            Assert.AreEqual("qa", runtimeConfig.name_group);
        }

        [Test]
        public void ReflectionGatewayDoesNotWriteAotMissLogByDefault()
        {
            var root = Path.Combine(Application.temporaryCachePath, "puerts-unity-mcp-tests", Guid.NewGuid().ToString("N"));
            var previous = ReflectionGateway.EnableAotMissLog;
            using (new StateRootScope(root))
            {
                try
                {
                    ReflectionGateway.EnableAotMissLog = false;
                    var gateway = new ReflectionGateway();

                    Assert.Throws<TypeLoadException>(() => gateway.GetStaticJson("Missing.Type.For.PuertsUnityMcp.Test", "Value"));

                    Assert.False(File.Exists(UnityMcpPaths.AotMissesPath()));
                }
                finally
                {
                    ReflectionGateway.EnableAotMissLog = previous;
                }
            }
        }

        [Test]
        public void ReflectionGatewayReadsEditorBuildSettingsStaticPath()
        {
            var gateway = new ReflectionGateway();
            var countResult = UnityJson.FromJson<ReflectionGatewayResultForTest>(
                gateway.GetStaticPathJson("UnityEditor.EditorBuildSettings", "scenes.length"));

            Assert.AreEqual("number", countResult.kind);
            Assert.AreEqual(EditorBuildSettings.scenes.Length, (int)countResult.numberValue);

            if (EditorBuildSettings.scenes.Length > 0)
            {
                var pathResult = UnityJson.FromJson<ReflectionGatewayResultForTest>(
                    gateway.GetStaticPathJson("UnityEditor.EditorBuildSettings", "scenes[0].path"));
                var enabledResult = UnityJson.FromJson<ReflectionGatewayResultForTest>(
                    gateway.GetStaticPathJson("UnityEditor.EditorBuildSettings", "scenes[0].enabled"));

                Assert.AreEqual("string", pathResult.kind);
                Assert.AreEqual(EditorBuildSettings.scenes[0].path, pathResult.stringValue);
                Assert.AreEqual("bool", enabledResult.kind);
                Assert.AreEqual(EditorBuildSettings.scenes[0].enabled, enabledResult.boolValue);
            }
        }

        [Test]
        public void LanDiscoveryAcceptsOnlyMatchingNameGroup()
        {
            var heartbeat = new UnityMcpHeartbeat
            {
                endpointId = "phone_1",
                endpointKind = "player",
                endpointName = "Phone 1",
                projectName = "Phone Game",
                name = "qa-phone",
                name_group = "qa",
                httpUrl = "http://127.0.0.1:18991",
                port = 18991,
                platform = "Android",
                isEditor = false,
                capabilities = new UnityMcpCapabilities { runtimeToolCall = true, http = true }
            };
            var json = UnityMcpLanDiscoveryService.BuildAnnouncementJson(heartbeat, "qa");

            Assert.True(UnityMcpLanDiscoveryService.TryBuildHeartbeatFromMessageJson(json, "qa", "192.168.1.25", out var accepted));
            Assert.AreEqual("phone_1", accepted.endpointId);
            Assert.AreEqual("qa-phone", accepted.name);
            Assert.AreEqual("qa", accepted.name_group);
            Assert.AreEqual("http://192.168.1.25:18991", accepted.httpUrl);
            Assert.AreEqual(UnityMcpConstants.DiscoverySource, accepted.source);
            Assert.False(UnityMcpLanDiscoveryService.TryBuildHeartbeatFromMessageJson(json, "prod", "192.168.1.25", out _));
        }

        [Test]
        public void LanDiscoveryRewritesWildcardHttpUrlToSenderAddress()
        {
            var heartbeat = new UnityMcpHeartbeat
            {
                endpointId = "phone_2",
                endpointKind = "player",
                endpointName = "Phone 2",
                name = "qa-phone-2",
                name_group = "qa",
                httpUrl = "http://0.0.0.0:18991",
                port = 18991,
                platform = "Android",
                capabilities = new UnityMcpCapabilities { runtimeToolCall = true, http = true }
            };
            var json = UnityMcpLanDiscoveryService.BuildAnnouncementJson(heartbeat, "qa");

            Assert.True(UnityMcpLanDiscoveryService.TryBuildHeartbeatFromMessageJson(json, "qa", "192.168.1.26", out var accepted));
            Assert.AreEqual("http://192.168.1.26:18991", accepted.httpUrl);
        }

        [Test]
        public void AgentInstallerNoLongerWritesAgentRootFiles()
        {
            var root = Path.Combine(Application.temporaryCachePath, "puerts-unity-mcp-tests", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var unityRoot = Path.Combine(root, "unity-project");
                var agentRoot = Path.Combine(root, "agent-workspace");
                Directory.CreateDirectory(unityRoot);
                Directory.CreateDirectory(agentRoot);
                var proxyPath = Path.Combine(root, "proxy.js");
                File.WriteAllText(proxyPath, "process.exit(0);", System.Text.Encoding.UTF8);

                var config = new UnityMcpProjectConfig
                {
                    editorBindAddress = "0.0.0.0",
                    editorPort = 24567,
                    serverName = "test-server"
                };

                var result = UnityMcpAgentConfigInstaller.InstallAtAgentRoot(agentRoot, unityRoot, config, null, proxyPath);

                Assert.False(result.succeeded);
                StringAssert.Contains("setup-for-agent.md", result.message);
                Assert.AreEqual(0, result.writtenFiles.Length);
                Assert.AreEqual(0, result.backupFiles.Length);
                Assert.False(File.Exists(Path.Combine(agentRoot, ".mcp.json")));
                Assert.False(Directory.Exists(Path.Combine(agentRoot, ".cursor")));
                Assert.False(Directory.Exists(Path.Combine(agentRoot, ".codex")));
                Assert.False(Directory.Exists(Path.Combine(agentRoot, ".claude")));
                Assert.False(Directory.Exists(Path.Combine(agentRoot, ".codex-plugin")));
                StringAssert.Contains("24567", UnityMcpAgentConfigInstaller.BuildMcpUrl(config));
                StringAssert.Contains(Path.Combine("puerts-unity-mcp-extension", "editor-mcp-config.json"), UnityMcpAgentConfigInstaller.ResolveProjectConfigPath(unityRoot));
                Assert.False(UnityMcpAgentConfigInstaller.ResolveProjectConfigPath(unityRoot).Contains(Path.Combine("Assets", "PuertsUnityMcp")));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void RuntimeLogBufferCapturesFiltersAndClearsLogs()
        {
            var buffer = new UnityMcpRuntimeLogBuffer();
            var marker = "puerts-mcp-log-" + Guid.NewGuid().ToString("N");
            try
            {
                buffer.Initialize(5);
                LogAssert.Expect(LogType.Warning, marker);
                Debug.LogWarning(marker);

                Assert.True(SpinWait.SpinUntil(() => buffer.Count > 0, TimeSpan.FromSeconds(2)));

                var entries = buffer.GetEntries(10, "Warning", marker, false);
                Assert.AreEqual(1, entries.Length);
                Assert.AreEqual("Warning", entries[0].type);
                Assert.AreEqual(marker, entries[0].message);
                Assert.IsNull(entries[0].stackTrace);
                Assert.GreaterOrEqual(buffer.Clear(), 1);
                Assert.AreEqual(0, buffer.Count);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void CommandFilePumpKeepsCommandUntilResultIsWritten()
        {
            var root = Path.Combine(Application.temporaryCachePath, "puerts-unity-mcp-tests", Guid.NewGuid().ToString("N"));
            using (new StateRootScope(root))
            {
                Assert.AreEqual(
                    Path.GetFullPath(Path.Combine(root, "temp")),
                    Path.GetFullPath(UnityMcpPaths.TempRoot));
                var endpoint = new FakeEndpoint("pump-editor", "editor", "pump");
                var gate = new TaskCompletionSource<string>();
                var calls = 0;
                endpoint.Tools.Register(new DelegateUnityMcpTool("hold.tool", "Hold.", JsonSchemas.Object(), (ctx, args) =>
                {
                    calls++;
                    return gate.Task;
                }));
                var pump = new CommandFilePump(endpoint);
                var command = new UnityMcpCommand
                {
                    id = "durable-message",
                    action = "hold.tool",
                    @params = new UnityMcpToolArguments()
                };
                var commandPath = Path.Combine(UnityMcpPaths.CommandsRoot(endpoint.EndpointKind, endpoint.EndpointId), command.id + ".json");
                var resultPath = Path.Combine(UnityMcpPaths.ResultsRoot(endpoint.EndpointKind, endpoint.EndpointId), command.id + ".json");
                AtomicFile.WriteJson(commandPath, command, false);

                pump.Tick();
                pump.Tick();

                Assert.AreEqual(1, calls);
                Assert.True(File.Exists(commandPath), "Command file should remain while execution is in flight.");
                Assert.False(File.Exists(resultPath), "Result should not exist before the tool completes.");

                gate.SetResult("{\"ok\":true}");

                Assert.True(SpinWait.SpinUntil(() => File.Exists(resultPath), TimeSpan.FromSeconds(2)));
                Assert.False(File.Exists(commandPath), "Command file should be deleted only after the result is durable.");
                Assert.True(AtomicFile.TryReadJson<UnityMcpCommandResult>(resultPath, out var result));
                Assert.True(result.success);
            }
        }

        [Test]
        public void CommandFilePumpDoesNotReexecuteCompletedCommandAfterReload()
        {
            var root = Path.Combine(Application.temporaryCachePath, "puerts-unity-mcp-tests", Guid.NewGuid().ToString("N"));
            using (new StateRootScope(root))
            {
                var endpoint = new FakeEndpoint("pump-editor", "editor", "pump");
                var calls = 0;
                endpoint.Tools.Register(new DelegateUnityMcpTool("repeat.tool", "Repeat.", JsonSchemas.Object(), (ctx, args) =>
                {
                    calls++;
                    return Task.FromResult("{\"unexpected\":true}");
                }));
                var pump = new CommandFilePump(endpoint);
                var command = new UnityMcpCommand
                {
                    id = "completed-message",
                    action = "repeat.tool",
                    @params = new UnityMcpToolArguments()
                };
                var commandPath = Path.Combine(UnityMcpPaths.CommandsRoot(endpoint.EndpointKind, endpoint.EndpointId), command.id + ".json");
                var resultPath = Path.Combine(UnityMcpPaths.ResultsRoot(endpoint.EndpointKind, endpoint.EndpointId), command.id + ".json");
                AtomicFile.WriteJson(commandPath, command, false);
                AtomicFile.WriteJson(resultPath, UnityMcpCommandResult.Ok(command.id, "{\"ok\":true}", DateTime.UtcNow, 0), false);

                pump.Tick();

                Assert.AreEqual(0, calls);
                Assert.False(File.Exists(commandPath));
                Assert.True(File.Exists(resultPath));
            }
        }

        [Test]
        public void CommandFilePumpDoesNotQuarantineFreshPartialCommand()
        {
            var root = Path.Combine(Application.temporaryCachePath, "puerts-unity-mcp-tests", Guid.NewGuid().ToString("N"));
            using (new StateRootScope(root))
            {
                var endpoint = new FakeEndpoint("pump-editor", "editor", "pump");
                var pump = new CommandFilePump(endpoint);
                var commandPath = Path.Combine(UnityMcpPaths.CommandsRoot(endpoint.EndpointKind, endpoint.EndpointId), "partial.json");
                File.WriteAllText(commandPath, "{\"id\":\"partial\"", System.Text.Encoding.UTF8);

                pump.Tick();

                Assert.True(File.Exists(commandPath));
                Assert.False(File.Exists(commandPath + ".error"));
            }
        }

        [Test]
        public void OperationStoreCleansExpiredCompletedOperationsOnly()
        {
            var root = Path.Combine(Application.temporaryCachePath, "puerts-unity-mcp-tests", Guid.NewGuid().ToString("N"));
            using (new StateRootScope(root))
            {
                var store = new OperationStore();
                var expiredId = store.Create("expired.tool", "editor", new UnityMcpToolArguments());
                store.Complete(expiredId, true, "{\"ok\":true}");
                var runningId = store.Create("running.tool", "editor", new UnityMcpToolArguments());
                store.Update(runningId, "running", "{}");

                MakeOperationOlderThan(expiredId, UnityMcpConstants.OperationResultRetention + TimeSpan.FromMinutes(1));

                var deleted = store.CleanupNow();

                Assert.GreaterOrEqual(deleted, 1);
                Assert.False(Directory.Exists(UnityMcpPaths.OperationRoot(expiredId)));
                Assert.True(Directory.Exists(UnityMcpPaths.OperationRoot(runningId)));
            }
        }

        private static string Handle(FakeEndpoint endpoint, string body)
        {
            return new UnityMcpJsonRpc(endpoint).HandleAsync(body).GetAwaiter().GetResult();
        }

        private static void MakeOperationOlderThan(string operationId, TimeSpan age)
        {
            var root = UnityMcpPaths.OperationRoot(operationId);
            var timestamp = DateTime.UtcNow - age;
            foreach (var file in Directory.GetFiles(root))
            {
                File.SetLastWriteTimeUtc(file, timestamp);
            }

            Directory.SetLastWriteTimeUtc(root, timestamp);
        }

        private static string ResolvePackageRootForTest()
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityMcpConstants).Assembly);
            if (packageInfo != null && !string.IsNullOrEmpty(packageInfo.resolvedPath))
            {
                return packageInfo.resolvedPath;
            }

            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "puerts-unity-mcp", "Packages", UnityMcpConstants.PackageName));
        }

        [Serializable]
        private sealed class ToolDescriptorBox
        {
            public UnityMcpToolDescriptor[] tools;
        }

        [Serializable]
        private sealed class AtomicTestDto
        {
            public string name;
            public int count;
        }

        [Serializable]
        private sealed class ReflectionGatewayResultForTest
        {
            public string kind;
            public string stringValue;
            public double numberValue;
            public bool boolValue;
        }

        private sealed class StateRootScope : IDisposable
        {
            private readonly string previous;
            private readonly string previousAllowExternal;
            private readonly string root;

            public StateRootScope(string root)
            {
                this.root = root;
                previous = Environment.GetEnvironmentVariable("PUERTS_UNITY_MCP_DIR");
                previousAllowExternal = Environment.GetEnvironmentVariable("PUERTS_UNITY_MCP_ALLOW_EXTERNAL_STATE");
                Environment.SetEnvironmentVariable("PUERTS_UNITY_MCP_DIR", root);
                Environment.SetEnvironmentVariable("PUERTS_UNITY_MCP_ALLOW_EXTERNAL_STATE", "1");
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("PUERTS_UNITY_MCP_DIR", previous);
                Environment.SetEnvironmentVariable("PUERTS_UNITY_MCP_ALLOW_EXTERNAL_STATE", previousAllowExternal);
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private sealed class FakeEndpoint : IUnityMcpEndpoint
        {
            public FakeEndpoint(string endpointId = "fake-editor", string endpointKind = "editor", string endpointName = "fake")
            {
                EndpointId = endpointId;
                EndpointKind = endpointKind;
                EndpointName = endpointName;
            }

            public string EndpointId { get; }
            public string EndpointKind { get; }
            public string EndpointName { get; }
            public UnityMcpToolRegistry Tools { get; } = new UnityMcpToolRegistry();

            public string BuildHealthJson()
            {
                return UnityJson.ToJson(new UnityMcpHealth
                {
                    status = "ok",
                    endpointId = EndpointId,
                    endpointKind = EndpointKind,
                    endpointName = EndpointName,
                    ready = true
                });
            }

            public Task<string> CallToolAsync(string name, UnityMcpToolArguments arguments)
            {
                return Tools.ExecuteAsync(new UnityMcpToolContext(this), name, arguments ?? new UnityMcpToolArguments());
            }
        }
    }
}
