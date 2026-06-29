using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Scripting;

namespace PuertsUnityMcp
{
    [Preserve]
    public sealed class ReflectionGateway
    {
        private static readonly BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        private static readonly BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static bool EnableAotMissLog { get; set; }

        [Preserve]
        public string InvokeStaticJson(string typeName, string methodName, string argsJson)
        {
            var type = ResolveType(typeName, methodName);
            var args = ParseArgs(argsJson);
            var method = FindMethod(type, methodName, args, AllStatic);
            if (method == null)
            {
                RecordMiss(typeName, methodName, "static_method_not_found");
                throw new MissingMethodException(typeName, methodName);
            }

            var converted = ConvertArguments(method.GetParameters(), args);
            return SerializeResult(method.Invoke(null, converted));
        }

        [Preserve]
        public string GetStaticJson(string typeName, string memberName)
        {
            var type = ResolveType(typeName, memberName);
            var property = type.GetProperty(memberName, AllStatic);
            if (property != null)
            {
                return SerializeResult(property.GetValue(null, null));
            }

            var field = type.GetField(memberName, AllStatic);
            if (field != null)
            {
                return SerializeResult(field.GetValue(null));
            }

            RecordMiss(typeName, memberName, "static_member_not_found");
            throw new MissingMemberException(typeName, memberName);
        }

        [Preserve]
        public string GetStaticPathJson(string typeName, string memberPath)
        {
            var type = ResolveType(typeName, memberPath);
            object current = type;
            var segments = (memberPath ?? string.Empty)
                .Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                current = ResolvePathSegment(typeName, current, segment);
            }

            return SerializeResult(current);
        }

        [Preserve]
        public void SetStaticJson(string typeName, string memberName, string valueJson)
        {
            var type = ResolveType(typeName, memberName);
            var value = ParseSingleArg(valueJson);
            var property = type.GetProperty(memberName, AllStatic);
            if (property != null)
            {
                property.SetValue(null, ConvertArg(value, property.PropertyType), null);
                return;
            }

            var field = type.GetField(memberName, AllStatic);
            if (field != null)
            {
                field.SetValue(null, ConvertArg(value, field.FieldType));
                return;
            }

            RecordMiss(typeName, memberName, "static_member_not_found");
            throw new MissingMemberException(typeName, memberName);
        }

        [Preserve]
        public string TypeExists(string typeName)
        {
            return SerializeResult(FindType(typeName) != null);
        }

        private static Type ResolveType(string typeName, string memberName)
        {
            var type = FindType(typeName);
            if (type != null)
            {
                return type;
            }

            RecordMiss(typeName, memberName, "type_not_found");
            throw new TypeLoadException("Type not found: " + typeName);
        }

