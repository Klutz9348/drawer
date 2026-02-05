using System.Collections.Generic;
using Features.Drawing.Domain.ValueObject;

namespace Features.Drawing.Domain.Entity
{
    /// <summary>
    /// Represents a complete stroke in the drawing session.
    /// Core entity for domain logic.
    /// </summary>
    public class StrokeEntity
    {
        public uint Id { get; private set; }
        public ushort AuthorId { get; private set; }
        public ushort BrushId { get; private set; }
        public uint Seed { get; private set; }
        public bool IsEnded { get; private set; }
        public float Size { get; private set; }
        
        // Color encoded as integer (RGBA) for simple serialization
        public uint ColorRGBA { get; private set; }
        
        // Convenience property for Unity Color interaction
        public UnityEngine.Color Color => Common.Utils.ColorPacking.ToColor(ColorRGBA);
        
        // Sequence ID to track rendering order (higher means drawn later)
        public long SequenceId { get; private set; }

        private readonly List<LogicPoint> _points;
        public IReadOnlyList<LogicPoint> Points => _points;

        public StrokeEntity(uint id, ushort authorId, ushort brushId, uint seed, uint colorRGBA, float size, long sequenceId = 0)
        {
            Id = id;
            AuthorId = authorId;
            BrushId = brushId;
            Seed = seed;
            ColorRGBA = colorRGBA;
            Size = size;
            SequenceId = sequenceId;
            _points = new List<LogicPoint>();
            IsEnded = false;
        }

        public StrokeEntity(uint id, ushort authorId, ushort brushId, uint seed, UnityEngine.Color color, float size, long sequenceId = 0, IEnumerable<LogicPoint> points = null)
            : this(id, authorId, brushId, seed, Common.Utils.ColorPacking.ToUInt(color), size, sequenceId)
        {
            if (points != null)
            {
                _points.AddRange(points);
            }
        }

        public void AddPoints(IEnumerable<LogicPoint> newPoints)
        {
            if (IsEnded) return; // Should throw domain exception in strict mode
            _points.AddRange(newPoints);
        }

        public void EndStroke()
        {
            IsEnded = true;
        }
    }
}
