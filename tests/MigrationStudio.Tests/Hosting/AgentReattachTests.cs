using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Hosting;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using Xunit;

namespace MigrationStudio.Tests.Hosting
{
    /// <summary>
    /// 창을 닫았다 다시 열 때의 "다시 붙기" 회귀 — 실제 Folderss에서 첫 클라이언트가 떠난 뒤 두 번째 클라이언트가
    /// "[Connect] 에이전트 파이프 연결 시간 초과"로 붙지 못했다(끝난 뒤 에이전트가 파이프를 열지 않고 잠듦).
    /// 메모리 어댑터로 진짜 에이전트 프로세스를 띄워 확인한다.
    /// </summary>
    public sealed class AgentReattachTests
    {
        [Fact]
        public async Task Second_client_can_attach_after_first_disconnects_and_sees_final_snapshot()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var agentExe = LocateAgentExe();
            if (agentExe == null)
            {
                return; // 에이전트를 아직 빌드하지 않은 환경
            }

            var data = Path.Combine(Path.GetTempPath(), "mig-agent-reattach-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            var runId = "R-REATTACH-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Environment.SetEnvironmentVariable("MIGRATION_AGENT_TEST_ADAPTER", "memory");
            Environment.SetEnvironmentVariable("MIGRATION_AGENT_START_WAIT_SECONDS", "30");
            var install = new AgentInstall { Directory = Path.GetDirectoryName(agentExe), ExePath = agentExe, Version = "test" };
            var first = AgentClient.Launch(install, runId, data, Process.GetCurrentProcess().Id, false, null);
            var process = Process.GetProcessById(first.Pid);
            AgentClient second = null;
            try
            {
                await first.ConnectAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
                var ended = new TaskCompletionSource<string>();
                first.Ended += (_, reason) => ended.TrySetResult(reason);
                await first.StartAsync(Spec(runId, data), CancellationToken.None);
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(60));

                // 창이 닫히듯 첫 클라이언트는 shutdown 없이 그냥 떠난다
                first.Dispose();
                Assert.False(process.HasExited, "클라이언트가 떠나도 에이전트는 계속 살아 있어야 합니다");

                var record = AgentRunFiles.Read(AgentRunFiles.RunFilePath(data, runId));
                Assert.Equal(runId, record.RunId);
                second = AgentClient.Attach(record, data);
                var snapshot = new TaskCompletionSource<RunSnapshot>();
                second.Snapshot += s => snapshot.TrySetResult(s);
                await second.ConnectAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                await second.AttachAsync(200, CancellationToken.None);
                var snap = await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(runId, snap.RunId);
                var logPath = AgentRunFiles.LogFilePath(data, runId);
                var log = File.Exists(logPath) ? ReadShared(logPath) : "(로그 없음)";
                Assert.True(snap.State == "done", "다시 붙어 받은 최종 스냅숏 상태가 done이어야 합니다: " + snap.State + Environment.NewLine + log);

                await second.ShutdownAsync();
                process.WaitForExit(30000);
                Assert.True(process.HasExited, "shutdown 뒤 에이전트가 끝나야 합니다");
            }
            finally
            {
                try { first.Dispose(); } catch { }
                try { if (second != null) second.Dispose(); } catch { }
                try { if (!process.HasExited) process.Kill(true); process.WaitForExit(5000); } catch { }
                try { if (Directory.Exists(data)) Directory.Delete(data, true); } catch { }
            }
        }

        /// <summary>에이전트가 쓰고 있는 로그를 읽는다(공유 열기).</summary>
        private static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        private static RunSpec Spec(string runId, string data)
        {
            var job = new MigrationJob
            {
                JobName = "MEM",
                Strategy = new MigrationStrategy { CommitSize = 5, FetchSize = 5, Workers = 1 },
                Mappings = new System.Collections.Generic.List<Mapping>
                {
                    new Mapping
                    {
                        Id = "M1", Source = "SRC", Target = "TGT", Mode = WriteModes.InsertOnly, CheckpointColumn = "ID",
                        Columns = new System.Collections.Generic.List<ColumnMapping>
                        {
                            new ColumnMapping { Source = "ID", Target = "ID" },
                            new ColumnMapping { Source = "NAME", Target = "NAME" }
                        }
                    }
                }
            };
            return new RunSpec
            {
                RunId = runId,
                RunMode = "EXECUTE",
                Job = job,
                DataDirectory = data,
                CheckpointStore = "LOCAL",
                Source = new EndpointSpec { Schema = "S", Connection = new MigrationStudio.Core.Adapters.ConnectionTarget { Password = "x" } },
                Target = new EndpointSpec { Schema = "T", Connection = new MigrationStudio.Core.Adapters.ConnectionTarget { Password = "x" } },
                Plan = new System.Collections.Generic.List<PlanItem>
                {
                    new PlanItem
                    {
                        Key = "M1",
                        Mapping = job.Mappings[0],
                        ScopeTotal = 10,
                        Ranges = new System.Collections.Generic.List<KeyRange> { new KeyRange { Rows = 10 } },
                        WriteColumns = new System.Collections.Generic.List<WriteColumn> { new WriteColumn { Name = "ID" }, new WriteColumn { Name = "NAME" } },
                        SourceMetadata = new TableMetadata { Name = "SRC", Columns = new System.Collections.Generic.List<ColumnMetadata>() },
                        TargetMetadata = new TableMetadata { Name = "TGT", Columns = new System.Collections.Generic.List<ColumnMetadata>() }
                    }
                }
            };
        }

        private static string LocateAgentExe()
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                var built = Path.Combine(dir, "src", "MigrationAgent", "bin", "Release", "net8.0", "win-x64", "MigrationAgent.exe");
                if (File.Exists(built))
                {
                    return built;
                }

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }
    }
}
