using System.Collections.Generic;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Sql;
using MigrationStudio.Tests.Golden;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class SqlSourceServiceTests
    {
        [Fact]
        public void VirtualTable_uses_described_columns()
        {
            var golden = GoldenFixture.Document;
            var job = GoldenTestHelpers.SampleJob();
            var sqlMap = GoldenTestHelpers.SqlSampleMapping(job);
            var described = new List<QueryColumn>
            {
                new QueryColumn { Name = "MEMBER_ID", Type = "NUMBER(12)", Nullable = false },
                new QueryColumn { Name = "MEMBER_NAME", Type = "VARCHAR2(100)", Nullable = true }
            };
            var table = SqlSourceService.VirtualTable(sqlMap, described, golden.Source);
            Assert.Equal("SQL", table.Kind);
            Assert.Equal(2, table.Columns.Count);
            Assert.Equal("NUMBER(12)", table.FindColumn("MEMBER_ID").Type);
        }

        [Fact]
        public void Validate_overload_with_null_matches_golden_validate()
        {
            var golden = GoldenFixture.Document;
            var job = GoldenTestHelpers.SampleJob();
            var sqlMap = GoldenTestHelpers.SqlSampleMapping(job);
            var a = SqlSourceAnalyzer.Validate(sqlMap, golden.Source, golden.Target);
            var b = SqlSourceAnalyzer.Validate(sqlMap, golden.Source, golden.Target, null);
            Assert.Equal(a.Level, b.Level);
            Assert.Equal(a.Columns.Count, b.Columns.Count);
        }
    }
}
