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
    public sealed class AgentIntegrationTests
    {
        [Fact]
        public async Task Agent_memory_run_completes()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var data = Path.Combine(Path.GetTempPath(), "mig-agent-it-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            var runId = "R-TEST-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var pipe = AgentPipeNames.ForRun(runId);
            var agentExe = LocateAgentExe();
            Assert.False(string.IsNullOrEmpty(agentExe), "MigrationAgent.exe publish 출력이 필요합니다.");

            Environment.SetEnvironmentVariable("MIGRATION_AGENT_TEST_ADAPTER", "memory");
            Environment.SetEnvironmentVariable("MIGRATION_AGENT_START_WAIT_SECONDS", "30");
            var install = new AgentInstall { Directory = Path.GetDirectoryName(agentExe), ExePath = agentExe, Version = "test" };
            var client = AgentClient.Launch(install, runId, data, Process.GetCurrentProcess().Id, false, null);
            var process = Process.GetProcessById(client.Pid);
            try
            {
                await client.ConnectAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
                var job = MemoryJob();
                var spec = new RunSpec
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
                            WriteColumns = new System.Collections.Generic.List<WriteColumn>
                            {
                                new WriteColumn { Name = "ID" }, new WriteColumn { Name = "NAME" }
                            },
                            SourceMetadata = new TableMetadata { Name = "SRC", Columns = new System.Collections.Generic.List<ColumnMetadata>() },
                            TargetMetadata = new TableMetadata { Name = "TGT", Columns = new System.Collections.Generic.List<ColumnMetadata>() }
                        }
                    }
                };
                var ended = new TaskCompletionSource<string>();
                client.Ended += (_, reason) => ended.TrySetResult(reason);
                await client.StartAsync(spec, CancellationToken.None);
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
                // 설계(07 지시서 3.2-6): 에이전트는 끝난 뒤 클라이언트가 shutdown을 보내면 즉시 끝난다(안 보내면 --idle-exit 동안 기다림)
                await client.ShutdownAsync();
                process.WaitForExit(30000);
                // GetProcessById로 얻은 Process는 종료 코드를 읽을 수 없으므로 "shutdown 뒤 제때 끝났는가"만 확인한다
                Assert.True(process.HasExited, "shutdown 뒤 30초 안에 에이전트가 끝나야 합니다");
                Assert.True(File.Exists(AgentRunFiles.LogFilePath(data, runId)));
            }
            finally
            {
                try
                {
                    client.Dispose();
                }
                catch
                {
                }

                try
                {
                    if (!process.HasExited) process.Kill(true);
                    process.WaitForExit(5000);
                }
                catch
                {
                }

                try
                {
                    if (Directory.Exists(data)) Directory.Delete(data, true);
                }
                catch
                {
                }
            }
        }

        private static MigrationJob MemoryJob()
        {
            return new MigrationJob
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
        }

        private static string LocateAgentExe()
        {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                // 방금 빌드한 것을 우선한다(publish 폴더에는 옛 복사본이 남아 있을 수 있다)
                var built = Path.Combine(dir, "src", "MigrationAgent", "bin", "Release", "net8.0", "win-x64", "MigrationAgent.exe");
                if (File.Exists(built))
                {
                    return built;
                }

                var publish = Path.Combine(dir, "src", "MigrationAgent", "bin", "Release", "net8.0", "win-x64", "publish", "MigrationAgent.exe");
                if (File.Exists(publish))
                {
                    return publish;
                }

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }
    }
}
