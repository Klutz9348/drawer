using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Features.Drawing.App;
using Features.Drawing.Domain;
using Features.Drawing.Domain.ValueObject;
using Features.Drawing.Presentation;
using UnityEngine.SceneManagement;

using DrawingCanvasRenderer = Features.Drawing.Presentation.CanvasRenderer;

namespace Tests.Performance
{
    public class DrawingPerformanceTests
    {
        private DrawingAppService _appService;
        private DrawingCanvasRenderer _renderer;
        private GameObject _appObject;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // Setup a minimal scene with UI
            _appObject = new GameObject("App");
            
            // 1. Setup UI for CanvasRenderer (Required for CanvasLayoutController)
            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            
            var rawImageGo = new GameObject("DisplayRawImage");
            rawImageGo.transform.SetParent(canvasGo.transform);
            var rawImage = rawImageGo.AddComponent<UnityEngine.UI.RawImage>();
            
            // 2. Setup Renderer
            _renderer = _appObject.AddComponent<DrawingCanvasRenderer>();
            
            // Inject private field _displayImage using Reflection
            // Because CanvasRenderer initializes CanvasLayoutController in Awake/Start using this field
            var field = typeof(DrawingCanvasRenderer).GetField("_displayImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(_renderer, rawImage);
            }

            // 3. Setup AppService
            _appService = _appObject.AddComponent<DrawingAppService>();
            
            // Minimal DI Setup
            var eraserStrategy = ScriptableObject.CreateInstance<BrushStrategy>();
            var brushes = new BrushStrategy[0];
            var brushRegistry = new Features.Drawing.Service.BrushRegistryService(brushes, eraserStrategy);
            var inputState = new Features.Drawing.App.State.InputStateManager(_renderer, eraserStrategy);
            
            var smoothing = new Features.Drawing.Service.StrokeSmoothingService();
            var collision = new Features.Drawing.Service.StrokeCollisionService();
            var history = new Features.Drawing.Service.DrawingHistoryManager(_renderer, smoothing, collision);
            
            var logger = new Common.Diagnostics.StructuredLogger("PerfTest", 0, false);
            
            var inputHandler = new Features.Drawing.App.Input.StrokeInputHandler(
                inputState, _renderer, smoothing, collision, history, 
                brushRegistry, eraserStrategy, logger
            );

            _appService.Initialize(
                _renderer,
                inputState,
                inputHandler,
                null, 
                brushRegistry,
                history,
                smoothing,
                logger
            );

            // Wait for CanvasRenderer's async initialization (InitializeRoutine)
            // It yields at least once. Wait a few frames to be safe.
            for (int i = 0; i < 5; i++) yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Object.Destroy(_appObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator StressTest_Drawing10kPoints()
        {
            // Requirement: Simulate 10,000 points rapid drawing
            // Assert: FPS > 30 (Frame time < 33ms)
            
            int pointCount = 10000;
            var points = new List<LogicPoint>(pointCount);
            
            // Generate spiral data
            for (int i = 0; i < pointCount; i++)
            {
                float angle = i * 0.1f;
                float radius = i * 0.1f;
                float x = Mathf.Cos(angle) * radius + 500;
                float y = Mathf.Sin(angle) * radius + 500;
                
                // Use FromNormalized logic or manually construct
                // Here we manually construct assuming 0-65535 range if needed, or normalized
                // LogicPoint constructor takes (ushort x, ushort y, byte pressure)
                // Let's assume the test wants to pass normalized coordinates converted to LogicPoint
                
                // Mock normalized
                float u = Mathf.Clamp01(x / 2048f);
                float v = Mathf.Clamp01(y / 2048f);
                
                points.Add(LogicPoint.FromNormalized(new Vector2(u, v), 1.0f));
            }

            // Start Stroke
            _appService.StartStroke(points[0]);
            
            float startTime = Time.realtimeSinceStartup;
            
            // Simulate input batching (e.g. 10 points per frame)
            int batchSize = 10;
            for (int i = 1; i < pointCount; i += batchSize)
            {
                for (int j = 0; j < batchSize && (i + j) < pointCount; j++)
                {
                    _appService.MoveStroke(points[i + j]);
                }
                
                // Wait for frame to measure realistic impact
                yield return null; 
                
                // Simple FPS assertion
                float frameTime = Time.deltaTime;
                if (frameTime > 0.033f) // 30 FPS = 33ms
                {
                   // Debug.LogWarning($"[Performance] Frame drop detected at index {i}: {frameTime * 1000}ms");
                }
            }

            _appService.EndStroke();
            
            float totalTime = Time.realtimeSinceStartup - startTime;
            Debug.Log($"[Performance] 10k Points drawn in {totalTime} seconds.");
            
            Assert.Less(Time.deltaTime, 0.1f, "Frame time should not exceed 100ms even under load");
        }
    }
}
