using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Features.Drawing.Domain;
using Features.Drawing.Domain.Interface;
using Features.Drawing.Domain.ValueObject;
using Features.Drawing.Service;
using Features.Drawing.Domain.Algorithm;
using Features.Drawing.Domain.Data;
using Features.Drawing.Domain.Entity;
using Common.Constants;
using Common.Utils;
using Features.Drawing.App.Command;
using Features.Drawing.App.Interface;
using Features.Drawing.App.State;
using Common.Diagnostics;

namespace Features.Drawing.App
{
    /// <summary>
    /// Facade service that coordinates input, domain logic, and rendering.
    /// This is the main entry point for the drawing feature.
    /// </summary>
    public class DrawingAppService : MonoBehaviour, IDrawingFacade, IBrushRegistry
    {
        [Header("References")]
        [SerializeField] private MonoBehaviour _concreteRenderer;
        [SerializeField] private BrushStrategy _eraserStrategy; // Hard brush for eraser
        [SerializeField] private BrushStrategy[] _registeredBrushes; // Registry of available brushes
        
        [Header("Diagnostics")]
        [SerializeField] private bool _enableDiagnostics = true;

        private IStructuredLogger _logger;
        private PerformanceMonitor _perfMonitor;
        private TraceContext _activeStrokeTrace;

        // State Management
        private InputStateManager _inputState;
        
        // Public Accessors for UI/Preview
        public bool IsEraser => _inputState?.IsEraser ?? false;
        public float CurrentSize => _inputState?.CurrentSize ?? 10f;
        public Color CurrentColor => _inputState?.CurrentColor ?? Color.black;
        public BrushStrategy EraserStrategy => _eraserStrategy;

        // Optimization State
        private LogicPoint _lastAddedPoint;
        private Vector2 _currentStabilizedPos;
        private long _nextSequenceId = 1;

        
        // Services
        private IStrokeRenderer _renderer;
        private StrokeSmoothingService _smoothingService;
        private DrawingHistoryManager _historyManager;

        // Buffers
        private List<LogicPoint> _currentStrokeRaw = new List<LogicPoint>(1024);
        private List<LogicPoint> _smoothingInputBuffer = new List<LogicPoint>(8);
        private List<LogicPoint> _smoothingOutputBuffer = new List<LogicPoint>(64);
        private List<LogicPoint> _singlePointBuffer = new List<LogicPoint>(1);
        private readonly LogicPoint[] _singlePointArray = new LogicPoint[1];

        // Current stroke state capturing
        private StrokeEntity _currentStroke;

        private StrokeCollisionService _collisionService;
        
        private float _logicToWorldRatio = DrawingConstants.LOGIC_TO_WORLD_RATIO;

        // Network Integration
        private Features.Drawing.Service.Network.DrawingNetworkService _networkService;

        // Events
        public event System.Action OnStrokeStarted;

        private IEnumerator Start()
        {
            // Performance: Limit frame rate to 60 FPS
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;

            if (_enableDiagnostics)
            {
                // Create default logger
                IStructuredLogger logger = new StructuredLogger("DrawingApp", 10, true);
                _perfMonitor = gameObject.AddComponent<PerformanceMonitor>();
                _perfMonitor.Initialize(logger);
                
                // Temporary DI setup for Logger if needed
            }

            if (_concreteRenderer == null)
                _concreteRenderer = FindRendererComponent();

            if (_concreteRenderer is IRendererInitializer initializer)
            {
                yield return initializer.InitializeAsync();
            }

            IStrokeRenderer renderer = _concreteRenderer as IStrokeRenderer;
            
            if (renderer == null)
            {
                Debug.LogError("DrawingAppService: CanvasRenderer does not implement IStrokeRenderer!");
            }
            
            // 3. Initialize App Logic
            // Note: We pass null for dependencies to trigger internal default creation if not already injected
            Initialize(renderer, null, null, null, _enableDiagnostics ? new StructuredLogger("DrawingApp", 10, true) : null);
        }

        private void Awake()
        {
            if (Application.platform == RuntimePlatform.IPhonePlayer && !Debug.isDebugBuild)
            {
                _enableDiagnostics = false;
            }
        }

