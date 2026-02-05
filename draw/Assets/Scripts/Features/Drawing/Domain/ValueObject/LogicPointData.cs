using System;

namespace Features.Drawing.Domain.ValueObject
{
    [Serializable]
    public struct LogicPointData
    {
        public ushort X;
        public ushort Y;
        public byte Pressure;
        
        public LogicPointData(ushort x, ushort y, byte pressure)
        {
            X = x;
            Y = y;
            Pressure = pressure;
        }
    }
}
