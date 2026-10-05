using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters.Oracle.Engine;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Sql;
using MigrationStudio.Tests.Engine.Fakes;
using Xunit;

namespace MigrationStudio.Tests.Engine
{
    public sealed class MigrationEngineTests
    {
        [Theory]
        [InlineData(WriteModes.InsertOnly)]
        [InlineData(WriteModes.Merge)]
        [InlineData(WriteModes.TruncateInsert)]
        [InlineData(WriteModes.DeleteInsert)]
        public async Task Engine_copies_all_rows_for_each_write_mode(string mode)
        {
            var fixture = Fixture(mode, 25);
            if (mode == WriteModes.TruncateInsert) fixture.Target.Rows.Add(new object[] { 99m, "old" });
            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Equal("done", fixture.Engine.State);
            Assert.Equal(25, fixture.Target.Rows.Count);
            Assert.Equal(Enumerable.Range(1, 25).Select(i => (decimal)i), fixture.Target.Rows.OrderBy(r => r[0]).Select(r => (decimal)r[0]));
        }

        [Fact]
        public async Task Merge_twice_is_idempotent_and_counts_updates()
        {
            var first = Fixture(WriteModes.Merge, 12);
            await first.Engine.RunAsync(CancellationToken.None);
            var second = Fixture(WriteModes.Merge, 12, first.Target);
            await second.Engine.RunAsync(CancellationToken.None);

            Assert.Equal(12, second.Target.Rows.Count);
            Assert.Equal(12, second.Listener.Final.Tasks[0].Updated);
            Assert.Equal(0, second.Listener.Final.Tasks[0].Inserted);
        }