        private void OnDestroy()
        {
            if (_networkService != null)
            {
                _networkService.OnRemoteStrokeCommitted -= CommitRemoteStroke;
            }
        }

        public void SetNetworkService(Features.Drawing.Service.Network.DrawingNetworkService networkService)
        {
            _networkService = networkService;
            if (_networkService != null)
            {
                // Subscribe to network events
                _networkService.OnRemoteStrokeCommitted += CommitRemoteStroke;
                _networkService.InitializeBrushRegistry(this);
            }
        }

        /// <summary>
        /// Dependency Injection Entry Point.
        /// Allows external systems (Zenject/Tests) to inject mock or specific implementations.
        /// </summary>
        public void Initialize(
            IStrokeRenderer renderer,
            StrokeSmoothingService smoothingService = null,
            StrokeCollisionService collisionService = null,
            DrawingHistoryManager historyManager = null,
            IStructuredLogger logger = null)
        {
            // Only set if not null (allow partial injection logic if needed, though usually all or nothing)
            if (_renderer == null) _renderer = renderer;
            
            // Diagnostics
            if (_logger == null) _logger = logger;

            // Lazy init services if not provided
            if (_smoothingService == null) 
                _smoothingService = smoothingService ?? new StrokeSmoothingService();
                
            if (_collisionService == null) 
                _collisionService = collisionService ?? new StrokeCollisionService();
            
            // HistoryManager depends on others
            if (_historyManager == null) 
                _historyManager = historyManager ?? new DrawingHistoryManager(_renderer, _smoothingService, _collisionService);

            // Init State Manager
            _inputState = new InputStateManager(_renderer, _eraserStrategy);

            // 3. Setup Resolution Handling
            if (renderer is ICanvasResolutionProvider resolutionProvider)
            {
                resolutionProvider.OnResolutionChanged += UpdateResolutionRatio;
                UpdateResolutionRatio(resolutionProvider.Resolution);
            }
        }

