using System.Collections.Generic;
using UnityEngine;
using Features.Drawing.Domain.ValueObject;
using Features.Drawing.Domain.Network;
using Features.Drawing.Domain.Interface;
using Features.Drawing.Domain.Logic;
using Common.Constants;
using Common.Utils;

namespace Features.Drawing.Service.Network
{
    /// <summary>
    /// Manages the state of a single remote stroke being drawn (The "Ghost").
    /// Buffers points and handles rendering to the Ghost Layer.
    /// Implements Client-Side Prediction (Extrapolation) to smooth out network jitter.
    /// </summary>
    public class RemoteStrokeContext
    {
        public uint StrokeId { get; }
        public bool IsActive { get; private set; }
        public BeginStrokePacket Metadata { get; private set; }
        
        // Data Buffer
        private List<LogicPoint> _points = new List<LogicPoint>(512);
        private int _lastReceivedSequenceId = -1; // -1 means no packets received yet
        public int LastReceivedSequenceId => _lastReceivedSequenceId;
        
        // Rendering State
        private readonly IGhostRenderer _ghostRenderer;
        private readonly bool _usePrediction;
        private Features.Drawing.Domain.BrushStrategy _strategy;
        private List<LogicPoint> _predictionBuffer = new List<LogicPoint>(512);
        private StrokeStampGenerator _incrementalStampGenerator = new StrokeStampGenerator();
        private List<StampData> _incrementalStampBuffer = new List<StampData>(256);
        private List<LogicPoint> _newPointsBuffer = new List<LogicPoint>(256);
        private List<LogicPoint> _spacingFilteredBuffer = new List<LogicPoint>(512);
        private LogicPoint _lastSpacingPoint;
        private bool _hasLastSpacingPoint;
        private int _lastRenderedIndex = 0;

        public RemoteStrokeContext(uint strokeId, IGhostRenderer ghostRenderer, bool usePrediction)
        {
            StrokeId = strokeId;
            _ghostRenderer = ghostRenderer;
            _usePrediction = usePrediction;
            IsActive = true;
            _lastPacketTime = Time.time;
        }

        public void SetStrategy(Features.Drawing.Domain.BrushStrategy strategy)
        {
            _strategy = strategy;
            ConfigureIncrementalGenerator();
        }

        public void SetMetadata(BeginStrokePacket metadata)
        {
            Metadata = metadata;
            // Debug.Log($"[RemoteStrokeContext] SetMetadata: ID={Metadata.StrokeId}, BrushId={Metadata.BrushId}, Size={Metadata.Size}");
            _lastRenderedIndex = 0;
            _incrementalStampGenerator.Reset();
            ConfigureIncrementalGenerator();
        }

        // Prediction State
        private float _lastPacketTime;
        private Vector2 _velocity; // Pixels (LogicUnits) per second
        private const float PREDICTION_THRESHOLD = 0.033f; // Start predicting after 33ms silence
        private const float MAX_PREDICTION_TIME = 0.100f; // Max 100ms prediction
        private const int MAX_PREDICTION_POINTS = 512;
        private const float MIN_PREDICTION_TIME = 0.016f;
        private const float ADAPTIVE_SPEED_LOW = 100f;
        private const float ADAPTIVE_SPEED_HIGH = 2000f;

        public void ProcessUpdate(UpdateStrokePacket packet)
        {
            if (!IsActive) return;
            if (packet.Payload == null || packet.PayloadLength <= 0) return;

            // Packet Loss Handling with Redundancy
            int expectedSeq = _lastReceivedSequenceId + 1;
            int actualSeq = packet.Sequence;

            // If we missed a packet, try to recover from RedundantPayload
            if (actualSeq > expectedSeq)
            {
                if (actualSeq == expectedSeq + 1 && packet.RedundantPayload != null && packet.RedundantPayloadLength > 0)
                {
                    if (packet.RedundantPayloadLength > packet.RedundantPayload.Length) return;
                    LogicPoint recoveryOrigin = _points.Count > 0 ? _points[_points.Count - 1] : new LogicPoint(0, 0, 0);
                    StrokeDeltaCompressor.Decompress(recoveryOrigin, packet.RedundantPayload, 0, packet.RedundantPayloadLength, _points);
                    _lastReceivedSequenceId = actualSeq - 1; 
                }
            }
            
            if (actualSeq <= _lastReceivedSequenceId) return;

            LogicPoint origin = _points.Count > 0 ? _points[_points.Count - 1] : new LogicPoint(0, 0, 0);
            StrokeDeltaCompressor.Decompress(origin, packet.Payload, 0, packet.PayloadLength, _points);
            
            // Update velocity and state
            UpdateVelocity(packet.PayloadLength); // Simplified velocity check
            _lastReceivedSequenceId = actualSeq;
            _lastPacketTime = Time.time;
        }

