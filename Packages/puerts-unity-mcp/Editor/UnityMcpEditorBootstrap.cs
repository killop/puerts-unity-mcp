using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace PuertsUnityMcp.Editor
{
    [InitializeOnLoad]
    internal static class UnityMcpEditorBootstrap
    {
        private static UnityMcpEditorEndpoint endpoint;
        private static bool startupScheduled;
        private static readonly List<CompileMessageRecord> compilerMessages = new List<CompileMessageRecord>();

        public static UnityMcpEditorEndpoint Endpoint => endpoint;
        public static bool IsRunning => endpoint != null && endpoint.IsRunning;

        static UnityMcpEditorBootstrap()
        {
            if (IsBackgroundUnityProcess())
            {
                return;
            }

            UnityMcpMainThread.Initialize();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += OnEditorQuitting;
            EditorApplication.update += OnEditorUpdate;
            ScheduleStartup();
        }

        private static void ScheduleStartup()
        {
            if (startupScheduled)
            {
                return;
            }

            startupScheduled = true;
            EditorApplication.delayCall += () =>
            {
                startupScheduled = false;
                EnsureStarted();
            };
        }

        public static void StartEndpoint()
        {
            EnsureEndpointCreated();
            endpoint.Start();
            UnityMcpEditorSettings.WasRunning = true;
        }

        public static void StopEndpoint()
        {
            if (endpoint != null)
            {
                endpoint.Stop();
            }

            UnityMcpEditorSettings.WasRunning = false;
        }

        private static void EnsureStarted()
        {
            if (IsBackgroundUnityProcess())
            {
                return;
            }

            var recoveringFromDomainReload = File.Exists(UnityMcpPaths.TempLockPath(UnityMcpConstants.DomainReloadLockName));
            var config = UnityMcpProjectConfigStore.LoadOrCreate();
            UnityMcpEditorLocks.CreateServerStartingLock();
            try
            {
                EnsureEndpointCreated();

                if (config.editorAutoStart || (recoveringFromDomainReload && UnityMcpEditorSettings.WasRunning))
                {
                    endpoint.Start();
                }

                if (recoveringFromDomainReload)
                {
                    UnityMcpDomainReloadJournal.Append("after", endpoint?.EndpointId, endpoint?.Port ?? UnityMcpEditorSettings.Port, endpoint != null && endpoint.IsRunning);
                }
            }
            finally
            {
                UnityMcpEditorLocks.DeleteServerStartingLock();
                if (!EditorApplication.isCompiling)
                {
                    UnityMcpEditorLocks.DeleteCompilingLock();
                }

                UnityMcpEditorLocks.DeleteDomainReloadLock();
                CleanupCompileResults();
            }
        }

        private static void EnsureEndpointCreated()
        {
            if (endpoint == null)
            {
                endpoint = new UnityMcpEditorEndpoint();
            }
        }

        private static void OnEditorUpdate()
        {
            UnityMcpMainThread.Drain();
            endpoint?.Tick();
        }

        private static void OnBeforeAssemblyReload()
        {
            if (IsBackgroundUnityProcess())
            {
                return;
            }

            UnityMcpEditorLocks.CreateDomainReloadLock();
            UnityMcpEditorSettings.WasRunning = endpoint != null && endpoint.IsRunning;
            UnityMcpDomainReloadJournal.Append("before", endpoint?.EndpointId, endpoint?.Port ?? UnityMcpEditorSettings.Port, UnityMcpEditorSettings.WasRunning);
            if (endpoint != null)
            {
                UnityMcpEditorSettings.Port = endpoint.Port;
                endpoint.Stop();
            }
        }

        private static void OnAfterAssemblyReload()
        {
            ScheduleStartup();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode)
            {
                return;
            }

            EditorApplication.delayCall += EnsurePlayModeRuntimeStarted;
        }

        private static void EnsurePlayModeRuntimeStarted()
        {
            if (IsBackgroundUnityProcess() || !EditorApplication.isPlaying)
            {
                return;
            }

            UnityMcpRuntimeHost.EnsureAutoStarted();
        }

        private static void OnCompilationStarted(object context)
        {
            compilerMessages.Clear();
            UnityMcpEditorLocks.CreateCompilingLock();
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null)
            {
                return;
            }

            foreach (var message in messages)
            {
                compilerMessages.Add(new CompileMessageRecord
                {
                    assemblyPath = assemblyPath,
                    message = message.message,
                    file = message.file,
                    line = message.line,
                    column = message.column,
                    type = message.type.ToString()
                });
            }
        }

        private static void OnCompilationFinished(object context)
        {
            UnityMcpEditorLocks.DeleteCompilingLock();
            CleanupCompileResults();
            WriteCompileResult();
        }

        private static void WriteCompileResult()
        {
            var requestId = UnityMcpEditorSettings.ActiveCompileRequestId;
            if (string.IsNullOrEmpty(requestId))
            {
                return;
            }

            var path = Path.Combine(UnityMcpPaths.CompileResultsRoot(), requestId + ".json");
            var success = true;
            foreach (var message in compilerMessages)
            {
                if (string.Equals(message.type, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    success = false;
                    break;
                }
            }

            AtomicFile.WriteJson(path, new CompileResultRecord
            {
                requestId = requestId,
                success = success,
                completedAtUtc = DateTime.UtcNow.ToString("o"),
                compilerMessages = compilerMessages.ToArray()
            });
            UnityMcpEditorSettings.ActiveCompileRequestId = string.Empty;
        }

        private static void CleanupCompileResults()
        {
            var root = UnityMcpPaths.CompileResultsRoot();
            if (!Directory.Exists(root))
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(root, "*.json");
            }
            catch
            {
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > UnityMcpConstants.CommandResultRetention)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        }

        private static void OnEditorQuitting()
        {
            endpoint?.Dispose();
            endpoint = null;
            UnityMcpEditorLocks.DeleteCompilingLock();
            UnityMcpEditorLocks.DeleteDomainReloadLock();
            UnityMcpEditorLocks.DeleteServerStartingLock();
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private static bool IsBackgroundUnityProcess()
        {
            try
            {
                return AssetDatabase.IsAssetImportWorkerProcess();
            }
            catch
            {
                return false;
            }
        }

        [Serializable]
        private sealed class CompileResultRecord
        {
            public string requestId;
            public bool success;
            public string completedAtUtc;
            public CompileMessageRecord[] compilerMessages = new CompileMessageRecord[0];
        }

        [Serializable]
        private sealed class CompileMessageRecord
        {
            public string assemblyPath;
            public string message;
            public string file;
            public int line;
            public int column;
            public string type;
        }
    }
}
