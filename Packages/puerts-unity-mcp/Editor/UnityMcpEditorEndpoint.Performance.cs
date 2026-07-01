using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Profiling;

namespace PuertsUnityMcp.Editor
{
    internal sealed partial class UnityMcpEditorEndpoint
    {
        private const int DefaultProfilerCaptureDurationMs = 15000;
        private const int MaxProfilerCaptureDurationMs = 120000;
        private const int DefaultProfilerTopMarkerCount = 40;
        private const double DefaultProfilerHitchThresholdMs = 50d;

        private void RegisterPerformanceTools()
        {
            var schema = JsonSchemas.Object(
                JsonSchemas.StringProperty("target", "editor records the Unity Editor. current/player/phone uses the Profiler's currently attached target, for example an Android or iOS player selected in the Profiler window."),
                JsonSchemas.StringProperty("profilerTargetName", "Optional player target name/IP substring from Unity Profiler target list."),
                JsonSchemas.StringProperty("profilerTargetId", "Optional Unity Profiler connection id."),
                JsonSchemas.StringProperty("profilerTargetUrl", "Optional direct Profiler player address or host:port, if the current Unity version exposes direct profiler connect APIs."),
                JsonSchemas.StringProperty("scenario", "Scenario name for report filenames and markdown."),
                JsonSchemas.BooleanProperty("record", "When true, start Unity Profiler recording before analysis. Defaults to true for capture/hotspot tools and false for analyze."),
                JsonSchemas.StringProperty("duration", "Recording duration such as 15s. Numeric duration means seconds."),
                JsonSchemas.NumberProperty("durationMs", "Recording duration in milliseconds."),
                JsonSchemas.NumberProperty("firstFrame", "Optional first display frame to analyze. Defaults to Profiler first available frame."),
                JsonSchemas.NumberProperty("lastFrame", "Optional last display frame to analyze. Defaults to Profiler last available frame."),
                JsonSchemas.NumberProperty("maxMarkers", "Maximum marker rows in JSON and CSV outputs. Defaults to 40."),
                JsonSchemas.NumberProperty("hitchThresholdMs", "Frame time threshold counted as a hitch. Defaults to 50ms."),
                JsonSchemas.BooleanProperty("stopRecording", "Stop Profiler recording after capture. Defaults to true."),
                JsonSchemas.BooleanProperty("openProfiler", "Open/focus the Unity Profiler window before recording."));

            tools.Register(new DelegateUnityMcpTool("editor.profiler.targets.list", "List profiler target metadata exposed by the Unity Editor Profiler so an agent can choose editor/current/player/phone capture mode.", JsonSchemas.Object(), (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(BuildProfilerTargetListResult()))));

            tools.Register(new DelegateUnityMcpTool("editor.profiler.connect", "Best-effort connection helper for the Unity Editor Profiler. Use target=editor for Editor profiling, or profilerTargetName/profilerTargetId/profilerTargetUrl for a Player/phone when Unity exposes those APIs.", schema, (ctx, args) =>
                Task.FromResult(UnityJson.ToJson(ConnectProfilerTarget(BuildEditorProfilerRequest(args, "editor.profiler.connect", false))))));

            tools.Register(new DelegateUnityMcpTool("editor.profiler.analyze", "Analyze frames currently available in the Unity Editor Profiler. Uses RawFrameDataView to produce frame summary, thread summary, top markers, GC.Alloc summary, CSV, and Markdown under .puerts-unity-mcp/perf-reports.", schema, async (ctx, args) =>
                UnityJson.ToJson(await BuildEditorProfilerReportAsync(args, "editor.profiler.analyze", false), true)));

            tools.Register(new DelegateUnityMcpTool("editor.profiler.capture", "Record the Unity Editor Profiler for a bounded window, then analyze the collected frames. For phones, attach the Profiler to the device first or use Autoconnect Profiler, then set target=current/player/phone.", schema, async (ctx, args) =>
                UnityJson.ToJson(await BuildEditorProfilerReportAsync(args, "editor.profiler.capture", true), true)));

            tools.Register(new DelegateUnityMcpTool("performance.hotspot.report", "Profiler-backed hotspot report. Records via the Unity Editor Profiler, then analyzes Profiler frames with a Profile-Analyzer-inspired pipeline. This replaces the old Runtime sampler path.", schema, async (ctx, args) =>
                UnityJson.ToJson(await BuildEditorProfilerReportAsync(args, "performance.hotspot.report", true), true)));

            tools.Register(new DelegateUnityMcpTool("perf.hotspot.report", "Alias for performance.hotspot.report.", schema, async (ctx, args) =>
                UnityJson.ToJson(await BuildEditorProfilerReportAsync(args, "perf.hotspot.report", true), true)));
        }

        private static async Task<EditorProfilerReportResult> BuildEditorProfilerReportAsync(UnityMcpToolArguments args, string action, bool defaultRecord)
        {
            var request = BuildEditorProfilerRequest(args, action, defaultRecord);
            var warnings = new List<string>();
            var errors = new List<string>();
            var startedUtc = DateTime.UtcNow;
            var runId = BuildProfilerRunId(startedUtc, request.scenario);
            var runRoot = Path.Combine(UnityMcpPaths.PerformanceReportsRoot(), runId);
            Directory.CreateDirectory(runRoot);

            var recordState = new EditorProfilerRecordState
            {
                profilerEnabledBefore = ProfilerDriver.enabled,
                profileEditorBefore = ProfilerDriver.profileEditor
            };

            if (request.openProfiler)
            {
                TryOpenProfilerWindow(warnings);
            }

            ApplyProfilerTargetSelection(request, warnings);

            if (request.record)
            {
                await RecordProfilerWindowAsync(request, recordState, warnings);
                ConstrainCaptureFrameRangeToRecordedFrames(request, recordState, warnings);
            }

            var analysis = AnalyzeEditorProfilerFrames(request, warnings, errors);

            var artifacts = new List<EditorProfilerArtifact>();
            var analysisPath = Path.Combine(runRoot, "profiler-analysis.json");
            AtomicFile.WriteJson(analysisPath, analysis, true);
            artifacts.Add(new EditorProfilerArtifact
            {
                kind = "profiler-analysis",
                path = analysisPath,
                success = true,
                contentType = "application/json"
            });

            var markerCsvPath = Path.Combine(runRoot, "top-markers.csv");
            AtomicFile.WriteAllText(markerCsvPath, BuildTopMarkersCsv(analysis.topMarkers));
            artifacts.Add(new EditorProfilerArtifact
            {
                kind = "top-markers-csv",
                path = markerCsvPath,
                success = true,
                contentType = "text/csv"
            });

            var report = new EditorProfilerReportResult
            {
                action = action,
                success = errors.Count == 0 && analysis.success,
                backend = "unity-editor-profiler-raw-frame-data",
                analyzer = "puerts-unity-mcp-profile-analyzer-inspired",
                runId = runId,
                scenario = request.scenario,
                startedAtUtc = startedUtc.ToString("o", CultureInfo.InvariantCulture),
                completedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                reportPath = Path.Combine(runRoot, "report.md"),
                artifactsRoot = runRoot,
                request = request,
                recordState = recordState,
                profiler = analysis,
                warnings = warnings.ToArray(),
                errors = errors.ToArray()
            };

            AtomicFile.WriteAllText(report.reportPath, BuildEditorProfilerReportMarkdown(report));
            artifacts.Add(new EditorProfilerArtifact
            {
                kind = "markdown-report",
                path = report.reportPath,
                success = true,
                contentType = "text/markdown"
            });

            report.artifacts = artifacts.ToArray();
            var resultPath = Path.Combine(runRoot, "report-result.json");
            AtomicFile.WriteJson(resultPath, report, true);
            artifacts.Add(new EditorProfilerArtifact
            {
                kind = "report-result",
                path = resultPath,
                success = true,
                contentType = "application/json"
            });
            report.artifacts = artifacts.ToArray();
            return report;
        }

