using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle.Engine;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Sql;

namespace MigrationStudio.Core.Hosting
{
    public sealed class RunLauncher
    {
        private readonly IPluginDirs _dirs;
        private readonly MigrationSettings _settings;
        private readonly IDatabaseAdapter _adapter;

        public RunLauncher(IPluginDirs dirs, MigrationSettings settings, IDatabaseAdapter adapter)
        {
            _dirs = dirs;
            _settings = settings;
            _adapter = adapter;
        }

        public async Task<AgentClient> StartAsync(MigrationJob job, SchemaMetadata sourceMeta, SchemaMetadata targetMeta,
            ISet<string> selectedIds, string runMode, PasswordResolver passwords, IProgress<string> stage, CancellationToken ct)
        {
            RunGuard guard = null;
            AgentClient client = null;
            var started = false;
            try
            {
                if (stage != null) stage.Report("준비 중…");
                var dataDirectory = _dirs.DataDirectory;
                var agentSettings = _settings != null ? _settings.Agent : new AgentSettings();
                guard = new RunGuard(dataDirectory, job != null ? job.JobName : "", agentSettings.MaxConcurrent);
                guard.Acquire();

                if (stage != null) stage.Report("에이전트 준비 중…");
                var install = AgentLocator.Prepare(_dirs.PluginDirectory, dataDirectory);
                AgentRuns.PruneOld(dataDirectory, agentSettings.LogDays);

                if (stage != null) stage.Report("계획 중…");
                var spec = await BuildRunSpecAsync(job, sourceMeta, targetMeta, selectedIds, runMode, passwords, dataDirectory, agentSettings, ct).ConfigureAwait(false);

                if (stage != null) stage.Report("에이전트 시작 중…");
                var killOnHost = string.Equals(agentSettings.OnHostExit, "STOP", StringComparison.OrdinalIgnoreCase);
                client = AgentClient.Launch(install, spec.RunId, dataDirectory, AgentProcessHelper.CurrentProcessId(), killOnHost, null);

                if (stage != null) stage.Report("연결 중…");
                await client.ConnectAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

                if (stage != null) stage.Report("실행 시작 중…");
                await client.StartAsync(spec, ct).ConfigureAwait(false);
                started = true;
                await client.WaitFirstSnapshotAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                client.BindRunGuard(guard);
                guard = null;
                return client;
            }
            catch (Exception ex) when (!(ex is LaunchFailedException) && !(ex is AlreadyRunningException) && !(ex is ConcurrencyLimitException) && !(ex is OperationCanceledException))
            {
                throw WrapStage(ex);
            }
            catch
            {
                await CleanupAsync(client, started).ConfigureAwait(false);
                throw;
            }
            finally
            {
                guard?.Dispose();
            }
        }

        public async Task<AgentClient> AttachAsync(AgentRunInfo record, CancellationToken ct)
        {
            var client = AgentClient.Attach(record, _dirs.DataDirectory);
            await client.ConnectAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            await client.AttachAsync(2000, ct).ConfigureAwait(false);
            return client;
        }

