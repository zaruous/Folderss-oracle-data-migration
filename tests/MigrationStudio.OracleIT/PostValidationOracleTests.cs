using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Adapters.Oracle.Engine;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MigrationStudio.Core.Validation;
using Oracle.ManagedDataAccess.Client;
using Xunit;
using Xunit.Abstractions;

namespace MigrationStudio.OracleIT
{
    [Collection(OracleCollection.Name)]
    public sealed class PostValidationOracleTests : OracleTestBase
    {
        private const int RowCount = 3000;

        public PostValidationOracleTests(OracleFixture db, ITestOutputHelper output)
            : base(db, output, "post-validation")
        {
            if (OracleFixture.Enabled)
            {
                V7OracleUsers.Ensure(Db.Dsn);
            }
        }

        [OracleFact]
        public async Task PostValidation_passes_after_insert_only_copy()
        {
            PrepareTables();
            var source = Endpoint(V7OracleUsers.SrcUser, V7OracleUsers.SrcPassword);
            var target = Endpoint(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword);
            var plan = Plan();
            var spec = Spec(source, target, plan);
            var listener = new Listener();
            var engine = new MigrationEngine(spec, new OracleSourceFactory(source), new OracleTargetFactory(target, "MIG_", spec.RunId),
                new OracleCheckpointStore(target, "MIG_"), listener, new SystemRunClock());
            await engine.RunAsync(CancellationToken.None);
            Assert.Equal("done", engine.State);
            Assert.Equal(RowCount, Scalar(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword, "SELECT COUNT(*) FROM POST_TGT"));

            var items = await RunPostAsync(source, target, plan, listener.Final, spec.RunId);
            Log("post items=" + items.Count + " ERROR=" + items.Count(i => i.Level == CheckLevels.Error));
            Assert.DoesNotContain(items, i => i.Level == CheckLevels.Error);
            Assert.Contains(items, i => i.Check == "행 수" && i.Level == CheckLevels.Pass);
            Assert.Contains(items, i => i.Check == "PK 누락" && i.Level == CheckLevels.Pass);
            Assert.Contains(items, i => i.Check == "중복 키" && i.Level == CheckLevels.Pass);
            Assert.Contains(items, i => i.Check == "샘플 데이터" && i.Level == CheckLevels.Pass);
            Assert.Contains(items, i => i.Check == "해시" && i.Level == CheckLevels.Pass);
        }

        [OracleFact]
        public async Task PostValidation_P04_catches_value_mismatch()
        {
            PrepareTables();
            var source = Endpoint(V7OracleUsers.SrcUser, V7OracleUsers.SrcPassword);
            var target = Endpoint(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword);
            var plan = Plan();
            var spec = Spec(source, target, plan);
            var listener = new Listener();
            await new MigrationEngine(spec, new OracleSourceFactory(source), new OracleTargetFactory(target, "MIG_", spec.RunId),
                new OracleCheckpointStore(target, "MIG_"), listener, new SystemRunClock()).RunAsync(CancellationToken.None);
            Execute(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword, "UPDATE POST_TGT SET NAME='TAMPERED'");
            Execute(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword, "COMMIT");

            var items = await RunPostAsync(source, target, plan, listener.Final, spec.RunId);
            var p04 = items.First(i => i.Check == "샘플 데이터");
            Assert.NotEqual(CheckLevels.Pass, p04.Level);
        }

        [OracleFact]
        public async Task PostValidation_P02_catches_missing_key()
        {
            PrepareTables();
            var source = Endpoint(V7OracleUsers.SrcUser, V7OracleUsers.SrcPassword);
            var target = Endpoint(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword);
            var plan = Plan();
            var spec = Spec(source, target, plan);
            var listener = new Listener();
            await new MigrationEngine(spec, new OracleSourceFactory(source), new OracleTargetFactory(target, "MIG_", spec.RunId),
                new OracleCheckpointStore(target, "MIG_"), listener, new SystemRunClock()).RunAsync(CancellationToken.None);
            Execute(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword, "DELETE FROM POST_TGT WHERE ID=1");
            Execute(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword, "COMMIT");
            listener.Final.Tasks[0].Written = RowCount - 1;

            var items = await RunPostAsync(source, target, plan, listener.Final, spec.RunId);
            var p02 = items.First(i => i.Check == "PK 누락");
            Assert.Equal(CheckLevels.Error, p02.Level);
        }

        [OracleFact]
        public async Task PostValidation_P03_catches_duplicate_key()
        {
            PrepareTables();
            var source = Endpoint(V7OracleUsers.SrcUser, V7OracleUsers.SrcPassword);
            var target = Endpoint(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword);
            var plan = Plan();
            var spec = Spec(source, target, plan);
            var listener = new Listener();
            await new MigrationEngine(spec, new OracleSourceFactory(source), new OracleTargetFactory(target, "MIG_", spec.RunId),
                new OracleCheckpointStore(target, "MIG_"), listener, new SystemRunClock()).RunAsync(CancellationToken.None);
            Execute(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword, "ALTER TABLE POST_TGT DISABLE CONSTRAINT POST_TGT_PK");
            Execute(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword,
                "INSERT INTO POST_TGT (ID,NAME,CREATED_AT,AMOUNT) SELECT ID,NAME,CREATED_AT,AMOUNT FROM POST_TGT WHERE ID=2");
            Execute(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword, "COMMIT");

            var items = await RunPostAsync(source, target, plan, listener.Final, spec.RunId);
            var p03 = items.First(i => i.Check == "중복 키");
            Assert.Equal(CheckLevels.Error, p03.Level);
        }

