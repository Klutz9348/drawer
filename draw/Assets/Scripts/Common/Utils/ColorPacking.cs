using UnityEngine;

namespace Common.Utils
{
    public static class ColorPacking
    {
        public static uint ToUInt(Color color)
        {
            Color32 c32 = color;
            return (uint)((c32.r << 24) | (c32.g << 16) | (c32.b << 8) | c32.a);
        }

        public static Color ToColor(uint color)
        {
            byte r = (byte)((color >> 24) & 0xFF);
            byte g = (byte)((color >> 16) & 0xFF);
            byte b = (byte)((color >> 8) & 0xFF);
            byte a = (byte)(color & 0xFF);
            return new Color32(r, g, b, a);
        }
    }
}
