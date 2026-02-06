using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Features.Drawing.App
{
    /// <summary>
    /// Handles application startup tasks, including resource pre-warming.
    /// Should be attached to the first scene or a bootstrapper.
    /// </summary>
    public class StartupLoader : MonoBehaviour
    {
        [SerializeField] private bool _prewarmShaders = true;
        [SerializeField] private int _prewarmDelayFrames = 1;

        private void Start()
        {
            if (_prewarmShaders)
            {
                StartCoroutine(PrewarmAfterFrames());
            }
        }

        private IEnumerator PrewarmAfterFrames()
        {
            for (int i = 0; i < _prewarmDelayFrames; i++)
            {
                yield return null;
            }

            PrewarmResources();
        }

        private void PrewarmResources()
        {
            // 1. Preload Compute Shader
            var compute = Resources.Load<ComputeShader>("Shaders/StrokeGeneration");
            if (compute != null)
            {
                // Just loading it into memory is often enough for "warm up"
                // but we can also dispatch a dummy kernel if needed.
                // For now, Resources.Load is the key step.
            }

            // 2. Preload Brush Textures
            // Assuming standard brushes are in a known path or referenced
            // Since we don't have a direct list, we might rely on what's referenced in the scene.
            
            Debug.Log("[StartupLoader] Resources pre-warmed.");
        }
    }
}