        private MonoBehaviour FindRendererComponent()
        {
            var components = FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] is IStrokeRenderer)
                {
                    return components[i];
                }
            }

            return null;
        }

        private void UpdateResolutionRatio(Vector2Int resolution)
        {
            // Calculate ratio based on logical resolution (65536) and max screen dimension
            // LogicPoint Space: 0-65535
            // Pixel Space: 0-Resolution
            // Ratio = 65535 / MaxDimension
            
            float maxDim = Mathf.Max(resolution.x, resolution.y);
            if (maxDim > 0)
            {
                _logicToWorldRatio = DrawingConstants.LOGICAL_RESOLUTION / maxDim;
            }
            else
            {
                _logicToWorldRatio = DrawingConstants.LOGIC_TO_WORLD_RATIO; // Fallback
            }

            _collisionService?.SetLogicToWorldRatio(_logicToWorldRatio);
        }

        // --- State Management ---

        public void SetBrushStrategy(BrushStrategy strategy, Texture2D runtimeTexture = null)
        {
            _inputState?.SetBrushStrategy(strategy, runtimeTexture);
        }

        public void SetColor(Color color)
        {
            _inputState?.SetColor(color);
        }

        public void SetSize(float size)
        {
            _inputState?.SetSize(size);
        }

        public void SetEraser(bool isEraser)
        {
            _inputState?.SetEraser(isEraser);
        }

        public void ClearCanvas()
        {
            // Create a clear command
            var cmd = new ClearCanvasCommand(_nextSequenceId++);
            
            // Execute immediately
            cmd.Execute(_renderer, _smoothingService);
            
            // Add to history
            _historyManager.AddCommand(cmd);
        }

        // --- Input Handling ---

        public void StartStroke(LogicPoint point)
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
                    { "point", point.ToString() }
                };
                _logger.Info("StrokeStarted", _activeStrokeTrace, meta);
            }
            if (_enableDiagnostics) Debug.Log($"[App] StartStroke ID:{_activeStrokeTrace.TraceId} Point:{point} Size:{_inputState.CurrentSize}");

            // Notify listeners (e.g. UI to close panels)
            OnStrokeStarted?.Invoke();
            
            // CRITICAL FIX: Force sync Renderer state with Service state.
            _inputState.SyncToRenderer();

            _currentStrokeRaw.Clear();

            // Create Domain Entity
            uint id = (uint)Random.Range(0, int.MaxValue); // Simple random ID
            uint seed = (uint)Random.Range(0, int.MaxValue);
            uint colorInt = ColorPacking.ToUInt(_inputState.CurrentColor);
            
            // Resolve Brush ID
            ushort brushId = GetBrushId(_inputState.CurrentStrategy);
            
            _currentStroke = new StrokeEntity(id, 0, brushId, seed, colorInt, _inputState.CurrentSize, _nextSequenceId++);

            // Network Sync: Begin Stroke
            if (_networkService != null && _networkService.isActiveAndEnabled)
            {
                _networkService.OnLocalStrokeStarted(id, brushId, _inputState.CurrentColor, _inputState.CurrentSize, _inputState.IsEraser);
            }

            _lastAddedPoint = point;
            AddPoint(point);
            _currentStabilizedPos = point.ToNormalized();

            // Network Sync: Send the first point immediately
            // This is critical because BeginStrokePacket does not contain coordinates.
            if (_networkService != null && _networkService.isActiveAndEnabled)
            {
                _networkService.OnLocalStrokeMoved(point);
            }
        }

        public void MoveStroke(LogicPoint point)
        {
            // Optimization: Eraser Deduplication (User Requirement)
            // "Eraser repeated drawing positions can be not recorded"
            // Filter out points that are too close to the last added point to avoid redundant collision checks and history data.
            if (_inputState.IsEraser)
            {
                // LogicPoint uses 0-65535. 
                // Convert size (pixels) to approximate logical units.
                // Assuming 1920px screen ~ 65535 units => factor ~ 34.
                // Threshold: 10% of brush size.
                // If brush is 20px, threshold is 2px ~ 70 units.
                float scale = _logicToWorldRatio;
                float threshold = (_inputState.CurrentSize * 0.1f) * scale;
                
                // Use squared distance for perf
                float sqrDist = LogicPoint.SqrDistance(_lastAddedPoint, point);
                if (sqrDist < threshold * threshold)
                {
                    return; // Skip this point
                }
            }

            LogicPoint pointToAdd = point;
            
            // Apply Stabilization (Anti-Shake)
            if (!_inputState.IsEraser && _inputState.CurrentStrategy != null && _inputState.CurrentStrategy.StabilizationFactor > 0.001f)
            {
                Vector2 target = point.ToNormalized();
                float dist = Vector2.Distance(target, _currentStabilizedPos);
                
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
                    return;
                }
            }

            AddPoint(pointToAdd);
            _lastAddedPoint = pointToAdd;
            
            // Log every 10th point or if distance is large? Just log count.
            if (_enableDiagnostics && _currentStrokeRaw.Count % 10 == 0)
            {
                 Debug.Log($"[App] MoveStroke ID:{_currentStroke?.Id} Count:{_currentStrokeRaw.Count} Last:{pointToAdd}");
            }

            // Network Sync: Move Stroke
            if (_networkService != null && _networkService.isActiveAndEnabled)
            {
                _networkService.OnLocalStrokeMoved(pointToAdd);
            }
        }

        public void EndStroke()
        {
            if (_currentStroke == null) return;
            
            _currentStroke.EndStroke();

            int pointCount = _currentStroke.Points.Count;
            if (!_inputState.IsEraser && pointCount > 0 && pointCount < 4)
            {
                _renderer.DrawPoints(_currentStroke.Points);
            }

            if (_enableDiagnostics) Debug.Log($"[App] EndStroke ID:{_currentStroke.Id} Points:{pointCount}");

            // FIX: Don't add empty strokes to history
            if (pointCount > 0)
            {
                // OPTIMIZATION: Discard eraser strokes that don't intersect with any existing ink.
                if (_inputState.IsEraser)
                {
                    bool isEffective = _collisionService.IsEraserStrokeEffective(_currentStroke, _historyManager.ActiveStrokeIds);
                    
                    if (!isEffective)
                    {
                        Debug.Log($"[Optimization] Eraser stroke discarded [ID: {_currentStroke.Id}] - Redundant (covered area or no ink).");
                        _renderer.EndStroke();
                        _currentStroke = null;
                        return;
                    }
                }

                // Create Command
                // Note: We copy the points from the domain entity (or raw list).
                // _currentStroke.Points is List<LogicPoint>.
                // We pass the current state configuration.
                
                // Fix: Eraser should use _eraserStrategy if available
                var strategyToUse = _inputState.IsEraser ? _eraserStrategy : _inputState.CurrentStrategy;

                var cmd = new DrawStrokeCommand(
                    _currentStroke.Id.ToString(),
                    _currentStroke.SequenceId,
                    new List<LogicPoint>(_currentStroke.Points),
                    strategyToUse,
                    _inputState.CurrentRuntimeTexture,
                    _inputState.CurrentColor,
                    _inputState.CurrentSize,
                    _inputState.IsEraser
                );
                
                _historyManager.AddCommand(cmd);
                
                // Spatial Indexing
                _collisionService.Insert(_currentStroke);

                // Network Sync: End Stroke
                if (_networkService != null && _networkService.isActiveAndEnabled)
                {
                    uint checksum = Features.Drawing.Service.Network.DrawingNetworkService.ComputeStrokeChecksum(_currentStroke.Points);
                    _networkService.OnLocalStrokeEnded(checksum, _currentStroke.Points.Count);
                }
            }
            
            // Serialization Check (Debug)
            // var bytes = StrokeSerializer.Serialize(_currentStroke);
            // Debug.Log($"[Stroke] Ended. Bytes: {bytes.Length}");
            
            _renderer.EndStroke();
            _currentStroke = null;
        }

        public void Undo()
        {
            if (!_historyManager.CanUndo) return;

            // Save state
            var savedColor = _inputState.CurrentColor;
            var savedSize = _inputState.CurrentSize;
            var savedEraser = _inputState.IsEraser;
            var savedStrategy = _inputState.CurrentStrategy;
            var savedRuntimeTex = _inputState.CurrentRuntimeTexture;

            _historyManager.Undo();
            
            // Restore state
            RestoreState(savedColor, savedSize, savedEraser, savedStrategy, savedRuntimeTex);
        }

        public void Redo()
        {
            if (!_historyManager.CanRedo) return;

            // Save state
            var savedColor = _inputState.CurrentColor;
            var savedSize = _inputState.CurrentSize;
            var savedEraser = _inputState.IsEraser;
            var savedStrategy = _inputState.CurrentStrategy;
            var savedRuntimeTex = _inputState.CurrentRuntimeTexture;

            _historyManager.Redo();
            
            // Restore state
            RestoreState(savedColor, savedSize, savedEraser, savedStrategy, savedRuntimeTex);
        }

        private void RestoreState(Color color, float size, bool isEraser, BrushStrategy strategy, Texture2D runtimeTex)
        {
            if (isEraser)
            {
                SetEraser(true);
                SetSize(size); 
            }
            else
            {
                SetBrushStrategy(strategy, runtimeTex);
                SetColor(color);
                SetSize(size);
            }
        }

        private void AddPoint(LogicPoint point)
        {
            if (_renderer == null) return;

            _currentStrokeRaw.Add(point);
            
            if (_currentStroke != null)
            {
                // Optimization: Use pre-allocated array to avoid GC allocation per point
                _singlePointArray[0] = point;
                _currentStroke.AddPoints(_singlePointArray);
            }

            int count = _currentStrokeRaw.Count;

            if (count >= 4)
            {
                // Sliding window smoothing
                _smoothingInputBuffer.Clear();
                _smoothingInputBuffer.Add(_currentStrokeRaw[count - 4]);
                _smoothingInputBuffer.Add(_currentStrokeRaw[count - 3]);
                _smoothingInputBuffer.Add(_currentStrokeRaw[count - 2]);
                _smoothingInputBuffer.Add(_currentStrokeRaw[count - 1]);
                
                _smoothingService.SmoothPoints(_smoothingInputBuffer, _smoothingOutputBuffer);
                _renderer.DrawPoints(_smoothingOutputBuffer);
            }
            else
            {
                if (_inputState.IsEraser)
                {
                    _singlePointBuffer.Clear();
                    _singlePointBuffer.Add(point);
                    _renderer.DrawPoints(_singlePointBuffer);
                }
            }
        }

        // --- Network Sync Helpers (Proposed) ---

        public void CommitRemoteStroke(StrokeEntity stroke)
        {
            if (stroke == null || stroke.Points.Count == 0) return;

            bool isEraser = stroke.BrushId == DrawingConstants.ERASER_BRUSH_ID;
            BrushStrategy strategy = isEraser ? _eraserStrategy : GetBrushStrategy(stroke.BrushId);

            // Create Command & Add to History
            // Note: We use the current runtime texture for now, but ideally this should be part of the stroke data if customized.
            var cmd = new DrawStrokeCommand(
                stroke.Id.ToString(),
                stroke.SequenceId,
                new List<LogicPoint>(stroke.Points),
                strategy,
                _inputState.CurrentRuntimeTexture, 
                ColorPacking.ToColor(stroke.ColorRGBA),
                stroke.Size,
                isEraser
            );
            
            // Execute (Draws it on main canvas)
            cmd.Execute(_renderer, _smoothingService);
            
            // Add to history
            _historyManager.AddCommand(cmd);
            
            // Spatial Index
            _collisionService.Insert(stroke);
            
            // Diagnostics
            if (_logger != null && _enableDiagnostics)
            {
                 // Log if needed
            }
        }
        
        public BrushStrategy GetBrushStrategy(ushort id)
        {
            if (id == DrawingConstants.ERASER_BRUSH_ID) return _eraserStrategy;
            if (id == DrawingConstants.UNKNOWN_BRUSH_ID)
            {
                Debug.LogWarning($"[DrawingAppService] Received UNKNOWN_BRUSH_ID. Falling back to current local strategy: {_inputState.CurrentStrategy?.name}");
                return _inputState.CurrentStrategy;
            }

            if (_registeredBrushes != null && id < _registeredBrushes.Length)
            {
                // Debug.Log($"[DrawingAppService] Resolved Brush ID {id} to '{_registeredBrushes[id].name}'");
                return _registeredBrushes[id];
            }
            
            Debug.LogWarning($"[DrawingAppService] Brush ID {id} out of bounds (Count: {_registeredBrushes?.Length ?? 0}). Fallback to default.");

            // Fallback for valid but out-of-bounds IDs (should not happen if registry is consistent)
            if (_registeredBrushes != null && _registeredBrushes.Length > 0) return _registeredBrushes[0];
            
            return _inputState.CurrentStrategy; // Last resort
        }

        private ushort GetBrushId(BrushStrategy strategy)
        {
            if (_inputState.IsEraser) return DrawingConstants.ERASER_BRUSH_ID;

            if (strategy == null)
            {
                Debug.LogWarning("[DrawingAppService] Brush strategy is null. Returning UNKNOWN_BRUSH_ID.");
                return DrawingConstants.UNKNOWN_BRUSH_ID;
            }
            
            if (_registeredBrushes != null)
            {
                for (int i = 0; i < _registeredBrushes.Length; i++)
                {
                    if (_registeredBrushes[i] == strategy) 
                    {
                        // Debug.Log($"[DrawingAppService] Found ID {i} for brush '{strategy.name}'");
                        return (ushort)i;
                    }
                }
            }
            
            Debug.LogWarning($"[DrawingAppService] Brush '{strategy.name}' NOT FOUND in registry! Current Registry: {string.Join(", ", _registeredBrushes != null ? System.Linq.Enumerable.Select(_registeredBrushes, b => b.name) : new string[0])}");
            return DrawingConstants.UNKNOWN_BRUSH_ID;
        }
    }
}
