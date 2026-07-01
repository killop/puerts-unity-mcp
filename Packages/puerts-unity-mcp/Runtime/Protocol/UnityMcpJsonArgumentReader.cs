using System;
using System.Globalization;
using System.Text;

namespace PuertsUnityMcp
{
    public static class UnityMcpJsonArgumentReader
    {
        public static bool TryGetString(string json, string propertyName, out string value)
        {
            value = null;
            var raw = GetRawProperty(json, propertyName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            raw = raw.Trim();
            if (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
            {
                value = UnescapeJsonString(raw.Substring(1, raw.Length - 2));
                return true;
            }

            value = raw;
            return true;
        }

        public static bool TryGetDouble(string json, string propertyName, out double value)
        {
            value = 0d;
            if (!TryGetString(json, propertyName, out var text) || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryGetInt(string json, string propertyName, out int value)
        {
            value = 0;
            if (!TryGetDouble(json, propertyName, out var number))
            {
                return false;
            }

            if (number < int.MinValue || number > int.MaxValue)
            {
                return false;
            }

            value = (int)Math.Round(number);
            return true;
        }

        public static bool TryGetBool(string json, string propertyName, out bool value)
        {
            value = false;
            if (!TryGetString(json, propertyName, out var text) || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "1", StringComparison.Ordinal))
            {
                value = true;
                return true;
            }

            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "0", StringComparison.Ordinal))
            {
                value = false;
                return true;
            }

            return false;
        }

        public static string GetRawProperty(string json, string propertyName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            var index = 0;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index] != '{')
            {
                return null;
            }

            index++;
            while (index < json.Length)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] == '}')
                {
                    return null;
                }

                if (!TryReadJsonString(json, ref index, out var currentPropertyName))
                {
                    return null;
                }

                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':')
                {
                    return null;
                }

                index++;
                SkipWhitespace(json, ref index);
                var valueStart = index;
                var valueEnd = FindJsonValueEnd(json, index);
                if (valueEnd < valueStart)
                {
                    return null;
                }

                if (string.Equals(currentPropertyName, propertyName, StringComparison.Ordinal))
                {
                    return json.Substring(valueStart, valueEnd - valueStart).Trim();
                }

                index = valueEnd;
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                }
            }

            return null;
        }

        private static bool TryReadJsonString(string json, ref int index, out string value)
        {
            value = null;
            if (index >= json.Length || json[index] != '"')
            {
                return false;
            }

            index++;
            var start = index;
            var escaped = false;
            var builder = new StringBuilder();
            while (index < json.Length)
            {
                var ch = json[index++];
                if (escaped)
                {
                    builder.Append(DecodeEscapedChar(ch));
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    value = builder.Length == 0 && index - 1 > start
                        ? json.Substring(start, index - start - 1)
                        : builder.ToString();
                    return true;
                }

                builder.Append(ch);
            }

            return false;
        }

        private static string UnescapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            var escaped = false;
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (escaped)
                {
                    builder.Append(DecodeEscapedChar(ch));
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                builder.Append(ch);
            }

            if (escaped)
            {
                builder.Append('\\');
            }

            return builder.ToString();
        }

        private static char DecodeEscapedChar(char ch)
        {
            switch (ch)
            {
                case '"':
                case '\\':
                case '/':
                    return ch;
                case 'b':
                    return '\b';
                case 'f':
                    return '\f';
                case 'n':
                    return '\n';
                case 'r':
                    return '\r';
                case 't':
                    return '\t';
                default:
                    return ch;
            }
        }

        private static int FindJsonValueEnd(string json, int index)
        {
            if (index >= json.Length)
            {
                return index;
            }

            var first = json[index];
            if (first == '"')
            {
                return ScanJsonString(json, index);
            }

            if (first == '{' || first == '[')
            {
                return ScanJsonContainer(json, index);
            }

            while (index < json.Length && json[index] != ',' && json[index] != '}' && json[index] != ']')
            {
                index++;
            }

            return index;
        }

        private static int ScanJsonString(string json, int index)
        {
            index++;
            while (index < json.Length)
            {
                var ch = json[index++];
                if (ch == '\\' && index < json.Length)
                {
                    index++;
                    continue;
                }

                if (ch == '"')
                {
                    return index;
                }
            }

            return index;
        }

        private static int ScanJsonContainer(string json, int index)
        {
            var depth = 0;
            while (index < json.Length)
            {
                var ch = json[index];
                if (ch == '"')
                {
                    index = ScanJsonString(json, index);
                    continue;
                }

                if (ch == '{' || ch == '[')
                {
                    depth++;
                }
                else if (ch == '}' || ch == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return index + 1;
                    }
                }

                index++;
            }

            return index;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }
        }
    }
}
