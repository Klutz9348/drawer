using System.Collections.Generic;
using UnityEngine;
using Features.Drawing.Domain.ValueObject;

using Features.Drawing.Domain.Interface;

namespace Features.Drawing.Service
{
    /// <summary>
    /// Responsible for smoothing raw input points into high-quality curves.
    /// Uses Catmull-Rom spline interpolation.
    /// </summary>
    public class StrokeSmoothingService : IStrokeSmoothingService
    {
        private const int MinStepsPerSegment = 1;
        private const int MaxStepsPerSegment = 8;
        private const float StepsPerNormalizedUnit = 64f;

        /// <summary>
        /// Generates smoothed LogicPoints from a sequence of control points.
        /// Writes result into the provided output buffer to avoid GC.
        /// </summary>
        /// <param name="controlPoints">Input control points (window)</param>
        /// <param name="outputBuffer">Buffer to write smoothed points into</param>
        public void SmoothPoints(List<LogicPoint> controlPoints, List<LogicPoint> outputBuffer)
        {
            if (outputBuffer == null) return;
            outputBuffer.Clear();

            if (controlPoints == null || controlPoints.Count < 3)
            {
                if (controlPoints != null) outputBuffer.AddRange(controlPoints);
                return;
            }
            
            // We need at least 4 points for Catmull-Rom. 
            // If less, we might need to duplicate start/end points.
            // Simplified logic here for MVP.
            
            for (int i = 0; i < controlPoints.Count - 3; i++)
            {
                LogicPoint p0 = controlPoints[i];
                LogicPoint p1 = controlPoints[i + 1];
                LogicPoint p2 = controlPoints[i + 2];
                LogicPoint p3 = controlPoints[i + 3];

                int steps = GetSteps(p1, p2);
                for (int t = 0; t < steps; t++)
                {
                    float tNorm = t / (float)steps;
                    LogicPoint interpolated = CatmullRom(p0, p1, p2, p3, tNorm);
                    outputBuffer.Add(interpolated);
                }
            }
            
            // Ensure the last control point of the curve segment (p2) is added
            // Note: Catmull-Rom from p1 to p2. p2 is reached when t=1 (which is start of next segment)
            // but we only loop t < steps. So we might miss p2 if we don't handle end correctly.
            // For continuous streaming, we don't add the very last point yet unless it's the end of stroke.
            // But here we are just smoothing a window.
            // outputBuffer.Add(controlPoints[controlPoints.Count - 2]); 
        }

        private LogicPoint CatmullRom(LogicPoint p0, LogicPoint p1, LogicPoint p2, LogicPoint p3, float t)
        {
            // Convert to float for calculation
            Vector2 v0 = p0.ToNormalized();
            Vector2 v1 = p1.ToNormalized();
            Vector2 v2 = p2.ToNormalized();
            Vector2 v3 = p3.ToNormalized();

            float t2 = t * t;
            float t3 = t2 * t;

            Vector2 pos = 0.5f * (
                (2f * v1) +
                (-v0 + v2) * t +
                (2f * v0 - 5f * v1 + 4f * v2 - v3) * t2 +
                (-v0 + 3f * v1 - 3f * v2 + v3) * t3
            );

            // Interpolate pressure linearly
            float pressure = Mathf.Lerp(p1.GetNormalizedPressure(), p2.GetNormalizedPressure(), t);

            return LogicPoint.FromNormalized(pos, pressure);
        }

        private int GetSteps(LogicPoint a, LogicPoint b)
        {
            Vector2 v1 = a.ToNormalized();
            Vector2 v2 = b.ToNormalized();
            float dist = Vector2.Distance(v1, v2);
            int steps = Mathf.CeilToInt(dist * StepsPerNormalizedUnit);
            return Mathf.Clamp(steps, MinStepsPerSegment, MaxStepsPerSegment);
        }
    }
}
