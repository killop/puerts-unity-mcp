using System;
using System.IO;
using UnityEngine;

namespace PuertsUnityMcp
{
    public static class UnityMcpPaths
    {
        private const string StateRootEnvironment = "PUERTS_UNITY_MCP_DIR";
        private const string ExtensionRootEnvironment = "PUERTS_UNITY_MCP_EXTENSION_DIR";
        private const string AllowExternalStateRootEnvironment = "PUERTS_UNITY_MCP_ALLOW_EXTERNAL_STATE";

        public static string ProjectRoot
        {
            get
            {
                var dataPath = Application.dataPath;
                if (!string.IsNullOrEmpty(dataPath))
                {
                    var normalized = dataPath.Replace('\\', '/');
                    if (normalized.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase))
                    {
                        return Path.GetFullPath(Path.Combine(dataPath, ".."));
                    }
                }

                return null;
            }
        }

        public static string StateRoot
        {
            get
            {
                var projectRoot = ProjectRoot;
                if (!string.IsNullOrEmpty(projectRoot))
                {
                    var projectStateRoot = Path.Combine(projectRoot, UnityMcpConstants.StateDirectoryName);
                    var fromEnv = Environment.GetEnvironmentVariable(StateRootEnvironment);
                    if (!string.IsNullOrEmpty(fromEnv))
                    {
                        var envStateRoot = Path.GetFullPath(fromEnv);
                        if (IsUnderRoot(envStateRoot, projectRoot) || IsExternalStateRootAllowed())
                        {
                            return envStateRoot;
                        }
                    }

                    return projectStateRoot;
                }

                var fallbackFromEnv = Environment.GetEnvironmentVariable(StateRootEnvironment);
                if (!string.IsNullOrEmpty(fallbackFromEnv))
                {
                    return Path.GetFullPath(fallbackFromEnv);
                }

                return Path.Combine(Application.persistentDataPath, UnityMcpConstants.StateDirectoryName);
            }
        }

        public static string TempRoot
        {
            get
            {
                return Path.Combine(StateRoot, UnityMcpConstants.TempDirectoryName);
            }
        }

        public static string InstancesPath => Path.Combine(StateRoot, UnityMcpConstants.InstancesFileName);

        public static string ProjectExtensionRoot
        {
            get
            {
                var fromEnv = Environment.GetEnvironmentVariable(ExtensionRootEnvironment);
                if (!string.IsNullOrEmpty(fromEnv))
                {
                    return Path.GetFullPath(fromEnv);
                }

                var projectRoot = ProjectRoot;
                if (!string.IsNullOrEmpty(projectRoot))
                {
                    return Path.Combine(projectRoot, UnityMcpConstants.ExtensionDirectoryName);
                }

                return Path.Combine(Application.persistentDataPath, UnityMcpConstants.ExtensionDirectoryName);
            }
        }

        public static string ProjectAssetsRoot
        {
            get
            {
                return ProjectExtensionRoot;
            }
        }

        public static string LegacyProjectAssetsRoot
        {
            get
            {
                var projectRoot = ProjectRoot;
                if (!string.IsNullOrEmpty(projectRoot))
                {
                    return Path.Combine(
                        projectRoot,
                        UnityMcpConstants.AssetsDirectoryName,
                        UnityMcpConstants.ProjectAssetsDirectoryName);
                }

                return Path.Combine(Application.persistentDataPath, UnityMcpConstants.ProjectAssetsDirectoryName);
            }
        }

        public static string EditorExtensionRoot
        {
            get
            {
                return Path.Combine(ProjectExtensionRoot, UnityMcpConstants.EditorDirectoryName);
            }
        }

        public static string RuntimeExtensionRoot
        {
            get
            {
                return Path.Combine(ProjectExtensionRoot, UnityMcpConstants.RuntimeDirectoryName);
            }
        }

        public static string EditorAssetsRoot
        {
            get
            {
                return EditorExtensionRoot;
            }
        }

        public static string RuntimeAssetsRoot
        {
            get
            {
                return RuntimeExtensionRoot;
            }
        }

        public static string RuntimeResourcesRoot
        {
            get
            {
                return RuntimeExtensionRoot;
            }
        }

        public static string EditorResourcesRoot
        {
            get
            {
                return EditorExtensionRoot;
            }
        }

        public static string ProjectConfigPath
        {
            get
            {
                return Path.Combine(ProjectExtensionRoot, UnityMcpConstants.ConfigFileName);
            }
        }

        public static string LegacyProjectExtensionConfigPath
        {
            get
            {
                return Path.Combine(EditorExtensionRoot, UnityMcpConstants.LegacyConfigFileName);
            }
        }

