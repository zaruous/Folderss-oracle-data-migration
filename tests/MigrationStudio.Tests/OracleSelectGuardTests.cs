using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class OracleSelectGuardTests
    {
        [Theory]
        [InlineData("DROP TABLE X")]
        [InlineData("SELECT 1; DROP TABLE X")]
        [InlineData("-- comment\nDELETE FROM T")]
        public void Rejects_non_select(string sql)
        {
            var ex = Assert.Throws<AdapterException>(() => OracleSelectGuard.EnsureSelectOnly(sql));
            Assert.Contains(OracleSelectGuard.SelectOnlyMessage, ex.Message);
        }

        [Theory]
        [InlineData("SELECT 1 FROM DUAL")]
        [InlineData("  \nWITH X AS (SELECT 1 A FROM DUAL) SELECT * FROM X")]
        [InlineData("select 1 from dual;")]
        public void Allows_select_and_with(string sql)
        {
            OracleSelectGuard.EnsureSelectOnly(sql);
        }

        [Fact]
        public void Schema_name_validation()
        {
            Assert.Throws<AdapterException>(() => OracleSelectGuard.ValidateSchemaName("LEGACY;APP"));
            Assert.Equal("LEGACY_APP", OracleSelectGuard.ValidateSchemaName("legacy_app"));
        }
    }
}
