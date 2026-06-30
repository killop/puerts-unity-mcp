using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PuertsUnityMcp
{
    public sealed class UnityMcpJsonRpc
    {
        private const string ResponseIdPlaceholder = "__PUERTS_UNITY_MCP_RESPONSE_ID__";
        private const string AgentInstructions =
            "PuerTS Unity MCP controls Unity Editor, Play Mode, and real Player/phone targets. " +
            "Use editor.js.eval for Unity Editor automation; it runs JavaScript in the Editor PuerTS VM and normally does not generate C# or trigger domain reload. " +
            "Use runtime.js.eval for Play Mode, Android, iOS, or standalone Player automation; pass targetId/httpUrl when targeting a remote phone/player. " +
            "Write PuerTS JavaScript with CS.UnityEngine/CS.UnityEditor first, return only JSON-serializable values, and do not return Unity objects directly. " +
            "If a wrapped C# type or member is unavailable, use __unity_mcp.typeExists/getStatic/getStaticPath/setStatic/invokeStatic as the reflection fallback. " +
            "For phone UI automation, observe before acting with screen.screenshot, runtime.ui.snapshot, runtime.ui.find, and runtime.ui.raycast, then click with runtime.ui.click or input.tap. " +
            "Move stable project-specific flows into puerts-unity-mcp-extension/Editor/editor-tools or puerts-unity-mcp-extension/Runtime/runtime-tools instead of repeatedly generating one-off eval scripts.";

        private readonly IUnityMcpEndpoint endpoint;

        public UnityMcpJsonRpc(IUnityMcpEndpoint endpoint)
        {
            this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        }

        public async Task<string> HandleAsync(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return Error(null, -32700, "Empty JSON-RPC body");
            }

            UnityMcpJsonRpcRequest request;
            try
            {
                request = UnityJson.FromJson<UnityMcpJsonRpcRequest>(body);
            }
            catch (Exception ex)
            {
                return Error(null, -32700, "Parse error: " + ex.Message);
            }

            var responseId = ExtractRawId(body);
            AttachRawArguments(body, request);
            if (request == null || string.IsNullOrEmpty(request.method))
            {
                return Error(responseId, -32600, "Missing method");
            }

            try
            {
                switch (request.method)
                {
                    case "initialize":
                        return InitializeSuccess(responseId, BuildInitializeResult(request.@params));
                    case "ping":
                        return ValueSuccess(responseId, "{}");
                    case "tools/list":
                        return ToolsListSuccess(responseId, endpoint.Tools.List().ToArray());
                    case "tools/call":
                        return ToolCallSuccess(responseId, await HandleToolCallAsync(request.@params));
                    case "notifications/initialized":
                    case "notifications/cancelled":
                        return string.Empty;
                    default:
                        return ValueSuccess(responseId, await endpoint.CallToolAsync(request.method, request.@params == null ? null : request.@params.arguments));
                }
            }
            catch (Exception ex)
            {
                return Error(responseId, -32000, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private InitializeResult BuildInitializeResult(UnityMcpJsonRpcParams parameters)
        {
            var requestedProtocol = parameters == null ? null : parameters.protocolVersion;
            return new InitializeResult
            {
                protocolVersion = string.IsNullOrEmpty(requestedProtocol) ? "2025-06-18" : requestedProtocol,
                serverInfo = new UnityMcpServerInfo
                {
                    name = UnityMcpConstants.PackageName,
                    version = UnityMcpConstants.Version,
                    endpointId = endpoint.EndpointId,
                    endpointKind = endpoint.EndpointKind,
                    endpointName = endpoint.EndpointName
                },
                capabilities = new UnityMcpJsonRpcCapabilities
                {
                    tools = new UnityMcpToolsCapability { listChanged = true }
                },
                instructions = AgentInstructions
            };
        }

        private async Task<ToolCallResult> HandleToolCallAsync(UnityMcpJsonRpcParams parameters)
        {
            if (parameters == null || string.IsNullOrEmpty(parameters.name))
            {
                throw new InvalidOperationException("tools/call requires params.name.");
            }

            var structuredJson = await endpoint.CallToolAsync(parameters.name, parameters.arguments);
            return new ToolCallResult
            {
                content = new[]
                {
                    new UnityMcpToolContent
                    {
                        type = "text",
                        text = string.IsNullOrEmpty(structuredJson) ? "null" : structuredJson
                    }
                },
                structuredContentJson = structuredJson,
                isError = false,
                valueJson = structuredJson
            };
        }

        private static string InitializeSuccess(string id, InitializeResult result)
        {
            return SerializeResponseWithRawId(new InitializeResponse
            {
                id = ResponseIdPlaceholder,
                result = result
            }, id);
        }

        private static string ToolsListSuccess(string id, UnityMcpToolDescriptor[] tools)
        {
            var builder = new StringBuilder();
            builder.Append("{\"jsonrpc\":\"2.0\",\"id\":").Append(NormalizeRawId(id)).Append(",\"result\":{\"tools\":[");
            var safeTools = tools ?? new UnityMcpToolDescriptor[0];
            for (var i = 0; i < safeTools.Length; i++)
            {
                var tool = safeTools[i];
                if (i > 0)
                {
                    builder.Append(",");
                }

                builder.Append("{\"name\":").Append(UnityJson.ToJsonStringValue(tool == null ? string.Empty : tool.name))
                    .Append(",\"description\":").Append(UnityJson.ToJsonStringValue(tool == null ? string.Empty : tool.description))
                    .Append(",\"inputSchema\":").Append(NormalizeSchemaJson(tool == null ? null : tool.inputSchemaJson))
                    .Append("}");
            }

            builder.Append("]}}");
            return builder.ToString();
        }

        private static string ToolCallSuccess(string id, ToolCallResult result)
        {
            var structuredJson = result == null ? null : result.structuredContentJson;
            var structuredValue = string.IsNullOrWhiteSpace(structuredJson) ? "null" : structuredJson.Trim();
            var text = structuredValue;
            if (result != null && result.content != null && result.content.Length > 0 && result.content[0] != null)
            {
                text = result.content[0].text;
            }

            return "{\"jsonrpc\":\"2.0\",\"id\":" + NormalizeRawId(id)
                + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":" + UnityJson.ToJsonStringValue(text)
                + "}],\"structuredContent\":" + structuredValue
                + ",\"isError\":" + (result != null && result.isError ? "true" : "false")
                + "}}";
        }

        private static string ValueSuccess(string id, string valueJson)
        {
            return SerializeResponseWithRawId(new ValueResponse
            {
                id = ResponseIdPlaceholder,
                result = new ValueResult
                {
                    valueJson = string.IsNullOrEmpty(valueJson) ? "{}" : valueJson
                }
            }, id);
        }

        private static string Error(string id, int code, string message)
        {
            return SerializeResponseWithRawId(new ErrorResponse
            {
                id = ResponseIdPlaceholder,
                error = new UnityMcpJsonRpcError
                {
                    code = code,
                    message = message ?? "Unknown error"
                }
            }, id);
        }

        private static string SerializeResponseWithRawId<T>(T response, string rawId)
        {
            var json = UnityJson.ToJson(response);
            return json.Replace("\"id\":\"" + ResponseIdPlaceholder + "\"", "\"id\":" + NormalizeRawId(rawId));
        }

        private static string NormalizeRawId(string rawId)
        {
            return string.IsNullOrWhiteSpace(rawId) ? "null" : rawId.Trim();
        }

        private static string NormalizeSchemaJson(string schemaJson)
        {
            return string.IsNullOrWhiteSpace(schemaJson) ? JsonSchemas.Object() : schemaJson.Trim();
        }

        private static string ExtractRawId(string json)
        {
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

                if (!TryReadJsonString(json, ref index, out var propertyName))
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

                if (propertyName == "id")
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

        private static void AttachRawArguments(string json, UnityMcpJsonRpcRequest request)
        {
            if (request == null || request.@params == null || request.@params.arguments == null)
            {
                return;
            }

            var rawArguments = ExtractNestedProperty(json, "params", "arguments");
            if (!string.IsNullOrWhiteSpace(rawArguments))
            {
                request.@params.arguments.rawArgumentsJson = rawArguments.Trim();
            }
        }

        private static string ExtractNestedProperty(string json, string firstPropertyName, string secondPropertyName)
        {
            var first = ExtractObjectProperty(json, 0, firstPropertyName);
            if (string.IsNullOrWhiteSpace(first))
            {
                return null;
            }

            return ExtractObjectProperty(first, 0, secondPropertyName);
        }

        private static string ExtractObjectProperty(string json, int objectStart, string propertyName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            var index = objectStart;
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
                    return json.Substring(valueStart, valueEnd - valueStart);
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
            var builder = new StringBuilder();
            while (index < json.Length)
            {
                var ch = json[index++];
                if (ch == '"')
                {
                    value = builder.ToString();
                    return true;
                }

                if (ch == '\\' && index < json.Length)
                {
                    var escaped = json[index++];
                    if (escaped == '"' || escaped == '\\' || escaped == '/')
                    {
                        builder.Append(escaped);
                    }
                    else
                    {
                        builder.Append(ch);
                        builder.Append(escaped);
                    }

                    continue;
                }

                builder.Append(ch);
            }

            return false;
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

        [Serializable]
        private sealed class InitializeResponse
        {
            public string jsonrpc = "2.0";
            public string id;
            public InitializeResult result;
        }

        [Serializable]
        private sealed class InitializeResult
        {
            public string protocolVersion;
            public UnityMcpServerInfo serverInfo;
            public UnityMcpJsonRpcCapabilities capabilities;
            public string instructions;
        }

        [Serializable]
        private sealed class ToolsListResponse
        {
            public string jsonrpc = "2.0";
            public string id;
            public ToolsListResult result;
        }

        [Serializable]
        private sealed class ToolsListResult
        {
            public UnityMcpToolDescriptor[] tools;
        }

        [Serializable]
        private sealed class ToolCallResponse
        {
            public string jsonrpc = "2.0";
            public string id;
            public ToolCallResult result;
        }

        [Serializable]
        private sealed class ToolCallResult
        {
            public UnityMcpToolContent[] content;
            public string structuredContentJson;
            public bool isError;
            public string valueJson;
        }

        [Serializable]
        private sealed class ValueResponse
        {
            public string jsonrpc = "2.0";
            public string id;
            public ValueResult result;
        }

        [Serializable]
        private sealed class ValueResult
        {
            public string valueJson;
        }

        [Serializable]
        private sealed class ErrorResponse
        {
            public string jsonrpc = "2.0";
            public string id;
            public UnityMcpJsonRpcError error;
        }
    }
}
