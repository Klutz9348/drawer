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
using Features.Drawing.App.Input;
using Common.Diagnostics;

namespace Features.Drawing.App
{
    /// <summary>
    /// Facade service that coordinates input, domain logic, and rendering.
    /// This is the main entry point for the drawing feature.
    /// Refactored to delegate responsibilities to specialized services.
    /// </summary>
    public class DrawingAppService : MonoBehaviour, IDrawingFacade, IBrushRegistry
    {
        [Header("References")]
        [SerializeField] private MonoBehaviour _concreteRenderer;
        // Serialized fields kept for Inspector compatibility, but used to initialize services via Context
        [SerializeField] private BrushStrategy _eraserStrategy; 
        [SerializeField] private BrushStrategy[] _registeredBrushes; 
        
        [Header("Diagnostics")]
        [SerializeField] private bool _enableDiagnostics = true;

        // Exposed for Context to read
        public BrushStrategy EraserStrategy => _eraserStrategy;
        public BrushStrategy[] RegisteredBrushes => _registeredBrushes;

        // Services
        private InputStateManager _inputState;
        private StrokeInputHandler _strokeInputHandler;
        private DrawingPersistenceService _persistenceService;
        private IBrushRegistry _brushRegistryService;
        private DrawingHistoryManager _historyManager;
        private IStrokeRenderer _renderer;
        private IStrokeSmoothingService _smoothingService;
        private IStructuredLogger _logger;
        private Features.Drawing.Service.Network.DrawingNetworkService _networkService;

        // Public Accessors for UI/Preview
        public bool IsEraser => _inputState?.IsEraser ?? false;
        public float CurrentSize => _inputState?.CurrentSize ?? 10f;
        public Color CurrentColor => _inputState?.CurrentColor ?? Color.black;
        
        // Sequence ID management (Application Layer responsibility)
        private long _nextSequenceId = 1;

        // Events
        public event System.Action OnStrokeStarted;

        private bool _isInitialized = false;

        private IEnumerator Start()
        {
            // Performance: Limit frame rate to 60 FPS
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;

            // Wait for DI initialization (DrawingContext runs before this due to DefaultExecutionOrder)
            yield return null;

            if (!_isInitialized)
            {
                // Fallback: If not initialized by Context (e.g. standalone test scene), try to self-initialize
                Debug.LogWarning("[DrawingAppService] Not initialized via Context. Attempting fallback initialization.");
                
                if (_concreteRenderer == null) _concreteRenderer = FindRendererComponent();
                var renderer = _concreteRenderer as IStrokeRenderer;
                
                if (renderer != null)
                {
                    if (renderer is IRendererInitializer initializer)
                    {
                        yield return initializer.InitializeAsync();
                    }

                    // Create minimal dependencies for fallback
                    var logger = _enableDiagnostics ? new StructuredLogger("DrawingApp", 10, true) : null;
                    var brushRegistry = new BrushRegistryService(_registeredBrushes, _eraserStrategy);
                    var inputState = new InputStateManager(renderer, _eraserStrategy);
                    var smoothing = new StrokeSmoothingService();
                    var collision = new StrokeCollisionService();
                    var history = new DrawingHistoryManager(renderer, smoothing, collision);
                    // Persistence is optional/null in fallback
                    
                    var inputHandler = new StrokeInputHandler(
                        inputState, renderer, smoothing, collision, history, 
                        null, brushRegistry, _eraserStrategy, logger
                    );

                    Initialize(renderer, inputState, inputHandler, null, brushRegistry, history, smoothing, logger);
                }
            }
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

        /// <summary>
        /// Dependency Injection Entry Point.
        /// </summary>
        public void Initialize(
            IStrokeRenderer renderer,
            InputStateManager inputState,
            StrokeInputHandler strokeInputHandler,
            DrawingPersistenceService persistenceService,
            IBrushRegistry brushRegistry,
            DrawingHistoryManager historyManager,
            IStrokeSmoothingService smoothingService,
            IStructuredLogger logger = null)
        {
            _renderer = renderer;
            _inputState = inputState;
            _strokeInputHandler = strokeInputHandler;
            _persistenceService = persistenceService;
            _brushRegistryService = brushRegistry;
            _historyManager = historyManager;
            _smoothingService = smoothingService;
            _logger = logger;
            
            _isInitialized = true;

            if (_logger != null)
            {
                _logger.Info("DrawingAppService initialized via DI.");
            }

            // Setup Resolution Handling
            if (_renderer is ICanvasResolutionProvider resolutionProvider)
            {
                resolutionProvider.OnResolutionChanged += UpdateResolutionRatio;
                UpdateResolutionRatio(resolutionProvider.Resolution);
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

        // --- IDrawingFacade Implementation ---

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
            _historyManager?.AddCommand(cmd);
        }

        public void Undo() => _historyManager?.Undo();
        public void Redo() => _historyManager?.Redo();

        // --- Input Handling (Delegated) ---

        public void StartStroke(LogicPoint point)
        {
            // Notify listeners (e.g. UI to close panels)
            OnStrokeStarted?.Invoke();
            
            _strokeInputHandler?.StartStroke(point, _nextSequenceId++);
        }

        public void MoveStroke(LogicPoint point)
        {
            _strokeInputHandler?.MoveStroke(point);
        }

        public void EndStroke()
        {
            _strokeInputHandler?.EndStroke();
        }

        // --- Persistence (Delegated) ---

        public async System.Threading.Tasks.Task SaveSessionAsync(string sessionId)
        {
            if (_persistenceService != null && _historyManager != null)
            {
                var history = _historyManager.GetFullHistory();
                await _persistenceService.SaveSessionAsync(sessionId, history);
            }
            else
            {
                _logger?.Error("Cannot save session: Service or History not initialized.");
            }
        }

        public async System.Threading.Tasks.Task LoadSessionAsync(string sessionId)
        {
            if (_persistenceService != null)
            {
                ClearCanvas(); // Clear before load
                var commands = await _persistenceService.LoadSessionAsync(sessionId);
                if (commands != null && _historyManager != null)
                {
                    _historyManager.ReplaceHistory(commands);
                }
            }
             else
            {
                _logger?.Error("Cannot load session: Service not initialized.");
            }
        }

        // --- IBrushRegistry Implementation (Delegated) ---

        public BrushStrategy GetBrushStrategy(ushort id)
        {
            return _brushRegistryService?.GetBrushStrategy(id);
        }

        public ushort GetBrushId(BrushStrategy strategy)
        {
            return _brushRegistryService?.GetBrushId(strategy) ?? DrawingConstants.UNKNOWN_BRUSH_ID;
        }

        // --- Helpers ---

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
            float maxDim = Mathf.Max(resolution.x, resolution.y);
            float ratio = DrawingConstants.LOGIC_TO_WORLD_RATIO; // Default fallback

            if (maxDim > 0)
            {
                ratio = DrawingConstants.LOGICAL_RESOLUTION / maxDim;
            }

            _strokeInputHandler?.SetLogicToWorldRatio(ratio);
        }

        private void CommitRemoteStroke(StrokeEntity stroke)
        {
            // Reconstruct command from entity
            var strategy = GetBrushStrategy(stroke.BrushId);
            if (strategy != null)
            {
                var cmd = new DrawStrokeCommand(stroke, strategy);
                cmd.Execute(_renderer, _smoothingService);
                _historyManager?.AddCommand(cmd);
            }
        }
    }
}
