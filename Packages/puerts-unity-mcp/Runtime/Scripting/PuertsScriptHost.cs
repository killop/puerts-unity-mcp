#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using Puerts;
using UnityEngine;

namespace PuertsUnityMcp
{
    public sealed class PuertsScriptHost : IDisposable
    {
        private readonly string hostName;
        private readonly ReflectionGateway reflectionGateway;
        private ScriptEnv scriptEnv;
        private bool initialized;

        public PuertsScriptHost(string hostName)
        {
            this.hostName = string.IsNullOrEmpty(hostName) ? "default" : hostName;
            reflectionGateway = new ReflectionGateway();
        }

        public bool IsInitialized => initialized && scriptEnv != null;

        public void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            if (!PreloadAndroidNativeLibraries())
            {
                throw new DllNotFoundException("Android PuerTS native libraries are not available. Rebuild the player after running add-pum-to-build.mjs.");
            }

            scriptEnv = new ScriptEnv(new BackendV8());
            InstallGlobals();
            initialized = true;
        }

        public string Eval(string code, string chunkName = null, bool wrapReturn = true)
        {
            EnsureInitialized();
            var safeChunkName = string.IsNullOrEmpty(chunkName) ? "mcp://" + hostName + "/eval.js" : chunkName;
            var script = wrapReturn ? BuildWrappedScript(code) : code;
            var json = scriptEnv.Eval<string>(script, safeChunkName);
            return string.IsNullOrEmpty(json) ? "{\"kind\":\"null\"}" : json;
        }

        public void Execute(string code, string chunkName = null)
        {
            EnsureInitialized();
            var safeChunkName = string.IsNullOrEmpty(chunkName) ? "mcp://" + hostName + "/script.js" : chunkName;
            scriptEnv.Eval(code ?? string.Empty, safeChunkName);
        }

        public T EvalRaw<T>(string code, string chunkName = null)
        {
            EnsureInitialized();
            var safeChunkName = string.IsNullOrEmpty(chunkName) ? "mcp://" + hostName + "/raw.js" : chunkName;
            return scriptEnv.Eval<T>(code ?? string.Empty, safeChunkName);
        }

