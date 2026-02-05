using System.Collections.Generic;
using Features.Drawing.Domain.ValueObject;
using System.Threading.Tasks;

namespace Features.Drawing.Domain.Interface
{
    public interface IDrawingRepository
    {
        Task SaveAsync(DrawingSessionData session);
        Task<DrawingSessionData> LoadAsync(string id);
        Task<List<DrawingSessionMetadata>> ListAllAsync();
        Task DeleteAsync(string id);
    }
    
    public struct DrawingSessionMetadata
    {
        public string Id;
        public string Name;
        public long ModifiedAt;
        public string ThumbnailPath;
    }
}