        private static EditorProfilerRequest BuildEditorProfilerRequest(UnityMcpToolArguments args, string action, bool defaultRecord)
        {
            var raw = args == null ? null : args.rawArgumentsJson;
            var target = ReadProfilerString(raw, "target", args == null ? null : args.target);
            var scenario = ReadProfilerString(raw, "scenario", args == null ? null : args.scenario);
            var explicitDuration = TryReadProfilerDurationMs(args, out var durationMs);
            var record = ReadProfilerBool(raw, "record", args != null && args.record ? true : defaultRecord || explicitDuration);

            if (!record)
            {
                durationMs = 0;
            }
            else if (!explicitDuration)
            {
                durationMs = DefaultProfilerCaptureDurationMs;
            }

            durationMs = ClampInt(durationMs, 0, MaxProfilerCaptureDurationMs);
            return new EditorProfilerRequest
            {
                action = action,
                target = string.IsNullOrWhiteSpace(target) ? "current" : target.Trim(),
                profilerTargetName = ReadProfilerString(raw, "profilerTargetName", args == null ? null : args.profilerTargetName),
                profilerTargetId = ReadProfilerString(raw, "profilerTargetId", args == null ? null : args.profilerTargetId),
                profilerTargetUrl = ReadProfilerString(raw, "profilerTargetUrl", args == null ? null : args.profilerTargetUrl),
                scenario = string.IsNullOrWhiteSpace(scenario) ? "unspecified" : scenario.Trim(),
                record = record,
                durationMs = durationMs,
                firstFrame = ReadProfilerInt(raw, "firstFrame", args == null ? 0 : args.firstFrame),
                lastFrame = ReadProfilerInt(raw, "lastFrame", args == null ? 0 : args.lastFrame),
                maxMarkers = ClampInt(ReadProfilerInt(raw, "maxMarkers", args != null && args.maxMarkers > 0 ? args.maxMarkers : DefaultProfilerTopMarkerCount), 1, 500),
                hitchThresholdMs = Math.Max(1d, ReadProfilerDouble(raw, "hitchThresholdMs", args == null ? 0d : args.hitchThresholdMs, DefaultProfilerHitchThresholdMs)),
                stopRecording = ReadProfilerBool(raw, "stopRecording", args == null || !args.stopRecording ? true : args.stopRecording),
                openProfiler = ReadProfilerBool(raw, "openProfiler", args != null && args.openProfiler)
            };
        }

        private static async Task RecordProfilerWindowAsync(EditorProfilerRequest request, EditorProfilerRecordState state, List<string> warnings)
        {
            state.firstFrameBefore = ProfilerDriver.firstFrameIndex;
            state.lastFrameBefore = ProfilerDriver.lastFrameIndex;

            if (request.durationMs <= 0)
            {
                warnings.Add("record=true but duration was 0ms; analyzing the existing Profiler frame buffer.");
                state.firstFrameAfter = state.firstFrameBefore;
                state.lastFrameAfter = state.lastFrameBefore;
                return;
            }

            if (string.Equals(request.target, "editor", StringComparison.OrdinalIgnoreCase))
            {
                ProfilerDriver.profileEditor = true;
                state.targetMode = "editor";
            }
            else
            {
                state.targetMode = "current-profiler-target";
                warnings.Add("Profiler target selection uses Unity Editor Profiler state. For phone analysis, connect the Profiler target first, enable Autoconnect Profiler in the build, or pass profilerTargetName/profilerTargetId/profilerTargetUrl.");
            }

            ProfilerDriver.enabled = true;
            state.recordStarted = true;
            state.recordDurationMs = request.durationMs;
            await WaitEditorMilliseconds(request.durationMs);

            if (request.stopRecording)
            {
                ProfilerDriver.enabled = false;
            }

            if (string.Equals(request.target, "editor", StringComparison.OrdinalIgnoreCase))
            {
                ProfilerDriver.profileEditor = state.profileEditorBefore;
            }

            state.profilerEnabledAfter = ProfilerDriver.enabled;
            state.profileEditorAfter = ProfilerDriver.profileEditor;
            state.firstFrameAfter = ProfilerDriver.firstFrameIndex;
            state.lastFrameAfter = ProfilerDriver.lastFrameIndex;
        }

        private static void ConstrainCaptureFrameRangeToRecordedFrames(EditorProfilerRequest request, EditorProfilerRecordState state, List<string> warnings)
        {
            if (request == null || state == null || request.firstFrame > 0 || request.lastFrame > 0 || state.lastFrameAfter < 0)
            {
                return;
            }

            var firstRecordedFrame = Math.Max(state.firstFrameAfter, state.lastFrameBefore + 1);
            if (firstRecordedFrame > state.lastFrameAfter)
            {
                warnings.Add("Profiler recording completed, but no new frames were added after the previous Profiler buffer.");
                return;
            }

            request.firstFrame = firstRecordedFrame + 1;
            request.lastFrame = state.lastFrameAfter + 1;
        }

        private static Task WaitEditorMilliseconds(int durationMs)
        {
            var completion = new TaskCompletionSource<bool>();
            var start = EditorApplication.timeSinceStartup;
            var durationSeconds = Math.Max(0, durationMs) / 1000.0;
            EditorApplication.CallbackFunction callback = null;
            callback = () =>
            {
                if (EditorApplication.timeSinceStartup - start < durationSeconds)
                {
                    return;
                }

                EditorApplication.update -= callback;
                completion.TrySetResult(true);
            };
            EditorApplication.update += callback;
            return completion.Task;
        }

