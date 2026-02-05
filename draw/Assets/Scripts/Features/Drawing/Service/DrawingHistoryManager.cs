using System.Collections.Generic;
using UnityEngine;
using Features.Drawing.Domain.Interface;
using Features.Drawing.App.Command;
using Features.Drawing.App.Interface;

namespace Features.Drawing.Service
{
    /// <summary>
    /// Manages the command history, undo/redo stacks, and synchronization state.
    /// Extracted from DrawingAppService to separate concerns.
    /// </summary>
    public class DrawingHistoryManager
    {
        // State
        private List<ICommand> _history = new List<ICommand>();
        private List<ICommand> _redoHistory = new List<ICommand>();
        private List<ICommand> _archivedHistory = new List<ICommand>();
        
        // Tracks IDs of currently active strokes (history + archived)
        private HashSet<string> _activeStrokeIds = new HashSet<string>();

        // Dependencies
        private readonly IStrokeRenderer _renderer;
        private readonly StrokeSmoothingService _smoothingService;
        private readonly StrokeCollisionService _collisionService;

        public DrawingHistoryManager(
            IStrokeRenderer renderer,
            StrokeSmoothingService smoothingService,
            StrokeCollisionService collisionService)
        {
            _renderer = renderer;
            _smoothingService = smoothingService;
            _collisionService = collisionService;
        }

        // Public Accessors
        public IReadOnlyList<ICommand> History => _history;
        public IReadOnlyList<ICommand> ArchivedHistory => _archivedHistory;
        public HashSet<string> ActiveStrokeIds => _activeStrokeIds;
        public bool CanUndo => _history.Count > 0;
        public bool CanRedo => _redoHistory.Count > 0;

        /// <summary>
        /// Adds a command to history and handles sliding window/baking.
        /// </summary>
        public void AddCommand(ICommand cmd)
        {
            Debug.Log($"[History] Added command: {cmd.GetType().Name} [ID: {cmd.Id}]. Count: {_history.Count + 1}");
            _history.Add(cmd);
            _activeStrokeIds.Add(cmd.Id);

            _redoHistory.Clear();

            // Maintain sliding window (Keep last 50 active)
            while (_history.Count > 50)
            {
                var removedCmd = _history[0];
                
                // Archive it (Logical Save)
                _archivedHistory.Add(removedCmd);
                // Note: We KEEP the ID in _activeStrokeIds because it is still part of the drawing
                
                // Optimization: If the baked command is a ClearCanvas, 
                // we can safely discard all previous archive history to save RAM.
                if (removedCmd is ClearCanvasCommand)
                {
                    // Everything before a Clear is visually irrelevant.
                    foreach (var archivedCmd in _archivedHistory)
                    {
                        if (archivedCmd != removedCmd)
                        {
                            _activeStrokeIds.Remove(archivedCmd.Id);
                        }
                    }

                    _archivedHistory.Clear();
                    _archivedHistory.Add(removedCmd);
                }

                // Bake the command into the back buffer before removing it (Visual Save)
                if (_renderer != null)
                {
                    _renderer.SetBakingMode(true);
                    removedCmd.Execute(_renderer, _smoothingService);
                    _renderer.SetBakingMode(false);
                }
                
                _history.RemoveAt(0);
            }
        }

        public void Undo()
        {
            if (_history.Count == 0) return;

            // Remove last command
            var cmd = _history[_history.Count - 1];
            Debug.Log($"[Undo] Reverting command [ID: {cmd.Id}]");
            _history.RemoveAt(_history.Count - 1);
            
            _activeStrokeIds.Remove(cmd.Id);

            // Add to Redo history
            _redoHistory.Add(cmd);
            
            RedrawHistory();
        }

        public void Redo()
        {
            if (_redoHistory.Count == 0) return;

            // Remove last redo item
            var cmd = _redoHistory[_redoHistory.Count - 1];
            Debug.Log($"[Redo] Restoring command [ID: {cmd.Id}]");
            _redoHistory.RemoveAt(_redoHistory.Count - 1);
            
            // Add back to history
            _history.Add(cmd);
            _activeStrokeIds.Add(cmd.Id);

            RedrawHistory();
        }

        private void RedrawHistory()
        {
            if (_renderer == null) return;

            // 1. Determine start state
            int startIndex = 0;
            bool fullClear = false;

            // Check if we have a ClearCanvasCommand in history
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i] is ClearCanvasCommand)
                {
                    startIndex = i;
                    fullClear = true;
                    break;
                }
            }

            // 2. Prepare Canvas
            if (fullClear)
            {
                _renderer.ClearCanvas();
            }
            else
            {
                _renderer.RestoreFromBackBuffer();
            }
            
            // 3. Replay commands
            for (int i = startIndex; i < _history.Count; i++)
            {
                _history[i].Execute(_renderer, _smoothingService);
            }
        }
    }
}
