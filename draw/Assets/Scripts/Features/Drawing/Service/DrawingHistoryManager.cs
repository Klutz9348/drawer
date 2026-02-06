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
    public class DrawingHistoryManager : IDrawingHistoryManager
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

            if (newHistory == null || newHistory.Count == 0) return;

            // Bulk Load Strategy:
            // 1. Archive everything except the last 50 commands.
            // 2. Add last 50 to active history.
            // 3. Bake the archived commands efficiently.
            
            int total = newHistory.Count;
            int activeCount = Mathf.Min(total, 50);
            int archiveCount = total - activeCount;

            // Phase 1: Archive & Bake
            if (archiveCount > 0)
            {
                if (_renderer != null) _renderer.SetBakingMode(true);
                
                for (int i = 0; i < archiveCount; i++)
                {
                    var cmd = newHistory[i];
                    _archivedHistory.Add(cmd);
                    // Bake
                    if (_renderer != null)
                    {
                        cmd.Execute(_renderer, _smoothingService);
                    }
                }
                
                if (_renderer != null) _renderer.SetBakingMode(false);
            }

            // Phase 2: Active History
            for (int i = archiveCount; i < total; i++)
            {
                var cmd = newHistory[i];
                _history.Add(cmd);
                _activeStrokeIds.Add(cmd.Id);
            }
            
            // Phase 3: Draw Active Commands (Visual Refresh)
            // We only need to draw the active ones because archived ones are already baked into the background.
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

            var fullHistory = GetFullHistory();

            int startIndex = 0;
            bool hasClear = false;

            for (int i = fullHistory.Count - 1; i >= 0; i--)
            {
                if (fullHistory[i] is ClearCanvasCommand)
                {
                    startIndex = i + 1;
                    hasClear = true;
                    break;
                }
            }

            if (hasClear)
            {
                _renderer.ClearCanvas();
            }
            else if (_archivedHistory.Count > 0)
            {
                _renderer.RestoreFromBackBuffer();
            }
            else
            {
                _renderer.ClearCanvas();
            }

            Debug.Log($"[RedrawHistory] Replaying {fullHistory.Count - startIndex} commands. StartIndex: {startIndex}");

            for (int i = startIndex; i < fullHistory.Count; i++)
            {
                fullHistory[i].Execute(_renderer, _smoothingService);
            }
        }
    }
}
