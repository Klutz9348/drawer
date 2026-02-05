using System.Collections.Generic;
using Features.Drawing.Domain.ValueObject;

namespace Features.Drawing.Domain.Interface
{
    public interface IStrokeSmoothingService
    {
        void SmoothPoints(List<LogicPoint> controlPoints, List<LogicPoint> outputBuffer);
    }
}
