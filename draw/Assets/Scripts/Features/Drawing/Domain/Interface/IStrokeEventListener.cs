using Features.Drawing.Domain.Entity;
using Features.Drawing.Domain.ValueObject;

namespace Features.Drawing.Domain.Interface
{
    /// <summary>
    /// Listener interface for stroke lifecycle events.
    /// Implement this to support Network synchronization, Replay systems, or Analytics.
    /// </summary>
    public interface IStrokeEventListener
    {
        void OnStrokeStarted(StrokeEntity stroke);
        
        /// <summary>
        /// Called when points are added to the stroke. 
        /// Useful for real-time network transmission.
        /// </summary>
        void OnStrokeUpdated(StrokeEntity stroke, LogicPoint newPoint);
        
        void OnStrokeCompleted(StrokeEntity stroke);
    }
}
