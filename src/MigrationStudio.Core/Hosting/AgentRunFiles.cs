using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using MigrationStudio.Core.Engine;

namespace MigrationStudio.Core.Hosting
{
    public static class AgentRunFiles
    {
        public static string RunsDirectory(string dataDirectory)
        {
            return Path.Combine(dataDirectory, "runs");
        }

        public static string RunFilePath(string dataDirectory, string runId)
        {
            return Path.Combine(RunsDirectory(dataDirectory), runId + ".json");
        }

        public static string LogsDirectory(string dataDirectory)
        {
            return Path.Combine(dataDirectory, "logs");
        }

        public static string LogFilePath(string dataDirectory, string runId)
        {
            return Path.Combine(LogsDirectory(dataDirectory), runId + ".log");
        }

        public static AgentRunInfo Read(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            return Deserialize(json);
        }

        public static AgentRunInfo Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var info = new AgentRunInfo();
                info.RunId = GetString(root, "runId");
                info.Pid = GetInt(root, "pid");
                info.Pipe = GetString(root, "pipe");
                info.AgentVersion = GetString(root, "agentVersion");
                info.State = GetString(root, "state");
                info.JobName = GetString(root, "jobName");
                info.Message = GetString(root, "message");
                info.StartedAt = ParseDate(GetString(root, "startedAt"));
                info.Cycle = GetInt(root, "cycle");
                var lastCycle = ParseDate(GetString(root, "lastCycleAt"));
                info.LastCycleAt = lastCycle == DateTime.MinValue ? (DateTime?)null : lastCycle;
                return info;
            }
        }

        public static void WriteAtomic(string path, AgentRunInfo info)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var json = Serialize(info);
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
        }

        public static string Serialize(AgentRunInfo info)
        {
            var started = info.StartedAt.ToString("o", CultureInfo.InvariantCulture);
            var obj = new
            {
                runId = info.RunId,
                pid = info.Pid,
                pipe = info.Pipe,
                startedAt = started,
                agentVersion = info.AgentVersion,
                state = info.State,
                jobName = info.JobName,
                message = info.Message,
                cycle = info.Cycle,
                lastCycleAt = info.LastCycleAt.HasValue ? info.LastCycleAt.Value.ToString("o", CultureInfo.InvariantCulture) : null
            };
            return JsonSerializer.Serialize(obj, AgentProtocol.JsonOptions);
        }

        private static string GetString(JsonElement root, string name)
        {
            JsonElement value;
            return root.TryGetProperty(name, out value) ? value.GetString() : null;
        }

        private static int GetInt(JsonElement root, string name)
        {
            JsonElement value;
            return root.TryGetProperty(name, out value) && value.TryGetInt32(out var n) ? n : 0;
        }

        private static DateTime ParseDate(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return DateTime.MinValue;
            }

            DateTime parsed;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
            {
                return parsed;
            }

            return DateTime.MinValue;
        }
    }
}
