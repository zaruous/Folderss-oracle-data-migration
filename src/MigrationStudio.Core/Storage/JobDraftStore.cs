using System;
using System.IO;
using System.Text;
using System.Text.Json;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;

namespace MigrationStudio.Core.Storage
{
    public static class JobDraftStore
    {
        public static string PathFor(string dataDirectory)
        {
            return Path.Combine(dataDirectory, "jobs", "current.draft.json");
        }

        public static void Save(string dataDirectory, JobDraft draft)
        {
            if (draft == null)
            {
                throw new ArgumentNullException(nameof(draft));
            }

            draft.SavedAt = DateTime.Now;
            var path = PathFor(dataDirectory);
            draft.FilePath = path;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var settings = MigrationSettingsStore.CreateDefaultSettings();
            var json = JobFile.Serialize(draft.Job, settings);
            var envelope = JsonSerializer.Serialize(new JobDraftEnvelope
            {
                SavedAt = draft.SavedAt,
                FilePath = path,
                Dirty = draft.Dirty,
                JobJson = json
            }, JobJson.Options);

            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, envelope, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        public static JobDraft TryLoad(string dataDirectory)
        {
            var path = PathFor(dataDirectory);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var envelope = JsonSerializer.Deserialize<JobDraftEnvelope>(text, JobJson.Options);
                if (envelope == null || string.IsNullOrEmpty(envelope.JobJson))
                {
                    RenameBad(path);
                    return null;
                }

                var job = JobFile.Parse(envelope.JobJson);
                return new JobDraft
                {
                    SavedAt = envelope.SavedAt,
                    FilePath = path,
                    Dirty = envelope.Dirty,
                    Job = job
                };
            }
            catch (JsonException)
            {
                RenameBad(path);
                return null;
            }
            catch (JobFileException)
            {
                RenameBad(path);
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static void RenameBad(string path)
        {
            try
            {
                var bad = path + ".bad";
                if (File.Exists(bad))
                {
                    File.Delete(bad);
                }

                File.Move(path, bad);
            }
            catch (IOException)
            {
            }
        }

        private sealed class JobDraftEnvelope
        {
            public DateTime SavedAt { get; set; }
            public string FilePath { get; set; }
            public bool Dirty { get; set; }
            public string JobJson { get; set; }
        }
    }
}
