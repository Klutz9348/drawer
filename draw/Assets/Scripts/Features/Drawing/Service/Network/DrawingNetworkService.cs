using System.Collections.Generic;
using UnityEngine;
using System.Buffers;
using Features.Drawing.Domain.Network;
using Features.Drawing.Domain.ValueObject;
using Features.Drawing.Domain.Interface;
using Features.Drawing.App;
using Features.Drawing.Domain;
using Common.Utils;

namespace Features.Drawing.Service.Network
{
    /// <summary>
    /// Service that coordinates network packets and drawing actions.
    /// Acts as the bridge between DrawingAppService and the Network Layer.
    /// </summary>
    public class DrawingNetworkService : MonoBehaviour
    {
        [Header("References")]
        // Decoupled: Removed direct reference to DrawingAppService
        [SerializeField] private MonoBehaviour _ghostRendererComponent;
        private Features.Drawing.Domain.Interface.IGhostRenderer _ghostRenderer;

        [Header("Ghost Settings")]
        [SerializeField] private bool _usePrediction = false;

        [Header("Security")]
        [SerializeField] private int _maxActiveRemoteStrokes = 32;
        [SerializeField] private bool _useBuildDefaults = true;
        [SerializeField] private bool _strictPacketValidation = true;
        [SerializeField] private bool _rejectUnknownBrushId = true;
        // _maxPointsPerUpdate removed as packet.Count (byte) limits us to 255 points, which is safe.

        // Dependencies
        private IDrawingNetworkClient _networkClient;
        private IBrushRegistry _brushRegistry;

        // Events
        public event System.Action<Features.Drawing.Domain.Entity.StrokeEntity> OnRemoteStrokeCommitted;
        
        // State
        private Dictionary<uint, RemoteStrokeContext> _activeRemoteStrokes = new Dictionary<uint, RemoteStrokeContext>();
        
        // Local Buffer (for sending)
        private List<LogicPoint> _pendingLocalPoints = new List<LogicPoint>(64);
        private uint _currentLocalStrokeId;
        private ushort _currentSequence;
        private LogicPoint _lastSentPoint;
        private float _lastSendTime; // For adaptive batching
        private float _currentBrushSize = 10f;
        private LogicPoint _lastSpeedPoint;
        private float _lastSpeedTime;
        private bool _hasLastSpeedPoint;
        private byte[] _redundantBuffer = new byte[4096];
        private int _redundantLength;

        // Reusable Buffers for Zero-GC
        private byte[] _compressionBuffer = new byte[4096]; // 4KB should be enough for a batch of 10-20 points
        private List<LogicPoint> _decompressionBuffer = new List<LogicPoint>(64);
        private bool _missingGhostLogged = false;

        private void Awake()
        {
            if (_ghostRendererComponent != null)
            {
                _ghostRenderer = _ghostRendererComponent as Features.Drawing.Domain.Interface.IGhostRenderer;
                if (_ghostRenderer == null)
                {
                    Debug.LogError("[DrawingNetworkService] GhostRendererComponent does not implement IGhostRenderer!");
                }
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_useBuildDefaults)
            {
                _strictPacketValidation = true;
                _rejectUnknownBrushId = true;
            }
#else
            if (_useBuildDefaults)
            {
                _strictPacketValidation = false;
                _rejectUnknownBrushId = false;
            }
#endif
        }

        public void Initialize(IDrawingNetworkClient client)
        {
            _networkClient = client;
            if (_networkClient != null)
            {
                _networkClient.OnPacketReceived += OnPacketReceived;
            }
        }

        public void InitializeBrushRegistry(IBrushRegistry registry)
        {
            _brushRegistry = registry;
        }

        private void OnDestroy()
        {
            if (_networkClient != null)
            {
                _networkClient.OnPacketReceived -= OnPacketReceived;
            }
        }

        // --- Outbound (Sending) ---

