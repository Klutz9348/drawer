using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using System.Collections.Generic;
using Features.Drawing.Domain;
using Features.Drawing.Domain.Interface;
using Features.Drawing.Domain.ValueObject;
using Features.Drawing.Domain.Logic;
using Common.Constants;

namespace Features.Drawing.Presentation
{
    /// <summary>
    /// Renders transient "Ghost" strokes from remote users.
    /// Overlays on top of the main canvas.
    /// Uses Retained Mode (Clear & Redraw per frame) to support Extrapolation/Prediction.
    /// </summary>
    public class GhostOverlayRenderer : MonoBehaviour, IGhostRenderer
    {
        [Header("References")]
        [SerializeField] private CanvasRenderer _mainRenderer;
        [SerializeField] private RawImage _displayImage;
        [SerializeField] private Shader _brushShader;
        [SerializeField] private Texture2D _defaultBrushTip;

        [Header("Ghost Settings")]
        [SerializeField] private Color _eraserTrailColor = new Color(1f, 0f, 0f, 0.2f); // Semi-transparent red

        // State
        private CanvasLayoutController _layoutController;
        private Material _brushMaterial;
        private CommandBuffer _cmd;
        private Mesh _quadMesh;
        private MaterialPropertyBlock _props;
        
        private StrokeStampGenerator _stampGenerator = new StrokeStampGenerator();
        private List<StampData> _stampBuffer = new List<StampData>(1024);
        private List<LogicPoint> _pointBuffer = new List<LogicPoint>(1024);
        private const int BATCH_SIZE = 1023;
        private Matrix4x4[] _matrices = new Matrix4x4[BATCH_SIZE];

        // Brush State
        private float _brushOpacity = 1f;
        // _isEraser, _brushColor, _baseBrushSize removed as they are passed per-call in DrawGhostStroke

        private void Awake()
        {
            if (_mainRenderer == null)
                _mainRenderer = FindObjectOfType<CanvasRenderer>();
                
            InitializeGraphics();
        }

        private void Start()
        {
            // Sync with main renderer resolution
            if (_mainRenderer != null)
            {
                _mainRenderer.OnResolutionChanged += OnMainResolutionChanged;
                // Initial sync
                OnMainResolutionChanged(_mainRenderer.Resolution);
            }
        }

        private void OnDestroy()
        {
            if (_mainRenderer != null)
                _mainRenderer.OnResolutionChanged -= OnMainResolutionChanged;

            _layoutController?.Release();
            if (_cmd != null) _cmd.Release();
            if (_brushMaterial != null) Destroy(_brushMaterial);
            if (_quadMesh != null) Destroy(_quadMesh);
        }

        private void OnMainResolutionChanged(Vector2Int resolution)
        {
            // Re-initialize layout with new resolution
            // We use the same resolution as main canvas to ensure 1:1 mapping
            if (_layoutController == null)
            {
                _layoutController = new CanvasLayoutController(_displayImage, resolution, 0);
                _layoutController.Initialize(); // Create RTs
            }
            else
            {
                // Force resize logic if exposed, or just rely on CheckLayoutChanges
                // But CanvasLayoutController doesn't have public Resize. 
                // It checks displayImage size.
                // Actually, CanvasLayoutController constructor takes initialResolution.
                // We might need to recreate it or add a method to UpdateResolution.
                // For now, let's assume CheckLayoutChanges handles it if displayImage size changes?
                // But Resolution is logical.
                
                // Hack: Release and recreate for now to ensure sync
                _layoutController.Release();
                _layoutController = new CanvasLayoutController(_displayImage, resolution, 0);
                _layoutController.Initialize();
            }
            
            // Sync generator scale
            _stampGenerator.SetCanvasResolution(resolution);
        }

        public Vector2Int Resolution => _layoutController != null ? _layoutController.Resolution : Vector2Int.zero;

        public float GetBrushSizeScale()
        {
            return _layoutController != null ? _layoutController.GetBrushSizeScale() : 1f;
        }

        private void InitializeGraphics()
        {
            if (_brushShader == null) _brushShader = Shader.Find("Drawing/BrushStamp");
            if (_brushShader == null)
            {
                Debug.LogError("[GhostOverlayRenderer] Brush Shader not found!");
                return;
            }

            _brushMaterial = new Material(_brushShader);
            if (_defaultBrushTip != null) _brushMaterial.mainTexture = _defaultBrushTip;

            _cmd = new CommandBuffer();
            _cmd.name = "GhostBuffer";

            _quadMesh = CreateQuad();
            _props = new MaterialPropertyBlock();
        }

        // --- IGhostRenderer Implementation ---

