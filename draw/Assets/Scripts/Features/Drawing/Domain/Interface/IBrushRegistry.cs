using Features.Drawing.Domain;

namespace Features.Drawing.Domain.Interface
{
    public interface IBrushRegistry
    {
        BrushStrategy GetBrushStrategy(ushort id);
        ushort GetBrushId(BrushStrategy strategy);
    }
}
