using System;
using System.Collections.Generic;
using System.Text;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Il2CppSpacewood.Unity.UI;

namespace SuperAutoAccessibility
{
    /// <summary>
    /// Interactive UI inspector tool toggled with F7.
    /// Silently highlights UI elements under the mouse cursor with an outline.
    /// Middle-click cycles between overlapping elements.
    /// Right-click dumps detailed element info to console and clipboard (like scene change dumps).
    /// Does NOT speak/announce anything while moving the mouse â€” purely visual + right-click info.
    ///
    /// Uses a dedicated overlay Canvas (sortingOrder=32000) so highlight borders are always on top.
    /// Places a full-screen invisible raycast blocker to prevent middle/right clicks from
    /// reaching game UI underneath.
    /// </summary>
    public static class UIInspector
    {
        private static bool _active = false;
        private static List<GameObject> _elementsUnderCursor = new List<GameObject>();
        private static int _cycleIndex = 0;
        private static Vector3 _lastMousePos = Vector3.zero;

        // Dedicated overlay canvas for highlight borders (always on top)
        private static GameObject _overlayCanvasGO = null;
        private static Canvas _overlayCanvas = null;

        // Highlight outline (4 border images forming a rectangular frame)
        private static GameObject _highlightRoot = null;
        private static GameObject[] _borderObjects = null;

        // Full-screen invisible blocker to eat mouse clicks
        private static GameObject _blockerGO = null;

        // Track the currently highlighted element to avoid redundant updates
        private static GameObject _currentHighlightedElement = null;

        public static bool IsActive => _active;

        /// <summary>
        /// Toggle the UI inspector on/off.
        /// </summary>
        public static void Toggle()
        {
            _active = !_active;
            if (_active)
            {
                EnsureOverlayCanvas();
                if (_blockerGO != null) _blockerGO.SetActive(true);
                AccessibilityManager.Announce("UI Inspector enabled. Move mouse over elements. Middle click to cycle, right click to copy info.");
            }
            else
            {
                AccessibilityManager.Announce("UI Inspector disabled");
                ClearHighlight();
                if (_blockerGO != null) _blockerGO.SetActive(false);
                _currentHighlightedElement = null;
            }
        }

        /// <summary>
        /// Called each frame when active. Handles mouse-based UI inspection.
        /// We do our own raycasting BEFORE the blocker intercepts clicks,
        /// by temporarily disabling the blocker during raycast then re-enabling.
        /// </summary>
        public static void Update()
        {
            if (!_active) return;

            Vector3 mousePos = Input.mousePosition;

            // Detect mouse movement â€” refresh elements under cursor
            if (Vector3.Distance(mousePos, _lastMousePos) > 0.5f)
            {
                _lastMousePos = mousePos;
                UpdateElementsUnderCursor(mousePos);
                _cycleIndex = 0;

                if (_elementsUnderCursor.Count > 0)
                {
                    var newElement = _elementsUnderCursor[_cycleIndex];
                    if (newElement != _currentHighlightedElement)
                    {
                        _currentHighlightedElement = newElement;
                        HighlightCurrentElement();
                    }
                }
                else
                {
                    _currentHighlightedElement = null;
                    ClearHighlight();
                }
            }

            // Middle-click: cycle through overlapping elements (silent â€” no announce)
            if (Input.GetMouseButtonDown(2))
            {
                if (_elementsUnderCursor.Count > 1)
                {
                    _cycleIndex = (_cycleIndex + 1) % _elementsUnderCursor.Count;
                    _currentHighlightedElement = _elementsUnderCursor[_cycleIndex];
                    HighlightCurrentElement();
                }
            }

            // Right-click: dump detailed element info to console + clipboard
            if (Input.GetMouseButtonDown(1))
            {
                DumpElementInfo();
            }
        }

