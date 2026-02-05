using UnityEngine;
using Features.Drawing.Domain;
using Features.Drawing.Domain.Interface;
using Common.Constants;
using System.Linq;

namespace Features.Drawing.Service
{
    public class BrushRegistryService : IBrushRegistry
    {
        private readonly BrushStrategy[] _registeredBrushes;
        private readonly BrushStrategy _eraserStrategy;

        public BrushRegistryService(BrushStrategy[] brushes, BrushStrategy eraser)
        {
            _registeredBrushes = brushes;
            _eraserStrategy = eraser;
        }

        public BrushStrategy GetBrushStrategy(ushort id)
        {
            if (id == DrawingConstants.ERASER_BRUSH_ID) return _eraserStrategy;
            if (id == DrawingConstants.UNKNOWN_BRUSH_ID)
            {
                Debug.LogWarning($"[BrushRegistry] Received UNKNOWN_BRUSH_ID. Returning default or null.");
                return _registeredBrushes != null && _registeredBrushes.Length > 0 ? _registeredBrushes[0] : null;
            }

            if (_registeredBrushes != null && id < _registeredBrushes.Length)
            {
                return _registeredBrushes[id];
            }
            
            Debug.LogWarning($"[BrushRegistry] Brush ID {id} out of bounds (Count: {_registeredBrushes?.Length ?? 0}).");
            return _registeredBrushes != null && _registeredBrushes.Length > 0 ? _registeredBrushes[0] : null;
        }

        public ushort GetBrushId(BrushStrategy strategy)
        {
            if (strategy == _eraserStrategy) return DrawingConstants.ERASER_BRUSH_ID;
            
            if (strategy == null) return DrawingConstants.UNKNOWN_BRUSH_ID;

            if (_registeredBrushes != null)
            {
                for (int i = 0; i < _registeredBrushes.Length; i++)
                {
                    if (_registeredBrushes[i] == strategy) 
                    {
                        return (ushort)i;
                    }
                }
            }
            
            return DrawingConstants.UNKNOWN_BRUSH_ID;
        }
    }
}
