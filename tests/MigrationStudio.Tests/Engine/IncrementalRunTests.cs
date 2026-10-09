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
            world.Store.Save(new CheckpointRecord { Job = "JOB", TaskKey = "M1", Column = "UPD_AT", Value = Stamp(10), Status = "done" });

            var run = await world.RunAsync(ExecutionModes.Incremental, "EXECUTE");

            Assert.Equal("done", run.Engine.State);
            Assert.Equal(0, run.Listener.Final.Tasks[0].Rejected);
            Assert.Equal(12, world.Target.Rows.Count);
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
            return new object[] { (decimal)i, "NAME-" + i, Base.AddMinutes(i) };
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
                    Strategy = new MigrationStrategy { Mode = strategyMode, CommitSize = 4, FetchSize = 4, Workers = 1, ErrorPolicy = ErrorPolicies.Continue },
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
                var engine = new MigrationEngine(spec, new MemorySourceFactory(Source), TargetFactory, Store, listener, Clock);
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
