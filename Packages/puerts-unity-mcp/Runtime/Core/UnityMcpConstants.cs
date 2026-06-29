using System;

namespace PuertsUnityMcp
{
    public static class UnityMcpConstants
    {
        public const string PackageName = "puerts-unity-mcp";
        public const string DisplayName = "PuerTS Unity MCP";
        public const string Version = "0.1.0";
        public const string StateDirectoryName = ".puerts-unity-mcp";
        public const string ExtensionDirectoryName = "puerts-unity-mcp-extension";
        public const string AssetsDirectoryName = "Assets";
        public const string ProjectAssetsDirectoryName = "PuertsUnityMcp";
        public const string EditorDirectoryName = "Editor";
        public const string RuntimeDirectoryName = "Runtime";
        public const string EditorToolsDirectoryName = "editor-tools";
        public const string RuntimeToolsDirectoryName = "runtime-tools";
        public const string SkillsDirectoryName = "skills";
        public const string ResourcesDirectoryName = "Resources";
        public const string TempDirectoryName = "temp";
        public const string ToolsDirectoryName = "tools";
        public const string LogsDirectoryName = "logs";
        public const string EditorsDirectoryName = "editors";
        public const string PlayersDirectoryName = "players";
        public const string CommandsDirectoryName = "commands";
        public const string ResultsDirectoryName = "results";
        public const string ArtifactsDirectoryName = "artifacts";
        public const string OpsDirectoryName = "ops";
        public const string HeartbeatFileName = "heartbeat.json";
        public const string InstancesFileName = "instances.json";
        public const string ConfigFileName = "editor-mcp-config.json";
        public const string RuntimeConfigFileName = "mobile-mcp-config.json";
        public const string LegacyConfigFileName = "config.json";
        public const string LegacyRuntimeConfigFileName = "runtime-config.json";
        public const string DiscoveryProtocol = "puerts-unity-mcp.discovery.v1";
        public const string DiscoverySource = "lan";
        public const int DiscoveryPort = 18992;
        public const int DiscoveryIntervalMs = 2000;
        public const string DomainReloadEventsFileName = "domainreload-events.jsonl";
        public const string DomainReloadLockName = "domainreload.lock";
        public const string CompilingLockName = "compiling.lock";
        public const string ServerStartingLockName = "serverstarting.lock";
        public const string CompileResultsDirectoryName = "compile-results";
        public const string AotMissesFileName = "aot-misses.jsonl";
        public const int DefaultEditorPort = 18990;
        public const int DefaultPlayerPort = 18991;
        public const int DefaultCommandTimeoutMs = 60000;
        public const int HeartbeatIntervalMs = 2000;
        public const int StaleFileMinutes = 10;
        public const int OperationCleanupIntervalSeconds = 30;
        public const int OperationDirectoryLimit = 200;
        public const int InProgressOperationRetentionMinutes = 60;
        public const int DomainReloadJournalMaxLines = 200;

        public static readonly TimeSpan CommandResultRetention = TimeSpan.FromMinutes(StaleFileMinutes);
        public static readonly TimeSpan OperationResultRetention = TimeSpan.FromMinutes(StaleFileMinutes);
        public static readonly TimeSpan InProgressOperationRetention = TimeSpan.FromMinutes(InProgressOperationRetentionMinutes);
    }
}
