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
        private readonly IStrokeSmoothingService _smoothingService;
        private readonly IStrokeCollisionService _collisionService;

        public DrawingHistoryManager(
            IStrokeRenderer renderer,
            IStrokeSmoothingService smoothingService,
            IStrokeCollisionService collisionService)
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
        /// Returns the complete history (Archived + Active).
        /// </summary>
        public List<ICommand> GetFullHistory()
        {
            var fullList = new List<ICommand>(_archivedHistory.Count + _history.Count);
            fullList.AddRange(_archivedHistory);
            fullList.AddRange(_history);
            return fullList;
        }

        /// <summary>
        /// Replaces the entire history with a new set of commands.
        /// Useful for loading sessions.
        /// </summary>
        public void ReplaceHistory(List<ICommand> newHistory)
        {
            _history.Clear();
            _redoHistory.Clear();
            _archivedHistory.Clear();
            _activeStrokeIds.Clear();

            // We treat all loaded commands as "Active" initially for simplicity,
            // or we could split them if we want to enforce the limit immediately.
            // For now, let's just add them.
            
            // I'll implement ReplaceHistory to AddCommand (which handles baking/archiving) AND ensure the remaining active ones are drawn.
            
            foreach (var cmd in newHistory)
            {
                AddCommand(cmd);
            }
            
            // Draw the active ones
            foreach (var cmd in _history)
            {
                cmd.Execute(_renderer, _smoothingService);
            }
        }

        public void AddCommand(ICommand cmd)
        {
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
            if (_history.Count == 0) 
            {
                Debug.LogWarning("[History] Undo called but history is empty.");
                return;
            }

            // Remove last command
            var cmd = _history[_history.Count - 1];
            Debug.Log($"[Undo] Reverting command [ID: {cmd.Id}]. Remaining: {_history.Count - 1}");
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

            // Ensure we are not in baking mode
            _renderer.SetBakingMode(false);

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
                // This clears the active RT by copying the baked RT (which is clear or has baked strokes)
                _renderer.RestoreFromBackBuffer();
            }
            
            Debug.Log($"[RedrawHistory] Replaying {_history.Count - startIndex} commands. StartIndex: {startIndex}");

            // 3. Replay commands
            for (int i = startIndex; i < _history.Count; i++)
            {
                _history[i].Execute(_renderer, _smoothingService);
            }
        }
    }
}
