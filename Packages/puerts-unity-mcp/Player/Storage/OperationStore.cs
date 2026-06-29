using System;
using System.Collections.Generic;
using System.IO;

namespace PuertsUnityMcp
{
    public sealed class OperationStore
    {
        private static readonly object CleanupSync = new object();
        private static DateTime lastCleanupUtc;

        public string Create(string action, string targetId, UnityMcpToolArguments request)
        {
            CleanupIfNeeded();
            var operationId = "op_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var root = UnityMcpPaths.OperationRoot(operationId);
            Directory.CreateDirectory(root);
            AtomicFile.WriteJson(Path.Combine(root, "request.json"), new OperationRequestRecord
            {
                operationId = operationId,
                action = action,
                targetId = targetId,
                createdAtUtc = DateTime.UtcNow.ToString("o"),
                request = request ?? new UnityMcpToolArguments()
            });
            Update(operationId, "queued", null);
            return operationId;
        }

        public void Update(string operationId, string state, string dataJson)
        {
            var root = UnityMcpPaths.OperationRoot(operationId);
            Directory.CreateDirectory(root);
            AtomicFile.WriteJson(Path.Combine(root, "state.json"), new OperationStateRecord
            {
                operationId = operationId,
                state = state,
                updatedAtUtc = DateTime.UtcNow.ToString("o"),
                dataJson = dataJson
            });
        }

        public void Complete(string operationId, bool success, string resultJson, string error = null)
        {
            var root = UnityMcpPaths.OperationRoot(operationId);
            Directory.CreateDirectory(root);
            AtomicFile.WriteJson(Path.Combine(root, "result.json"), new OperationResultRecord
            {
                operationId = operationId,
                success = success,
                completedAtUtc = DateTime.UtcNow.ToString("o"),
                resultJson = resultJson,
                error = error
            });
            Update(operationId, success ? "completed" : "failed", error);
        }

        public string Read(string operationId)
        {
            CleanupIfNeeded();
            var root = UnityMcpPaths.OperationRoot(operationId);
            AtomicFile.TryReadAllText(Path.Combine(root, "request.json"), out var requestJson);
            AtomicFile.TryReadAllText(Path.Combine(root, "state.json"), out var stateJson);
            AtomicFile.TryReadAllText(Path.Combine(root, "result.json"), out var resultJson);
            return UnityJson.ToJson(new OperationSnapshot
            {
                operationId = operationId,
                requestJson = requestJson,
                stateJson = stateJson,
                resultJson = resultJson
            });
        }

        public int CleanupNow()
        {
            return CleanupOperations(true);
        }

        private static void CleanupIfNeeded()
        {
            CleanupOperations(false);
        }

        private static int CleanupOperations(bool force)
        {
            var now = DateTime.UtcNow;
            lock (CleanupSync)
            {
                if (!force && now - lastCleanupUtc < TimeSpan.FromSeconds(UnityMcpConstants.OperationCleanupIntervalSeconds))
                {
                    return 0;
                }

                lastCleanupUtc = now;
                return CleanupOperationsLocked(now);
            }
        }

        private static int CleanupOperationsLocked(DateTime now)
        {
            var opsRoot = UnityMcpPaths.OperationsRoot();
            if (!Directory.Exists(opsRoot))
            {
                return 0;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(opsRoot, "op_*");
            }
            catch
            {
                return 0;
            }

            var deleted = 0;
            var remaining = new List<OperationDirectoryRecord>();
            foreach (var directory in directories)
            {
                if (!TryBuildDirectoryRecord(directory, out var record))
                {
                    continue;
                }

                if (ShouldDeleteByAge(record, now) && TryDeleteDirectory(record.path))
                {
                    deleted++;
                    continue;
                }

                remaining.Add(record);
            }

            if (remaining.Count <= UnityMcpConstants.OperationDirectoryLimit)
            {
                return deleted;
            }

            remaining.Sort(CompareDeletionPriority);
            var limitDeleted = 0;
            for (var i = 0; i < remaining.Count && remaining.Count - limitDeleted > UnityMcpConstants.OperationDirectoryLimit; i++)
            {
                var record = remaining[i];
                if (!record.IsShortLived && now - record.lastWriteUtc <= UnityMcpConstants.InProgressOperationRetention)
                {
                    continue;
                }

                if (TryDeleteDirectory(record.path))
                {
                    deleted++;
                    limitDeleted++;
                }
            }

            return deleted;
        }

