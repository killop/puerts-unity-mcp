using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace PuertsUnityMcp
{
    public sealed class UnityMcpRuntimeLogBuffer : IDisposable
    {
        private const int UnknownFrame = -1;
        private readonly object syncRoot = new object();
        private readonly List<UnityMcpRuntimeLogEntry> entries = new List<UnityMcpRuntimeLogEntry>();
        private int capacity = 500;
        private int mainThreadId;
        private bool initialized;

        public int Count
        {
            get
            {
                lock (syncRoot)
                {
                    return entries.Count;
                }
            }
        }

        public void Initialize(int requestedCapacity)
        {
            capacity = Math.Max(1, requestedCapacity);
            if (initialized)
            {
                return;
            }

            mainThreadId = Environment.CurrentManagedThreadId;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
            initialized = true;
        }

        public void Dispose()
        {
            if (!initialized)
            {
                return;
            }

            Application.logMessageReceivedThreaded -= OnLogMessageReceived;
            initialized = false;
        }

        public int Clear()
        {
            lock (syncRoot)
            {
                var previous = entries.Count;
                entries.Clear();
                return previous;
            }
        }

        public UnityMcpRuntimeLogEntry[] GetEntries(int requestedCount, string requestedType, string regexPattern, bool includeStackTrace)
        {
            requestedCount = Math.Max(1, requestedCount <= 0 ? 100 : requestedCount);
            var regex = string.IsNullOrEmpty(regexPattern) ? null : new Regex(regexPattern, RegexOptions.CultureInvariant);
            var result = new List<UnityMcpRuntimeLogEntry>(Math.Min(requestedCount, Count));

            lock (syncRoot)
            {
                for (var i = entries.Count - 1; i >= 0 && result.Count < requestedCount; i--)
                {
                    var entry = entries[i];
                    if (!MatchesLogType(requestedType, entry.type))
                    {
                        continue;
                    }

                    if (regex != null && !regex.IsMatch(entry.message ?? string.Empty))
                    {
                        continue;
                    }

                    result.Add(CloneEntry(entry, includeStackTrace));
                }
            }

            result.Reverse();
            return result.ToArray();
        }

        private void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            var now = DateTime.UtcNow;
            var entry = new UnityMcpRuntimeLogEntry
            {
                type = NormalizeLogType(type),
                message = Truncate(condition, 4096),
                stackTrace = Truncate(stackTrace, 8192),
                timestampUtc = now.ToString("o"),
                timestampUnixMs = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
                frame = GetFrameForCurrentThread()
            };

            lock (syncRoot)
            {
                entries.Add(entry);
                while (entries.Count > capacity)
                {
                    entries.RemoveAt(0);
                }
            }
        }

        private int GetFrameForCurrentThread()
        {
            return Environment.CurrentManagedThreadId == mainThreadId ? Time.frameCount : UnknownFrame;
        }

        private static UnityMcpRuntimeLogEntry CloneEntry(UnityMcpRuntimeLogEntry entry, bool includeStackTrace)
        {
            return new UnityMcpRuntimeLogEntry
            {
                type = entry.type,
                message = entry.message,
                stackTrace = includeStackTrace ? entry.stackTrace : null,
                timestampUtc = entry.timestampUtc,
                timestampUnixMs = entry.timestampUnixMs,
                frame = entry.frame
            };
        }

        private static bool MatchesLogType(string requestedType, string entryType)
        {
            if (string.IsNullOrEmpty(requestedType) || string.Equals(requestedType, "All", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(requestedType, entryType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(requestedType, "Error", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(entryType, "Exception", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entryType, "Assert", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeLogType(LogType type)
        {
            switch (type)
            {
                case LogType.Warning:
                    return "Warning";
                case LogType.Error:
                    return "Error";
                case LogType.Assert:
                    return "Assert";
                case LogType.Exception:
                    return "Exception";
                default:
                    return "Log";
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength) + "...";
        }
    }

    [Serializable]
    public sealed class UnityMcpRuntimeLogEntry
    {
        public string type;
        public string message;
        public string stackTrace;
        public string timestampUtc;
        public long timestampUnixMs;
        public int frame;
    }
}
