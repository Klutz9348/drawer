using UnityEngine;
using Features.Drawing.Domain.Interface;
using Features.Drawing.Service;
using Common.Diagnostics;
using Features.Drawing.App.Data;
using Features.Drawing.App.Input;
using Features.Drawing.App.State;

using System.Linq;

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

            // Input Handler (Coordinator)
            var inputHandler = new StrokeInputHandler(
                inputState, renderer, smoothing, collision, history, 
                brushRegistry, eraser, logger
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
            
            Debug.Log("[DrawingContext] Dependencies Injected successfully.");
        }

        private IStrokeRenderer FindRenderer()
        {
            // 1. Try GetComponent on self or children
            var r = GetComponentInChildren<IStrokeRenderer>();
            if (r != null) return r;

            // 2. Try finding any global implementation
            // Modified to avoid circular dependency on Presentation assembly
            var allMonos = FindObjectsOfType<MonoBehaviour>();
            foreach (var mono in allMonos)
            {
                if (mono is IStrokeRenderer renderer)
                {
                    return renderer;
                }
            }

            return null;
        }
    }
}