        private void UpdateVelocity(int pointsAdded)
        {
             if (_points.Count < 2) return;
             
             LogicPoint curr = _points[_points.Count - 1];
             LogicPoint prev = _points[_points.Count - 2];
             
             float dt = Time.time - _lastPacketTime;
             if (dt > 0.001f)
             {
                 Vector2 displacement = new Vector2(curr.X - prev.X, curr.Y - prev.Y);
                 Vector2 instantVel = displacement / dt;
                 _velocity = Vector2.Lerp(_velocity, instantVel, 0.5f);
             }
        }


        public void Update(float deltaTime)
        {
            if (!IsActive) return;
            if (_ghostRenderer == null) return;
            if (!_usePrediction) return;

            // Prepare points to draw
            List<LogicPoint> pointsToDraw = _points;

            // Extrapolation Logic
            float timeSincePacket = Time.time - _lastPacketTime;
            
            if (timeSincePacket > PREDICTION_THRESHOLD && _points.Count > 0)
            {
                // Limit prediction
                float predictionTime = Mathf.Min(timeSincePacket - PREDICTION_THRESHOLD, MAX_PREDICTION_TIME);
                float speed = _velocity.magnitude;
                float speedT = Mathf.InverseLerp(ADAPTIVE_SPEED_LOW, ADAPTIVE_SPEED_HIGH, speed);
                float adaptiveCap = Mathf.Lerp(MAX_PREDICTION_TIME, MIN_PREDICTION_TIME, speedT);
                if (predictionTime > adaptiveCap) predictionTime = adaptiveCap;
                
                if (predictionTime > 0 && _velocity.sqrMagnitude > 1f)
                {
                    LogicPoint lastPoint = _points[_points.Count - 1];
                    Vector2 predictedPos = new Vector2(lastPoint.X, lastPoint.Y) + _velocity * predictionTime;
                    
                    // Clamp to valid range (0-65535)
                    predictedPos.x = Mathf.Clamp(predictedPos.x, 0, 65535);
                    predictedPos.y = Mathf.Clamp(predictedPos.y, 0, 65535);
                    
                    LogicPoint predictedPoint = new LogicPoint((ushort)predictedPos.x, (ushort)predictedPos.y, lastPoint.Pressure);
                    
                    BuildPredictionBuffer(predictedPoint);
                    pointsToDraw = _predictionBuffer;
                }
            }

            // Draw
            // Need Color and Size from Metadata
            Color color = Color.black; // Default
            float size = 10f;
            bool isEraser = false;

            if (Metadata.StrokeId != 0) // Valid metadata
            {
                // Metadata.Color is uint. Need conversion helper.
                // Assuming Utils exist or manual conversion.
                // RGBA packed?
                // Let's assume DrawingConstants or similar has ColorFromUInt.
                // Or just use default for now if helper missing.
                // Actually DrawingNetworkService had ColorToUInt.
                // We'll interpret it here.
                color = ColorPacking.ToColor(Metadata.Color);
                size = Metadata.Size;
                isEraser = Metadata.BrushId == Common.Constants.DrawingConstants.ERASER_BRUSH_ID;
            }

            var filtered = FilterPointsBySpacing(pointsToDraw, size, MAX_PREDICTION_POINTS);
            _ghostRenderer.DrawGhostStroke(filtered, size, color, isEraser, _strategy);
        }

        public void RenderIncremental()
        {
            if (!IsActive) return;
            if (_ghostRenderer == null) return;
            if (_usePrediction) return;

            if (_points.Count <= _lastRenderedIndex) return;

            Color color = Color.black;
            float size = 10f;
            bool isEraser = false;

            if (Metadata.StrokeId != 0)
            {
                color = ColorPacking.ToColor(Metadata.Color);
                size = Metadata.Size;
                isEraser = Metadata.BrushId == Common.Constants.DrawingConstants.ERASER_BRUSH_ID;
            }

            _newPointsBuffer.Clear();
            int startIndex = _lastRenderedIndex;

            if (!_hasLastSpacingPoint)
            {
                if (_lastRenderedIndex > 0 && _lastRenderedIndex - 1 < _points.Count)
                {
                    _lastSpacingPoint = _points[_lastRenderedIndex - 1];
                    _hasLastSpacingPoint = true;
                }
                else if (_points.Count > 0)
                {
                    LogicPoint first = _points[0];
                    _newPointsBuffer.Add(first);
                    _lastSpacingPoint = first;
                    _hasLastSpacingPoint = true;
                    startIndex = 1;
                }
            }

            for (int i = startIndex; i < _points.Count; i++)
            {
                LogicPoint p = _points[i];
                if (ShouldKeepBySpacing(p, size))
                {
                    _newPointsBuffer.Add(p);
                    _lastSpacingPoint = p;
                }
            }

            UpdateIncrementalGeneratorScale();
            if (_newPointsBuffer.Count > 0)
            {
                _incrementalStampGenerator.ProcessPoints(_newPointsBuffer, size, _incrementalStampBuffer);
                _ghostRenderer.DrawGhostStamps(_incrementalStampBuffer, color, isEraser, _strategy);
                _incrementalStampBuffer.Clear();
            }
            _lastRenderedIndex = _points.Count;
        }

