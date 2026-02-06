using System.Collections;
using UnityEngine;
using Features.Drawing.Domain;
using Features.Drawing.Domain.Interface;
using Features.Drawing.Domain.ValueObject;
using Features.Drawing.Service;
using Features.Drawing.Domain.Entity;
using Common.Constants;
using Features.Drawing.App.Command;
using Features.Drawing.App.Interface;
using Features.Drawing.App.Data;
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
        
        // [Header("Diagnostics")]
        // [SerializeField] private bool _enableDiagnostics = true;

        // Exposed for Context to read
        public BrushStrategy EraserStrategy => _eraserStrategy;
        public BrushStrategy[] RegisteredBrushes => _registeredBrushes;

        // Services
        private InputStateManager _inputState;
        private StrokeInputHandler _strokeInputHandler;
        private IDrawingPersistenceService _persistenceService;
        private IBrushRegistry _brushRegistryService;
        private IDrawingHistoryManager _historyManager;
        private IStrokeRenderer _renderer;
        private IStrokeSmoothingService _smoothingService;
        private IStructuredLogger _logger;

        private int _lastUndoFrame = -1;
        private int _lastRedoFrame = -1;
        
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
                TryInitializeFallback();
            }

            // CRITICAL: Ensure Renderer is initialized (Async) regardless of injection method
            // This was previously missing in the DI path
            if (_renderer is IRendererInitializer initializer)
            {
                yield return initializer.InitializeAsync();
            }

            if (!_isInitialized)
            {
                Debug.LogWarning("[DrawingAppService] Initialization incomplete. Ensure an IStrokeRenderer is present or add DrawingContext.");
            }
        }

        private void Awake()
        {
            // Application-level Camera Setup
            if (Camera.main != null)
            {
                Camera.main.backgroundColor = Color.white;
                Camera.main.clearFlags = CameraClearFlags.SolidColor;
            }

            if (Application.platform == RuntimePlatform.IPhonePlayer && !Debug.isDebugBuild)
            {
                // _enableDiagnostics = false;
            }
        }

        private void OnDestroy()
        {
        }

        /// <summary>
        /// Dependency Injection Entry Point.
        /// </summary>
        public void Initialize(
            IStrokeRenderer renderer,
            InputStateManager inputState,
            StrokeInputHandler strokeInputHandler,
            IDrawingPersistenceService persistenceService,
            IBrushRegistry brushRegistry,
            IDrawingHistoryManager historyManager,
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

        public void Undo()
        {
            if (Time.frameCount == _lastUndoFrame) return;
            _lastUndoFrame = Time.frameCount;
            _historyManager?.Undo();
        }

        public void Redo()
        {
            if (Time.frameCount == _lastRedoFrame) return;
            _lastRedoFrame = Time.frameCount;
            _historyManager?.Redo();
        }

        // --- Input Handling (Delegated) ---

        public void RegisterStrokeListener(IStrokeEventListener listener)
        {
            _strokeInputHandler?.RegisterListener(listener);
        }

        public void UnregisterStrokeListener(IStrokeEventListener listener)
        {
            _strokeInputHandler?.UnregisterListener(listener);
        }

        public void StartStroke(LogicPoint point)
        {
            StartStroke(point, 0); // Default to local author
        }

        public void StartStroke(LogicPoint point, int authorId)
        {
            // Notify listeners (e.g. UI to close panels)
            OnStrokeStarted?.Invoke();
            
            _strokeInputHandler?.StartStroke(point, _nextSequenceId++, authorId);
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

        private bool TryInitializeFallback()
        {
            if (_isInitialized) return true;

            IStrokeRenderer renderer = null;
            if (_concreteRenderer != null && _concreteRenderer is IStrokeRenderer concrete)
            {
                renderer = concrete;
            }
            else
            {
                var resolved = FindRendererComponent();
                if (resolved != null)
                {
                    renderer = resolved as IStrokeRenderer;
                }
            }

            if (renderer == null) return false;

            var eraser = _eraserStrategy;
            var brushes = _registeredBrushes ?? new BrushStrategy[0];
            var brushRegistry = new BrushRegistryService(brushes, eraser);
            var inputState = new InputStateManager(renderer, eraser);
            var smoothing = new StrokeSmoothingService();
            var collision = new StrokeCollisionService();
            var history = new DrawingHistoryManager(renderer, smoothing, collision);
            var repoPath = System.IO.Path.Combine(Application.persistentDataPath, "Sessions");
            var repository = new LocalFileDrawingRepository(repoPath);
            var persistence = new DrawingPersistenceService(repository, brushRegistry, null);
            var inputHandler = new StrokeInputHandler(
                inputState,
                renderer,
                smoothing,
                collision,
                history,
                brushRegistry,
                eraser,
                null
            );

            Initialize(
                renderer,
                inputState,
                inputHandler,
                persistence,
                brushRegistry,
                history,
                smoothing,
                null
            );

            return _isInitialized;
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
    }
}
