using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Hosting;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Logic;

namespace MigrationStudio.Services
{
    internal sealed class RunService : IRunService
    {
        private readonly StudioState _state;
        private readonly ConnectionService _connections;

        public RunService(StudioState state, ConnectionService connections)
        {
            _state = state;
            _connections = connections;
        }

        public async Task<IAgentClient> StartAsync(RunRequest request, IProgress<string> stage, CancellationToken ct)
        {
            if (request == null || request.Job == null)
            {
                throw new LaunchFailedException(LaunchStage.Prepare, "실행할 작업이 없습니다.");
            }

            var dirs = new PluginDirs(AppServices.PluginDirectory, AppServices.DataDirectory);
            var adapter = AppServices.DatabaseAdapter ?? DatabaseAdapters.For("oracle");
            var launcher = new RunLauncher(dirs, _state.Settings, adapter);
            PasswordResolver passwords = (profile, reason) =>
            {
                var pwd = _connections.PasswordFor(profile, reason);
                if (pwd == null)
                {
                    throw new OperationCanceledException("비밀번호 입력이 취소되었습니다.");
                }

                return pwd;
            };
            var selected = request.Selected ?? new HashSet<string>(StringComparer.Ordinal);
            if (selected.Count == 0 && request.Job.Mappings != null)
            {
                foreach (var mapping in request.Job.Mappings)
                {
                    selected.Add(mapping.Id);
                }
            }

            var client = await launcher.StartAsync(
                request.Job,
                request.SourceMeta ?? _state.SourceMeta(),
                request.TargetMeta ?? _state.TargetMeta(),
                selected,
                request.Mode ?? "EXECUTE",
                passwords,
                stage,
                ct).ConfigureAwait(true);

            WatchAgentEnd(client);
            return client;
        }

        public Task<IAgentClient> AttachAsync(AgentRunInfo run, CancellationToken ct)
        {
            var dirs = new PluginDirs(AppServices.PluginDirectory, AppServices.DataDirectory);
            var adapter = AppServices.DatabaseAdapter ?? DatabaseAdapters.For("oracle");
            var launcher = new RunLauncher(dirs, _state.Settings, adapter);
            return AttachInternalAsync(launcher, run, ct);
        }

        public List<AgentRunInfo> ListAlive()
        {
            return AgentRuns.ListAlive(AppServices.DataDirectory);
        }

        public List<AgentRunInfo> ListRecent(int count)
        {
            return AgentRuns.ListRecent(AppServices.DataDirectory, count);
        }

        private static async Task<IAgentClient> AttachInternalAsync(RunLauncher launcher, AgentRunInfo run, CancellationToken ct)
        {
            var client = await launcher.AttachAsync(run, ct).ConfigureAwait(true);
            return client;
        }

        private static void WatchAgentEnd(AgentClient client)
        {
            if (client == null)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    var process = Process.GetProcessById(client.Pid);
                    process.WaitForExit();
                    client.ReleaseRunGuard();
                }
                catch
                {
                    client.ReleaseRunGuard();
                }
            });
        }
    }
}
