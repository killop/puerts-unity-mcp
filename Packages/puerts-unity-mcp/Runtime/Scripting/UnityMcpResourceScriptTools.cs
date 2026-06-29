using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PuertsUnityMcp
{
    public static class UnityMcpResourceScriptTools
    {
        public static UnityMcpScriptToolManifest[] LoadManifests(string directoryRoot)
        {
            if (string.IsNullOrEmpty(directoryRoot) || !Directory.Exists(directoryRoot))
            {
                return new UnityMcpScriptToolManifest[0];
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directoryRoot, "*.*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityMCP] Failed to scan script tool directory '" + directoryRoot + "': " + ex.Message);
                return new UnityMcpScriptToolManifest[0];
            }

            var result = new List<UnityMcpScriptToolManifest>();
            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                if (!IsToolManifestFile(file))
                {
                    continue;
                }

                try
                {
                    var manifest = UnityJson.FromJson<UnityMcpScriptToolManifest>(File.ReadAllText(file));
                    if (manifest == null || manifest.disabled || string.IsNullOrEmpty(manifest.name))
                    {
                        continue;
                    }

                    manifest.Normalize(directoryRoot, file);
                    result.Add(manifest);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[UnityMCP] Failed to parse script tool manifest '" + file + "': " + ex.Message);
                }
            }

            result.Sort((left, right) => string.CompareOrdinal(left.name, right.name));
            return result.ToArray();
        }

        private static bool IsToolManifestFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            var fileName = Path.GetFileName(path);
            if (fileName.EndsWith(".tool", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".tool.json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                var text = File.ReadAllText(path);
                return !string.IsNullOrEmpty(text)
                    && text.TrimStart().StartsWith("{", StringComparison.Ordinal)
                    && text.IndexOf("\"modulePath\"", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }
    }

    [Serializable]
    public sealed class UnityMcpScriptToolManifest
    {
        public string name;
        public string description;
        public string inputSchemaJson;
        public string modulePath;
        public string functionName;
        public bool disabled;

        public void Normalize(string directoryRoot, string manifestPath)
        {
            functionName = string.IsNullOrEmpty(functionName) ? "execute" : functionName;
            description = string.IsNullOrEmpty(description) ? "Project JavaScript MCP tool loaded from puerts-unity-mcp-extension." : description;
            inputSchemaJson = string.IsNullOrEmpty(inputSchemaJson) ? JsonSchemas.Object() : inputSchemaJson;

            var manifestDirectory = string.IsNullOrEmpty(manifestPath)
                ? directoryRoot
                : Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrEmpty(modulePath))
            {
                var baseName = Path.GetFileName(manifestPath) ?? string.Empty;
                if (baseName.EndsWith(".tool.json", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = baseName.Substring(0, baseName.Length - ".tool.json".Length);
                }

                if (baseName.EndsWith(".tool", StringComparison.OrdinalIgnoreCase))
                {
                    baseName = baseName.Substring(0, baseName.Length - ".tool".Length);
                }

                modulePath = Path.Combine(manifestDirectory ?? directoryRoot, baseName + ".mjs");
            }
            else if (!Path.IsPathRooted(modulePath))
            {
                modulePath = Path.Combine(manifestDirectory ?? directoryRoot, modulePath);
            }

            modulePath = Path.GetFullPath(modulePath);
        }
    }

    [Serializable]
    public sealed class UnityMcpScriptToolListResult
    {
        public string action;
        public string targetId;
        public string resourceRoot;
        public string directoryRoot;
        public int count;
        public UnityMcpScriptToolManifest[] tools;
    }

    [Serializable]
    public sealed class UnityMcpScriptToolContext
    {
        public string endpointKind;
        public string endpointId;
        public string endpointName;
        public string toolName;
        public string modulePath;
        public string projectRoot;
        public string stateRoot;
        public string extensionRoot;
    }
}
