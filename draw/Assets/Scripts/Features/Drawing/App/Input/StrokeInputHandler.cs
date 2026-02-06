using System.Collections.Generic;
using UnityEngine;
using Features.Drawing.Domain;
using Features.Drawing.Domain.ValueObject;
using Features.Drawing.Domain.Entity;
using Features.Drawing.Domain.Interface;
using Features.Drawing.App.State;
using Features.Drawing.App.Command;
using Features.Drawing.Service;
using Common.Utils;
using Common.Diagnostics;

namespace Features.Drawing.App.Input
{
    public class StrokeInputHandler
    {
        private readonly InputStateManager _inputState;
        private readonly IStrokeRenderer _renderer;
        private readonly IStrokeSmoothingService _smoothingService;
        private readonly IStrokeCollisionService _collisionService;
        private readonly IDrawingHistoryManager _historyManager;
        private readonly IBrushRegistry _brushRegistry;
        private readonly IStructuredLogger _logger;
        private readonly BrushStrategy _eraserStrategy;

        // State
        private StrokeEntity _currentStroke;
        private List<LogicPoint> _currentStrokeRaw = new List<LogicPoint>(1024);
        private TraceContext _activeStrokeTrace;
        
        // Optimization State
        private LogicPoint _lastAddedPoint;
        private Vector2 _currentStabilizedPos;
        private float _logicToWorldRatio = 1.0f; // Default

        // Buffers for smoothing
        private List<LogicPoint> _smoothingInputBuffer = new List<LogicPoint>(8);
        private List<LogicPoint> _smoothingOutputBuffer = new List<LogicPoint>(64);
        private List<LogicPoint> _singlePointBuffer = new List<LogicPoint>(1);
        private readonly LogicPoint[] _singlePointArray = new LogicPoint[1];
        private readonly List<IStrokeEventListener> _listeners = new List<IStrokeEventListener>();
        
        public StrokeInputHandler(
            InputStateManager inputState,
            IStrokeRenderer renderer,
            IStrokeSmoothingService smoothingService,
            IStrokeCollisionService collisionService,
            IDrawingHistoryManager historyManager,
            IBrushRegistry brushRegistry,
            BrushStrategy eraserStrategy,
            IStructuredLogger logger = null)
        {
            _inputState = inputState;
            _renderer = renderer;
            _smoothingService = smoothingService;
            _collisionService = collisionService;
            _historyManager = historyManager;
            _brushRegistry = brushRegistry;
            _eraserStrategy = eraserStrategy;
            _logger = logger;
        }

        public void RegisterListener(IStrokeEventListener listener)
        {
            if (!_listeners.Contains(listener)) _listeners.Add(listener);
        }

        public void UnregisterListener(IStrokeEventListener listener)
        {
            _listeners.Remove(listener);
        }

        public void SetLogicToWorldRatio(float ratio)
        {
            _logicToWorldRatio = ratio;
            _collisionService?.SetLogicToWorldRatio(ratio);
        }

        public void StartStroke(LogicPoint point, long sequenceId, int authorId = 0)
        {
            if (_inputState == null) return;

            // Diagnostics
            _activeStrokeTrace = TraceContext.New();
            if (_logger != null)
            {
                var meta = new Dictionary<string, object> 
                { 
                    { "isEraser", _inputState.IsEraser },
                    { "size", _inputState.CurrentSize },
                    { "color", _inputState.CurrentColor },
                    { "point", point.ToString() },
                    { "authorId", authorId }
                };
                _logger.Info("StrokeStarted", _activeStrokeTrace, meta);
            }

            // Sync Renderer
            _inputState.SyncToRenderer();

            _currentStrokeRaw.Clear();
            _smoothingInputBuffer.Clear();
            
            // Create Entity
            uint id = (uint)Random.Range(0, int.MaxValue); 
            uint seed = (uint)Random.Range(0, int.MaxValue);
            
            var activeStrategy = _inputState.IsEraser ? _eraserStrategy : _inputState.CurrentStrategy;
            ushort brushId = _brushRegistry.GetBrushId(activeStrategy);
            
            _currentStroke = new StrokeEntity(
                id, 
                (ushort)authorId, // Support Multi-author
                brushId, 
                seed, 
                _inputState.CurrentColor, 
                _inputState.CurrentSize, 
                sequenceId,
                _currentStrokeRaw 
            );

            // Notify Listeners
            foreach (var listener in _listeners)
            {
                listener.OnStrokeStarted(_currentStroke);
            }

            // Init Optimization State
            _lastAddedPoint = point;
            _currentStabilizedPos = point.ToNormalized();

            // Add first point
            AddPoint(point);

            // Start Renderer
            _renderer.StartStroke(point, _inputState.IsEraser, _inputState.CurrentSize, _inputState.CurrentColor);
        }

