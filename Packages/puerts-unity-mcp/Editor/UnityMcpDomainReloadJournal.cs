using System;
using System.IO;

namespace PuertsUnityMcp.Editor
{
    internal static class UnityMcpDomainReloadJournal
    {
        public static void Append(string phase, string endpointId, int port, bool wasRunning)
        {
            var path = Path.Combine(UnityMcpPaths.StateRoot, UnityMcpConstants.DomainReloadEventsFileName);
            AtomicFile.AppendLine(path, UnityJson.ToJson(new DomainReloadEventRecord
            {
                phase = phase,
                endpointId = endpointId,
                port = port,
                wasRunning = wasRunning,
                processId = UnityMcpInstanceRegistry.GetProcessId(),
                timestampUtc = DateTime.UtcNow.ToString("o")
            }, false));
            Trim(path);
        }

        private static void Trim(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                var lines = File.ReadAllLines(path);
                if (lines.Length <= UnityMcpConstants.DomainReloadJournalMaxLines)
                {
                    return;
                }

                var start = lines.Length - UnityMcpConstants.DomainReloadJournalMaxLines;
                var kept = new string[UnityMcpConstants.DomainReloadJournalMaxLines];
                Array.Copy(lines, start, kept, 0, kept.Length);
                AtomicFile.WriteAllText(path, string.Join(Environment.NewLine, kept) + Environment.NewLine);
            }
            catch
            {
            }
        }

        [Serializable]
        private sealed class DomainReloadEventRecord
        {
            public string phase;
            public string endpointId;
            public int port;
            public bool wasRunning;
            public int processId;
            public string timestampUtc;
        }
    }
}
