using System.Collections.Generic;
using System.Threading.Tasks;
using Features.Drawing.App.Command;

namespace Features.Drawing.Service
{
    public interface IDrawingPersistenceService
    {
        Task SaveSessionAsync(string sessionId, List<ICommand> history);
        Task<List<ICommand>> LoadSessionAsync(string sessionId);
    }
}
