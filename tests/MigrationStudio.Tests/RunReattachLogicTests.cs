using System.Collections.Generic;
using MigrationStudio.Core.Hosting;
using MigrationStudio.Logic;
using Xunit;

namespace MigrationStudio.Tests
{
    /// <summary>창을 닫았다 다시 열 때 살아 있는 에이전트에 다시 붙는 선택 규칙.</summary>
    public sealed class RunReattachLogicTests
    {
        private static AgentRunInfo Run(string id, string job, string state)
        {
            return new AgentRunInfo { RunId = id, JobName = job, State = state, Pid = 1 };
        }

        [Fact]
        public void Finished_runs_are_not_candidates()
        {
            var alive = new List<AgentRunInfo> { Run("R1", "A", "done"), Run("R2", "A", "failed"), Run("R3", "B", "running"), Run("R4", "C", "paused") };
            var candidates = RunLogic.AliveForReattach(alive);
            Assert.Equal(new[] { "R3", "R4" }, candidates.ConvertAll(r => r.RunId));
        }

        [Fact]
        public void Notice_shows_last_sync_cycle_and_flags_stale_agents()
        {
            var now = new System.DateTime(2026, 10, 9, 12, 0, 0);
            var fresh = Run("R5", "SYNC_A", "running");
            fresh.Cycle = 12;
            fresh.LastCycleAt = now.AddMinutes(-5);
            var stale = Run("R6", "SYNC_B", "running");
            stale.Cycle = 3;
            stale.LastCycleAt = now.AddHours(-2);
            var text = RunLogic.ReattachNoticeText(new List<AgentRunInfo> { fresh, stale, Run("R7", "C", "running") }, now);
            Assert.Contains("SYNC_A · R5 (동기화 주기 12 · 마지막 5분 전)", text);
            Assert.Contains("SYNC_B · R6 (동기화 주기 3 · 마지막 2시간 전 — 멈췄을 수 있음, 붙어서 확인)", text);
            Assert.Contains("C · R7,", text + ",");
        }

        [Fact]
        public void Same_job_name_is_picked_automatically()
        {
            var alive = new List<AgentRunInfo> { Run("R3", "B", "running"), Run("R4", "A", "running") };
            Assert.Equal("R4", RunLogic.PickReattach(alive, "A").RunId);
        }

        [Fact]
        public void Different_job_is_not_picked_automatically()
        {
            var alive = new List<AgentRunInfo> { Run("R3", "B", "running") };
            Assert.Null(RunLogic.PickReattach(alive, "A"));
            Assert.Null(RunLogic.PickReattach(alive, null));
            Assert.Null(RunLogic.PickReattach(null, "A"));
        }

        [Fact]
        public void Reattach_notice_text_counts_runs()
        {
            var alive = new List<AgentRunInfo> { Run("R3", "B", "running"), Run("R4", "C", "paused") };
            var text = RunLogic.ReattachNoticeText(alive);
            Assert.Contains("2개", text);
            Assert.Contains("B", text);
        }
    }
}
