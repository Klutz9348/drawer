using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Features.Drawing.App.Data;
using Features.Drawing.Domain.ValueObject;

namespace Tests.App.Data
{
    public class LocalFileDrawingRepositoryTests
    {
        private string _testPath;
        private LocalFileDrawingRepository _repository;

        [SetUp]
        public void Setup()
        {
            _testPath = Path.Combine(Application.temporaryCachePath, "TestSessions");
            if (Directory.Exists(_testPath))
            {
                Directory.Delete(_testPath, true);
            }
            _repository = new LocalFileDrawingRepository(_testPath);
        }

        [TearDown]
        public void Teardown()
        {
            if (Directory.Exists(_testPath))
            {
                Directory.Delete(_testPath, true);
            }
        }

        [Test]
        public async Task SaveAndLoad_ShouldPersistData()
        {
            // Arrange
            var session = new DrawingSessionData
            {
                Id = "test_session_1",
                CreatedAt = 1000,
                ModifiedAt = 2000,
                Strokes = new List<StrokeData>
                {
                    new StrokeData
                    {
                        Id = 1,
                        BrushId = 1,
                        Points = new List<LogicPointData>
                        {
                            new LogicPointData { X = 10, Y = 10, Pressure = 128 },
                            new LogicPointData { X = 20, Y = 20, Pressure = 255 }
                        }
                    }
                }
            };

            // Act
            await _repository.SaveAsync(session);
            var loaded = await _repository.LoadAsync(session.Id);

            // Assert
            Assert.IsNotNull(loaded);
            Assert.AreEqual(session.Id, loaded.Id);
            Assert.AreEqual(session.Strokes.Count, loaded.Strokes.Count);
            Assert.AreEqual(session.Strokes[0].Points.Count, loaded.Strokes[0].Points.Count);
            Assert.AreEqual(session.Strokes[0].Points[1].Pressure, loaded.Strokes[0].Points[1].Pressure);
        }

        [Test]
        public async Task ListAll_ShouldReturnSavedSessions()
        {
            // Arrange
            var s1 = new DrawingSessionData { Id = "s1" };
            var s2 = new DrawingSessionData { Id = "s2" };

            // Act
            await _repository.SaveAsync(s1);
            await _repository.SaveAsync(s2);
            var list = await _repository.ListAllAsync();

            // Assert
            Assert.AreEqual(2, list.Count);
            Assert.IsTrue(list.Exists(x => x.Id == "s1"));
            Assert.IsTrue(list.Exists(x => x.Id == "s2"));
        }

        [Test]
        public async Task Delete_ShouldRemoveFile()
        {
            // Arrange
            var s1 = new DrawingSessionData { Id = "s1" };
            await _repository.SaveAsync(s1);

            // Act
            await _repository.DeleteAsync("s1");
            var loaded = await _repository.LoadAsync("s1");

            // Assert
            Assert.IsNull(loaded);
        }
    }
}
