using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Settings;
using Xunit;
using Xunit.Abstractions;

namespace MigrationStudio.OracleIT
{
    [Collection(OracleCollection.Name)]
    public sealed class OracleAdapterTests : OracleTestBase
    {
        public OracleAdapterTests(OracleFixture db, ITestOutputHelper output)
            : base(db, output, "adapter")
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

        private ConnectionTarget ForUser(string user, string password)
        {
            return new ConnectionTarget
            {
                Host = Db.Host,
                Port = Db.Port,
                Service = Db.Service,
                User = user,
                Password = password
            };
        }

        [OracleFact]
        public async Task Connection_test_success_and_readonly()
        {
            var adapter = DatabaseAdapters.For("oracle");
            long ms = 0;
            var result = await Timed(async () => await adapter.TestAsync(Src(), true, CancellationToken.None), x => ms = x);
            Log("접속 TestAsync " + ms + "ms, LatencyMs=" + result.LatencyMs);
            Assert.True(result.Ok);
            Assert.Equal("Oracle 12c", result.Version);
            Assert.NotNull(result.LatencyMs);
            Assert.Equal(OracleFixture.SrcUser, result.CurrentSchema);
            Assert.NotNull(result.Nls);
            Assert.NotNull(result.Nls.CharacterSet);
        }

        [OracleFact]
        public async Task Connection_failures_and_special_password()
        {
            var adapter = DatabaseAdapters.For("oracle");
            var badPw = ForUser(OracleFixture.SrcUser, "wrong");
            var r1 = await adapter.TestAsync(badPw, false, CancellationToken.None);
            Assert.False(r1.Ok);
            Assert.Equal("ORA-01017", r1.ErrorCode);

            var badService = new ConnectionTarget
            {
                Host = Db.Host,
                Port = Db.Port,
                Service = "no_such_service",
                User = OracleFixture.SrcUser,
                Password = OracleFixture.SrcPassword
            };
            var r2 = await adapter.TestAsync(badService, false, CancellationToken.None);
            Assert.False(r2.Ok);
            Assert.Equal("ORA-12514", r2.ErrorCode);

            var badPort = new ConnectionTarget
            {
                Host = Db.Host,
                Port = 1,
                Service = Db.Service,
                User = OracleFixture.SrcUser,
                Password = OracleFixture.SrcPassword
            };
            var r3 = await adapter.TestAsync(badPort, false, CancellationToken.None);
            Assert.False(r3.Ok);
            Log("닫힌 포트 ErrorCode=" + (r3.ErrorCode ?? "(없음)"));

            var special = ForUser(OracleFixture.SpecialUser, OracleFixture.SpecialPassword);
            var r4 = await adapter.TestAsync(special, false, CancellationToken.None);
            Assert.True(r4.Ok);
        }

        [OracleFact]
        public async Task Metadata_loads_tables_views_constraints()
        {
            var adapter = DatabaseAdapters.For("oracle");
            OracleDatabaseAdapter.Trace = (name, qms) => Log("  query " + name + " " + qms + "ms");
            long ms = 0;
            var meta = await Timed(async () => await adapter.LoadMetadataAsync(Src(), OracleFixture.SrcUser, CancellationToken.None), x => ms = x);
            OracleDatabaseAdapter.Trace = null;
            Log("메타데이터 LoadMetadataAsync " + ms + "ms, ElapsedMs=" + meta.ElapsedMs);
            Assert.Equal(OracleFixture.SrcUser, meta.Schema);
            // 다른 시험(엔진·검증 등)이 같은 스키마에 만든 표가 남아 있을 수 있으니, 이 시험이 만든 표 3개가 들어 있는지만 본다
            foreach (var expected in new[] { "SRC_CUSTOMER", "SRC_CUSTOMER_GRADE", "SRC_ORDER" })
            {
                Assert.Contains(meta.Tables, t => t.Kind == "TABLE" && t.Name == expected);
            }
            Assert.Equal(1, meta.Tables.Count(t => t.Kind == "VIEW"));

            var customer = meta.FindTable("SRC_CUSTOMER");
            Assert.NotNull(customer);
            Assert.Equal("고객", customer.Comment);
            var id = customer.FindColumn("CUSTOMER_ID");
            Assert.True(id.PrimaryKey);
            Assert.Equal("NUMBER(12)", id.Type);
            Assert.Equal("고객 번호", id.Comment);
            Assert.Equal("VARCHAR2(30)", customer.FindColumn("PHONE_NO").Type);
            Assert.Equal("CHAR(1)", customer.FindColumn("STATUS_CD").Type);
            Assert.Equal("'Y'", customer.FindColumn("STATUS_CD").DefaultValue);
            Assert.Equal("VARCHAR2(20 CHAR)", customer.FindColumn("NOTE").Type);
            Assert.Equal("TIMESTAMP(6) WITH TIME ZONE", customer.FindColumn("EVENT_TS").Type);
            Assert.NotNull(id.Stats.Nulls);

            var order = meta.FindTable("SRC_ORDER");
            Assert.Single(order.ForeignKeys);
            Assert.Equal("SRC_CUSTOMER", order.ForeignKeys[0].RefTable);
        }

        [OracleFact]
        public async Task Metadata_other_schema_empty()
        {
            var adapter = DatabaseAdapters.For("oracle");
            var meta = await adapter.LoadMetadataAsync(Src(), OracleFixture.OtherUser, CancellationToken.None);
            Assert.Empty(meta.Tables);
        }

        [OracleFact]
        public async Task Control_store_auto_resolves()
        {
            var adapter = DatabaseAdapters.For("oracle");
            var tgt = ForUser(OracleFixture.TgtUser, OracleFixture.TgtPassword);
            var r1 = await adapter.CheckControlStoreAsync(tgt, OracleFixture.TgtUser, "MIG_", CheckpointStores.Auto, CancellationToken.None);
            Assert.Equal(CheckpointStores.Target, r1.Resolved);
            Assert.True(r1.CanCreate);

            var ro = ForUser(OracleFixture.RoUser, OracleFixture.RoPassword);
            var r2 = await adapter.CheckControlStoreAsync(ro, OracleFixture.RoUser, "MIG_", CheckpointStores.Auto, CancellationToken.None);
            Assert.Equal(CheckpointStores.Local, r2.Resolved);
            Assert.False(r2.CanCreate);
        }

        [OracleFact]
        public async Task Metadata_cancel_throws()
        {
            var adapter = DatabaseAdapters.For("oracle");
            var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                adapter.LoadMetadataAsync(Src(), OracleFixture.SrcUser, cts.Token));
        }

        private static async Task<T> Timed<T>(Func<Task<T>> work, Action<long> elapsedMs)
        {
            var w = System.Diagnostics.Stopwatch.StartNew();
            var r = await work();
            elapsedMs(w.ElapsedMilliseconds);
            return r;
        }
    }
}
