using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Model;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class SqlBindTests
    {
        [Fact]
        public void From_copies_bind_parameter()
        {
            var b = SqlBind.From(new BindParameter { Name = "LAST_ID", Type = "NUMBER", Value = "0" });
            Assert.Equal("LAST_ID", b.Name);
            Assert.Equal("NUMBER", b.Type);
            Assert.Equal("0", b.Value);
        }
    }
}
