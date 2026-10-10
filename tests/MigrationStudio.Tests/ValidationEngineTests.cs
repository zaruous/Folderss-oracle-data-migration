using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Validation;
using MigrationStudio.Testing.Adapters;
using MigrationStudio.Tests.Golden;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class ValidationEngineTests
    {
        private static readonly GoldenDocument G = GoldenFixture.Document;

        [Fact]
        public async Task RunPre_sampleJob_matches_poc_shape()
        {
            var ctx = SampleContext();
            var adapter = Fake(ctx);
            var items = await new ValidationEngine(adapter).RunPreAsync(ctx, null, CancellationToken.None);
            Assert.Contains(items, i => i.Check == "원본 접속" && i.Level == CheckLevels.Pass);
            Assert.Contains(items, i => i.Check == "NOT NULL" && i.Target == "TB_SALES_ORDER.CHANNEL_CD" && i.Level == CheckLevels.Error);
            Assert.Contains(items, i => i.Check == "VARCHAR 길이" && i.Level == CheckLevels.Warn);
            Assert.Contains(items, i => i.MappingId == "tm-order" && i.Check == "컬럼 매핑" && i.Detail.Contains("6 / 7"));
            Assert.DoesNotContain(items, i => i.Check == "원본 SQL");
        }

        [Fact]
        public async Task RunPre_incremental_requires_checkpoint_column_and_blocks_truncate()
        {
            var full = SampleContext();
            var fullItems = await new ValidationEngine(Fake(full)).RunPreAsync(full, null, CancellationToken.None);
            Assert.DoesNotContain(fullItems, i => i.Check == "증분 기준");

            var ctx = SampleContext();
            ctx.Job.Strategy.Mode = ExecutionModes.Incremental;
            var order = ctx.Job.Mappings.First(m => m.Id == "tm-order");
            order.Mode = WriteModes.TruncateInsert;
            var items = await new ValidationEngine(Fake(ctx)).RunPreAsync(ctx, null, CancellationToken.None);

            // tm-grade: 체크포인트 열 없음 → ERROR / tm-order: TRUNCATE + INSERT → ERROR / tm-customer: NUMBER 키 + MERGE → PASS
            Assert.Contains(items, i => i.Check == "증분 기준" && i.MappingId == "tm-grade" && i.Level == CheckLevels.Error && i.Detail.Contains("체크포인트 열이 없어"));
            Assert.Contains(items, i => i.Check == "증분 기준" && i.MappingId == "tm-order" && i.Level == CheckLevels.Error && i.Detail.Contains("TRUNCATE"));
            Assert.Contains(items, i => i.Check == "증분 기준" && i.MappingId == "tm-customer" && i.Level == CheckLevels.Pass);
            var gate = ValidationGate.Evaluate(items, new HashSet<string>(StringComparer.Ordinal) { "tm-grade" });
            Assert.True(gate.Blocked);
        }

        [Fact]
        public async Task RunPre_cdc_warns_insert_only_mapping()
        {
            var ctx = SampleContext();
            ctx.Job.Strategy.Mode = ExecutionModes.Cdc;
            var items = await new ValidationEngine(Fake(ctx)).RunPreAsync(ctx, null, CancellationToken.None);
            // tm-order는 INSERT ONLY — 변경동기화에서는 수정된 행이 다시 올 때 중복 키라 WARN
            Assert.Contains(items, i => i.Check == "증분 기준" && i.MappingId == "tm-order" && i.Level == CheckLevels.Warn && i.Detail.Contains("ORA-00001"));
            Assert.Contains(items, i => i.Check == "증분 기준" && i.MappingId == "tm-customer" && i.Level == CheckLevels.Pass);
        }

        [Fact]
        public async Task RunPre_sync_checks_sql_source_password_and_source_clock()
        {
            var ctx = SampleContext();
            ctx.Job.Strategy.Mode = ExecutionModes.Cdc;
            ctx.Job.Strategy.MaxRunHours = 0;
            ctx.Job.Mappings.First(m => m.Id == "tm-sql-member").Use = true;
            ctx.SourceProfile.SavePassword = false;
            var adapter = Fake(ctx);
            var inner = adapter.QueryResponder;
            adapter.QueryResponder = sql => sql.Contains("SYSTIMESTAMP", StringComparison.Ordinal)
                ? new QueryResult { Columns = new List<QueryColumn>(), Rows = new List<string[]> { new[] { "2026-10-09 10:00:00" } } }
                : inner(sql);
            var items = await new ValidationEngine(adapter).RunPreAsync(ctx, null, CancellationToken.None);

            Assert.Contains(items, i => i.Check == "증분 기준" && i.MappingId == "tm-sql-member" && i.Level == CheckLevels.Warn && i.Detail.Contains("SQL 원본"));
            Assert.Contains(items, i => i.Check == "동기화 접속" && i.Level == CheckLevels.Error && i.MappingId == null);
            Assert.Contains(items, i => i.Check == "원본 시계" && i.Detail.Contains("2026-10-09 10:00:00"));

            ctx.Job.Strategy.MaxRunHours = 24;
            var limited = await new ValidationEngine(Fake(ctx)).RunPreAsync(ctx, null, CancellationToken.None);
            Assert.Contains(limited, i => i.Check == "동기화 접속" && i.Level == CheckLevels.Warn);
            Assert.DoesNotContain(limited, i => i.Check == "원본 시계");
        }

        [Fact]
        public async Task RunPre_delete_mark_checks_column_value_target_and_approval()
        {
            var ctx = SampleContext();
            ctx.Job.Strategy.Mode = ExecutionModes.Incremental;
            var customer = ctx.Job.Mappings.First(m => m.Id == "tm-customer");
            customer.DeleteMode = DeleteModes.Mark;
            customer.MarkColumn = "MEMBER_GRADE";
            customer.MarkValue = "DELETED";
            var items = await new ValidationEngine(Fake(ctx)).RunPreAsync(ctx, null, CancellationToken.None);
            var pending = items.Single(i => i.Check == "삭제 반영" && i.MappingId == "tm-customer");
            Assert.Equal(CheckLevels.Info, pending.Level);
            Assert.Contains("승인 전", pending.Detail);
            Assert.DoesNotContain(items, i => i.Check == "증분 기준" && i.MappingId == "tm-customer" && i.Detail.Contains("원본에서 지운 행은 대상에 남음"));

            customer.DeleteApprovedAt = "2026-10-09 10:00:00";
            items = await new ValidationEngine(Fake(ctx)).RunPreAsync(ctx, null, CancellationToken.None);
            Assert.Equal(CheckLevels.Pass, items.Single(i => i.Check == "삭제 반영").Level);

            // NOT NULL 열 · 이관이 쓰는 날짜 열에 문자 값 · 같은 대상을 쓰는 다른 매핑 · SQL 원본
            customer.MarkColumn = "USE_YN";
            Assert.Contains("NULL 허용", await DeleteDetail(ctx));
            customer.MarkColumn = "UPDATED_AT";
            var detail = await DeleteDetail(ctx);
            Assert.Contains("원본 값을 매핑함", detail);
            Assert.Contains("SYSDATE만", detail);
            customer.MarkColumn = "MEMBER_GRADE";
            ctx.Job.Mappings.First(m => m.Id == "tm-sql-member").Use = true;
            Assert.Contains("같은 대상에 쓰는 매핑이 2개", await DeleteDetail(ctx));

            ctx.Job.Strategy.Mode = ExecutionModes.Full;
            ctx.Job.Mappings.First(m => m.Id == "tm-sql-member").Use = false;
            items = await new ValidationEngine(Fake(ctx)).RunPreAsync(ctx, null, CancellationToken.None);
            var full = items.Single(i => i.Check == "삭제 반영");
            Assert.Equal(CheckLevels.Warn, full.Level);
            Assert.Contains("전체 이관에서는 삭제를 대조하지 않음", full.Detail);
        }

        [Fact]
        public async Task RunPre_incremental_warns_when_where_filters_by_soft_delete_column()
        {
            var ctx = SampleContext();
            ctx.Job.Strategy.Mode = ExecutionModes.Cdc;
            ctx.Job.Mappings.First(m => m.Id == "tm-customer").Where = "DEL_YN = 'N'";
            var items = await new ValidationEngine(Fake(ctx)).RunPreAsync(ctx, null, CancellationToken.None);
            var item = items.Single(i => i.Check == "증분 기준" && i.MappingId == "tm-customer");
            Assert.Equal(CheckLevels.Warn, item.Level);
            Assert.Contains("삭제 표시 열로 거름", item.Detail);
            Assert.Contains("원본에서 지운 행은 대상에 남음", item.Detail);
        }

        [Fact]
        public void Mark_value_must_fit_the_column_type()
        {
            Assert.Null(ValidationEngine.MarkValueProblem("SYSDATE", new ColumnMetadata { Name = "DEL_AT", Type = "DATE" }));
            Assert.NotNull(ValidationEngine.MarkValueProblem("Y", new ColumnMetadata { Name = "DEL_AT", Type = "DATE" }));
            Assert.NotNull(ValidationEngine.MarkValueProblem("SYSDATE", new ColumnMetadata { Name = "DEL_YN", Type = "CHAR(1)" }));
            Assert.NotNull(ValidationEngine.MarkValueProblem("YES", new ColumnMetadata { Name = "DEL_YN", Type = "CHAR(1)" }));
            Assert.Null(ValidationEngine.MarkValueProblem("1", new ColumnMetadata { Name = "DEL_FLAG", Type = "NUMBER(1)" }));
            Assert.NotNull(ValidationEngine.MarkValueProblem("Y", new ColumnMetadata { Name = "DEL_FLAG", Type = "NUMBER(1)" }));
            Assert.NotNull(ValidationEngine.MarkValueProblem(" ", new ColumnMetadata { Name = "DEL_YN", Type = "CHAR(1)" }));
        }

        private static async Task<string> DeleteDetail(ValidationContext ctx)
        {
            var items = await new ValidationEngine(Fake(ctx)).RunPreAsync(ctx, null, CancellationToken.None);
            var item = items.Single(i => i.Check == "삭제 반영" && i.MappingId == "tm-customer");
            Assert.Equal(CheckLevels.Error, item.Level);
            return item.Detail;
        }

        [Fact]
        public async Task RunPre_missing_meta_stops_with_error()
        {
            var ctx = SampleContext();
            ctx.SourceMeta = null;
            var items = await new ValidationEngine(Fake(ctx)).RunPreAsync(ctx, null, CancellationToken.None);
            Assert.Contains(items, i => i.Check == "메타데이터" && i.Level == CheckLevels.Error);
        }

        [Fact]
        public async Task RunPre_write_blocked_target_is_error()
        {
            var ctx = SampleContext();
            ctx.TargetProfile.WriteBlocked = true;
            ctx.TargetTest = null;
            var items = await new ValidationEngine(Fake(ctx)).RunPreAsync(ctx, null, CancellationToken.None);
            Assert.Contains(items, i => i.Check == "대상 접속" && i.Level == CheckLevels.Error);
        }

        [Fact]
        public async Task RunPre_cancel_throws()
        {
            var ctx = SampleContext();
            var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new ValidationEngine(Fake(ctx)).RunPreAsync(ctx, null, cts.Token));
        }

        [Fact]
        public void ValidationGate_blocks_global_and_selected_mapping_errors()
        {
            var items = new List<ValidationItem>
            {
                new ValidationItem { Level = CheckLevels.Error, Check = "접속", MappingId = null },
                new ValidationItem { Level = CheckLevels.Error, Check = "col", MappingId = "tm-order" },
                new ValidationItem { Level = CheckLevels.Error, Check = "col2", MappingId = "tm-other" }
            };
            var selected = new HashSet<string>(StringComparer.Ordinal) { "tm-order" };
            var gate = ValidationGate.Evaluate(items, selected);
            Assert.True(gate.Blocked);
            Assert.Equal(2, gate.Errors);
            Assert.Equal(2, gate.Blocking.Count);
        }

        [Fact]
        public void ValidationSql_existingTables_splits_in_chunks()
        {
            var names = Enumerable.Range(1, 1001).Select(i => "T" + i.ToString()).ToList();
            var chunks = ValidationSql.ExistingTables("LEGACY_APP", names);
            Assert.Equal(2, chunks.Count);
            Assert.Contains("T1", chunks[0]);
            Assert.Contains("T1001", chunks[1]);
        }

        [Fact]
        public async Task RunPre_queries_are_select_only()
        {
            var ctx = SampleContext();
            var adapter = Fake(ctx);
            await new ValidationEngine(adapter).RunPreAsync(ctx, null, CancellationToken.None);
            foreach (var call in adapter.Calls)
            {
                var t = (call.Sql ?? "").TrimStart();
                Assert.True(t.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                    || t.StartsWith("WITH", StringComparison.OrdinalIgnoreCase), call.Sql);
            }
        }

        [Fact]
        public async Task RunPre_truncation_item_has_sample_tail()
        {
            var ctx = SampleContext();
            var items = await new ValidationEngine(Fake(ctx)).RunPreAsync(ctx, null, CancellationToken.None);
            var trunc = items.First(i => i.Check == "VARCHAR 길이" && i.Level == CheckLevels.Warn);
            Assert.Equal("표본 5%", trunc.Sample);
        }

        private static ValidationContext SampleContext()
        {
            var settings = GoldenTestHelpers.SettingsFromGolden();
            var srcProfile = settings.Connections.First(c => c.Id == "cn-legacy-prod");
            var tgtProfile = settings.Connections.First(c => c.Id == "cn-next-prod");
            return new ValidationContext
            {
                Job = GoldenTestHelpers.SampleJob(),
                Settings = settings,
                SourceMeta = G.Source,
                TargetMeta = G.Target,
                SourceProfile = srcProfile,
                TargetProfile = tgtProfile,
                Source = new ConnectionTarget
                {
                    Host = srcProfile.Host,
                    Port = srcProfile.Port,
                    Service = srcProfile.Service,
                    User = srcProfile.User,
                    Password = "x"
                },
                Target = new ConnectionTarget
                {
                    Host = tgtProfile.Host,
                    Port = tgtProfile.Port,
                    Service = tgtProfile.Service,
                    User = tgtProfile.User,
                    Password = "x"
                },
                SourceTest = new ConnectionTestResult
                {
                    Ok = true,
                    Version = "Oracle 19c",
                    LatencyMs = 31
                },
                TargetTest = new ConnectionTestResult
                {
                    Ok = true,
                    Version = "Oracle 19c",
                    LatencyMs = 18
                }
            };
        }

        private static FakeAdapter Fake(ValidationContext ctx)
        {
            var adapter = new FakeAdapter(ctx.SourceMeta);
            var responder = new ValidationResponder(ctx.SourceMeta, ctx.TargetMeta);
            adapter.QueryResponder = sql => responder.Respond(sql);
            adapter.CountResults[string.Empty] = 0;
            return adapter;
        }
    }
}