        private static Type FindType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var direct = Type.GetType(typeName);
            if (direct != null)
            {
                return direct;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try
                {
                    type = assembly.GetType(typeName, false);
                }
                catch
                {
                }

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static object ResolvePathSegment(string typeName, object current, string segment)
        {
            if (current == null)
            {
                return null;
            }

            var name = segment ?? string.Empty;
            var index = -1;
            var bracket = name.IndexOf('[');
            if (bracket >= 0)
            {
                var close = name.IndexOf(']', bracket + 1);
                if (close > bracket)
                {
                    int.TryParse(name.Substring(bracket + 1, close - bracket - 1), out index);
                    name = name.Substring(0, bracket);
                }
            }

            if (!string.IsNullOrEmpty(name))
            {
                current = GetPathMemberValue(typeName, current, name);
            }

            if (index >= 0)
            {
                current = GetIndexedValue(current, index);
            }

            return current;
        }

        private static object GetPathMemberValue(string typeName, object current, string memberName)
        {
            if (current == null)
            {
                return null;
            }

            if (string.Equals(memberName, "length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(memberName, "count", StringComparison.OrdinalIgnoreCase))
            {
                var count = TryGetCollectionCount(current);
                if (count >= 0)
                {
                    return count;
                }
            }

            if (current is Type staticType)
            {
                return GetMemberValue(typeName, staticType, null, memberName, AllStatic);
            }

            return GetMemberValue(typeName, current.GetType(), current, memberName, AllInstance);
        }

        private static object GetMemberValue(string typeName, Type type, object target, string memberName, BindingFlags flags)
        {
            var property = type.GetProperty(memberName, flags);
            if (property != null)
            {
                return property.GetValue(target, null);
            }

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                return field.GetValue(target);
            }

            RecordMiss(typeName, memberName, target == null ? "static_path_member_not_found" : "instance_path_member_not_found");
            throw new MissingMemberException(type.FullName, memberName);
        }

        private static object GetIndexedValue(object current, int index)
        {
            if (current == null)
            {
                return null;
            }

            if (current is Array array)
            {
                return index >= 0 && index < array.Length ? array.GetValue(index) : null;
            }

            if (current is IList list)
            {
                return index >= 0 && index < list.Count ? list[index] : null;
            }

            var itemProperty = current.GetType().GetProperty("Item", AllInstance, null, null, new[] { typeof(int) }, null);
            if (itemProperty != null)
            {
                return itemProperty.GetValue(current, new object[] { index });
            }

            return null;
        }

        private static int TryGetCollectionCount(object current)
        {
            if (current is Array array)
            {
                return array.Length;
            }

            if (current is ICollection collection)
            {
                return collection.Count;
            }

            var type = current.GetType();
            var lengthProperty = type.GetProperty("Length", AllInstance);
            if (lengthProperty != null && lengthProperty.PropertyType == typeof(int))
            {
                return (int)lengthProperty.GetValue(current, null);
            }

            var countProperty = type.GetProperty("Count", AllInstance);
            if (countProperty != null && countProperty.PropertyType == typeof(int))
            {
                return (int)countProperty.GetValue(current, null);
            }

            return -1;
        }

        private static MethodInfo FindMethod(Type type, string methodName, JsonArg[] args, BindingFlags flags)
        {
            return type.GetMethods(flags)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
                .Where(m => m.GetParameters().Length == args.Length)
                .OrderByDescending(m => ScoreMethod(m, args))
                .FirstOrDefault();
        }

        private static int ScoreMethod(MethodInfo method, JsonArg[] args)
        {
            var score = 0;
            var parameters = method.GetParameters();
            for (var i = 0; i < parameters.Length; i++)
            {
                var target = parameters[i].ParameterType;
                var arg = args[i];
                if (arg == null || arg.kind == "null")
                {
                    score += target.IsValueType ? 0 : 2;
                }
                else if (target == typeof(string) && arg.kind == "string")
                {
                    score += 3;
                }
                else if (target == typeof(bool) && arg.kind == "bool")
                {
                    score += 3;
                }
                else if (target.IsPrimitive && arg.kind == "number")
                {
                    score += 2;
                }
                else
                {
                    score += 1;
                }
            }

            return score;
        }

        private static object[] ConvertArguments(ParameterInfo[] parameters, JsonArg[] args)
        {
            var converted = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                converted[i] = ConvertArg(args[i], parameters[i].ParameterType);
            }

            return converted;
        }

        private static object ConvertArg(JsonArg arg, Type targetType)
        {
            if (arg == null || arg.kind == "null")
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            if (targetType == typeof(string))
            {
                return arg.stringValue;
            }

            if (targetType == typeof(bool))
            {
                return arg.boolValue;
            }

            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, arg.stringValue, true);
            }

            if (targetType == typeof(Vector2))
            {
                return new Vector2(arg.x, arg.y);
            }

            if (targetType == typeof(Vector3))
            {
                return new Vector3(arg.x, arg.y, arg.z);
            }

            if (targetType == typeof(Color))
            {
                return new Color(arg.r, arg.g, arg.b, arg.a == 0f ? 1f : arg.a);
            }

            if (targetType == typeof(int))
            {
                return (int)arg.numberValue;
            }

