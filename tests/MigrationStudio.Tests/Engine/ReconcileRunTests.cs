using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters.Oracle.Engine;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MigrationStudio.Tests.Engine.Fakes;
using Xunit;
using World = MigrationStudio.Tests.Engine.IncrementalRunTests.World;

namespace MigrationStudio.Tests.Engine
{
    /// <summary>
    /// 삭제 대조(증분·CDC): 원본에 없는 대상 행을 숨기지 않고 보이고, MARK 매핑은 승인된 뒤에만 표시 열에 값을 넣는다.
    /// 표시는 되돌릴 수 있다 — 원본에 다시 나타나면 NULL로.
    /// </summary>
    public sealed class ReconcileRunTests
    {
        private const string Approved = "2026-10-09 10:00:00";

        [Fact]
        public async Task Mark_waits_for_approval_then_marks_and_unmarks_reappeared_rows()
        {
            var world = MarkWorld(10, null);
            var first = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            Assert.Equal(0, first.Listener.Final.Tasks[0].Reconcile.MarkCandidates);

            world.Source.Rows.RemoveAll(r => (decimal)r[0] == 3m || (decimal)r[0] == 7m);

            // 승인 전: 대조만 하고 표시하지 않는다
            var waiting = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            var pending = waiting.Listener.Final.Tasks[0].Reconcile;
            Assert.Equal(2, pending.MarkCandidates);
            Assert.Equal(0, pending.Marked);
            Assert.False(pending.Approved);
            Assert.Equal(new[] { "3", "7" }, pending.Samples.OrderBy(x => x));
            Assert.All(world.Target.Rows, r => Assert.Null(r[3]));
            Assert.Contains(waiting.Listener.Logs, l => l.Tag == "WARN" && l.Text.Contains("승인 전이라 표시하지 않음 · 대기 2행", StringComparison.Ordinal));

            // Dry Run: 표시 예정만
            var dry = await world.RunAsync(ExecutionModes.Incremental, "DRY");
            Assert.Contains(dry.Listener.Logs, l => l.Tag == "DRY" && l.Text.Contains("표시 예정 2행", StringComparison.Ordinal));
            Assert.All(world.Target.Rows, r => Assert.Null(r[3]));

            // 승인 뒤: 표시
            world.ConfigureMapping = Mark(Approved);
            var marked = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            Assert.Equal(2, marked.Listener.Final.Tasks[0].Reconcile.Marked);
            Assert.Equal(10, world.Target.Rows.Count);
            Assert.Equal(new[] { 3m, 7m }, world.Target.Rows.Where(r => (string)r[3] == "Y").Select(r => (decimal)r[0]).OrderBy(x => x));
            Assert.Contains(marked.Listener.Logs, l => l.Tag == "DELETE" && l.Text.Contains("삭제 표시 2행", StringComparison.Ordinal));

            // 다시 돌려도 이미 표시된 행은 그대로
            var again = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            Assert.Equal(0, again.Listener.Final.Tasks[0].Reconcile.Marked);
            Assert.Equal(2, again.Listener.Final.Tasks[0].Reconcile.AlreadyMarked);

            // 3이 원본에 다시 나타남: 이관이 행을 갱신해도 표시는 남아 있다가(쓰기 열 아님) 대조가 지운다
            world.Source.Rows.Add(IncrementalRunTests.RowAt(3, 20));
            world.SourceNow = IncrementalRunTests.Base.AddMinutes(20);
            var back = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            Assert.Equal(1, back.Listener.Final.Tasks[0].Reconcile.Unmarked);
            Assert.Null(world.Target.Rows.First(r => (decimal)r[0] == 3m)[3]);
            Assert.Equal("Y", world.Target.Rows.First(r => (decimal)r[0] == 7m)[3]);
        }

        [Fact]
        public async Task Marking_stops_above_the_ratio_cap_and_when_source_is_empty()
        {
            var world = MarkWorld(30, Approved);
            await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            world.Source.Rows.RemoveAll(r => (decimal)r[0] <= 20m);

            var capped = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            var r1 = capped.Listener.Final.Tasks[0].Reconcile;
            Assert.Equal(20, r1.MarkCandidates);
            Assert.Equal(0, r1.Marked);
            Assert.Contains("상한 10", r1.Blocked);
            Assert.All(world.Target.Rows, r => Assert.Null(r[3]));

            world.Reconciler.EmptySource = true;
            var empty = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE", s => s.DeleteMaxRatio = 1.0, null);
            Assert.Contains("원본 키가 0개", empty.Listener.Final.Tasks[0].Reconcile.Blocked);
            Assert.All(world.Target.Rows, r => Assert.Null(r[3]));

            world.Reconciler.EmptySource = false;
            var allowed = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE", s => s.DeleteMaxRatio = 1.0, null);
            Assert.Equal(20, allowed.Listener.Final.Tasks[0].Reconcile.Marked);
        }

