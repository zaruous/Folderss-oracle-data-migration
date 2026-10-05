using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MigrationStudio.Core.Engine
{
    public sealed class LocalCheckpointStore : ICheckpointStore
    {
        private readonly string _directory;
        private readonly object _gate = new object();

        public LocalCheckpointStore(string dataDirectory)
        {
            _directory = Path.Combine(dataDirectory ?? "", "checkpoints");
        }

        public Task<CheckpointRecord> GetAsync(string job, string taskKey, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(Read(job).FirstOrDefault(r => string.Equals(r.TaskKey, taskKey, StringComparison.Ordinal)));
            }
        }

        public Task<List<CheckpointRecord>> ListAsync(string job, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(Read(job));
            }
        }

        public Task SaveLocalAsync(CheckpointRecord record, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var records = Read(record.Job);
                records.RemoveAll(r => string.Equals(r.TaskKey, record.TaskKey, StringComparison.Ordinal));
                records.Add(record);
                Write(record.Job, records);
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string job, string taskKey, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var records = Read(job);
                records.RemoveAll(r => string.Equals(r.TaskKey, taskKey, StringComparison.Ordinal));
                Write(job, records);
            }

            return Task.CompletedTask;
        }

        private List<CheckpointRecord> Read(string job)
        {
            var path = PathFor(job);
            if (!File.Exists(path))
            {
                return new List<CheckpointRecord>();
            }

            return JsonSerializer.Deserialize<List<CheckpointRecord>>(File.ReadAllText(path)) ?? new List<CheckpointRecord>();
        }

        private void Write(string job, List<CheckpointRecord> records)
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(job);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }

        private string PathFor(string job)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string((job ?? "job").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return Path.Combine(_directory, name + ".json");
        }
    }
}
