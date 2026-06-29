using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PuertsUnityMcp.Editor
{
    public sealed class UnityMcpJavaScriptEditorWindow : EditorWindow
    {
        private const string ScriptFileName = "puerts-unity-mcp-window.mjs";
        private const string BridgeGlobalName = "__unity_mcp_window";
        private const string ModuleGlobalName = "__unity_mcp_window_module";
        private const string ScriptFolderName = "JavaScriptWindows";

        private PuertsScriptHost scriptHost;
        private UnityMcpJavaScriptEditorWindowBridge bridge;
        private UnityMcpProjectConfig config;
        private Action onEnableCallback;
        private Action onDisableCallback;
        private Action onGuiCallback;
        private Action onInspectorUpdateCallback;
        private string scriptPath;
        private string scriptSource;
        private string scriptError;
        private string message;
        private Vector2 scrollPosition;
        private bool reloadQueued;

        internal UnityMcpProjectConfig Config
        {
            get
            {
                if (config == null)
                {
                    config = UnityMcpProjectConfigStore.LoadOrCreate();
                }

                return config;
            }
        }

        internal string ScriptPath => scriptPath;
        internal string ScriptSource => scriptSource;
        internal string ScriptError => scriptError;
        internal string Message => message;

        [MenuItem("Window/PuerTS Unity MCP/JavaScript Window", priority = 1)]
        public static void OpenWindow()
        {
            var window = GetWindow<UnityMcpJavaScriptEditorWindow>();
            window.titleContent = new GUIContent("PuerTS MCP JS");
            window.minSize = new Vector2(620f, 500f);
            window.Show();
        }

        [MenuItem("PuerTS Unity MCP/JavaScript Window", priority = 1)]
        private static void OpenLegacyMenuWindow()
        {
            OpenWindow();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("PuerTS MCP JS");
            minSize = new Vector2(620f, 500f);
            LoadScript();
            InvokeCallback(onEnableCallback);
        }

        private void OnDisable()
        {
            DisposeScript(true);
        }

        private void OnGUI()
        {
            if (onGuiCallback == null)
            {
                DrawFallbackGui();
                return;
            }

            try
            {
                onGuiCallback();
            }
            catch (Exception ex)
            {
                scriptError = ex.ToString();
                DrawFallbackGui();
            }
        }

        private void OnInspectorUpdate()
        {
            try
            {
                scriptHost?.Tick();
                onInspectorUpdateCallback?.Invoke();
            }
            catch (Exception ex)
            {
                scriptError = ex.ToString();
            }

            Repaint();
        }

        internal void QueueReloadScript()
        {
            if (reloadQueued)
            {
                return;
            }

            reloadQueued = true;
            EditorApplication.delayCall += () =>
            {
                reloadQueued = false;
                if (this == null)
                {
                    return;
                }

                ReloadScriptNow();
            };
        }

        internal void ReloadScriptNow()
        {
            DisposeScript(true);
            LoadScript();
            InvokeCallback(onEnableCallback);
            Repaint();
        }

        internal void ReloadConfig()
        {
            config = UnityMcpProjectConfigStore.LoadOrCreate();
            message = "Config reloaded.";
        }

        internal void SaveConfig()
        {
            UnityMcpProjectConfigStore.Save(Config);
        }

        internal void SetMessage(string value)
        {
            message = value ?? string.Empty;
        }

        internal void ClearMessage()
        {
            message = string.Empty;
        }

        internal Vector2 BeginWindowScrollView()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            return scrollPosition;
        }

        internal void EndWindowScrollView()
        {
            EditorGUILayout.EndScrollView();
        }

        internal UnityMcpJavaScriptEditorWindowSnapshot BuildSnapshot()
        {
            var currentConfig = Config;
            var endpoint = UnityMcpEditorBootstrap.Endpoint;
            var runtime = UnityMcpRuntimeHost.Instance;
            var targets = endpoint != null && endpoint.IsRunning
                ? endpoint.ListAllTargets().targets
                : new UnityMcpHeartbeat[0];

            return new UnityMcpJavaScriptEditorWindowSnapshot
            {
                packageVersion = UnityMcpConstants.Version,
                scriptPath = scriptPath ?? string.Empty,
                scriptSource = scriptSource ?? string.Empty,
                scriptError = scriptError ?? string.Empty,
                message = message ?? string.Empty,
                projectRoot = UnityMcpPaths.ProjectRoot ?? string.Empty,
                stateRoot = UnityMcpPaths.StateRoot ?? string.Empty,
                editorConfigPath = UnityMcpPaths.ProjectConfigPath ?? string.Empty,
                runtimeConfigPath = UnityMcpPaths.RuntimeConfigPath ?? string.Empty,
                setupGuidePath = UnityMcpAgentConfigInstaller.ResolveSetupGuidePath(UnityMcpPaths.ProjectRoot) ?? string.Empty,
                proxyScriptPath = UnityMcpAgentConfigInstaller.ResolveProxyScriptPath(UnityMcpPaths.ProjectRoot) ?? string.Empty,
                editorRunning = UnityMcpEditorBootstrap.IsRunning,
                editorEndpointId = endpoint == null ? string.Empty : endpoint.EndpointId,
                editorHealthUrl = BuildEditorHealthUrl(currentConfig),
                runtimeActive = runtime != null,
                runtimeEndpointId = runtime == null ? string.Empty : runtime.EndpointId,
                runtimeHealthUrl = runtime == null ? string.Empty : runtime.BuildHealth().httpUrl,
                isCompiling = EditorApplication.isCompiling,
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                selectedTargetKind = currentConfig.selectedTargetKind ?? string.Empty,
                selectedTargetId = currentConfig.selectedTargetId ?? string.Empty,
                selectedTargetName = currentConfig.selectedTargetName ?? string.Empty,
                selectedTargetUrl = currentConfig.selectedTargetUrl ?? string.Empty,
                config = currentConfig,
                targets = BuildTargetRows(targets)
            };
        }

        private void LoadScript()
        {
            scriptError = string.Empty;
            try
            {
                scriptPath = ResolveScriptPath(out scriptSource);
                bridge = new UnityMcpJavaScriptEditorWindowBridge(this);
                scriptHost = new PuertsScriptHost("editor-window:" + GetInstanceID());
                scriptHost.SetGlobal(BridgeGlobalName, bridge);
                scriptHost.Execute(File.ReadAllText(scriptPath, Encoding.UTF8), "mcp://editor-window/" + Path.GetFileName(scriptPath));
                onEnableCallback = LoadCallback("onEnable");
                onDisableCallback = LoadCallback("onDisable");
                onGuiCallback = LoadCallback("onGUI");
                onInspectorUpdateCallback = LoadCallback("onInspectorUpdate");
            }
            catch (Exception ex)
            {
                scriptError = ex.ToString();
                onEnableCallback = null;
                onDisableCallback = null;
                onGuiCallback = null;
                onInspectorUpdateCallback = null;
            }
        }

        private Action LoadCallback(string functionName)
        {
            var expression = "(function() {"
                + "var module = globalThis." + ModuleGlobalName + ";"
                + "var bridge = globalThis." + BridgeGlobalName + ";"
                + "if (!module || typeof module." + functionName + " !== 'function') return function() {};"
                + "return function() { module." + functionName + "(bridge); };"
                + "})()";
            return scriptHost.EvalRaw<Action>(expression, "mcp://editor-window/callback/" + functionName + ".js");
        }

        private void DisposeScript(bool invokeDisable)
        {
            if (invokeDisable)
            {
                InvokeCallback(onDisableCallback);
            }

            onEnableCallback = null;
            onDisableCallback = null;
            onGuiCallback = null;
            onInspectorUpdateCallback = null;
            bridge = null;

            if (scriptHost != null)
            {
                scriptHost.Dispose();
                scriptHost = null;
            }
        }

        private static void InvokeCallback(Action callback)
        {
            if (callback == null)
            {
                return;
            }

            try
            {
                callback();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityMCP] JavaScript EditorWindow callback failed: " + ex.Message);
            }
        }

        private void DrawFallbackGui()
        {
            EditorGUILayout.LabelField("PuerTS Unity MCP JavaScript Window", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("The JavaScript window script did not load. Fix the script and press Reload JS.", MessageType.Warning);
            if (!string.IsNullOrEmpty(scriptPath))
            {
                EditorGUILayout.LabelField("Script", scriptPath);
            }

            if (!string.IsNullOrEmpty(scriptError))
            {
                EditorGUILayout.HelpBox(scriptError, MessageType.Error);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Reload JS", GUILayout.Height(28f)))
            {
                ReloadScriptNow();
            }

            if (GUILayout.Button("Open Script", GUILayout.Height(28f)))
            {
                UnityMcpJavaScriptEditorWindowBridge.RevealPath(scriptPath);
            }

            EditorGUILayout.EndHorizontal();
        }

        private static string ResolveScriptPath(out string source)
        {
            var overridePath = ResolveProjectOverrideScriptPath();
            if (File.Exists(overridePath))
            {
                source = "project";
                return overridePath;
            }

            var packagePath = ResolvePackageScriptPath();
            if (File.Exists(packagePath))
            {
                source = "package";
                return packagePath;
            }

            source = "missing";
            return packagePath;
        }

        internal static string ResolveProjectOverrideScriptPath()
        {
            return Path.Combine(UnityMcpPaths.EditorExtensionRoot, ScriptFolderName, ScriptFileName);
        }

        internal static string ResolvePackageScriptPath()
        {
            var packageRoot = ResolvePackageRoot();
            return Path.Combine(packageRoot, "Editor", ScriptFolderName, ScriptFileName);
        }

        private static string ResolvePackageRoot()
        {
            try
            {
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityMcpConstants).Assembly);
                if (packageInfo != null && !string.IsNullOrEmpty(packageInfo.resolvedPath))
                {
                    return packageInfo.resolvedPath;
                }
            }
            catch
            {
            }

            return Path.GetFullPath(Path.Combine("Packages", UnityMcpConstants.PackageName));
        }

        private static string BuildEditorHealthUrl(UnityMcpProjectConfig currentConfig)
        {
            var endpoint = UnityMcpEditorBootstrap.Endpoint;
            if (endpoint != null && !string.IsNullOrEmpty(endpoint.Url))
            {
                return endpoint.Url.TrimEnd('/') + "/health";
            }

            return "http://" + DisplayHost(currentConfig.editorBindAddress) + ":" + currentConfig.editorPort + "/health";
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

        private static UnityMcpJavaScriptEditorWindowTarget[] BuildTargetRows(UnityMcpHeartbeat[] targets)
        {
            if (targets == null || targets.Length == 0)
            {
                return new UnityMcpJavaScriptEditorWindowTarget[0];
            }

            var rows = new UnityMcpJavaScriptEditorWindowTarget[targets.Length];
            for (var i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                rows[i] = new UnityMcpJavaScriptEditorWindowTarget
                {
                    index = i,
                    kind = ResolveSelectedTargetKind(target),
                    id = target == null ? string.Empty : target.endpointId ?? string.Empty,
                    name = BuildTargetDisplayName(target),
                    group = target == null ? string.Empty : target.name_group ?? string.Empty,
                    source = target == null ? string.Empty : target.source ?? string.Empty,
                    platform = target == null ? string.Empty : target.platform ?? string.Empty,
                    url = target == null ? string.Empty : target.httpUrl ?? string.Empty
                };
            }

            return rows;
        }

        internal static string ResolveSelectedTargetKind(UnityMcpHeartbeat target)
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

        internal static string BuildTargetDisplayName(UnityMcpHeartbeat target)
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
    }

    public sealed class UnityMcpJavaScriptEditorWindowBridge
    {
        private readonly UnityMcpJavaScriptEditorWindow owner;

        public UnityMcpJavaScriptEditorWindowBridge(UnityMcpJavaScriptEditorWindow owner)
        {
            this.owner = owner;
        }

        public string SnapshotJson()
        {
            return UnityJson.ToJson(owner.BuildSnapshot());
        }

        public void Label(string text)
        {
            EditorGUILayout.LabelField(text ?? string.Empty);
        }

        public void BoldLabel(string text)
        {
            EditorGUILayout.LabelField(text ?? string.Empty, EditorStyles.boldLabel);
        }

        public void MiniLabel(string text)
        {
            EditorGUILayout.LabelField(text ?? string.Empty, EditorStyles.miniLabel);
        }

        public void SelectableLabel(string text)
        {
            EditorGUILayout.SelectableLabel(text ?? string.Empty, EditorStyles.textField, GUILayout.Height(20f));
        }

        public void HelpBox(string text, string type)
        {
            EditorGUILayout.HelpBox(text ?? string.Empty, ParseMessageType(type));
        }

        public bool Button(string text)
        {
            return GUILayout.Button(text ?? string.Empty, GUILayout.Height(28f));
        }

        public bool ToolbarButton(string text, float width)
        {
            return GUILayout.Button(text ?? string.Empty, EditorStyles.toolbarButton, GUILayout.Width(width));
        }

        public int Toolbar(int selected, string labels)
        {
            return GUILayout.Toolbar(selected, SplitLabels(labels));
        }

        public string TextField(string label, string value)
        {
            return EditorGUILayout.TextField(label ?? string.Empty, value ?? string.Empty);
        }

        public int IntField(string label, int value)
        {
            return EditorGUILayout.IntField(label ?? string.Empty, value);
        }

        public bool Toggle(string label, bool value)
        {
            return EditorGUILayout.Toggle(label ?? string.Empty, value);
        }

        public void BeginHorizontal()
        {
            EditorGUILayout.BeginHorizontal();
        }

        public void EndHorizontal()
        {
            EditorGUILayout.EndHorizontal();
        }

        public void BeginVerticalBox()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        }

        public void EndVertical()
        {
            EditorGUILayout.EndVertical();
        }

        public void BeginToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        }

        public void EndToolbar()
        {
            EditorGUILayout.EndHorizontal();
        }

        public void BeginDisabled(bool disabled)
        {
            EditorGUI.BeginDisabledGroup(disabled);
        }

        public void EndDisabled()
        {
            EditorGUI.EndDisabledGroup();
        }

        public void BeginScrollView()
        {
            owner.BeginWindowScrollView();
        }

        public void EndScrollView()
        {
            owner.EndWindowScrollView();
        }

        public void Space(float pixels)
        {
            EditorGUILayout.Space(pixels);
        }

        public void FlexibleSpace()
        {
            GUILayout.FlexibleSpace();
        }

        public void StartEditorMcp()
        {
            UnityMcpEditorBootstrap.StartEndpoint();
            owner.SetMessage("Editor MCP started.");
        }

        public void StopEditorMcp()
        {
            UnityMcpEditorBootstrap.StopEndpoint();
            owner.SetMessage("Editor MCP stopped.");
        }

        public void ReloadConfig()
        {
            owner.ReloadConfig();
        }

        public void SaveConfig()
        {
            owner.SaveConfig();
            owner.SetMessage("Config saved.");
        }

        public void SetConfigString(string key, string value)
        {
            var config = owner.Config;
            switch (key)
            {
                case "editorBindAddress":
                    config.editorBindAddress = value ?? string.Empty;
                    break;
                case "runtimeBindAddress":
                    config.runtimeBindAddress = value ?? string.Empty;
                    break;
                case "name":
                    config.name = value ?? string.Empty;
                    break;
                case "name_group":
                    config.name_group = value ?? string.Empty;
                    break;
                case "serverName":
                    config.serverName = value ?? string.Empty;
                    break;
            }

            owner.SaveConfig();
        }

        public void SetConfigInt(string key, int value)
        {
            var config = owner.Config;
            switch (key)
            {
                case "editorPort":
                    config.editorPort = value;
                    break;
                case "runtimePort":
                    config.runtimePort = value;
                    break;
                case "runtimeLogBufferSize":
                    config.runtimeLogBufferSize = value;
                    break;
            }

            owner.SaveConfig();
        }

        public void SetConfigBool(string key, bool value)
        {
            var config = owner.Config;
            switch (key)
            {
                case "editorAutoStart":
                    config.editorAutoStart = value;
                    break;
                case "runtimeAutoStart":
                    config.runtimeAutoStart = value;
                    break;
                case "lanDiscoveryEnabled":
                    config.lanDiscoveryEnabled = value;
                    break;
            }

            owner.SaveConfig();
        }

        public void ScanLanTargets()
        {
            var endpoint = UnityMcpEditorBootstrap.Endpoint;
            if (endpoint == null || !endpoint.IsRunning)
            {
                owner.SetMessage("Editor MCP is not running.");
                return;
            }

            try
            {
                endpoint.CallToolAsync("lan.discovery.scan", new UnityMcpToolArguments()).GetAwaiter().GetResult();
                owner.SetMessage("LAN discovery scan sent.");
            }
            catch (Exception ex)
            {
                owner.SetMessage("Scan failed: " + ex.Message);
            }
        }

        public void SelectTarget(int index)
        {
            var endpoint = UnityMcpEditorBootstrap.Endpoint;
            var targets = endpoint != null && endpoint.IsRunning
                ? endpoint.ListAllTargets().targets
                : new UnityMcpHeartbeat[0];
            if (targets == null || index < 0 || index >= targets.Length)
            {
                owner.SetMessage("Target index is not available.");
                return;
            }

            var target = targets[index];
            var config = owner.Config;
            config.selectedTargetKind = UnityMcpJavaScriptEditorWindow.ResolveSelectedTargetKind(target);
            config.selectedTargetId = target.endpointId ?? string.Empty;
            config.selectedTargetName = UnityMcpJavaScriptEditorWindow.BuildTargetDisplayName(target);
            config.selectedTargetUrl = target.httpUrl ?? string.Empty;
            owner.SaveConfig();
            owner.SetMessage("Selected target: " + config.selectedTargetName);
        }

        public void WriteRuntimeResourcesConfig()
        {
            try
            {
                owner.SaveConfig();
                AtomicFile.WriteJson(UnityMcpPaths.RuntimeConfigPath, UnityMcpRuntimeConfig.FromProjectConfig(owner.Config), true);
                owner.SetMessage("Wrote mobile MCP config: " + UnityMcpPaths.RuntimeConfigPath);
            }
            catch (Exception ex)
            {
                owner.SetMessage("Write mobile MCP config failed: " + ex.Message);
            }
        }

        public void OpenCSharpSettings()
        {
            UnityMcpSettingsWindow.OpenWindow();
        }

        public void OpenSetupGuide()
        {
            RevealPath(UnityMcpAgentConfigInstaller.ResolveSetupGuidePath(UnityMcpPaths.ProjectRoot));
        }

        public void OpenEditorConfig()
        {
            RevealPath(UnityMcpPaths.ProjectConfigPath);
        }

        public void OpenStateRoot()
        {
            RevealPath(UnityMcpPaths.StateRoot);
        }

        public void OpenProjectRoot()
        {
            RevealPath(UnityMcpPaths.ProjectRoot);
        }

        public void OpenRuntimeConfig()
        {
            RevealPath(UnityMcpPaths.RuntimeConfigPath);
        }

        public void OpenScript()
        {
            RevealPath(owner.ScriptPath);
        }

        public void OpenOverrideFolder()
        {
            var path = Path.GetDirectoryName(UnityMcpJavaScriptEditorWindow.ResolveProjectOverrideScriptPath());
            if (!string.IsNullOrEmpty(path))
            {
                Directory.CreateDirectory(path);
            }

            RevealPath(path);
        }

        public void CreateProjectOverrideScript()
        {
            var targetPath = UnityMcpJavaScriptEditorWindow.ResolveProjectOverrideScriptPath();
            if (File.Exists(targetPath))
            {
                owner.SetMessage("Project override already exists: " + targetPath);
                RevealPath(targetPath);
                return;
            }

            var sourcePath = UnityMcpJavaScriptEditorWindow.ResolvePackageScriptPath();
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            File.Copy(sourcePath, targetPath);
            owner.SetMessage("Created project override: " + targetPath);
            RevealPath(targetPath);
        }

        public void ReloadScript()
        {
            owner.QueueReloadScript();
        }

        public void Repaint()
        {
            owner.Repaint();
        }

        public void ClearMessage()
        {
            owner.ClearMessage();
        }

        public static void RevealPath(string path)
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

        private static MessageType ParseMessageType(string type)
        {
            if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
            {
                return MessageType.Error;
            }

            if (string.Equals(type, "warning", StringComparison.OrdinalIgnoreCase))
            {
                return MessageType.Warning;
            }

            if (string.Equals(type, "info", StringComparison.OrdinalIgnoreCase))
            {
                return MessageType.Info;
            }

            return MessageType.None;
        }

        private static string[] SplitLabels(string labels)
        {
            return string.IsNullOrEmpty(labels)
                ? new[] { "Default" }
                : labels.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }

    [Serializable]
    public sealed class UnityMcpJavaScriptEditorWindowSnapshot
    {
        public string packageVersion;
        public string scriptPath;
        public string scriptSource;
        public string scriptError;
        public string message;
        public string projectRoot;
        public string stateRoot;
        public string editorConfigPath;
        public string runtimeConfigPath;
        public string setupGuidePath;
        public string proxyScriptPath;
        public bool editorRunning;
        public string editorEndpointId;
        public string editorHealthUrl;
        public bool runtimeActive;
        public string runtimeEndpointId;
        public string runtimeHealthUrl;
        public bool isCompiling;
        public bool isPlaying;
        public bool isPaused;
        public string selectedTargetKind;
        public string selectedTargetId;
        public string selectedTargetName;
        public string selectedTargetUrl;
        public UnityMcpProjectConfig config;
        public UnityMcpJavaScriptEditorWindowTarget[] targets = new UnityMcpJavaScriptEditorWindowTarget[0];
    }

    [Serializable]
    public sealed class UnityMcpJavaScriptEditorWindowTarget
    {
        public int index;
        public string kind;
        public string id;
        public string name;
        public string group;
        public string source;
        public string platform;
        public string url;
    }
}