        public void MoveStroke(LogicPoint point)
        {
            if (_currentStroke == null) return;

            // Stabilization & Spacing Logic
            LogicPoint pointToAdd = point;
            
            if (_inputState.CurrentStrategy != null && _inputState.CurrentStrategy.EnableStabilizer && !_inputState.IsEraser)
            {
                // Simple stabilizer
                Vector2 target = point.ToNormalized();
                float dist = Vector2.Distance(_currentStabilizedPos, target);
                
                const float MIN_SPEED_THRESHOLD = 0.002f; 
                const float MAX_SPEED_THRESHOLD = 0.05f;

                float speedT = Mathf.InverseLerp(MIN_SPEED_THRESHOLD, MAX_SPEED_THRESHOLD, dist);
                float dynamicFactor = Mathf.Lerp(_inputState.CurrentStrategy.StabilizationFactor, _inputState.CurrentStrategy.StabilizationFactor * 0.2f, speedT);
                float pressure = Mathf.Clamp01(point.GetNormalizedPressure());
                float pressureWeight = Mathf.Lerp(1.1f, 0.7f, pressure);
                dynamicFactor *= pressureWeight;
                
                float t = Mathf.Clamp01(1.0f - dynamicFactor);
                _currentStabilizedPos = Vector2.Lerp(_currentStabilizedPos, target, t);
                
                pointToAdd = LogicPoint.FromNormalized(_currentStabilizedPos, point.GetNormalizedPressure());
            }
            else
            {
                _currentStabilizedPos = point.ToNormalized();
            }

            if (!_inputState.IsEraser)
            {
                float spacingRatio = _inputState.CurrentStrategy != null ? _inputState.CurrentStrategy.SpacingRatio : 0.15f;
                float minPixelSpacing = _inputState.CurrentSize * spacingRatio;
                if (minPixelSpacing < 1f) minPixelSpacing = 1f;
                float minLogical = minPixelSpacing * _logicToWorldRatio;
                float sqrDist = LogicPoint.SqrDistance(_lastAddedPoint, pointToAdd);
                if (sqrDist < minLogical * minLogical)
                {
                    return; // Too close
                }
            }
            
            _lastAddedPoint = pointToAdd;
            AddPoint(pointToAdd);

            // Notify Listeners (Optional: Real-time update)
            foreach (var listener in _listeners)
            {
                listener.OnStrokeUpdated(_currentStroke, pointToAdd);
            }

            // Draw Logic (Simplified Smoothing)
            // We use the raw list to look back
            int count = _currentStrokeRaw.Count;
            if (count >= 4)
            {
                _smoothingInputBuffer.Clear();
                _smoothingInputBuffer.Add(_currentStrokeRaw[count - 4]);
                _smoothingInputBuffer.Add(_currentStrokeRaw[count - 3]);
                _smoothingInputBuffer.Add(_currentStrokeRaw[count - 2]);
                _smoothingInputBuffer.Add(_currentStrokeRaw[count - 1]);

                _smoothingService.SmoothPoints(_smoothingInputBuffer, _smoothingOutputBuffer);
                _renderer.DrawPoints(_smoothingOutputBuffer);
            }
            else if (_inputState.IsEraser)
            {
                // Eraser draws immediately
                _singlePointBuffer.Clear();
                _singlePointBuffer.Add(pointToAdd);
                _renderer.DrawPoints(_singlePointBuffer);
            }
        }

        public void EndStroke()
        {
            if (_currentStroke == null) return;

            // Draw any remaining points for short strokes (brush mode)
            // If < 4 points, they weren't drawn by the smoothing logic in MoveStroke
            int pointCount = _currentStroke.Points.Count;
            if (!_inputState.IsEraser && pointCount > 0 && pointCount < 4)
            {
                _renderer.DrawPoints(_currentStroke.Points);
            }

            // Eraser Optimization: Discard effective-less strokes
            if (_inputState.IsEraser)
            {
                // We need access to active stroke IDs from history manager.
                // Assuming HistoryManager exposes a way to check, or we pass the list.
                // If HistoryManager doesn't expose it, we might skip this for now or expose it.
                // DrawingAppService used _historyManager.ActiveStrokeIds.
                
                // Note: Accessing ActiveStrokeIds might be expensive if it copies.
                // Ideally collision service should just take the history manager?
                // For now, let's assume we can access it.
                // If this fails compilation, I will need to fix HistoryManager.
                
                // However, I can't easily see HistoryManager content right now.
                // But I know DrawingAppService used it.
                
                // bool isEffective = _collisionService.IsEraserStrokeEffective(_currentStroke, _historyManager.ActiveStrokeIds);
                // if (!isEffective) { ... return; }
                
                // Let's implement it carefully.
                var activeIds = _historyManager.ActiveStrokeIds;
                if (!_collisionService.IsEraserStrokeEffective(_currentStroke, activeIds))
                {
                    _renderer.EndStroke();
                    _currentStroke = null;
                    return;
                }
            }

            // Command creation
            var strategyToUse = _inputState.IsEraser ? _eraserStrategy : _inputState.CurrentStrategy;

            var cmd = new DrawStrokeCommand(
                _currentStroke,
                strategyToUse,
                _inputState.CurrentRuntimeTexture
            );
            
            _historyManager.AddCommand(cmd);
            
            // Spatial Indexing
            _collisionService.Insert(_currentStroke);
            
            _renderer.EndStroke();

            // Notify Listeners
            foreach (var listener in _listeners)
            {
                listener.OnStrokeCompleted(_currentStroke);
            }

            _currentStroke = null;
        }

        private void AddPoint(LogicPoint point)
        {
            _currentStrokeRaw.Add(point);

            if (_currentStroke != null)
            {
                _singlePointArray[0] = point;
                _currentStroke.AddPoints(_singlePointArray);
            }
        }
    }
}
