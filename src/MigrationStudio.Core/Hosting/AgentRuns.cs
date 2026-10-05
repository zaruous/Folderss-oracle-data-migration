using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MigrationStudio.Core.Hosting
{
    public static class AgentRuns
    {
        public static List<AgentRunInfo> ListAlive(string dataDirectory)
        {
            var result = new List<AgentRunInfo>();
            if (string.IsNullOrEmpty(dataDirectory))
            {
                return result;
            }

            var dir = AgentRunFiles.RunsDirectory(dataDirectory);
            if (!Directory.Exists(dir))
            {
                return result;
            }

            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                AgentRunInfo info;
                try
                {
                    info = AgentRunFiles.Read(file);
                }
                catch
                {
                    continue;
                }

                if (info == null || string.IsNullOrEmpty(info.RunId))
                {
                    continue;
                }

                if (AgentProcessHelper.IsAlive(info.Pid, info.RunId, info.StartedAt))
                {
                    result.Add(info);
                }
            }

            return result;
        }

        public static List<AgentRunInfo> ListRecent(string dataDirectory, int count)
        {
            var result = new List<AgentRunInfo>();
            if (string.IsNullOrEmpty(dataDirectory) || count <= 0)
            {
                return result;
            }

            var dir = AgentRunFiles.RunsDirectory(dataDirectory);
            if (!Directory.Exists(dir))
            {
                return result;
            }

            var files = Directory.GetFiles(dir, "*.json")
                .Select(path => new { Path = path, Write = File.GetLastWriteTimeUtc(path) })
                .OrderByDescending(x => x.Write)
                .Take(Math.Max(count, 1) * 3)
                .ToList();

            foreach (var item in files)
            {
                AgentRunInfo info;
                try
                {
                    info = AgentRunFiles.Read(item.Path);
                }
                catch
                {
                    continue;
                }

                if (info == null)
                {
                    continue;
                }

                if (!AgentProcessHelper.IsAlive(info.Pid, info.RunId, info.StartedAt))
                {
                    if (string.Equals(info.State, "waiting", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(info.State, "running", StringComparison.OrdinalIgnoreCase))
                    {
                        info = Clone(info);
                        info.State = "crashed";
                    }
                }

                result.Add(info);
                if (result.Count >= count)
                {
                    break;
                }
            }

            return result;
        }

        public static void PruneOld(string dataDirectory, int logDays)
        {
            if (string.IsNullOrEmpty(dataDirectory) || logDays <= 0)
            {
                return;
            }

            var alive = new HashSet<string>(ListAlive(dataDirectory).Select(r => r.RunId), StringComparer.Ordinal);
            var cutoff = DateTime.UtcNow.AddDays(-logDays);
            PruneDirectory(AgentRunFiles.RunsDirectory(dataDirectory), "*.json", alive, cutoff);
            PruneDirectory(AgentRunFiles.LogsDirectory(dataDirectory), "*.log", alive, cutoff);
        }

        private static void PruneDirectory(string dir, string pattern, HashSet<string> keepRunIds, DateTime cutoffUtc)
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(dir, pattern))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (keepRunIds.Contains(name))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(file) >= cutoffUtc)
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                }
                catch
                {
                }
            }
        }

        private static AgentRunInfo Clone(AgentRunInfo source)
        {
            return new AgentRunInfo
            {
                RunId = source.RunId,
                Pid = source.Pid,
                Pipe = source.Pipe,
                StartedAt = source.StartedAt,
                AgentVersion = source.AgentVersion,
                State = source.State,
                JobName = source.JobName,
                Message = source.Message
            };
        }
    }
}
