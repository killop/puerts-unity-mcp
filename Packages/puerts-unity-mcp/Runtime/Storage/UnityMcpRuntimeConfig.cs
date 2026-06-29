using System;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace PuertsUnityMcp
{
    [Serializable]
    public sealed class UnityMcpRuntimeConfig
    {
        public const int LatestVersion = 4;

        public int version = LatestVersion;
        public string _comment_runtimeLanDirect = "Store the phone/player config at puerts-unity-mcp-extension/mobile-mcp-config.json. Run node Tools~/add-pum-to-build.mjs to copy it into StreamingAssets/PuertsUnityMcp/mobile-mcp-config.json and include PuerTS Unity MCP in player builds. Keep runtimeBindAddress as 0.0.0.0 and match name_group with the PC agent config for LAN direct MCP.";
        public string _comment_runtimeIo = "Runtime MCP uses HTTP and LAN discovery by default. Disk heartbeat, discovered endpoint cache, file command pump, file screenshots, and AOT miss logs are opt-in to keep phone IO low.";
        public bool runtimeAutoStart = true;
        public string runtimeBindAddress = "0.0.0.0";
        public int runtimePort = UnityMcpConstants.DefaultPlayerPort;
        public int runtimeLogBufferSize = 500;
        public bool lanDiscoveryEnabled = true;
        public string name = "";
        public string name_group = "default";
        public bool allowJsEval = true;
        public bool allowReflection = true;
        public bool allowPrivateReflection = true;
        public bool allowFileAccess = true;
        public bool allowNetworkAccess = true;
        public bool allowRuntimeCodeLoad = true;
        public string targetId = "";
        public int maxCommandsPerFrame = 4;
        public bool runInBackground = true;
        public bool enableFileCommandPump = false;
        public bool enableDiskHeartbeat = false;
        public bool enableDiscoveredEndpointCache = false;
        public bool enableAotMissLog = false;
        public string screenshotWriteMode = "memory";
        public int heartbeatIntervalMs = 30000;

        public static UnityMcpRuntimeConfig CreateDefault()
        {
            return new UnityMcpRuntimeConfig();
        }

        public static UnityMcpRuntimeConfig FromProjectConfig(UnityMcpProjectConfig projectConfig)
        {
            projectConfig = projectConfig ?? UnityMcpProjectConfig.CreateDefault();
            projectConfig.Normalize();
            return new UnityMcpRuntimeConfig
            {
                version = LatestVersion,
                _comment_runtimeLanDirect = "Store the phone/player config at puerts-unity-mcp-extension/mobile-mcp-config.json. Run node Tools~/add-pum-to-build.mjs to copy it into StreamingAssets/PuertsUnityMcp/mobile-mcp-config.json and include PuerTS Unity MCP in player builds. Keep runtimeBindAddress as 0.0.0.0 and match name_group with the PC agent config for LAN direct MCP.",
                _comment_runtimeIo = "Runtime MCP uses HTTP and LAN discovery by default. Disk heartbeat, discovered endpoint cache, file command pump, file screenshots, and AOT miss logs are opt-in to keep phone IO low.",
                runtimeAutoStart = projectConfig.runtimeAutoStart,
                runtimeBindAddress = projectConfig.runtimeBindAddress,
                runtimePort = projectConfig.runtimePort,
                runtimeLogBufferSize = projectConfig.runtimeLogBufferSize,
                lanDiscoveryEnabled = projectConfig.lanDiscoveryEnabled,
                name = projectConfig.name,
                name_group = projectConfig.name_group
            };
        }

        public void Normalize()
        {
            version = LatestVersion;
            _comment_runtimeLanDirect = string.IsNullOrEmpty(_comment_runtimeLanDirect)
                ? "Store the phone/player config at puerts-unity-mcp-extension/mobile-mcp-config.json. Run node Tools~/add-pum-to-build.mjs to copy it into StreamingAssets/PuertsUnityMcp/mobile-mcp-config.json and include PuerTS Unity MCP in player builds. Keep runtimeBindAddress as 0.0.0.0 and match name_group with the PC agent config for LAN direct MCP."
                : _comment_runtimeLanDirect;
            _comment_runtimeIo = string.IsNullOrEmpty(_comment_runtimeIo)
                ? "Runtime MCP uses HTTP and LAN discovery by default. Disk heartbeat, discovered endpoint cache, file command pump, file screenshots, and AOT miss logs are opt-in to keep phone IO low."
                : _comment_runtimeIo;
            runtimeBindAddress = string.IsNullOrEmpty(runtimeBindAddress) ? "0.0.0.0" : runtimeBindAddress;
            runtimePort = runtimePort <= 0 ? UnityMcpConstants.DefaultPlayerPort : runtimePort;
            runtimeLogBufferSize = runtimeLogBufferSize <= 0 ? 500 : runtimeLogBufferSize;
            name = name ?? string.Empty;
            name_group = string.IsNullOrEmpty(name_group) ? "default" : name_group.Trim();
            maxCommandsPerFrame = maxCommandsPerFrame <= 0 ? 4 : maxCommandsPerFrame;
            targetId = targetId ?? string.Empty;
            screenshotWriteMode = NormalizeScreenshotWriteMode(screenshotWriteMode);
            heartbeatIntervalMs = heartbeatIntervalMs <= 0 ? 30000 : heartbeatIntervalMs;
        }

        private static string NormalizeScreenshotWriteMode(string value)
        {
            if (string.Equals(value, "file", StringComparison.OrdinalIgnoreCase))
            {
                return "file";
            }

            if (string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                return "disabled";
            }

            return "memory";
        }
    }

    public static class UnityMcpRuntimeConfigStore
    {
        public static string LastLoadSource { get; private set; } = "not-loaded";
        public static string LastLoadError { get; private set; } = string.Empty;

        public static string PersistentConfigPath => UnityMcpPaths.RuntimeConfigPath;

        public static string StreamingAssetsConfigPath
        {
            get
            {
                return Path.Combine(
                    Application.streamingAssetsPath,
                    "PuertsUnityMcp",
                    UnityMcpConstants.RuntimeConfigFileName);
            }
        }

        public static string LegacyStreamingAssetsConfigPath
        {
            get
            {
                return Path.Combine(
                    Application.streamingAssetsPath,
                    "PuertsUnityMcp",
                    UnityMcpConstants.LegacyRuntimeConfigFileName);
            }
        }

        public static UnityMcpRuntimeConfig Load()
        {
            LastLoadSource = "default";
            LastLoadError = string.Empty;

            if (Application.isEditor && TryLoadProjectConfigForEditor(out var projectConfig, out var projectConfigPath))
            {
                LastLoadSource = projectConfigPath;
                Debug.Log("[UnityMCP] Runtime config loaded from editor project config: " + projectConfigPath);
                return UnityMcpRuntimeConfig.FromProjectConfig(projectConfig);
            }

            if (TryLoadFromPaths(UnityMcpPaths.RuntimeConfigReadPaths(), out var config))
            {
                return config;
            }

            if (TryLoadFromPath(StreamingAssetsConfigPath, out config))
            {
                return config;
            }

            if (TryLoadFromPath(LegacyStreamingAssetsConfigPath, out config))
            {
                return config;
            }

            Debug.LogWarning("[UnityMCP] Runtime config not found; using defaults. candidates="
                + string.Join(";", UnityMcpPaths.RuntimeConfigReadPaths() ?? new string[0])
                + ";" + StreamingAssetsConfigPath
                + ";" + LegacyStreamingAssetsConfigPath);
            return UnityMcpRuntimeConfig.CreateDefault();
        }

        private static bool TryLoadProjectConfigForEditor(out UnityMcpProjectConfig config, out string configPath)
        {
            config = null;
            configPath = string.Empty;
            var paths = UnityMcpPaths.ProjectConfigReadPaths();
            for (var i = 0; i < paths.Length; i++)
            {
                if (!string.IsNullOrEmpty(paths[i]) && File.Exists(paths[i]))
                {
                    config = UnityMcpProjectConfigStore.LoadFromPath(paths[i]);
                    configPath = paths[i];
                    return true;
                }
            }

            return false;
        }

        private static bool TryLoadFromPaths(string[] paths, out UnityMcpRuntimeConfig config)
        {
            config = null;
            if (paths == null)
            {
                return false;
            }

            for (var i = 0; i < paths.Length; i++)
            {
                if (TryLoadFromPath(paths[i], out config))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryLoadFromPath(string path, out UnityMcpRuntimeConfig config)
        {
            config = null;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                if (!TryReadAllText(path, out var json))
                {
                    return false;
                }

                if (TryLoadFromJson(json, out config))
                {
                    LastLoadSource = path;
                    Debug.Log("[UnityMCP] Runtime config loaded from: " + path);
                    return true;
                }

                LastLoadError = "Invalid JSON: " + path;
                Debug.LogWarning("[UnityMCP] Runtime config parse failed: " + path);
                return false;
            }
            catch (Exception ex)
            {
                LastLoadError = path + ": " + ex.Message;
                Debug.LogWarning("[UnityMCP] Runtime config read failed: " + path + " error=" + ex.Message);
                config = null;
                return false;
            }
        }

        private static bool TryReadAllText(string path, out string text)
        {
            text = null;
            if (!IsUnityWebRequestPath(path))
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                text = File.ReadAllText(path);
                return true;
            }

            using (var request = UnityWebRequest.Get(path))
            {
                var operation = request.SendWebRequest();
                var started = DateTime.UtcNow;
                while (!operation.isDone)
                {
                    if ((DateTime.UtcNow - started).TotalSeconds > 5)
                    {
                        throw new TimeoutException("Timed out reading " + path);
                    }

                    Thread.Sleep(1);
                }

#if UNITY_2020_2_OR_NEWER
                if (request.result != UnityWebRequest.Result.Success)
#else
                if (request.isNetworkError || request.isHttpError)
#endif
                {
                    throw new IOException(request.error);
                }

                text = request.downloadHandler.text;
                return true;
            }
        }

        private static bool IsUnityWebRequestPath(string path)
        {
            return path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryLoadFromJson(string json, out UnityMcpRuntimeConfig config)
        {
            config = null;
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            try
            {
                config = UnityMcpRuntimeConfig.CreateDefault();
                UnityJson.FromJsonOverwrite(json, config);
                config.Normalize();
                return true;
            }
            catch (Exception ex)
            {
                LastLoadError = ex.Message;
                config = null;
                return false;
            }
        }
    }
}
