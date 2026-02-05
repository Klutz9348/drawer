using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Features.Drawing.Domain.Interface;
using Features.Drawing.Domain.ValueObject;

namespace Features.Drawing.App.Data
{
    public class LocalFileDrawingRepository : IDrawingRepository
    {
        private readonly string _storagePath;

        public LocalFileDrawingRepository(string storagePath)
        {
            _storagePath = storagePath;
            if (!Directory.Exists(_storagePath))
            {
                Directory.CreateDirectory(_storagePath);
            }
        }

        public Task SaveAsync(DrawingSessionData session)
        {
            return Task.Run(() =>
            {
                try
                {
                    string filePath = Path.Combine(_storagePath, $"{session.Id}.json");
                    string json = JsonUtility.ToJson(session, true);
                    File.WriteAllText(filePath, json);
                    
                    // Ideally we would also save a thumbnail here
                    Debug.Log($"[LocalFileDrawingRepository] Saved session {session.Id} to {filePath}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[LocalFileDrawingRepository] Failed to save session {session.Id}: {e.Message}");
                    throw;
                }
            });
        }

        public Task<DrawingSessionData> LoadAsync(string id)
        {
            return Task.Run(() =>
            {
                try
                {
                    string filePath = Path.Combine(_storagePath, $"{id}.json");
                    if (!File.Exists(filePath))
                    {
                        Debug.LogWarning($"[LocalFileDrawingRepository] Session {id} not found at {filePath}");
                        return null;
                    }

                    string json = File.ReadAllText(filePath);
                    return JsonUtility.FromJson<DrawingSessionData>(json);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[LocalFileDrawingRepository] Failed to load session {id}: {e.Message}");
                    throw;
                }
            });
        }

        public Task<List<DrawingSessionMetadata>> ListAllAsync()
        {
            return Task.Run(() =>
            {
                var result = new List<DrawingSessionMetadata>();
                try
                {
                    var files = Directory.GetFiles(_storagePath, "*.json");
                    foreach (var file in files)
                    {
                        var info = new FileInfo(file);
                        var id = Path.GetFileNameWithoutExtension(file);
                        
                        // Note: To get the real name, we might need to read the file or store metadata separately.
                        // For performance, we'll just use the ID/Filename for now.
                        
                        result.Add(new DrawingSessionMetadata
                        {
                            Id = id,
                            Name = $"Session {id}", // Placeholder name
                            ModifiedAt = info.LastWriteTime.Ticks,
                            ThumbnailPath = "" // Placeholder
                        });
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[LocalFileDrawingRepository] Failed to list sessions: {e.Message}");
                }
                return result;
            });
        }

        public Task DeleteAsync(string id)
        {
            return Task.Run(() =>
            {
                try
                {
                    string filePath = Path.Combine(_storagePath, $"{id}.json");
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        Debug.Log($"[LocalFileDrawingRepository] Deleted session {id}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[LocalFileDrawingRepository] Failed to delete session {id}: {e.Message}");
                    throw;
                }
            });
        }
    }
}
