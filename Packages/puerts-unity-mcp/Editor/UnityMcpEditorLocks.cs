using System;
using System.IO;

namespace PuertsUnityMcp.Editor
{
    internal static class UnityMcpEditorLocks
    {
        public static void CreateDomainReloadLock()
        {
            CreateLock(UnityMcpConstants.DomainReloadLockName, "domain_reload");
        }

        public static void DeleteDomainReloadLock()
        {
            DeleteLock(UnityMcpConstants.DomainReloadLockName);
        }

        public static void CreateCompilingLock()
        {
            CreateLock(UnityMcpConstants.CompilingLockName, "compiling");
        }

        public static void DeleteCompilingLock()
        {
            DeleteLock(UnityMcpConstants.CompilingLockName);
        }

        public static string CreateServerStartingLock()
        {
            var token = Guid.NewGuid().ToString("N");
            var path = UnityMcpPaths.TempLockPath(UnityMcpConstants.ServerStartingLockName);
            AtomicFile.WriteJson(path, new LockRecord
            {
                token = token,
                state = "server_starting",
                createdAtUtc = DateTime.UtcNow.ToString("o")
            });
            return token;
        }

        public static void DeleteServerStartingLock()
        {
            DeleteLock(UnityMcpConstants.ServerStartingLockName);
        }

        private static void CreateLock(string name, string state)
        {
            var path = UnityMcpPaths.TempLockPath(name);
            AtomicFile.WriteJson(path, new LockRecord
            {
                state = state,
                createdAtUtc = DateTime.UtcNow.ToString("o")
            });
        }

        private static void DeleteLock(string name)
        {
            var path = UnityMcpPaths.TempLockPath(name);
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

        [Serializable]
        private sealed class LockRecord
        {
            public string token;
            public string state;
            public string createdAtUtc;
        }
    }
}
