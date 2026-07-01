using System;

namespace PuertsUnityMcp
{
    [Serializable]
    public sealed class UnityMcpCommand
    {
        public string id;
        public string action;
        public string targetId;
        public UnityMcpToolArguments @params = new UnityMcpToolArguments();
    }

    [Serializable]
    public sealed class UnityMcpCommandResult
    {
        public string id;
        public bool success;
        public string resultJson;
        public string error;
        public string startedAtUtc;
        public string completedAtUtc;
        public long executionTimeMs;

        public static UnityMcpCommandResult Ok(string id, string resultJson, DateTime startedAtUtc, long elapsedMs)
        {
            return new UnityMcpCommandResult
            {
                id = id,
                success = true,
                resultJson = resultJson,
                startedAtUtc = startedAtUtc.ToString("o"),
                completedAtUtc = DateTime.UtcNow.ToString("o"),
                executionTimeMs = elapsedMs
            };
        }

        public static UnityMcpCommandResult Fail(string id, string error, DateTime startedAtUtc, long elapsedMs)
        {
            return new UnityMcpCommandResult
            {
                id = id,
                success = false,
                error = error,
                startedAtUtc = startedAtUtc.ToString("o"),
                completedAtUtc = DateTime.UtcNow.ToString("o"),
                executionTimeMs = elapsedMs
            };
        }
    }

    [Serializable]
    public sealed class UnityMcpToolDescriptor
    {
        public string name;
        public string description;
        public string inputSchemaJson;
    }

    [Serializable]
    public sealed class UnityMcpToolArguments
    {
        public string rawArgumentsJson;
        public string code;
        public string mode;
        public string chunkName;
        public string toolName;
        public string targetId;
        public string httpUrl;
        public string requestId;
        public bool wait;
        public string operationId;
        public string typeName;
        public string methodName;
        public string argsJson;
        public string fileName;
        public float x;
        public float y;
        public string state;
        public string keyword;
        public string path;
        public string target;
        public string profilerTargetName;
        public string profilerTargetId;
        public string profilerTargetUrl;
        public string scenario;
        public bool record;
        public int firstFrame;
        public int lastFrame;
        public int maxMarkers;
        public bool stopRecording;
        public bool openProfiler;
        public string rootPath;
        public bool includeInactive;
        public int maxDepth;
        public bool includeComponents;
        public bool includePaths;
        public string useComponentsLut;
        public bool useSelection;
        public string windowName;
        public string matchMode;
        public string outputDirectory;
        public string captureMode;
        public float resolutionScale;
        public float hitchThresholdMs;
        public int instanceId;
        public int maxResults;
        public bool includeDisabled;
        public string logType;
        public int count;
        public string regex;
        public bool includeStackTrace;
        public string action;
        public string name;
        public string key;
        public string resourcePath;
        public float fromX;
        public float fromY;
        public float duration;
        public float durationMs;
        public float dragSpeed;
        public float deltaX;
        public float deltaY;
        public float scrollX;
        public float scrollY;
        public bool bypassRaycast;
        public string dropTargetPath;
    }

    [Serializable]
    public sealed class UnityMcpHeartbeat
    {
        public string endpointId;
        public string endpointKind;
        public string endpointName;
        public string projectRoot;
        public string projectName;
        public string name;
        public int processId;
        public string httpUrl;
        public int port;
        public string unityVersion;
        public string platform;
        public bool isEditor;
        public string lastUpdatedUtc;
        public UnityMcpCapabilities capabilities = new UnityMcpCapabilities();
        public string source;
    }

    [Serializable]
    public sealed class UnityMcpCapabilities
    {
        public bool jsEval;
        public bool editorJsEval;
        public bool runtimeJsEval;
        public bool reflection;
        public bool privateReflection;
        public bool fileAccess;
        public bool networkAccess;
        public bool runtimeCodeLoad;
        public bool screenshot;
        public bool uiClick;
        public bool uiSnapshot;
        public bool uiFind;
        public bool uiRaycast;
        public bool runtimeLogs;
        public bool runtimeToolCall;
        public bool fileCommandPump;
        public bool http;
        public bool domainReloadRecovery;
        public bool compileLocks;
    }

    [Serializable]
    public sealed class UnityMcpHealth
    {
        public string status;
        public bool ready;
        public string runtimeState;
        public string endpointKind;
        public string endpointId;
        public string endpointName;
        public string projectRoot;
        public string stateRoot;
        public string httpUrl;
        public int httpPort;
        public string unityVersion;
        public string platform;
        public string productName;
        public string applicationVersion;
        public bool isEditor;
        public bool isDebugBuild;
        public bool isCompiling;
        public bool isUpdating;
        public bool isPlaying;
        public bool isPaused;
        public bool isPlayingOrWillChangePlaymode;
        public double timeSinceStartup;
        public double uptimeSeconds;
        public long lastMainThreadTickAgeMs;
        public int toolRegistryVersion;
        public UnityMcpCapabilities capabilities = new UnityMcpCapabilities();
    }

    [Serializable]
    public sealed class UnityMcpTargetList
    {
        public UnityMcpHeartbeat[] targets = new UnityMcpHeartbeat[0];
    }

    [Serializable]
    public sealed class UnityMcpJsonRpcRequest
    {
        public string jsonrpc;
        public string id;
        public string method;
        public UnityMcpJsonRpcParams @params = new UnityMcpJsonRpcParams();
    }

    [Serializable]
    public sealed class UnityMcpJsonRpcParams
    {
        public string protocolVersion;
        public string name;
        public UnityMcpToolArguments arguments = new UnityMcpToolArguments();
    }

    [Serializable]
    public sealed class UnityMcpJsonRpcResponse
    {
        public string jsonrpc = "2.0";
        public string id;
        public UnityMcpJsonRpcResult result;
        public UnityMcpJsonRpcError error;
    }

    [Serializable]
    public sealed class UnityMcpJsonRpcResult
    {
        public string protocolVersion;
        public UnityMcpServerInfo serverInfo;
        public UnityMcpJsonRpcCapabilities capabilities;
        public string instructions;
        public UnityMcpToolDescriptor[] tools;
        public UnityMcpToolContent[] content;
        public string structuredContentJson;
        public bool isError;
        public string valueJson;
    }

    [Serializable]
    public sealed class UnityMcpServerInfo
    {
        public string name;
        public string version;
        public string endpointId;
        public string endpointKind;
        public string endpointName;
    }

    [Serializable]
    public sealed class UnityMcpJsonRpcCapabilities
    {
        public UnityMcpToolsCapability tools = new UnityMcpToolsCapability();
    }

    [Serializable]
    public sealed class UnityMcpToolsCapability
    {
        public bool listChanged;
    }

    [Serializable]
    public sealed class UnityMcpToolContent
    {
        public string type;
        public string text;
    }

    [Serializable]
    public sealed class UnityMcpJsonRpcError
    {
        public int code;
        public string message;
    }
}
