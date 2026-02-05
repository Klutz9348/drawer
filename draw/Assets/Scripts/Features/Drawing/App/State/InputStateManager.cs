using UnityEngine;
using Features.Drawing.Domain;
using Features.Drawing.Domain.Interface;

namespace Features.Drawing.App.State
{
    /// <summary>
    /// Manages the current input state (Color, Size, Brush Type) and synchronizes it with the Renderer.
    /// Extracts state management logic from DrawingAppService.
    /// </summary>
    public class InputStateManager
    {
        private readonly IStrokeRenderer _renderer;
        private readonly BrushStrategy _eraserStrategy;

        // State
        private Color _currentColor = Color.black;
        private float _currentSize = 10f;
        private float _lastBrushSize = 10f;
        private float _lastEraserSize = 30f;
        private bool _isEraser = false;
        private BrushStrategy _currentStrategy;
        private Texture2D _currentRuntimeTexture;

        // Accessors
        public bool IsEraser => _isEraser;
        public float CurrentSize => _currentSize;
        public Color CurrentColor => _currentColor;
        public BrushStrategy CurrentStrategy => _currentStrategy;
        public Texture2D CurrentRuntimeTexture => _currentRuntimeTexture;

        public InputStateManager(IStrokeRenderer renderer, BrushStrategy eraserStrategy)
        {
            _renderer = renderer;
            _eraserStrategy = eraserStrategy;
        }

        public void SetBrushStrategy(BrushStrategy strategy, Texture2D runtimeTexture = null)
        {
            if (strategy == null) return;
            
            _currentStrategy = strategy;
            _currentRuntimeTexture = runtimeTexture;
            _isEraser = false;
            _currentSize = _lastBrushSize; // Restore brush size
            
            if (_renderer != null)
            {
                _renderer.ConfigureBrush(strategy, runtimeTexture);
                _renderer.SetEraser(false);
                _renderer.SetBrushColor(_currentColor);
                _renderer.SetBrushSize(_lastBrushSize);
            }
        }

        public void SetColor(Color color)
        {
            _currentColor = color;
            _isEraser = false;
            // Restore brush size because setting color implies using brush
            _currentSize = _lastBrushSize; 
            
            if (_renderer != null)
            {
                _renderer.SetBrushColor(color);
                _renderer.SetEraser(false);
                _renderer.SetBrushSize(_lastBrushSize);
            }
        }

        public void SetSize(float size)
        {
            // USER REQUEST: "Size is for changing brush size, not eraser."
            // So we ALWAYS update _lastBrushSize, regardless of current mode.
            _lastBrushSize = size;

            if (!_isEraser)
            {
                _currentSize = size;
                if (_renderer != null)
                {
                    _renderer.SetBrushSize(size);
                }
            }
        }

        public void SetEraser(bool isEraser)
        {
            _isEraser = isEraser;
            
            // Swap size based on mode
            float targetSize = isEraser ? _lastEraserSize : _lastBrushSize;
            _currentSize = targetSize;

            if (_renderer != null)
            {
                if (isEraser)
                {
                    // Force Eraser to use Hard Brush Strategy if available
                    if (_eraserStrategy != null)
                    {
                        _renderer.ConfigureBrush(_eraserStrategy);
                    }
                }

                _renderer.SetEraser(isEraser);
                _renderer.SetBrushSize(targetSize);
                
                // CRITICAL FIX: If switching BACK to brush, we MUST restore the brush's blend modes and texture.
                if (!isEraser && _currentStrategy != null)
                {
                    _renderer.ConfigureBrush(_currentStrategy, _currentRuntimeTexture); // Restore runtime texture if available
                    _renderer.SetBrushColor(_currentColor);
                }
            }
        }
        
        /// <summary>
        /// Force re-sync of state to renderer (useful after Undo/Redo or Context Loss).
        /// </summary>
        public void SyncToRenderer()
        {
            if (_renderer == null) return;

            if (_isEraser)
            {
                if (_eraserStrategy != null) _renderer.ConfigureBrush(_eraserStrategy);
                _renderer.SetEraser(true);
                _renderer.SetBrushSize(_lastEraserSize);
            }
            else
            {
                if (_currentStrategy != null) _renderer.ConfigureBrush(_currentStrategy, _currentRuntimeTexture);
                _renderer.SetEraser(false);
                _renderer.SetBrushColor(_currentColor);
                _renderer.SetBrushSize(_lastBrushSize);
            }
        }
    }
}
