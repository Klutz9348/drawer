namespace Features.Drawing.Domain.ValueObject
{
    /// <summary>
    /// Defines how the brush texture rotates.
    /// </summary>
    public enum BrushRotationMode
    {
        None = 0,       // Always 0 degrees
        Follow = 1,     // Follows stroke direction (Snake-like)
        Fixed = 2       // Fixed angle (Calligraphy-like), currently defaults to 45 deg or 0
    }
}