        [Fact]
        public async Task Continue_rejects_invalid_rows_but_finishes()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 4, nameLength: 3);
            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Equal("done", fixture.Engine.State);
            Assert.Equal(4, fixture.Listener.Final.Tasks[0].Rejected);
            Assert.Empty(fixture.Target.Rows);
        }

        [Fact]
        public async Task Retry_reopens_sessions_after_transient_failure()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 20, errorPolicy: ErrorPolicies.Retry);
            fixture.TargetFactory.FailWrites = 1;
            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Equal("done", fixture.Engine.State);
            Assert.Equal(20, fixture.Target.Rows.Count);
            Assert.Contains(fixture.Listener.Logs, l => l.Tag == "WARN" && l.Text.Contains("재시도 1/3", StringComparison.Ordinal));
            Assert.True(fixture.TargetFactory.OpenCount >= 2);
        }

        [Fact]
        public async Task Retry_reopens_source_after_transient_read_failure()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 20, errorPolicy: ErrorPolicies.Retry);
            fixture.SourceFactory.FailOnRead = 2;

            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Equal("done", fixture.Engine.State);
            Assert.Equal(20, fixture.Target.Rows.Count);
            Assert.Contains(fixture.Listener.Logs, l => l.Tag == "WARN" && l.Text.Contains("재시도 1/3", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Retry_does_not_retry_data_error()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 3, nameLength: 1, errorPolicy: ErrorPolicies.Retry);
            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Equal("failed", fixture.Engine.State);
            Assert.Contains(fixture.Listener.Logs, l => l.Text.Contains("데이터 오류는 재시도로 해결되지 않음", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Retry_fails_after_three_reconnects()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 3, errorPolicy: ErrorPolicies.Retry);
            fixture.TargetFactory.FailWrites = 4;
            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Equal("failed", fixture.Engine.State);
            Assert.Equal(4, fixture.TargetFactory.OpenCount);
            Assert.Equal(3, fixture.Listener.Logs.Count(l => l.Tag == "WARN" && l.Text.Contains("다시 연결해 재시도", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task Stop_policy_rolls_back_rejected_batch()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 4, nameLength: 3, errorPolicy: ErrorPolicies.Stop);
            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Equal("failed", fixture.Engine.State);
            Assert.Empty(fixture.Target.Rows);
            Assert.Contains(fixture.Listener.Logs, l => l.Tag == "ERROR" && l.Text.Contains("진행 중 배치 4행 롤백", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Dry_run_leaves_target_unchanged_and_estimates_updates()
        {
            var target = Table("TGT", 20);
            target.Rows.Add(new object[] { 1m, "NAME-1" });
            var fixture = Fixture(WriteModes.Merge, 5, target, runMode: "DRY");
            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Single(target.Rows);
            Assert.Equal(1, fixture.Listener.Final.Tasks[0].Updated);
            Assert.Equal(4, fixture.Listener.Final.Tasks[0].Inserted);
            Assert.Empty(fixture.Listener.Checkpoints);
        }

        [Fact]
        public async Task Listener_exception_does_not_stop_engine_and_password_is_redacted()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 5);
            fixture.Listener.Throw = true;
            fixture.Spec.Source.Connection.Password = "top-secret";
            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Equal("done", fixture.Engine.State);
        }

        [Fact]
        public async Task Password_is_removed_from_failure_log()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 2, errorPolicy: ErrorPolicies.Stop);
            fixture.Spec.Target.Connection.Password = "top-secret";
            fixture.TargetFactory.FailWrites = 1;
            fixture.TargetFactory.FailureMessage = "실패한 접속 password=top-secret";
            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Equal("failed", fixture.Engine.State);
            Assert.DoesNotContain("top-secret", string.Join("\n", fixture.Listener.Logs.Select(l => l.Text)), StringComparison.Ordinal);
            Assert.Contains("***", string.Join("\n", fixture.Listener.Logs.Select(l => l.Text)), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Parallel_ranges_cover_source_once_and_keep_bounded_buffer()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 100, workers: 4);
            fixture.Spec.Plan[0].Ranges = new List<KeyRange>
            {
                new KeyRange { From = "1", To = "25", Rows = 25 },
                new KeyRange { From = "26", To = "50", Rows = 25 },
                new KeyRange { From = "51", To = "75", Rows = 25 },
                new KeyRange { From = "76", To = "100", Rows = 25 }
            };
            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Equal(100, fixture.Target.Rows.Count);
            Assert.Equal(4, fixture.Listener.Final.Tasks[0].Ranges.Count);
            Assert.True(fixture.Listener.Final.Pipeline.MaxBufferedBatches <= 2, fixture.Listener.Final.Pipeline.MaxBufferedBatches.ToString());
        }

        [Fact]
        public async Task Parallel_worker_failure_cancels_sibling_ranges()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 400, workers: 4, errorPolicy: ErrorPolicies.Stop);
            fixture.Spec.Plan[0].Ranges = new List<KeyRange>
            {
                new KeyRange { From = "1", To = "100", Rows = 100 },
                new KeyRange { From = "101", To = "200", Rows = 100 },
                new KeyRange { From = "201", To = "300", Rows = 100 },
                new KeyRange { From = "301", To = "400", Rows = 100 }
            };
            fixture.SourceFactory.FailOnRead = 2;
            fixture.SourceFactory.DelayMilliseconds = 5;

            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Equal("failed", fixture.Engine.State);
            Assert.True(fixture.Target.Rows.Count < 400);
        }

        [Fact]
        public async Task Pause_and_resume_stops_at_commit_boundary()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 60);
            fixture.TargetFactory.DelayMilliseconds = 20;
            var run = fixture.Engine.RunAsync(CancellationToken.None);
            await WaitUntil(() => fixture.TargetFactory.WriteCalls > 0);
            fixture.Engine.Pause();
            await WaitUntil(() => fixture.Engine.State == "paused");
            var committed = fixture.Target.Rows.Count;
            Assert.True(committed > 0);
            fixture.Engine.Resume();
            await run;

            Assert.Equal("done", fixture.Engine.State);
            Assert.Equal(60, fixture.Target.Rows.Count);
        }

        [Fact]
        public async Task Stop_rolls_back_inflight_batch_and_resume_completes()
        {
            var fixture = Fixture(WriteModes.Merge, 100);
            fixture.TargetFactory.DelayMilliseconds = 25;
            var run = fixture.Engine.RunAsync(CancellationToken.None);
            await WaitUntil(() => fixture.TargetFactory.WriteCalls >= 2);
            fixture.Engine.Stop();
            await run;
            var checkpoint = await fixture.Store.GetAsync("JOB", "M1", CancellationToken.None);
            Assert.Equal(fixture.Target.Rows.Count, checkpoint.RowsDone);

            var resumed = Fixture(WriteModes.Merge, 100, fixture.Target, runMode: "RESUME", store: fixture.Store);
            resumed.Spec.Plan[0].ResumeFrom = checkpoint.Value;
            resumed.Spec.Plan[0].BaseRows = checkpoint.RowsDone;
            resumed.Spec.Plan[0].Ranges[0].Last = checkpoint.Value;
            await resumed.Engine.RunAsync(CancellationToken.None);
            Assert.Equal(100, fixture.Target.Rows.Count);
        }

        [Fact]
        public async Task Resume_keeps_cumulative_checkpoint_rows()
        {
            var store = new MemoryCheckpointStore();
            store.Save(new CheckpointRecord { Job = "JOB", TaskKey = "M1", Column = "ID", Value = "10", RowsDone = 10, RowsTotal = 20 });
            var fixture = Fixture(WriteModes.Merge, 20, runMode: "RESUME", store: store);
            fixture.Spec.Plan[0].ResumeFrom = "10";
            fixture.Spec.Plan[0].BaseRows = 10;
            fixture.Spec.Plan[0].Ranges[0].Last = "10";
            await fixture.Engine.RunAsync(CancellationToken.None);

            var checkpoint = await store.GetAsync("JOB", "M1", CancellationToken.None);
            Assert.Equal("done", fixture.Engine.State);
            Assert.Equal(20, checkpoint.RowsDone);
            Assert.Equal(20, fixture.Listener.Final.Totals.Done);
        }

        [Fact]
        public async Task Target_checkpoint_rolls_back_with_batch_and_retry_is_exactly_once()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 20, errorPolicy: ErrorPolicies.Retry);
            fixture.TargetFactory.FailBeforeCommitOnce = true;
            await fixture.Engine.RunAsync(CancellationToken.None);

            Assert.Equal("done", fixture.Engine.State);
            Assert.Equal(20, fixture.Target.Rows.Count);
            Assert.Equal(20, fixture.Target.Rows.Select(r => r[0]).Distinct().Count());
            var checkpoint = await fixture.Store.GetAsync("JOB", "M1", CancellationToken.None);
            Assert.Equal(20, checkpoint.RowsDone);
        }

        [Fact]
        public async Task Local_checkpoint_gap_replays_first_batch_as_merge()
        {
            var store = new MemoryCheckpointStore { FailNextSave = true };
            var first = Fixture(WriteModes.InsertOnly, 20, store: store);
            first.Spec.CheckpointStore = "LOCAL";
            await first.Engine.RunAsync(CancellationToken.None);
            Assert.Equal("failed", first.Engine.State);
            Assert.Equal(10, first.Target.Rows.Count);

            var resumed = Fixture(WriteModes.InsertOnly, 20, first.Target, runMode: "RESUME", store: store);
            resumed.Spec.CheckpointStore = "LOCAL";
            await resumed.Engine.RunAsync(CancellationToken.None);
            Assert.Equal("done", resumed.Engine.State);
            Assert.Equal(20, first.Target.Rows.Count);
            Assert.Equal(20, first.Target.Rows.Select(r => r[0]).Distinct().Count());
        }

        [Fact]
        public async Task Local_checkpoint_file_is_atomic_and_round_trips()
        {
            var directory = Path.Combine(Path.GetTempPath(), "migration-engine-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new LocalCheckpointStore(directory);
                var record = new CheckpointRecord { Job = "A:B", TaskKey = "M1", Column = "ID", Value = "9", RowsDone = 9 };
                await store.SaveLocalAsync(record, CancellationToken.None);
                var loaded = await store.GetAsync("A:B", "M1", CancellationToken.None);
                Assert.Equal("9", loaded.Value);
                Assert.Empty(Directory.GetFiles(Path.Combine(directory, "checkpoints"), "*.tmp"));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void Planner_orders_parent_before_child_and_keeps_cycle_order()
        {
            var parent = new Mapping { Id = "P", Target = "PARENT" };
            var child = new Mapping { Id = "C", Target = "CHILD" };
            var metadata = new SchemaMetadata { Tables = new List<TableMetadata>
            {
                new TableMetadata { Name = "PARENT" },
                new TableMetadata { Name = "CHILD", ForeignKeys = new List<ForeignKeyMetadata> { new ForeignKeyMetadata { RefTable = "PARENT" } } }
            } };
            Assert.Equal(new[] { "P", "C" }, RunPlanner.OrderByFk(new[] { child, parent }, metadata).Select(m => m.Id));

            metadata.Tables[0].ForeignKeys.Add(new ForeignKeyMetadata { RefTable = "CHILD" });
            Assert.Equal(new[] { "C", "P" }, RunPlanner.OrderByFk(new[] { child, parent }, metadata).Select(m => m.Id));
        }

        [Fact]
        public async Task Planner_counts_filtered_scope_and_reuses_saved_ranges()
        {
            var mapping = new Mapping
            {
                Id = "M1", Source = "SRC", Target = "TGT", CheckpointColumn = "ID", Where = "ID > 10",
                Columns = new List<ColumnMapping> { new ColumnMapping { Source = "ID", Target = "ID" } }
            };
            var job = new MigrationJob
            {
                JobName = "JOB", Mappings = new List<Mapping> { mapping },
                Strategy = new MigrationStrategy { Workers = 1 }
            };
            var column = new ColumnMetadata { Name = "ID", Type = "NUMBER(18)", PrimaryKey = true, Nullable = false };
            var metadata = new RunMetadata
            {
                Source = new SchemaMetadata { Tables = new List<TableMetadata> { new TableMetadata { Name = "SRC", Rows = 999, Columns = new List<ColumnMetadata> { column } } } },
                Target = new SchemaMetadata { Tables = new List<TableMetadata> { new TableMetadata { Name = "TGT", Columns = new List<ColumnMetadata> { column } } } }
            };
            var store = new MemoryCheckpointStore();
            store.Save(new CheckpointRecord { Job = "JOB", TaskKey = "M1#1", Column = "ID", Value = "20", RangeFrom = "1", RangeTo = "50", RowsDone = 20, RowsTotal = 50 });
            store.Save(new CheckpointRecord { Job = "JOB", TaskKey = "M1#2", Column = "ID", Value = "50", RangeFrom = "51", RangeTo = "100", RowsDone = 0, RowsTotal = 50 });
            var probe = new Probe { Count = 90 };

            var plans = await RunPlanner.BuildAsync(job, new MigrationSettings(), metadata, new[] { "M1" }, "RESUME", probe, store, CancellationToken.None);

            Assert.Equal(90, plans[0].ScopeTotal);
            Assert.Equal(2, plans[0].Ranges.Count);
            Assert.Equal("1", plans[0].Ranges[0].From);
            Assert.Equal("100", plans[0].Ranges[1].To);
            Assert.Equal(1, probe.CountCalls);
            Assert.Equal(0, probe.RangeCalls);
        }

        [Fact]
        public void Error_table_name_is_30_bytes_and_collision_safe()
        {
            var strategy = new MigrationStrategy { ErrorPolicy = ErrorPolicies.Continue, ErrorTable = "ERR$_" };
            var used = new HashSet<string>(StringComparer.Ordinal);
            var first = ErrorTableNamer.Resolve(strategy, "A_VERY_LONG_TARGET_TABLE_NAME", used);
            var second = ErrorTableNamer.Resolve(strategy, "A_VERY_LONG_TARGET_TABLE_NAME", used);
            Assert.True(first.Length <= 30);
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void Run_id_and_oracle_source_sql_follow_contract()
        {
            Assert.Equal("R-20261003-144805", RunIds.New(new DateTime(2026, 10, 3, 14, 48, 5)));
            var fixture = Fixture(WriteModes.InsertOnly, 1);
            var sql = OracleSourceFactory.BuildSql(fixture.Spec.Plan[0], 10);
            Assert.Contains("AS MIG_CP_HIDDEN", sql, StringComparison.Ordinal);
            Assert.Contains("ORDER BY ID", sql, StringComparison.Ordinal);
        }

        /// <summary>실행 기록기(MIG_RUN)가 죽어도 이관은 끝까지 간다 — 에이전트 시험 어댑터·로컬 체크포인트 모드에서 기록기 예외가 실행을 통째로 실패시키던 회귀.</summary>
        [Fact]
        public async Task Recorder_failure_is_logged_once_and_does_not_fail_the_run()
        {
            var fixture = Fixture(WriteModes.InsertOnly, 25);
            var recorder = new ThrowingRecorder();
            var engine = new MigrationEngine(fixture.Spec, fixture.SourceFactory, fixture.TargetFactory, fixture.Store, fixture.Listener,
                new ManualClock(new DateTime(2026, 10, 5, 13, 0, 0)), recorder);
            await engine.RunAsync(CancellationToken.None);

            Assert.Equal("done", engine.State);
            Assert.Equal(25, fixture.Target.Rows.Count);
            Assert.Equal(1, recorder.Calls); // 첫 실패 뒤에는 더 부르지 않는다
            Assert.Contains(fixture.Listener.Logs, l => l.Tag == "WARN" && l.Text.Contains("MIG_RUN"));
        }

        private sealed class ThrowingRecorder : IRunRecorder
        {
            internal int Calls;
            public Task StartAsync(RunSpec spec, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("ORA-00942: 테이블 또는 뷰가 존재하지 않습니다"); }
            public Task TaskStartedAsync(RunSpec spec, PlanItem item, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("no"); }
            public Task TaskEndedAsync(RunSpec spec, TaskSnapshot task, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("no"); }
            public Task EndAsync(RunSpec spec, RunSnapshot snapshot, string message, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("no"); }
        }

        private static FixtureData Fixture(string mode, int count, MemoryTable target = null, int nameLength = 20,
            string errorPolicy = ErrorPolicies.Continue, string runMode = "EXECUTE", int workers = 1, MemoryCheckpointStore store = null)
        {
            var source = Table("SRC", 20);
            for (var i = 1; i <= count; i++) source.Rows.Add(new object[] { (decimal)i, "NAME-" + i });
            target = target ?? Table("TGT", nameLength);
            var mapping = new Mapping
            {
                Id = "M1", Source = "SRC", Target = "TGT", Mode = mode, CheckpointColumn = "ID",
                MergeKey = new List<string> { "ID" }, Columns = new List<ColumnMapping>
                {
                    new ColumnMapping { Source = "ID", Target = "ID" },
                    new ColumnMapping { Source = "NAME", Target = "NAME" }
                }
            };
            var item = new PlanItem
            {
                Key = "M1", Mapping = mapping, Label = mapping.Label, ScopeTotal = count,
                SourceMetadata = Meta(source), TargetMetadata = Meta(target), ErrorTable = "ERR$_TGT",
                Ranges = new List<KeyRange> { new KeyRange { Rows = count } }
            };
            item.WriteColumns = SqlGenerator.WriteColumns(mapping, item.TargetMetadata);
            var job = new MigrationJob
            {
                JobName = "JOB",
                Strategy = new MigrationStrategy { Mode = ExecutionModes.Full, CommitSize = 10, FetchSize = 4, Workers = workers, ErrorPolicy = errorPolicy },
                Mappings = new List<Mapping> { mapping }
            };
            var endpoint = new EndpointSpec { Schema = "S", Connection = new MigrationStudio.Core.Adapters.ConnectionTarget { Password = "pw" } };
            var spec = new RunSpec { RunId = "R-TEST", RunMode = runMode, Job = job, Source = endpoint, Target = endpoint, CheckpointStore = "TARGET", Plan = new List<PlanItem> { item }, RetryDelays = new List<int> { 0, 0, 0 } };
            store = store ?? new MemoryCheckpointStore();
            var sourceFactory = new MemorySourceFactory(source);
            var targetFactory = new MemoryTargetFactory(target) { Checkpoints = store };
            var listener = new CaptureListener();
            var engine = new MigrationEngine(spec, sourceFactory, targetFactory, store, listener, new ManualClock(new DateTime(2026, 10, 3, 14, 48, 5)));
            return new FixtureData { Source = source, Target = target, Store = store, SourceFactory = sourceFactory, TargetFactory = targetFactory, Listener = listener, Spec = spec, Engine = engine };
        }

        private static MemoryTable Table(string name, int nameLength)
        {
            return new MemoryTable(name, new[]
            {
                new ColumnMetadata { Name = "ID", Type = "NUMBER(18)", Nullable = false, PrimaryKey = true },
                new ColumnMetadata { Name = "NAME", Type = "VARCHAR2(" + nameLength + ")", Nullable = false }
            }, new[] { "ID" });
        }

        private static TableMetadata Meta(MemoryTable table)
        {
            return new TableMetadata { Name = table.Name, Rows = table.Rows.Count, Columns = table.Columns };
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
            Assert.True(condition());
        }

        private sealed class FixtureData
        {
            internal MemoryTable Source;
            internal MemoryTable Target;
            internal MemoryCheckpointStore Store;
            internal MemorySourceFactory SourceFactory;
            internal MemoryTargetFactory TargetFactory;
            internal CaptureListener Listener;
            internal RunSpec Spec;
            internal MigrationEngine Engine;
        }

        private sealed class Probe : ISourceProbe
        {
            internal long Count;
            internal int CountCalls;
            internal int RangeCalls;

            public Task<long> CountAsync(PlanItem item, CancellationToken cancellationToken)
            {
                CountCalls++;
                return Task.FromResult(Count);
            }

            public Task<List<KeyRange>> RangesAsync(PlanItem item, int workers, string lastValue, CancellationToken cancellationToken)
            {
                RangeCalls++;
                return Task.FromResult(new List<KeyRange>());
            }

            public Task<long> ExistingKeyCountAsync(PlanItem item, CancellationToken cancellationToken)
            {
                return Task.FromResult(0L);
            }
        }
    }
}
