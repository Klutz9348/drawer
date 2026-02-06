using System.Collections.Generic;
using UnityEngine;
using Features.Drawing.Domain.ValueObject;
using Common.Constants;

namespace Features.Drawing.Domain.Logic
{
    /// <summary>
    /// Handles the mathematical logic of generating "stamps" from stroke points.
    /// Responsible for interpolation (densification) and rotation calculations.
    /// </summary>
    public class StrokeStampGenerator
    {
        // Configuration
        public BrushRotationMode RotationMode { get; set; } = BrushRotationMode.None;
        public float SpacingRatio { get; set; } = 0.15f;
        public float AngleJitter { get; set; } = 0f; // Random rotation jitter range (+/- degrees)

        // State
        private Vector2? _lastDrawPos = null;
        private float _lastPressure = 0f;
        private float _lastSize = 0f;
        private float _distanceAccumulator = 0f;
        private float _currentAngle = 0f;
        private float _sizeScale = 1f;
        
        // Resolution Scaling (Cached)
        private float _scaleX = 1f;
        private float _scaleY = 1f;

        public void SetCanvasResolution(Vector2Int resolution)
        {
            _scaleX = resolution.x / (float)DrawingConstants.LOGICAL_RESOLUTION;
            _scaleY = resolution.y / (float)DrawingConstants.LOGICAL_RESOLUTION;
        }

        public void SetSizeScale(float sizeScale)
        {
            _sizeScale = Mathf.Max(0.0001f, sizeScale);
        }

        public void Reset()
        {
            _lastDrawPos = null;
            _distanceAccumulator = 0f;
            _currentAngle = 0f;
            _lastPressure = 0f;
            _lastSize = 0f;
        }

        public void ProcessPoints(List<LogicPoint> points, float brushSize, List<StampData> outputBuffer)
        {
            if (outputBuffer == null) return;
            outputBuffer.Clear();

            // Optimization: Use for loop to avoid enumerator allocation
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                // Position mapping
                float x = p.X * _scaleX;
                float y = p.Y * _scaleY;
                Vector2 currentPos = new Vector2(x, y);
                
                // Ignore input pressure; use constant size for all strokes.
                float normalizedPressure = 1.0f;
                // Brush size is in UI pixels; convert to render-texture pixels.
                float currentSize = brushSize * normalizedPressure * _sizeScale;

                // --- Interpolation Logic ---
                if (_lastDrawPos.HasValue)
                {
                    Vector2 dir = currentPos - _lastDrawPos.Value;
                    float dist = dir.magnitude;
                    
                    // Dynamic spacing based on brush size
                    // If spacing is too large, hard brushes look jagged (circles visible).
                    // If too small, performance drops.
                    // Hard brushes need smaller spacing (e.g. 0.05-0.1). Soft brushes can handle 0.15-0.2.
                    // We use SpacingRatio, but clamp it to avoid extreme performance hit.
                    // Minimum spacing: 1.0 pixel
                    float spacing = Mathf.Max(1.0f, currentSize * SpacingRatio); 

                    // Add to accumulator
                    _distanceAccumulator += dist;

                    if (_distanceAccumulator >= spacing)
                    {
                        // Calculate rotation target
                        float targetAngle = 0f;
                        bool needsRotation = RotationMode != BrushRotationMode.None;

                        if (RotationMode == BrushRotationMode.Follow)
                        {
                            targetAngle = Mathf.Atan2(dir.normalized.y, dir.normalized.x) * Mathf.Rad2Deg;
                        }
                        else if (RotationMode == BrushRotationMode.Fixed)
                        {
                            targetAngle = 45f;
                        }

                        // Interpolate
                        // We need to walk forward from the last theoretical stamp position.
                        // Ideally, we shouldn't modify _distanceAccumulator inside the loop destructively if we want precision.
                        // But for simplicity:
                        
                        while (_distanceAccumulator >= spacing)
                        {
                            _distanceAccumulator -= spacing;
                            
                            // The point is located at `spacing` distance "back" from the current accumulated front.
                            // But since we just subtracted spacing, it means we are at `_distanceAccumulator` distance "past" the new stamp.
                            // So the new stamp is at `dist - _distanceAccumulator` along the vector `dir` from `_lastDrawPos`.
                            
                            float d = dist - _distanceAccumulator;
                            float t = d / dist;
                            
                            Vector2 interpPos = Vector2.Lerp(_lastDrawPos.Value, currentPos, t);
                            float interpSize = Mathf.Lerp(_lastSize, currentSize, t);
                            
                            float drawAngle = 0f;
                            if (needsRotation)
                            {
                                if (RotationMode == BrushRotationMode.Follow)
                                {
                                    _currentAngle = Mathf.LerpAngle(_currentAngle, targetAngle, 0.3f);
                                    drawAngle = _currentAngle;
                                }
                                else
                                {
                                    drawAngle = targetAngle;
                                }
                            }
                            
                            if (AngleJitter > 0f) drawAngle += Random.Range(-AngleJitter, AngleJitter);
                            
                            outputBuffer.Add(new StampData(interpPos, interpSize, drawAngle));
                        }
                    }
                }
                else
                {
                    // First point
                    float startAngle = 0f;
                    if (RotationMode == BrushRotationMode.Follow)
                    {
                        _currentAngle = 0f;
                    }
                    else if (RotationMode == BrushRotationMode.Fixed)
                    {
                        startAngle = 45f;
                    }
                    
                    outputBuffer.Add(new StampData(currentPos, currentSize, startAngle));
                    _distanceAccumulator = 0f;
                }

                // Update state
                _lastDrawPos = currentPos;
                _lastPressure = normalizedPressure;
                _lastSize = currentSize;
            }
        }
    }
}
