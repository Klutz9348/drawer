using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Features.Drawing.App;
using Features.Drawing.Domain.Interface;
using Features.Drawing.Domain.Entity;
using Features.Drawing.Domain.ValueObject;
using Features.Drawing.Domain;

namespace Tests.App
{
    public class DrawingFacadeTests
    {
        private GameObject _testObject;
        private DrawingAppService _appService;
        private MockStrokeRenderer _mockRenderer;

        [SetUp]
        public void Setup()
        {
            _testObject = new GameObject("TestApp");
            _appService = _testObject.AddComponent<DrawingAppService>();
            _mockRenderer = _testObject.AddComponent<MockStrokeRenderer>();

            // Inject Renderer
            var field = typeof(DrawingAppService).GetField("_concreteRenderer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(_appService, _mockRenderer);
            }
        }

        [TearDown]
        public void Teardown()
        {
            if (_testObject != null)
            {
                Object.Destroy(_testObject);
            }
        }

        [UnityTest]
        public IEnumerator StartStroke_InitializesStrokeState()
        {
            // Wait for Start()
            yield return null;

            // Act
            _appService.StartStroke(LogicPoint.FromNormalized(Vector2.zero, 0.5f));
            
            // Assert
            // Since we can't easily access private state, we verify via public properties or side effects
            // e.g. CurrentSize should be accessible
            Assert.IsTrue(_appService.CurrentSize > 0);
            
            // End Stroke
            _appService.EndStroke();
            
            // Verify Renderer was called (EndStroke usually commits to history -> calls renderer)
            // Wait a frame for command execution if needed
            yield return null;
            
            Assert.IsTrue(_mockRenderer.DrawPointsCalled, "Renderer.DrawPoints should be called after EndStroke");
        }

        [UnityTest]
        public IEnumerator SetEraser_UpdatesFacadeState()
        {
            yield return null;

            // Act
            _appService.SetEraser(true);

            // Assert
            Assert.IsTrue(_appService.IsEraser);
            Assert.IsTrue(_mockRenderer.SetEraserCalled);

            _appService.SetEraser(false);
            Assert.IsFalse(_appService.IsEraser);
        }

        [UnityTest]
        public IEnumerator SetColor_UpdatesStateAndRenderer()
        {
            yield return null;
            
            // Act
            _appService.SetColor(Color.red);
            
            // Assert
            Assert.AreEqual(Color.red, _appService.CurrentColor); // Assuming I can access CurrentColor via reflection or public prop
            // Actually InputStateManager logic: IsEraser -> false
            Assert.IsFalse(_appService.IsEraser);
            Assert.IsTrue(_mockRenderer.SetBrushColorCalled);
        }

        [UnityTest]
        public IEnumerator SetSize_UpdatesStateAndRenderer()
        {
            yield return null;
            
            // Act
            _appService.SetSize(25.0f);
            
            // Assert
            Assert.AreEqual(25.0f, _appService.CurrentSize);
            Assert.IsTrue(_mockRenderer.SetBrushSizeCalled);
        }

        [UnityTest]
        public IEnumerator ClearCanvas_CallsRendererAndHistory()
        {
            yield return null;
            
            // Act
            _appService.ClearCanvas();
            
            // Assert
            // Note: ClearCanvas creates a command which executes immediately.
            // The command execution calls _renderer.ClearCanvas() IF the command implements it.
            // ClearCanvasCommand calls renderer.ClearCanvas().
            Assert.IsTrue(_mockRenderer.ClearCanvasCalled);
        }

        [UnityTest]
        public IEnumerator Undo_RestoresHistory()
        {
            yield return null;

            // 1. Create a stroke
            _appService.StartStroke(new LogicPoint(100, 100, 128));
            _appService.MoveStroke(new LogicPoint(110, 110, 128));
            _appService.MoveStroke(new LogicPoint(120, 120, 128));
            _appService.MoveStroke(new LogicPoint(130, 130, 128)); // Need enough points
            _appService.EndStroke();
            
            // Reset flags
            _mockRenderer.DrawPointsCalled = false;
            _mockRenderer.RestoreFromBackBufferCalled = false;
            
            // 2. Undo
            _appService.Undo();
            
            // Assert
            // Undo should trigger RedrawHistory -> RestoreFromBackBuffer
            Assert.IsTrue(_mockRenderer.RestoreFromBackBufferCalled);
        }

        [UnityTest]
        public IEnumerator MoveStroke_DrawsImmediateFeedback()
        {
            yield return null;
            
            _appService.StartStroke(new LogicPoint(100, 100, 128));
            
            // Reset flag from StartStroke (if any)
            _mockRenderer.DrawPointsCalled = false;
            
            // Add enough points to trigger smoothing/drawing (min 4 usually)
            _appService.MoveStroke(new LogicPoint(110, 110, 128));
            _appService.MoveStroke(new LogicPoint(120, 120, 128));
            _appService.MoveStroke(new LogicPoint(130, 130, 128));
            _appService.MoveStroke(new LogicPoint(140, 140, 128));
            
            Assert.IsTrue(_mockRenderer.DrawPointsCalled);
            
            _appService.EndStroke();
        }

        // Mock Implementation
        public class MockStrokeRenderer : MonoBehaviour, IStrokeRenderer
        {
            public bool DrawPointsCalled = false;
            public bool ConfigureBrushCalled = false;
            public bool SetBrushSizeCalled = false;
            public bool SetBrushColorCalled = false;
            public bool SetEraserCalled = false;
            public bool ClearCanvasCalled = false;
            public bool RestoreFromBackBufferCalled = false;
            
            public void ConfigureBrush(BrushStrategy strategy, Texture2D runtimeTexture = null) 
            {
                ConfigureBrushCalled = true;
            }

            public void SetBrushSize(float size) 
            {
                SetBrushSizeCalled = true;
            }

            public void SetBrushColor(Color color) 
            {
                SetBrushColorCalled = true;
            }

            public void SetEraser(bool isEraser) 
            {
                SetEraserCalled = true;
            }
            
            public void DrawPoints(System.Collections.Generic.IEnumerable<LogicPoint> points)
            {
                DrawPointsCalled = true;
            }

            public void EndStroke() { }
            
            public void ClearCanvas() 
            {
                ClearCanvasCalled = true;
            }
            
            public void SetBakingMode(bool enabled) { }
            
            public void RestoreFromBackBuffer() 
            {
                RestoreFromBackBufferCalled = true;
            }
        }
    }
}
