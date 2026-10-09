using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Sql;
using MigrationStudio.Tests.Engine.Fakes;
using Xunit;

namespace MigrationStudio.Tests.Engine
{
    /// <summary>
    /// 증분 이관(전략 모드 INCREMENTAL): 플래너가 저장된 마지막 키를 워터마크로 삼아 그 다음 행만 읽고, 끝에 워터마크를 올리는지.
    /// 체크포인트 열은 수정시각(UPD_AT)이라 새 행뿐 아니라 수정된 행도 다시 온다.
    /// </summary>
    public sealed class IncrementalRunTests
    {
        internal static readonly DateTime Base = new DateTime(2026, 10, 1, 9, 0, 0);

        [Fact]
        public async Task Incremental_reads_only_rows_after_watermark_and_advances_it()
        {
            var world = new World(WriteModes.Merge, 10);

            var first = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            Assert.Equal("done", first.Engine.State);
            Assert.Equal(10, world.Target.Rows.Count);
            Assert.Contains(first.Listener.Logs, l => l.Tag == "WARN" && l.Text.Contains("워터마크 없음", StringComparison.Ordinal));
            var watermark = (await world.Store.GetAsync("JOB", "M1", CancellationToken.None)).Value;
            Assert.Equal(Stamp(10), watermark);

            // 원본 변경: 3행 추가 · 2행 수정(수정시각이 올라감) · 1행은 이름만 바꾸고 수정시각은 그대로(증분이 못 보는 변경)
            world.Source.Rows.Add(Row(11));
            world.Source.Rows.Add(Row(12));
            world.Source.Rows.Add(Row(13));
            world.Touch(2, 14);
            world.Touch(5, 15);
            world.Source.Rows.First(r => (decimal)r[0] == 7m)[1] = "SILENT-7";
            world.SourceNow = Base.AddMinutes(15);

            var second = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            Assert.Equal("done", second.Engine.State);
            Assert.Equal(5, second.Plan[0].ScopeTotal);
            Assert.Equal(Stamp(10), second.Plan[0].ResumeFrom);
            Assert.Equal(0, second.Plan[0].BaseRows);
            var task = second.Listener.Final.Tasks[0];
            Assert.Equal(5, task.Written);
            Assert.Equal(3, task.Inserted);
            Assert.Equal(2, task.Updated);
            Assert.Equal(13, world.Target.Rows.Count);
            Assert.Equal("NAME-2-v14", world.TargetName(2));
            Assert.Equal("NAME-5-v15", world.TargetName(5));
            Assert.Equal("NAME-7", world.TargetName(7));
            Assert.Contains(second.Listener.Logs, l => l.Tag == "START" && l.Text.Contains("증분: UPD_AT > " + Stamp(10), StringComparison.Ordinal));
            Assert.Equal(Stamp(15), (await world.Store.GetAsync("JOB", "M1", CancellationToken.None)).Value);

            var third = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            Assert.Equal("done", third.Engine.State);
            Assert.Equal(0, third.Listener.Final.Tasks[0].Written);
            Assert.Equal(13, world.Target.Rows.Count);
            Assert.Equal(Stamp(15), (await world.Store.GetAsync("JOB", "M1", CancellationToken.None)).Value);
        }

        [Fact]
        public async Task Full_mode_ignores_saved_watermark()
        {
            var world = new World(WriteModes.Merge, 10);
            await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            world.Source.Rows.Add(Row(11));

            var full = await world.RunAsync(ExecutionModes.Full, "EXECUTE");

            Assert.Null(full.Plan[0].ResumeFrom);
            Assert.Equal(11, full.Listener.Final.Tasks[0].Written);
            Assert.Equal(10, full.Listener.Final.Tasks[0].Updated);
            Assert.Equal(1, full.Listener.Final.Tasks[0].Inserted);
        }

        [Fact]
        public async Task Dry_run_in_incremental_plans_from_watermark_but_keeps_it()
        {
            var world = new World(WriteModes.Merge, 10);
            await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            world.Source.Rows.Add(Row(11));
            world.SourceNow = Base.AddMinutes(11);

            var dry = await world.RunAsync(ExecutionModes.Incremental, "DRY");

            Assert.Equal(Stamp(10), dry.Plan[0].ResumeFrom);
            Assert.Equal(1, dry.Plan[0].ScopeTotal);
            Assert.Equal(10, world.Target.Rows.Count);
            Assert.Equal(Stamp(10), (await world.Store.GetAsync("JOB", "M1", CancellationToken.None)).Value);
        }

