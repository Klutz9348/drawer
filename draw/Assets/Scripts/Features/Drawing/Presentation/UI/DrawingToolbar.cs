using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Features.Drawing.App.Interface;
using Features.Drawing.Domain;
using Common.Utils;

namespace Features.Drawing.Presentation.UI
{
    /// <summary>
    /// Manages the Drawing UI Toolbar.
    /// </summary>
    public class DrawingToolbar : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private MonoBehaviour _appService;

        // Top Bar Buttons
        [SerializeField] private Button _btnUndo;
        [SerializeField] private Button _btnRedo;
        [SerializeField] private Button _btnClear;

        // Bottom Navigation Tabs
        [SerializeField] private Button _tabBrush;
        [SerializeField] private Button _tabEraser;
        [SerializeField] private Button _tabSize;
        [SerializeField] private Button _tabColor;

        // Sub Panels
        [SerializeField] private GameObject _panelContainer; // The background container for sub-panels
        [SerializeField] private GameObject _panelBrush; // Shows brush types
        [SerializeField] private GameObject _panelSize;  // Shows sizes
        [SerializeField] private GameObject _panelColor; // Shows colors

        // Panel Content Buttons
        // Brush Types
        [SerializeField] private Button _btnTypeSoft;
        [SerializeField] private Button _btnTypeHard;
        [SerializeField] private Button _btnTypeMarker;
        [SerializeField] private Button _btnTypePencil;

        // Sizes
        [SerializeField] private Button[] _sizeButtons; // Shared for both Brush and Eraser

        // Colors
        [SerializeField] private Button[] _colorButtons;

        [Header("Brush Strategies")]
        [SerializeField] private BrushStrategy _softBrushStrategy;
        [SerializeField] private BrushStrategy _hardBrushStrategy;
        [SerializeField] private BrushStrategy _markerBrushStrategy;
        [SerializeField] private BrushStrategy _pencilBrushStrategy;

        // State
        private float[] _sizePresets = new float[] { 10f, 20f, 40f, 60f, 80f };
        private float _currentSize = 20f;
        private enum Tab { None, Brush, Eraser, Size, Color }
        private Tab _activeTab = Tab.None;
        private bool _isEraserMode = false;
        private Color _currentUiColor = Color.black; // Default to black
        private IDrawingFacade _drawingFacade;

        private void Start()
        {
            if (_appService != null)
            {
                _drawingFacade = _appService as IDrawingFacade;
            }

            if (_drawingFacade == null)
            {
                _drawingFacade = FindDrawingFacade();
                _appService = _drawingFacade as MonoBehaviour;
            }

            // Events
            if (_drawingFacade != null)
            {
                _drawingFacade.OnStrokeStarted += OnStrokeStarted;
            }

            // Top Bar
            if (_btnClear) _btnClear.onClick.AddListener(OnClearClick);
            if (_btnUndo) _btnUndo.onClick.AddListener(OnUndoClick);

            // Tabs
            if (_tabBrush) _tabBrush.onClick.AddListener(() => SwitchTab(Tab.Brush));
            if (_tabEraser) _tabEraser.onClick.AddListener(() => SwitchTab(Tab.Eraser));
            if (_tabSize) _tabSize.onClick.AddListener(() => SwitchTab(Tab.Size));
            if (_tabColor) _tabColor.onClick.AddListener(() => SwitchTab(Tab.Color));

            // Brush Types
            if (_btnTypeSoft) _btnTypeSoft.onClick.AddListener(() => SetBrushType(0));
            if (_btnTypeHard) _btnTypeHard.onClick.AddListener(() => SetBrushType(1));
            if (_btnTypeMarker) _btnTypeMarker.onClick.AddListener(() => SetBrushType(2));
            if (_btnTypePencil) _btnTypePencil.onClick.AddListener(() => SetBrushType(3));

            // Sizes
            if (_sizeButtons != null)
            {
                for (int i = 0; i < _sizeButtons.Length; i++)
                {
                    float size = _sizePresets[Mathf.Clamp(i, 0, _sizePresets.Length - 1)];
                    if (_sizeButtons[i]) 
                        _sizeButtons[i].onClick.AddListener(() => SetSize(size));
                }
            }

            // Colors
            Color[] presetColors = new Color[] { 
                Color.black, Color.red, new Color(1f, 0.8f, 0f), new Color(0.2f, 1f, 0.2f), new Color(0f, 0.8f, 1f), Color.blue, new Color(0.6f, 0f, 1f), new Color(1f, 0f, 0.5f)
            };
            if (_colorButtons != null)
            {
                for (int i = 0; i < _colorButtons.Length; i++)
                {
                    Color c = presetColors[Mathf.Clamp(i, 0, presetColors.Length - 1)];
                    if (_colorButtons[i])
                        _colorButtons[i].onClick.AddListener(() => SetColor(c));
                }
            }

            // Default State
            // Set a default brush strategy immediately so AppService has a valid state
            SetBrushType(0); // Select Soft Brush by default
            
            // Default to first size (40f)
            if (_sizePresets.Length > 0)
            {
                _currentSize = _sizePresets[0];
            }

            SetSize(_currentSize); // Initialize size and UI
            SwitchTab(Tab.Brush);

            // Ensure Eraser Preview exists
            if (FindObjectOfType<EraserPreviewController>() == null)
            {
                var go = new GameObject("EraserPreviewController");
                // Parent it to the toolbar's canvas or input provider's area if possible
                // But the controller will find its own parent/input area.
                // Just keeping it in the hierarchy is enough.
                go.transform.SetParent(this.transform.parent); 
                go.AddComponent<EraserPreviewController>();
            }
        }