        private static bool ShouldDeleteByAge(OperationDirectoryRecord record, DateTime now)
        {
            if (record.isTerminal || record.isRefreshState)
            {
                return now - record.lastWriteUtc > UnityMcpConstants.OperationResultRetention;
            }

            return now - record.lastWriteUtc > UnityMcpConstants.InProgressOperationRetention;
        }

        private static int CompareDeletionPriority(OperationDirectoryRecord left, OperationDirectoryRecord right)
        {
            if (left.IsShortLived != right.IsShortLived)
            {
                return left.IsShortLived ? -1 : 1;
            }

            return DateTime.Compare(left.lastWriteUtc, right.lastWriteUtc);
        }

        private static bool TryBuildDirectoryRecord(string directory, out OperationDirectoryRecord record)
        {
            record = null;
            try
            {
                var info = new DirectoryInfo(directory);
                if (!info.Exists || !info.Name.StartsWith("op_", StringComparison.Ordinal))
                {
                    return false;
                }

                var statePath = Path.Combine(directory, "state.json");
                var resultPath = Path.Combine(directory, "result.json");
                var requestPath = Path.Combine(directory, "request.json");
                var lastWriteUtc = Max(
                    info.LastWriteTimeUtc,
                    GetLastWriteUtc(requestPath),
                    GetLastWriteUtc(statePath),
                    GetLastWriteUtc(resultPath));
                var isTerminal = File.Exists(resultPath);
                var isRefreshState = false;
                if (AtomicFile.TryReadJson<OperationStateRecord>(statePath, out var state) && state != null)
                {
                    isTerminal = isTerminal
                        || string.Equals(state.state, "completed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(state.state, "failed", StringComparison.OrdinalIgnoreCase);
                    isRefreshState = !string.IsNullOrEmpty(state.state)
                        && state.state.StartsWith("refresh_", StringComparison.OrdinalIgnoreCase);
                }

                record = new OperationDirectoryRecord
                {
                    path = directory,
                    lastWriteUtc = lastWriteUtc,
                    isTerminal = isTerminal,
                    isRefreshState = isRefreshState
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static DateTime Max(params DateTime[] values)
        {
            var result = DateTime.MinValue;
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] > result)
                {
                    result = values[i];
                }
            }

            return result == DateTime.MinValue ? DateTime.UtcNow : result;
        }

        private static DateTime GetLastWriteUtc(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static bool TryDeleteDirectory(string path)
        {
            try
            {
                if (!IsSafeOperationDirectory(path))
                {
                    return false;
                }

                Directory.Delete(path, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSafeOperationDirectory(string path)
        {
            try
            {
                var operationsRoot = Path.GetFullPath(UnityMcpPaths.OperationsRoot()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(fullPath);
                return name.StartsWith("op_", StringComparison.Ordinal)
                    && fullPath.StartsWith(operationsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private sealed class OperationDirectoryRecord
        {
            public string path;
            public DateTime lastWriteUtc;
            public bool isTerminal;
            public bool isRefreshState;
            public bool IsShortLived => isTerminal || isRefreshState;
        }

        [Serializable]
        private sealed class OperationRequestRecord
        {
            public string operationId;
            public string action;
            public string targetId;
            public string createdAtUtc;
            public UnityMcpToolArguments request;
        }

        [Serializable]
        private sealed class OperationStateRecord
        {
            public string operationId;
            public string state;
            public string updatedAtUtc;
            public string dataJson;
        }

        [Serializable]
        private sealed class OperationResultRecord
        {
            public string operationId;
            public bool success;
            public string completedAtUtc;
            public string resultJson;
            public string error;
        }

        [Serializable]
        private sealed class OperationSnapshot
        {
            public string operationId;
            public string requestJson;
            public string stateJson;
            public string resultJson;
        }
    }
}
