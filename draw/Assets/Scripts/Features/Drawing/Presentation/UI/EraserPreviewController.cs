using UnityEngine;
using UnityEngine.UI;
using Features.Drawing.App.Interface;
using Features.Drawing.Domain.Interface;
using Features.Drawing.Domain;
using Features.Drawing.Presentation;

namespace Features.Drawing.Presentation.UI
{
    /// <summary>
    /// Displays a visual preview of the eraser tool position and size.
    /// Renders a semi-transparent red overlay that follows the mouse/stylus.
    /// </summary>
    public class EraserPreviewController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private MonoBehaviour _appService; // Serialized as MonoBehaviour for Interface
        [SerializeField] private MonoBehaviour _rendererComponent; // Serialized as MonoBehaviour for Interface
        [SerializeField] private RectTransform _inputArea;
        
        [Header("Visual Settings")]
        [SerializeField] private Color _previewColor = new Color(1f, 0f, 0f, 0.3f);
        [SerializeField] private Sprite _defaultCircleSprite;

        private IDrawingFacade _drawingFacade;
        private ICanvasResolutionProvider _resolutionProvider;

        private GameObject _previewObj;
        private RectTransform _previewRect;
        private Image _previewImage;
        private Texture2D _lastTexture;
        private Sprite _generatedSprite;

        private void Start()
        {
            InitializeReferences();
            CreatePreviewObject();
        }

        private void InitializeReferences()
        {
            // Resolve IDrawingFacade
            if (_appService != null)
                _drawingFacade = _appService as IDrawingFacade;

            if (_drawingFacade == null)
            {
                var facade = FindDrawingFacade();
                _drawingFacade = facade;
                _appService = facade as MonoBehaviour;
            }

            // Resolve ICanvasResolutionProvider
            if (_rendererComponent != null)
                _resolutionProvider = _rendererComponent as ICanvasResolutionProvider;

            if (_resolutionProvider == null)
            {
                var provider = FindResolutionProvider();
                _resolutionProvider = provider;
                _rendererComponent = provider as MonoBehaviour;
            }

            // If input area is not assigned, try to find the one used by MouseInputProvider or default to this transform if it's a RectTransform
            if (_inputArea == null)
            {
                var inputProvider = FindObjectOfType<MouseInputProvider>();
                if (inputProvider != null)
                {
                    _inputArea = inputProvider.InputArea;
                }
                
                if (_inputArea == null)
                {
                     _inputArea = GetComponentInParent<Canvas>()?.GetComponent<RectTransform>();
                }
            }
        }
        
        private IDrawingFacade FindDrawingFacade()
        {
            var components = FindObjectsOfType<MonoBehaviour>();
            foreach (var c in components)
            {
                if (c is IDrawingFacade f) return f;
            }
            return null;
        }

        private ICanvasResolutionProvider FindResolutionProvider()
        {
            var components = FindObjectsOfType<MonoBehaviour>();
            foreach (var c in components)
            {
                if (c is ICanvasResolutionProvider p) return p;
            }
            return null;
        }

        private void CreatePreviewObject()
        {
            if (_previewObj != null) return;

            _previewObj = new GameObject("EraserPreview");
            _previewObj.transform.SetParent(_inputArea != null ? _inputArea : transform, false);
            
            // Ensure it's last sibling to render on top of drawing
            _previewObj.transform.SetAsLastSibling();

            _previewRect = _previewObj.AddComponent<RectTransform>();
            _previewRect.anchorMin = new Vector2(0.5f, 0.5f);
            _previewRect.anchorMax = new Vector2(0.5f, 0.5f);
            _previewRect.pivot = new Vector2(0.5f, 0.5f);

            _previewImage = _previewObj.AddComponent<Image>();
            _previewImage.color = _previewColor;
            _previewImage.raycastTarget = false; // Pass through input
            
            // Generate default circle if needed
            if (_defaultCircleSprite == null)
            {
                _defaultCircleSprite = GenerateCircleSprite();
            }
            _previewImage.sprite = _defaultCircleSprite;
            
            _previewObj.SetActive(false);
        }

        private Sprite GenerateCircleSprite()
        {
            int res = 64;
            Texture2D tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
            Color[] colors = new Color[res * res];
            float center = res * 0.5f;
            float radius = res * 0.45f;
            float radiusSqr = radius * radius;

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float distSqr = dx * dx + dy * dy;
                    
                    // Simple AA circle
                    float alpha = 1.0f;
                    if (distSqr > radiusSqr)
                    {
                        alpha = Mathf.Clamp01(radius + 1.0f - Mathf.Sqrt(distSqr));
                    }
                    
                    colors[y * res + x] = new Color(1, 1, 1, alpha);
                }
            }
            
            tex.SetPixels(colors);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, res, res), new Vector2(0.5f, 0.5f));
        }

        private void Update()
        {
            if (_drawingFacade == null || _resolutionProvider == null || _previewObj == null || _inputArea == null) return;

            bool show = _drawingFacade.IsEraser;
            
            if (show)
            {
                if (!_previewObj.activeSelf) _previewObj.SetActive(true);
                UpdatePreview();
            }
            else
            {
                if (_previewObj.activeSelf) _previewObj.SetActive(false);
            }
        }

        private void UpdatePreview()
        {
            // 1. Follow Mouse
            Vector2 screenPos = Input.mousePosition;
            Camera worldCam = null;
            Canvas canvas = _inputArea.GetComponentInParent<Canvas>();
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                worldCam = canvas.worldCamera;
                if (worldCam == null) worldCam = Camera.main;
            }

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _inputArea, screenPos, worldCam, out Vector2 localPos))
            {
                _previewRect.localPosition = localPos;
            }

            // 2. Update Size
            Vector2Int rtRes = _resolutionProvider.Resolution;
            if (rtRes.x <= 0 || rtRes.y <= 0) return;

            Rect uiRect = _inputArea.rect;
            float scaleX = uiRect.width / rtRes.x;
            float scaleY = uiRect.height / rtRes.y;
            
            // Use the smaller scale to fit (aspect ratio fit)
            float scale = Mathf.Min(scaleX, scaleY);
            
            float brushSizePixels = _drawingFacade.CurrentSize;
            
            // Apply BrushStrategy Size Multiplier if needed
            float multiplier = 1.0f;
            BrushStrategy strategy = _drawingFacade.EraserStrategy;
            if (strategy != null)
            {
                multiplier = strategy.SizeMultiplier;
                
                // 3. Update Sprite if strategy changes
                UpdateSprite(strategy);
            }
            
            float finalSize = brushSizePixels * multiplier * scale;
            _previewRect.sizeDelta = new Vector2(finalSize, finalSize);
        }

        private void UpdateSprite(BrushStrategy strategy)
        {
            Texture2D tex = strategy.MainTexture;
            
            if (tex != null && tex != _lastTexture)
            {
                _lastTexture = tex;
                if (_generatedSprite != null) Destroy(_generatedSprite); // Cleanup previous
                
                _generatedSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                _previewImage.sprite = _generatedSprite;
            }
            else if (tex == null && _lastTexture != null)
            {
                // Revert to default
                _lastTexture = null;
                _previewImage.sprite = _defaultCircleSprite;
            }
        }

        private void OnDestroy()
        {
            if (_generatedSprite != null) Destroy(_generatedSprite);
            if (_defaultCircleSprite != null) Destroy(_defaultCircleSprite);
            if (_previewObj != null) Destroy(_previewObj);
        }
    }
}
