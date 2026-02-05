using Features.Drawing.Domain.Interface;

namespace Features.Drawing.App.Command
{
    public interface ICommand
    {
        string Id { get; }
        long SequenceId { get; }
        void Execute(IStrokeRenderer renderer, IStrokeSmoothingService smoothingService);
    }
}