        public void ConfigureBrush(BrushStrategy strategy, Texture2D runtimeTexture = null)
        {
            if (strategy == null) return;

            Texture2D tex = runtimeTexture != null ? runtimeTexture : strategy.MainTexture;
            if (tex == null) tex = _defaultBrushTip;
            
            if (_brushMaterial != null) _brushMaterial.mainTexture = tex;

            _brushOpacity = strategy.Opacity;
            // _sizeMultiplier = strategy.SizeMultiplier; // Removed, applied on input size if needed, but here we just take raw size from network
            // _currentSize = _baseBrushSize * _sizeMultiplier; // Removed

            _stampGenerator.RotationMode = strategy.RotationMode;
            _stampGenerator.SpacingRatio = strategy.SpacingRatio;
            _stampGenerator.AngleJitter = strategy.AngleJitter;
            _stampGenerator.Reset();
            
            // Procedural settings
             if (_brushMaterial != null)
            {
                _brushMaterial.SetInt("_BlendOp", (int)strategy.BlendOp);
                _brushMaterial.SetInt("_SrcBlend", (int)strategy.SrcBlend);
                _brushMaterial.SetInt("_DstBlend", (int)strategy.DstBlend);
                _brushMaterial.SetFloat("_UseProcedural", strategy.UseProceduralSDF ? 1.0f : 0.0f);
                _brushMaterial.SetFloat("_EdgeSoftness", strategy.EdgeSoftness);
            }
        }

        // Removed SetBrushSize/SetBrushColor as they are stateful and replaced by DrawGhostStroke arguments
        // public void SetBrushSize(float size) { ... }
        // public void SetBrushColor(Color color) { ... }

        // --- Retained Mode Support (For Extrapolation) ---

        public void BeginFrame()
        {
            if (_layoutController == null || _layoutController.ActiveRT == null) return;
            
            // Clear the Active RT completely
            _layoutController.ClearActiveRT();
        }

        public void DrawGhostStroke(IEnumerable<LogicPoint> points, float size, Color color, bool isEraser, BrushStrategy strategy)
        {
            if (_layoutController == null || _layoutController.ActiveRT == null) return;

            // Configure Brush immediately before drawing
            // This ensures we use the correct texture, spacing, and other settings
            if (strategy != null)
            {
                ConfigureBrush(strategy);
            }

            // Reset generator state for this fresh stroke draw
            _stampGenerator.Reset();
            
            // Generate stamps
            var pointList = points as List<LogicPoint>;
            if (pointList == null)
            {
                _pointBuffer.Clear();
                _pointBuffer.AddRange(points);
                pointList = _pointBuffer;
            }
            _stampGenerator.ProcessPoints(pointList, size, _stampBuffer);
            DrawStampsInternal(_stampBuffer, color, isEraser, strategy);
            _stampBuffer.Clear();
        }

        public void DrawGhostStamps(List<StampData> stamps, Color color, bool isEraser, BrushStrategy strategy)
        {
            if (_layoutController == null || _layoutController.ActiveRT == null) return;
            DrawStampsInternal(stamps, color, isEraser, strategy);
        }

        public void EndStroke()
        {
            _stampGenerator.Reset();
        }

        private void DrawStampsInternal(List<StampData> stamps, Color color, bool isEraser, BrushStrategy strategy)
        {
            if (stamps == null || stamps.Count == 0) return;

            if (strategy != null)
            {
                ConfigureBrush(strategy);
            }

            _cmd.Clear();
            _cmd.SetRenderTarget(_layoutController.ActiveRT);

            if (isEraser)
            {
                _brushMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                _brushMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                _brushMaterial.SetInt("_BlendOp", (int)BlendOp.Add);
                _props.SetColor("_Color", _eraserTrailColor);
            }
            else
            {
                Color drawColor = color;
                drawColor.a *= _brushOpacity;
                _props.SetColor("_Color", drawColor);
            }

            _cmd.SetViewMatrix(Matrix4x4.identity);
            _cmd.SetProjectionMatrix(Matrix4x4.Ortho(0, _layoutController.Resolution.x, 0, _layoutController.Resolution.y, -1, 1));

            _brushMaterial.enableInstancing = true;

            int batchCount = 0;
            for (int i = 0; i < stamps.Count; i++)
            {
                var stamp = stamps[i];

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

            Graphics.ExecuteCommandBuffer(_cmd);
        }

        // Unused legacy methods from IStrokeRenderer
        // public void ClearCanvas() { ... }
        // public void SetBakingMode(bool enabled) { ... }
        // public void RestoreFromBackBuffer() { ... }

        // --- Helpers ---

        private Mesh CreateQuad()
        {
            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[] {
                new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0),
                new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0)
            };
            mesh.uv = new Vector2[] {
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(0, 1), new Vector2(1, 1)
            };
            mesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
            return mesh;
        }
    }
}
