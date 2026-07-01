using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace PuertsUnityMcp.Editor
{
    internal sealed partial class UnityMcpEditorEndpoint
    {
        private const string HierarchyResultsDirectoryName = "hierarchy-results";
        private const string EditorWindowScreenshotsDirectoryName = "editor-window-screenshots";

        private void RegisterEditorSceneAndWindowTools()
        {
            var hierarchySchema = JsonSchemas.Object(
                JsonSchemas.BooleanProperty("UseSelection", "uLoop-compatible alias. When true, selected GameObject roots are exported and child selections under selected parents are deduplicated."),
                JsonSchemas.BooleanProperty("useSelection", "When true, selected GameObject roots are exported."),
                JsonSchemas.StringProperty("RootPath", "uLoop-compatible root GameObject path. Ignored when UseSelection is true."),
                JsonSchemas.StringProperty("rootPath", "Root GameObject path. Empty exports all loaded scene roots."),
                JsonSchemas.BooleanProperty("IncludeInactive", "Whether to include inactive GameObjects. Defaults to true."),
                JsonSchemas.BooleanProperty("includeInactive", "Whether to include inactive GameObjects. Defaults to true."),
                JsonSchemas.NumberProperty("MaxDepth", "Maximum hierarchy depth; -1 means unlimited. Defaults to -1."),
                JsonSchemas.NumberProperty("maxDepth", "Maximum hierarchy depth; -1 means unlimited."),
                JsonSchemas.BooleanProperty("IncludeComponents", "Whether to include component type names. Defaults to true."),
                JsonSchemas.BooleanProperty("includeComponents", "Whether to include component type names."),
                JsonSchemas.BooleanProperty("IncludePaths", "Whether to include transform paths for every node."),
                JsonSchemas.BooleanProperty("includePaths", "Whether to include transform paths for every node."),
                JsonSchemas.StringProperty("UseComponentsLut", "uLoop-compatible component LUT option: auto, true, or false. The exported file records the requested value."),
                JsonSchemas.StringProperty("useComponentsLut", "Component LUT option: auto, true, or false."));

            tools.Register(new DelegateUnityMcpTool("editor.hierarchy.get", "Export Unity scene hierarchy to .puerts-unity-mcp/hierarchy-results and return only file paths plus summary. Supports uLoop-style UseSelection, RootPath, IncludeInactive, MaxDepth, IncludeComponents, and IncludePaths.", hierarchySchema, (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(ExportHierarchy(args), true))));

            tools.Register(new DelegateUnityMcpTool("get-hierarchy", "uLoop-compatible alias for editor.hierarchy.get. Exports hierarchy JSON to .puerts-unity-mcp/hierarchy-results instead of returning a huge payload.", hierarchySchema, (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(ExportHierarchy(args), true))));

            var focusSchema = JsonSchemas.Object();
            tools.Register(new DelegateUnityMcpTool("editor.window.focus", "Bring the Unity Editor process/window to the foreground. Windows and macOS are supported; falls back to focusing the current EditorWindow.", focusSchema, (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(FocusUnityEditorWindow(), true))));

            tools.Register(new DelegateUnityMcpTool("focus-window", "uLoop-compatible alias for editor.window.focus.", focusSchema, (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(FocusUnityEditorWindow(), true))));

            var screenshotSchema = JsonSchemas.Object(
                JsonSchemas.StringProperty("WindowName", "uLoop-compatible EditorWindow title, for example Game, Scene, Console, Inspector, Project, or Hierarchy."),
                JsonSchemas.StringProperty("windowName", "EditorWindow title, for example Game, Scene, Console, Inspector, Project, or Hierarchy."),
                JsonSchemas.StringProperty("MatchMode", "exact, prefix, or contains. Defaults to exact."),
                JsonSchemas.StringProperty("matchMode", "exact, prefix, or contains. Defaults to exact."),
                JsonSchemas.NumberProperty("ResolutionScale", "PNG scale from 0.1 to 1.0. Defaults to 1.0."),
                JsonSchemas.NumberProperty("resolutionScale", "PNG scale from 0.1 to 1.0. Defaults to 1.0."),
                JsonSchemas.StringProperty("OutputDirectory", "Optional absolute output directory. Defaults to .puerts-unity-mcp/editor-window-screenshots."),
                JsonSchemas.StringProperty("outputDirectory", "Optional absolute output directory."),
                JsonSchemas.StringProperty("CaptureMode", "window is supported. screen.screenshot remains the Player/phone screenshot tool."),
                JsonSchemas.StringProperty("captureMode", "window is supported. screen.screenshot remains the Player/phone screenshot tool."));

            tools.Register(new DelegateUnityMcpTool("editor.window.screenshot", "Capture one or more Unity EditorWindow tabs as PNG. This is Editor-only and does not conflict with Runtime screen.screenshot, which captures Player/phone screens.", screenshotSchema, async (ctx, args) =>
                UnityJson.ToJson(await CaptureEditorWindowScreenshot(args), true)));

            tools.Register(new DelegateUnityMcpTool("screenshot", "uLoop-compatible Editor MCP alias for editor.window.screenshot. Use screen.screenshot for Runtime/phone Player screenshots.", screenshotSchema, async (ctx, args) =>
                UnityJson.ToJson(await CaptureEditorWindowScreenshot(args), true)));
        }

        private static HierarchyExportResult ExportHierarchy(UnityMcpToolArguments args)
        {
            var options = ResolveHierarchyOptions(args);
            var roots = options.useSelection ? GetSelectedHierarchyRoots() : GetHierarchyRoots(options.rootPath);
            var sceneGroups = BuildHierarchyGroups(roots, options);
            var context = BuildHierarchyContext(sceneGroups, options);
            var exportData = new HierarchyExportData
            {
                exportTimestampUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                context = context,
                hierarchy = sceneGroups
            };

            var outputDirectory = Path.Combine(UnityMcpPaths.StateRoot, HierarchyResultsDirectoryName);
            Directory.CreateDirectory(outputDirectory);
            var filePath = Path.Combine(outputDirectory, "hierarchy_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".json");
            AtomicFile.WriteJson(filePath, exportData, true);

            return new HierarchyExportResult
            {
                action = "editor.hierarchy.get",
                success = true,
                hierarchyFilePath = filePath,
                message = "Hierarchy data saved to hierarchyFilePath. Open the JSON to inspect context and hierarchy.",
                context = context,
                rootCount = CountRoots(sceneGroups),
                nodeCount = context.nodeCount,
                options = options
            };
        }

        private static HierarchyOptions ResolveHierarchyOptions(UnityMcpToolArguments args)
        {
            return new HierarchyOptions
            {
                includeInactive = ReadBoolArg(args, "IncludeInactive", "includeInactive", args == null || args.includeInactive),
                maxDepth = ReadIntArg(args, "MaxDepth", "maxDepth", args == null || args.maxDepth == 0 ? -1 : args.maxDepth),
                rootPath = ReadStringArg(args, "RootPath", "rootPath", args == null ? null : args.rootPath),
                includeComponents = ReadBoolArg(args, "IncludeComponents", "includeComponents", args == null || args.includeComponents),
                includePaths = ReadBoolArg(args, "IncludePaths", "includePaths", args != null && args.includePaths),
                useComponentsLut = ReadStringArg(args, "UseComponentsLut", "useComponentsLut", args == null ? null : args.useComponentsLut) ?? "auto",
                useSelection = ReadBoolArg(args, "UseSelection", "useSelection", args != null && args.useSelection)
            };
        }

        private static SceneHierarchyGroup[] BuildHierarchyGroups(GameObject[] roots, HierarchyOptions options)
        {
            var groupsByScene = new Dictionary<string, List<HierarchyNodeNested>>(StringComparer.Ordinal);
            if (roots != null)
            {
                for (var i = 0; i < roots.Length; i++)
                {
                    var root = roots[i];
                    if (root == null)
                    {
                        continue;
                    }

                    if (!options.includeInactive && !root.activeInHierarchy)
                    {
                        continue;
                    }

                    var node = BuildHierarchyNode(root, 0, options, string.Empty);
                    if (node == null)
                    {
                        continue;
                    }

                    var sceneName = ResolveSceneName(root);
                    if (!groupsByScene.TryGetValue(sceneName, out var sceneRoots))
                    {
                        sceneRoots = new List<HierarchyNodeNested>();
                        groupsByScene.Add(sceneName, sceneRoots);
                    }

                    sceneRoots.Add(node);
                }
            }

            var groups = new List<SceneHierarchyGroup>();
            foreach (var pair in groupsByScene)
            {
                var stats = CalculateHierarchyStats(pair.Value);
                groups.Add(new SceneHierarchyGroup
                {
                    sceneName = pair.Key,
                    stats = stats,
                    roots = pair.Value.ToArray()
                });
            }

            groups.Sort((left, right) => string.CompareOrdinal(left.sceneName, right.sceneName));
            return groups.ToArray();
        }

        private static HierarchyNodeNested BuildHierarchyNode(GameObject go, int depth, HierarchyOptions options, string parentPath)
        {
            if (go == null)
            {
                return null;
            }

            if (options.maxDepth >= 0 && depth > options.maxDepth)
            {
                return null;
            }

            var path = string.IsNullOrEmpty(parentPath) ? go.name : parentPath + "/" + go.name;
            var children = new List<HierarchyNodeNested>();
            var transform = go.transform;
            for (var i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                if (child == null || child.gameObject == null)
                {
                    continue;
                }

                if (!options.includeInactive && !child.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var childNode = BuildHierarchyNode(child.gameObject, depth + 1, options, path);
                if (childNode != null)
                {
                    children.Add(childNode);
                }
            }

            return new HierarchyNodeNested
            {
                id = go.GetInstanceID().ToString(CultureInfo.InvariantCulture),
                name = go.name,
                path = options.includePaths ? path : null,
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                tag = SafeTag(go),
                layer = go.layer,
                depth = depth,
                siblingIndex = transform.GetSiblingIndex(),
                sceneName = ResolveSceneName(go),
                components = options.includeComponents ? GetComponentNames(go) : new string[0],
                childCount = children.Count,
                children = children.ToArray()
            };
        }

        private static HierarchyContext BuildHierarchyContext(SceneHierarchyGroup[] groups, HierarchyOptions options)
        {
            var nodeCount = 0;
            var maxDepth = 0;
            var rootCount = 0;
            var sceneNames = new List<string>();
            for (var i = 0; groups != null && i < groups.Length; i++)
            {
                var group = groups[i];
                if (group == null)
                {
                    continue;
                }

                sceneNames.Add(group.sceneName);
                if (group.stats != null)
                {
                    rootCount += group.stats.rootCount;
                    nodeCount += group.stats.nodeCount;
                    maxDepth = Math.Max(maxDepth, group.stats.maxDepth);
                }
            }

            return new HierarchyContext
            {
                sceneType = ResolveHierarchySceneType(),
                sceneName = sceneNames.Count == 0 ? string.Empty : string.Join(", ", sceneNames.ToArray()),
                nodeCount = nodeCount,
                rootCount = rootCount,
                maxDepth = maxDepth,
                useSelection = options.useSelection,
                rootPath = options.rootPath
            };
        }

        private static HierarchyStats CalculateHierarchyStats(List<HierarchyNodeNested> roots)
        {
            var stats = new HierarchyStats
            {
                rootCount = roots == null ? 0 : roots.Count
            };

            for (var i = 0; roots != null && i < roots.Count; i++)
            {
                AccumulateHierarchyStats(roots[i], stats);
            }

            return stats;
        }

        private static void AccumulateHierarchyStats(HierarchyNodeNested node, HierarchyStats stats)
        {
            if (node == null || stats == null)
            {
                return;
            }

            stats.nodeCount++;
            stats.maxDepth = Math.Max(stats.maxDepth, node.depth);
            var children = node.children;
            for (var i = 0; children != null && i < children.Length; i++)
            {
                AccumulateHierarchyStats(children[i], stats);
            }
        }

        private static int CountRoots(SceneHierarchyGroup[] groups)
        {
            var count = 0;
            for (var i = 0; groups != null && i < groups.Length; i++)
            {
                count += groups[i] == null || groups[i].roots == null ? 0 : groups[i].roots.Length;
            }

            return count;
        }

        private static GameObject[] GetSelectedHierarchyRoots()
        {
            var selected = Selection.gameObjects;
            if (selected == null || selected.Length == 0)
            {
                return new GameObject[0];
            }

            var roots = new List<GameObject>();
            for (var i = 0; i < selected.Length; i++)
            {
                var candidate = selected[i];
                if (candidate == null)
                {
                    continue;
                }

                var isDescendant = false;
                for (var j = 0; j < selected.Length; j++)
                {
                    var other = selected[j];
                    if (other == null || other == candidate)
                    {
                        continue;
                    }

                    if (candidate.transform.IsChildOf(other.transform))
                    {
                        isDescendant = true;
                        break;
                    }
                }

                if (!isDescendant)
                {
                    roots.Add(candidate);
                }
            }

            return roots.ToArray();
        }

        private static GameObject[] GetHierarchyRoots(string rootPath)
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null && prefabStage.prefabContentsRoot != null)
            {
                return FilterRootsByPath(new[] { prefabStage.prefabContentsRoot }, rootPath);
            }

            var roots = new List<GameObject>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    continue;
                }

                roots.AddRange(scene.GetRootGameObjects());
            }

            roots.AddRange(GetDontDestroyOnLoadRoots());
            return FilterRootsByPath(roots.ToArray(), rootPath);
        }

        private static GameObject[] FilterRootsByPath(GameObject[] roots, string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return roots ?? new GameObject[0];
            }

            rootPath = rootPath.Trim().Trim('/');
            var matches = new List<GameObject>();
            for (var i = 0; roots != null && i < roots.Length; i++)
            {
                var root = roots[i];
                if (root == null)
                {
                    continue;
                }

                var relativePath = NormalizeRootRelativePath(rootPath, root.name);
                if (string.IsNullOrEmpty(relativePath))
                {
                    matches.Add(root);
                    continue;
                }

                var found = root.transform.Find(relativePath);
                if (found != null)
                {
                    matches.Add(found.gameObject);
                }
            }

            return matches.ToArray();
        }

        private static string NormalizeRootRelativePath(string rootPath, string rootName)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                return string.Empty;
            }

            if (string.Equals(rootPath, rootName, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return rootPath.StartsWith(rootName + "/", StringComparison.Ordinal)
                ? rootPath.Substring(rootName.Length + 1)
                : rootPath;
        }

        private static GameObject[] GetDontDestroyOnLoadRoots()
        {
            if (!Application.isPlaying)
            {
                return new GameObject[0];
            }

            GameObject probe = null;
            try
            {
                probe = new GameObject("__pum_ddol_probe__");
                UnityEngine.Object.DontDestroyOnLoad(probe);
                var scene = probe.scene;
                if (!scene.IsValid())
                {
                    return new GameObject[0];
                }

                var roots = scene.GetRootGameObjects();
                var result = new List<GameObject>();
                for (var i = 0; roots != null && i < roots.Length; i++)
                {
                    if (roots[i] != null && roots[i] != probe)
                    {
                        result.Add(roots[i]);
                    }
                }

                return result.ToArray();
            }
            finally
            {
                if (probe != null)
                {
                    if (Application.isPlaying)
                    {
                        UnityEngine.Object.Destroy(probe);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(probe);
                    }
                }
            }
        }

        private static string[] GetComponentNames(GameObject go)
        {
            var components = go.GetComponents<Component>();
            var names = new string[components == null ? 0 : components.Length];
            for (var i = 0; i < names.Length; i++)
            {
                names[i] = components[i] == null ? "MissingScript" : components[i].GetType().Name;
            }

            return names;
        }

        private static string ResolveSceneName(GameObject go)
        {
            if (go == null)
            {
                return string.Empty;
            }

            var scene = go.scene;
            if (!scene.IsValid())
            {
                return "Invalid";
            }

            return string.IsNullOrEmpty(scene.name) ? scene.path : scene.name;
        }

        private static string ResolveHierarchySceneType()
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                return "prefab";
            }

            return Application.isPlaying ? "runtime" : "editor";
        }

        private static string SafeTag(GameObject go)
        {
            try
            {
                return go == null ? null : go.tag;
            }
            catch
            {
                return null;
            }
        }

        private static EditorWindowFocusResult FocusUnityEditorWindow()
        {
            var result = new EditorWindowFocusResult
            {
                action = "editor.window.focus",
                platform = Application.platform.ToString(),
                processId = Process.GetCurrentProcess().Id
            };

            try
            {
#if UNITY_EDITOR_WIN
                var handle = Process.GetCurrentProcess().MainWindowHandle;
                result.nativeHandle = handle.ToInt64().ToString(CultureInfo.InvariantCulture);
                if (handle != IntPtr.Zero)
                {
                    result.success = FocusNativeWindow(handle);
                    if (result.success)
                    {
                        result.message = "Unity Editor window focused.";
                        return result;
                    }

                    result.message = "Native window focus returned false; used EditorWindow fallback if available.";
                }
                else
                {
                    result.message = "Unity process main window handle was zero; used EditorWindow fallback.";
                }
#elif UNITY_EDITOR_OSX
                var script = "tell application \"System Events\" to set frontmost of first process whose unix id is " + result.processId + " to true";
                var startInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    Arguments = "-e " + QuoteShellArgument(script),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        process.WaitForExit(2000);
                        result.success = process.ExitCode == 0;
                        result.message = result.success ? "Unity Editor window focused." : "osascript returned " + process.ExitCode + ".";
                        return result;
                    }
                }