        public void OnLocalStrokeStarted(uint strokeId, ushort brushId, Color color, float size, bool isEraser)
        {
            if (_networkClient == null) return;

            _currentLocalStrokeId = strokeId;
            _currentSequence = 0;
            _pendingLocalPoints.Clear();
            _lastSentPoint = default; // Will be set on first point add
            _lastSendTime = Time.time;
            _redundantLength = 0;
            _currentBrushSize = size;
            _hasLastSpeedPoint = false;

            // Construct Begin Packet
            ushort resolvedBrushId = isEraser
                ? Common.Constants.DrawingConstants.ERASER_BRUSH_ID
                : brushId;

            var packet = new BeginStrokePacket
            {
                StrokeId = strokeId,
                BrushId = resolvedBrushId,
                Color = ColorPacking.ToUInt(color),
                Size = size,
                Seed = 0 // TODO: Add seed to app service if needed
            };

            _networkClient.SendBeginStroke(packet);
        }

        public void OnLocalStrokeMoved(LogicPoint point)
        {
            if (_networkClient == null) return;

            // If this is the very first point of the stroke, we must ensure 
            // the compression uses (0,0) as origin so the first point is encoded absolutely.
            if (_pendingLocalPoints.Count == 0 && _currentSequence == 0)
            {
                _lastSentPoint = new LogicPoint(0, 0, 0); 
            }

            _pendingLocalPoints.Add(point);

            float now = Time.time;
            float speed = 0f;
            if (_hasLastSpeedPoint)
            {
                float dt = now - _lastSpeedTime;
                if (dt > 0.0001f)
                {
                    float dist = Mathf.Sqrt(LogicPoint.SqrDistance(_lastSpeedPoint, point));
                    speed = dist / dt;
                }
            }
            _lastSpeedPoint = point;
            _lastSpeedTime = now;
            _hasLastSpeedPoint = true;

            float sizeT = Mathf.InverseLerp(6f, 80f, _currentBrushSize);
            float speedT = Mathf.InverseLerp(200f, 6000f, speed);

            float timeThreshold = Mathf.Lerp(0.018f, 0.05f, sizeT);
            timeThreshold = Mathf.Lerp(timeThreshold, 0.012f, speedT);
            int countThreshold = Mathf.RoundToInt(Mathf.Lerp(6f, 18f, sizeT));
            countThreshold = Mathf.RoundToInt(Mathf.Lerp(countThreshold, 4f, speedT));
            countThreshold = Mathf.Clamp(countThreshold, 3, 24);

            bool timeThresholdMet = (now - _lastSendTime) >= timeThreshold;
            bool countThresholdMet = _pendingLocalPoints.Count >= countThreshold;
            
            // Only send if we have at least 1 point and threshold is met
            // Exception: If count is very high (buffer overflow protection), send immediately
            if (_pendingLocalPoints.Count > 0 && (timeThresholdMet || countThresholdMet))
            {
                FlushPendingPoints();
            }
        }

        public void OnLocalStrokeEnded(uint checksum, int totalPoints)
        {
            if (_networkClient == null) return;

            // Flush remaining points
            FlushPendingPoints();

            var packet = new EndStrokePacket
            {
                StrokeId = _currentLocalStrokeId,
                TotalPoints = (ushort)totalPoints,
                Checksum = checksum
            };

            _networkClient.SendEndStroke(packet);
            _pendingLocalPoints.Clear();
        }

