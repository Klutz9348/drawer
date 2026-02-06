using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using Features.Drawing.Domain.ValueObject;
using Features.Drawing.App.Interface;

namespace Features.Drawing.Presentation
{
    /// <summary>
    /// Simple Mouse Input Provider for testing in Editor.
    /// Captures Mouse position and delegates to DrawingAppService.
    /// </summary>
    public class MouseInputProvider : MonoBehaviour
    {
        [SerializeField] private RectTransform _inputArea;
        [SerializeField] private MonoBehaviour _inputHandlerComponent; // Serialized as MonoBehaviour to allow Interface assignment (sort of)
        [SerializeField] private bool _enableDiagnostics = true;
        
        public RectTransform InputArea => _inputArea;
        
        private IInputHandler _inputHandler;
        private bool _isDrawing = false;
        private Vector2 _lastPos;
        private const float MinPixelSpacing = 0.5f;
        private readonly List<RaycastResult> _raycastResults = new List<RaycastResult>(8);
        
        // Cache to avoid GC in Update
        private PointerEventData _cachedPointerEventData;

        private void Awake()
        {
            if (_inputArea == null)
            {
                _inputArea = GetComponent<RectTransform>();
                if (_inputArea == null)
                {
                    Debug.LogError("MouseInputProvider: No Input Area (RectTransform) assigned or found on this GameObject!");
                }
            }
            
            // Resolve Interface
            if (_inputHandlerComponent != null)
            {
                _inputHandler = _inputHandlerComponent as IInputHandler;
            }
            
            if (_inputHandler == null)
            {
                var handler = FindInputHandler();
                _inputHandler = handler as IInputHandler;
                _inputHandlerComponent = handler;
                
                if (_inputHandler == null)
                {
                    Debug.LogError("MouseInputProvider: No IInputHandler found! Please assign DrawingAppService.");
                }
            }
        }