        public static string LegacyProjectConfigPath
        {
            get
            {
                var projectRoot = ProjectRoot;
                if (!string.IsNullOrEmpty(projectRoot))
                {
                    return Path.Combine(projectRoot, UnityMcpConstants.StateDirectoryName, UnityMcpConstants.LegacyConfigFileName);
                }

                return Path.Combine(StateRoot, UnityMcpConstants.LegacyConfigFileName);
            }
        }

        public static string RuntimeConfigPath
        {
            get
            {
                return Path.Combine(ProjectExtensionRoot, UnityMcpConstants.RuntimeConfigFileName);
            }
        }

        public static string LegacyRuntimeConfigPath
        {
            get
            {
                return Path.Combine(RuntimeExtensionRoot, UnityMcpConstants.LegacyRuntimeConfigFileName);
            }
        }

        public static string[] ProjectConfigReadPaths()
        {
            return new[]
            {
                ProjectConfigPath,
                LegacyProjectExtensionConfigPath,
                LegacyProjectConfigPath
            };
        }

        public static string[] RuntimeConfigReadPaths()
        {
            return new[]
            {
                RuntimeConfigPath,
                LegacyRuntimeConfigPath
            };
        }

        public static string EditorToolsRoot()
        {
            return Path.Combine(EditorExtensionRoot, UnityMcpConstants.EditorToolsDirectoryName);
        }

        public static string RuntimeToolsRoot()
        {
            return Path.Combine(RuntimeExtensionRoot, UnityMcpConstants.RuntimeToolsDirectoryName);
        }

        public static string SkillsRoot()
        {
            return Path.Combine(ProjectExtensionRoot, UnityMcpConstants.SkillsDirectoryName);
        }

        public static string EditorRoot(string editorId)
        {
            return Path.Combine(StateRoot, UnityMcpConstants.EditorsDirectoryName, SanitizeId(editorId));
        }

        public static string PlayerRoot(string playerId)
        {
            return Path.Combine(StateRoot, UnityMcpConstants.PlayersDirectoryName, SanitizeId(playerId));
        }

        public static string CommandsRoot(string endpointKind, string endpointId)
        {
            return Path.Combine(EndpointRoot(endpointKind, endpointId), UnityMcpConstants.CommandsDirectoryName);
        }

        public static string ResultsRoot(string endpointKind, string endpointId)
        {
            return Path.Combine(EndpointRoot(endpointKind, endpointId), UnityMcpConstants.ResultsDirectoryName);
        }

        public static string ArtifactsRoot(string endpointKind, string endpointId)
        {
            return Path.Combine(EndpointRoot(endpointKind, endpointId), UnityMcpConstants.ArtifactsDirectoryName);
        }

        public static string EndpointRoot(string endpointKind, string endpointId)
        {
            return string.Equals(endpointKind, "editor", StringComparison.OrdinalIgnoreCase)
                ? EditorRoot(endpointId)
                : PlayerRoot(endpointId);
        }

        public static string OperationRoot(string operationId)
        {
            return Path.Combine(OperationsRoot(), SanitizeId(operationId));
        }

        public static string OperationsRoot()
        {
            return Path.Combine(StateRoot, UnityMcpConstants.OpsDirectoryName);
        }

        public static string CompileResultsRoot()
        {
            return Path.Combine(TempRoot, UnityMcpConstants.CompileResultsDirectoryName);
        }

        public static string TempLockPath(string lockFileName)
        {
            return Path.Combine(TempRoot, lockFileName);
        }

        public static string AotMissesPath()
        {
            return Path.Combine(StateRoot, UnityMcpConstants.AotMissesFileName);
        }

        public static string ToolsRoot()
        {
            return Path.Combine(StateRoot, UnityMcpConstants.ToolsDirectoryName);
        }

        public static string LogsRoot()
        {
            return Path.Combine(StateRoot, UnityMcpConstants.LogsDirectoryName);
        }

        public static string PerformanceReportsRoot()
        {
            return Path.Combine(StateRoot, UnityMcpConstants.PerformanceReportsDirectoryName);
        }

        public static string SanitizeId(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "default";
            }

            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                {
                    continue;
                }

                chars[i] = '_';
            }

            return new string(chars);
        }

        private static bool IsExternalStateRootAllowed()
        {
            var value = Environment.GetEnvironmentVariable(AllowExternalStateRootEnvironment);
            return string.Equals(value, "1", StringComparison.Ordinal)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnderRoot(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
            {
                return false;
            }

            try
            {
                var normalizedRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var normalizedPath = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