        private async Task<List<PostItem>> RunPostAsync(EndpointSpec source, EndpointSpec target, PlanItem plan, RunSnapshot final, string runId)
        {
            var srcMeta = await LoadMeta(source);
            var tgtMeta = await LoadMeta(target);
            var ctx = new PostValidationContext
            {
                Job = new MigrationJob { Mappings = new List<Mapping> { plan.Mapping } },
                Source = source.Connection,
                Target = target.Connection,
                SourceMeta = srcMeta,
                TargetMeta = tgtMeta,
                Plan = new List<PlanItem> { plan }
            };
            var request = new PostValidationRequest { RunId = runId, Dry = false, Final = final };
            return await new PostValidationEngine(DatabaseAdapters.For("oracle"), ctx).RunPostAsync(request, null, CancellationToken.None);
        }

        private async Task<SchemaMetadata> LoadMeta(EndpointSpec ep)
        {
            return await DatabaseAdapters.For("oracle").LoadMetadataAsync(ep.Connection, ep.Schema, CancellationToken.None);
        }

        private void PrepareTables()
        {
            Execute(V7OracleUsers.SrcUser, V7OracleUsers.SrcPassword, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE POST_SRC PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;");
            Execute(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE POST_TGT PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;");
            Execute(V7OracleUsers.SrcUser, V7OracleUsers.SrcPassword,
                "CREATE TABLE POST_SRC (ID NUMBER(18) PRIMARY KEY, NAME VARCHAR2(60) NOT NULL, CREATED_AT DATE, AMOUNT NUMBER(12,2))");
            Execute(V7OracleUsers.TgtUser, V7OracleUsers.TgtPassword,
                "CREATE TABLE POST_TGT (ID NUMBER(18) CONSTRAINT POST_TGT_PK PRIMARY KEY, NAME VARCHAR2(60) NOT NULL, CREATED_AT DATE, AMOUNT NUMBER(12,2))");
            Execute(V7OracleUsers.SrcUser, V7OracleUsers.SrcPassword,
                "INSERT INTO POST_SRC SELECT LEVEL,'NAME-'||LEVEL,DATE '2020-01-01'+MOD(LEVEL,365),MOD(LEVEL,10)*10 FROM DUAL CONNECT BY LEVEL<=" + RowCount);
            Execute(V7OracleUsers.SrcUser, V7OracleUsers.SrcPassword, "COMMIT");
        }

        private PlanItem Plan()
        {
            var columns = new List<ColumnMetadata>
            {
                new ColumnMetadata { Name = "ID", Type = "NUMBER(18)", Nullable = false, PrimaryKey = true },
                new ColumnMetadata { Name = "NAME", Type = "VARCHAR2(60)", Nullable = false },
                new ColumnMetadata { Name = "CREATED_AT", Type = "DATE" },
                new ColumnMetadata { Name = "AMOUNT", Type = "NUMBER(12,2)" }
            };
            var mapping = new Mapping
            {
                Id = "POST", Use = true, Source = "POST_SRC", Target = "POST_TGT", Mode = WriteModes.InsertOnly,
                CheckpointColumn = "ID", MergeKey = new List<string> { "ID" }
            };
            foreach (var c in columns)
            {
                mapping.Columns.Add(new ColumnMapping { Source = c.Name, Target = c.Name });
            }

            var item = new PlanItem
            {
                Key = "POST",
                Mapping = mapping,
                Label = "POST_SRC → POST_TGT",
                ScopeTotal = RowCount,
                SourceMetadata = new TableMetadata { Name = "POST_SRC", Rows = RowCount, Columns = columns },
                TargetMetadata = new TableMetadata { Name = "POST_TGT", Columns = columns },
                Ranges = new List<KeyRange> { new KeyRange { Rows = RowCount } }
            };
            item.WriteColumns = SqlGenerator.WriteColumns(mapping, item.TargetMetadata);
            return item;
        }

        private RunSpec Spec(EndpointSpec source, EndpointSpec target, PlanItem plan)
        {
            return new RunSpec
            {
                RunId = RunIds.New(DateTime.Now),
                RunMode = "EXECUTE",
                Source = source,
                Target = target,
                CheckpointStore = "TARGET",
                ControlPrefix = "MIG_",
                Plan = new List<PlanItem> { plan },
                RetryDelays = new List<int> { 100, 200, 400 },
                Job = new MigrationJob
                {
                    JobName = "POST_VAL_IT",
                    Strategy = new MigrationStrategy { CommitSize = 2000, FetchSize = 1000, Workers = 1, ErrorPolicy = ErrorPolicies.Retry },
                    Mappings = new List<Mapping> { plan.Mapping }
                }
            };
        }

        private EndpointSpec Endpoint(string user, string password)
        {
            return new EndpointSpec
            {
                Schema = user,
                Connection = new ConnectionTarget { Host = Db.Host, Port = Db.Port, Service = Db.Service, User = user, Password = password }
            };
        }

        private void Execute(string user, string password, string sql)
        {
            using (var connection = new OracleConnection(Connection(user, password)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandTimeout = 0;
                    command.ExecuteNonQuery();
                }
            }
        }

        private long Scalar(string user, string password, string sql)
        {
            using (var connection = new OracleConnection(Connection(user, password)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
        }

        private string Connection(string user, string password)
        {
            return new OracleConnectionStringBuilder { DataSource = Db.Dsn, UserID = user, Password = password, Pooling = false }.ConnectionString;
        }

        private sealed class Listener : IRunListener
        {
            public RunSnapshot Final;
            public void OnSnapshot(RunSnapshot snapshot) { }
            public void OnLog(LogEntry entry) { }
            public void OnCheckpoint(CheckpointRecord record) { }
            public void OnEnd(RunSnapshot final) { Final = final; }
        }
    }
}