            if (targetType == typeof(float))
            {
                return (float)arg.numberValue;
            }

            if (targetType == typeof(double))
            {
                return arg.numberValue;
            }

            if (targetType == typeof(long))
            {
                return (long)arg.numberValue;
            }

            return Convert.ChangeType(arg.stringValue, targetType, CultureInfo.InvariantCulture);
        }

        private static JsonArg[] ParseArgs(string argsJson)
        {
            if (string.IsNullOrEmpty(argsJson))
            {
                return new JsonArg[0];
            }

            var list = UnityJson.FromJson<JsonArgList>(argsJson);
            return list == null || list.items == null ? new JsonArg[0] : list.items;
        }

        private static JsonArg ParseSingleArg(string valueJson)
        {
            if (string.IsNullOrEmpty(valueJson))
            {
                return new JsonArg { kind = "null" };
            }

            try
            {
                return UnityJson.FromJson<JsonArg>(valueJson);
            }
            catch
            {
                return new JsonArg { kind = "string", stringValue = valueJson };
            }
        }

        private static string SerializeResult(object value)
        {
            var result = new ReflectionResult();
            if (value == null)
            {
                result.kind = "null";
                return UnityJson.ToJson(result);
            }

            if (value is bool boolValue)
            {
                result.kind = "bool";
                result.boolValue = boolValue;
                return UnityJson.ToJson(result);
            }

            if (value is string stringValue)
            {
                result.kind = "string";
                result.stringValue = stringValue;
                return UnityJson.ToJson(result);
            }

            if (value is int || value is float || value is double || value is long)
            {
                result.kind = "number";
                result.numberValue = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return UnityJson.ToJson(result);
            }

            if (value is UnityEngine.Object unityObject)
            {
                result.kind = "unityObject";
                result.objectType = unityObject.GetType().FullName;
                result.name = unityObject.name;
                result.instanceId = unityObject.GetInstanceID();
                return UnityJson.ToJson(result);
            }

            if (value is Vector2 v2)
            {
                result.kind = "vector2";
                result.x = v2.x;
                result.y = v2.y;
                return UnityJson.ToJson(result);
            }

            if (value is Vector3 v3)
            {
                result.kind = "vector3";
                result.x = v3.x;
                result.y = v3.y;
                result.z = v3.z;
                return UnityJson.ToJson(result);
            }

            if (value is Color color)
            {
                result.kind = "color";
                result.r = color.r;
                result.g = color.g;
                result.b = color.b;
                result.a = color.a;
                return UnityJson.ToJson(result);
            }

            result.kind = "object";
            result.objectType = value.GetType().FullName;
            result.stringValue = value.ToString();
            return UnityJson.ToJson(result);
        }

        private static void RecordMiss(string typeName, string memberName, string reason)
        {
            if (!EnableAotMissLog)
            {
                return;
            }

            var miss = UnityJson.ToJson(new AotMissRecord
            {
                utc = DateTime.UtcNow.ToString("o"),
                type = typeName,
                member = memberName,
                reason = reason
            });
            AtomicFile.AppendLine(UnityMcpPaths.AotMissesPath(), miss);
        }

        [Serializable]
        private sealed class JsonArgList
        {
            public JsonArg[] items = new JsonArg[0];
        }

        [Serializable]
        private sealed class JsonArg
        {
            public string kind;
            public string stringValue;
            public double numberValue;
            public bool boolValue;
            public float x;
            public float y;
            public float z;
            public float r;
            public float g;
            public float b;
            public float a;
        }

        [Serializable]
        private sealed class ReflectionResult
        {
            public string kind;
            public string stringValue;
            public double numberValue;
            public bool boolValue;
            public string objectType;
            public string name;
            public int instanceId;
            public float x;
            public float y;
            public float z;
            public float r;
            public float g;
            public float b;
            public float a;
        }

        [Serializable]
        private sealed class AotMissRecord
        {
            public string utc;
            public string type;
            public string member;
            public string reason;
        }
    }
}
