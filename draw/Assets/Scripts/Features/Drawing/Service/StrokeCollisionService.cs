using System.Collections.Generic;
using UnityEngine;
using Features.Drawing.Domain.Entity;
using Features.Drawing.Domain.Algorithm;
using Features.Drawing.Domain.ValueObject;
using Common.Constants;

using Features.Drawing.Domain.Interface;

namespace Features.Drawing.Service
{
    public class StrokeCollisionService : IStrokeCollisionService
    {
        private readonly StrokeSpatialIndex _spatialIndex;
        
        // Zero-GC Buffers
        private readonly List<StrokeEntity> _inkBuffer = new List<StrokeEntity>(32);
        private readonly List<StrokeEntity> _eraserBuffer = new List<StrokeEntity>(16);

        // Dynamic ratio, initialized to default but updateable via SetLogicToWorldRatio
        private float _logicToWorldRatio = DrawingConstants.LOGIC_TO_WORLD_RATIO;

        public StrokeCollisionService()
        {
            _spatialIndex = new StrokeSpatialIndex();
        }

        public void SetLogicToWorldRatio(float ratio)
        {
            _logicToWorldRatio = ratio;
        }

        public void Insert(StrokeEntity stroke)
        {
            _spatialIndex.Insert(stroke);
        }

        public void Clear()
        {
            _spatialIndex.Clear();
        }

        /// <summary>
        /// Checks if an eraser stroke actually intersects with any active ink strokes.
        /// Returns true if it touches at least one ink stroke that is not covered by a later eraser.
        /// </summary>
        public bool IsEraserStrokeEffective(StrokeEntity eraserStroke, HashSet<string> activeStrokeIds)
        {
            if (eraserStroke == null || eraserStroke.Points.Count == 0) return false;

            Rect bounds = CalculateStrokeBounds(eraserStroke);
            var candidates = _spatialIndex.Query(bounds);

            // Separate candidates into Inks and Erasers
            _inkBuffer.Clear();
            _eraserBuffer.Clear();

            foreach (var s in candidates)
            {
                if (!activeStrokeIds.Contains(s.Id.ToString())) continue;

                if (s.BrushId == DrawingConstants.ERASER_BRUSH_ID)
                {
                    _eraserBuffer.Add(s);
                }
                else
                {
                    _inkBuffer.Add(s);
                }
            }

            // If no ink at all, discard immediately
            if (_inkBuffer.Count == 0)
            {
                return false;
            }

            float scale = _logicToWorldRatio;
            float eraserRadius = eraserStroke.Size * 0.5f;

            // Iterate over sample points of the current eraser
            int stride = 5; // Optimization: Check every 5th point
            for (int i = 0; i < eraserStroke.Points.Count; i += stride)
            {
                var p = eraserStroke.Points[i];

                // Check against inks
                foreach (var ink in _inkBuffer)
                {
                    // 1. Check distance to ink (using the relaxed threshold)
                    float inkThreshold = (eraserRadius + ink.Size * 0.5f) * scale * 1.2f;
                    float distToInkSqr = SqrDistancePointStroke(p, ink);

                    if (distToInkSqr < inkThreshold * inkThreshold)
                    {
                        // This point touches this ink.
                        // Now check if it is obscured by any EXISTING eraser that is NEWER than the ink.
                        bool isObscured = false;

                        foreach (var existingEraser in _eraserBuffer)
                        {
                            // Only check erasers that came AFTER the ink (and thus could cover it)
                            // AND exclude the current eraser itself
                            if (existingEraser.SequenceId > ink.SequenceId && existingEraser.Id != eraserStroke.Id)
                            {
                                // Check if point p is covered by existingEraser
                                // Use strict threshold (actual size) for covering check
                                float coverThreshold = (existingEraser.Size * 0.5f) * scale;
                                float distToEraserSqr = SqrDistancePointStroke(p, existingEraser);

                                if (distToEraserSqr < coverThreshold * coverThreshold)
                                {
                                    isObscured = true;
                                    break;
                                }
                            }
                        }

                        if (!isObscured)
                        {
                            // Found at least one point that touches ink and is NOT obscured.
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private float SqrDistancePointStroke(LogicPoint pE, StrokeEntity candidateStroke)
        {
            // Convert LogicPoint to Vector2 for math
            Vector2 vE = new Vector2(pE.X, pE.Y);

            float minSqrDist = float.MaxValue;
            var candidatePoints = candidateStroke.Points;

            for (int j = 0; j < candidatePoints.Count - 1; j++)
            {
                var pA = candidatePoints[j];
                var pB = candidatePoints[j + 1];
                Vector2 vA = new Vector2(pA.X, pA.Y);
                Vector2 vB = new Vector2(pB.X, pB.Y);

                float sqrDist = SqrDistancePointSegment(vE, vA, vB);
                if (sqrDist < minSqrDist)
                {
                    minSqrDist = sqrDist;
                }
            }
            return minSqrDist;
        }

        private float SqrDistancePointSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            Vector2 ap = p - a;
            float t = Vector2.Dot(ap, ab) / Vector2.Dot(ab, ab);
            t = Mathf.Clamp01(t);
            Vector2 closest = a + ab * t;
            return (p - closest).sqrMagnitude;
        }

        private Rect CalculateStrokeBounds(StrokeEntity stroke)
        {
            if (stroke.Points.Count == 0) return Rect.zero;

            ushort minX = ushort.MaxValue;
            ushort minY = ushort.MaxValue;
            ushort maxX = ushort.MinValue;
            ushort maxY = ushort.MinValue;

            foreach (var p in stroke.Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            // Return with some padding? LogicPoint is 0-65535.
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }
}
