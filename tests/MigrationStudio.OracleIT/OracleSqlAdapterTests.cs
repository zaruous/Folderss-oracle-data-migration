using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace MigrationStudio.OracleIT
{
    [Collection(OracleCollection.Name)]
    public sealed class OracleSqlAdapterTests : OracleTestBase
    {
        public OracleSqlAdapterTests(OracleFixture db, ITestOutputHelper output)
            : base(db, output, "sql-adapter")
        {
        }

        private ConnectionTarget Src()
        {
            return new ConnectionTarget
            {
                Host = Db.Host,
                Port = Db.Port,
                Service = Db.Service,
                User = OracleFixture.SrcUser,
                Password = OracleFixture.SrcPassword
            };
        }

        [OracleFact]
        public async Task Parse_ok_and_missing_table()
        {
            var adapter = DatabaseAdapters.For("oracle");
            var ok = await adapter.ParseSqlAsync(
                Src(),
                OracleFixture.SrcUser,
                "SELECT CUSTOMER_ID FROM SRC_CUSTOMER",
                CancellationToken.None);
            Assert.True(ok.Ok);

            var bad = await adapter.ParseSqlAsync(
                Src(),
                OracleFixture.SrcUser,
                "SELECT X FROM NO_SUCH_TABLE_ZZZ",
                CancellationToken.None);
            Assert.False(bad.Ok);
            Assert.Equal("ORA-00942", bad.ErrorCode);
        }

        [OracleFact]
        public async Task Parse_rejects_drop_before_connect()
        {
            var adapter = DatabaseAdapters.For("oracle");
            await Assert.ThrowsAsync<AdapterException>(() =>
                adapter.ParseSqlAsync(Src(), OracleFixture.SrcUser, "DROP TABLE SRC_CUSTOMER", CancellationToken.None));
            var ok = await adapter.ParseSqlAsync(
                Src(),
                OracleFixture.SrcUser,
                "SELECT COUNT(*) FROM SRC_CUSTOMER",
                CancellationToken.None);
            Assert.True(ok.Ok);
        }

        [OracleFact]
        public async Task Describe_returns_columns_and_count_star_name()
        {
            var adapter = DatabaseAdapters.For("oracle");
            var cols = await adapter.DescribeSqlAsync(
                Src(),
                OracleFixture.SrcUser,
                "SELECT CUSTOMER_ID, COUNT(*) AS CNT FROM SRC_CUSTOMER GROUP BY CUSTOMER_ID",
                CancellationToken.None);
            Assert.Contains(cols, c => c.Name == "CUSTOMER_ID");
            Assert.Contains(cols, c => c.Name == "CNT" || c.Name.Contains("COUNT"));
        }

        [OracleFact]
        public async Task Query_respects_max_rows_and_null()
        {
            var adapter = DatabaseAdapters.For("oracle");
            var result = await adapter.QueryAsync(
                Src(),
                OracleFixture.SrcUser,
                "SELECT NULL AS N, CUSTOMER_ID, REG_DT FROM SRC_CUSTOMER ORDER BY CUSTOMER_ID",
                new List<SqlBind>(),
                0,
                CancellationToken.None);
            Assert.Equal(3, result.Columns.Count);
            Assert.Single(result.Rows);
            Assert.True(result.HasMore);
            Assert.Null(result.Rows[0][0]);
        }

        [OracleFact]
        public async Task Count_returns_row_total()
        {
            var adapter = DatabaseAdapters.For("oracle");
            var count = await adapter.CountAsync(
                Src(),
                OracleFixture.SrcUser,
                "SELECT CUSTOMER_ID FROM SRC_CUSTOMER",
                new List<SqlBind>(),
                CancellationToken.None);
            Assert.True(count > 0);
        }
    }
}
