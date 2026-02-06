using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using System.Collections;
using System.Collections.Generic;
using Features.Drawing.Domain;
using Features.Drawing.Domain.ValueObject;
using Common.Constants;
using Features.Drawing.Domain.Interface;
using Features.Drawing.Domain.Logic;

namespace Features.Drawing.Presentation
{

    /// <summary>
    /// Handles the low-level GPU drawing using CommandBuffers.
    /// Implements the "Mesh Stamping" technique.
    /// </summary>
    public class CanvasRenderer : MonoBehaviour, IStrokeRenderer, ICanvasResolutionProvider, IRendererInitializer
    {
        [Header("Settings")]
        [SerializeField] private Vector2Int _resolution = new Vector2Int(2048, 2048);
        [SerializeField] private RawImage _displayImage; // UI to display the RT
        [SerializeField] private Shader _brushShader;
        [SerializeField] private ShaderVariantCollection _shaderVariants; // Pre-warm shaders
        [SerializeField] private Texture2D _defaultBrushTip;

        [Header("Runtime Debug")]
        [SerializeField] private float _brushSize = 50.0f;
        [SerializeField] private Color _brushColor = Color.black;
        [SerializeField] private bool _isEraser = false;
        [SerializeField] private float _brushOpacity = 1.0f; // 0-1
        [SerializeField] private BrushRotationMode _rotationMode = BrushRotationMode.None;
        [SerializeField] private bool _enableDiagnostics = true;

        // Size Management
        private float _baseBrushSize = 50.0f; // Raw size from logic
        private float _sizeMultiplier = 1.0f; // From strategy
        private float _strategyEdgeSoftness = 0.05f;
        private bool _strategyUseProcedural = false;

        public Vector2Int Resolution => _layoutController != null ? _layoutController.Resolution : _resolution;
        public event System.Action<Vector2Int> OnResolutionChanged;

        // RenderTextures managed by CanvasLayoutController
        private bool _isBaking = false;

        private Material _brushMaterial;
        private CommandBuffer _cmd;
        private Mesh _quadMesh; // Reused quad mesh
        private MaterialPropertyBlock _props; // Property block for colors

        // Interpolation Logic Delegated to Generator
        private StrokeStampGenerator _stampGenerator = new StrokeStampGenerator();
        private GpuStrokeStampGenerator _gpuStampGenerator;
        private bool _useGpuStamping = true;
        private List<StampData> _stampBuffer = new List<StampData>(1024);
        
        // Cache for batching (optimization)
        private const int BATCH_SIZE = 1023; // Max for DrawMeshInstanced

        private CanvasLayoutController _layoutController;

        // Runtime Optimization
        private bool _forceCpuMode = false;
        public bool ForceCpuMode 
        { 
            get => _forceCpuMode; 
            set => _forceCpuMode = value; 
        }

        private bool _isInitialized = false;

        private void Awake()
        {
            // Fix background color (User requirement: White, not Blue)
            if (Camera.main != null)
            {
                Camera.main.backgroundColor = Color.white;
                Camera.main.clearFlags = CameraClearFlags.SolidColor;
            }

            // Note: Removed coroutine initialization. 
            // Now relies on explicit Initialize() call from AppService or Bootstrapper.
        }

