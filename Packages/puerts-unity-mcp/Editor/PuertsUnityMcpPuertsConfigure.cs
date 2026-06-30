using System.IO;
using Puerts;
using UnityEngine;

namespace PuertsUnityMcp.Editor
{
    [Configure]
    public static class PuertsUnityMcpPuertsConfigure
    {
        [CodeOutputDirectory]
        public static string CodeOutputDirectory
        {
            get
            {
                return Path.Combine(Application.dataPath, "puerts-unity-mcp", "Runtime", "Generated") + Path.DirectorySeparatorChar;
            }
        }
    }
}
