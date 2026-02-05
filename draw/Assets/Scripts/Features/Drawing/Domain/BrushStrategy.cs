using UnityEngine;
using UnityEngine.Rendering;
using Features.Drawing.Domain.ValueObject;

namespace Features.Drawing.Domain
{
    [CreateAssetMenu(fileName = "NewBrush", menuName = "Drawing/Brush Strategy")]
    public class BrushStrategy : ScriptableObject
    {
        [Header("Appearance")]
        [Tooltip("The base texture for the brush stamp.")]
        public Texture2D MainTexture;
        
        [Tooltip("If true, the texture will be generated procedurally at runtime (e.g. for pixel-perfect hard brushes).")]
        public bool UseRuntimeGeneration;

        [Header("Rendering")]
        [Range(0.001f, 1.0f)]
        public float SpacingRatio = 0.15f;
        
        [Tooltip("Multiplier for the brush size to compensate for visual differences (e.g. soft brushes looking smaller).")]
        public float SizeMultiplier = 1.0f;

        public BrushRotationMode RotationMode = BrushRotationMode.None;
        
        [Range(0f, 360f)]
        public float AngleJitter = 0f;
        
        [Range(0f, 1f)]
        public float Opacity = 1.0f;

        [Header("Blending")]
        public BlendOp BlendOp = BlendOp.Add;
        public BlendMode SrcBlend = BlendMode.One;
        public BlendMode DstBlend = BlendMode.OneMinusSrcAlpha;

        [Header("Procedural SDF")]
        [Tooltip("Use shader-based SDF for perfect circles instead of texture. Overrides MainTexture.")]
        public bool UseProceduralSDF = true;

        [Range(0.001f, 0.5f)]
        [Tooltip("Softness of the brush edge in SDF mode. 0.001 is hard, 0.5 is very soft.")]
        public float EdgeSoftness = 0.05f;

        [Header("Input Processing")]
        public bool EnableStabilizer = false;

        [Range(0f, 0.95f)]
        [Tooltip("Stabilization factor (0 = raw input, 0.9 = heavy smoothing/lag).")]
        public float StabilizationFactor = 0f;
    }
}