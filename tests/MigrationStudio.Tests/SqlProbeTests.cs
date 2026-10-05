using System.Collections.Generic;
using System.Linq;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MigrationStudio.Tests.Golden;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class SqlProbeTests
    {
        [Fact]
        public void TableCount_uses_parallel_hint()
        {
            var sql = SqlProbe.TableCount("LEGACY_APP", "SRC_CUSTOMER");
            Assert.Equal("SELECT /*+ PARALLEL(4) */ COUNT(*) FROM LEGACY_APP.SRC_CUSTOMER", sql);
        }

        [Fact]
        public void SamplePreview_table_with_checkpoint_includes_order_by()
        {
            var job = GoldenTestHelpers.SampleJob();
            var mapping = GoldenTestHelpers.TableMappings(job).First(m => m.Source == "SRC_CUSTOMER");
            var sql = SqlProbe.SamplePreview(
                mapping,
                "LEGACY_APP",
                new List<string> { "PHONE_NO" },
                "REGEXP_REPLACE(PHONE_NO, '[^0-9]', '')",
                6);
            Assert.Contains("FROM LEGACY_APP.SRC_CUSTOMER", sql);
            Assert.Contains("CUSTOMER_ID > :LAST_ID", sql);
            Assert.Contains("ORDER BY CUSTOMER_ID", sql);
            Assert.Contains("FETCH FIRST 6 ROWS ONLY", sql);
            Assert.Contains("AS SRC_1", sql);
            Assert.Contains("AS RESULT", sql);
        }

        [Fact]
        public void SqlSourcePreview_wraps_sql_mapping()
        {
            var job = GoldenTestHelpers.SampleJob();
            var sqlMap = GoldenTestHelpers.SqlSampleMapping(job);
            var sql = SqlProbe.SqlSourcePreview(sqlMap, 100);
            Assert.Contains("SELECT * FROM (", sql);
            Assert.Contains(") S", sql);
            Assert.Contains("FETCH FIRST 100 ROWS ONLY", sql);
        }

        [Fact]
        public void ExpressionCheck_sql_source_uses_inline_view()
        {
            var job = GoldenTestHelpers.SampleJob();
            var sqlMap = GoldenTestHelpers.SqlSampleMapping(job);
            var sql = SqlProbe.ExpressionCheck(sqlMap, "LEGACY_APP", "MEMBER_ID");
            Assert.Contains("FROM (", sql);
            Assert.Contains(") S", sql);
            Assert.Contains("WHERE 1 = 0", sql);
        }

        [Fact]
        public void Invalid_identifier_throws()
        {
            Assert.Throws<System.ArgumentException>(() => SqlProbe.TableCount("bad schema", "T"));
        }
    }
}