        private void FlushPendingPoints()
        {
            if (_pendingLocalPoints.Count == 0) return;

            // Handle potential overflow if called with > 255 points (though OnLocalStrokeMoved tries to prevent this)
            // We split into chunks of 255 max.
            int processedCount = 0;
            while (processedCount < _pendingLocalPoints.Count)
            {
                int remaining = _pendingLocalPoints.Count - processedCount;
                int batchSize = Mathf.Min(remaining, 255);
                
                // Zero-GC Compression (using reusable buffer)
                // Ensure buffer is large enough? 4KB is plenty for 255 points
                int bytesWritten = StrokeDeltaCompressor.Compress(_lastSentPoint, _pendingLocalPoints, processedCount, batchSize, _compressionBuffer, 0);

                if (bytesWritten <= 0)
                {
                    processedCount += batchSize;
                    continue;
                }

                byte[] payload = ArrayPool<byte>.Shared.Rent(bytesWritten);
                System.Buffer.BlockCopy(_compressionBuffer, 0, payload, 0, bytesWritten);
                
                byte[] redundantPayload = null;
                int redundantLength = 0;
                if (_redundantLength > 0)
                {
                    redundantLength = _redundantLength;
                    redundantPayload = ArrayPool<byte>.Shared.Rent(redundantLength);
                    System.Buffer.BlockCopy(_redundantBuffer, 0, redundantPayload, 0, redundantLength);
                }
                
                var packet = new UpdateStrokePacket
                {
                    StrokeId = _currentLocalStrokeId,
                    Sequence = _currentSequence++,
                    Count = (byte)batchSize,
                    Payload = payload,
                    PayloadLength = bytesWritten,
                    PayloadIsPooled = true,
                    RedundantPayload = redundantPayload,
                    RedundantPayloadLength = redundantLength,
                    RedundantPayloadIsPooled = redundantPayload != null
                };

                _networkClient.SendUpdateStroke(packet);
                ReleasePooledPacket(packet);
                
                // Update state for next batch
                _lastSentPoint = _pendingLocalPoints[processedCount + batchSize - 1];
                if (_redundantBuffer.Length < bytesWritten)
                {
                    _redundantBuffer = new byte[bytesWritten];
                }
                System.Buffer.BlockCopy(_compressionBuffer, 0, _redundantBuffer, 0, bytesWritten);
                _redundantLength = bytesWritten;
                
                processedCount += batchSize;
            }
            
            _pendingLocalPoints.Clear();
            _lastSendTime = Time.time;
        }

        private void Update()
        {
            if (_ghostRenderer == null)
            {
                if (!_missingGhostLogged)
                {
                    Debug.LogError("[DrawingNetworkService] GhostOverlayRenderer missing. Remote rendering disabled.");
                    _missingGhostLogged = true;
                }
                return;
            }

            if (_usePrediction)
            {
                _ghostRenderer.BeginFrame();
                if (_activeRemoteStrokes.Count > 0)
                {
                    foreach (var kvp in _activeRemoteStrokes)
                    {
                        kvp.Value.Update(Time.deltaTime);
                    }
                }
                else
                {
                    _ghostRenderer.BeginFrame();
                }
            }
        }

        private void OnPacketReceived(object packetObj)
        {
            switch (packetObj)
            {
                case BeginStrokePacket begin:
                    HandleBeginStroke(begin);
                    break;
                case UpdateStrokePacket update:
                    HandleUpdateStroke(update);
                    break;
                case EndStrokePacket end:
                    HandleEndStroke(end);
                    break;
                case AbortStrokePacket abort:
                    HandleAbortStroke(abort);
                    break;
            }
        }

        private void HandleBeginStroke(BeginStrokePacket packet)
        {
            if (_activeRemoteStrokes.ContainsKey(packet.StrokeId)) return; // Duplicate

            // Security: Limit active strokes to prevent DoS (Memory Exhaustion)
            if (_activeRemoteStrokes.Count >= _maxActiveRemoteStrokes)
            {
                Debug.LogWarning($"[Security] Ignored remote stroke {packet.StrokeId}. Max active strokes ({_maxActiveRemoteStrokes}) reached.");
                return;
            }

            // Security: Basic Validation
            if (packet.Size < 0 || packet.Size > 500) // 500 is a reasonable max brush size
            {
                Debug.LogWarning($"[Security] Ignored remote stroke {packet.StrokeId}. Invalid size {packet.Size}.");
                return;
            }

            if (!IsBrushIdAllowed(packet.BrushId))
            {
                Debug.LogWarning($"[Security] Ignored remote stroke {packet.StrokeId}. Invalid brushId {packet.BrushId}.");
                return;
            }

            if (!_usePrediction && _activeRemoteStrokes.Count == 0)
            {
                _ghostRenderer.BeginFrame();
            }

            // Setup Renderer
            var context = new RemoteStrokeContext(packet.StrokeId, _ghostRenderer, _usePrediction);
            context.SetMetadata(packet); // Store metadata
            
            // Resolve Strategy and pass to context
            var strategy = _brushRegistry?.GetBrushStrategy(packet.BrushId);
            context.SetStrategy(strategy);

            _activeRemoteStrokes.Add(packet.StrokeId, context);
        }

