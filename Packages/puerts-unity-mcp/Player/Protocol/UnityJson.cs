using System;
using UnityEngine;

namespace PuertsUnityMcp
{
    public static class UnityJson
    {
        public static string ToJson<T>(T value, bool pretty = false)
        {
            return JsonUtility.ToJson(value, pretty);
        }

        public static T FromJson<T>(string json)
        {
            return JsonUtility.FromJson<T>(StripBom(json));
        }

        public static void FromJsonOverwrite(string json, object objectToOverwrite)
        {
            JsonUtility.FromJsonOverwrite(StripBom(json), objectToOverwrite);
        }

        public static string Quote(string value)
        {
            return JsonUtility.ToJson(new StringBox { value = value ?? string.Empty });
        }

        public static string EscapeString(string value)
        {
            var json = Quote(value);
            const string prefix = "{\"value\":\"";
            const string suffix = "\"}";
            if (json.StartsWith(prefix, StringComparison.Ordinal) && json.EndsWith(suffix, StringComparison.Ordinal))
            {
                return json.Substring(prefix.Length, json.Length - prefix.Length - suffix.Length);
            }

            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public static string ToJsonStringValue(string value)
        {
            return "\"" + EscapeString(value) + "\"";
        }

        private static string StripBom(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }

            return content[0] == '\ufeff' ? content.Substring(1) : content;
        }

        [Serializable]
        private sealed class StringBox
        {
            public string value;
        }
    }
}