        private static EditorProfilerAnalysis AnalyzeEditorProfilerFrames(EditorProfilerRequest request, List<string> warnings, List<string> errors)
        {
            var firstAvailable = ProfilerDriver.firstFrameIndex;
            var lastAvailable = ProfilerDriver.lastFrameIndex;
            var firstFrame = request.firstFrame > 0 ? request.firstFrame - 1 : firstAvailable;
            var lastFrame = request.lastFrame > 0 ? request.lastFrame - 1 : lastAvailable;
            firstFrame = Math.Max(firstFrame, firstAvailable);
            lastFrame = Math.Min(lastFrame, lastAvailable);

            var frames = new List<double>();
            var markerAccumulators = new Dictionary<string, MarkerAccumulator>(StringComparer.Ordinal);
            var threadAccumulators = new Dictionary<string, ThreadAccumulator>(StringComparer.Ordinal);
            var gcPathAccumulators = new Dictionary<string, GcPathAccumulator>(StringComparer.Ordinal);
            var skippedFrames = 0;

            if (firstAvailable < 0 || lastAvailable < firstAvailable)
            {
                errors.Add("Unity Profiler has no frame data. Open the Profiler, attach/record a target, or call editor.profiler.capture with record=true.");
            }
            else if (lastFrame < firstFrame)
            {
                errors.Add("Requested frame range is empty. availableDisplayFrames=" + (firstAvailable + 1) + "-" + (lastAvailable + 1));
            }
            else
            {
                for (var frameIndex = firstFrame; frameIndex <= lastFrame; frameIndex++)
                {
                    var frameMarkers = new Dictionary<string, MarkerFrameAccumulator>(StringComparer.Ordinal);
                    var frameTimeMs = ReadFrameTimeMs(frameIndex);
                    if (frameTimeMs <= 0d)
                    {
                        skippedFrames++;
                    }
                    else
                    {
                        frames.Add(frameTimeMs);
                    }

                    var threadIndex = 0;
                    while (true)
                    {
                        using (var frameData = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex))
                        {
                            if (!frameData.valid)
                            {
                                break;
                            }

                            ProcessProfilerThreadFrame(frameData, frameIndex + 1, frameMarkers, markerAccumulators, threadAccumulators, gcPathAccumulators, warnings);
                        }

                        threadIndex++;
                    }

                    foreach (var pair in frameMarkers)
                    {
                        if (!markerAccumulators.TryGetValue(pair.Key, out var marker))
                        {
                            marker = new MarkerAccumulator(pair.Key);
                            markerAccumulators.Add(pair.Key, marker);
                        }

                        marker.AddFrame(frameIndex + 1, pair.Value);
                    }
                }
            }

            if (skippedFrames > 0)
            {
                warnings.Add("Skipped " + skippedFrames.ToString(CultureInfo.InvariantCulture) + " frame(s) with unavailable frame time.");
            }

            var sortedFrames = new List<double>(frames);
            sortedFrames.Sort();
            var topMarkers = BuildMarkerRows(markerAccumulators, request.maxMarkers, MarkerSortMode.Total);
            var topSelfMarkers = BuildMarkerRows(markerAccumulators, request.maxMarkers, MarkerSortMode.Self);
            var topGcMarkers = BuildMarkerRows(markerAccumulators, request.maxMarkers, MarkerSortMode.Gc);
            var topGcPaths = BuildGcPathRows(gcPathAccumulators, request.maxMarkers);

            return new EditorProfilerAnalysis
            {
                success = errors.Count == 0,
                availableFirstFrame = firstAvailable < 0 ? 0 : firstAvailable + 1,
                availableLastFrame = lastAvailable < 0 ? 0 : lastAvailable + 1,
                analyzedFirstFrame = firstFrame < 0 ? 0 : firstFrame + 1,
                analyzedLastFrame = lastFrame < 0 ? 0 : lastFrame + 1,
                frameCount = frames.Count,
                skippedFrameCount = skippedFrames,
                frameSummary = new EditorProfilerFrameSummary
                {
                    count = frames.Count,
                    avgMs = Average(frames),
                    medianMs = Percentile(sortedFrames, 50d),
                    p95Ms = Percentile(sortedFrames, 95d),
                    p99Ms = Percentile(sortedFrames, 99d),
                    minMs = sortedFrames.Count == 0 ? 0d : sortedFrames[0],
                    maxMs = sortedFrames.Count == 0 ? 0d : sortedFrames[sortedFrames.Count - 1],
                    hitchThresholdMs = request.hitchThresholdMs,
                    hitchCount = CountHitches(frames, request.hitchThresholdMs)
                },
                threadSummary = BuildThreadRows(threadAccumulators, Math.Max(20, request.maxMarkers)),
                topMarkers = topMarkers,
                topSelfTimeMarkers = topSelfMarkers,
                topGcMarkers = topGcMarkers,
                topGcPaths = topGcPaths,
                markerCount = markerAccumulators.Count,
                gcAllocatedBytes = ReadGcAllocMarkerBytes(markerAccumulators),
                profilerEnabled = ProfilerDriver.enabled,
                profileEditor = ProfilerDriver.profileEditor,
                renderPipeline = RenderPipelineManager.currentPipeline == null ? "BuiltIn" : RenderPipelineManager.currentPipeline.GetType().Name
            };
        }

        private static double ReadFrameTimeMs(int frameIndex)
        {
            using (var frameData = ProfilerDriver.GetRawFrameDataView(frameIndex, 0))
            {
                if (!frameData.valid)
                {
                    return 0d;
                }

                return frameData.frameTimeMs;
            }
        }

        private static void ProcessProfilerThreadFrame(
            RawFrameDataView frameData,
            int displayFrame,
            Dictionary<string, MarkerFrameAccumulator> frameMarkers,
            Dictionary<string, MarkerAccumulator> markerAccumulators,
            Dictionary<string, ThreadAccumulator> threadAccumulators,
            Dictionary<string, GcPathAccumulator> gcPathAccumulators,
            List<string> warnings)
        {
            var threadName = BuildProfilerThreadName(frameData.threadGroupName, frameData.threadName);
            if (!threadAccumulators.TryGetValue(threadName, out var thread))
            {
                thread = new ThreadAccumulator(threadName);
                threadAccumulators.Add(threadName, thread);
            }

            var samples = BuildSampleRecords(frameData, warnings);
            for (var i = samples.Count - 1; i >= 0; i--)
            {
                var sample = samples[i];
                sample.selfMs = Math.Max(0d, sample.ms - sample.childrenMs);
                if (sample.parentIndex >= 0)
                {
                    var parent = samples[sample.parentIndex];
                    parent.childrenMs += sample.ms;
                    parent.gcBytesWithChildren += sample.gcBytesWithChildren;
                    parent.gcAllocationCountWithChildren += sample.gcAllocationCountWithChildren;
                }
            }

            var activeMs = 0d;
            var idleMs = 0d;
            for (var i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                if (sample.depth == 1)
                {
                    if (IsIdleMarker(sample.name))
                    {
                        idleMs += sample.ms;
                    }
                    else
                    {
                        activeMs += sample.ms;
                    }
                }

                if (!frameMarkers.TryGetValue(sample.name, out var frameMarker))
                {
                    frameMarker = new MarkerFrameAccumulator();
                    frameMarkers.Add(sample.name, frameMarker);
                }

                frameMarker.inclusiveMs += sample.ms;
                frameMarker.selfMs += sample.selfMs;
                frameMarker.count++;
                frameMarker.gcBytes += sample.gcBytesWithChildren;
                frameMarker.gcAllocationCount += sample.gcAllocationCountWithChildren;
                frameMarker.minDepth = Math.Min(frameMarker.minDepth, sample.depth);
                frameMarker.maxDepth = Math.Max(frameMarker.maxDepth, sample.depth);
                frameMarker.AddThread(threadName);

                if (string.Equals(sample.name, "GC.Alloc", StringComparison.Ordinal) && sample.gcBytesWithChildren > 0L)
                {
                    var path = BuildSamplePath(samples, i);
                    if (!gcPathAccumulators.TryGetValue(path, out var gcPath))
                    {
                        gcPath = new GcPathAccumulator(path);
                        gcPathAccumulators.Add(path, gcPath);
                    }

                    gcPath.Add(displayFrame, threadName, sample.gcBytesWithChildren);
                }
            }

            thread.AddFrame(displayFrame, activeMs, idleMs);
        }