        private void HandleUpdateStroke(UpdateStrokePacket packet)
        {
            if (_activeRemoteStrokes.TryGetValue(packet.StrokeId, out var context))
            {
                // Security: Payload check
                if (packet.Payload == null)
                {
                    ReleasePooledPacket(packet);
                    if (_strictPacketValidation) AbortAndClearRemoteStroke(packet.StrokeId);
                    return;
                }
                if (packet.PayloadLength <= 0 || packet.PayloadLength > packet.Payload.Length)
                {
                    ReleasePooledPacket(packet);
                    if (_strictPacketValidation) AbortAndClearRemoteStroke(packet.StrokeId);
                    return;
                }
                if (packet.PayloadLength > 1024 * 10) // 10KB limit per packet
                {
                     Debug.LogWarning($"[Security] Update packet too large for stroke {packet.StrokeId}.");
                     ReleasePooledPacket(packet);
                     if (_strictPacketValidation) AbortAndClearRemoteStroke(packet.StrokeId);
                     return;
                }
                if (packet.Count == 0)
                {
                    Debug.LogWarning($"[Security] Update packet with zero count for stroke {packet.StrokeId}.");
                    ReleasePooledPacket(packet);
                    if (_strictPacketValidation) AbortAndClearRemoteStroke(packet.StrokeId);
                    return;
                }
                int minBytes = packet.Count * 3; // 1x + 1y + 1p (min)
                if (packet.PayloadLength < minBytes)
                {
                    Debug.LogWarning($"[Security] Update packet payload too small for stroke {packet.StrokeId} (count={packet.Count}, len={packet.PayloadLength}).");
                    ReleasePooledPacket(packet);
                    if (_strictPacketValidation) AbortAndClearRemoteStroke(packet.StrokeId);
                    return;
                }

                int expectedSeq = context.LastReceivedSequenceId + 1;
                if (packet.Sequence > expectedSeq + 1 && (packet.RedundantPayload == null || packet.RedundantPayloadLength <= 0))
                {
                    Debug.LogWarning($"[Network] Gap detected without redundancy for stroke {packet.StrokeId}. Expected {expectedSeq}, got {packet.Sequence}.");
                    if (_strictPacketValidation) AbortAndClearRemoteStroke(packet.StrokeId);
                }
                
                context.ProcessUpdate(packet);
                if (!_usePrediction)
                {
                    context.RenderIncremental();
                }

                ReleasePooledPacket(packet);
            }
            else
            {
                ReleasePooledPacket(packet);
            }
        }

