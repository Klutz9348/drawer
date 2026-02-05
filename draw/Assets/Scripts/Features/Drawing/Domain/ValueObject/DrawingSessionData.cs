using System;
using System.Collections.Generic;

namespace Features.Drawing.Domain.ValueObject
{
    [Serializable]
    public class DrawingSessionData
    {
        public string Id;
        public string Name;
        public long CreatedAt;
        public long ModifiedAt;
        public List<StrokeData> Strokes = new List<StrokeData>();
        public int CanvasWidth;
        public int CanvasHeight;
    }
}
