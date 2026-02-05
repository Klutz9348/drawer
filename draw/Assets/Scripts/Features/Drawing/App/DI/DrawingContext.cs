using UnityEngine;
using Features.Drawing.Domain.Interface;
using Features.Drawing.Service;
using Features.Drawing.Service.Network;
using Common.Diagnostics;
using Features.Drawing.App.Data;
using Features.Drawing.App.Input;
using Features.Drawing.App.State;

namespace Features.Drawing.App.DI
{
    /// <summary>
    /// Composition Root for the Drawing Feature.
    /// Responsible for wiring up dependencies before the application starts.
    /// This replaces the ad-hoc initialization in DrawingAppService.
    /// </summary>
    [DefaultExecutionOrder(-100)] // Run before AppService
    public class DrawingContext : MonoBehaviour
    {
        [Header("Components")]
        [SerializeField] private DrawingAppService _appService;
        [SerializeField] private MonoBehaviour _rendererComponent;
        [SerializeField] private DrawingNetworkService _networkService;
        
        [Header("Configuration")]
        [SerializeField] private bool _enableDiagnostics = true;

        private void Awake()
        {
            if (_appService == null)
            {
                _appService = FindObjectOfType<DrawingAppService>();
                if (_appService == null)
                {
                    Debug.LogError("[DrawingContext] DrawingAppService not found!");
                    return;
                }
            }
            
            // 1. Resolve Renderer
            IStrokeRenderer renderer = null;
            if (_rendererComponent != null && _rendererComponent is IStrokeRenderer r)
            {
                renderer = r;
            }
            else
            {
                renderer = FindRenderer();
            }

            if (renderer == null)
            {
                Debug.LogError("[DrawingContext] Failed to resolve IStrokeRenderer! Ensure a CanvasRenderer is present.");
                return;
            }

            // 2. Create Services (Pure C#)
            // Note: In a full DI framework, these would be bound in a container.
            var logger = _enableDiagnostics ? new StructuredLogger("DrawingApp", 10, true) : null;
            var smoothing = new StrokeSmoothingService();
            var collision = new StrokeCollisionService();
            
            // HistoryManager needs renderer to execute Redo/Undo visual updates
            var history = new DrawingHistoryManager(renderer, smoothing, collision);

            // Brush Registry & Input State (Read config from AppService)
            var eraser = _appService.EraserStrategy;
            var brushes = _appService.RegisteredBrushes;
            
            var brushRegistry = new BrushRegistryService(brushes, eraser);
            var inputState = new InputStateManager(renderer, eraser);

            // Persistence
            var repoPath = System.IO.Path.Combine(Application.persistentDataPath, "Sessions");
            var repository = new LocalFileDrawingRepository(repoPath);
            var persistence = new DrawingPersistenceService(repository, brushRegistry, logger);

            // Network Service (Optional)
            if (_networkService == null) _networkService = FindObjectOfType<DrawingNetworkService>();

            // Input Handler (Coordinator)
            var inputHandler = new StrokeInputHandler(
                inputState, renderer, smoothing, collision, history, 
                _networkService, brushRegistry, eraser, logger
            );

            // 3. Inject into AppService
            _appService.Initialize(
                renderer, 
                inputState, 
                inputHandler, 
                persistence, 
                brushRegistry, 
                history, 
                smoothing, 
                logger
            );
            
            // 4. Network Injection
            if (_networkService != null)
            {
                _appService.SetNetworkService(_networkService);
            }
            
            Debug.Log("[DrawingContext] Dependencies Injected successfully.");
        }

        private IStrokeRenderer FindRenderer()
        {
            var components = FindObjectsOfType<MonoBehaviour>();
            foreach (var c in components)
            {
                if (c is IStrokeRenderer r) return r;
            }
            return null;
        }
    }
}
