using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace PuertsUnityMcp
{
    public sealed partial class UnityMcpRuntimeHost
    {
        private const int UiDefaultMaxResults = 100;
        private const int UiDefaultRaycastMaxResults = 20;

        private RuntimeLogsResult BuildRuntimeLogsResult(UnityMcpToolArguments args)
        {
            var entries = runtimeLogBuffer.GetEntries(args.count <= 0 ? 100 : args.count, args.logType, args.regex, args.includeStackTrace);
            return new RuntimeLogsResult
            {
                action = "runtime.logs",
                targetId = EndpointId,
                totalCount = runtimeLogBuffer.Count,
                returnedCount = entries.Length,
                count = args.count <= 0 ? 100 : args.count,
                logType = string.IsNullOrEmpty(args.logType) ? "All" : args.logType,
                regex = args.regex,
                includeStackTrace = args.includeStackTrace,
                entries = entries
            };
        }

        private RuntimeLogsClearResult ClearRuntimeLogs()
        {
            return new RuntimeLogsClearResult
            {
                action = "runtime.logs.clear",
                targetId = EndpointId,
                clearedCount = runtimeLogBuffer.Clear()
            };
        }

        private RuntimeUiSnapshotResult BuildUiSnapshotResult(UnityMcpToolArguments args)
        {
            var maxResults = ResolveMaxResults(args.maxResults, UiDefaultMaxResults);
            var buttons = CollectButtonSnapshots(null, args.includeDisabled, maxResults, true);
            var canvases = BuildCanvasSnapshots(buttons.countsByCanvasPath, args.includeDisabled);

            return new RuntimeUiSnapshotResult
            {
                action = "runtime.ui.snapshot",
                targetId = EndpointId,
                screen = BuildScreenSnapshot(),
                eventSystem = BuildEventSystemSnapshot(),
                selected = BuildGameObjectInfo(GetSelectedGameObject()),
                canvases = canvases,
                buttons = buttons.entries.ToArray(),
                summary = new RuntimeUiSnapshotSummary
                {
                    canvasCount = canvases.Length,
                    buttonCount = buttons.totalCount,
                    returnedButtonCount = buttons.entries.Count,
                    clickableButtonCount = CountClickableButtons(buttons.entries),
                    truncated = buttons.truncated,
                    hasEventSystem = EventSystem.current != null
                }
            };
        }

        private RuntimeUiFindResult BuildUiFindResult(UnityMcpToolArguments args)
        {
            var maxResults = ResolveMaxResults(args.maxResults, UiDefaultMaxResults);
            var buttons = CollectButtonSnapshots(args.keyword, args.includeDisabled, maxResults, true);

            return new RuntimeUiFindResult
            {
                action = "runtime.ui.find",
                targetId = EndpointId,
                keyword = args.keyword,
                buttons = buttons.entries.ToArray(),
                summary = new RuntimeUiFindSummary
                {
                    matchCount = buttons.totalCount,
                    returnedCount = buttons.entries.Count,
                    clickableCount = CountClickableButtons(buttons.entries),
                    truncated = buttons.truncated
                }
            };
        }

        private RuntimeUiRaycastResult BuildUiRaycastResult(UnityMcpToolArguments args)
        {
            var target = ResolveOptionalTarget(args);
            if (HasRequestedTarget(args) && target == null)
            {
                return new RuntimeUiRaycastResult
                {
                    action = "runtime.ui.raycast",
                    targetId = EndpointId,
                    success = false,
                    error = "Requested target was not found."
                };
            }

            if (!TryResolveScreenPoint(args, target, out var point, out var pointSource, out var error))
            {
                return new RuntimeUiRaycastResult
                {
                    action = "runtime.ui.raycast",
                    targetId = EndpointId,
                    success = false,
                    error = error,
                    requestedTarget = BuildGameObjectInfo(target)
                };
            }

            if (!TryRaycast(point, out var hits, out error))
            {
                return new RuntimeUiRaycastResult
                {
                    action = "runtime.ui.raycast",
                    targetId = EndpointId,
                    success = false,
                    error = error,
                    point = BuildVector2Info(point),
                    pointSource = pointSource,
                    requestedTarget = BuildGameObjectInfo(target)
                };
            }

            var maxResults = ResolveMaxResults(args.maxResults, UiDefaultRaycastMaxResults);
            var truncated = false;
            if (hits.Count > maxResults)
            {
                hits.RemoveRange(maxResults, hits.Count - maxResults);
                truncated = true;
            }

            return new RuntimeUiRaycastResult
            {
                action = "runtime.ui.raycast",
                targetId = EndpointId,
                success = true,
                point = BuildVector2Info(point),
                pointSource = pointSource,
                requestedTarget = BuildGameObjectInfo(target),
                hitCount = hits.Count,
                truncated = truncated,
                hits = BuildRaycastSnapshots(hits),
                topHit = hits.Count > 0 ? BuildRaycastSnapshot(hits[0], 0) : null
            };
        }

        private RuntimeUiClickResult BuildUiClickResult(UnityMcpToolArguments args)
        {
            var target = ResolveOptionalTarget(args);
            if (HasRequestedTarget(args) && target == null)
            {
                return new RuntimeUiClickResult
                {
                    action = "runtime.ui.click",
                    targetId = EndpointId,
                    success = false,
                    error = "Requested target was not found."
                };
            }

            if (!TryResolveScreenPoint(args, target, out var point, out var pointSource, out var error))
            {
                return new RuntimeUiClickResult
                {
                    action = "runtime.ui.click",
                    targetId = EndpointId,
                    success = false,
                    error = error,
                    requestedTarget = BuildGameObjectInfo(target)
                };
            }

            var selectedBefore = GetSelectedGameObject();
            if (!TryClickAtScreenPoint(point, target, out var state, out error))
            {
                return new RuntimeUiClickResult
                {
                    action = "runtime.ui.click",
                    targetId = EndpointId,
                    success = false,
                    error = error,
                    point = BuildVector2Info(point),
                    pointSource = pointSource,
                    requestedTarget = BuildGameObjectInfo(target),
                    selectedBefore = BuildGameObjectInfo(selectedBefore)
                };
            }

            var selectedAfter = GetSelectedGameObject();
            return new RuntimeUiClickResult
            {
                action = "runtime.ui.click",
                targetId = EndpointId,
                success = true,
                point = BuildVector2Info(point),
                pointSource = pointSource,
                requestedTarget = BuildGameObjectInfo(target),
                selectedBefore = BuildGameObjectInfo(selectedBefore),
                selectedAfter = BuildGameObjectInfo(selectedAfter),
                hitCount = state.raycastResults.Count,
                hits = BuildRaycastSnapshots(state.raycastResults),
                raycastTarget = BuildGameObjectInfo(state.raycastTarget),
                pointerPress = BuildGameObjectInfo(state.pointerPress),
                clickHandler = BuildGameObjectInfo(state.clickHandler),
                clicked = state.pointerPress != null && state.clickHandler != null && state.pointerPress == state.clickHandler,
                topHit = state.raycastResults.Count > 0 ? BuildRaycastSnapshot(state.raycastResults[0], 0) : null
            };
        }

        private RuntimeScreenSnapshot BuildScreenSnapshot()
        {
            return new RuntimeScreenSnapshot
            {
                width = Screen.width,
                height = Screen.height,
                dpi = Screen.dpi,
                orientation = Screen.orientation.ToString(),
                coordinateOrigin = "bottom-left",
                safeArea = BuildRectInfo(Screen.safeArea)
            };
        }

        private RuntimeEventSystemSnapshot BuildEventSystemSnapshot()
        {
            var eventSystem = EventSystem.current;
            return new RuntimeEventSystemSnapshot
            {
                present = eventSystem != null,
                selected = BuildGameObjectInfo(GetSelectedGameObject()),
                currentInputModule = eventSystem != null && eventSystem.currentInputModule != null
                    ? eventSystem.currentInputModule.GetType().FullName
                    : null
            };
        }

        private RuntimeCanvasSnapshot[] BuildCanvasSnapshots(Dictionary<string, int> buttonCountsByCanvasPath, bool includeDisabled)
        {
            var canvases = FindSceneObjects<Canvas>(includeDisabled);
            var entries = new List<RuntimeCanvasSnapshot>();
            for (var i = 0; i < canvases.Length; i++)
            {
                var canvas = canvases[i];
                if (canvas == null || canvas.gameObject == null)
                {
                    continue;
                }

                if (!includeDisabled && (!canvas.enabled || !canvas.gameObject.activeInHierarchy))
                {
                    continue;
                }

                var path = BuildTransformPath(canvas.transform);
                entries.Add(new RuntimeCanvasSnapshot
                {
                    name = canvas.name,
                    path = path,
                    instanceId = canvas.gameObject.GetInstanceID(),
                    enabled = canvas.enabled,
                    activeInHierarchy = canvas.gameObject.activeInHierarchy,
                    isRootCanvas = canvas.isRootCanvas,
                    renderMode = canvas.renderMode.ToString(),
                    sortingLayerId = canvas.sortingLayerID,
                    sortingLayerName = SortingLayer.IDToName(canvas.sortingLayerID),
                    sortingOrder = canvas.sortingOrder,
                    overrideSorting = canvas.overrideSorting,
                    scaleFactor = canvas.scaleFactor,
                    referencePixelsPerUnit = canvas.referencePixelsPerUnit,
                    pixelRect = BuildRectInfo(canvas.pixelRect),
                    worldCamera = BuildGameObjectInfo(canvas.worldCamera == null ? null : canvas.worldCamera.gameObject),
                    buttonCount = buttonCountsByCanvasPath != null && buttonCountsByCanvasPath.TryGetValue(path, out var count) ? count : 0,
                    hasGraphicRaycaster = canvas.GetComponent<GraphicRaycaster>() != null
                });
            }

            entries.Sort(CompareCanvasSnapshots);
            return entries.ToArray();
        }

        private UiButtonSnapshotCollection CollectButtonSnapshots(string keyword, bool includeDisabled, int maxResults, bool includeRaycastDetails)
        {
            var buttons = FindSceneObjects<Button>(includeDisabled);
            var result = new UiButtonSnapshotCollection();
            var raycastEnabled = includeRaycastDetails && EventSystem.current != null;
            for (var i = 0; i < buttons.Length; i++)
            {
                var button = buttons[i];
                if (button == null || button.gameObject == null)
                {
                    continue;
                }

                if (!includeDisabled && (!button.gameObject.activeInHierarchy || !button.enabled || !button.IsInteractable()))
                {
                    continue;
                }

                var entry = CreateButtonSnapshot(button);
                if (!MatchesButtonKeyword(entry, keyword))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(entry.canvasPath))
                {
                    if (result.countsByCanvasPath.ContainsKey(entry.canvasPath))
                    {
                        result.countsByCanvasPath[entry.canvasPath] = result.countsByCanvasPath[entry.canvasPath] + 1;
                    }
                    else
                    {
                        result.countsByCanvasPath.Add(entry.canvasPath, 1);
                    }
                }

                if (raycastEnabled && entry.screenPointAvailable)
                {
                    if (TryRaycast(new Vector2(entry.clickPoint.x, entry.clickPoint.y), out var hits, out var raycastError))
                    {
                        entry.raycastAvailable = true;
                        entry.raycastCount = hits.Count;
                        entry.raycastIndex = IndexOfGameObjectInRaycast(button.gameObject, hits);
                        entry.raycastTarget = hits.Count > 0 ? BuildGameObjectInfo(hits[0].gameObject) : null;
                        entry.topmost = entry.raycastIndex == 0;
                    }
                    else
                    {
                        entry.raycastError = raycastError;
                    }
                }

                entry.clickable = button.IsInteractable()
                    && entry.screenPointAvailable
                    && (!entry.raycastAvailable || entry.raycastIndex == 0);
                result.entries.Add(entry);
                result.totalCount++;
            }

            result.entries.Sort(CompareButtonSnapshots);
            if (result.entries.Count > maxResults)
            {
                result.truncated = true;
                result.entries.RemoveRange(maxResults, result.entries.Count - maxResults);
            }

            return result;
        }

        private RuntimeButtonSnapshot CreateButtonSnapshot(Button button)
        {
            var entry = new RuntimeButtonSnapshot
            {
                name = button.name,
                label = ExtractButtonLabel(button),
                path = BuildTransformPath(button.transform),
                instanceId = button.gameObject.GetInstanceID(),
                activeInHierarchy = button.gameObject.activeInHierarchy,
                enabled = button.enabled,
                interactable = button.IsInteractable(),
                raycastIndex = -1
            };

            var canvas = button.GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                entry.canvasPath = BuildTransformPath(canvas.transform);
                entry.canvasName = canvas.name;
                entry.canvasSortingOrder = canvas.sortingOrder;
                entry.canvasRenderMode = canvas.renderMode.ToString();
            }

            if (TryGetScreenMetrics(button.transform as RectTransform, out var screenPoint, out var screenRect, out var error))
            {
                entry.screenPointAvailable = true;
                entry.clickPoint = BuildVector2Info(screenPoint);
                entry.screenRect = BuildRectInfo(screenRect);
                entry.screenRectAvailable = true;
            }
            else
            {
                entry.screenMetricError = error;
            }

            entry.clickable = button.IsInteractable() && entry.screenPointAvailable;
            return entry;
        }

        private static bool TryResolveScreenPoint(UnityMcpToolArguments args, GameObject target, out Vector2 screenPoint, out string pointSource, out string error)
        {
            screenPoint = Vector2.zero;
            pointSource = null;
            error = null;

            if (target != null)
            {
                if (TryResolveTargetScreenPoint(target, out screenPoint, out pointSource, out error))
                {
                    return true;
                }
            }

            screenPoint = new Vector2(args.x, args.y);
            pointSource = "screen";
            return true;
        }

        private static bool TryResolveTargetScreenPoint(GameObject target, out Vector2 screenPoint, out string pointSource, out string error)
        {
            screenPoint = Vector2.zero;
            pointSource = null;
            error = null;

            if (target == null)
            {
                error = "Missing target.";
                return false;
            }

            if (TryGetScreenMetrics(target.transform as RectTransform, out screenPoint, out var screenRect, out error))
            {
                pointSource = "target";
                return true;
            }

            var renderer = target.GetComponentInChildren<Renderer>();
            if (renderer != null && TryWorldToScreenPoint(renderer.bounds.center, out screenPoint, out error))
            {
                pointSource = "renderer";
                return true;
            }

            var collider = target.GetComponentInChildren<Collider>();
            if (collider != null && TryWorldToScreenPoint(collider.bounds.center, out screenPoint, out error))
            {
                pointSource = "collider";
                return true;
            }

            if (TryWorldToScreenPoint(target.transform.position, out screenPoint, out error))
            {
                pointSource = "transform";
                return true;
            }

            if (string.IsNullOrEmpty(error))
            {
                error = "Unable to project target to screen coordinates.";
            }

            return false;
        }

        private static bool TryClickAtScreenPoint(Vector2 screenPoint, GameObject fallbackTarget, out UiPointerPressState state, out string error)
        {
            state = null;
            error = null;

            if (!TryRaycast(screenPoint, out var hits, out error))
            {
                return false;
            }

            var raycastTarget = GetFirstRaycastTarget(hits);
            var eventTarget = raycastTarget != null ? raycastTarget : fallbackTarget;
            if (eventTarget == null)
            {
                error = "No EventSystem raycast target found at the requested screen position.";
                return false;
            }

            var data = CreatePointerEventData(screenPoint);
            if (hits.Count > 0)
            {
                data.pointerCurrentRaycast = hits[0];
                data.pointerPressRaycast = hits[0];
            }

            data.rawPointerPress = eventTarget;
            data.pressPosition = screenPoint;
            data.position = screenPoint;
            data.eligibleForClick = true;
            data.clickTime = Time.unscaledTime;
            data.clickCount = 1;
            data.useDragThreshold = true;
            data.pointerEnter = eventTarget;

            var selected = ExecuteEvents.GetEventHandler<ISelectHandler>(eventTarget);
            if (selected != null && EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(selected, data);
            }

            var pointerPress = ExecuteEvents.ExecuteHierarchy(eventTarget, data, ExecuteEvents.pointerDownHandler);
            if (pointerPress == null)
            {
                pointerPress = ExecuteEvents.GetEventHandler<IPointerClickHandler>(eventTarget);
            }

            var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(eventTarget);
            if (pointerPress == null && clickHandler == null)
            {
                error = "No pointer click handler found for target.";
                return false;
            }

            data.pointerPress = pointerPress;
            state = new UiPointerPressState
            {
                data = data,
                raycastTarget = raycastTarget,
                pointerPress = pointerPress,
                clickHandler = clickHandler,
                raycastResults = hits,
                screenPoint = screenPoint
            };

            FinishUiClick(state);
            return true;
        }

        private static void FinishUiClick(UiPointerPressState state)
        {
            if (state == null || state.data == null)
            {
                return;
            }

            if (state.pointerPress != null)
            {
                ExecuteEvents.Execute(state.pointerPress, state.data, ExecuteEvents.pointerUpHandler);
            }

            if (state.pointerPress != null
                && state.clickHandler != null
                && state.pointerPress == state.clickHandler
                && state.data.eligibleForClick)
            {
                ExecuteEvents.Execute(state.pointerPress, state.data, ExecuteEvents.pointerClickHandler);
            }

            state.data.eligibleForClick = false;
            state.data.pointerPress = null;
            state.data.rawPointerPress = null;
            state.data.dragging = false;
        }

        private static bool TryRaycast(Vector2 screenPoint, out List<RaycastResult> hits, out string error)
        {
            hits = new List<RaycastResult>();
            error = null;

            if (EventSystem.current == null)
            {
                error = "runtime.ui.raycast requires an active EventSystem.";
                return false;
            }

            var data = CreatePointerEventData(screenPoint);
            EventSystem.current.RaycastAll(data, hits);
            return true;
        }

        private static PointerEventData CreatePointerEventData(Vector2 screenPoint)
        {
            return new PointerEventData(EventSystem.current)
            {
                pointerId = -1,
                button = PointerEventData.InputButton.Left,
                position = screenPoint,
                delta = Vector2.zero
            };
        }

        private static bool TryGetScreenMetrics(RectTransform rectTransform, out Vector2 screenPoint, out Rect screenRect, out string error)
        {
            screenPoint = Vector2.zero;
            screenRect = new Rect();
            error = null;

            if (rectTransform == null)
            {
                error = "Missing RectTransform.";
                return false;
            }

            var canvas = rectTransform.GetComponentInParent<Canvas>();
            var camera = GetCanvasCamera(canvas);
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay && camera == null)
            {
                error = "Unable to resolve camera for world-space UI.";
                return false;
            }

            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            var minX = float.MaxValue;
            var minY = float.MaxValue;
            var maxX = float.MinValue;
            var maxY = float.MinValue;

            for (var i = 0; i < corners.Length; i++)
            {
                var projected = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
                minX = Mathf.Min(minX, projected.x);
                minY = Mathf.Min(minY, projected.y);
                maxX = Mathf.Max(maxX, projected.x);
                maxY = Mathf.Max(maxY, projected.y);
            }

            screenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            screenPoint = screenRect.center;
            return true;
        }

        private static bool TryWorldToScreenPoint(Vector3 worldPosition, out Vector2 screenPoint, out string error)
        {
            screenPoint = Vector2.zero;
            error = null;

            var camera = Camera.main;
            if (camera == null)
            {
                error = "Camera.main is required to project non-UI targets.";
                return false;
            }

            var projected = camera.WorldToScreenPoint(worldPosition);
            if (projected.z < 0f)
            {
                error = "Target is behind Camera.main.";
                return false;
            }

            screenPoint = new Vector2(projected.x, projected.y);
            return true;
        }

        private static RuntimeRaycastSnapshot[] BuildRaycastSnapshots(List<RaycastResult> hits)
        {
            var snapshots = new RuntimeRaycastSnapshot[hits == null ? 0 : hits.Count];
            if (hits == null)
            {
                return snapshots;
            }

            for (var i = 0; i < hits.Count; i++)
            {
                snapshots[i] = BuildRaycastSnapshot(hits[i], i);
            }

            return snapshots;
        }

        private static RuntimeRaycastSnapshot BuildRaycastSnapshot(RaycastResult hit, int index)
        {
            var moduleGameObject = hit.module != null ? hit.module.gameObject : null;
            return new RuntimeRaycastSnapshot
            {
                index = index,
                gameObject = BuildGameObjectInfo(hit.gameObject),
                module = hit.module != null ? hit.module.GetType().FullName : null,
                moduleObject = BuildGameObjectInfo(moduleGameObject),
                distance = hit.distance,
                depth = hit.depth,
                sortingLayer = hit.sortingLayer,
                sortingOrder = hit.sortingOrder,
                displayIndex = hit.displayIndex,
                screenPosition = BuildVector2Info(hit.screenPosition),
                worldPosition = BuildVector3Info(hit.worldPosition),
                worldNormal = BuildVector3Info(hit.worldNormal),
                isTopmost = index == 0
            };
        }

        private static RuntimeGameObjectInfo BuildGameObjectInfo(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            return new RuntimeGameObjectInfo
            {
                name = go.name,
                path = BuildTransformPath(go.transform),
                instanceId = go.GetInstanceID(),
                activeInHierarchy = go.activeInHierarchy,
                layer = go.layer,
                tag = SafeTag(go)
            };
        }

        private static RuntimeRectInfo BuildRectInfo(Rect rect)
        {
            return new RuntimeRectInfo
            {
                x = rect.x,
                y = rect.y,
                width = rect.width,
                height = rect.height,
                xMin = rect.xMin,
                yMin = rect.yMin,
                xMax = rect.xMax,
                yMax = rect.yMax,
                center = BuildVector2Info(rect.center)
            };
        }

        private static RuntimeVector2Info BuildVector2Info(Vector2 value)
        {
            return new RuntimeVector2Info { x = value.x, y = value.y };
        }

        private static RuntimeVector3Info BuildVector3Info(Vector3 value)
        {
            return new RuntimeVector3Info { x = value.x, y = value.y, z = value.z };
        }

        private static GameObject ResolveOptionalTarget(UnityMcpToolArguments args)
        {
            if (!string.IsNullOrEmpty(args.path))
            {
                var found = GameObject.Find(args.path);
                if (found != null)
                {
                    return found;
                }

                return FindGameObjectByPath(args.path);
            }

            if (args.instanceId != 0)
            {
                return FindGameObjectByInstanceId(args.instanceId);
            }

            return null;
        }

        private static GameObject FindGameObjectByPath(string path)
        {
            var objects = FindSceneObjects<GameObject>(true);
            for (var i = 0; i < objects.Length; i++)
            {
                var go = objects[i];
                if (go != null && string.Equals(BuildTransformPath(go.transform), path, StringComparison.Ordinal))
                {
                    return go;
                }
            }

            return null;
        }

        private static GameObject FindGameObjectByInstanceId(int instanceId)
        {
            var objects = FindSceneObjects<GameObject>(true);
            for (var i = 0; i < objects.Length; i++)
            {
                if (objects[i] != null && objects[i].GetInstanceID() == instanceId)
                {
                    return objects[i];
                }
            }

            return null;
        }

        private static T[] FindSceneObjects<T>(bool includeInactive) where T : UnityEngine.Object
        {
            if (!includeInactive)
            {
                return UnityEngine.Object.FindObjectsOfType<T>();
            }

            var all = Resources.FindObjectsOfTypeAll<T>();
            var result = new List<T>(all == null ? 0 : all.Length);
            if (all == null)
            {
                return result.ToArray();
            }

            for (var i = 0; i < all.Length; i++)
            {
                var item = all[i];
                if (IsSceneObject(item))
                {
                    result.Add(item);
                }
            }

            return result.ToArray();
        }

        private static bool IsSceneObject(UnityEngine.Object item)
        {
            if (item == null)
            {
                return false;
            }

            GameObject go = null;
            var component = item as Component;
            if (component != null)
            {
                go = component.gameObject;
            }
            else
            {
                go = item as GameObject;
            }

            if (go == null)
            {
                return false;
            }

            var scene = go.scene;
            return scene.IsValid() && scene.isLoaded;
        }

        private static bool MatchesButtonKeyword(RuntimeButtonSnapshot entry, string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return true;
            }

            return ContainsText(entry.path, keyword)
                || ContainsText(entry.label, keyword)
                || ContainsText(entry.name, keyword)
                || ContainsText(entry.canvasPath, keyword)
                || ContainsText(entry.canvasName, keyword);
        }

        private static bool ContainsText(string value, string keyword)
        {
            return !string.IsNullOrEmpty(value)
                && !string.IsNullOrEmpty(keyword)
                && value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int CountClickableButtons(List<RuntimeButtonSnapshot> entries)
        {
            var count = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].clickable)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CompareButtonSnapshots(RuntimeButtonSnapshot left, RuntimeButtonSnapshot right)
        {
            if (left == null && right == null)
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            var clickableCompare = right.clickable.CompareTo(left.clickable);
            if (clickableCompare != 0)
            {
                return clickableCompare;
            }

            var orderCompare = right.canvasSortingOrder.CompareTo(left.canvasSortingOrder);
            if (orderCompare != 0)
            {
                return orderCompare;
            }

            if (left.screenPointAvailable && right.screenPointAvailable)
            {
                var yCompare = right.clickPoint.y.CompareTo(left.clickPoint.y);
                if (yCompare != 0)
                {
                    return yCompare;
                }

                var xCompare = left.clickPoint.x.CompareTo(right.clickPoint.x);
                if (xCompare != 0)
                {
                    return xCompare;
                }
            }

            return string.CompareOrdinal(left.path, right.path);
        }

        private static int CompareCanvasSnapshots(RuntimeCanvasSnapshot left, RuntimeCanvasSnapshot right)
        {
            if (left == null && right == null)
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            var orderCompare = right.sortingOrder.CompareTo(left.sortingOrder);
            if (orderCompare != 0)
            {
                return orderCompare;
            }

            return string.CompareOrdinal(left.path, right.path);
        }

        private static int IndexOfGameObjectInRaycast(GameObject target, List<RaycastResult> hits)
        {
            if (target == null || hits == null)
            {
                return -1;
            }

            for (var i = 0; i < hits.Count; i++)
            {
                if (hits[i].gameObject == target)
                {
                    return i;
                }
            }

            return -1;
        }

        private static GameObject GetFirstRaycastTarget(List<RaycastResult> hits)
        {
            if (hits == null)
            {
                return null;
            }

            for (var i = 0; i < hits.Count; i++)
            {
                if (hits[i].gameObject != null)
                {
                    return hits[i].gameObject;
                }
            }

            return null;
        }

        private static GameObject GetSelectedGameObject()
        {
            return EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        }

        private static Camera GetCanvasCamera(Canvas canvas)
        {
            if (canvas == null)
            {
                return Camera.main;
            }

            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                return null;
            }

            if (canvas.worldCamera != null)
            {
                return canvas.worldCamera;
            }

            return Camera.main;
        }

        private static string ExtractButtonLabel(Button button)
        {
            if (button == null)
            {
                return null;
            }

            var texts = button.GetComponentsInChildren<Text>(true);
            for (var i = 0; texts != null && i < texts.Length; i++)
            {
                if (texts[i] != null && !string.IsNullOrWhiteSpace(texts[i].text))
                {
                    return texts[i].text.Trim();
                }
            }

            var components = button.GetComponentsInChildren<Component>(true);
            for (var i = 0; components != null && i < components.Length; i++)
            {
                var value = TryReadTmpText(components[i]);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return button.name;
        }

        private static string TryReadTmpText(Component component)
        {
            if (component == null)
            {
                return null;
            }

            var type = component.GetType();
            while (type != null)
            {
                if (type.FullName == "TMPro.TMP_Text" || type.Name.Contains("TMP_Text"))
                {
                    var property = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                    return property == null ? null : property.GetValue(component, null) as string;
                }

                type = type.BaseType;
            }

            return null;
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

        private static int ResolveMaxResults(int requested, int fallback)
        {
            return Math.Max(1, requested <= 0 ? fallback : requested);
        }

        private static bool HasRequestedTarget(UnityMcpToolArguments args)
        {
            return args != null && (!string.IsNullOrEmpty(args.path) || args.instanceId != 0);
        }

        [Serializable]
        private sealed class RuntimeLogsResult
        {
            public string action;
            public string targetId;
            public int totalCount;
            public int returnedCount;
            public int count;
            public string logType;
            public string regex;
            public bool includeStackTrace;
            public UnityMcpRuntimeLogEntry[] entries;
        }

        [Serializable]
        private sealed class RuntimeLogsClearResult
        {
            public string action;
            public string targetId;
            public int clearedCount;
        }

        [Serializable]
        private sealed class RuntimeUiSnapshotResult
        {
            public string action;
            public string targetId;
            public RuntimeScreenSnapshot screen;
            public RuntimeEventSystemSnapshot eventSystem;
            public RuntimeGameObjectInfo selected;
            public RuntimeCanvasSnapshot[] canvases;
            public RuntimeButtonSnapshot[] buttons;
            public RuntimeUiSnapshotSummary summary;
        }

        [Serializable]
        private sealed class RuntimeUiFindResult
        {
            public string action;
            public string targetId;
            public string keyword;
            public RuntimeButtonSnapshot[] buttons;
            public RuntimeUiFindSummary summary;
        }

        [Serializable]
        private sealed class RuntimeUiRaycastResult
        {
            public string action;
            public string targetId;
            public bool success;
            public string error;
            public RuntimeVector2Info point;
            public string pointSource;
            public RuntimeGameObjectInfo requestedTarget;
            public int hitCount;
            public bool truncated;
            public RuntimeRaycastSnapshot[] hits;
            public RuntimeRaycastSnapshot topHit;
        }

        [Serializable]
        private sealed class RuntimeUiClickResult
        {
            public string action;
            public string targetId;
            public bool success;
            public string error;
            public RuntimeVector2Info point;
            public string pointSource;
            public RuntimeGameObjectInfo requestedTarget;
            public RuntimeGameObjectInfo selectedBefore;
            public RuntimeGameObjectInfo selectedAfter;
            public int hitCount;
            public RuntimeRaycastSnapshot[] hits;
            public RuntimeGameObjectInfo raycastTarget;
            public RuntimeGameObjectInfo pointerPress;
            public RuntimeGameObjectInfo clickHandler;
            public bool clicked;
            public RuntimeRaycastSnapshot topHit;
        }

        [Serializable]
        private sealed class RuntimeUiSnapshotSummary
        {
            public int canvasCount;
            public int buttonCount;
            public int returnedButtonCount;
            public int clickableButtonCount;
            public bool truncated;
            public bool hasEventSystem;
        }

        [Serializable]
        private sealed class RuntimeUiFindSummary
        {
            public int matchCount;
            public int returnedCount;
            public int clickableCount;
            public bool truncated;
        }

        [Serializable]
        private sealed class RuntimeScreenSnapshot
        {
            public int width;
            public int height;
            public float dpi;
            public string orientation;
            public string coordinateOrigin;
            public RuntimeRectInfo safeArea;
        }

        [Serializable]
        private sealed class RuntimeEventSystemSnapshot
        {
            public bool present;
            public RuntimeGameObjectInfo selected;
            public string currentInputModule;
        }

        [Serializable]
        private sealed class RuntimeCanvasSnapshot
        {
            public string name;
            public string path;
            public int instanceId;
            public bool enabled;
            public bool activeInHierarchy;
            public bool isRootCanvas;
            public string renderMode;
            public int sortingLayerId;
            public string sortingLayerName;
            public int sortingOrder;
            public bool overrideSorting;
            public float scaleFactor;
            public float referencePixelsPerUnit;
            public RuntimeRectInfo pixelRect;
            public RuntimeGameObjectInfo worldCamera;
            public int buttonCount;
            public bool hasGraphicRaycaster;
        }

        [Serializable]
        private sealed class RuntimeButtonSnapshot
        {
            public string name;
            public string label;
            public string path;
            public int instanceId;
            public bool activeInHierarchy;
            public bool enabled;
            public bool interactable;
            public bool clickable;
            public bool screenPointAvailable;
            public RuntimeVector2Info clickPoint;
            public bool screenRectAvailable;
            public RuntimeRectInfo screenRect;
            public string screenMetricError;
            public bool raycastAvailable;
            public int raycastIndex;
            public int raycastCount;
            public string raycastError;
            public RuntimeGameObjectInfo raycastTarget;
            public bool topmost;
            public string canvasPath;
            public string canvasName;
            public int canvasSortingOrder;
            public string canvasRenderMode;
        }

        [Serializable]
        private sealed class RuntimeRaycastSnapshot
        {
            public int index;
            public RuntimeGameObjectInfo gameObject;
            public string module;
            public RuntimeGameObjectInfo moduleObject;
            public float distance;
            public int depth;
            public int sortingLayer;
            public int sortingOrder;
            public int displayIndex;
            public RuntimeVector2Info screenPosition;
            public RuntimeVector3Info worldPosition;
            public RuntimeVector3Info worldNormal;
            public bool isTopmost;
        }

        [Serializable]
        private sealed class RuntimeGameObjectInfo
        {
            public string name;
            public string path;
            public int instanceId;
            public bool activeInHierarchy;
            public int layer;
            public string tag;
        }

        [Serializable]
        private sealed class RuntimeRectInfo
        {
            public float x;
            public float y;
            public float width;
            public float height;
            public float xMin;
            public float yMin;
            public float xMax;
            public float yMax;
            public RuntimeVector2Info center;
        }

        [Serializable]
        private sealed class RuntimeVector2Info
        {
            public float x;
            public float y;
        }

        [Serializable]
        private sealed class RuntimeVector3Info
        {
            public float x;
            public float y;
            public float z;
        }

        private sealed class UiButtonSnapshotCollection
        {
            public readonly List<RuntimeButtonSnapshot> entries = new List<RuntimeButtonSnapshot>();
            public readonly Dictionary<string, int> countsByCanvasPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public int totalCount;
            public bool truncated;
        }

        private sealed class UiPointerPressState
        {
            public PointerEventData data;
            public GameObject raycastTarget;
            public GameObject pointerPress;
            public GameObject clickHandler;
            public List<RaycastResult> raycastResults;
            public Vector2 screenPoint;
        }
    }
}