        public IEnumerator InitializeAsync()
        {
            if (_isInitialized) yield break;

            // 1. GPU Generator (Compute Shader)
            InitializeGpuGenerator();
            yield return null; // Yield after loading shader
            
            // 2. Layout Controller (RenderTexture Allocation - Heavy)
            _layoutController = new CanvasLayoutController(_displayImage, _resolution, 0);
            _layoutController.OnLayoutChanged += OnLayoutChanged;
            _layoutController.Initialize(); 
            _layoutController.OnLayoutChanged += () => OnResolutionChanged?.Invoke(_layoutController.Resolution);
            yield return null; // Yield after RT allocation

            // 3. Graphics Resources (Materials, Meshes)
            // Pre-warm Shaders (Heavy operation)
            if (_shaderVariants != null)
            {
                _shaderVariants.WarmUp();
                yield return null; 
            }

            // Setup Material
            if (_brushShader == null) 
                _brushShader = Shader.Find("Drawing/BrushStamp");
            
            if (_brushShader == null)
            {
                Debug.LogError("[CanvasRenderer] CRITICAL: Brush Shader NOT FOUND!");
                yield break;
            }

            // Clean up existing material if any (re-init safety)
            if (_brushMaterial != null) Destroy(_brushMaterial);
            _brushMaterial = new Material(_brushShader);
            if (_defaultBrushTip != null)
                _brushMaterial.mainTexture = _defaultBrushTip;

            // Setup CommandBuffer
            _cmd = new CommandBuffer();
            _cmd.name = "DrawingBuffer";

            // Create Quad Mesh for stamping
            _quadMesh = CreateQuad();
            
            // Init props
            _props = new MaterialPropertyBlock();
            
            // Apply initial scale
            ApplyStampGeneratorScale();

            Debug.Log($"[Renderer] Initialized Async. Resolution: {_resolution}, GPU Stamping: {_useGpuStamping}");

            _isInitialized = true;
        }

        private void OnLayoutChanged()
        {
            ApplyStampGeneratorScale();
        }


        private void InitializeGpuGenerator()
        {
            if (_gpuStampGenerator != null) return;

            // Try load Compute Shader
            var shader = Resources.Load<ComputeShader>("Shaders/StrokeGeneration");
            if (shader != null)
            {
                _gpuStampGenerator = new GpuStrokeStampGenerator(shader);
                // Debug.Log("[CanvasRenderer] GPU Stroke Generation Enabled.");
                _useGpuStamping = true;
            }
            else
            {
                Debug.LogWarning("[CanvasRenderer] Compute Shader not found in Resources/Shaders/StrokeGeneration. Falling back to CPU.");
                _useGpuStamping = false;
            }
        }

        private void OnEnable()
        {
            InitializeGpuGenerator();
        }

        private void OnDisable()
        {
            if (_gpuStampGenerator != null)
            {
                _gpuStampGenerator.Dispose();
                _gpuStampGenerator = null;
            }
        }

        private void OnRectTransformDimensionsChange()
        {
            _layoutController?.CheckLayoutChanges();
        }

        private void OnDestroy()
        {
            _layoutController?.Release();

            if (_cmd != null) _cmd.Release();
            if (_brushMaterial != null) Destroy(_brushMaterial);
            // Don't destroy _quadMesh if it's a primitive, but if we created it:
            if (_quadMesh != null) Destroy(_quadMesh);
            
            // Already handled in OnDisable, but double check
            if (_gpuStampGenerator != null)
            {
                _gpuStampGenerator.Dispose();
                _gpuStampGenerator = null;
            }
        }



        private void ApplyStampGeneratorScale()
        {
            float sizeScale = _layoutController.GetBrushSizeScale();
            Vector2Int res = _layoutController.Resolution;

            _stampGenerator.SetCanvasResolution(res);
            _stampGenerator.SetSizeScale(sizeScale);

            if (_useGpuStamping && _gpuStampGenerator != null)
            {
                _gpuStampGenerator.SetCanvasResolution(res);
                _gpuStampGenerator.SetSizeScale(sizeScale);
            }
        }

        private Mesh CreateQuad()
        {
            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[] {
                new Vector3(-0.5f, -0.5f, 0),
                new Vector3( 0.5f, -0.5f, 0),
                new Vector3(-0.5f,  0.5f, 0),
                new Vector3( 0.5f,  0.5f, 0)
            };
            mesh.uv = new Vector2[] {
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(0, 1),
                new Vector2(1, 1)
            };
            mesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
            return mesh;
        }