        private static string BuildSamplePath(List<ProfilerSampleRecord> samples, int sampleIndex)
        {
            var names = new List<string>();
            var guard = 0;
            while (sampleIndex >= 0 && sampleIndex < samples.Count && guard++ < 128)
            {
                var sample = samples[sampleIndex];
                names.Add(sample.name);
                sampleIndex = sample.parentIndex;
            }

            names.Reverse();
            return string.Join(" > ", names.ToArray());
        }

        private static List<ProfilerSampleRecord> BuildSampleRecords(RawFrameDataView frameData, List<string> warnings)
        {
            var samples = new List<ProfilerSampleRecord>(Math.Max(0, frameData.sampleCount - 1));
            var depthStack = new Stack<int>();
            var lastAtDepth = new Dictionary<int, int>();

            for (var i = 1; i < frameData.sampleCount; i++)
            {
                var durationMs = frameData.GetSampleTimeMs(i);
                if (durationMs < 0f)
                {
                    continue;
                }

                var depth = 1 + depthStack.Count;
                var parentIndex = -1;
                if (depth > 1 && lastAtDepth.TryGetValue(depth - 1, out var foundParent))
                {
                    parentIndex = foundParent;
                }

                var markerName = frameData.GetSampleName(i);
                var gcBytes = ReadGcAllocBytes(frameData, i, markerName, warnings);
                var sample = new ProfilerSampleRecord
                {
                    name = string.IsNullOrEmpty(markerName) ? "(unnamed)" : markerName,
                    ms = durationMs,
                    selfMs = durationMs,
                    depth = depth,
                    parentIndex = parentIndex,
                    gcBytesWithChildren = gcBytes,
                    gcAllocationCountWithChildren = gcBytes > 0L ? 1 : 0
                };

                samples.Add(sample);
                lastAtDepth[depth] = samples.Count - 1;

                var childrenCount = frameData.GetSampleChildrenCount(i);
                if (childrenCount > 0)
                {
                    depthStack.Push(childrenCount);
                }
                else
                {
                    while (depthStack.Count > 0)
                    {
                        var remainingChildren = depthStack.Pop();
                        if (remainingChildren > 1)
                        {
                            depthStack.Push(remainingChildren - 1);
                            break;
                        }
                    }
                }
            }

            return samples;
        }

        private static long ReadGcAllocBytes(RawFrameDataView frameData, int sampleIndex, string markerName, List<string> warnings)
        {
            if (!string.Equals(markerName, "GC.Alloc", StringComparison.Ordinal))
            {
                return 0L;
            }

            try
            {
                var markerId = frameData.GetSampleMarkerId(sampleIndex);
                var metadata = frameData.GetMarkerMetadataInfo(markerId);
                for (var i = 0; metadata != null && i < metadata.Length; i++)
                {
                    var info = metadata[i];
                    if (string.Equals(info.name, "Size", StringComparison.Ordinal)
                        && info.unit == ProfilerMarkerDataUnit.Bytes)
                    {
                        return Math.Max(0L, frameData.GetSampleMetadataAsLong(sampleIndex, i));
                    }
                }
            }
            catch (Exception ex)
            {
                AddUniqueWarning(warnings, "Failed to read GC.Alloc metadata: " + ex.GetType().Name);
            }

            return 0L;
        }

        private static EditorProfilerMarkerRow[] BuildMarkerRows(Dictionary<string, MarkerAccumulator> markers, int maxMarkers, MarkerSortMode sortMode)
        {
            var rows = new List<EditorProfilerMarkerRow>();
            foreach (var pair in markers)
            {
                rows.Add(pair.Value.ToRow());
            }

            rows.Sort((left, right) =>
            {
                var compare = sortMode == MarkerSortMode.Self
                    ? right.selfTotalMs.CompareTo(left.selfTotalMs)
                    : sortMode == MarkerSortMode.Gc
                        ? right.gcBytesTotal.CompareTo(left.gcBytesTotal)
                        : right.totalMs.CompareTo(left.totalMs);
                return compare != 0 ? compare : string.CompareOrdinal(left.name, right.name);
            });

            if (rows.Count > maxMarkers)
            {
                rows.RemoveRange(maxMarkers, rows.Count - maxMarkers);
            }

            return rows.ToArray();
        }

        private static EditorProfilerThreadRow[] BuildThreadRows(Dictionary<string, ThreadAccumulator> threads, int maxRows)
        {
            var rows = new List<EditorProfilerThreadRow>();
            foreach (var pair in threads)
            {
                rows.Add(pair.Value.ToRow());
            }

            rows.Sort((left, right) => right.totalActiveMs.CompareTo(left.totalActiveMs));
            if (rows.Count > maxRows)
            {
                rows.RemoveRange(maxRows, rows.Count - maxRows);
            }

            return rows.ToArray();
        }

        private static EditorProfilerGcPathRow[] BuildGcPathRows(Dictionary<string, GcPathAccumulator> paths, int maxRows)
        {
            var rows = new List<EditorProfilerGcPathRow>();
            foreach (var pair in paths)
            {
                rows.Add(pair.Value.ToRow());
            }

            rows.Sort((left, right) =>
            {
                var compare = right.gcBytesTotal.CompareTo(left.gcBytesTotal);
                return compare != 0 ? compare : string.CompareOrdinal(left.path, right.path);
            });

            if (rows.Count > maxRows)
            {
                rows.RemoveRange(maxRows, rows.Count - maxRows);
            }

            return rows.ToArray();
        }

