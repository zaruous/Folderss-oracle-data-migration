using System.Linq;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Model;
using MigrationStudio.Tests.Golden;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class MappingFactoryTests
    {
        [Fact]
        public void StarterSql_matches_poc_shape_for_customer()
        {
            var golden = GoldenFixture.Document;
            var src = golden.Source;
            var start = MappingFactory.StarterSql("SRC_CUSTOMER", src);
            Assert.Contains("T.CUSTOMER_ID AS CUSTOMER_ID", start.Sql);
            Assert.Contains("FROM SRC_CUSTOMER T", start.Sql);
            Assert.Contains("WHERE T.CUSTOMER_ID > :LAST_ID", start.Sql);
            Assert.Single(start.Binds);
            Assert.Equal("LAST_ID", start.Binds[0].Name);
            Assert.True(start.Binds[0].FromCheckpoint);
            Assert.Equal("CUSTOMER_ID", start.CheckpointColumn);
        }

        [Fact]
        public void CreateTableMapping_auto_maps_columns()
        {
            var golden = GoldenFixture.Document;
            var m = MappingFactory.CreateTableMapping(golden.Source, golden.Target, "SRC_CUSTOMER", "TB_MEMBER", WriteModes.Merge);
            Assert.Equal(SourceTypes.Table, m.SourceType);
            Assert.Equal("SRC_CUSTOMER", m.Source);
            Assert.Equal("TB_MEMBER", m.Target);
            Assert.Equal("CUSTOMER_ID", m.CheckpointColumn);
            Assert.Contains("MEMBER_ID", m.MergeKey);
            Assert.True(m.Columns.Count > 0);
            Assert.NotNull(m.Columns.FirstOrDefault(c => c.Target == "MEMBER_ID"));
        }

        [Fact]
        public void NormalizeSqlName_replaces_invalid_chars()
        {
            Assert.Equal("SQLMAP_1", MappingFactory.NormalizeSqlName("sqlmap-1"));
        }
    }
}