        [Fact]
        public async Task Sync_counts_unfollowed_mappings_at_reconcile_interval_and_shows_extra_target_rows()
        {
            var world = new World(WriteModes.Merge, 10);
            world.Reconciler = new MemoryReconcileStore(world.Source, world.Target);
            world.Target.Rows.Add(new object[] { 99m, "ORPHAN", IncrementalRunTests.Base });

            var run = await world.RunAsync(ExecutionModes.Cdc, RunModes.Sync, s => s.ReconcileIntervalMinutes = 2, e =>
                e.CycleDelay = (span, ct) =>
                {
                    world.Clock.Advance(span);
                    if (e.Snapshot().Sync.Cycle >= 3) e.Stop();
                    return Task.CompletedTask;
                });

            Assert.Equal("stopped", run.Engine.State);
            // 주기 1(0분)과 주기 3(2분)에서만 센다 — 원본·대상 한 번씩
            Assert.Equal(4, world.Reconciler.CountCalls);
            var r = run.Listener.Final.Tasks[0].Reconcile;
            Assert.Equal(DeleteModes.None, r.Mode);
            Assert.Equal(10, r.SourceRows);
            Assert.Equal(11, r.TargetRows);
            Assert.Contains(run.Listener.Logs, l => l.Tag == "INFO" && l.Text.Contains("대상이 원본보다 1행 많음", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Sync_skips_sql_source_mappings_when_counting()
        {
            var world = new World(WriteModes.Merge, 3);
            world.Reconciler = new MemoryReconcileStore(world.Source, world.Target);
            world.ConfigureMapping = m => { m.SourceType = SourceTypes.Sql; m.Sql = "SELECT ID, NAME, UPD_AT FROM SRC"; };

            var run = await world.RunAsync(ExecutionModes.Cdc, RunModes.Sync, null, e => e.CycleDelay = (span, ct) => { e.Stop(); return Task.CompletedTask; });

            Assert.Equal(0, world.Reconciler.CountCalls);
            Assert.Null(run.Listener.Final.Tasks[0].Reconcile);
            Assert.DoesNotContain(run.Listener.Logs, l => l.Text.Contains("삭제 대조 실패", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Reconcile_failure_is_a_warning_and_the_run_still_finishes()
        {
            var world = MarkWorld(10, Approved);
            world.Reconciler.FailOpen = 1;

            var run = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");

            Assert.Equal("done", run.Engine.State);
            Assert.Equal(10, world.Target.Rows.Count);
            Assert.Contains("ORA-00942", run.Listener.Final.Tasks[0].Reconcile.Error);
            Assert.Contains(run.Listener.Logs, l => l.Tag == "WARN" && l.Text.Contains("삭제 대조 실패", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Mark_mapping_without_reconciler_warns_once_and_full_mode_never_reconciles()
        {
            var world = MarkWorld(5, Approved);
            world.Reconciler = null;
            var run = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            Assert.Single(run.Listener.Logs, l => l.Text.Contains("삭제 대조를 할 수 없는", StringComparison.Ordinal));

            var full = MarkWorld(5, Approved);
            full.Source.Rows.RemoveAt(0);
            var fullRun = await full.RunAsync(ExecutionModes.Full, "EXECUTE");
            Assert.Null(fullRun.Listener.Final.Tasks[0].Reconcile);
            Assert.Equal(0, full.Reconciler.MarkCalls);
        }

        [Fact]
        public void Oracle_reconcile_sql_normalizes_keys_by_target_type_and_checks_rowid_with_key()
        {
            var item = Item();
            var source = OracleReconcileStore.SourceKeysSql(item, "SRC_APP");
            Assert.Contains("TO_CHAR(CAST(CUSTOMER_ID AS NUMBER), 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''')", source);
            Assert.Contains("RTRIM(TO_CHAR(BRANCH_CD))", source);
            Assert.Contains(" || CHR(31) || ", source);
            Assert.Contains("ORA_HASH(UTL_I18N.STRING_TO_RAW(K, 'AL32UTF8')) AS H", source);
            Assert.Contains("FROM SRC_APP.SRC_CUSTOMER WHERE (STATUS_CD <> 'X')", source);
            Assert.EndsWith("WHERE K IS NOT NULL ORDER BY H", source);

            var target = OracleReconcileStore.TargetKeysSql(item, "TGT_APP");
            Assert.Contains("TO_CHAR(MEMBER_ID, 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''') || CHR(31) || RTRIM(BRANCH_CD) AS K", target);
            Assert.Contains("CASE WHEN DEL_YN IS NULL THEN 0 ELSE 1 END AS MK", target);
            Assert.Contains("CAST(ROWID AS VARCHAR2(4000)) AS RID", target);

            var mark = OracleReconcileStore.MarkSql(item, "TGT_APP", true);
            Assert.Equal("UPDATE TGT_APP.TB_MEMBER SET DEL_YN = :MV WHERE ROWID = :RID AND DEL_YN IS NULL AND " +
                "TO_CHAR(MEMBER_ID, 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''') || CHR(31) || RTRIM(BRANCH_CD) = :K", mark);
            Assert.Contains("SET DEL_YN = NULL WHERE ROWID = :RID AND DEL_YN IS NOT NULL", OracleReconcileStore.MarkSql(item, "TGT_APP", false));
            item.Mapping.MarkValue = "sysdate";
            Assert.Contains("SET DEL_YN = SYSDATE", OracleReconcileStore.MarkSql(item, "TGT_APP", true));

            Assert.Equal("TO_CHAR(CAST(X AS DATE), 'YYYYMMDDHH24MISS')", OracleReconcileStore.Normalize("X", "DATE", false));
            Assert.Equal("TO_CHAR(X, 'YYYYMMDDHH24MISSFF6')", OracleReconcileStore.Normalize("X", "TIMESTAMP(6)", true));
            Assert.Equal("TO_CHAR(X)", OracleReconcileStore.Normalize("X", "VARCHAR2(20)", false));
            Assert.Throws<InvalidOperationException>(() => OracleReconcileStore.Normalize("X", "TIMESTAMP WITH TIME ZONE", true));
            Assert.Equal("SELECT COUNT(*) FROM TGT_APP.TB_MEMBER WHERE DEL_YN IS NULL", OracleReconcileStore.TargetCountSql(item, "TGT_APP"));
        }

        private static World MarkWorld(int rows, string approvedAt)
        {
            var world = new World(WriteModes.Merge, rows, "TARGET", true);
            world.SourceNow = IncrementalRunTests.Base.AddMinutes(rows);
            world.Reconciler = new MemoryReconcileStore(world.Source, world.Target);
            world.ConfigureMapping = Mark(approvedAt);
            return world;
        }

        private static Action<Mapping> Mark(string approvedAt)
        {
            return m =>
            {
                m.DeleteMode = DeleteModes.Mark;
                m.MarkColumn = "DEL_AT";
                m.MarkValue = "Y";
                m.DeleteApprovedAt = approvedAt;
            };
        }

        private static PlanItem Item()
        {
            var columns = new List<ColumnMetadata>
            {
                new ColumnMetadata { Name = "MEMBER_ID", Type = "NUMBER(18)", Nullable = false, PrimaryKey = true },
                new ColumnMetadata { Name = "BRANCH_CD", Type = "CHAR(5)", Nullable = false, PrimaryKey = true },
                new ColumnMetadata { Name = "DEL_YN", Type = "CHAR(1)", Nullable = true }
            };
            var mapping = new Mapping
            {
                Id = "M1", Source = "SRC_CUSTOMER", Target = "TB_MEMBER", Mode = WriteModes.Merge, Where = "STATUS_CD <> 'X'",
                MergeKey = new List<string> { "MEMBER_ID", "BRANCH_CD" }, DeleteMode = DeleteModes.Mark, MarkColumn = "DEL_YN", MarkValue = "Y",
                Columns = new List<ColumnMapping>
                {
                    new ColumnMapping { Source = "CUSTOMER_ID", Target = "MEMBER_ID" },
                    new ColumnMapping { Source = "BRANCH_CD", Target = "BRANCH_CD" }
                }
            };
            var target = new TableMetadata { Name = "TB_MEMBER", Columns = columns };
            return new PlanItem { Key = "M1", Mapping = mapping, Label = mapping.Label, TargetMetadata = target, WriteColumns = SqlGenerator.WriteColumns(mapping, target) };
        }
    }
}
