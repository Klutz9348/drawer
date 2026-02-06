using UnityEngine;
using Features.Drawing.Domain;
using Features.Drawing.Domain.ValueObject;

namespace Features.Drawing.App.Interface
{
    public interface IDrawingFacade : IInputHandler
    {
        event System.Action OnStrokeStarted;

        bool IsEraser { get; }
        float CurrentSize { get; }
        Color CurrentColor { get; }
        BrushStrategy EraserStrategy { get; }

        void Undo();
        void Redo();

        void StartStroke(LogicPoint point, int authorId);
        void ClearCanvas();
        void SetBrushStrategy(BrushStrategy strategy, Texture2D runtimeTexture = null);
        void SetColor(Color color);
        void SetSize(float size);
        void SetEraser(bool isEraser);
    }
}
