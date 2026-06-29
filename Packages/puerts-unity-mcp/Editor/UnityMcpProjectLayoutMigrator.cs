using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PuertsUnityMcp.Editor
{
    public static class UnityMcpProjectLayoutMigrator
    {
        [MenuItem("PuerTS Unity MCP/Migrate Extension Layout", priority = 42)]
        public static void MigrateFromMenu()
        {
            var result = MigrateLegacyLayout();
            var message = "PuerTS Unity MCP extension migration finished. Moved "
                          + result.moved.Length + ", skipped " + result.skipped.Length
                          + ", removed empty folders " + result.removedEmptyFolders.Length + ".";
            Debug.Log("[UnityMCP] " + message);
        }

        public static UnityMcpLayoutMigrationResult MigrateLegacyLayout()
        {
            var moved = new List<string>();
            var skipped = new List<string>();
            var removed = new List<string>();
            var projectRoot = UnityMcpPaths.ProjectRoot;
            if (string.IsNullOrEmpty(projectRoot))
            {
                skipped.Add("Project root was not resolved.");
                return BuildResult(moved, skipped, removed);
            }

            MoveKnown(projectRoot, "puerts-unity-mcp-extension/Editor/config.json", UnityMcpPaths.ProjectConfigPath, moved, skipped, removed);
            MoveKnown(projectRoot, "puerts-unity-mcp-extension/Runtime/runtime-config.json", UnityMcpPaths.RuntimeConfigPath, moved, skipped, removed);
            MoveKnown(projectRoot, "Assets/PuertsUnityMcp/Editor/config.json", UnityMcpPaths.ProjectConfigPath, moved, skipped, removed);
            MoveKnown(projectRoot, "Assets/PuertsUnityMcp/Editor/JavaScriptWindows", Path.Combine(UnityMcpPaths.EditorExtensionRoot, "JavaScriptWindows"), moved, skipped, removed);

            MoveKnown(projectRoot, "Assets/PuertsUnityMcp/Editor/Resources/PuertsUnityMcp/editor-tools", UnityMcpPaths.EditorToolsRoot(), moved, skipped, removed);
            MoveKnown(projectRoot, "Assets/PuertsUnityMcp/Runtime/Resources/PuertsUnityMcp/runtime-config.json", UnityMcpPaths.RuntimeConfigPath, moved, skipped, removed);
            MoveKnown(projectRoot, "Assets/PuertsUnityMcp/Runtime/Resources/PuertsUnityMcp/runtime-tools", UnityMcpPaths.RuntimeToolsRoot(), moved, skipped, removed);
            MoveKnown(projectRoot, "Assets/PuertsUnityMcp/Runtime/Resources/PuertsUnityMcp/skills", UnityMcpPaths.SkillsRoot(), moved, skipped, removed);

            MoveKnown(projectRoot, "Assets/PuertsUnityMcp/Resources/PuertsUnityMcp/editor-tools", UnityMcpPaths.EditorToolsRoot(), moved, skipped, removed);
            MoveKnown(projectRoot, "Assets/PuertsUnityMcp/Resources/PuertsUnityMcp/runtime-config.json", UnityMcpPaths.RuntimeConfigPath, moved, skipped, removed);
            MoveKnown(projectRoot, "Assets/PuertsUnityMcp/Resources/PuertsUnityMcp/runtime-tools", UnityMcpPaths.RuntimeToolsRoot(), moved, skipped, removed);
            MoveKnown(projectRoot, "Assets/PuertsUnityMcp/Resources/PuertsUnityMcp/skills", UnityMcpPaths.SkillsRoot(), moved, skipped, removed);

            MoveKnown(projectRoot, "Assets/Resources/PuertsUnityMcp/editor-tools", UnityMcpPaths.EditorToolsRoot(), moved, skipped, removed);
            MoveKnown(projectRoot, "Assets/Resources/PuertsUnityMcp/runtime-config.json", UnityMcpPaths.RuntimeConfigPath, moved, skipped, removed);
            MoveKnown(projectRoot, "Assets/Resources/PuertsUnityMcp/runtime-tools", UnityMcpPaths.RuntimeToolsRoot(), moved, skipped, removed);
            MoveKnown(projectRoot, "Assets/Resources/PuertsUnityMcp/skills", UnityMcpPaths.SkillsRoot(), moved, skipped, removed);

            RemoveEmptyLegacyDirectory(projectRoot, "Assets/PuertsUnityMcp/Editor/Resources/PuertsUnityMcp", removed);
            RemoveEmptyLegacyDirectory(projectRoot, "Assets/PuertsUnityMcp/Editor/Resources", removed);
            RemoveEmptyLegacyDirectory(projectRoot, "Assets/PuertsUnityMcp/Runtime/Resources/PuertsUnityMcp", removed);
            RemoveEmptyLegacyDirectory(projectRoot, "Assets/PuertsUnityMcp/Runtime/Resources", removed);
            RemoveEmptyLegacyDirectory(projectRoot, "Assets/PuertsUnityMcp/Resources/PuertsUnityMcp", removed);
            RemoveEmptyLegacyDirectory(projectRoot, "Assets/PuertsUnityMcp/Resources", removed);
            RemoveEmptyLegacyDirectory(projectRoot, "Assets/PuertsUnityMcp/Editor", removed);
            RemoveEmptyLegacyDirectory(projectRoot, "Assets/PuertsUnityMcp/Runtime", removed);
            RemoveEmptyLegacyDirectory(projectRoot, "Assets/PuertsUnityMcp", removed);
            RemoveEmptyLegacyDirectory(projectRoot, "Assets/Resources/PuertsUnityMcp", removed);
            RemoveEmptyLegacyDirectory(projectRoot, "puerts-unity-mcp-extension/Editor", removed);
            RemoveEmptyLegacyDirectory(projectRoot, "puerts-unity-mcp-extension/Runtime", removed);

            AssetDatabase.Refresh();
            return BuildResult(moved, skipped, removed);
        }

        private static UnityMcpLayoutMigrationResult BuildResult(List<string> moved, List<string> skipped, List<string> removed)
        {
            return new UnityMcpLayoutMigrationResult
            {
                moved = moved.ToArray(),
                skipped = skipped.ToArray(),
                removedEmptyFolders = removed.ToArray()
            };
        }

        private static void MoveKnown(string projectRoot, string sourceRelativePath, string targetFullPath, List<string> moved, List<string> skipped, List<string> removed)
        {
            var sourceFullPath = Path.GetFullPath(Path.Combine(projectRoot, sourceRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(sourceFullPath) && !Directory.Exists(sourceFullPath))
            {
                return;
            }

            MoveOrMerge(sourceFullPath, targetFullPath, moved, skipped, removed);
        }

        private static void MoveOrMerge(string sourceFullPath, string targetFullPath, List<string> moved, List<string> skipped, List<string> removed)
        {
            if (string.IsNullOrEmpty(sourceFullPath) || string.IsNullOrEmpty(targetFullPath))
            {
                skipped.Add((sourceFullPath ?? string.Empty) + " -> " + (targetFullPath ?? string.Empty) + " (invalid path)");
                return;
            }

            if (File.Exists(sourceFullPath))
            {
                MoveFile(sourceFullPath, targetFullPath, moved, skipped);
                TryDeleteMeta(sourceFullPath);
                return;
            }

            if (!Directory.Exists(sourceFullPath))
            {
                return;
            }

            Directory.CreateDirectory(targetFullPath);
            foreach (var child in Directory.GetFileSystemEntries(sourceFullPath))
            {
                if (IsMetaFile(child))
                {
                    continue;
                }

                MoveOrMerge(child, Path.Combine(targetFullPath, Path.GetFileName(child)), moved, skipped, removed);
            }

            RemoveDirectoryIfEmpty(sourceFullPath, removed);
        }

        private static void MoveFile(string sourceFullPath, string targetFullPath, List<string> moved, List<string> skipped)
        {
            if (File.Exists(targetFullPath))
            {
                skipped.Add(sourceFullPath + " -> " + targetFullPath + " (target exists)");
                return;
            }

            var targetDirectory = Path.GetDirectoryName(targetFullPath);
            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            File.Move(sourceFullPath, targetFullPath);
            moved.Add(sourceFullPath + " -> " + targetFullPath);
        }

        private static void RemoveEmptyLegacyDirectory(string projectRoot, string sourceRelativePath, List<string> removed)
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, sourceRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            RemoveDirectoryIfEmpty(fullPath, removed);
        }

        private static void RemoveDirectoryIfEmpty(string fullPath, List<string> removed)
        {
            if (string.IsNullOrEmpty(fullPath) || !Directory.Exists(fullPath))
            {
                return;
            }

            foreach (var entry in Directory.GetFileSystemEntries(fullPath))
            {
                if (!IsMetaFile(entry))
                {
                    return;
                }
            }

            foreach (var entry in Directory.GetFileSystemEntries(fullPath))
            {
                TryDeleteFile(entry);
            }

            Directory.Delete(fullPath);
            TryDeleteMeta(fullPath);
            removed.Add(fullPath);
        }

        private static bool IsMetaFile(string path)
        {
            return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDeleteMeta(string sourcePath)
        {
            TryDeleteFile(sourcePath + ".meta");
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    [Serializable]
    public sealed class UnityMcpLayoutMigrationResult
    {
        public string[] moved = new string[0];
        public string[] skipped = new string[0];
        public string[] removedEmptyFolders = new string[0];
    }
}