        /// <summary>
        /// Finds all UI elements under the given screen position using EventSystem raycasting.
        /// Temporarily disables the blocker so it doesn't interfere with our raycast.
        /// </summary>
        private static void UpdateElementsUnderCursor(Vector3 screenPos)
        {
            _elementsUnderCursor.Clear();

            try
            {
                if (EventSystem.current == null) return;

                // Temporarily disable blocker for raycasting
                bool blockerWasActive = false;
                if (_blockerGO != null)
                {
                    blockerWasActive = _blockerGO.activeSelf;
                    _blockerGO.SetActive(false);
                }

                try
                {
                    var pointerData = new PointerEventData(EventSystem.current);
                    pointerData.position = new Vector2(screenPos.x, screenPos.y);

                    // Raycast through all GraphicRaycasters in the scene
                    var raycasters = UnityEngine.Object.FindObjectsOfType<GraphicRaycaster>();

                    foreach (var raycaster in raycasters)
                    {
                        if (raycaster == null) continue;

                        // Skip our overlay canvas raycaster
                        if (_overlayCanvasGO != null && raycaster.gameObject == _overlayCanvasGO) continue;

                        try
                        {
                            var localResults = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
                            raycaster.Raycast(pointerData, localResults);

                            for (int i = 0; i < localResults.Count; i++)
                            {
                                try
                                {
                                    var result = localResults[i];
                                    if (result.gameObject != null && result.gameObject.activeInHierarchy)
                                    {
                                        // Skip the inspector's own objects
                                        if (result.gameObject.name.StartsWith("UIInspector_")) continue;

                                        // Avoid duplicates
                                        if (!_elementsUnderCursor.Contains(result.gameObject))
                                            _elementsUnderCursor.Add(result.gameObject);
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
                finally
                {
                    // Re-enable blocker
                    if (_blockerGO != null && blockerWasActive)
                        _blockerGO.SetActive(true);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"UIInspector raycast error: {ex.Message}");
            }
        }

        /// <summary>
        /// Dumps detailed info about the currently highlighted element to console and clipboard.
        /// Uses the same format as UIElementDumper for consistency.
        /// </summary>
        private static void DumpElementInfo()
        {
            if (_elementsUnderCursor.Count == 0)
            {
                AccessibilityManager.Announce("No element under cursor");
                return;
            }

            if (_cycleIndex >= _elementsUnderCursor.Count) _cycleIndex = 0;
            var go = _elementsUnderCursor[_cycleIndex];
            if (go == null) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"=== UI INSPECTOR: {go.name} ===");

                // Hierarchy path
                string path = GetHierarchyPath(go.transform);
                sb.AppendLine($"Path: {path}");

                // Active / interactable state
                sb.AppendLine($"Active: {go.activeInHierarchy}");

                // SelectableBase info
                try
                {
                    var selectable = go.GetComponent<SelectableBase>();
                    if (selectable != null)
                    {
                        string type = "Selectable";
                        if (go.GetComponent<ButtonBase>() != null) type = "Button";
                        else if (go.GetComponent<SliderBase>() != null) type = "Slider";
                        else if (go.GetComponent<DropdownBase>() != null) type = "Dropdown";
                        else if (go.GetComponent<InputFieldBase>() != null) type = "InputField";
                        bool interactable = false;
                        try { interactable = selectable.GetInteractable(); } catch { }
                        sb.AppendLine($"Type: {type} | Interactable: {interactable}");
                    }
                }
                catch { }

                // Text content (this element and children)
                try
                {
                    string text = TextExtractor.GetElementText(go);
                    if (!string.IsNullOrWhiteSpace(text))
                        sb.AppendLine($"Text: {text.Trim()}");
                }
                catch { }

                // TMP text children
                try
                {
                    var tmpTexts = go.GetComponentsInChildren<Il2CppTMPro.TextMeshProUGUI>();
                    if (tmpTexts != null && tmpTexts.Count > 0)
                    {
                        for (int i = 0; i < tmpTexts.Count; i++)
                        {
                            try
                            {
                                var tmp = tmpTexts[i];
                                if (tmp != null && !string.IsNullOrWhiteSpace(tmp.text))
                                    sb.AppendLine($"  TMP[{i}]: \"{tmp.text.Trim()}\"");
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // Position and size
                try
                {
                    var rt = go.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        sb.AppendLine($"Position: {rt.position}");
                        sb.AppendLine($"Size: {rt.rect.width:F0}x{rt.rect.height:F0}");
                    }
                }
                catch { }

                // Components list
                try
                {
                    var components = go.GetComponents<Component>();
                    if (components != null)
                    {
                        var compNames = new List<string>();
                        foreach (var comp in components)
                        {
                            try
                            {
                                if (comp != null)
                                    compNames.Add(comp.GetIl2CppType().Name);
                            }
                            catch { }
                        }
                        sb.AppendLine($"Components: {string.Join(", ", compNames)}");
                    }
                }
                catch { }

                // Children count
                try
                {
                    sb.AppendLine($"Children: {go.transform.childCount}");
                }
                catch { }

                // Also list siblings (other elements at this position)
                if (_elementsUnderCursor.Count > 1)
                {
                    sb.AppendLine($"--- Overlapping elements ({_elementsUnderCursor.Count} total) ---");
                    for (int i = 0; i < _elementsUnderCursor.Count; i++)
                    {
                        try
                        {
                            var sibling = _elementsUnderCursor[i];
                            string marker = i == _cycleIndex ? " [SELECTED]" : "";
                            string sibText = TextExtractor.GetElementText(sibling);
                            string sibPath = GetShortPath(sibling);
                            sb.AppendLine($"  [{i + 1}] {sibPath}{(string.IsNullOrWhiteSpace(sibText) ? "" : $" | \"{sibText}\"")}{marker}");
                        }
                        catch { }
                    }
                }

                sb.AppendLine("===");

                string info = sb.ToString();
                MelonLogger.Msg(info);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"UIInspector dump error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the full hierarchy path of a Transform.
        /// </summary>
        private static string GetHierarchyPath(Transform t)
        {
            var parts = new List<string>();
            while (t != null)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        /// <summary>
        /// Gets a short path: grandparent/parent/name (3 levels max).
        /// </summary>
        private static string GetShortPath(GameObject go)
        {
            if (go == null) return "null";
            string path = go.name;
            Transform parent = go.transform.parent;
            if (parent != null)
            {
                path = parent.name + "/" + path;
                Transform grandparent = parent.parent;
                if (grandparent != null)
                    path = grandparent.name + "/" + path;
            }
            return path;
        }

        /// <summary>
        /// Highlights the current element with a proper rectangular border outline.
        /// Uses 4 thin Image rectangles forming a frame (no fill).
        /// Borders live on a dedicated overlay Canvas so they render on top of everything.
        /// </summary>
        private static void HighlightCurrentElement()
        {
            if (_elementsUnderCursor.Count == 0 || _cycleIndex >= _elementsUnderCursor.Count)
            {
                ClearHighlight();
                return;
            }

            var go = _elementsUnderCursor[_cycleIndex];
            if (go == null)
            {
                ClearHighlight();
                return;
            }

            var targetRect = go.GetComponent<RectTransform>();
            if (targetRect == null)
            {
                ClearHighlight();
                return;
            }

            try
            {
                EnsureHighlightObjects();

                if (_highlightRoot == null || _borderObjects == null) return;

                // Get the target's world corners
                Vector3[] corners = new Vector3[4];
                targetRect.GetWorldCorners(corners);
                // corners: 0=bottom-left, 1=top-left, 2=top-right, 3=bottom-right

                // Convert world corners to screen coordinates
                Camera cam = null;
                try
                {
                    // Find the camera rendering the target's canvas
                    var targetCanvas = go.GetComponentInParent<Canvas>();
                    if (targetCanvas != null && targetCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
                        cam = targetCanvas.worldCamera;
                }
                catch { }

                float left, bottom, right, top;
                if (cam != null)
                {
                    // Convert world position to screen coords via the target's camera
                    Vector3 screenBL = cam.WorldToScreenPoint(corners[0]);
                    Vector3 screenTR = cam.WorldToScreenPoint(corners[2]);
                    left = screenBL.x;
                    bottom = screenBL.y;
                    right = screenTR.x;
                    top = screenTR.y;
                }
                else
                {
                    // ScreenSpaceOverlay: world coords are already screen coords
                    left = corners[0].x;
                    bottom = corners[0].y;
                    right = corners[2].x;
                    top = corners[2].y;
                }

                float width = right - left;
                float height = top - bottom;

                const float borderThickness = 3f;

                // Position each border (in screen-space pixel coords, since our overlay is ScreenSpaceOverlay)
                // Top border
                SetBorderRect(_borderObjects[0], left, top - borderThickness, width, borderThickness);
                // Bottom border
                SetBorderRect(_borderObjects[1], left, bottom, width, borderThickness);
                // Left border
                SetBorderRect(_borderObjects[2], left, bottom, borderThickness, height);
                // Right border
                SetBorderRect(_borderObjects[3], right - borderThickness, bottom, borderThickness, height);

                _highlightRoot.SetActive(true);
                for (int i = 0; i < 4; i++)
                {
                    if (_borderObjects[i] != null) _borderObjects[i].SetActive(true);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"UIInspector highlight error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets a border rect's position and size in screen space.
        /// Since our overlay Canvas is ScreenSpaceOverlay with pixelPerfect,
        /// screen coords map 1:1 to the rect positions.
        /// </summary>
        private static void SetBorderRect(GameObject border, float x, float y, float w, float h)
        {
            if (border == null) return;
            var rt = border.GetComponent<RectTransform>();
            if (rt == null) return;

            rt.anchoredPosition = new Vector2(x + w / 2f, y + h / 2f);
            rt.sizeDelta = new Vector2(w, h);
            border.SetActive(true);
        }

        /// <summary>
        /// Creates a dedicated overlay Canvas that renders on top of everything.
        /// This canvas has a very high sortingOrder (32000) and ScreenSpaceOverlay mode.
        /// </summary>
        private static void EnsureOverlayCanvas()
        {
            if (_overlayCanvasGO != null)
            {
                try { var _ = _overlayCanvasGO.name; }
                catch { _overlayCanvasGO = null; _overlayCanvas = null; _highlightRoot = null; _borderObjects = null; _blockerGO = null; }
            }

            if (_overlayCanvasGO == null)
            {
                _overlayCanvasGO = new GameObject("UIInspector_OverlayCanvas");
                UnityEngine.Object.DontDestroyOnLoad(_overlayCanvasGO);

                _overlayCanvas = _overlayCanvasGO.AddComponent<Canvas>();
                _overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _overlayCanvas.sortingOrder = 32000; // Very high â€” always on top

                var scaler = _overlayCanvasGO.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

                // Add GraphicRaycaster so the blocker can intercept clicks
                _overlayCanvasGO.AddComponent<GraphicRaycaster>();

                // Create full-screen blocker image (invisible, but catches raycast)
                _blockerGO = new GameObject("UIInspector_Blocker");
                _blockerGO.transform.SetParent(_overlayCanvasGO.transform, false);

                var blockerRT = _blockerGO.AddComponent<RectTransform>();
                blockerRT.anchorMin = Vector2.zero;
                blockerRT.anchorMax = Vector2.one;
                blockerRT.offsetMin = Vector2.zero;
                blockerRT.offsetMax = Vector2.zero;

                var blockerImage = _blockerGO.AddComponent<Image>();
                blockerImage.color = new Color(0, 0, 0, 0); // Fully transparent
                blockerImage.raycastTarget = true; // Catches all clicks

                _blockerGO.SetActive(_active);

                // Create highlight objects
                _highlightRoot = new GameObject("UIInspector_HighlightRoot");
                _highlightRoot.transform.SetParent(_overlayCanvasGO.transform, false);
                _highlightRoot.AddComponent<RectTransform>();

                // Make highlight root not intercept raycasts
                var rootGroup = _highlightRoot.AddComponent<CanvasGroup>();
                rootGroup.blocksRaycasts = false;
                rootGroup.interactable = false;

                _borderObjects = new GameObject[4];
                string[] names = { "UIInspector_Top", "UIInspector_Bottom", "UIInspector_Left", "UIInspector_Right" };

                for (int i = 0; i < 4; i++)
                {
                    var border = new GameObject(names[i]);
                    border.transform.SetParent(_overlayCanvasGO.transform, false);
                    border.transform.SetAsLastSibling();

                    var rt = border.AddComponent<RectTransform>();
                    // Anchor at bottom-left of canvas, pivot at center
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.zero;
                    rt.pivot = new Vector2(0.5f, 0.5f);

                    var img = border.AddComponent<Image>();
                    img.color = new Color(1f, 0.5f, 0f, 1f); // Solid orange
                    img.raycastTarget = false; // Don't intercept clicks

                    _borderObjects[i] = border;
                }

                _highlightRoot.SetActive(false);
            }
        }

        /// <summary>
        /// Creates the highlight border GameObjects if they don't exist.
        /// </summary>
        private static void EnsureHighlightObjects()
        {
            // Ensure the overlay canvas exists (may have been destroyed on scene change)
            EnsureOverlayCanvas();
        }

        /// <summary>
        /// Hides the highlight borders.
        /// </summary>
        private static void ClearHighlight()
        {
            if (_highlightRoot != null)
            {
                try { _highlightRoot.SetActive(false); }
                catch { _highlightRoot = null; }
            }

            if (_borderObjects != null)
            {
                for (int i = 0; i < _borderObjects.Length; i++)
                {
                    try
                    {
                        if (_borderObjects[i] != null)
                            _borderObjects[i].SetActive(false);
                    }
                    catch { _borderObjects[i] = null; }
                }
            }
        }

        /// <summary>
        /// Cleanup on disable â€” destroy overlay canvas.
        /// </summary>
        public static void Destroy()
        {
            if (_overlayCanvasGO != null)
            {
                try { UnityEngine.Object.Destroy(_overlayCanvasGO); }
                catch { }
                _overlayCanvasGO = null;
                _overlayCanvas = null;
                _highlightRoot = null;
                _borderObjects = null;
                _blockerGO = null;
            }
        }
    }
}
