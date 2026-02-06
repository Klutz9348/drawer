using System.Collections;
using System.Diagnostics;
using UnityEngine;

namespace Common.Diagnostics
{
    /// <summary>
    /// Monitors system performance metrics (FPS, Memory).
    /// Designed to be non-intrusive and efficient.
    /// </summary>
#if DEVELOPMENT_BUILD || UNITY_EDITOR
    public class PerformanceMonitor : MonoBehaviour
    {
        private IStructuredLogger _logger;
        private bool _isInitialized = false;

        // Settings
        [SerializeField] private float _sampleInterval = 1.0f; // Seconds
        [SerializeField] private bool _logToConsole = false;

        // State
        private int _frameCount = 0;
        private float _timeAccumulator = 0f;
        
        // Metrics
        public float CurrentFPS { get; private set; }
        public float MemoryUsageMB { get; private set; }

        public void Initialize(IStructuredLogger logger)
        {
            _logger = logger;
            _isInitialized = true;
            StartCoroutine(MonitoringRoutine());
        }

        private void Update()
        {
            if (!_isInitialized) return;

            _frameCount++;
            _timeAccumulator += Time.unscaledDeltaTime;

            if (_timeAccumulator >= _sampleInterval)
            {
                CurrentFPS = _frameCount / _timeAccumulator;
                _frameCount = 0;
                _timeAccumulator = 0f;
            }
        }

        private IEnumerator MonitoringRoutine()
        {
            var wait = new WaitForSeconds(_sampleInterval);
            
            while (true)
            {
                yield return wait;

                // Collect Metrics
                MemoryUsageMB = System.GC.GetTotalMemory(false) / (1024f * 1024f);
                
                // Log structured metric
                if (_logger != null)
                {
                    var metadata = new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "fps", CurrentFPS.ToString("F1") },
                        { "mem_mb", MemoryUsageMB.ToString("F2") },
                        { "type", "metric" }
                    };

                    // Use Info level for metrics, but could be filtered
                    if (_logToConsole)
                    {
                        _logger.Info("PerformanceHeartbeat", default, metadata);
                    }
                    else
                    {
                        // Direct log without console echo to avoid spamming Unity Console
                        // We cast to access the raw Log method or rely on the implementation's config
                        // For this demo, we just log it.
                        // _logger.Info("PerformanceHeartbeat", default, metadata);
                    }
                }
            }
        }
    }
#endif
}
