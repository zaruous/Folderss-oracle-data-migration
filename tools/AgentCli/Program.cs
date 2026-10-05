using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Hosting;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;

namespace AgentCli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args == null || args.Length == 0)
                {
                    Usage();
                    return 2;
                }

                var cmd = args[0].ToLowerInvariant();
                if (cmd == "run") return RunAsync(args).GetAwaiter().GetResult();
                if (cmd == "attach") return AttachAsync(args).GetAwaiter().GetResult();
                if (cmd == "list") return List(args);
                if (cmd == "pause" || cmd == "resume" || cmd == "stop") return Control(cmd, args);
                Usage();
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            string jobPath = null;
            string settingsPath = null;
            var seedIt = false;
            var mode = "EXECUTE";
            var data = Path.Combine(Path.GetTempPath(), "migration-agentcli-" + Guid.NewGuid().ToString("N"));
            string passwordSrc = null;
            string passwordTgt = null;
            var selected = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--job" && i + 1 < args.Length) jobPath = args[++i];
                else if (args[i] == "--settings" && i + 1 < args.Length) settingsPath = args[++i];
                else if (args[i] == "--seed-it") seedIt = true;
                else if (args[i] == "--mode" && i + 1 < args.Length) mode = args[++i];
                else if (args[i] == "--data" && i + 1 < args.Length) data = args[++i];
                else if (args[i] == "--password-src" && i + 1 < args.Length) passwordSrc = args[++i];
                else if (args[i] == "--password-tgt" && i + 1 < args.Length) passwordTgt = args[++i];
                else if (args[i] == "--select" && i + 1 < args.Length)
                {
                    foreach (var id in args[++i].Split(',')) selected.Add(id.Trim());
                }
            }

            if (string.IsNullOrEmpty(jobPath))
            {
                Usage();
                return 2;
            }

            Directory.CreateDirectory(data);
            var job = JobFile.Parse(File.ReadAllText(jobPath));
            MigrationSettings settings;
            if (seedIt)
            {
                settings = SeedItSettings();
            }
            else if (!string.IsNullOrEmpty(settingsPath))
            {
                settings = MigrationSettingsStore.Deserialize(File.ReadAllText(settingsPath));
            }
            else
            {
                settings = MigrationSettingsStore.CreateDefaultSettings();
            }

            var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugin");
            Directory.CreateDirectory(pluginDir);
            var agentPublish = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "MigrationAgent", "bin", "Release", "net8.0", "win-x64", "publish"));
            if (!Directory.Exists(agentPublish))
            {
                agentPublish = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "MigrationAgent", "bin", "Release", "net8.0", "win-x64"));
            }

            CopyAgent(agentPublish, Path.Combine(pluginDir, "agent"));
            var dirs = new PluginDirs(pluginDir, data);
            var adapter = DatabaseAdapters.For("oracle");
            var launcher = new RunLauncher(dirs, settings, adapter);
            var srcProfile = MigrationSettingsStore.Find(settings, job.Source.ProfileId);
            var tgtProfile = MigrationSettingsStore.Find(settings, job.Target.ProfileId);
            var sourceMeta = EmptyMeta(job.Source.Schema);
            var targetMeta = EmptyMeta(job.Target.Schema);
            if (selected.Count == 0)
            {
                foreach (var m in job.Mappings) selected.Add(m.Id);
            }

            using (var client = await launcher.StartAsync(job, sourceMeta, targetMeta, selected, mode,
                (profile, reason) => PasswordFor(profile, srcProfile, tgtProfile, passwordSrc, passwordTgt),
                new Progress<string>(s => Console.WriteLine("[" + s + "]")),
                CancellationToken.None).ConfigureAwait(false))
            {
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("연결만 끊습니다. 에이전트는 계속 실행됩니다 — attach로 다시 붙으세요.");
                    client.Dispose();
                };
                await MonitorAsync(client).ConfigureAwait(false);
            }

            return 0;
        }

        private static async Task<int> AttachAsync(string[] args)
        {
            string data = null;
            string runId = null;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--data" && i + 1 < args.Length) data = args[++i];
                else if (args[i] == "--run" && i + 1 < args.Length) runId = args[++i];
            }

            if (string.IsNullOrEmpty(data))
            {
                Usage();
                return 2;
            }

            AgentRunInfo run = null;
            if (!string.IsNullOrEmpty(runId))
            {
                run = AgentRunFiles.Read(AgentRunFiles.RunFilePath(data, runId));
            }
            else
            {
                run = AgentRuns.ListAlive(data).FirstOrDefault();
            }

            if (run == null)
            {
                Console.Error.WriteLine("붙을 에이전트가 없습니다.");
                return 1;
            }

            var dirs = new PluginDirs(Path.Combine(AppContext.BaseDirectory, "plugin"), data);
            var launcher = new RunLauncher(dirs, MigrationSettingsStore.CreateDefaultSettings(), DatabaseAdapters.For("oracle"));
            using (var client = await launcher.AttachAsync(run, CancellationToken.None).ConfigureAwait(false))
            {
                await MonitorAsync(client).ConfigureAwait(false);
            }

            return 0;
        }

        private static int List(string[] args)
        {
            string data = null;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--data" && i + 1 < args.Length) data = args[++i];
            }

            if (string.IsNullOrEmpty(data))
            {
                Usage();
                return 2;
            }

            Console.WriteLine("=== 살아 있음 ===");
            foreach (var r in AgentRuns.ListAlive(data))
            {
                Console.WriteLine(r.RunId + " PID=" + r.Pid + " " + r.JobName + " " + r.State);
            }

            Console.WriteLine("=== 최근 ===");
            foreach (var r in AgentRuns.ListRecent(data, 10))
            {
                Console.WriteLine(r.RunId + " PID=" + r.Pid + " " + r.JobName + " " + r.State);
            }

            return 0;
        }

        private static int Control(string cmd, string[] args)
        {
            string data = null;
            string runId = null;
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--data" && i + 1 < args.Length) data = args[++i];
                else if (args[i] == "--run" && i + 1 < args.Length) runId = args[++i];
            }

            var run = AgentRunFiles.Read(AgentRunFiles.RunFilePath(data, runId));
            using (var client = AgentClient.Attach(run, data))
            {
                client.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
                if (cmd == "pause") client.Pause();
                else if (cmd == "resume") client.Resume();
                else client.Stop();
            }

            return 0;
        }

        private static async Task MonitorAsync(IAgentClient client)
        {
            var done = new TaskCompletionSource<bool>();
            client.Snapshot += s =>
            {
                var total = s.Totals != null ? s.Totals.Total : 0;
                var doneRows = s.Totals != null ? s.Totals.Done : 0;
                var pct = s.Totals != null ? s.Totals.Pct : 0;
                var rate = s.Rate;
                var elapsed = TimeSpan.FromSeconds(s.Elapsed);
                var eta = s.Totals != null && s.Totals.EtaSeconds.HasValue
                    ? TimeSpan.FromSeconds(s.Totals.EtaSeconds.Value)
                    : TimeSpan.Zero;
                Console.Write("\r" + doneRows.ToString("N0", CultureInfo.GetCultureInfo("en-US")) + " / " +
                    total.ToString("N0", CultureInfo.GetCultureInfo("en-US")) + " 행 · " +
                    pct.ToString("F0", CultureInfo.InvariantCulture) + "% · " +
                    rate.ToString("N0", CultureInfo.InvariantCulture) + "행/초 · 경과 " +
                    elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) + " · 남은 " +
                    eta.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
            };
            client.Log += e => Console.WriteLine("\n[" + e.Tag + "] " + e.Text);
            client.Ended += (_, __) => done.TrySetResult(true);
            await done.Task.ConfigureAwait(false);
            Console.WriteLine();
        }

        private static string PasswordFor(ConnectionProfile profile, ConnectionProfile src, ConnectionProfile tgt, string passwordSrc, string passwordTgt)
        {
            if (profile != null && src != null && string.Equals(profile.Id, src.Id, StringComparison.Ordinal) && passwordSrc != null) return passwordSrc;
            if (profile != null && tgt != null && string.Equals(profile.Id, tgt.Id, StringComparison.Ordinal) && passwordTgt != null) return passwordTgt;
            return profile != null && profile.SavePassword ? "prompt-needed" : null;
        }

        private static SchemaMetadata EmptyMeta(string schema)
        {
            return new SchemaMetadata { Schema = schema ?? "", Tables = new List<TableMetadata>() };
        }

        private static MigrationSettings SeedItSettings()
        {
            var settings = MigrationSettingsStore.CreateDefaultSettings();
            var src = new ConnectionProfile
            {
                Id = "it-src", Name = "IT SRC", Kind = "oracle", Host = "localhost", Port = 1521, Service = "xe",
                User = "MIG_IT_SRC", SavePassword = false
            };
            var tgt = new ConnectionProfile
            {
                Id = "it-tgt", Name = "IT TGT", Kind = "oracle", Host = "localhost", Port = 1521, Service = "xe",
                User = "MIG_IT_TGT", SavePassword = false
            };
            settings.Connections = new List<ConnectionProfile> { src, tgt };
            return settings;
        }

        private static void CopyAgent(string from, string to)
        {
            if (!Directory.Exists(from))
            {
                return;
            }

            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            }
        }

        private static void Usage()
        {
            Console.Error.WriteLine("AgentCli run --job <작업.json> [--settings <settings.json>|--seed-it] --mode DRY|EXECUTE|RESUME [--data dir] [--password-src pw --password-tgt pw]");
            Console.Error.WriteLine("AgentCli attach --data dir [--run RUN_ID]");
            Console.Error.WriteLine("AgentCli list --data dir");
            Console.Error.WriteLine("AgentCli pause|resume|stop --data dir --run RUN_ID");
        }
    }
}
