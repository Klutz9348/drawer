using Features.Drawing.Domain.Interface;
using Features.Drawing.Service;

namespace Features.Drawing.App.Command
{
    public class ClearCanvasCommand : ICommand
    {
        public string Id { get; } = System.Guid.NewGuid().ToString();
        public long SequenceId { get; }

        public ClearCanvasCommand(long sequenceId)
        {
            SequenceId = sequenceId;
        }

        public void Execute(IStrokeRenderer renderer, IStrokeSmoothingService smoothingService)
        {
            renderer.ClearCanvas();
        }
    }
}
