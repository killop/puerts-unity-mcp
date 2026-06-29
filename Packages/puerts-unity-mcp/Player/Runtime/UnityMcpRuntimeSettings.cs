using System;
using UnityEngine;

namespace PuertsUnityMcp
{
    [Serializable]
    public sealed class UnityMcpRuntimeSettings
    {
        public bool mcpEnabled = true;
        public bool allowJsEval = true;
        public bool allowReflection = true;
        public bool allowPrivateReflection = true;
        public bool allowFileAccess = true;
        public bool allowNetworkAccess = true;
        public bool allowRuntimeCodeLoad = true;
        public bool requireAuthToken = false;
        public string authToken = "";
        public string bindAddress = "0.0.0.0";
        public int httpPort = UnityMcpConstants.DefaultPlayerPort;
        public string targetId = "";
        public string exchangeDirectory = "";
        public float pollIntervalSeconds = 0.1f;
        public int maxCommandsPerFrame = 4;
        public int logBufferSize = 500;
        public bool runInBackground = true;

        public static UnityMcpRuntimeSettings CreateDefault()
        {
            return new UnityMcpRuntimeSettings();
        }
    }
}
