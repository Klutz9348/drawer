using System.Collections.Generic;
using Features.Drawing.Domain.Entity;

namespace Features.Drawing.Domain.Interface
{
    public interface IStrokeCollisionService
    {
        void SetLogicToWorldRatio(float ratio);
        void Insert(StrokeEntity stroke);
        void Clear();
        bool IsEraserStrokeEffective(StrokeEntity eraserStroke, HashSet<string> activeStrokeIds);
    }
}
