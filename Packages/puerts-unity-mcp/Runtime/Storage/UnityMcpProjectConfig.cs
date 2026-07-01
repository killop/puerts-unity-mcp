using System;
using System.IO;

namespace PuertsUnityMcp
{
    [Serializable]
    public sealed class UnityMcpProjectConfig
    {
        public const int LatestVersion = 5;

        public int version = LatestVersion;
        public string _comment_editorBindAddress = "Use 127.0.0.1 for local-only access. Use 0.0.0.0 when other computers need to connect to this Editor MCP by explicit URL.";
        public string _comment_runtimeBindAddress = "Use 0.0.0.0 for APK/IPA/standalone LAN direct so a PC agent can connect to the embedded Player MCP by explicit URL. 127.0.0.1 makes the Player MCP local-only inside the device.";
        public string _comment_directTargets = "No LAN discovery is performed. Set selectedTargetUrl, pass --target-url, or set PUERTS_UNITY_MCP_TARGET_URL when connecting to a remote Unity Editor or phone/player MCP.";
        public bool editorAutoStart = true;
        public string editorBindAddress = "127.0.0.1";
        public int editorPort = UnityMcpConstants.DefaultEditorPort;
        public bool runtimeAutoStart = true;
        public string runtimeBindAddress = "0.0.0.0";
        public int runtimePort = UnityMcpConstants.DefaultPlayerPort;
        public int runtimeLogBufferSize = 500;
        public string name = "";
        public string selectedTargetKind = "editor";
        public string selectedTargetId = "";
        public string selectedTargetName = "";
        public string selectedTargetUrl = "";
        public string serverName = "puerts-unity-mcp";

        public static UnityMcpProjectConfig CreateDefault()
        {
            return new UnityMcpProjectConfig();
        }

        public void Normalize()
        {
            version = LatestVersion;
            _comment_editorBindAddress = "Use 127.0.0.1 for local-only access. Use 0.0.0.0 when other computers need to connect to this Editor MCP by explicit URL.";
            _comment_runtimeBindAddress = "Use 0.0.0.0 for APK/IPA/standalone LAN direct so a PC agent can connect to the embedded Player MCP by explicit URL. 127.0.0.1 makes the Player MCP local-only inside the device.";
            _comment_directTargets = "No LAN discovery is performed. Set selectedTargetUrl, pass --target-url, or set PUERTS_UNITY_MCP_TARGET_URL when connecting to a remote Unity Editor or phone/player MCP.";
            editorBindAddress = string.IsNullOrEmpty(editorBindAddress) ? "127.0.0.1" : editorBindAddress;
            runtimeBindAddress = string.IsNullOrEmpty(runtimeBindAddress) ? "0.0.0.0" : runtimeBindAddress;
            editorPort = editorPort <= 0 ? UnityMcpConstants.DefaultEditorPort : editorPort;
            runtimePort = runtimePort <= 0 ? UnityMcpConstants.DefaultPlayerPort : runtimePort;
            runtimeLogBufferSize = runtimeLogBufferSize <= 0 ? 500 : runtimeLogBufferSize;
            name = name ?? string.Empty;
            selectedTargetKind = string.IsNullOrEmpty(selectedTargetKind) ? "editor" : selectedTargetKind;
            selectedTargetId = selectedTargetId ?? string.Empty;
            selectedTargetName = selectedTargetName ?? string.Empty;
            selectedTargetUrl = selectedTargetUrl ?? string.Empty;
            serverName = string.IsNullOrEmpty(serverName) ? "puerts-unity-mcp" : UnityMcpPaths.SanitizeId(serverName);
        }
    }

    public static class UnityMcpProjectConfigStore
    {
        public static UnityMcpProjectConfig LoadOrCreate()
        {
            var path = UnityMcpPaths.ProjectConfigPath;
            var config = LoadFirstExisting(UnityMcpPaths.ProjectConfigReadPaths());
            SaveToPath(path, config);
            return config;
        }

        public static UnityMcpProjectConfig Load()
        {
            return LoadFirstExisting(UnityMcpPaths.ProjectConfigReadPaths());
        }

        public static void Save(UnityMcpProjectConfig config)
        {
            SaveToPath(UnityMcpPaths.ProjectConfigPath, config);
        }

        public static UnityMcpProjectConfig LoadFromPath(string path)
        {
            UnityMcpProjectConfig config = null;
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    config = UnityMcpProjectConfig.CreateDefault();
                    UnityJson.FromJsonOverwrite(File.ReadAllText(path), config);
                }
            }
            catch
            {
                config = null;
            }

            config = config ?? UnityMcpProjectConfig.CreateDefault();
            config.Normalize();
            return config;
        }

        public static void SaveToPath(string path, UnityMcpProjectConfig config)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            config = config ?? UnityMcpProjectConfig.CreateDefault();
            config.Normalize();
            try
            {
                AtomicFile.WriteJson(path, config, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static UnityMcpProjectConfig LoadFirstExisting(string[] paths)
        {
            if (paths != null)
            {
                for (var i = 0; i < paths.Length; i++)
                {
                    if (!string.IsNullOrEmpty(paths[i]) && File.Exists(paths[i]))
                    {
                        return LoadFromPath(paths[i]);
                    }
                }
            }

            return UnityMcpProjectConfig.CreateDefault();
        }
    }
}