        public void SetBrushSize(float size)
        {
            // Debug.Log($"[CanvasRenderer] SetBrushSize: {size}");
            _baseBrushSize = Mathf.Max(1.0f, size);
            _brushSize = _baseBrushSize * _sizeMultiplier;
            UpdateDynamicMaterialProperties();
        }

        private void UpdateDynamicMaterialProperties()
        {
            if (_brushMaterial == null) return;

            if (_strategyUseProcedural)
            {
                // Dynamic Softness: Ensure at least ~4.0 pixel of anti-aliasing to prevent artifacts on small brushes
                // This fixes "jagged edges" (aliasing) when using Hard Brush at small sizes.
                float pixelSoftness = _brushSize * _strategyEdgeSoftness;
                float minSoftness = 4.0f; 
                float effectiveSoftness = Mathf.Max(pixelSoftness, minSoftness);
                
                // Convert back to ratio (0-0.5 range usually)
                // Clamp to 0.5 to avoid inverting the SDF (softness > radius)
                float softnessRatio = Mathf.Min(effectiveSoftness / _brushSize, 0.5f);
                
                _brushMaterial.SetFloat("_EdgeSoftness", softnessRatio);
            }
            else
            {
                // For textures, we just use the configured softness (if used by shader)
                _brushMaterial.SetFloat("_EdgeSoftness", _strategyEdgeSoftness);
            }
        }

        public void SetBrushColor(Color color)
        {
            _brushColor = color;
            _isEraser = false;
        }

        public void SetEraser(bool isEraser)
        {
            _isEraser = isEraser;
        }

        public void StartStroke(LogicPoint point, bool isEraser, float size, Color color)
        {
            SetEraser(isEraser);
            if (!isEraser) SetBrushColor(color);
            SetBrushSize(size);
            
            // Reset generator state for new stroke
            if (_stampGenerator != null) _stampGenerator.Reset();
            if (_useGpuStamping && _gpuStampGenerator != null) _gpuStampGenerator.Reset();
        }

        /// <summary>
        /// Configures the brush appearance and behavior based on a Strategy object.
        /// </summary>
        public void ConfigureBrush(BrushStrategy strategy, Texture2D runtimeTexture = null)
        {
            if (strategy == null) return;

            // 1. Texture
            Texture2D tex = runtimeTexture != null ? runtimeTexture : strategy.MainTexture;
            
            // If texture is still null (e.g. strategy has no texture and UseRuntimeGeneration is false),
            // fallback to default
            if (tex == null && _defaultBrushTip != null)
            {
                tex = _defaultBrushTip;
            }

            if (tex != null && _brushMaterial != null)
            {
                _brushMaterial.mainTexture = tex;
            }
            
            // 2. Parameters
            // If strategy.Opacity is 0, we should fallback to 1? Or trust the strategy?
            // Usually Opacity 0 means invisible. Let's assume strategy is correct but debug if it's suspicious.
            _brushOpacity = strategy.Opacity;
            _rotationMode = strategy.RotationMode;
            
            // Apply Size Multiplier
            _sizeMultiplier = strategy.SizeMultiplier;
            // Recalculate current effective size immediately
            _brushSize = _baseBrushSize * _sizeMultiplier;
            
            // CRITICAL: Ensure _stampGenerator is initialized before accessing
            if (_stampGenerator == null) _stampGenerator = new StrokeStampGenerator();

            _stampGenerator.RotationMode = strategy.RotationMode;
            _stampGenerator.SpacingRatio = strategy.SpacingRatio;
            _stampGenerator.AngleJitter = strategy.AngleJitter;
            
            // Reset generator state
            _stampGenerator.Reset();

            if (_useGpuStamping && _gpuStampGenerator != null)
            {
                _gpuStampGenerator.SpacingRatio = strategy.SpacingRatio;
                _gpuStampGenerator.AngleJitter = strategy.AngleJitter;
                _gpuStampGenerator.Reset();
            }

            // 3. Blend Mode
            if (_brushMaterial != null)
            {
                _brushMaterial.SetInt("_BlendOp", (int)strategy.BlendOp);
                _brushMaterial.SetInt("_SrcBlend", (int)strategy.SrcBlend);
                _brushMaterial.SetInt("_DstBlend", (int)strategy.DstBlend);
                
                // 4. Procedural SDF Settings
                _strategyUseProcedural = strategy.UseProceduralSDF;
                _strategyEdgeSoftness = strategy.EdgeSoftness;

                _brushMaterial.SetFloat("_UseProcedural", strategy.UseProceduralSDF ? 1.0f : 0.0f);
                // _EdgeSoftness is now set by UpdateDynamicMaterialProperties
                UpdateDynamicMaterialProperties();

                // Force update material keywords if needed (Standard shader relies on this, custom shader might not)
                // But let's log to be sure
                Debug.Log($"[CanvasRenderer] Applied Brush: {strategy.name}, Op: {strategy.BlendOp}, SDF: {strategy.UseProceduralSDF}");
            }
        }

