using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PuertsUnityMcp.Editor
{
    public sealed class UnityMcpMobileConfigBuildHook : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        private const string AndroidManifestText = "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">\n  <uses-permission android:name=\"android.permission.INTERNET\" />\n  <uses-permission android:name=\"android.permission.ACCESS_NETWORK_STATE\" />\n  <uses-permission android:name=\"android.permission.ACCESS_WIFI_STATE\" />\n  <uses-permission android:name=\"android.permission.CHANGE_WIFI_MULTICAST_STATE\" />\n</manifest>\n";
        private const string AndroidProjectPropertiesText = "target=android-35\n";
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly Dictionary<string, BackupState> Backups = new Dictionary<string, BackupState>(StringComparer.OrdinalIgnoreCase);

        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report == null)
            {
                return;
            }

            CopyMobileConfigToStreamingAssets();
            if (report.summary.platform == BuildTarget.Android)
            {
                WriteAndroidPermissionLibrary();
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            RestoreBuildFiles();
        }

        public static string DestinationPath => UnityMcpRuntimeConfigStore.StreamingAssetsConfigPath;

        private static void CopyMobileConfigToStreamingAssets()
        {
            var sourcePath = UnityMcpPaths.RuntimeConfigPath;
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                Debug.LogWarning("[UnityMCP] Mobile MCP config was not found: " + sourcePath);
                return;
            }

            var destinationPath = DestinationPath;
            RememberBackup(destinationPath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourcePath, destinationPath, true);
            ImportAsset(destinationPath);
            Debug.Log("[UnityMCP] Copied mobile MCP config to StreamingAssets for build: " + destinationPath);
        }

        private static void WriteAndroidPermissionLibrary()
        {
            var root = AndroidLibraryRoot;
            if (string.IsNullOrEmpty(root))
            {
                Debug.LogWarning("[UnityMCP] Project root was not resolved; Android MCP permission library was not generated.");
                return;
            }

            var manifestPath = Path.Combine(root, "AndroidManifest.xml");
            var propertiesPath = Path.Combine(root, "project.properties");
            RememberBackup(manifestPath);
            RememberBackup(propertiesPath);
            Directory.CreateDirectory(root);
            File.WriteAllText(manifestPath, AndroidManifestText, Utf8NoBom);
            File.WriteAllText(propertiesPath, AndroidProjectPropertiesText, Utf8NoBom);
            ImportAsset(manifestPath);
            ImportAsset(propertiesPath);
            Debug.Log("[UnityMCP] Wrote temporary Android MCP permission library for build: " + root);
        }

        private static string AndroidLibraryRoot
        {
            get
            {
                var projectRoot = UnityMcpPaths.ProjectRoot;
                return string.IsNullOrEmpty(projectRoot)
                    ? null
                    : Path.Combine(projectRoot, "Assets", "Plugins", "Android", "puerts-unity-mcp.androidlib");
            }
        }

        private static void RestoreBuildFiles()
        {
            foreach (var pair in Backups)
            {
                var path = pair.Key;
                var backup = pair.Value;
                try
                {
                    if (backup.existed)
                    {
                        File.WriteAllBytes(path, backup.bytes ?? new byte[0]);
                    }
                    else if (File.Exists(path))
                    {
                        File.Delete(path);
                        TryDeleteMeta(path);
                    }

                    ImportAsset(path);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[UnityMCP] Failed to restore StreamingAssets mobile MCP config: " + ex.Message);
                }
            }

            Backups.Clear();
            RemoveEmptyGeneratedAndroidDirectories();
        }

        private static void RememberBackup(string path)
        {
            if (string.IsNullOrEmpty(path) || Backups.ContainsKey(path))
            {
                return;
            }

            Backups[path] = new BackupState
            {
                existed = File.Exists(path),
                bytes = File.Exists(path) ? File.ReadAllBytes(path) : null
            };
        }

        private static void ImportAsset(string fullPath)
        {
            var relative = ToProjectRelativePath(fullPath);
            if (!string.IsNullOrEmpty(relative))
            {
                AssetDatabase.ImportAsset(relative);
            }
        }

        private static string ToProjectRelativePath(string fullPath)
        {
            var projectRoot = UnityMcpPaths.ProjectRoot;
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(fullPath))
            {
                return null;
            }

            var normalizedRoot = Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var normalizedPath = Path.GetFullPath(fullPath);
            if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return normalizedPath.Substring(normalizedRoot.Length).Replace('\\', '/');
        }

        private static void TryDeleteMeta(string path)
        {
            try
            {
                var metaPath = path + ".meta";
                if (File.Exists(metaPath))
                {
                    File.Delete(metaPath);
                }
            }
            catch
            {
            }
        }

        private static void RemoveEmptyGeneratedAndroidDirectories()
        {
            var root = AndroidLibraryRoot;
            RemoveDirectoryIfEmpty(root);
            if (!string.IsNullOrEmpty(root))
            {
                RemoveDirectoryIfEmpty(Path.GetDirectoryName(root));
            }
        }

        private static void RemoveDirectoryIfEmpty(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                {
                    return;
                }

                if (Directory.GetFileSystemEntries(path).Length != 0)
                {
                    return;
                }

                Directory.Delete(path);
                TryDeleteMeta(path);
            }
            catch
            {
            }
        }

        private sealed class BackupState
        {
            public bool existed;
            public byte[] bytes;
        }
    }
}