        public void RenderFull()
        {
            if (!IsActive) return;
            if (_ghostRenderer == null) return;

            Color color = Color.black;
            float size = 10f;
            bool isEraser = false;

            if (Metadata.StrokeId != 0)
            {
                color = ColorPacking.ToColor(Metadata.Color);
                size = Metadata.Size;
                isEraser = Metadata.BrushId == Common.Constants.DrawingConstants.ERASER_BRUSH_ID;
            }

            var filtered = FilterPointsBySpacing(_points, size, 0);
            _ghostRenderer.DrawGhostStroke(filtered, size, color, isEraser, _strategy);
        }

        public List<LogicPoint> GetFullPoints()
        {
            return _points;
        }

        public void Finish()
        {
            IsActive = false;
        }

        private void ConfigureIncrementalGenerator()
        {
            if (_strategy == null) return;
            _incrementalStampGenerator.RotationMode = _strategy.RotationMode;
            _incrementalStampGenerator.SpacingRatio = _strategy.SpacingRatio;
            _incrementalStampGenerator.AngleJitter = _strategy.AngleJitter;
        }

        private List<LogicPoint> FilterPointsBySpacing(List<LogicPoint> source, float size, int maxPoints)
        {
            if (source == null || source.Count == 0) return source;

            float maxDim = GetMaxResolutionDim();
            if (maxDim <= 0f) return source;

            int startIndex = 0;
            if (maxPoints > 0 && source.Count > maxPoints)
            {
                startIndex = source.Count - maxPoints;
            }

            float spacingRatio = _strategy != null ? _strategy.SpacingRatio : 0.15f;
            float minPixelSpacing = size * spacingRatio;
            if (minPixelSpacing < 1f) minPixelSpacing = 1f;
            float minLogical = minPixelSpacing * (DrawingConstants.LOGICAL_RESOLUTION / maxDim);
            float minSqr = minLogical * minLogical;

            _spacingFilteredBuffer.Clear();
            LogicPoint last = source[startIndex];
            _spacingFilteredBuffer.Add(last);

            for (int i = startIndex + 1; i < source.Count; i++)
            {
                LogicPoint p = source[i];
                if (LogicPoint.SqrDistance(last, p) >= minSqr)
                {
                    _spacingFilteredBuffer.Add(p);
                    last = p;
                }
            }

            return _spacingFilteredBuffer;
        }

        private void BuildPredictionBuffer(LogicPoint predictedPoint)
        {
            _predictionBuffer.Clear();

            int count = _points.Count;
            int start = 0;
            if (count > MAX_PREDICTION_POINTS)
            {
                start = count - MAX_PREDICTION_POINTS;
            }

            for (int i = start; i < count; i++)
            {
                _predictionBuffer.Add(_points[i]);
            }

            _predictionBuffer.Add(predictedPoint);
        }

        private bool ShouldKeepBySpacing(LogicPoint point, float size)
        {
            float maxDim = GetMaxResolutionDim();
            if (maxDim <= 0f) return true;

            float spacingRatio = _strategy != null ? _strategy.SpacingRatio : 0.15f;
            float minPixelSpacing = size * spacingRatio;
            if (minPixelSpacing < 1f) minPixelSpacing = 1f;
            float minLogical = minPixelSpacing * (DrawingConstants.LOGICAL_RESOLUTION / maxDim);
            float minSqr = minLogical * minLogical;

            if (!_hasLastSpacingPoint) return true;
            return LogicPoint.SqrDistance(_lastSpacingPoint, point) >= minSqr;
        }

        private float GetMaxResolutionDim()
        {
            if (_ghostRenderer == null) return 0f;
            Vector2Int res = _ghostRenderer.Resolution;
            return Mathf.Max(res.x, res.y);
        }

        private void UpdateIncrementalGeneratorScale()
        {
            if (_ghostRenderer == null) return;
            _incrementalStampGenerator.SetCanvasResolution(_ghostRenderer.Resolution);
            _incrementalStampGenerator.SetSizeScale(_ghostRenderer.GetBrushSizeScale());
        }
    }
}
