using System;
using System.IO;

namespace PuertsUnityMcp
{
    [Serializable]
    public sealed class UnityMcpProjectConfig
    {
        public const int LatestVersion = 4;

        public int version = LatestVersion;
        public string _comment_editorBindAddress = "Use 127.0.0.1 for local-only access. Use 0.0.0.0 when other computers need to connect to this Editor MCP. If discovery shows this Editor but remote machines cannot connect, editorBindAddress is probably still bound to 127.0.0.1.";
        public string _comment_runtimeBindAddress = "Use 0.0.0.0 for APK/IPA/standalone LAN direct so a PC agent can connect to the embedded Player MCP. 127.0.0.1 makes the Player MCP local-only inside the device.";
        public string _comment_lanDiscovery = "LAN discovery only accepts endpoints with the same name_group. PC agents can connect directly to discovered APK/IPA Player MCP endpoints without opening the Unity Editor. UDP broadcast/multicast may be blocked by firewalls, AP isolation, VLAN routing, or network administrator policy, and usually only works inside one subnet. Use lanHttpProbeHosts or lanHttpProbeCidrs such as 192.168.1.0/24 as the TCP/HTTP fallback.";
        public bool editorAutoStart = true;
        public string editorBindAddress = "127.0.0.1";
        public int editorPort = UnityMcpConstants.DefaultEditorPort;
        public bool runtimeAutoStart = true;
        public string runtimeBindAddress = "0.0.0.0";
        public int runtimePort = UnityMcpConstants.DefaultPlayerPort;
        public int runtimeLogBufferSize = 500;
        public bool lanDiscoveryEnabled = true;
        public string[] lanHttpProbeHosts = new string[0];
        public string[] lanHttpProbeCidrs = new string[0];
        public int lanHttpProbeTimeoutMs = 1000;
        public string name = "";
        public string name_group = "default";
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
            _comment_editorBindAddress = string.IsNullOrEmpty(_comment_editorBindAddress)
                ? "Use 127.0.0.1 for local-only access. Use 0.0.0.0 when other computers need to connect to this Editor MCP. If discovery shows this Editor but remote machines cannot connect, editorBindAddress is probably still bound to 127.0.0.1."
                : _comment_editorBindAddress;
            _comment_lanDiscovery = string.IsNullOrEmpty(_comment_lanDiscovery)
                ? "LAN discovery only accepts endpoints with the same name_group. PC agents can connect directly to discovered APK/IPA Player MCP endpoints without opening the Unity Editor. UDP broadcast/multicast may be blocked by firewalls, AP isolation, VLAN routing, or network administrator policy, and usually only works inside one subnet. Use lanHttpProbeHosts or lanHttpProbeCidrs such as 192.168.1.0/24 as the TCP/HTTP fallback."
                : _comment_lanDiscovery;
            _comment_runtimeBindAddress = string.IsNullOrEmpty(_comment_runtimeBindAddress)
                ? "Use 0.0.0.0 for APK/IPA/standalone LAN direct so a PC agent can connect to the embedded Player MCP. 127.0.0.1 makes the Player MCP local-only inside the device."
                : _comment_runtimeBindAddress;
            editorBindAddress = string.IsNullOrEmpty(editorBindAddress) ? "127.0.0.1" : editorBindAddress;
            runtimeBindAddress = string.IsNullOrEmpty(runtimeBindAddress) ? "0.0.0.0" : runtimeBindAddress;
            editorPort = editorPort <= 0 ? UnityMcpConstants.DefaultEditorPort : editorPort;
            runtimePort = runtimePort <= 0 ? UnityMcpConstants.DefaultPlayerPort : runtimePort;
            runtimeLogBufferSize = runtimeLogBufferSize <= 0 ? 500 : runtimeLogBufferSize;
            lanHttpProbeHosts = lanHttpProbeHosts ?? new string[0];
            lanHttpProbeCidrs = lanHttpProbeCidrs ?? new string[0];
            lanHttpProbeTimeoutMs = lanHttpProbeTimeoutMs <= 0 ? 1000 : Math.Min(lanHttpProbeTimeoutMs, 10000);
            name = name ?? string.Empty;
            name_group = string.IsNullOrEmpty(name_group) ? "default" : name_group.Trim();
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
            AtomicFile.WriteJson(path, config, true);
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