        private static string BuildTopMarkersCsv(EditorProfilerMarkerRow[] markers)
        {
            var builder = new StringBuilder();
            builder.AppendLine("name,totalMs,selfTotalMs,medianFrameMs,maxFrameMs,maxFrame,count,presentOnFrameCount,gcBytesTotal,gcAllocationCount,minDepth,maxDepth,threads");
            for (var i = 0; markers != null && i < markers.Length; i++)
            {
                var marker = markers[i];
                builder.Append(EscapeCsv(marker.name)).Append(',')
                    .Append(FormatDouble(marker.totalMs)).Append(',')
                    .Append(FormatDouble(marker.selfTotalMs)).Append(',')
                    .Append(FormatDouble(marker.medianFrameMs)).Append(',')
                    .Append(FormatDouble(marker.maxFrameMs)).Append(',')
                    .Append(marker.maxFrame).Append(',')
                    .Append(marker.count).Append(',')
                    .Append(marker.presentOnFrameCount).Append(',')
                    .Append(marker.gcBytesTotal).Append(',')
                    .Append(marker.gcAllocationCount).Append(',')
                    .Append(marker.minDepth).Append(',')
                    .Append(marker.maxDepth).Append(',')
                    .Append(EscapeCsv(marker.threads));
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static string BuildEditorProfilerReportMarkdown(EditorProfilerReportResult report)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# Unity Profiler Hotspot Report");
            builder.AppendLine();
            builder.AppendLine("- Scenario: " + report.scenario);
            builder.AppendLine("- Backend: " + report.backend);
            builder.AppendLine("- Target: " + report.request.target);
            builder.AppendLine("- Record: " + report.request.record + " (" + report.request.durationMs + " ms)");
            builder.AppendLine("- Frames: " + report.profiler.analyzedFirstFrame + "-" + report.profiler.analyzedLastFrame + " (" + report.profiler.frameCount + ")");
            builder.AppendLine("- Success: " + report.success);
            builder.AppendLine();

            var frame = report.profiler.frameSummary;
            builder.AppendLine("## Frame Summary");
            builder.AppendLine();
            builder.AppendLine("| Metric | Value |");
            builder.AppendLine("| --- | ---: |");
            builder.AppendLine("| Avg ms | " + FormatDouble(frame.avgMs) + " |");
            builder.AppendLine("| Median ms | " + FormatDouble(frame.medianMs) + " |");
            builder.AppendLine("| P95 ms | " + FormatDouble(frame.p95Ms) + " |");
            builder.AppendLine("| P99 ms | " + FormatDouble(frame.p99Ms) + " |");
            builder.AppendLine("| Max ms | " + FormatDouble(frame.maxMs) + " |");
            builder.AppendLine("| Hitches | " + frame.hitchCount + " over " + FormatDouble(frame.hitchThresholdMs) + " ms |");
            builder.AppendLine();

            AppendMarkerTable(builder, "Top Markers By Total Time", report.profiler.topMarkers);
            AppendMarkerTable(builder, "Top Markers By Self Time", report.profiler.topSelfTimeMarkers);
            AppendMarkerTable(builder, "Top GC Allocation Markers", report.profiler.topGcMarkers);
            AppendGcPathTable(builder, "Top GC Allocation Paths", report.profiler.topGcPaths);

            if (report.warnings != null && report.warnings.Length > 0)
            {
                builder.AppendLine("## Warnings");
                for (var i = 0; i < report.warnings.Length; i++)
                {
                    builder.AppendLine("- " + report.warnings[i]);
                }

                builder.AppendLine();
            }

            if (report.errors != null && report.errors.Length > 0)
            {
                builder.AppendLine("## Errors");
                for (var i = 0; i < report.errors.Length; i++)
                {
                    builder.AppendLine("- " + report.errors[i]);
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static void AppendMarkerTable(StringBuilder builder, string title, EditorProfilerMarkerRow[] markers)
        {
            builder.AppendLine("## " + title);
            builder.AppendLine();
            builder.AppendLine("| Marker | Total ms | Self ms | Median frame ms | Max frame ms | Count | GC bytes |");
            builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
            var count = Math.Min(15, markers == null ? 0 : markers.Length);
            for (var i = 0; i < count; i++)
            {
                var marker = markers[i];
                builder.Append("| ")
                    .Append(EscapeMarkdownCell(marker.name)).Append(" | ")
                    .Append(FormatDouble(marker.totalMs)).Append(" | ")
                    .Append(FormatDouble(marker.selfTotalMs)).Append(" | ")
                    .Append(FormatDouble(marker.medianFrameMs)).Append(" | ")
                    .Append(FormatDouble(marker.maxFrameMs)).Append(" | ")
                    .Append(marker.count).Append(" | ")
                    .Append(marker.gcBytesTotal).AppendLine(" |");
            }

            builder.AppendLine();
        }

        private static void AppendGcPathTable(StringBuilder builder, string title, EditorProfilerGcPathRow[] paths)
        {
            builder.AppendLine("## " + title);
            builder.AppendLine();
            builder.AppendLine("| Path | GC bytes | Alloc count | Frames | Max frame bytes | Threads |");
            builder.AppendLine("| --- | ---: | ---: | ---: | ---: | --- |");
            var count = Math.Min(15, paths == null ? 0 : paths.Length);
            for (var i = 0; i < count; i++)
            {
                var path = paths[i];
                builder.Append("| ")
                    .Append(EscapeMarkdownCell(path.path)).Append(" | ")
                    .Append(path.gcBytesTotal).Append(" | ")
                    .Append(path.gcAllocationCount).Append(" | ")
                    .Append(path.presentOnFrameCount).Append(" | ")
                    .Append(path.maxFrameBytes).Append(" | ")
                    .Append(EscapeMarkdownCell(path.threads)).AppendLine(" |");
            }

            builder.AppendLine();
        }

        private static EditorProfilerTargetListResult BuildProfilerTargetListResult()
        {
            var warnings = new List<string>();
            return new EditorProfilerTargetListResult
            {
                action = "editor.profiler.targets.list",
                backend = "unity-editor-profiler",
                profilerEnabled = ProfilerDriver.enabled,
                profileEditor = ProfilerDriver.profileEditor,
                currentProfilerId = GetConnectedProfilerId(warnings),
                targets = ListProfilerTargets(warnings),
                warnings = warnings.ToArray()
            };
        }

        private static EditorProfilerConnectResult ConnectProfilerTarget(EditorProfilerRequest request)
        {
            var warnings = new List<string>();
            ApplyProfilerTargetSelection(request, warnings);
            return new EditorProfilerConnectResult
            {
                action = "editor.profiler.connect",
                success = warnings.Count == 0 || ProfilerDriver.profileEditor || GetConnectedProfilerId(null) != 0,
                requestedTarget = request.target,
                requestedProfilerTargetName = request.profilerTargetName,
                requestedProfilerTargetId = request.profilerTargetId,
                requestedProfilerTargetUrl = request.profilerTargetUrl,
                profilerEnabled = ProfilerDriver.enabled,
                profileEditor = ProfilerDriver.profileEditor,
                currentProfilerId = GetConnectedProfilerId(null),
                targets = ListProfilerTargets(warnings),
                warnings = warnings.ToArray()
            };
        }

        private static void ApplyProfilerTargetSelection(EditorProfilerRequest request, List<string> warnings)
        {
            if (request == null)
            {
                return;
            }

            if (string.Equals(request.target, "editor", StringComparison.OrdinalIgnoreCase))
            {
                ProfilerDriver.profileEditor = true;
                return;
            }

            if (!string.IsNullOrWhiteSpace(request.profilerTargetUrl))
            {
                if (TryConnectProfilerDirectUrl(request.profilerTargetUrl.Trim(), warnings))
                {
                    ProfilerDriver.profileEditor = false;
                }

                return;
            }

            var targetId = ParseProfilerTargetId(request.profilerTargetId);
            if (targetId != 0)
            {
                if (TrySetConnectedProfiler(targetId, warnings))
                {
                    ProfilerDriver.profileEditor = false;
                }

                return;
            }

            if (!string.IsNullOrWhiteSpace(request.profilerTargetName))
            {
                var target = FindProfilerTarget(request.profilerTargetName, warnings);
                if (target != null && TrySetConnectedProfiler(target.id, warnings))
                {
                    ProfilerDriver.profileEditor = false;
                    return;
                }

                warnings.Add("Profiler target not found: " + request.profilerTargetName);
            }
        }

        private static bool TryConnectProfilerDirectUrl(string url, List<string> warnings)
        {
            var driverType = typeof(ProfilerDriver);
            try
            {
                var property = driverType.GetProperty("directConnectionUrl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?? driverType.GetProperty("directURL", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (property != null && property.CanWrite && property.PropertyType == typeof(string))
                {
                    property.SetValue(null, url, null);
                }

                var method = driverType.GetMethod("DirectURLConnect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null)
                    ?? driverType.GetMethod("ConnectDirectly", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (method != null)
                {
                    method.Invoke(null, new object[] { url });
                    return true;
                }

                method = driverType.GetMethod("DirectURLConnect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null)
                    ?? driverType.GetMethod("ConnectDirectly", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    method.Invoke(null, null);
                    return true;
                }

                warnings.Add("This Unity version did not expose a direct Profiler URL connect method; select the phone/player in the Profiler window or use Autoconnect Profiler.");
            }
            catch (Exception ex)
            {
                warnings.Add("Direct Profiler URL connect failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            return false;
        }

        private static bool TrySetConnectedProfiler(int id, List<string> warnings)
        {
            var driverType = typeof(ProfilerDriver);
            try
            {
                var property = driverType.GetProperty("connectedProfiler", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (property != null && property.CanWrite)
                {
                    var converted = Convert.ChangeType(id, property.PropertyType, CultureInfo.InvariantCulture);
                    property.SetValue(null, converted, null);
                    return true;
                }

                var method = driverType.GetMethod("SetActiveProfiler", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(int) }, null)
                    ?? driverType.GetMethod("SetConnectedProfiler", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(int) }, null);
                if (method != null)
                {
                    method.Invoke(null, new object[] { id });
                    return true;
                }

                warnings?.Add("This Unity version did not expose a writable connected Profiler target API; select the phone/player in the Profiler window.");
            }
            catch (Exception ex)
            {
                warnings?.Add("Failed to set Profiler target id " + id.ToString(CultureInfo.InvariantCulture) + ": " + ex.GetType().Name + ": " + ex.Message);
            }

            return false;
        }

        private static EditorProfilerTargetInfo FindProfilerTarget(string query, List<string> warnings)
        {
            var trimmed = (query ?? string.Empty).Trim();
            var targets = ListProfilerTargets(warnings);
            for (var i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                if (string.Equals(target.name, trimmed, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(target.ip, trimmed, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(target.identifier, trimmed, StringComparison.OrdinalIgnoreCase)
                    || target.id.ToString(CultureInfo.InvariantCulture) == trimmed)
                {
                    return target;
                }
            }

            for (var i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                var haystack = (target.name ?? string.Empty) + " " + (target.ip ?? string.Empty) + " " + (target.identifier ?? string.Empty) + " " + (target.platform ?? string.Empty);
                if (haystack.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return target;
                }
            }

            return null;
        }

        private static EditorProfilerTargetInfo[] ListProfilerTargets(List<string> warnings)
        {
            var driverType = typeof(ProfilerDriver);
            try
            {
                var method = driverType.GetMethod("GetAvailableProfilers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                var ids = method == null ? null : method.Invoke(null, null) as Array;
                if (ids == null)
                {
                    warnings?.Add("This Unity version did not expose ProfilerDriver.GetAvailableProfilers.");
                    return new EditorProfilerTargetInfo[0];
                }

                var result = new EditorProfilerTargetInfo[ids.Length];
                var current = GetConnectedProfilerId(null);
                for (var i = 0; i < ids.Length; i++)
                {
                    var id = Convert.ToInt32(ids.GetValue(i), CultureInfo.InvariantCulture);
                    result[i] = new EditorProfilerTargetInfo
                    {
                        id = id,
                        isConnected = id == current,
                        name = InvokeProfilerString("GetConnectionName", id),
                        identifier = InvokeProfilerString("GetConnectionIdentifier", id),
                        ip = InvokeProfilerString("GetConnectionIP", id),
                        platform = InvokeProfilerString("GetConnectionPlatform", id)
                    };
                }

                return result;
            }
            catch (Exception ex)
            {
                warnings?.Add("Failed to list Unity Profiler targets: " + ex.GetType().Name + ": " + ex.Message);
                return new EditorProfilerTargetInfo[0];
            }
        }

        private static int GetConnectedProfilerId(List<string> warnings)
        {
            try
            {
                var property = typeof(ProfilerDriver).GetProperty("connectedProfiler", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (property == null || !property.CanRead)
                {
                    warnings?.Add("This Unity version did not expose ProfilerDriver.connectedProfiler.");
                    return 0;
                }

                var value = property.GetValue(null, null);
                return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                warnings?.Add("Failed to read connected Profiler target: " + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }
        }

        private static string InvokeProfilerString(string methodName, int id)
        {
            try
            {
                var method = typeof(ProfilerDriver).GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(int) }, null);
                if (method == null)
                {
                    return null;
                }

                var value = method.Invoke(null, new object[] { id });
                return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static int ParseProfilerTargetId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                ? id
                : 0;
        }

        private static void TryOpenProfilerWindow(List<string> warnings)
        {
            try
            {
                var profilerWindowType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.ProfilerWindow")
                    ?? typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Profiling.ProfilerWindow");
                if (profilerWindowType == null)
                {
                    warnings.Add("Could not resolve Unity ProfilerWindow type.");
                    return;
                }

                var window = EditorWindow.GetWindow(profilerWindowType);
                window.Show();
                window.Focus();
            }
            catch (Exception ex)
            {
                warnings.Add("Failed to open Profiler window: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string BuildProfilerThreadName(string groupName, string threadName)
        {
            if (string.IsNullOrEmpty(groupName))
            {
                return string.IsNullOrEmpty(threadName) ? "(unnamed thread)" : threadName;
            }

            return groupName + "." + (string.IsNullOrEmpty(threadName) ? "(unnamed thread)" : threadName);
        }

        private static bool IsIdleMarker(string markerName)
        {
            if (string.IsNullOrEmpty(markerName))
            {
                return false;
            }

            return string.Equals(markerName, "Idle", StringComparison.Ordinal)
                || string.Equals(markerName, "Semaphore.WaitForSignal", StringComparison.Ordinal)
                || string.Equals(markerName, "WaitForTargetFPS", StringComparison.Ordinal)
                || string.Equals(markerName, "Gfx.WaitForPresentOnGfxThread", StringComparison.Ordinal)
                || (!string.Equals(markerName, "PlayerLoop", StringComparison.OrdinalIgnoreCase) && markerName.IndexOf("Wait", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string BuildProfilerRunId(DateTime startedUtc, string scenario)
        {
            var safeScenario = UnityMcpPaths.SanitizeId(string.IsNullOrWhiteSpace(scenario) ? "profiler" : scenario);
            return "profiler_" + startedUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + safeScenario;
        }

        private static bool TryReadProfilerDurationMs(UnityMcpToolArguments args, out int durationMs)
        {
            durationMs = 0;
            var raw = args == null ? null : args.rawArgumentsJson;
            if (UnityMcpJsonArgumentReader.TryGetString(raw, "duration", out var durationText))
            {
                durationMs = ParseProfilerDurationMs(durationText, true);
                return true;
            }

            if (UnityMcpJsonArgumentReader.TryGetDouble(raw, "durationMs", out var rawDurationMs))
            {
                durationMs = ClampInt((int)Math.Round(rawDurationMs), 0, MaxProfilerCaptureDurationMs);
                return true;
            }

            if (args != null && args.durationMs > 0f)
            {
                durationMs = ClampInt((int)Math.Round(args.durationMs), 0, MaxProfilerCaptureDurationMs);
                return true;
            }

            if (args != null && args.duration > 0f)
            {
                durationMs = ClampInt((int)Math.Round(args.duration * 1000d), 0, MaxProfilerCaptureDurationMs);
                return true;
            }

            return false;
        }

        private static int ParseProfilerDurationMs(string text, bool bareNumberMeansSeconds)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return DefaultProfilerCaptureDurationMs;
            }

            text = text.Trim().ToLowerInvariant();
            var multiplier = bareNumberMeansSeconds ? 1000d : 1d;
            if (text.EndsWith("ms", StringComparison.Ordinal))
            {
                multiplier = 1d;
                text = text.Substring(0, text.Length - 2);
            }
            else if (text.EndsWith("s", StringComparison.Ordinal))
            {
                multiplier = 1000d;
                text = text.Substring(0, text.Length - 1);
            }

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? ClampInt((int)Math.Round(value * multiplier), 0, MaxProfilerCaptureDurationMs)
                : DefaultProfilerCaptureDurationMs;
        }

        private static string ReadProfilerString(string raw, string name, string fallback)
        {
            return UnityMcpJsonArgumentReader.TryGetString(raw, name, out var value)
                ? value
                : fallback;
        }

        private static bool ReadProfilerBool(string raw, string name, bool fallback)
        {
            return UnityMcpJsonArgumentReader.TryGetBool(raw, name, out var value)
                ? value
                : fallback;
        }

        private static int ReadProfilerInt(string raw, string name, int fallback)
        {
            return UnityMcpJsonArgumentReader.TryGetInt(raw, name, out var value)
                ? value
                : fallback;
        }

        private static double ReadProfilerDouble(string raw, string name, double typedValue, double fallback)
        {
            if (typedValue > 0d)
            {
                return typedValue;
            }

            return UnityMcpJsonArgumentReader.TryGetDouble(raw, name, out var value)
                ? value
                : fallback;
        }

        private static int ClampInt(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static double Average(List<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0d;
            }

            var total = 0d;
            for (var i = 0; i < values.Count; i++)
            {
                total += values[i];
            }

            return total / values.Count;
        }

        private static double Percentile(List<double> sortedValues, double percentile)
        {
            if (sortedValues == null || sortedValues.Count == 0)
            {
                return 0d;
            }

            if (sortedValues.Count == 1)
            {
                return sortedValues[0];
            }

            var index = (int)Math.Round((sortedValues.Count - 1) * percentile / 100d);
            index = Math.Max(0, Math.Min(sortedValues.Count - 1, index));
            return sortedValues[index];
        }

        private static int CountHitches(List<double> values, double threshold)
        {
            var count = 0;
            for (var i = 0; values != null && i < values.Count; i++)
            {
                if (values[i] >= threshold)
                {
                    count++;
                }
            }

            return count;
        }

        private static long ReadGcAllocMarkerBytes(Dictionary<string, MarkerAccumulator> markers)
        {
            if (markers != null && markers.TryGetValue("GC.Alloc", out var marker))
            {
                return marker.gcBytesTotal;
            }

            return 0L;
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string EscapeCsv(string value)
        {
            value = value ?? string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string EscapeMarkdownCell(string value)
        {
            return (value ?? string.Empty).Replace("|", "\\|");
        }

        private static void AddUniqueWarning(List<string> warnings, string warning)
        {
            if (warnings != null && !string.IsNullOrEmpty(warning) && !warnings.Contains(warning))
            {
                warnings.Add(warning);
            }
        }

        private enum MarkerSortMode
        {
            Total,
            Self,
            Gc
        }

        private sealed class ProfilerSampleRecord
        {
            public string name;
            public double ms;
            public double selfMs;
            public double childrenMs;
            public int depth;
            public int parentIndex;
            public long gcBytesWithChildren;
            public int gcAllocationCountWithChildren;
        }

        private sealed class MarkerFrameAccumulator
        {
            private readonly List<string> threads = new List<string>();

            public double inclusiveMs;
            public double selfMs;
            public int count;
            public long gcBytes;
            public int gcAllocationCount;
            public int minDepth = int.MaxValue;
            public int maxDepth;

            public void AddThread(string threadName)
            {
                if (!threads.Contains(threadName))
                {
                    threads.Add(threadName);
                }
            }

            public string ThreadsCsv()
            {
                threads.Sort(StringComparer.Ordinal);
                return string.Join(", ", threads.ToArray());
            }
        }

        private sealed class MarkerAccumulator
        {
            private readonly List<double> frameInclusiveMs = new List<double>();
            private readonly List<string> threads = new List<string>();

            public MarkerAccumulator(string name)
            {
                this.name = name;
            }

            public string name;
            public double totalMs;
            public double selfTotalMs;
            public double maxFrameMs;
            public int maxFrame;
            public int count;
            public int presentOnFrameCount;
            public long gcBytesTotal;
            public int gcAllocationCount;
            public int minDepth = int.MaxValue;
            public int maxDepth;

            public void AddFrame(int frame, MarkerFrameAccumulator value)
            {
                totalMs += value.inclusiveMs;
                selfTotalMs += value.selfMs;
                count += value.count;
                presentOnFrameCount++;
                gcBytesTotal += value.gcBytes;
                gcAllocationCount += value.gcAllocationCount;
                minDepth = Math.Min(minDepth, value.minDepth);
                maxDepth = Math.Max(maxDepth, value.maxDepth);
                frameInclusiveMs.Add(value.inclusiveMs);
                if (value.inclusiveMs > maxFrameMs)
                {
                    maxFrameMs = value.inclusiveMs;
                    maxFrame = frame;
                }

                var threadCsv = value.ThreadsCsv();
                if (!string.IsNullOrEmpty(threadCsv) && !threads.Contains(threadCsv))
                {
                    threads.Add(threadCsv);
                }
            }

            public EditorProfilerMarkerRow ToRow()
            {
                var sorted = new List<double>(frameInclusiveMs);
                sorted.Sort();
                threads.Sort(StringComparer.Ordinal);
                return new EditorProfilerMarkerRow
                {
                    name = name,
                    totalMs = totalMs,
                    selfTotalMs = selfTotalMs,
                    medianFrameMs = Percentile(sorted, 50d),
                    maxFrameMs = maxFrameMs,
                    maxFrame = maxFrame,
                    count = count,
                    presentOnFrameCount = presentOnFrameCount,
                    gcBytesTotal = gcBytesTotal,
                    gcAllocationCount = gcAllocationCount,
                    minDepth = minDepth == int.MaxValue ? 0 : minDepth,
                    maxDepth = maxDepth,
                    threads = string.Join("; ", threads.ToArray())
                };
            }
        }

        private sealed class ThreadAccumulator
        {
            private readonly List<double> activeFrameMs = new List<double>();

            public ThreadAccumulator(string name)
            {
                this.name = name;
            }

            public string name;
            public double totalActiveMs;
            public double totalIdleMs;
            public double maxActiveMs;
            public int maxFrame;

            public void AddFrame(int frame, double activeMs, double idleMs)
            {
                totalActiveMs += activeMs;
                totalIdleMs += idleMs;
                activeFrameMs.Add(activeMs);
                if (activeMs > maxActiveMs)
                {
                    maxActiveMs = activeMs;
                    maxFrame = frame;
                }
            }

            public EditorProfilerThreadRow ToRow()
            {
                return new EditorProfilerThreadRow
                {
                    name = name,
                    totalActiveMs = totalActiveMs,
                    totalIdleMs = totalIdleMs,
                    avgActiveMs = Average(activeFrameMs),
                    maxActiveMs = maxActiveMs,
                    maxFrame = maxFrame,
                    frameCount = activeFrameMs.Count
                };
            }
        }

        private sealed class GcPathAccumulator
        {
            private readonly List<int> frames = new List<int>();
            private readonly List<string> threads = new List<string>();

            public GcPathAccumulator(string path)
            {
                this.path = path;
            }

            public string path;
            public long gcBytesTotal;
            public int gcAllocationCount;
            public long maxFrameBytes;
            public int maxFrame;

            public void Add(int frame, string threadName, long bytes)
            {
                gcBytesTotal += bytes;
                gcAllocationCount++;
                if (!frames.Contains(frame))
                {
                    frames.Add(frame);
                }

                if (!string.IsNullOrEmpty(threadName) && !threads.Contains(threadName))
                {
                    threads.Add(threadName);
                }

                if (bytes > maxFrameBytes)
                {
                    maxFrameBytes = bytes;
                    maxFrame = frame;
                }
            }

            public EditorProfilerGcPathRow ToRow()
            {
                threads.Sort(StringComparer.Ordinal);
                return new EditorProfilerGcPathRow
                {
                    path = path,
                    gcBytesTotal = gcBytesTotal,
                    gcAllocationCount = gcAllocationCount,
                    presentOnFrameCount = frames.Count,
                    maxFrameBytes = maxFrameBytes,
                    maxFrame = maxFrame,
                    threads = string.Join("; ", threads.ToArray())
                };
            }
        }

        [Serializable]
        private sealed class EditorProfilerRequest
        {
            public string action;
            public string target;
            public string profilerTargetName;
            public string profilerTargetId;
            public string profilerTargetUrl;
            public string scenario;
            public bool record;
            public int durationMs;
            public int firstFrame;
            public int lastFrame;
            public int maxMarkers;
            public double hitchThresholdMs;
            public bool stopRecording;
            public bool openProfiler;
        }

        [Serializable]
        private sealed class EditorProfilerRecordState
        {
            public bool profilerEnabledBefore;
            public bool profilerEnabledAfter;
            public bool profileEditorBefore;
            public bool profileEditorAfter;
            public bool recordStarted;
            public int recordDurationMs;
            public int firstFrameBefore;
            public int lastFrameBefore;
            public int firstFrameAfter;
            public int lastFrameAfter;
            public string targetMode;
        }

        [Serializable]
        private sealed class EditorProfilerTargetListResult
        {
            public string action;
            public string backend;
            public bool profilerEnabled;
            public bool profileEditor;
            public int currentProfilerId;
            public EditorProfilerTargetInfo[] targets;
            public string[] warnings;
        }

        [Serializable]
        private sealed class EditorProfilerConnectResult
        {
            public string action;
            public bool success;
            public string requestedTarget;
            public string requestedProfilerTargetName;
            public string requestedProfilerTargetId;
            public string requestedProfilerTargetUrl;
            public bool profilerEnabled;
            public bool profileEditor;
            public int currentProfilerId;
            public EditorProfilerTargetInfo[] targets;
            public string[] warnings;
        }

        [Serializable]
        private sealed class EditorProfilerTargetInfo
        {
            public int id;
            public bool isConnected;
            public string name;
            public string identifier;
            public string ip;
            public string platform;
        }

        [Serializable]
        private sealed class EditorProfilerReportResult
        {
            public string action;
            public bool success;
            public string backend;
            public string analyzer;
            public string runId;
            public string scenario;
            public string startedAtUtc;
            public string completedAtUtc;
            public string reportPath;
            public string artifactsRoot;
            public EditorProfilerRequest request;
            public EditorProfilerRecordState recordState;
            public EditorProfilerAnalysis profiler;
            public EditorProfilerArtifact[] artifacts;
            public string[] warnings;
            public string[] errors;
        }

        [Serializable]
        private sealed class EditorProfilerAnalysis
        {
            public bool success;
            public bool profilerEnabled;
            public bool profileEditor;
            public int availableFirstFrame;
            public int availableLastFrame;
            public int analyzedFirstFrame;
            public int analyzedLastFrame;
            public int frameCount;
            public int skippedFrameCount;
            public int markerCount;
            public long gcAllocatedBytes;
            public string renderPipeline;
            public EditorProfilerFrameSummary frameSummary;
            public EditorProfilerThreadRow[] threadSummary;
            public EditorProfilerMarkerRow[] topMarkers;
            public EditorProfilerMarkerRow[] topSelfTimeMarkers;
            public EditorProfilerMarkerRow[] topGcMarkers;
            public EditorProfilerGcPathRow[] topGcPaths;
        }

        [Serializable]
        private sealed class EditorProfilerFrameSummary
        {
            public int count;
            public double avgMs;
            public double medianMs;
            public double p95Ms;
            public double p99Ms;
            public double minMs;
            public double maxMs;
            public double hitchThresholdMs;
            public int hitchCount;
        }

        [Serializable]
        private sealed class EditorProfilerMarkerRow
        {
            public string name;
            public double totalMs;
            public double selfTotalMs;
            public double medianFrameMs;
            public double maxFrameMs;
            public int maxFrame;
            public int count;
            public int presentOnFrameCount;
            public long gcBytesTotal;
            public int gcAllocationCount;
            public int minDepth;
            public int maxDepth;
            public string threads;
        }

        [Serializable]
        private sealed class EditorProfilerThreadRow
        {
            public string name;
            public double totalActiveMs;
            public double totalIdleMs;
            public double avgActiveMs;
            public double maxActiveMs;
            public int maxFrame;
            public int frameCount;
        }

        [Serializable]
        private sealed class EditorProfilerGcPathRow
        {
            public string path;
            public long gcBytesTotal;
            public int gcAllocationCount;
            public int presentOnFrameCount;
            public long maxFrameBytes;
            public int maxFrame;
            public string threads;
        }

        [Serializable]
        private sealed class EditorProfilerArtifact
        {
            public string kind;
            public string path;
            public bool success;
            public string contentType;
        }
    }
}