        private async Task CleanupAsync(AgentClient client, bool started)
        {
            if (client == null)
            {
                return;
            }

            try
            {
                if (started)
                {
                    client.Stop();
                    await client.ShutdownAsync().ConfigureAwait(false);
                }
                else if (client.Pid > 0)
                {
                    try
                    {
                        var process = System.Diagnostics.Process.GetProcessById(client.Pid);
                        if (!process.HasExited) process.Kill();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            finally
            {
                client.Dispose();
            }
        }

        private static Exception WrapStage(Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                return ex;
            }

            if (ex is AgentMissingException || ex is AgentLaunchException)
            {
                return new LaunchFailedException(LaunchStage.Launch, ex.Message, ex);
            }

            if (ex is AgentExitedException || ex is LaunchFailedException)
            {
                return ex;
            }

            if (ex is AgentStartException)
            {
                return new LaunchFailedException(LaunchStage.Start, ex.Message, ex);
            }

            if (ex is UnsupportedColumnTypeException || ex is ControlStoreUnavailableException)
            {
                return new LaunchFailedException(LaunchStage.Plan, ex.Message, ex);
            }

            return new LaunchFailedException(LaunchStage.Plan, ex.Message, ex);
        }

        private async Task<RunSpec> BuildRunSpecAsync(MigrationJob job, SchemaMetadata sourceMeta, SchemaMetadata targetMeta,
            ISet<string> selectedIds, string runMode, PasswordResolver passwords, string dataDirectory, AgentSettings agentSettings, CancellationToken ct)
        {
            var sourceProfile = FindProfile(job.Source != null ? job.Source.ProfileId : null);
            var targetProfile = FindProfile(job.Target != null ? job.Target.ProfileId : null);
            if (sourceProfile == null || targetProfile == null)
            {
                throw new LaunchFailedException(LaunchStage.Plan, "마이그레이션 설정에서 원본·대상 접속을 고르세요.");
            }

            var sourcePassword = passwords != null ? passwords(sourceProfile, "이관 실행에 필요합니다.") : null;
            var targetPassword = passwords != null ? passwords(targetProfile, "이관 실행에 필요합니다.") : null;
            if (sourcePassword == null || targetPassword == null)
            {
                throw new OperationCanceledException("비밀번호 입력이 취소되었습니다.");
            }

            var sourceEndpoint = Endpoint(sourceProfile, sourcePassword, job.Source != null ? job.Source.Schema : null, "source");
            var targetEndpoint = Endpoint(targetProfile, targetPassword, job.Target != null ? job.Target.Schema : null, "target");

            var storeMode = _settings != null && _settings.Defaults != null ? _settings.Defaults.CheckpointStore : "AUTO";
            if (string.IsNullOrWhiteSpace(storeMode))
            {
                storeMode = "AUTO";
            }
            if (string.Equals(storeMode, "AUTO", StringComparison.OrdinalIgnoreCase))
            {
                var check = await _adapter.CheckControlStoreAsync(targetEndpoint.Connection, targetEndpoint.Schema, "MIG_", "AUTO", ct).ConfigureAwait(false);
                storeMode = check != null && string.Equals(check.Resolved, "TARGET", StringComparison.OrdinalIgnoreCase)
                    ? "TARGET"
                    : "LOCAL";
            }

            var metadata = BuildMetadata(job, sourceMeta, targetMeta);
            ICheckpointStore checkpointStore = string.Equals(storeMode, "LOCAL", StringComparison.OrdinalIgnoreCase)
                ? new LocalCheckpointStore(System.IO.Path.Combine(dataDirectory, "checkpoints"))
                : (ICheckpointStore)new OracleCheckpointStore(targetEndpoint, "MIG_");

            var probe = new OracleSourceProbe(sourceEndpoint, targetEndpoint);
            var plan = await RunPlanner.BuildAsync(job, _settings, metadata, selectedIds, runMode, probe, checkpointStore, ct).ConfigureAwait(false);

            var runId = RunIds.New(DateTime.Now);
            return new RunSpec
            {
                RunId = runId,
                RunMode = runMode,
                Job = job,
                Source = sourceEndpoint,
                Target = targetEndpoint,
                CheckpointStore = storeMode,
                ControlPrefix = "MIG_",
                DataDirectory = dataDirectory,
                Plan = plan,
                OnHostExit = agentSettings != null ? agentSettings.OnHostExit : "CONTINUE"
            };
        }

        private ConnectionProfile FindProfile(string id)
        {
            if (_settings == null || string.IsNullOrEmpty(id))
            {
                return null;
            }

            return MigrationSettingsStore.Find(_settings, id);
        }

        private static EndpointSpec Endpoint(ConnectionProfile profile, string password, string schema, string color)
        {
            return new EndpointSpec
            {
                Connection = ConnectionTarget.From(profile, password),
                Schema = schema,
                ProfileName = profile.Name,
                Color = color
            };
        }

        private static RunMetadata BuildMetadata(MigrationJob job, SchemaMetadata sourceMeta, SchemaMetadata targetMeta)
        {
            var metadata = new RunMetadata { Source = sourceMeta, Target = targetMeta };
            if (job == null || job.Mappings == null)
            {
                return metadata;
            }

            foreach (var mapping in job.Mappings.Where(m => m.IsSql))
            {
                var info = SqlSourceAnalyzer.Analyze(mapping, sourceMeta);
                if (info != null && info.Table != null)
                {
                    metadata.SqlSources[mapping.Id] = info.Table;
                }
            }

            return metadata;
        }
    }
}
