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
        public void VirtualTable_is_not_served_from_an_earlier_describe_of_the_same_sql()
        {
            // 같은 SQL을 빈 DESCRIBE로 먼저 분석해도 다음 DESCRIBE 결과가 반영돼야 한다 — 캐시 키에 열 목록이 빠져 있던 회귀
            var golden = GoldenFixture.Document;
            var sqlMap = GoldenTestHelpers.SqlSampleMapping(GoldenTestHelpers.SampleJob());
            sqlMap.Sql += "\n-- describe-cache " + System.Guid.NewGuid().ToString("N");
            var empty = SqlSourceService.VirtualTable(sqlMap, new List<QueryColumn>(), golden.Source);
            var described = SqlSourceService.VirtualTable(sqlMap, new List<QueryColumn>
            {
                new QueryColumn { Name = "MEMBER_ID", Type = "NUMBER(12)", Nullable = false }
            }, golden.Source);
            Assert.Empty(empty.Columns);
            Assert.Single(described.Columns);
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
