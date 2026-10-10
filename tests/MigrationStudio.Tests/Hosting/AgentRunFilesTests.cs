using System;
using MigrationStudio.Core.Hosting;
using Xunit;

namespace MigrationStudio.Tests.Hosting
{
    public sealed class AgentRunFilesTests
    {
        [Fact]
        public void Run_record_roundtrips_sync_cycle_fields_and_tolerates_their_absence()
        {
            var info = new AgentRunInfo
            {
                RunId = "R-1", Pid = 7, Pipe = "p", State = "running", JobName = "JOB", Message = "",
                StartedAt = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Local),
                Cycle = 4, LastCycleAt = new DateTime(2026, 10, 9, 12, 30, 0, DateTimeKind.Local)
            };
            var back = AgentRunFiles.Deserialize(AgentRunFiles.Serialize(info));
            Assert.Equal(4, back.Cycle);
            Assert.Equal(info.LastCycleAt, back.LastCycleAt);

            var old = AgentRunFiles.Deserialize("{\"runId\":\"R-0\",\"pid\":1,\"state\":\"done\"}");
            Assert.Equal(0, old.Cycle);
            Assert.Null(old.LastCycleAt);
        }

        [Fact]
        public void Cdc_strategy_runs_as_sync_except_dry_run()
        {
            var job = new MigrationStudio.Core.Model.MigrationJob();
            job.Strategy.Mode = MigrationStudio.Core.Model.ExecutionModes.Cdc;
            Assert.Equal("SYNC", RunLauncher.ResolveRunMode(job, "EXECUTE"));
            Assert.Equal("SYNC", RunLauncher.ResolveRunMode(job, "RESUME"));
            Assert.Equal("DRY", RunLauncher.ResolveRunMode(job, "DRY"));
            job.Strategy.Mode = MigrationStudio.Core.Model.ExecutionModes.Incremental;
            Assert.Equal("EXECUTE", RunLauncher.ResolveRunMode(job, "EXECUTE"));
        }
    }
}
