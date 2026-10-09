using System.Collections.Generic;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Validation;
using MigrationStudio.Logic;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class RunLogicTests
    {
        [Fact]
        public void Mode_description_mentions_watermark_only_for_incremental_strategy()
        {
            Assert.Contains("워터마크", RunLogic.ModeDescription("EXECUTE", true));
            Assert.Contains("워터마크", RunLogic.ModeDescription("DRY", true));
            Assert.DoesNotContain("워터마크", RunLogic.ModeDescription("EXECUTE", false));
            Assert.Equal(RunLogic.ModeDescription("RESUME"), RunLogic.ModeDescription("RESUME", true));
        }

        [Fact]
        public void Controls_follow_state()
        {
            var idle = RunLogic.Controls(RunStates.Idle, true);
            Assert.True(idle.CanStart);
            Assert.False(idle.CanPause);
            Assert.False(idle.CanStop);
            Assert.True(idle.CanChangeMode);
            var running = RunLogic.Controls(RunStates.Running, true);
            Assert.False(running.CanStart);
            Assert.True(running.CanPause);
            Assert.True(running.CanStop);
            Assert.False(running.CanChangeMode);
            var paused = RunLogic.Controls(RunStates.Paused, true);
            Assert.True(paused.CanResume);
            Assert.True(paused.CanStop);
            Assert.False(paused.CanPause);
            Assert.True(RunLogic.Controls(RunStates.Done, true).CanStart);
            Assert.False(RunLogic.Controls(RunStates.Idle, false).CanStart);
        }

        [Fact]
        public void State_texts_and_pill_kinds()
        {
            Assert.Equal("실행 중", RunLogic.StateText(RunStates.Running, false));
            Assert.Equal("Dry Run · 완료", RunLogic.StateText(RunStates.Done, true));
            Assert.Equal("대기", RunLogic.StateText(RunStates.Idle, true));
            Assert.Equal("run", RunLogic.PillKind(RunStates.Running));
            Assert.Equal("ok", RunLogic.PillKind(RunStates.Done));
            Assert.Equal("err", RunLogic.PillKind(RunStates.Failed));
            Assert.Equal("warn", RunLogic.PillKind(RunStates.Stopped));
            Assert.Equal("", RunLogic.PillKind(RunStates.Idle));
        }

        [Fact]
        public void Number_formats()
        {
            Assert.Equal("1,240,325", RunLogic.N(1240325));
            Assert.Equal("38,138", RunLogic.Rate(38137.6));
            Assert.Equal("—", RunLogic.Rate(0));
            Assert.Equal("00:03:45", RunLogic.Duration(225));
            Assert.Equal("01:01:01", RunLogic.Duration(3661));
            Assert.Equal("1.24M", RunLogic.Short(1240325));
            Assert.Equal("48.2M", RunLogic.Short(48210000));
            Assert.Equal("48K", RunLogic.Short(48210));
            Assert.Equal("6", RunLogic.Short(6));
            Assert.Equal(50, RunLogic.Pct(5, 10));
            Assert.Equal(0, RunLogic.Pct(5, 0));
        }

        [Fact]
        public void Log_classes_and_filter()
        {
            Assert.Equal("warn", RunLogic.LogClass("PAUSE"));
            Assert.Equal("error", RunLogic.LogClass("STOP"));
            Assert.Equal("info", RunLogic.LogClass("DRY"));
            Assert.True(RunLogic.LogVisible("WARN", "warn"));
            Assert.False(RunLogic.LogVisible("INFO", "warn"));
            Assert.True(RunLogic.LogVisible("ERROR", "all"));
        }

        [Fact]
        public void Checkpoint_where_quotes_non_numeric_values()
        {
            Assert.Equal("WHERE CUSTOMER_ID > 850000\nORDER BY CUSTOMER_ID", RunLogic.CheckpointWhere("CUSTOMER_ID", "850000"));
            Assert.Equal("WHERE ORDER_NO > 'O2025'\nORDER BY ORDER_NO", RunLogic.CheckpointWhere("ORDER_NO", "O2025"));
            Assert.Equal("WHERE X > 'a''b'\nORDER BY X", RunLogic.CheckpointWhere("X", "a'b"));
        }

        private static StartInputs Inputs(string mode = "EXECUTE")
        {
            return new StartInputs { HasMetadata = true, SelectedCount = 2, Mode = mode, HasValidation = true, SelectedWithCheckpoint = 1, Gate = new GateResult() };
        }

        [Fact]
        public void Start_gates_in_order()
        {
            var i = Inputs();
            i.HasMetadata = false;
            Assert.Equal(StartGate.NeedMetadata, RunLogic.NextStartGate(i));
            i = Inputs();
            i.SelectedCount = 0;
            Assert.Equal(StartGate.NoSelection, RunLogic.NextStartGate(i));
            i = Inputs("RESUME");
            i.SelectedWithCheckpoint = 0;
            Assert.Equal(StartGate.NoCheckpointToResume, RunLogic.NextStartGate(i));
            i = Inputs();
            i.HasValidation = false;
            Assert.Equal(StartGate.AskValidateFirst, RunLogic.NextStartGate(i));
            i.SkipValidateAsk = true;
            Assert.Equal(StartGate.Proceed, RunLogic.NextStartGate(i));
            i = Inputs();
            i.Gate = new GateResult { Blocked = true, Errors = 1 };
            Assert.Equal(StartGate.Blocked, RunLogic.NextStartGate(i));
            i = Inputs();
            i.ValidationStale = true;
            Assert.Equal(StartGate.ConfirmStale, RunLogic.NextStartGate(i));
            i.StaleAccepted = true;
            Assert.Equal(StartGate.Proceed, RunLogic.NextStartGate(i));
            i = Inputs();
            i.DestructiveLabels.Add("TRUNCATE + INSERT  X.Y");
            Assert.Equal(StartGate.ConfirmDestructive, RunLogic.NextStartGate(i));
            i.DestructiveAccepted = true;
            Assert.Equal(StartGate.Proceed, RunLogic.NextStartGate(i));
        }

        [Fact]
        public void Dry_run_skips_validation_gates_but_not_selection()
        {
            var i = Inputs("DRY");
            i.HasValidation = false;
            i.Gate = new GateResult { Blocked = true, Errors = 3 };
            i.DestructiveLabels.Add("x");
            Assert.Equal(StartGate.Proceed, RunLogic.NextStartGate(i));
            i.SelectedCount = 0;
            Assert.Equal(StartGate.NoSelection, RunLogic.NextStartGate(i));
        }

        [Fact]
        public void Step_summary_texts()
        {
            Assert.Equal("대기", RunLogic.StepSummary(null).Summary);
            Assert.Equal("재개 가능 · 68%", RunLogic.StepSummary(new RunSummaryInput { State = RunStates.Idle, ResumablePct = 68 }).Summary);
            var running = RunLogic.StepSummary(new RunSummaryInput { State = RunStates.Running, Pct = 68.4 });
            Assert.Equal("busy", running.State);
            Assert.Equal("실행 중 68%", running.Summary);
            Assert.Equal("Dry Run 완료 · 00:00:33", RunLogic.StepSummary(new RunSummaryInput { State = RunStates.Done, Dry = true, Elapsed = 33 }).Summary);
            Assert.Equal("중지 · 재개 가능 11%", RunLogic.StepSummary(new RunSummaryInput { State = RunStates.Stopped, Pct = 11.2 }).Summary);
            Assert.Equal("error", RunLogic.StepSummary(new RunSummaryInput { State = RunStates.Failed, Pct = 5 }).State);
        }

        [Fact]
        public void Status_bar_text()
        {
            Assert.Equal("대기", RunLogic.StatusBarText(RunStates.Idle, false, 0, 0, 0));
            Assert.Equal("실행 중 · 850,000 / 1,240,325 행 (68%)", RunLogic.StatusBarText(RunStates.Running, false, 850000, 1240325, 68.5));
        }

        [Fact]
        public void Task_row_model()
        {
            var t = new TaskSnapshot { Key = "k", Label = "A → B", Status = "run", Total = 1000, Written = 250, Inserted = 200, Updated = 50, Rejected = 3, Checkpoint = "ID = 250" };
            var row = RunLogic.TaskRow(1, t, 0);
            Assert.Equal("실행 중", row.StatusText);
            Assert.Equal(25, row.Percent);
            Assert.Equal("250 / 1,000", row.RowsText);
            Assert.True(row.HasRejects);
            Assert.True(row.Running);
            var resumed = RunLogic.TaskRow(1, new TaskSnapshot { Status = "wait", Total = 500, Written = 0 }, 500);
            Assert.Equal(50, resumed.Percent);
        }

        [Fact]
        public void Pipeline_stage_texts()
        {
            var snap = new RunSnapshot { Mode = "EXECUTE", Pipeline = new PipelineSnapshot { BufferPercent = 60, WritingWorkers = 2, ReadRowsPerSecond = 37457 } };
            snap.Tasks.Add(new TaskSnapshot { Key = "k", Label = "SRC_CUSTOMER → TB_MEMBER", Status = "run", Read = 1080000, Pending = 5000, Commits = 108, Checkpoint = "CUSTOMER_ID = 1080000" });
            var stages = RunLogic.Pipeline(snap, 5000, 10000, 4, "INSERT + UPDATE");
            Assert.Equal(6, stages.Count);
            Assert.Equal("37,457 행/초", stages[0].Metric);
            Assert.Equal("Fetch 5,000 × 216회", stages[0].Sub);
            Assert.Equal("→ TB_MEMBER", stages[2].Metric);
            Assert.Equal("INSERT + UPDATE", stages[3].Metric);
            Assert.Equal("배열 10,000 × 2", stages[3].Sub);
            Assert.Equal(50, stages[3].Percent);
            Assert.Equal("108회", stages[4].Metric);
            Assert.Equal("CUSTOMER_ID = 1080000", stages[5].Metric);
            var idle = RunLogic.Pipeline(null, 5000, 10000, 4, "");
            Assert.Equal("대기", idle[0].Metric);
        }
    }
}
