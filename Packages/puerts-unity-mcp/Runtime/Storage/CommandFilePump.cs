using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PuertsUnityMcp
{
    public sealed class CommandFilePump
    {
        private readonly IUnityMcpEndpoint endpoint;
        private readonly OperationStore operations = new OperationStore();
        private readonly string commandsPath;
        private readonly string resultsPath;
        private readonly object runningSync = new object();
        private readonly HashSet<string> runningCommandIds = new HashSet<string>(StringComparer.Ordinal);
        private DateTime lastCleanupUtc;

        public CommandFilePump(IUnityMcpEndpoint endpoint)
        {
            this.endpoint = endpoint;
            commandsPath = UnityMcpPaths.CommandsRoot(endpoint.EndpointKind, endpoint.EndpointId);
            resultsPath = UnityMcpPaths.ResultsRoot(endpoint.EndpointKind, endpoint.EndpointId);
            AtomicFile.EnsurePrivateDirectory(commandsPath);
            AtomicFile.EnsurePrivateDirectory(resultsPath);
        }

        public void Tick(int maxCommands = 4)
        {
            CleanupIfNeeded();

            if (ShouldDeferCommands())
            {
                return;
            }

            if (!Directory.Exists(commandsPath))
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(commandsPath, "*.json");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityMCP] Command scan failed: " + ex.Message);
                return;
            }

            Array.Sort(files, StringComparer.Ordinal);
            var processed = 0;
            foreach (var file in files)
            {
                if (processed >= maxCommands)
                {
                    break;
                }

                ProcessFile(file);
                processed++;
            }
        }

        private void ProcessFile(string file)
        {
            UnityMcpCommand command;
            string commandId = null;
            try
            {
                if (IsStale(file))
                {
                    File.Delete(file);
                    return;
                }

                if (!AtomicFile.TryReadJson(file, out command) || command == null)
                {
                    if (IsFresh(file))
                    {
                        return;
                    }

                    MoveToError(file);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityMCP] Failed to load command file: " + ex.Message);
                MoveToError(file);
                return;
            }

            var startedAt = DateTime.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            commandId = string.IsNullOrEmpty(command.id) ? Path.GetFileNameWithoutExtension(file) : command.id;
            if (string.IsNullOrEmpty(commandId))
            {
                commandId = Guid.NewGuid().ToString("N");
            }

            if (File.Exists(ResultPath(commandId)))
            {
                TryDelete(file);
                return;
            }

            lock (runningSync)
            {
                if (runningCommandIds.Contains(commandId))
                {
                    return;
                }

                runningCommandIds.Add(commandId);
            }

            var operationId = operations.Create(command.action, command.targetId, command.@params);
            operations.Update(operationId, "running", commandId);
            _ = ExecuteCommandAsync(file, command, commandId, operationId, startedAt, stopwatch);
        }

        private async System.Threading.Tasks.Task ExecuteCommandAsync(
            string sourceFile,
            UnityMcpCommand command,
            string commandId,
            string operationId,
            DateTime startedAt,
            Stopwatch stopwatch)
        {
            var resultWritten = false;
            try
            {
                var resultJson = await endpoint.CallToolAsync(command.action, command.@params ?? new UnityMcpToolArguments());
                stopwatch.Stop();
                operations.Complete(operationId, true, resultJson);
                WriteResult(UnityMcpCommandResult.Ok(commandId, resultJson, startedAt, stopwatch.ElapsedMilliseconds));
                resultWritten = true;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                operations.Complete(operationId, false, null, ex.Message);
                try
                {
                    WriteResult(UnityMcpCommandResult.Fail(commandId, ex.GetType().Name + ": " + ex.Message, startedAt, stopwatch.ElapsedMilliseconds));
                    resultWritten = true;
                }
                catch (Exception writeEx)
                {
                    Debug.LogWarning("[UnityMCP] Failed to write command result: " + writeEx.Message);
                }
            }
            finally
            {
                if (resultWritten || File.Exists(ResultPath(commandId)))
                {
                    TryDelete(sourceFile);
                }

                lock (runningSync)
                {
                    runningCommandIds.Remove(commandId);
                }
            }
        }

        private void WriteResult(UnityMcpCommandResult result)
        {
            AtomicFile.WriteJson(ResultPath(result.id), result);
        }

        private string ResultPath(string commandId)
        {
            return Path.Combine(resultsPath, UnityMcpPaths.SanitizeId(commandId) + ".json");
        }

        private static bool IsStale(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return DateTime.UtcNow - info.CreationTimeUtc > UnityMcpConstants.CommandResultRetention;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFresh(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromSeconds(2);
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldDeferCommands()
        {
#if UNITY_EDITOR
            return UnityEditor.EditorApplication.isCompiling
                || File.Exists(UnityMcpPaths.TempLockPath(UnityMcpConstants.DomainReloadLockName))
                || File.Exists(UnityMcpPaths.TempLockPath(UnityMcpConstants.CompilingLockName))
                || File.Exists(UnityMcpPaths.TempLockPath(UnityMcpConstants.ServerStartingLockName));
#else
            return false;
#endif
        }

        private static void TryDelete(string path)
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

        private static void MoveToError(string path)
        {
            try
            {
                var errorPath = path + ".error";
                if (File.Exists(errorPath))
                {
                    File.Delete(errorPath);
                }

                File.Move(path, errorPath);
            }
            catch
            {
                try { File.Delete(path); } catch { }
            }
        }

        private void CleanupIfNeeded()
        {
            if ((DateTime.UtcNow - lastCleanupUtc).TotalSeconds < 30)
            {
                return;
            }

            lastCleanupUtc = DateTime.UtcNow;
            CleanupDirectory(resultsPath, "*.json");
            CleanupDirectory(commandsPath, "*.error");
        }

        private static void CleanupDirectory(string path, string pattern)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(path, pattern))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (DateTime.UtcNow - info.CreationTimeUtc > UnityMcpConstants.CommandResultRetention)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        }
    }
}
