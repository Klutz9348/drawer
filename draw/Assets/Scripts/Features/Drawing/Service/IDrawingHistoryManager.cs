using System.Collections.Generic;
using Features.Drawing.App.Command;

namespace Features.Drawing.Service
{
    public interface IDrawingHistoryManager
    {
        IReadOnlyList<ICommand> History { get; }
        IReadOnlyList<ICommand> ArchivedHistory { get; }
        HashSet<string> ActiveStrokeIds { get; }
        bool CanUndo { get; }
        bool CanRedo { get; }

        List<ICommand> GetFullHistory();
        void ReplaceHistory(List<ICommand> newHistory);
        void AddCommand(ICommand cmd);
        void Undo();
        void Redo();
    }
}
