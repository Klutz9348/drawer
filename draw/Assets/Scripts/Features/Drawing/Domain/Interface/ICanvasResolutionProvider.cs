using UnityEngine;

namespace Features.Drawing.Domain.Interface
{
    public interface ICanvasResolutionProvider
    {
        event System.Action<Vector2Int> OnResolutionChanged;
        Vector2Int Resolution { get; }
    }
}
