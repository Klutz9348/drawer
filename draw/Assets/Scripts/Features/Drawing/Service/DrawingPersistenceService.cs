using System.Collections.Generic;
using System.Threading.Tasks;
using Features.Drawing.Domain.Interface;
using Features.Drawing.Domain.ValueObject;
using Features.Drawing.Domain.Entity;
using Features.Drawing.App.Command;
using Features.Drawing.App.Interface;
using Common.Utils;
using Common.Diagnostics;
using Features.Drawing.Domain;

namespace Features.Drawing.Service
{
    public class DrawingPersistenceService : IDrawingPersistenceService
    {
        private readonly IDrawingRepository _repository;
        private readonly IBrushRegistry _brushRegistry;
        private readonly IStructuredLogger _logger;

        public DrawingPersistenceService(
            IDrawingRepository repository, 
            IBrushRegistry registry,
            IStructuredLogger logger = null)
        {
            _repository = repository;
            _brushRegistry = registry;
            _logger = logger;
        }

        public async Task SaveSessionAsync(string sessionId, List<ICommand> history)
        {
            if (_repository == null)
            {
                _logger?.Error("Cannot save session: Repository not initialized.");
                return;
            }

            var sessionData = new DrawingSessionData
            {
                Id = sessionId,
                CreatedAt = System.DateTime.UtcNow.Ticks,
                ModifiedAt = System.DateTime.UtcNow.Ticks,
                Strokes = new List<StrokeData>()
            };

            foreach (var cmd in history)
            {
                if (cmd is DrawStrokeCommand drawCmd)
                {
                    var s = drawCmd.Stroke;
                    if (s == null) continue;

                    var sData = new StrokeData
                    {
                        Id = s.Id,
                        AuthorId = s.AuthorId,
                        BrushId = s.BrushId,
                        Seed = s.Seed,
                        Size = s.Size,
                        ColorRGBA = ColorPacking.ToUInt(s.Color),
                        SequenceId = s.SequenceId,
                        Points = new List<LogicPointData>()
                    };
                    
                    foreach (var p in s.Points)
                    {
                        sData.Points.Add(new LogicPointData { X = p.X, Y = p.Y, Pressure = p.Pressure });
                    }
                    sessionData.Strokes.Add(sData);
                }
            }

            await _repository.SaveAsync(sessionData);
            _logger?.Info($"Session {sessionId} saved with {sessionData.Strokes.Count} strokes.");
        }

        public async Task<List<ICommand>> LoadSessionAsync(string sessionId)
        {
            if (_repository == null)
            {
                _logger?.Error("Cannot load session: Repository not initialized.");
                return null;
            }

            var sessionData = await _repository.LoadAsync(sessionId);
            if (sessionData == null) return null;

            var commands = new List<ICommand>();
            foreach (var sData in sessionData.Strokes)
            {
                var points = new List<LogicPoint>();
                foreach (var p in sData.Points)
                {
                    points.Add(new LogicPoint(p.X, p.Y, p.Pressure));
                }

                var stroke = new StrokeEntity(
                    sData.Id,
                    sData.AuthorId,
                    sData.BrushId,
                    sData.Seed,
                    ColorPacking.ToColor(sData.ColorRGBA),
                    sData.Size,
                    sData.SequenceId,
                    points
                );
                
                var strategy = _brushRegistry.GetBrushStrategy(sData.BrushId);
                if (strategy != null)
                {
                    commands.Add(new DrawStrokeCommand(stroke, strategy));
                }
            }
            
            return commands;
        }
    }
}
