using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PuertsUnityMcp.Editor
{
    public sealed class UnityMcpSettingsWindow : EditorWindow
    {
        private enum TabType
        {
            Server,
            Runtime,
            Agents,
            Targets,
            Paths
        }

        private Vector2 scrollPosition;
        private TabType currentTab;
        private UnityMcpProjectConfig config;
        private string installMessage;
        private string targetMessage;

        [MenuItem("Window/PuerTS Unity MCP/Settings", priority = 0)]
        public static void OpenWindow()
        {
            var window = GetWindow<UnityMcpSettingsWindow>();
            window.titleContent = new GUIContent("PuerTS Unity MCP");
            window.minSize = new Vector2(620f, 500f);
            window.Show();
        }

        [MenuItem("PuerTS Unity MCP/Settings", priority = 0)]
        private static void OpenLegacyMenuWindow()
        {
            OpenWindow();
        }

        [MenuItem("PuerTS Unity MCP/Start Editor MCP", priority = 20)]
        private static void StartEditorMcp()
        {
            UnityMcpEditorBootstrap.StartEndpoint();
        }

        [MenuItem("PuerTS Unity MCP/Stop Editor MCP", priority = 21)]
        private static void StopEditorMcp()
        {
            UnityMcpEditorBootstrap.StopEndpoint();
        }

        [MenuItem("PuerTS Unity MCP/Open Agent Setup Guide", priority = 40)]
        private static void OpenAgentSetupGuide()
        {
            Reveal(UnityMcpAgentConfigInstaller.ResolveSetupGuidePath(UnityMcpPaths.ProjectRoot));
        }

        [MenuItem("PuerTS Unity MCP/Open Editor Config", priority = 41)]
        private static void OpenProjectConfig()
        {
            Reveal(UnityMcpPaths.ProjectConfigPath);
        }

        private void OnEnable()
        {
            ReloadConfig();
        }

        private void OnGUI()
        {
            if (config == null)
            {
                ReloadConfig();
            }

            DrawHeader();
            DrawToolbar();
            DrawTabs();
            EditorGUILayout.Space(6f);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            EditorGUI.BeginChangeCheck();
            switch (currentTab)
            {
                case TabType.Server:
                    DrawServerTab();
                    break;
                case TabType.Runtime:
                    DrawRuntimeTab();
                    break;
                case TabType.Agents:
                    DrawAgentsTab();
                    break;
                case TabType.Targets:
                    DrawTargetsTab();
                    break;
                case TabType.Paths:
                    DrawPathsTab();
                    break;
            }

            if (EditorGUI.EndChangeCheck())
            {
                SaveConfig();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("PuerTS Unity MCP", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Configure the Unity Editor MCP endpoint, Play Mode/runtime endpoint, LAN discovery, and project-local runtime assets.",
                MessageType.Info);
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(72f)))
            {
                ReloadConfig();
                Repaint();
            }

            if (GUILayout.Button("Open State", EditorStyles.toolbarButton, GUILayout.Width(86f)))
            {
                Reveal(UnityMcpPaths.StateRoot);
            }

            if (GUILayout.Button("Agent Setup", EditorStyles.toolbarButton, GUILayout.Width(104f)))
            {
                OpenAgentSetupGuide();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(BuildStatusSummary(), EditorStyles.miniLabel, GUILayout.Width(310f));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTabs()
        {
            currentTab = (TabType)GUILayout.Toolbar((int)currentTab, new[] { "Server", "Runtime", "Agents", "Targets", "Paths" });
        }

        private void DrawServerTab()
        {
            EditorGUILayout.LabelField("Editor MCP", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Status", UnityMcpEditorBootstrap.IsRunning ? "Running" : "Stopped");
            EditorGUILayout.LabelField("Endpoint ID", UnityMcpEditorBootstrap.Endpoint == null ? "(not created)" : UnityMcpEditorBootstrap.Endpoint.EndpointId);
            EditorGUILayout.LabelField("Health URL", BuildHealthUrl());
            EditorGUILayout.Space(8f);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(UnityMcpEditorBootstrap.IsRunning);
            if (GUILayout.Button("Start", GUILayout.Height(26f)))
            {
                UnityMcpEditorBootstrap.StartEndpoint();
            }

            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(!UnityMcpEditorBootstrap.IsRunning);
            if (GUILayout.Button("Stop", GUILayout.Height(26f)))
            {
                UnityMcpEditorBootstrap.StopEndpoint();
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Startup", EditorStyles.boldLabel);
            config.editorAutoStart = EditorGUILayout.Toggle("Auto Start With Unity", config.editorAutoStart);
            EditorGUILayout.HelpBox(
                "Auto start is stored in puerts-unity-mcp-extension/editor-mcp-config.json. Domain reload still restores a running endpoint so compile/reload does not strand pending commands.",
                MessageType.None);

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Network", EditorStyles.boldLabel);
            config.editorBindAddress = EditorGUILayout.TextField("Bind Address", config.editorBindAddress);
            config.editorPort = EditorGUILayout.IntField("Port", config.editorPort);
            EditorGUILayout.HelpBox(
                "Use 127.0.0.1 for local-only access. Use 0.0.0.0 if another computer should connect to this Editor MCP; otherwise discovery can find the Editor but remote HTTP connections may still fail.",
                MessageType.None);

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("LAN Discovery", EditorStyles.boldLabel);
            config.lanDiscoveryEnabled = EditorGUILayout.Toggle("Enabled", config.lanDiscoveryEnabled);
            config.name = EditorGUILayout.TextField("Name", config.name);
            config.name_group = EditorGUILayout.TextField("Name Group", config.name_group);
            EditorGUILayout.HelpBox(
                "LAN discovery only accepts endpoints with the same name_group. Runtime Play Mode is kept local; LAN player discovery is intended for real APK/IPA or standalone players. UDP can be blocked by firewalls, AP isolation, VLAN routing, or network policy; configure lanHttpProbeHosts/lanHttpProbeCidrs in editor-mcp-config.json for TCP/HTTP fallback.",
                MessageType.None);
        }

        private void DrawRuntimeTab()
        {
            EditorGUILayout.LabelField("Play Mode / Player MCP", EditorStyles.boldLabel);
            var runtime = UnityMcpRuntimeHost.Instance;
            EditorGUILayout.LabelField("Local Runtime", runtime == null ? "Not active" : runtime.EndpointId);
            EditorGUILayout.LabelField("Local Runtime URL", runtime == null ? "(none)" : runtime.BuildHealth().httpUrl);
            EditorGUILayout.Space(8f);

            config.runtimeAutoStart = EditorGUILayout.Toggle("Auto Start Runtime Host", config.runtimeAutoStart);
            config.runtimeBindAddress = EditorGUILayout.TextField("Bind Address", config.runtimeBindAddress);
            config.runtimePort = EditorGUILayout.IntField("Port", config.runtimePort);
            config.runtimeLogBufferSize = EditorGUILayout.IntField("Log Buffer Size", config.runtimeLogBufferSize);
            EditorGUILayout.HelpBox(
                "This controls the host created by Play Mode and player builds. For phone LAN direct, keep Bind Address as 0.0.0.0 so the PC agent can connect to the embedded Player MCP. Environment variables can still override target ID and runtime port.",
                MessageType.None);

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Build Default", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Write the phone/player config into puerts-unity-mcp-extension/mobile-mcp-config.json. The add-pum-to-build script copies this file into StreamingAssets/PuertsUnityMcp/mobile-mcp-config.json for player builds.",
                MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Write Mobile MCP Config", GUILayout.Height(28f)))
            {
                WriteExtensionRuntimeConfig();
            }

            if (GUILayout.Button("Open", GUILayout.Width(72f), GUILayout.Height(28f)))
            {
                Reveal(ExtensionRuntimeConfigPath());
            }

            EditorGUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(installMessage))
            {
                EditorGUILayout.HelpBox(installMessage, installMessage.StartsWith("Failed", StringComparison.Ordinal) ? MessageType.Error : MessageType.Info);
            }
        }

        private void DrawAgentsTab()
        {
            EditorGUILayout.LabelField("Agent Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This package no longer writes .mcp.json, .cursor, .codex, plugin, or skill files into the Unity project root. Open the setup guide and let the agent configure its own MCP client files outside the Unity project.",
                MessageType.Info);

            config.serverName = EditorGUILayout.TextField("Server Name", config.serverName);
            EditorGUILayout.LabelField("MCP URL", UnityMcpAgentConfigInstaller.BuildMcpUrl(config));

            EditorGUILayout.Space(8f);
            if (GUILayout.Button("Open setup-for-agent.md", GUILayout.Height(30f)))
            {
                OpenAgentSetupGuide();
            }

            if (!string.IsNullOrEmpty(installMessage))
            {
                EditorGUILayout.HelpBox(installMessage, installMessage.StartsWith("Failed", StringComparison.Ordinal) ? MessageType.Error : MessageType.Info);
            }

            EditorGUILayout.Space(10f);
            DrawPathButton("Setup Guide", UnityMcpAgentConfigInstaller.ResolveSetupGuidePath(UnityMcpPaths.ProjectRoot));
            DrawPathButton("Editor Config", UnityMcpPaths.ProjectConfigPath);
            DrawPathButton("MCP Proxy", UnityMcpAgentConfigInstaller.ResolveProxyScriptPath(UnityMcpPaths.ProjectRoot));
        }

        private void DrawTargetsTab()
        {
            EditorGUILayout.LabelField("Target Selection", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Select the preferred MCP target for this PC. Agents can also call targets.list to see the same Editor, Play Mode, and discovered phone/player endpoints.",
                MessageType.Info);

            EditorGUILayout.Space(6f);
            DrawSelectedTarget();

            EditorGUILayout.Space(8f);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh", GUILayout.Height(28f)))
            {
                targetMessage = null;
                Repaint();
            }

            EditorGUI.BeginDisabledGroup(!UnityMcpEditorBootstrap.IsRunning);
            if (GUILayout.Button("Scan LAN", GUILayout.Height(28f)))
            {
                ScanLanTargets();
            }

            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(UnityMcpEditorBootstrap.IsRunning);
            if (GUILayout.Button("Start Editor MCP", GUILayout.Height(28f)))
            {
                UnityMcpEditorBootstrap.StartEndpoint();
                targetMessage = "Editor MCP started.";
                Repaint();
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(targetMessage))
            {
                EditorGUILayout.HelpBox(targetMessage, MessageType.None);
            }

            var endpoint = UnityMcpEditorBootstrap.Endpoint;
            if (endpoint == null || !endpoint.IsRunning)
            {
                EditorGUILayout.HelpBox("Start the Editor MCP to list targets and run LAN discovery.", MessageType.Warning);
                return;
            }

            var list = endpoint.ListAllTargets();
            var targets = list == null ? null : list.targets;
            if (targets == null || targets.Length == 0)
            {
                EditorGUILayout.HelpBox("No targets are available yet.", MessageType.None);
                return;
            }

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Available Targets", EditorStyles.boldLabel);
            foreach (var target in targets)
            {
                DrawTargetRow(target);
            }
        }

        private void DrawSelectedTarget()
        {
            EditorGUILayout.LabelField("Selected", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Kind", string.IsNullOrEmpty(config.selectedTargetKind) ? "editor" : config.selectedTargetKind);
            EditorGUILayout.LabelField("ID", string.IsNullOrEmpty(config.selectedTargetId) ? "(not selected)" : config.selectedTargetId);
            EditorGUILayout.LabelField("Name", string.IsNullOrEmpty(config.selectedTargetName) ? "(not selected)" : config.selectedTargetName);
            EditorGUILayout.LabelField("URL", string.IsNullOrEmpty(config.selectedTargetUrl) ? "(through local Editor MCP)" : config.selectedTargetUrl);
        }

        private void DrawTargetRow(UnityMcpHeartbeat target)
        {
            if (target == null)
            {
                return;
            }

            var selected = IsSelectedTarget(target);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(BuildTargetDisplayName(target), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUI.BeginDisabledGroup(selected);
            if (GUILayout.Button(selected ? "Selected" : "Select", GUILayout.Width(76f)))
            {
                SelectTarget(target);
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            DrawTargetInfo("Kind", ResolveSelectedTargetKind(target));
            DrawTargetInfo("Source", string.IsNullOrEmpty(target.source) ? "(unknown)" : target.source);
            DrawTargetInfo("ID", target.endpointId);
            DrawTargetInfo("Group", target.name_group);
            DrawTargetInfo("Platform", target.platform);
            DrawTargetInfo("URL", string.IsNullOrEmpty(target.httpUrl) ? "(through local Editor MCP)" : target.httpUrl);
            EditorGUILayout.EndVertical();
        }

        private void DrawTargetInfo(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(70f));
            EditorGUILayout.SelectableLabel(value ?? string.Empty, EditorStyles.textField, GUILayout.Height(20f));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPathsTab()
        {
            EditorGUILayout.LabelField("Project Paths", EditorStyles.boldLabel);
            DrawPathButton("Project Root", UnityMcpPaths.ProjectRoot);
            DrawPathButton("Editor Config", UnityMcpPaths.ProjectConfigPath);
            DrawPathButton("Agent Setup", UnityMcpAgentConfigInstaller.ResolveSetupGuidePath(UnityMcpPaths.ProjectRoot));
            DrawPathButton("Mobile Config", ExtensionRuntimeConfigPath());
            DrawPathButton("Extension Root", UnityMcpPaths.ProjectExtensionRoot);
            DrawPathButton("Editor Tools", UnityMcpPaths.EditorToolsRoot());
            DrawPathButton("Runtime Tools", UnityMcpPaths.RuntimeToolsRoot());
            DrawPathButton("Skills", UnityMcpPaths.SkillsRoot());
            DrawPathButton("State Root", UnityMcpPaths.StateRoot);
            DrawPathButton("Temp Root", UnityMcpPaths.TempRoot);
            DrawPathButton("Proxy Script", UnityMcpAgentConfigInstaller.ResolveProxyScriptPath(UnityMcpPaths.ProjectRoot));

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Editor Config File", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(UnityMcpPaths.ProjectConfigPath, EditorStyles.textField, GUILayout.Height(20f));
        }

        private void DrawPathButton(string label, string path)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(110f));
            EditorGUILayout.SelectableLabel(path ?? string.Empty, EditorStyles.textField, GUILayout.Height(20f));
            EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(path));
            if (GUILayout.Button("Open", GUILayout.Width(58f)))
            {
                Reveal(path);
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }

        private void ScanLanTargets()
        {
            var endpoint = UnityMcpEditorBootstrap.Endpoint;
            if (endpoint == null || !endpoint.IsRunning)
            {
                targetMessage = "Editor MCP is not running.";
                return;
            }

            try
            {
                endpoint.CallToolAsync("lan.discovery.scan", new UnityMcpToolArguments()).GetAwaiter().GetResult();
                targetMessage = "LAN discovery scan sent. Matching name_group endpoints will appear after their heartbeat arrives.";
            }
            catch (Exception ex)
            {
                targetMessage = "Scan failed: " + ex.Message;
            }

            Repaint();
        }

        private void SelectTarget(UnityMcpHeartbeat target)
        {
            if (target == null)
            {
                return;
            }

            config.selectedTargetKind = ResolveSelectedTargetKind(target);
            config.selectedTargetId = target.endpointId ?? string.Empty;
            config.selectedTargetName = BuildTargetDisplayName(target);
            config.selectedTargetUrl = target.httpUrl ?? string.Empty;
            SaveConfig();
            targetMessage = "Selected target: " + config.selectedTargetName;
            Repaint();
        }

        private bool IsSelectedTarget(UnityMcpHeartbeat target)
        {
            if (target == null)
            {
                return false;
            }

            return string.Equals(config.selectedTargetKind, ResolveSelectedTargetKind(target), StringComparison.OrdinalIgnoreCase)
                && string.Equals(config.selectedTargetId, target.endpointId ?? string.Empty, StringComparison.Ordinal);
        }

        private static string ResolveSelectedTargetKind(UnityMcpHeartbeat target)
        {
            if (target == null)
            {
                return "editor";
            }

            if (string.Equals(target.source, "local-playmode", StringComparison.OrdinalIgnoreCase))
            {
                return "playmode";
            }

            if (string.Equals(target.endpointKind, "editor", StringComparison.OrdinalIgnoreCase))
            {
                return "editor";
            }

            return "player";
        }

        private static string BuildTargetDisplayName(UnityMcpHeartbeat target)
        {
            if (target == null)
            {
                return "(unknown)";
            }

            if (!string.IsNullOrEmpty(target.name))
            {
                return target.name;
            }

            if (!string.IsNullOrEmpty(target.endpointName))
            {
                return target.endpointName;
            }

            if (!string.IsNullOrEmpty(target.projectName))
            {
                return target.projectName;
            }

            return string.IsNullOrEmpty(target.endpointId) ? "(unknown)" : target.endpointId;
        }

        private string BuildStatusSummary()
        {
            return (UnityMcpEditorBootstrap.IsRunning ? "Editor MCP running" : "Editor MCP stopped")
                   + " | config " + (config == null || !config.editorAutoStart ? "manual" : "auto");
        }

        private string BuildHealthUrl()
        {
            var endpoint = UnityMcpEditorBootstrap.Endpoint;
            if (endpoint != null && !string.IsNullOrEmpty(endpoint.Url))
            {
                return endpoint.Url.TrimEnd('/') + "/health";
            }

            return "http://" + DisplayHost(config.editorBindAddress) + ":" + config.editorPort + "/health";
        }

        private static string DisplayHost(string bindAddress)
        {
            if (string.IsNullOrEmpty(bindAddress)
                || bindAddress == "0.0.0.0"
                || bindAddress == "*"
                || bindAddress == "+")
            {
                return "127.0.0.1";
            }

            return bindAddress;
        }

        private void ReloadConfig()
        {
            config = UnityMcpProjectConfigStore.LoadOrCreate();
            installMessage = null;
            targetMessage = null;
        }

        private void SaveConfig()
        {
            if (config == null)
            {
                return;
            }

            UnityMcpProjectConfigStore.Save(config);
        }

        private void WriteExtensionRuntimeConfig()
        {
            SaveConfig();
            var path = ExtensionRuntimeConfigPath();
            if (string.IsNullOrEmpty(path))
            {
                installMessage = "Failed: Unity project root was not resolved.";
                return;
            }

            AtomicFile.WriteJson(path, UnityMcpRuntimeConfig.FromProjectConfig(config), true);
            installMessage = "Wrote mobile MCP config: " + path;
        }

        private static string ExtensionRuntimeConfigPath()
        {
            return UnityMcpPaths.RuntimeConfigPath;
        }

        private static void Reveal(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var revealPath = path;
            if (!File.Exists(revealPath) && !Directory.Exists(revealPath))
            {
                var parent = Path.GetDirectoryName(revealPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                    revealPath = parent;
                }
            }

            EditorUtility.RevealInFinder(revealPath);
        }
    }
}
