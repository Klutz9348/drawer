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
            
            // If the list is huge, we might want to pre-archive some.
            // But relying on AddCommand to handle sliding window is safer logic-wise.
            
            foreach (var cmd in newHistory)
            {
                AddCommand(cmd);
            }
            
            // Note: AddCommand will trigger execution/baking if we are not careful.
            // But ReplaceHistory usually happens on Load, where we cleared the canvas.
            // However, AddCommand executes baking for things SLIDING OUT.
            // It does NOT execute the added command itself on the renderer.
            // Wait, AddCommand implementation:
            /*
                _history.Add(cmd);
                while (_history.Count > 50) { ... bake ... }
            */
            // It does not execute the new command.
            
            // So for Load, we probably want to execute them all to restore the visual state?
            // Or assumes the caller handles visual restoration?
            // DrawingAppService.LoadSessionAsync calls ClearCanvas(), then ReplaceHistory().
            // It doesn't seem to iterate and execute them.
            
            // If I use AddCommand, it might bake some if > 50.
            // But the ones remaining in _history won't be executed/drawn.
            
            // Let's modify logic:
            // 1. Clear everything.
            // 2. Add all to history.
            // 3. Re-render everything?
            
            // But wait, DrawingAppService's LoadSessionAsync just calls ReplaceHistory.
            // If ReplaceHistory doesn't draw, the canvas will be empty.
            
            // Let's look at AddCommand implementation again in my thought process or read it.
            // Read output of DrawingHistoryManager.cs earlier:
            // AddCommand adds to list, slides window, bakes removed ones.
            // It does NOT execute the added command.
            
            // So ReplaceHistory needs to:
            // 1. Clear internal lists.
            // 2. Add commands.
            // 3. Since we want to restore state, we should probably Execute them?
            // Or maybe the caller (AppService) expects to just set the data and then trigger a redraw?
            // But AppService.LoadSessionAsync ends after ReplaceHistory.
            
            // I should probably execute them here or in AppService.
            // Given the name "ReplaceHistory", it sounds like data manipulation.
            // But if I don't draw, the screen is blank.
            
            // Let's implement ReplaceHistory to just set the data, but arguably we should also re-execute them on the renderer to show them.
            // However, executing 10k strokes one by one might be slow.
            // But we have no choice if we want to restore the drawing.
            
            // Let's stick to the simplest implementation that satisfies the compilation error first.
            // Logic issues can be fixed later.
            
            // Actually, if I use AddCommand, it will bake old ones.
            // And for the new ones (last 50), they stay in _history.
            // I need to draw them.
            
            // Let's add a "RenderAll" logic or similar.
            // Or just loop and Execute.
            
            // NOTE: The previous implementation of LoadSessionAsync (before refactor) likely iterated and executed.
            // Now it builds a list and calls ReplaceHistory.
            
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
