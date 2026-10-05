using System.Linq;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MigrationStudio.Tests.Golden;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class SqlSourceBindsTests
    {
        [Fact]
        public void Sync_adds_new_binds_with_type_guess()
        {
            var job = GoldenTestHelpers.SampleJob();
            var m = GoldenTestHelpers.SqlSampleMapping(job);
            m.Binds = new System.Collections.Generic.List<BindParameter>();
            var names = SqlSourceBinds.Sync(m);
            Assert.Contains("LAST_ID", names);
            var last = m.Binds.First(b => b.Name == "LAST_ID");
            Assert.Equal("NUMBER", last.Type);
            Assert.True(last.FromCheckpoint);
        }

        [Fact]
        public void Sync_does_not_remove_stale_binds()
        {
            var m = new Mapping
            {
                SourceType = SourceTypes.Sql,
                Sql = "SELECT 1 AS X FROM DUAL WHERE X = :ONLY"
            };
            m.Binds.Add(new BindParameter { Name = "GONE", Type = "VARCHAR2", Value = "a" });
            SqlSourceBinds.Sync(m);
            Assert.Contains(m.Binds, b => b.Name == "GONE");
        }
    }
}