        [Fact]
        public async Task Incremental_truncate_insert_with_watermark_fails_without_touching_target()
        {
            var world = new World(WriteModes.TruncateInsert, 10);
            var first = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            Assert.Equal("done", first.Engine.State);
            world.Source.Rows.Add(Row(11));
            world.SourceNow = Base.AddMinutes(11);

            var second = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");

            Assert.Equal("failed", second.Engine.State);
            Assert.Equal(10, world.Target.Rows.Count);
            Assert.Contains(second.Listener.Logs, l => l.Tag == "ERROR" && l.Text.Contains("TRUNCATE + INSERT를 쓸 수 없음", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Local_store_incremental_first_batch_absorbs_replayed_row()
        {
            // 로컬 파일 저장소: 커밋 뒤 워터마크를 쓰기 전에 죽으면 다음 증분이 마지막 배치를 다시 읽는다 — 첫 배치를 MERGE로 써서 중복 키를 흡수해야 한다.
            var world = new World(WriteModes.InsertOnly, 10, "LOCAL");
            await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            world.Source.Rows.Add(Row(11));
            world.Source.Rows.Add(Row(12));
            world.Target.Rows.Add(Row(11));
            world.SourceNow = Base.AddMinutes(12);
            world.Store.Save(new CheckpointRecord { Job = "JOB", TaskKey = "M1", Column = "UPD_AT", Value = Stamp(10), Status = "done" });

            var run = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");

            Assert.Equal("done", run.Engine.State);
            Assert.Equal(0, run.Listener.Final.Tasks[0].Rejected);
            Assert.Equal(12, world.Target.Rows.Count);
        }

        [Fact]
        public async Task Lag_window_keeps_late_committed_row_and_never_moves_watermark_backwards()
        {
            // 지연 창 없이: 수정시각 09:08인 행 B가 09:10 실행 뒤에 커밋되면(지연 커밋) 워터마크 09:10을 지나쳐 영원히 빠진다
            var naive = new World(WriteModes.Merge, 10);
            await naive.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            naive.Source.Rows.Add(RowAt(22, 8));
            naive.SourceNow = Base.AddMinutes(16);
            await naive.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            Assert.DoesNotContain(naive.Target.Rows, r => (decimal)r[0] == 22m);

            // 지연 창 5분: 상한 = 원본 시각 − 5분. 09:10 실행은 09:05까지만 읽고, B는 09:16 실행(상한 09:11)에서 들어온다
            var lagged = new World(WriteModes.Merge, 10);
            Action<MigrationStrategy> lag = s => s.LagSeconds = 300;
            var first = await lagged.RunAsync(ExecutionModes.Incremental, "EXECUTE", lag, null);
            Assert.Equal(5, lagged.Target.Rows.Count);
            Assert.Equal(Stamp(5), (await lagged.Store.GetAsync("JOB", "M1", CancellationToken.None)).Value);
            Assert.Contains(first.Listener.Logs, l => l.Tag == "START" && l.Text.Contains("지연 창: UPD_AT <= " + Stamp(5), StringComparison.Ordinal));

            lagged.Source.Rows.Add(RowAt(22, 8));
            lagged.SourceNow = Base.AddMinutes(16);
            await lagged.RunAsync(ExecutionModes.Incremental, "EXECUTE", lag, null);
            Assert.Equal(11, lagged.Target.Rows.Count);
            Assert.Contains(lagged.Target.Rows, r => (decimal)r[0] == 22m);
            Assert.Equal(Stamp(11), (await lagged.Store.GetAsync("JOB", "M1", CancellationToken.None)).Value);

            // 원본 시계가 뒤로 가도(상한 < 워터마크) 읽을 것이 없을 뿐 워터마크는 그대로
            lagged.SourceNow = Base.AddMinutes(5);
            var skewed = await lagged.RunAsync(ExecutionModes.Incremental, "EXECUTE", lag, null);
            Assert.Equal("done", skewed.Engine.State);
            Assert.Equal(0, skewed.Listener.Final.Tasks[0].Written);
            Assert.Equal(Stamp(11), (await lagged.Store.GetAsync("JOB", "M1", CancellationToken.None)).Value);
        }

        [Fact]
        public async Task Oracle_source_sql_binds_upper_bound_only_when_window_is_set()
        {
            var world = new World(WriteModes.Merge, 3);
            var run = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");
            var plain = MigrationStudio.Core.Adapters.Oracle.Engine.OracleSourceFactory.BuildSql(run.Plan[0], 10, "S", new KeyRange());
            Assert.DoesNotContain(":UPPER", plain, StringComparison.Ordinal);
            var windowed = MigrationStudio.Core.Adapters.Oracle.Engine.OracleSourceFactory.BuildSql(run.Plan[0], 10, "S", new KeyRange { Upper = Stamp(10) });
            Assert.Contains("UPD_AT <= :UPPER", windowed, StringComparison.Ordinal);
            Assert.Contains("(:LAST_ID IS NULL OR UPD_AT > :LAST_ID)", windowed, StringComparison.Ordinal);
            Assert.True(windowed.IndexOf("WHERE", StringComparison.Ordinal) < windowed.IndexOf("ORDER BY", StringComparison.Ordinal));
        }

        [Fact]
        public void Watermark_prefers_single_record_and_takes_minimum_of_worker_ranges()
        {
            var records = new List<CheckpointRecord>
            {
                new CheckpointRecord { TaskKey = "M1#1", Value = "40" },
                new CheckpointRecord { TaskKey = "M1#2", Value = "90" },
                new CheckpointRecord { TaskKey = "M2", Value = "7" }
            };
            Assert.Equal("40", RunPlanner.Watermark(records, "M1"));
            Assert.Equal("7", RunPlanner.Watermark(records, "M2"));
            Assert.Null(RunPlanner.Watermark(records, "M3"));
            records.Add(new CheckpointRecord { TaskKey = "M1", Value = "100" });
            Assert.Equal("100", RunPlanner.Watermark(records, "M1"));
        }

        internal static object[] Row(int i)
        {
            return RowAt(i, i);
        }

        internal static object[] RowAt(int id, int minutes)
        {
            return new object[] { (decimal)id, "NAME-" + id, Base.AddMinutes(minutes) };
        }

        internal static string Stamp(int minutes)
        {
            return Base.AddMinutes(minutes).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        }

        internal sealed class RunResult
        {
            internal MigrationEngine Engine;
            internal CaptureListener Listener;
            internal List<PlanItem> Plan;
        }

        /// <summary>원본·대상·체크포인트 저장소를 들고 있다가 실행마다 플래너 → 엔진을 새로 만든다(실제 호스트가 하는 순서).</summary>
        internal sealed class World
        {
            private readonly string _writeMode;
            private readonly string _storeMode;

            internal World(string writeMode, int rows, string storeMode = "TARGET")
            {
                _writeMode = writeMode;
                _storeMode = storeMode;
                Source = Table("SRC");
                Target = Table("TGT");
                for (var i = 1; i <= rows; i++) Source.Rows.Add(Row(i));
            }

            internal MemoryTable Source { get; private set; }
            internal MemoryTable Target { get; private set; }
            internal MemoryCheckpointStore Store { get; private set; } = new MemoryCheckpointStore();
            internal ManualClock Clock { get; private set; }
            internal MemoryTargetFactory TargetFactory { get; private set; }
            internal MigrationStrategy LastStrategy { get; private set; }

            /// <summary>원본 DB 시각. 행 수정시각(Base + n분)과 같은 시간선 — 행을 넣거나 고친 뒤 그 시각 이상으로 올려야 창 안에 들어온다.</summary>
            internal DateTime SourceNow { get; set; } = Base.AddMinutes(10);

            internal void Touch(int id, int minutes)
            {
                var row = Source.Rows.First(r => (decimal)r[0] == (decimal)id);
                row[1] = "NAME-" + id + "-v" + minutes;
                row[2] = Base.AddMinutes(minutes);
            }

            internal string TargetName(int id)
            {
                return (string)Target.Rows.First(r => (decimal)r[0] == (decimal)id)[1];
            }

            internal Task<RunResult> RunAsync(string strategyMode, string runMode)
            {
                return RunAsync(strategyMode, runMode, null, null);
            }

            /// <param name="strategy">전략을 손볼 때(주기·최대 실행 시간).</param>
            /// <param name="configure">엔진을 돌리기 전에 손볼 때(주기 대기 바꿔 끼우기 등).</param>
            internal async Task<RunResult> RunAsync(string strategyMode, string runMode, Action<MigrationStrategy> strategy, Action<MigrationEngine> configure)
            {
                var mapping = new Mapping
                {
                    Id = "M1", Source = "SRC", Target = "TGT", Mode = _writeMode, CheckpointColumn = "UPD_AT",
                    MergeKey = new List<string> { "ID" },
                    Columns = new List<ColumnMapping>
                    {
                        new ColumnMapping { Source = "ID", Target = "ID" },
                        new ColumnMapping { Source = "NAME", Target = "NAME" },
                        new ColumnMapping { Source = "UPD_AT", Target = "UPD_AT" }
                    }
                };
                var job = new MigrationJob
                {
                    JobName = "JOB",
                    Strategy = new MigrationStrategy { Mode = strategyMode, CommitSize = 4, FetchSize = 4, Workers = 1, ErrorPolicy = ErrorPolicies.Continue, LagSeconds = 0 },
                    Mappings = new List<Mapping> { mapping }
                };
                if (strategy != null) strategy(job.Strategy);
                LastStrategy = job.Strategy;
                var metadata = new RunMetadata
                {
                    Source = new SchemaMetadata { Tables = new List<TableMetadata> { Meta(Source) } },
                    Target = new SchemaMetadata { Tables = new List<TableMetadata> { Meta(Target) } }
                };
                var plan = await RunPlanner.BuildAsync(job, new MigrationSettings(), metadata, new[] { "M1" }, runMode, new TableProbe(Source), Store, CancellationToken.None);
                var endpoint = new EndpointSpec { Schema = "S", Connection = new MigrationStudio.Core.Adapters.ConnectionTarget { Password = "pw" } };
                var spec = new RunSpec
                {
                    RunId = "R-INC", RunMode = runMode, Job = job, Source = endpoint, Target = endpoint,
                    CheckpointStore = _storeMode, Plan = plan, RetryDelays = new List<int> { 0, 0, 0 }
                };
                var listener = new CaptureListener();
                Clock = new ManualClock(new DateTime(2026, 10, 9, 10, 0, 0));
                TargetFactory = new MemoryTargetFactory(Target) { Checkpoints = Store };
                var sourceFactory = new MemorySourceFactory(Source) { Now = () => SourceNow };
                var engine = new MigrationEngine(spec, sourceFactory, TargetFactory, Store, listener, Clock);
                if (configure != null) configure(engine);
                await engine.RunAsync(CancellationToken.None);
                return new RunResult { Engine = engine, Listener = listener, Plan = plan };
            }

            private static MemoryTable Table(string name)
            {
                return new MemoryTable(name, new[]
                {
                    new ColumnMetadata { Name = "ID", Type = "NUMBER(18)", Nullable = false, PrimaryKey = true },
                    new ColumnMetadata { Name = "NAME", Type = "VARCHAR2(30)", Nullable = false },
                    new ColumnMetadata { Name = "UPD_AT", Type = "DATE", Nullable = false }
                }, new[] { "ID" });
            }

            private static TableMetadata Meta(MemoryTable table)
            {
                return new TableMetadata { Name = table.Name, Rows = table.Rows.Count, Columns = table.Columns };
            }
        }

        /// <summary>메모리 테이블을 세는 원본 탐침. 범위 하나(작업자 1)로 워터마크 이후 행 수를 돌려준다.</summary>
        private sealed class TableProbe : ISourceProbe
        {
            private readonly MemoryTable _table;

            internal TableProbe(MemoryTable table) { _table = table; }

            public Task<long> CountAsync(PlanItem item, CancellationToken cancellationToken)
            {
                return Task.FromResult((long)_table.Rows.Count);
            }

            public Task<List<KeyRange>> RangesAsync(PlanItem item, int workers, string lastValue, CancellationToken cancellationToken)
            {
                var ordinal = _table.Ordinal(item.Mapping.CheckpointColumn);
                var bound = lastValue == null ? (DateTime?)null : DateTime.Parse(lastValue, CultureInfo.InvariantCulture);
                var rows = _table.Rows.Where(r => bound == null || ((DateTime)r[ordinal]) > bound.Value).ToList();
                return Task.FromResult(new List<KeyRange> { new KeyRange { Rows = rows.Count } });
            }

            public Task<long> ExistingKeyCountAsync(PlanItem item, CancellationToken cancellationToken)
            {
                return Task.FromResult(0L);
            }
        }
    }
}