        /// <summary>
        /// Draws a batch of points to the RenderTexture.
        /// Optimized overload for List to avoid Boxing.
        /// </summary>
        public void DrawPoints(List<LogicPoint> points)
        {
            if (points == null || points.Count == 0) return;
            DrawPointsInternal(points, points.Count);
        }

        /// <summary>
        /// Interface implementation (boxing fallback).
        /// </summary>
        public void DrawPoints(IEnumerable<LogicPoint> points)
        {
             if (points == null) return;
             
             // Optimization: Check if it's actually a list
             if (points is List<LogicPoint> list)
             {
                 DrawPoints(list);
                 return;
             }

             // Slow path
             // We need to convert to list or iterate. 
             // Since internal logic needs a list or array for GPU/Stamping?
             // Actually ProcessPoints takes IEnumerable.
             // But let's consolidate logic.
             // To avoid allocation, we can't easily convert to List without new.
             // But we can just run the logic.
             
             DrawPointsInternal(points, -1);
        }

        private void DrawPointsInternal(IEnumerable<LogicPoint> points, int countHint)
        {
            if (_layoutController.ActiveRT == null)
            {
                Debug.LogError("[CanvasRenderer] ActiveRT is null!");
                return;
            }

            // Debug.Log($"[Renderer] DrawPoints. CountHint: {countHint}, Eraser: {_isEraser}");

            _layoutController.CheckLayoutChanges();
            _cmd.Clear();
            
            // Setup Eraser vs Brush state
            if (_isEraser)
            {
                // Eraser: Zero OneMinusSrcAlpha (Subtract alpha from destination)
                _cmd.SetExecutionFlags(CommandBufferExecutionFlags.None);
                
                _brushMaterial.SetInt("_SrcBlend", (int)BlendMode.Zero);
                _brushMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                _brushMaterial.SetInt("_BlendOp", (int)BlendOp.Add); // Ensure Op is Add for eraser
            }
            // ELSE: Do NOT force override blend modes. Trust the state set by ConfigureBrush.
            
            _cmd.SetRenderTarget(_isBaking ? _layoutController.BakedRT : _layoutController.ActiveRT);

            // Prepare PropertyBlock for color
            Color drawColor = _isEraser ? new Color(0,0,0,1) : _brushColor; 
            // Apply Opacity
            drawColor.a *= _brushOpacity;
            
            _props.SetColor("_Color", drawColor);

            _cmd.SetViewMatrix(Matrix4x4.identity);
            _cmd.SetProjectionMatrix(Matrix4x4.Ortho(0, _layoutController.Resolution.x, 0, _layoutController.Resolution.y, -1, 1));

            // Generate stamps
            bool gpuSuccess = false;
            // Heuristic: GPU overhead is not worth it for small batches (e.g. real-time drawing).
            // Also, GPU implementation is stateless and doesn't handle distance accumulation across small batches well.
            // So we only use GPU for large batch processing (e.g. redraw, history undo/redo).
            
            int pointsCount = countHint >= 0 ? countHint : 0;
            if (countHint < 0)
            {
                if (points is ICollection<LogicPoint> col) pointsCount = col.Count;
                else { foreach(var _ in points) pointsCount++; }
            }

            bool shouldTryGpu = !_forceCpuMode && _useGpuStamping && _gpuStampGenerator != null && pointsCount > 10;


            if (shouldTryGpu)
            {
                try
                {
                    _gpuStampGenerator.ProcessPoints(points, _brushSize, _stampBuffer);
                    gpuSuccess = true;
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[CanvasRenderer] GPU Generation Failed: {e.Message}. Fallback to CPU.");
                    _useGpuStamping = false; 
                }
            }
            
            if (!gpuSuccess)
            {
                _stampGenerator.ProcessPoints(points, _brushSize, _stampBuffer);
            }
            
            if (_enableDiagnostics)
            {
                 Debug.Log($"[Renderer] Generated {_stampBuffer.Count} stamps from {pointsCount} points. RT: {_layoutController.ActiveRT != null}");
            }

            // Debug if empty
            if (_stampBuffer.Count == 0 && pointsCount > 0)
            {
                 // Only warn if we had significant input (LogicPoint resolution might cause small movements to be skipped)
                 // But if pointsCount is large, it's definitely an issue.
                 if (_enableDiagnostics && pointsCount > 1) 
                 {
                     Debug.LogWarning($"[Renderer] Zero stamps generated! Input: {pointsCount}, Size: {_brushSize}, Res: {_layoutController.Resolution}");
                 }
            }

            // Draw stamps (Instanced)
            if (_stampBuffer.Count > 0)
            {
                // Debug.Log($"[Renderer] Drawing {batchCount} stamps. GPU: {shouldTryGpu}");
                // Ensure Instancing is enabled on material (Standard Requirement)
                // Note: Standard shaders require this, custom shaders might too.
                _brushMaterial.enableInstancing = true;

                int batchCount = 0;
                for (int i = 0; i < _stampBuffer.Count; i++)
                {
                    var stamp = _stampBuffer[i];
                    
                    if (batchCount >= BATCH_SIZE)
                    {
                        _cmd.DrawMeshInstanced(_quadMesh, 0, _brushMaterial, 0, _matrices, batchCount, _props);
                        batchCount = 0;
                    }

                    _matrices[batchCount] = Matrix4x4.TRS(
                        new Vector3(stamp.Position.x, stamp.Position.y, 0),
                        Quaternion.Euler(0, 0, stamp.Rotation),
                        new Vector3(stamp.Size, stamp.Size, 1)
                    );
                    batchCount++;
                }

                if (batchCount > 0)
                {
                    _cmd.DrawMeshInstanced(_quadMesh, 0, _brushMaterial, 0, _matrices, batchCount, _props);
                }
            }

            Graphics.ExecuteCommandBuffer(_cmd);
        }

        // Cache for batching (optimization)
        private Matrix4x4[] _matrices = new Matrix4x4[BATCH_SIZE];

        public void ClearCanvas()
        {
            var target = _isBaking ? _layoutController.BakedRT : _layoutController.ActiveRT;
            Graphics.SetRenderTarget(target);
            GL.Clear(true, true, Color.clear);
            
            // Reset state
            _stampGenerator.Reset();
            if (_gpuStampGenerator != null) _gpuStampGenerator.Reset();
        }

        public void EndStroke()
        {
            _stampGenerator.Reset();
            if (_gpuStampGenerator != null) _gpuStampGenerator.Reset();
        }

        public void SetBakingMode(bool enabled)
        {
            _isBaking = enabled;
        }

        public void RestoreFromBackBuffer()
        {
            if (_layoutController.BakedRT == null || _layoutController.ActiveRT == null) return;
            Graphics.Blit(_layoutController.BakedRT, _layoutController.ActiveRT);
        }
    }
}
