using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Features.Drawing.App.Command;
using Features.Drawing.Domain;
using Features.Drawing.Domain.Entity;
using Features.Drawing.Domain.Interface;
using Features.Drawing.Domain.ValueObject;
using Features.Drawing.Service;

namespace Tests.Service
{
    internal sealed class FakeRenderer : IStrokeRenderer
    {
        public int ClearCount { get; private set; }
        public int RestoreCount { get; private set; }
        public int StartStrokeCount { get; private set; }
        public int DrawPointsCount { get; private set; }
        public int EndStrokeCount { get; private set; }

        public void ConfigureBrush(BrushStrategy strategy, Texture2D runtimeTexture = null) { }
        public void SetBrushSize(float size) { }
        public void SetBrushColor(Color color) { }
        public void SetEraser(bool isEraser) { }
        public void StartStroke(LogicPoint point, bool isEraser, float size, Color color) { StartStrokeCount++; }
        public void DrawPoints(IEnumerable<LogicPoint> points) { DrawPointsCount++; }
        public void EndStroke() { EndStrokeCount++; }
        public void ClearCanvas() { ClearCount++; }
        public void SetBakingMode(bool enabled) { }
        public void RestoreFromBackBuffer() { RestoreCount++; }
    }

    internal sealed class PassThroughSmoothingService : IStrokeSmoothingService
    {
        public void SmoothPoints(List<LogicPoint> controlPoints, List<LogicPoint> outputBuffer)
        {
            outputBuffer.Clear();
            outputBuffer.AddRange(controlPoints);
        }
    }

    internal sealed class NoOpCollisionService : IStrokeCollisionService
    {
        public void SetLogicToWorldRatio(float ratio) { }
        public void Insert(StrokeEntity stroke) { }
        public void Clear() { }
        public bool IsEraserStrokeEffective(StrokeEntity eraserStroke, HashSet<string> activeStrokeIds) => true;
    }

    public class DrawingHistoryManagerTests
    {

        private StrokeEntity CreateStroke(uint id, int pointCount)
        {
            var points = new List<LogicPoint>(pointCount);
            for (int i = 0; i < pointCount; i++)
            {
                float t = pointCount <= 1 ? 0f : i / (float)(pointCount - 1);
                points.Add(LogicPoint.FromNormalized(new Vector2(t, t), 1.0f));
            }

            return new StrokeEntity(id, 0, 0, 0, Color.black, 10f, 0, points);
        }

        [Test]
        public void Undo_RemovesSingleStroke_AndRedrawsRemaining()
        {
            var renderer = new FakeRenderer();
            var smoothing = new PassThroughSmoothingService();
            var history = new DrawingHistoryManager(renderer, smoothing, null);
            var strategy = ScriptableObject.CreateInstance<BrushStrategy>();

            var cmd1 = new DrawStrokeCommand(CreateStroke(1, 4), strategy);
            var cmd2 = new DrawStrokeCommand(CreateStroke(2, 4), strategy);
            var cmd3 = new DrawStrokeCommand(CreateStroke(3, 4), strategy);

            history.AddCommand(cmd1);
            history.AddCommand(cmd2);
            history.AddCommand(cmd3);

            Assert.AreEqual(3, history.History.Count);

            history.Undo();

            Assert.AreEqual(2, history.History.Count);
            Assert.IsTrue(history.CanRedo);

            Assert.AreEqual(1, renderer.RestoreCount);
            Assert.AreEqual(2, renderer.StartStrokeCount);
            Assert.AreEqual(2, renderer.EndStrokeCount);
            Assert.AreEqual(2, renderer.DrawPointsCount);
        }

        [Test]
        public void Undo_WhenHistoryEmpty_DoesNothing()
        {
            var renderer = new FakeRenderer();
            var smoothing = new PassThroughSmoothingService();
            var history = new DrawingHistoryManager(renderer, smoothing, null);

            history.Undo();

            Assert.AreEqual(0, renderer.RestoreCount);
            Assert.AreEqual(0, renderer.StartStrokeCount);
            Assert.AreEqual(0, renderer.DrawPointsCount);
            Assert.AreEqual(0, renderer.EndStrokeCount);
        }

        [Test]
        public void Undo_MultipleTimes_RemovesStrokesOneByOne()
        {
            var renderer = new FakeRenderer();
            var smoothing = new PassThroughSmoothingService();
            var history = new DrawingHistoryManager(renderer, smoothing, null);
            var strategy = ScriptableObject.CreateInstance<BrushStrategy>();

            history.AddCommand(new DrawStrokeCommand(CreateStroke(1, 4), strategy));
            history.AddCommand(new DrawStrokeCommand(CreateStroke(2, 4), strategy));
            history.AddCommand(new DrawStrokeCommand(CreateStroke(3, 4), strategy));

            history.Undo();
            history.Undo();

            Assert.AreEqual(1, history.History.Count);
            Assert.IsTrue(history.CanRedo);
            Assert.AreEqual(2, renderer.RestoreCount);
            Assert.AreEqual(3, renderer.StartStrokeCount);
            Assert.AreEqual(3, renderer.EndStrokeCount);
            Assert.AreEqual(3, renderer.DrawPointsCount);
        }
    }

    public class StrokeInputHandlerTests
    {
        [Test]
        public void StrokeInputHandler_PopulatesStrokePoints_ForUndoRedraw()
        {
            var renderer = new FakeRenderer();
            var smoothing = new PassThroughSmoothingService();
            var collision = new NoOpCollisionService();
            var history = new DrawingHistoryManager(renderer, smoothing, collision);

            var brush = ScriptableObject.CreateInstance<BrushStrategy>();
            var eraser = ScriptableObject.CreateInstance<BrushStrategy>();
            var registry = new BrushRegistryService(new[] { brush }, eraser);
            var inputState = new Features.Drawing.App.State.InputStateManager(renderer, eraser);
            inputState.SetBrushStrategy(brush);

            var inputHandler = new Features.Drawing.App.Input.StrokeInputHandler(
                inputState,
                renderer,
                smoothing,
                collision,
                history,
                null,
                registry,
                eraser,
                null
            );

            var p1 = LogicPoint.FromNormalized(new Vector2(0.1f, 0.1f), 1.0f);
            var p2 = LogicPoint.FromNormalized(new Vector2(0.9f, 0.9f), 1.0f);

            inputHandler.StartStroke(p1, 1);
            inputHandler.MoveStroke(p2);
            inputHandler.EndStroke();

            Assert.AreEqual(1, history.History.Count);

            var cmd = history.History[0] as DrawStrokeCommand;
            Assert.IsNotNull(cmd);
            Assert.Greater(cmd.Stroke.Points.Count, 0);
        }
    }
}
