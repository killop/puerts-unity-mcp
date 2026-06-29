using System;
using System.IO;

namespace PuertsUnityMcp.Editor
{
    public static class UnityMcpAgentConfigInstaller
    {
        private const string ProxyScriptName = "puerts-unity-mcp-stdio-proxy.js";
        private const string SetupGuideFileName = "setup-for-agent.md";

        public static UnityMcpAgentInstallResult InstallFromProjectConfig()
        {
            return RemovedInstallerResult();
        }

        public static UnityMcpAgentInstallResult InstallFromConfig(UnityMcpProjectConfig config, string mcpUrl)
        {
            return RemovedInstallerResult();
        }

        public static UnityMcpAgentInstallResult InstallAtProjectRoot(string projectRoot, UnityMcpProjectConfig config, string mcpUrl, string proxyScriptPath = null)
        {
            return RemovedInstallerResult();
        }

        public static UnityMcpAgentInstallResult InstallAtAgentRoot(string agentRoot, string unityProjectRoot, UnityMcpProjectConfig config, string mcpUrl, string proxyScriptPath = null)
        {
            return RemovedInstallerResult();
        }

        public static string BuildMcpUrl(UnityMcpProjectConfig config)
        {
            config = config ?? UnityMcpProjectConfig.CreateDefault();
            config.Normalize();
            var host = NormalizeDisplayHost(config.editorBindAddress);
            return "http://" + host + ":" + config.editorPort + "/mcp";
        }

        public static string ResolveAgentRoot(UnityMcpProjectConfig config, string unityProjectRoot)
        {
            return unityProjectRoot;
        }

        public static string ResolveProjectConfigPath(string unityProjectRoot)
        {
            if (string.IsNullOrEmpty(unityProjectRoot))
            {
                return null;
            }

            return Path.Combine(
                unityProjectRoot,
                UnityMcpConstants.ExtensionDirectoryName,
                UnityMcpConstants.ConfigFileName);
        }

        public static string ResolveProxyScriptPath(string unityProjectRoot)
        {
            return ResolvePackageFile(unityProjectRoot, "Tools~", ProxyScriptName);
        }

        public static string ResolveSetupGuidePath(string unityProjectRoot)
        {
            return ResolvePackageFile(unityProjectRoot, SetupGuideFileName);
        }

        private static UnityMcpAgentInstallResult RemovedInstallerResult()
        {
            return UnityMcpAgentInstallResult.Fail(
                "Automatic agent config installation has been removed. Open puerts-unity-mcp/Packages/puerts-unity-mcp/setup-for-agent.md and let the agent configure its own MCP client files.");
        }

        private static string ResolvePackageFile(string projectRoot, params string[] relativeParts)
        {
            var packageRoot = ResolvePackageRoot();
            if (!string.IsNullOrEmpty(packageRoot))
            {
                var candidate = Combine(packageRoot, relativeParts);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            if (!string.IsNullOrEmpty(projectRoot))
            {
                var embedded = Combine(Path.Combine(projectRoot, "Packages", UnityMcpConstants.PackageName), relativeParts);
                if (File.Exists(embedded))
                {
                    return embedded;
                }

                var localPackage = Combine(Path.Combine(projectRoot, "puerts-unity-mcp", "Packages", UnityMcpConstants.PackageName), relativeParts);
                if (File.Exists(localPackage))
                {
                    return localPackage;
                }
            }

            return null;
        }

        private static string Combine(string root, string[] relativeParts)
        {
            var path = root;
            for (var i = 0; i < relativeParts.Length; i++)
            {
                path = Path.Combine(path, relativeParts[i]);
            }

            return path;
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
                return null;
            }

            return null;
        }

        private static string NormalizeDisplayHost(string bindAddress)
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
    }

    [Serializable]
    public sealed class UnityMcpAgentInstallResult
    {
        public bool succeeded;
        public string message;
        public string[] writtenFiles = new string[0];
        public string[] backupFiles = new string[0];
        public string[] skippedTargets = new string[0];

        public static UnityMcpAgentInstallResult Fail(string message)
        {
            return new UnityMcpAgentInstallResult
            {
                succeeded = false,
                message = message,
                writtenFiles = new string[0],
                backupFiles = new string[0],
                skippedTargets = new string[0]
            };
        }
    }
}