        private void OnDestroy()
        {
            if (_drawingFacade != null)
            {
                _drawingFacade.OnStrokeStarted -= OnStrokeStarted;
            }
        }

        private void Update()
        {
            // Keyboard Shortcuts
            // Only trigger if no input field is focused (basic check, can be improved)
            // Fix: Check for KeyDown to avoid multiple triggers per frame, which is correct here.
            // But verify if the event system might be processing it multiple times or if Update is called excessively?
            // Standard Update is once per frame. Input.GetKeyDown is true for one frame.
            
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.LeftCommand);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            if (ctrl)
            {
                if (Input.GetKeyDown(KeyCode.Z))
                {
                    if (shift)
                    {
                        if (_drawingFacade != null) _drawingFacade.Redo();
                    }
                    else
                    {
                        if (_drawingFacade != null) _drawingFacade.Undo();
                    }
                }
                else if (Input.GetKeyDown(KeyCode.Y))
                {
                    if (_drawingFacade != null) _drawingFacade.Redo();
                }
            }
        }

        private IDrawingFacade FindDrawingFacade()
        {
            var components = FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] is IDrawingFacade facade)
                {
                    return facade;
                }
            }

            return null;
        }

        private void OnStrokeStarted()
        {
            // Close any open panels when drawing starts
            if (_activeTab != Tab.None)
            {
                SwitchTab(Tab.None);
            }
        }

        private void SwitchTab(Tab tab)
        {
            if (_activeTab == tab)
            {
                return;
            }

            _activeTab = tab;
            UpdateTabVisuals();

            // Logic mapping
            switch (tab)
            {
                case Tab.Brush:
                    _isEraserMode = false;
                    if (_drawingFacade != null) _drawingFacade.SetEraser(false);
                    // Show Brush Panel
                    if (_panelContainer) _panelContainer.SetActive(true);
                    if (_panelBrush) _panelBrush.SetActive(true);
                    // Hide other panels
                    if (_panelSize) _panelSize.SetActive(false);
                    if (_panelColor) _panelColor.SetActive(false);
                    break;
                case Tab.Eraser:
                    _isEraserMode = true;
                    if (_drawingFacade != null) _drawingFacade.SetEraser(true);
                    // Hide all panels
                    if (_panelContainer) _panelContainer.SetActive(false);
                    if (_panelBrush) _panelBrush.SetActive(false);
                    if (_panelSize) _panelSize.SetActive(false);
                    if (_panelColor) _panelColor.SetActive(false);
                    break;
                case Tab.Size:
                    // Toggle Size Panel
                    if (_panelContainer) _panelContainer.SetActive(true);
                    if (_panelBrush) _panelBrush.SetActive(false);
                    if (_panelSize) _panelSize.SetActive(true);
                    if (_panelColor) _panelColor.SetActive(false);
                    break;
                case Tab.Color:
                    // Toggle Color Panel
                    if (_panelContainer) _panelContainer.SetActive(true);
                    if (_panelBrush) _panelBrush.SetActive(false);
                    if (_panelSize) _panelSize.SetActive(false);
                    if (_panelColor) _panelColor.SetActive(true);
                    break;
                case Tab.None:
                    // Hide all
                    if (_panelContainer) _panelContainer.SetActive(false);
                    if (_panelBrush) _panelBrush.SetActive(false);
                    if (_panelSize) _panelSize.SetActive(false);
                    if (_panelColor) _panelColor.SetActive(false);
                    break;
            }
        }

        private void UpdateTabVisuals()
        {
            // Keep the tool tab active if we are in that mode, even if looking at Size/Color panels
            // But usually tabs are mutually exclusive in UI. 
            // If the user request "Don't turn into eraser", they might mean visual confusion.
            // Let's highlight the tool button if it's the active tool, OR if it's the active tab.
            
            bool isBrushActive = _activeTab == Tab.Brush || (_activeTab != Tab.Eraser && !_isEraserMode);
            bool isEraserActive = _activeTab == Tab.Eraser || (_activeTab != Tab.Brush && _isEraserMode && _activeTab != Tab.Size && _activeTab != Tab.Color);
            
            // Simplification: The user likely wants to know "Am I using Brush or Eraser?"
            // If I click Size, I am still using Brush (if I was before).
            // So Brush tab should probably stay highlighted or indicate state?
            // Standard tab behavior: Only one tab active.
            // Let's stick to standard tab behavior for now, but ensure logic is correct.
            
            SetTabColor(_tabBrush, isBrushActive);
            SetTabColor(_tabEraser, isEraserActive);
            SetTabColor(_tabSize, _activeTab == Tab.Size);
            SetTabColor(_tabColor, _activeTab == Tab.Color);
        }

        private void SetTabColor(Button btn, bool isActive)
        {
            if (btn == null) return;
            
            // Use current selected color for Brush-related modes, and Blue for Eraser mode
            Color themeColor = _isEraserMode ? new Color(0.2f, 0.6f, 1f) : _currentUiColor;
            
            Color activeColor = themeColor;
            Color inactiveColor = Color.gray;

            Transform icon = btn.transform.Find("Icon");
            if (icon != null)
            {
                Image img = icon.GetComponent<Image>();
                if (img != null)
                {
                    img.color = isActive ? activeColor : inactiveColor;
                }
            }
            
            Transform label = btn.transform.Find("Label");
            if (label != null)
            {
                TextMeshProUGUI txt = label.GetComponent<TextMeshProUGUI>();
                if (txt != null)
                {
                    txt.color = isActive ? activeColor : inactiveColor;
                }
            }
            
            // Optional: Tint background slightly
            Image bg = btn.GetComponent<Image>();
            if (bg != null)
            {
                // Subtle blue tint for active tab background
                bg.color = isActive ? new Color(0.2f, 0.6f, 1f, 0.1f) : Color.clear;
            }
        }

        private void OnClearClick()
        {
            if (_drawingFacade == null) return;
            _drawingFacade.ClearCanvas();
            // TODO: Clear history in AppService if we want "Clear" to be undoable or just wipe everything?
            // Usually Clear is a distinct action.
            // If user wants to Undo Clear, we'd need to treat Clear as a Command.
            // For now, Clear just wipes canvas.
        }

        private void OnUndoClick()
        {
            if (_drawingFacade == null) return;
            _drawingFacade.Undo();
        }

        private void OnRedoClick()
        {
            if (_drawingFacade == null) return;
            _drawingFacade.Redo();
        }

        private void SetSize(float size)
        {
            _currentSize = size;
            if (_drawingFacade == null) return;
            _drawingFacade.SetSize(size);
            UpdateSizeTabDisplay();
            // Debug.Log($"Size set to: {size}");
        }

        private void UpdateSizeTabDisplay()
        {
            if (_tabSize == null) return;
            
            // Update Text to show numerical value
            Transform label = _tabSize.transform.Find("Label");
            if (label != null)
            {
                TextMeshProUGUI txt = label.GetComponent<TextMeshProUGUI>();
                if (txt != null)
                {
                    txt.text = $"{_currentSize:F0}"; // e.g. "20"
                }
            }

            // Update Icon Scale to reflect size visually
            Transform icon = _tabSize.transform.Find("Icon");
            if (icon != null)
            {
                // Map 10-80 to 0.4-1.0 scale range
                float t = Mathf.InverseLerp(10f, 80f, _currentSize);
                float scale = Mathf.Lerp(0.4f, 1.0f, t);
                icon.localScale = new Vector3(scale, scale, 1f);
            }
        }

        private void SetColor(Color c)
        {
            if (_drawingFacade == null) return;
            _drawingFacade.SetColor(c);
            _currentUiColor = c;
            
            // If we set color, we probably want to switch back to brush mode if we were in eraser
            if (_isEraserMode)
            {
                SwitchTab(Tab.Brush);
            }
            else
            {
                UpdateTabVisuals();
            }
        }

        private void SetBrushType(int type)
        {
            // IMPORTANT: Switching brush type implies we want to use the BRUSH tool.
            // So we must exit Eraser mode and update the UI tabs accordingly.
            if (_activeTab != Tab.Brush)
            {
                // Force switch to Brush tab logic (which handles SetEraser(false) and UI updates)
                SwitchTab(Tab.Brush);
                // Note: SwitchTab calls UpdateTabVisuals, so the UI will reflect the change.
            }
            else
            {
                // If we are already on Brush tab, just ensure Eraser is off (redundant but safe)
                if (_drawingFacade != null) _drawingFacade.SetEraser(false);
            }

            BrushStrategy strategy = null;
            switch (type)
            {
                case 0: strategy = _softBrushStrategy; break;
                case 1: strategy = _hardBrushStrategy; break;
                case 2: strategy = _markerBrushStrategy; break;
                case 3: strategy = _pencilBrushStrategy; break;
            }

            if (strategy != null)
            {
                ApplyBrushStrategy(strategy);
            }
            else
            {
                Debug.LogWarning($"Brush strategy missing for type {type}.");
            }
        }

        private void ApplyBrushStrategy(BrushStrategy strategy)
        {
            Texture2D runtimeTex = null;
            if (strategy.UseRuntimeGeneration)
            {
                runtimeTex = TextureGeneratorService.GetSharpHardBrush();
            }
            
            _drawingFacade.SetBrushStrategy(strategy, runtimeTex);
        }
    }
}
