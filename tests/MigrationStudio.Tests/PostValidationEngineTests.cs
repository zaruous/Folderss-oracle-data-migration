using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Validation;
using MigrationStudio.Testing.Adapters;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class PostValidationEngineTests
    {
        [Fact]
        public async Task RunPost_dry_emits_skip_row_count_only()
        {
            var ctx = SampleContext();
            var request = new PostValidationRequest
            {
                RunId = "R-DRY",
                Dry = true,
                Final = new RunSnapshot
                {
                    Tasks = new List<TaskSnapshot>
                    {
                        new TaskSnapshot { Key = "M1", Label = "A → B", Status = "done", Total = 100 }
                    }
                }
            };
            var items = await Engine(ctx).RunPostAsync(request, null, CancellationToken.None);
            Assert.Single(items);
            Assert.Equal(CheckLevels.Skip, items[0].Level);
            Assert.Equal("행 수", items[0].Check);
        }

        [Fact]
        public async Task RunPost_stopped_emits_warn_row_count_only()
        {
            var ctx = SampleContext();
            var request = new PostValidationRequest
            {
                RunId = "R-STOP",
                Dry = false,
                Final = new RunSnapshot
                {
                    Tasks = new List<TaskSnapshot>
                    {
                        new TaskSnapshot { Key = "M1", Label = "A → B", Status = "stopped", Total = 100, Written = 40, Rejected = 2 }
                    }
                }
            };
            var items = await Engine(ctx).RunPostAsync(request, null, CancellationToken.None);
            Assert.Single(items);
            Assert.Equal(CheckLevels.Warn, items[0].Level);
            Assert.Equal("행 수", items[0].Check);
        }

        [Fact]
        public async Task RunPost_done_emits_p01_through_p06_with_fake_adapter()
        {
            var ctx = SampleContext();
            var request = DoneRequest(100, 100, 0);
            var items = await Engine(ctx, 100, 100, 0).RunPostAsync(request, null, CancellationToken.None);
            Assert.Contains(items, i => i.Check == "행 수" && i.Level == CheckLevels.Pass);
            Assert.Contains(items, i => i.Check == "PK 누락" && i.Level == CheckLevels.Pass);
            Assert.Contains(items, i => i.Check == "중복 키" && i.Level == CheckLevels.Pass);
            Assert.Contains(items, i => i.Check == "샘플 데이터");
            Assert.Contains(items, i => i.Check == "해시" && i.Level == CheckLevels.Pass);
        }

        [Fact]
        public async Task RunPost_mismatch_row_count_is_error()
        {
            var ctx = SampleContext();
            var request = DoneRequest(100, 80, 0);
            var items = await Engine(ctx, 100, 80, 0).RunPostAsync(request, null, CancellationToken.None);
            var p01 = items.First(i => i.Check == "행 수");
            Assert.Equal(CheckLevels.Error, p01.Level);
        }

        private static PostValidationRequest DoneRequest(long total, long written, long rejected)
        {
            return new PostValidationRequest
            {
                RunId = "R-1",
                Dry = false,
                Final = new RunSnapshot
                {
                    Tasks = new List<TaskSnapshot>
                    {
                        new TaskSnapshot
                        {
                            Key = "M1", Label = "SRC → TGT", Status = "done", Total = total,
                            Written = written, Rejected = rejected, Inserted = written
                        }
                    }
                }
            };
        }

        private static PostValidationEngine Engine(PostValidationContext ctx, long scope = 100, long written = 100, long rejected = 0)
        {
            var adapter = new FakeAdapter(ctx.SourceMeta);
            var responder = new PostValidationResponder(scope, written, rejected);
            adapter.QueryResponder = sql => responder.Respond(sql);
            adapter.CountResults[string.Empty] = scope;
            return new PostValidationEngine(adapter, ctx);
        }

        private static PostValidationContext SampleContext()
        {
            var columns = new List<ColumnMetadata>
            {
                new ColumnMetadata { Name = "ID", Type = "NUMBER(18)", Nullable = false, PrimaryKey = true },
                new ColumnMetadata { Name = "NAME", Type = "VARCHAR2(60)", Nullable = true }
            };
            var mapping = new Mapping
            {
                Id = "M1",
                Use = true,
                Source = "SRC",
                Target = "TGT",
                Mode = WriteModes.InsertOnly,
                MergeKey = new List<string> { "ID" },
                Columns = new List<ColumnMapping>
                {
                    new ColumnMapping { Source = "ID", Target = "ID" },
                    new ColumnMapping { Source = "NAME", Target = "NAME" }
                }
            };
            var plan = new PlanItem
            {
                Key = "M1",
                Mapping = mapping,
                Label = "SRC → TGT",
                ScopeTotal = 100,
                SourceMetadata = new TableMetadata { Name = "SRC", Rows = 100, Columns = columns },
                TargetMetadata = new TableMetadata { Name = "TGT", Columns = columns },
                WriteColumns = MigrationStudio.Core.Sql.SqlGenerator.WriteColumns(mapping, new TableMetadata { Name = "TGT", Columns = columns })
            };
            return new PostValidationContext
            {
                Job = new MigrationJob { Mappings = new List<Mapping> { mapping } },
                Source = new ConnectionTarget { Host = "h", Port = 1521, Service = "x", User = "s", Password = "p" },
                Target = new ConnectionTarget { Host = "h", Port = 1521, Service = "x", User = "t", Password = "p" },
                SourceMeta = new SchemaMetadata { Schema = "SRC_SCHEMA", Tables = new List<TableMetadata> { plan.SourceMetadata } },
                TargetMeta = new SchemaMetadata { Schema = "TGT_SCHEMA", Tables = new List<TableMetadata> { plan.TargetMetadata } },
                Plan = new List<PlanItem> { plan }
            };
        }
    }
}
