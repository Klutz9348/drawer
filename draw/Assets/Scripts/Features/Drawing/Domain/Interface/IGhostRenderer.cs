using UnityEngine;
using System.Collections.Generic;
using Features.Drawing.Domain.ValueObject;
using Features.Drawing.Domain;

namespace Features.Drawing.Domain.Interface
{
    public interface IGhostRenderer
    {
        Vector2Int Resolution { get; }
        float GetBrushSizeScale();
        void BeginFrame();
        void DrawGhostStroke(IEnumerable<LogicPoint> points, float size, Color color, bool isEraser, BrushStrategy strategy);
        void DrawGhostStamps(List<StampData> stamps, Color color, bool isEraser, BrushStrategy strategy);
        void EndStroke();
        void ConfigureBrush(BrushStrategy strategy, Texture2D runtimeTexture = null);
    }
}
