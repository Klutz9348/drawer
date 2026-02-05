using System;
using System.Collections.Generic;

namespace Features.Drawing.Domain.ValueObject
{
    [Serializable]
    public class StrokeData
    {
        public uint Id;
        public ushort AuthorId;
        public ushort BrushId;
        public uint Seed;
        public float Size;
        public uint ColorRGBA;
        public long SequenceId;
        public List<LogicPointData> Points;
    }
}
