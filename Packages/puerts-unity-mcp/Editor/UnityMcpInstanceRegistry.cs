using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PuertsUnityMcp.Editor
{
    internal static class UnityMcpInstanceRegistry
    {
        private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(10);

        public static void Update(UnityMcpHeartbeat heartbeat)
        {
            var path = UnityMcpPaths.InstancesPath;
            if (!AtomicFile.TryReadJson(path, out InstanceRegistryDocument document) || document == null)
            {
                document = new InstanceRegistryDocument();
            }

            var now = DateTime.UtcNow;
            var entries = document.instances ?? new UnityMcpHeartbeat[0];
            document.instances = entries
                .Where(entry => !IsSameEndpoint(entry, heartbeat))
                .Where(entry => !IsStale(entry, now))
                .Concat(new[] { heartbeat })
                .ToArray();

            AtomicFile.WriteJson(path, document);
        }

        public static void Remove(string endpointId)
        {
            var path = UnityMcpPaths.InstancesPath;
            if (!AtomicFile.TryReadJson(path, out InstanceRegistryDocument document) || document == null || document.instances == null)
            {
                return;
            }

            document.instances = document.instances
                .Where(entry => !string.Equals(entry.endpointId, endpointId, StringComparison.Ordinal))
                .ToArray();
            AtomicFile.WriteJson(path, document);
        }

        private static bool IsSameEndpoint(UnityMcpHeartbeat entry, UnityMcpHeartbeat heartbeat)
        {
            return string.Equals(entry.endpointId, heartbeat.endpointId, StringComparison.Ordinal)
                || string.Equals(entry.projectRoot, heartbeat.projectRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStale(UnityMcpHeartbeat entry, DateTime now)
        {
            if (entry == null || string.IsNullOrEmpty(entry.lastUpdatedUtc))
            {
                return true;
            }

            return !DateTime.TryParse(entry.lastUpdatedUtc, out var timestamp) || now - timestamp.ToUniversalTime() > StaleThreshold;
        }

        public static int GetProcessId()
        {
            try
            {
                return Process.GetCurrentProcess().Id;
            }
            catch
            {
                return 0;
            }
        }

        public static string GetProjectName()
        {
            var projectRoot = UnityMcpPaths.ProjectRoot;
            return string.IsNullOrEmpty(projectRoot) ? Application.productName : new DirectoryInfo(projectRoot).Name;
        }

        public static string GetEditorWindowTitle()
        {
            return Application.productName + " - " + GetProjectName();
        }

        [Serializable]
        private sealed class InstanceRegistryDocument
        {
            public UnityMcpHeartbeat[] instances = new UnityMcpHeartbeat[0];
        }
    }
}
