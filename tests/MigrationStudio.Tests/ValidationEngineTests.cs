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