        private MonoBehaviour FindInputHandler()
        {
            var components = FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] is IInputHandler)
                {
                    return components[i];
                }
            }

            return null;
        }

        private void Update()
        {
            if (_inputHandler == null) return;
            
            // 1. Input Detection (Mouse)
            bool isDown = Input.GetMouseButtonDown(0);
            bool isUp = Input.GetMouseButtonUp(0);
            bool isHeld = Input.GetMouseButton(0);

            Vector2 screenPos = Input.mousePosition;

            // 0. Block Input over UI
            // Only block the START of a stroke. If we are already drawing, we continue.
            // DEBUG: Force disable blocking to test if drawing works
            /*
            if (isDown && IsPointerOverBlockingUi(screenPos))
            {
                return;
            }
            */
            if (isDown) 
            {
                bool blocked = IsPointerOverBlockingUi(screenPos);
                if (blocked) 
                {
                    if (_enableDiagnostics)
                    {
                        Debug.LogWarning($"[Input] UI Block Detected at {screenPos}. Raycast hits: {_raycastResults.Count}");
                        foreach(var hit in _raycastResults) Debug.Log($"[Input] Hit UI: {hit.gameObject.name}");
                    }
                    return;
                }
            }
            else if (isHeld && !_isDrawing)
            {
                // If held but not drawing (e.g. started over UI then dragged onto canvas),
                // we should NOT start drawing mid-drag to avoid weird artifacts.
                // Or maybe we should? Standard behavior is usually NO.
                return;
            }

            if (!isDown && !isUp && !isHeld) return;
            
            // 1. Determine which Camera to use for coordinate conversion
            Camera worldCam = null;
            if (_inputArea.GetComponentInParent<Canvas>().renderMode != RenderMode.ScreenSpaceOverlay)
            {
                worldCam = _inputArea.GetComponentInParent<Canvas>().worldCamera;
                // Fallback to Main Camera if canvas camera is missing
                if (worldCam == null) worldCam = Camera.main;
            }

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _inputArea, screenPos, worldCam, out Vector2 localPos))
            {
                if (_enableDiagnostics) Debug.LogWarning($"[Input] ScreenPointToLocalPointInRectangle Failed! Screen: {screenPos}, Cam: {worldCam?.name}");
                return; 
            }

            Rect rect = _inputArea.rect;
            float u = (localPos.x - rect.x) / rect.width;
            float v = (localPos.y - rect.y) / rect.height;
            Vector2 normalizedPos = new Vector2(u, v);

            if (u < 0 || u > 1 || v < 0 || v > 1)
            {
                if (_isDrawing) 
                {
                     if (_enableDiagnostics) Debug.Log($"[Input] Stroke Out of Bounds (u={u:F2}, v={v:F2}). Ending Stroke.");
                     EndStroke();
                }
                return;
            }

            if (isDown)
            {
                if (_enableDiagnostics) Debug.Log($"[Input] StartStroke at UV({u:F4}, {v:F4}) Screen({screenPos})");
                StartStroke(normalizedPos);
                if (isUp)
                {
                    EndStroke();
                }
            }
            else if (isHeld && _isDrawing)
            {
                ContinueStroke(normalizedPos);
            }
            else if (isUp && _isDrawing)
            {
                EndStroke();
            }
        }

        private void StartStroke(Vector2 pos)
        {
            _isDrawing = true;
            _lastPos = pos;
            _inputHandler.StartStroke(LogicPoint.FromNormalized(pos, 1.0f));
        }

        private void ContinueStroke(Vector2 pos)
        {
            float thresholdSqr = GetMinDistanceSqr();
            Vector2 delta = pos - _lastPos;
            if (delta.sqrMagnitude < thresholdSqr) return;
            _lastPos = pos;
            _inputHandler.MoveStroke(LogicPoint.FromNormalized(pos, 1.0f));
        }

        private float GetMinDistanceSqr()
        {
            if (_inputArea == null) return 0.001f * 0.001f;
            Rect rect = _inputArea.rect;
            float maxSize = Mathf.Max(rect.width, rect.height);
            if (maxSize <= 0.001f) return 0.001f * 0.001f;
            float normalized = MinPixelSpacing / maxSize;
            return normalized * normalized;
        }

        private void EndStroke()
        {
            _isDrawing = false;
            _inputHandler.EndStroke();
        }

        private bool IsPointerOverBlockingUi(Vector2 screenPos)
        {
            if (EventSystem.current == null) return false;

            if (_cachedPointerEventData == null)
            {
                _cachedPointerEventData = new PointerEventData(EventSystem.current);
            }

            _cachedPointerEventData.Reset();
            _cachedPointerEventData.position = screenPos;

            _raycastResults.Clear();
            EventSystem.current.RaycastAll(_cachedPointerEventData, _raycastResults);

            if (_raycastResults.Count == 0) return false;
            if (_inputArea == null) return true;

            Transform inputTransform = _inputArea.transform;

            // Iterate through all hit objects
            foreach (var result in _raycastResults)
            {
                GameObject hitObj = result.gameObject;
                Transform hitTransform = hitObj.transform;

                // 1. If we hit the input area (or its children), we are good to go!
                // NOTE: When using Screen Space - Camera, RaycastAll might hit the plane differently
                // but usually GraphicRaycaster handles it correctly.
                if (hitTransform == inputTransform || hitTransform.IsChildOf(inputTransform))
                {
                    return false; // Not blocked, we found the canvas!
                }

                // 2. If we hit something else, check if it's "interactive"
                // IGNORE "Canvas" object itself if it has a graphic raycaster but no visual blocking
                if (hitObj.name == "Canvas") continue;

                bool isInteractive = hitObj.GetComponentInParent<UnityEngine.UI.Selectable>() != null;
                
                if (isInteractive)
                {
                    return true; // Blocked by button/slider/etc.
                }
            }
            
            return false; 
        }

        private string GetPath(Transform t)
        {
            if (t.parent == null) return t.name;
            return GetPath(t.parent) + "/" + t.name;
        }
    }
}