#endif
                var focused = EditorWindow.focusedWindow;
                if (focused != null)
                {
                    focused.Show();
                    focused.Focus();
                    result.success = true;
                    result.message = "Focused the current Unity EditorWindow as a fallback.";
                    return result;
                }

                result.success = false;
                result.message = string.IsNullOrEmpty(result.message) ? "No focus strategy succeeded on this platform." : result.message;
                return result;
            }
            catch (Exception ex)
            {
                result.success = false;
                result.message = ex.GetType().Name + ": " + ex.Message;
                return result;
            }
        }

        private static async Task<EditorWindowScreenshotResult> CaptureEditorWindowScreenshot(UnityMcpToolArguments args)
        {
            var request = ResolveEditorWindowScreenshotRequest(args);
            if (!string.Equals(request.captureMode, "window", StringComparison.OrdinalIgnoreCase))
            {
                return new EditorWindowScreenshotResult
                {
                    action = "editor.window.screenshot",
                    success = false,
                    screenshotCount = 0,
                    error = "Only CaptureMode=window is supported by editor.window.screenshot. Use Runtime screen.screenshot for Player/phone screen capture.",
                    request = request,
                    screenshots = new EditorWindowScreenshotInfo[0],
                    openWindowNames = GetOpenEditorWindowNames()
                };
            }

            var windows = FindEditorWindowsByName(request.windowName, request.matchMode);
            if (windows.Length == 0)
            {
                return new EditorWindowScreenshotResult
                {
                    action = "editor.window.screenshot",
                    success = false,
                    screenshotCount = 0,
                    error = "Window not found: " + request.windowName + " (matchMode=" + request.matchMode + ")",
                    request = request,
                    screenshots = new EditorWindowScreenshotInfo[0],
                    openWindowNames = GetOpenEditorWindowNames()
                };
            }

            Directory.CreateDirectory(request.outputDirectory);
            var screenshots = new List<EditorWindowScreenshotInfo>();
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var safeWindowName = SanitizeFileName(request.windowName);
            for (var i = 0; i < windows.Length; i++)
            {
                var window = windows[i];
                Texture2D texture = null;
                try
                {
                    texture = await CaptureEditorWindowTexture(window, request.resolutionScale);
                    if (texture == null)
                    {
                        continue;
                    }

                    var fileName = windows.Length == 1
                        ? safeWindowName + "_" + timestamp + ".png"
                        : safeWindowName + "_" + (i + 1).ToString(CultureInfo.InvariantCulture) + "_" + timestamp + ".png";
                    var path = Path.Combine(request.outputDirectory, fileName);
                    var bytes = texture.EncodeToPNG();
                    File.WriteAllBytes(path, bytes);
                    screenshots.Add(new EditorWindowScreenshotInfo
                    {
                        imagePath = path,
                        windowTitle = window.titleContent == null ? string.Empty : window.titleContent.text,
                        fileSizeBytes = bytes == null ? 0 : bytes.Length,
                        width = texture.width,
                        height = texture.height,
                        coordinateSystem = "editorWindow",
                        resolutionScale = request.resolutionScale
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[UnityMCP] EditorWindow screenshot failed: " + ex.Message);
                }
                finally
                {
                    if (texture != null)
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                }
            }

            return new EditorWindowScreenshotResult
            {
                action = "editor.window.screenshot",
                success = screenshots.Count > 0,
                screenshotCount = screenshots.Count,
                error = screenshots.Count == 0 ? "Matching windows were found, but capture failed." : null,
                request = request,
                screenshots = screenshots.ToArray(),
                openWindowNames = screenshots.Count == 0 ? GetOpenEditorWindowNames() : new string[0]
            };
        }

        private static EditorWindowScreenshotRequest ResolveEditorWindowScreenshotRequest(UnityMcpToolArguments args)
        {
            var windowName = ReadStringArg(args, "WindowName", "windowName", args == null ? null : args.windowName);
            var outputDirectory = ReadStringArg(args, "OutputDirectory", "outputDirectory", args == null ? null : args.outputDirectory);
            var resolutionScale = ReadFloatArg(args, "ResolutionScale", "resolutionScale", args == null || args.resolutionScale <= 0f ? 1f : args.resolutionScale);
            resolutionScale = Mathf.Clamp(resolutionScale, 0.1f, 1f);
            return new EditorWindowScreenshotRequest
            {
                windowName = string.IsNullOrWhiteSpace(windowName) ? "Game" : windowName.Trim(),
                matchMode = NormalizeMatchMode(ReadStringArg(args, "MatchMode", "matchMode", args == null ? null : args.matchMode)),
                outputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
                    ? Path.Combine(UnityMcpPaths.StateRoot, EditorWindowScreenshotsDirectoryName)
                    : Path.GetFullPath(outputDirectory),
                captureMode = NormalizeCaptureMode(ReadStringArg(args, "CaptureMode", "captureMode", args == null ? null : args.captureMode)),
                resolutionScale = resolutionScale
            };
        }

        private static async Task<Texture2D> CaptureEditorWindowTexture(EditorWindow window, float resolutionScale)
        {
            if (window == null)
            {
                return null;
            }

            window.ShowTab();
            window.Repaint();
            await WaitEditorFrames(2);

            var scale = EditorGUIUtility.pixelsPerPoint;
            var width = Mathf.RoundToInt(window.position.width * scale);
            var height = Mathf.RoundToInt(window.position.height * scale);
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 24);
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
            {
                descriptor.sRGB = false;
            }

            var renderTexture = RenderTexture.GetTemporary(descriptor);
            var previous = RenderTexture.active;
            try
            {
                if (!TryGrabEditorWindowPixels(window, renderTexture))
                {
                    return null;
                }

                RenderTexture.active = renderTexture;
                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false);
                FlipTextureVertically(texture);
                if (!Mathf.Approximately(resolutionScale, 1f))
                {
                    texture = ScaleTexture(texture, resolutionScale);
                }

                return texture;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static bool TryGrabEditorWindowPixels(EditorWindow window, RenderTexture target)
        {
            try
            {
                var parentField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
                var parent = parentField == null ? null : parentField.GetValue(window);
                if (parent == null)
                {
                    return false;
                }

                var method = parent.GetType().GetMethod("GrabPixels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(RenderTexture), typeof(Rect) }, null);
                if (method == null)
                {
                    return false;
                }

                var scale = EditorGUIUtility.pixelsPerPoint;
                var rect = new Rect(0f, 0f, window.position.width * scale, window.position.height * scale);
                method.Invoke(parent, new object[] { target, rect });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[UnityMCP] GrabPixels failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static void FlipTextureVertically(Texture2D texture)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 1)
            {
                return;
            }

            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetPixels32();
            var flipped = new Color32[pixels.Length];
            for (var y = 0; y < height; y++)
            {
                Array.Copy(pixels, y * width, flipped, (height - y - 1) * width, width);
            }

            texture.SetPixels32(flipped);
            texture.Apply(false);
        }

        private static Texture2D ScaleTexture(Texture2D original, float scale)
        {
            var width = Mathf.Max(1, Mathf.RoundToInt(original.width * scale));
            var height = Mathf.Max(1, Mathf.RoundToInt(original.height * scale));
            var scaled = new Texture2D(width, height, original.format, false);
            var rt = RenderTexture.GetTemporary(width, height);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(original, rt);
                RenderTexture.active = rt;
                scaled.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                scaled.Apply(false);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(original);
            }

            return scaled;
        }

        private static EditorWindow[] FindEditorWindowsByName(string windowName, string matchMode)
        {
            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var matches = new List<EditorWindow>();
            for (var i = 0; allWindows != null && i < allWindows.Length; i++)
            {
                var window = allWindows[i];
                var title = window == null || window.titleContent == null ? null : window.titleContent.text;
                if (string.IsNullOrEmpty(title))
                {
                    continue;
                }

                if (MatchesWindowTitle(title, windowName, matchMode))
                {
                    matches.Add(window);
                }
            }

            return matches.ToArray();
        }

        private static bool MatchesWindowTitle(string title, string windowName, string matchMode)
        {
            if (string.Equals(matchMode, "prefix", StringComparison.OrdinalIgnoreCase))
            {
                return title.StartsWith(windowName, StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(matchMode, "contains", StringComparison.OrdinalIgnoreCase))
            {
                return title.IndexOf(windowName, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return string.Equals(title, windowName, StringComparison.OrdinalIgnoreCase);
        }

        private static string[] GetOpenEditorWindowNames()
        {
            var allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var names = new List<string>();
            for (var i = 0; allWindows != null && i < allWindows.Length; i++)
            {
                var title = allWindows[i] == null || allWindows[i].titleContent == null ? null : allWindows[i].titleContent.text;
                if (!string.IsNullOrEmpty(title) && !ContainsString(names, title))
                {
                    names.Add(title);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        private static Task WaitEditorFrames(int frames)
        {
            var completion = new TaskCompletionSource<bool>();
            var remaining = Math.Max(1, frames);
            EditorApplication.CallbackFunction callback = null;
            callback = () =>
            {
                remaining--;
                if (remaining > 0)
                {
                    return;
                }

                EditorApplication.update -= callback;
                completion.TrySetResult(true);
            };
            EditorApplication.update += callback;
            return completion.Task;
        }

        private static string NormalizeMatchMode(string matchMode)
        {
            if (string.Equals(matchMode, "prefix", StringComparison.OrdinalIgnoreCase)
                || string.Equals(matchMode, "1", StringComparison.OrdinalIgnoreCase))
            {
                return "prefix";
            }

            if (string.Equals(matchMode, "contains", StringComparison.OrdinalIgnoreCase)
                || string.Equals(matchMode, "2", StringComparison.OrdinalIgnoreCase))
            {
                return "contains";
            }

            return "exact";
        }

        private static string NormalizeCaptureMode(string captureMode)
        {
            if (string.Equals(captureMode, "rendering", StringComparison.OrdinalIgnoreCase)
                || string.Equals(captureMode, "1", StringComparison.OrdinalIgnoreCase))
            {
                return "rendering";
            }

            return "window";
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "window";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                var ok = true;
                for (var j = 0; j < invalid.Length; j++)
                {
                    if (ch == invalid[j])
                    {
                        ok = false;
                        break;
                    }
                }

                builder.Append(ok ? ch : '_');
            }

            return builder.ToString();
        }

        private static string ReadStringArg(UnityMcpToolArguments args, string pascalName, string camelName, string fallback)
        {
            var raw = args == null ? null : args.rawArgumentsJson;
            if (UnityMcpJsonArgumentReader.TryGetString(raw, pascalName, out var pascalValue))
            {
                return pascalValue;
            }

            if (UnityMcpJsonArgumentReader.TryGetString(raw, camelName, out var camelValue))
            {
                return camelValue;
            }

            return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
        }

        private static bool ReadBoolArg(UnityMcpToolArguments args, string pascalName, string camelName, bool fallback)
        {
            var raw = args == null ? null : args.rawArgumentsJson;
            if (UnityMcpJsonArgumentReader.TryGetBool(raw, pascalName, out var pascalValue))
            {
                return pascalValue;
            }

            if (UnityMcpJsonArgumentReader.TryGetBool(raw, camelName, out var camelValue))
            {
                return camelValue;
            }

            return fallback;
        }

        private static int ReadIntArg(UnityMcpToolArguments args, string pascalName, string camelName, int fallback)
        {
            var raw = args == null ? null : args.rawArgumentsJson;
            if (UnityMcpJsonArgumentReader.TryGetInt(raw, pascalName, out var pascalValue))
            {
                return pascalValue;
            }

            if (UnityMcpJsonArgumentReader.TryGetInt(raw, camelName, out var camelValue))
            {
                return camelValue;
            }

            return fallback;
        }

        private static float ReadFloatArg(UnityMcpToolArguments args, string pascalName, string camelName, float fallback)
        {
            var raw = args == null ? null : args.rawArgumentsJson;
            if (UnityMcpJsonArgumentReader.TryGetDouble(raw, pascalName, out var pascalValue))
            {
                return (float)pascalValue;
            }

            if (UnityMcpJsonArgumentReader.TryGetDouble(raw, camelName, out var camelValue))
            {
                return (float)camelValue;
            }

            return fallback;
        }

        private static string QuoteShellArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

#if UNITY_EDITOR_WIN
        private static bool FocusNativeWindow(IntPtr handle)
        {
            const int swShow = 5;
            const int swRestore = 9;

            if (IsIconic(handle))
            {
                ShowWindow(handle, swRestore);
            }
            else
            {
                ShowWindow(handle, swShow);
            }

            var currentThreadId = GetCurrentThreadId();
            var foregroundWindow = GetForegroundWindow();
            uint foregroundProcessId;
            var foregroundThreadId = foregroundWindow == IntPtr.Zero
                ? 0u
                : GetWindowThreadProcessId(foregroundWindow, out foregroundProcessId);
            uint targetProcessId;
            var targetThreadId = GetWindowThreadProcessId(handle, out targetProcessId);
            var attachedForeground = false;
            var attachedTarget = false;

            try
            {
                if (foregroundThreadId != 0u && foregroundThreadId != currentThreadId)
                {
                    attachedForeground = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                }

                if (targetThreadId != 0u && targetThreadId != currentThreadId)
                {
                    attachedTarget = AttachThreadInput(currentThreadId, targetThreadId, true);
                }

                BringWindowToTop(handle);
                SetActiveWindow(handle);
                return SetForegroundWindow(handle) || GetForegroundWindow() == handle;
            }
            finally
            {
                if (attachedTarget)
                {
                    AttachThreadInput(currentThreadId, targetThreadId, false);
                }

                if (attachedForeground)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
#endif

        [Serializable]
        private sealed class HierarchyOptions
        {
            public bool includeInactive;
            public int maxDepth;
            public string rootPath;
            public bool includeComponents;
            public bool includePaths;
            public string useComponentsLut;
            public bool useSelection;
        }

        [Serializable]
        private sealed class HierarchyExportData
        {
            public string exportTimestampUtc;
            public HierarchyContext context;
            public SceneHierarchyGroup[] hierarchy;
        }

        [Serializable]
        private sealed class HierarchyContext
        {
            public string sceneType;
            public string sceneName;
            public int rootCount;
            public int nodeCount;
            public int maxDepth;
            public bool useSelection;
            public string rootPath;
        }

        [Serializable]
        private sealed class SceneHierarchyGroup
        {
            public string sceneName;
            public HierarchyStats stats;
            public HierarchyNodeNested[] roots;
        }

        [Serializable]
        private sealed class HierarchyStats
        {
            public int rootCount;
            public int nodeCount;
            public int maxDepth;
        }

        [Serializable]
        private sealed class HierarchyNodeNested
        {
            public string id;
            public string name;
            public string path;
            public bool activeSelf;
            public bool activeInHierarchy;
            public string tag;
            public int layer;
            public int depth;
            public int siblingIndex;
            public string sceneName;
            public string[] components;
            public int childCount;
            public HierarchyNodeNested[] children;
        }

        [Serializable]
        private sealed class HierarchyExportResult
        {
            public string action;
            public bool success;
            public string message;
            public string hierarchyFilePath;
            public int rootCount;
            public int nodeCount;
            public HierarchyContext context;
            public HierarchyOptions options;
        }

        [Serializable]
        private sealed class EditorWindowFocusResult
        {
            public string action;
            public bool success;
            public string message;
            public string platform;
            public int processId;
            public string nativeHandle;
        }

        [Serializable]
        private sealed class EditorWindowScreenshotRequest
        {
            public string windowName;
            public string matchMode;
            public string outputDirectory;
            public string captureMode;
            public float resolutionScale;
        }

        [Serializable]
        private sealed class EditorWindowScreenshotResult
        {
            public string action;
            public bool success;
            public string error;
            public int screenshotCount;
            public EditorWindowScreenshotRequest request;
            public EditorWindowScreenshotInfo[] screenshots;
            public string[] openWindowNames;
        }

        [Serializable]
        private sealed class EditorWindowScreenshotInfo
        {
            public string imagePath;
            public string windowTitle;
            public long fileSizeBytes;
            public int width;
            public int height;
            public string coordinateSystem;
            public float resolutionScale;
        }
    }
}