        public void SetGlobal(string name, object value)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Global name is required.", nameof(name));
            }

            EnsureInitialized();
            scriptEnv.Eval<Action<string, object>>(@"
                (function(name, value) {
                    globalThis[name] = value;
                })
            ")(name, value);
        }

        public string ExecuteModuleFunctionJson(string modulePath, string functionName, string argsJson, string contextJson)
        {
            if (string.IsNullOrEmpty(modulePath))
            {
                throw new ArgumentException("Module path is required.", nameof(modulePath));
            }

            EnsureInitialized();
            var module = scriptEnv.ExecuteModule(modulePath);
            var safeFunctionName = string.IsNullOrEmpty(functionName) ? "execute" : functionName;
            var execute = module.Get<Func<string, string, string>>(safeFunctionName);
            if (execute == null)
            {
                throw new MissingMethodException(modulePath, safeFunctionName);
            }

            var result = execute(argsJson ?? "{}", contextJson ?? "{}");
            return string.IsNullOrEmpty(result) ? "{}" : result;
        }

        public void Tick()
        {
            if (scriptEnv == null)
            {
                return;
            }

            try
            {
                scriptEnv.Tick();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityMCP] PuerTS tick failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            initialized = false;
            if (scriptEnv != null)
            {
                try
                {
                    scriptEnv.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[UnityMCP] PuerTS dispose failed: " + ex.Message);
                }

                scriptEnv = null;
            }
        }

        private void InstallGlobals()
        {
            scriptEnv.Eval<Action<ReflectionGateway, string, string>>(@"
                (function(reflection, hostName, version) {
                    function pack(value) {
                        if (value === null || typeof value === 'undefined') return { kind: 'null' };
                        if (typeof value === 'string') return { kind: 'string', stringValue: value };
                        if (typeof value === 'number') return { kind: 'number', numberValue: value };
                        if (typeof value === 'boolean') return { kind: 'bool', boolValue: value };
                        if (typeof value === 'object') {
                            var packed = {
                                kind: 'object',
                                stringValue: String(value),
                                x: Number(value.x || 0),
                                y: Number(value.y || 0),
                                z: Number(value.z || 0),
                                r: Number(value.r || 0),
                                g: Number(value.g || 0),
                                b: Number(value.b || 0),
                                a: Number(value.a || 0)
                            };
                            return packed;
                        }
                        return { kind: 'string', stringValue: String(value) };
                    }
                    function unpack(json) {
                        var value = JSON.parse(json);
                        switch (value.kind) {
                            case 'null': return null;
                            case 'string': return value.stringValue;
                            case 'number': return value.numberValue;
                            case 'bool': return value.boolValue;
                            default: return value;
                        }
                    }
                    globalThis.__unity_mcp = {
                        hostName: hostName,
                        version: version,
                        reflection: reflection,
                        invokeStatic: function(typeName, methodName) {
                            var args = Array.prototype.slice.call(arguments, 2).map(pack);
                            return unpack(reflection.InvokeStaticJson(typeName, methodName, JSON.stringify({ items: args })));
                        },
                        getStatic: function(typeName, memberName) {
                            return unpack(reflection.GetStaticJson(typeName, memberName));
                        },
                        getStaticPath: function(typeName, memberPath) {
                            return unpack(reflection.GetStaticPathJson(typeName, memberPath));
                        },
                        setStatic: function(typeName, memberName, value) {
                            reflection.SetStaticJson(typeName, memberName, JSON.stringify(pack(value)));
                        },
                        typeExists: function(typeName) {
                            return unpack(reflection.TypeExists(typeName));
                        }
                    };
                })
            ")(reflectionGateway, hostName, UnityMcpConstants.Version);
        }

        private static string BuildWrappedScript(string code)
        {
            var escaped = EscapeJavaScriptString(code ?? string.Empty);
            return "(function() {"
                + "var __code = \"" + escaped + "\";"
                + "var __result = (new Function('__unity_mcp', __code))(globalThis.__unity_mcp);"
                + "if (typeof __result === 'undefined') { return JSON.stringify({ kind: 'undefined' }); }"
                + "return JSON.stringify({ kind: typeof __result, value: __result });"
                + "})()";
        }

        private static string EscapeJavaScriptString(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool androidNativeLibrariesPreloaded;

        private static bool PreloadAndroidNativeLibraries()
        {
            if (androidNativeLibrariesPreloaded)
            {
                return true;
            }

            if (!TryLoadAndroidNativeLibrary("PuertsCore") || !TryLoadAndroidNativeLibrary("PapiV8"))
            {
                return false;
            }

            TryLoadAndroidNativeLibrary("WSPPAddon");
            androidNativeLibrariesPreloaded = true;
            return true;
        }

        private static bool TryLoadAndroidNativeLibrary(string libraryName)
        {
            try
            {
                using (var system = new AndroidJavaClass("java.lang.System"))
                {
                    system.CallStatic("loadLibrary", libraryName);
                }

                Debug.Log("[UnityMCP] Loaded Android native library: " + libraryName);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityMCP] Failed to load Android native library " + libraryName + ": " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
#else
        private static bool PreloadAndroidNativeLibraries()
        {
            return true;
        }
#endif
    }
}
#else
using System;

namespace PuertsUnityMcp
{
    public sealed class PuertsScriptHost : IDisposable
    {
        public PuertsScriptHost(string hostName)
        {
        }

        public bool IsInitialized => false;

        public void EnsureInitialized()
        {
            throw new NotSupportedException("PuerTS V8 is not supported on WebGL Player.");
        }

        public string Eval(string code, string chunkName = null, bool wrapReturn = true)
        {
            throw new NotSupportedException("PuerTS V8 is not supported on WebGL Player.");
        }

        public void Execute(string code, string chunkName = null)
        {
            throw new NotSupportedException("PuerTS V8 is not supported on WebGL Player.");
        }

        public T EvalRaw<T>(string code, string chunkName = null)
        {
            throw new NotSupportedException("PuerTS V8 is not supported on WebGL Player.");
        }

        public void SetGlobal(string name, object value)
        {
            throw new NotSupportedException("PuerTS V8 is not supported on WebGL Player.");
        }

        public string ExecuteModuleFunctionJson(string modulePath, string functionName, string argsJson, string contextJson)
        {
            throw new NotSupportedException("PuerTS V8 is not supported on WebGL Player.");
        }

        public void Tick()
        {
        }

        public void Dispose()
        {
        }
    }
}
#endif