        private void HandleEndStroke(EndStrokePacket packet)
        {
            if (_activeRemoteStrokes.TryGetValue(packet.StrokeId, out var context))
            {
                context.Finish();
                
                // 1. Reconstruct full stroke entity
                var points = context.GetFullPoints();
                if (points.Count > 0)
                {
                    if (packet.TotalPoints > 0 && packet.TotalPoints != points.Count)
                    {
                        Debug.LogWarning($"[Network] EndStroke point mismatch for stroke {packet.StrokeId}. Packet={packet.TotalPoints}, Local={points.Count}.");
                        if (_strictPacketValidation)
                        {
                            _activeRemoteStrokes.Remove(packet.StrokeId);
                            return;
                        }
                    }
                    if (packet.Checksum != 0)
                    {
                        uint computed = ComputeStrokeChecksum(points);
                        if (computed != packet.Checksum)
                        {
                            Debug.LogWarning($"[Network] EndStroke checksum mismatch for stroke {packet.StrokeId}. Packet={packet.Checksum}, Local={computed}.");
                            if (_strictPacketValidation)
                            {
                                _activeRemoteStrokes.Remove(packet.StrokeId);
                                return;
                            }
                        }
                    }

                    // Construct StrokeEntity
                    var meta = context.Metadata;
                    var stroke = new Features.Drawing.Domain.Entity.StrokeEntity(
                        meta.StrokeId,
                        0, // Local Sequence ID? Or Remote? For now 0
                        meta.BrushId,
                        meta.Seed,
                        meta.Color,
                        meta.Size,
                        0 // Sequence
                    );
                    
                    stroke.AddPoints(points);
                    stroke.EndStroke();

                    // 2. Notify Listeners (AppService)
                    OnRemoteStrokeCommitted?.Invoke(stroke);
                }
                else
                {
                    Debug.LogWarning($"[Network] EndStroke received with no points for stroke {packet.StrokeId}.");
                }
                
                // 3. Clear Ghost
                _activeRemoteStrokes.Remove(packet.StrokeId);
                if (!_usePrediction)
                {
                    RedrawActiveGhosts();
                }
            }
        }

        private void HandleAbortStroke(AbortStrokePacket packet)
        {
            if (_activeRemoteStrokes.ContainsKey(packet.StrokeId))
            {
                _activeRemoteStrokes.Remove(packet.StrokeId);
                if (!_usePrediction)
                {
                    RedrawActiveGhosts();
                }
            }
        }

        private void AbortAndClearRemoteStroke(uint strokeId)
        {
            if (_activeRemoteStrokes.ContainsKey(strokeId))
            {
                _activeRemoteStrokes.Remove(strokeId);
                if (!_usePrediction)
                {
                    RedrawActiveGhosts();
                }
            }
        }

        private void RedrawActiveGhosts()
        {
            if (_ghostRenderer == null) return;
            _ghostRenderer.BeginFrame();
            foreach (var kvp in _activeRemoteStrokes)
            {
                kvp.Value.RenderFull();
            }
        }

        public static uint ComputeStrokeChecksum(IReadOnlyList<LogicPoint> points)
        {
            if (points == null || points.Count == 0) return 0;

            const uint FnvOffset = 2166136261u;
            const uint FnvPrime = 16777619u;
            uint hash = FnvOffset;

            for (int i = 0; i < points.Count; i++)
            {
                LogicPoint p = points[i];

                hash ^= (byte)(p.X & 0xFF);
                hash *= FnvPrime;
                hash ^= (byte)((p.X >> 8) & 0xFF);
                hash *= FnvPrime;

                hash ^= (byte)(p.Y & 0xFF);
                hash *= FnvPrime;
                hash ^= (byte)((p.Y >> 8) & 0xFF);
                hash *= FnvPrime;

                hash ^= p.Pressure;
                hash *= FnvPrime;
            }

            hash ^= (uint)points.Count;
            hash *= FnvPrime;

            return hash;
        }

        private void ReleasePooledPacket(UpdateStrokePacket packet)
        {
            if (packet.PayloadIsPooled && packet.Payload != null)
            {
                ArrayPool<byte>.Shared.Return(packet.Payload);
            }
            if (packet.RedundantPayloadIsPooled && packet.RedundantPayload != null)
            {
                ArrayPool<byte>.Shared.Return(packet.RedundantPayload);
            }
        }

        private bool IsBrushIdAllowed(ushort brushId)
        {
            if (brushId == Common.Constants.DrawingConstants.ERASER_BRUSH_ID) return true;

            // If no registry, we cannot validate
            if (_brushRegistry == null) return true;

            // Allow UNKNOWN for compatibility but warn elsewhere
            if (brushId == Common.Constants.DrawingConstants.UNKNOWN_BRUSH_ID) return !_rejectUnknownBrushId;

            return _brushRegistry.GetBrushStrategy(brushId) != null;
        }
    }
}
