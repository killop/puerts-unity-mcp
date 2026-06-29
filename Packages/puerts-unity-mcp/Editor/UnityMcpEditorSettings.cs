using UnityEditor;

namespace PuertsUnityMcp.Editor
{
    internal static class UnityMcpEditorSettings
    {
        private const string KeyPrefix = "PuertsUnityMcp.";

        public static int Port
        {
            get => EditorPrefs.GetInt(KeyPrefix + "Port", UnityMcpConstants.DefaultEditorPort);
            set => EditorPrefs.SetInt(KeyPrefix + "Port", value);
        }

        public static bool WasRunning
        {
            get => SessionState.GetBool(KeyPrefix + "WasRunning", EditorPrefs.GetBool(KeyPrefix + "WasRunning", true));
            set
            {
                SessionState.SetBool(KeyPrefix + "WasRunning", value);
                EditorPrefs.SetBool(KeyPrefix + "WasRunning", value);
            }
        }

        public static string ActiveCompileRequestId
        {
            get => SessionState.GetString(KeyPrefix + "ActiveCompileRequestId", string.Empty);
            set => SessionState.SetString(KeyPrefix + "ActiveCompileRequestId", value ?? string.Empty);
        }
    }
}

