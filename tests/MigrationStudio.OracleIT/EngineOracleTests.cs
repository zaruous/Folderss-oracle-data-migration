using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle.Engine;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using Oracle.ManagedDataAccess.Client;
using Xunit;
using Xunit.Abstractions;

namespace MigrationStudio.OracleIT
{
    [Collection(OracleCollection.Name)]
    public sealed class EngineOracleTests : OracleTestBase
    {
        public EngineOracleTests(OracleFixture db, ITestOutputHelper output)
            : base(db, output, "engine")
        {
        }

        [OracleFact]
        public async Task Engine_copies_200000_rows_with_four_workers_and_merges_again()
        {
            PrepareTables();
            var source = Endpoint(OracleFixture.SrcUser, OracleFixture.SrcPassword);
            var target = Endpoint(OracleFixture.TgtUser, OracleFixture.TgtPassword);
            var control = new OracleControlStore(target, "MIG_");
            await control.EnsureControlTablesAsync(CancellationToken.None);
            var store = new OracleCheckpointStore(target, "MIG_");
            var plan = Plan(WriteModes.InsertOnly);
            Log("source sql=" + OracleSourceFactory.BuildSql(plan, 5000, OracleFixture.SrcUser));
            var spec = Spec(source, target, plan, WriteModes.InsertOnly);
            var listener = new Listener();
            var sourceFactory = new OracleSourceFactory(source);
            var targetFactory = new OracleTargetFactory(target, "MIG_", spec.RunId);
            using (var diagnosticReader = await sourceFactory.OpenAsync(plan, plan.Ranges[0], null, 1, CancellationToken.None))
            {
                var diagnosticRows = await diagnosticReader.ReadAsync(1, CancellationToken.None);
                Log("source diagnostic rows=" + diagnosticRows.Count);
            }
            var engine = new MigrationEngine(spec, sourceFactory, targetFactory, store, listener, new SystemRunClock());

            GC.Collect();
            var memoryBefore = GC.GetTotalMemory(true);
            var process = Process.GetCurrentProcess();
            var workingBefore = process.WorkingSet64;
            var watch = Stopwatch.StartNew();
            var run = engine.RunAsync(CancellationToken.None);
            var peakWorkingSet = workingBefore;
            var peakManaged = memoryBefore;
            while (!run.IsCompleted)
            {
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
                await Task.Delay(50);
            }
            await run;
            watch.Stop();
            Log("engine state=" + engine.State + " · " + string.Join(" | ", listener.Logs.Select(l => "[" + l.Tag + "] " + l.Text)));
            Assert.Equal("done", engine.State);
            process.Refresh();
            var memoryAfter = GC.GetTotalMemory(false);
            var count = Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, "SELECT COUNT(*) FROM BIG_TGT");
            var sourceHash = Scalar(OracleFixture.SrcUser, OracleFixture.SrcPassword, Checksum("BIG_SRC"));
            var targetHash = Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, Checksum("BIG_TGT"));
            var rate = count / Math.Max(0.001, watch.Elapsed.TotalSeconds);
            Log("INSERT_ONLY rows=" + count + ", elapsed=" + watch.Elapsed.TotalSeconds.ToString("F3") + "s, rate=" + rate.ToString("F0") +
                " rows/s, GC delta=" + (memoryAfter - memoryBefore) + " bytes, GC peak delta=" + (peakManaged - memoryBefore) +
                " bytes, working-set delta=" + (process.WorkingSet64 - workingBefore) + " bytes, working-set peak delta=" + (peakWorkingSet - workingBefore) + " bytes");
            Assert.Equal(200000, count);
            Assert.Equal(sourceHash, targetHash);

            plan = Plan(WriteModes.Merge);
            spec = Spec(source, target, plan, WriteModes.Merge);
            listener = new Listener();
            engine = new MigrationEngine(spec, new OracleSourceFactory(source), new OracleTargetFactory(target, "MIG_", spec.RunId), store, listener, new SystemRunClock());
            watch.Restart();
            await engine.RunAsync(CancellationToken.None);
            watch.Stop();
            Log("MERGE rows=200000, elapsed=" + watch.Elapsed.TotalSeconds.ToString("F3") + "s, Updated=" + listener.Final.Tasks[0].Updated);
            Assert.Equal("done", engine.State);
            Assert.Equal(200000, listener.Final.Tasks[0].Updated);
            Assert.Equal(sourceHash, Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, Checksum("BIG_TGT")));
            Assert.Equal(4, (await store.ListAsync(spec.Job.JobName, CancellationToken.None)).Count);
        }

        [OracleFact]
        public async Task Engine_stops_and_resumes_with_target_and_local_checkpoints()
        {
            PrepareTables();
            var source = Endpoint(OracleFixture.SrcUser, OracleFixture.SrcPassword);
            var target = Endpoint(OracleFixture.TgtUser, OracleFixture.TgtPassword);
            await RunStopResumeAsync(source, target, "TARGET", new OracleCheckpointStore(target, "MIG_"), "ORACLE_RESUME_TARGET");

            Execute(OracleFixture.TgtUser, OracleFixture.TgtPassword, "TRUNCATE TABLE BIG_TGT");
            var directory = Path.Combine(Path.GetTempPath(), "migration-oracle-it-" + Guid.NewGuid().ToString("N"));
            try
            {
                await RunStopResumeAsync(source, target, "LOCAL", new LocalCheckpointStore(directory), "ORACLE_RESUME_LOCAL");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [OracleFact]
        public async Task Engine_handles_reject_stop_sql_pause_and_dry_run()
        {
            PrepareTables();
            PrepareStrictTables();
            var source = Endpoint(OracleFixture.SrcUser, OracleFixture.SrcPassword);
            var target = Endpoint(OracleFixture.TgtUser, OracleFixture.TgtPassword);
            var store = new OracleCheckpointStore(target, "MIG_");

            var strict = StrictPlan(ErrorPolicies.Continue);
            var spec = Spec(source, target, strict, "STRICT_CONTINUE", "EXECUTE", "TARGET", ErrorPolicies.Continue);
            var listener = new Listener();
            var engine = new MigrationEngine(spec, new OracleSourceFactory(source), new OracleTargetFactory(target, "MIG_", spec.RunId), store, listener, new SystemRunClock());
            await engine.RunAsync(CancellationToken.None);
            Log("STRICT CONTINUE state=" + engine.State + " · " + string.Join(" | ", listener.Logs.Select(l => "[" + l.Tag + "] " + l.Text)));
            var rejected = Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword,
                "SELECT COUNT(*) FROM ERR$_TGT_STRICT WHERE ORA_ERR_TAG$='" + spec.RunId + "'");
            Assert.Equal("done", engine.State);
            Assert.Equal(50, rejected);
            Assert.Equal(rejected, listener.Final.Tasks[0].Rejected);

            Execute(OracleFixture.TgtUser, OracleFixture.TgtPassword, "TRUNCATE TABLE TGT_STRICT");
            strict = StrictPlan(ErrorPolicies.Stop);
            spec = Spec(source, target, strict, "STRICT_STOP", "EXECUTE", "TARGET", ErrorPolicies.Stop);
            listener = new Listener();
            engine = new MigrationEngine(spec, new OracleSourceFactory(source), new OracleTargetFactory(target, "MIG_", spec.RunId), store, listener, new SystemRunClock());
            await engine.RunAsync(CancellationToken.None);
            Assert.Equal("failed", engine.State);
            Assert.Equal(0, Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, "SELECT COUNT(*) FROM TGT_STRICT"));

            Execute(OracleFixture.TgtUser, OracleFixture.TgtPassword, "TRUNCATE TABLE BIG_TGT");
            var sqlPlan = Plan(WriteModes.InsertOnly, 1);
            sqlPlan.Mapping.SourceType = SourceTypes.Sql;
            sqlPlan.Mapping.Source = "BIG_JOIN";
            sqlPlan.Mapping.Sql = "SELECT B.ID,B.NAME,B.CREATED_AT,B.EVENT_TS,B.AMOUNT,B.NOTE FROM BIG_SRC B JOIN BIG_SRC X ON X.ID=B.ID WHERE B.ID<=20000";
            sqlPlan.ScopeTotal = 20000;
            sqlPlan.Ranges[0].Rows = 20000;
            spec = Spec(source, target, sqlPlan, "SQL_JOIN", "EXECUTE", "TARGET", ErrorPolicies.Retry);
            listener = new Listener();
            engine = new MigrationEngine(spec, new OracleSourceFactory(source), new OracleTargetFactory(target, "MIG_", spec.RunId), store, listener, new SystemRunClock());
            await engine.RunAsync(CancellationToken.None);
            Assert.Equal("done", engine.State);
            Assert.Equal(20000, Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, "SELECT COUNT(*) FROM BIG_TGT"));

            sqlPlan = Plan(WriteModes.Merge, 1);
            sqlPlan.ScopeTotal = 200000;
            spec = Spec(source, target, sqlPlan, "PAUSE", "EXECUTE", "TARGET", ErrorPolicies.Retry);
            listener = new Listener();
            engine = new MigrationEngine(spec, new OracleSourceFactory(source), new OracleTargetFactory(target, "MIG_", spec.RunId), store, listener, new SystemRunClock());
            var pauseRequested = false;
            listener.CheckpointAction = r =>
            {
                if (!pauseRequested && r.RowsDone >= 10000 && engine.State == "running")
                {
                    pauseRequested = true;
                    engine.Pause();
                }
            };
            var pausedRun = engine.RunAsync(CancellationToken.None);
            await WaitUntilAsync(() => engine.State == "paused", 10000);
            Assert.Equal("paused", engine.State);
            engine.Resume();
            await pausedRun;
            Assert.Equal("done", engine.State);

            var beforeDry = Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, "SELECT COUNT(*) FROM BIG_TGT");
            sqlPlan = Plan(WriteModes.Merge, 1);
            sqlPlan.TargetRowsBefore = beforeDry;
            spec = Spec(source, target, sqlPlan, "DRY", "DRY", "TARGET", ErrorPolicies.Retry);
            listener = new Listener();
            engine = new MigrationEngine(spec, new OracleSourceFactory(source), new OracleTargetFactory(target, "MIG_", spec.RunId), store, listener, new SystemRunClock());
            await engine.RunAsync(CancellationToken.None);
            Assert.Equal("done", engine.State);
            Assert.Equal(beforeDry, Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, "SELECT COUNT(*) FROM BIG_TGT"));
            Assert.Equal(200000, listener.Final.Tasks[0].Updated);
        }

        [OracleFact]
        public async Task Engine_runs_with_local_store_through_read_only_login()
        {
            PrepareTables();
            Execute("SYSTEM", Environment.GetEnvironmentVariable("ORACLE_IT_SYS_PW") ?? "oracle",
                "GRANT SELECT,INSERT,UPDATE,DELETE ON " + OracleFixture.TgtUser + ".BIG_TGT TO " + OracleFixture.RoUser);
            var source = Endpoint(OracleFixture.SrcUser, OracleFixture.SrcPassword);
            var target = Endpoint(OracleFixture.RoUser, OracleFixture.RoPassword);
            target.Schema = OracleFixture.TgtUser;
            var plan = Plan(WriteModes.InsertOnly, 1);
            plan.Mapping.Where = "ID <= 20000";
            plan.ScopeTotal = 20000;
            plan.Ranges[0].Rows = 20000;
            var directory = Path.Combine(Path.GetTempPath(), "migration-oracle-ro-" + Guid.NewGuid().ToString("N"));
            try
            {
                var spec = Spec(source, target, plan, "ORACLE_READ_ONLY", "EXECUTE", "LOCAL", ErrorPolicies.Retry);
                var listener = new Listener();
                var engine = new MigrationEngine(spec, new OracleSourceFactory(source),
                    new OracleTargetFactory(target, "MIG_", spec.RunId), new LocalCheckpointStore(directory), listener, new SystemRunClock());

                await engine.RunAsync(CancellationToken.None);

                Assert.Equal("done", engine.State);
                Assert.Equal(20000, Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, "SELECT COUNT(*) FROM BIG_TGT"));
                Log("MIG_IT_RO LOCAL rows=20000");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [OracleFact]
        public async Task Engine_reconnects_after_target_session_is_killed()
        {
            PrepareTables();
            var source = Endpoint(OracleFixture.SrcUser, OracleFixture.SrcPassword);
            var target = Endpoint(OracleFixture.TgtUser, OracleFixture.TgtPassword);
            var store = new OracleCheckpointStore(target, "MIG_");
            var plan = Plan(WriteModes.InsertOnly, 1);
            var spec = Spec(source, target, plan, "ORACLE_KILL", "EXECUTE", "TARGET", ErrorPolicies.Retry);
            var listener = new Listener();
            var engine = new MigrationEngine(spec, new OracleSourceFactory(source),
                new OracleTargetFactory(target, "MIG_", spec.RunId), store, listener, new SystemRunClock());
            var killed = false;
            listener.CheckpointAction = r =>
            {
                if (!killed && r.RowsDone >= 10000)
                {
                    killed = true;
                    KillSession(OracleFixture.TgtUser);
                }
            };

            await engine.RunAsync(CancellationToken.None);

            var warning = listener.Logs.FirstOrDefault(l => l.Tag == "WARN" && l.Text.Contains("재시도", StringComparison.Ordinal));
            Assert.Equal("done", engine.State);
            Assert.True(killed);
            Assert.NotNull(warning);
            Assert.Equal(200000, Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, "SELECT COUNT(*) FROM BIG_TGT"));
            Log("KILL SESSION retry=" + warning.Text);
        }

        [OracleFact]
        public async Task Engine_delete_insert_uses_only_key_array_binds()
        {
            PrepareTables();
            var source = Endpoint(OracleFixture.SrcUser, OracleFixture.SrcPassword);
            var target = Endpoint(OracleFixture.TgtUser, OracleFixture.TgtPassword);
            var plan = Plan(WriteModes.DeleteInsert, 1);
            plan.Mapping.Where = "ID <= 1000";
            plan.ScopeTotal = 1000;
            plan.Ranges[0].Rows = 1000;
            var spec = Spec(source, target, plan, "ORACLE_DELETE_INSERT", "EXECUTE", "TARGET", ErrorPolicies.Retry);
            var engine = new MigrationEngine(spec, new OracleSourceFactory(source),
                new OracleTargetFactory(target, "MIG_", spec.RunId), new OracleCheckpointStore(target, "MIG_"),
                new Listener(), new SystemRunClock());

            await engine.RunAsync(CancellationToken.None);

            Assert.Equal("done", engine.State);
            Assert.Equal(1000, Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, "SELECT COUNT(*) FROM BIG_TGT"));
        }

        private async Task RunStopResumeAsync(EndpointSpec source, EndpointSpec target, string checkpointStore,
            ICheckpointStore store, string jobName)
        {
            var plan = Plan(WriteModes.InsertOnly, 1);
            var spec = Spec(source, target, plan, jobName, "EXECUTE", checkpointStore, ErrorPolicies.Retry);
            var listener = new Listener();
            var engine = new MigrationEngine(spec, new OracleSourceFactory(source), new OracleTargetFactory(target, "MIG_", spec.RunId), store, listener, new SystemRunClock());
            listener.CheckpointAction = r =>
            {
                if (r.RowsDone >= 80000 && engine.State == "running") engine.Stop();
            };
            await engine.RunAsync(CancellationToken.None);
            Assert.Equal("stopped", engine.State);
            var checkpoint = await store.GetAsync(jobName, "BIG", CancellationToken.None);
            var stoppedRows = Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, "SELECT COUNT(*) FROM BIG_TGT");
            Assert.Equal(stoppedRows, checkpoint.RowsDone);
            Assert.InRange(stoppedRows, 80000, 190000);

            plan = Plan(WriteModes.InsertOnly, 1);
            plan.ResumeFrom = checkpoint.Value;
            plan.BaseRows = checkpoint.RowsDone;
            plan.Ranges[0].Last = checkpoint.Value;
            plan.Ranges[0].BaseRows = checkpoint.RowsDone;
            spec = Spec(source, target, plan, jobName, "RESUME", checkpointStore, ErrorPolicies.Retry);
            listener = new Listener();
            engine = new MigrationEngine(spec, new OracleSourceFactory(source), new OracleTargetFactory(target, "MIG_", spec.RunId), store, listener, new SystemRunClock());
            await engine.RunAsync(CancellationToken.None);
            Assert.Equal("done", engine.State);
            Assert.Equal(200000, Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, "SELECT COUNT(*) FROM BIG_TGT"));
            Assert.Equal(Scalar(OracleFixture.SrcUser, OracleFixture.SrcPassword, Checksum("BIG_SRC")),
                Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, Checksum("BIG_TGT")));
            Log(checkpointStore + " stop=" + stoppedRows + ", resume=200000");
        }

        private void PrepareTables()
        {
            Execute(OracleFixture.SrcUser, OracleFixture.SrcPassword, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE BIG_SRC PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;");
            Execute(OracleFixture.TgtUser, OracleFixture.TgtPassword, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE BIG_TGT PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;");
            Execute(OracleFixture.SrcUser, OracleFixture.SrcPassword, "CREATE TABLE BIG_SRC (ID NUMBER(18) PRIMARY KEY, NAME VARCHAR2(60) NOT NULL, CREATED_AT DATE, EVENT_TS TIMESTAMP(6), AMOUNT NUMBER(12,2), NOTE CLOB)");
            Execute(OracleFixture.TgtUser, OracleFixture.TgtPassword, "CREATE TABLE BIG_TGT (ID NUMBER(18) PRIMARY KEY, NAME VARCHAR2(60) NOT NULL, CREATED_AT DATE, EVENT_TS TIMESTAMP(6), AMOUNT NUMBER(12,2), NOTE CLOB)");
            Execute(OracleFixture.SrcUser, OracleFixture.SrcPassword,
                "INSERT /*+ APPEND */ INTO BIG_SRC SELECT LEVEL,'NAME-'||LEVEL,DATE '2020-01-01'+MOD(LEVEL,365),TIMESTAMP '2020-01-01 00:00:00'+NUMTODSINTERVAL(MOD(LEVEL,86400),'SECOND'),CASE WHEN MOD(LEVEL,10)=0 THEN NULL ELSE LEVEL/100 END,CASE WHEN MOD(LEVEL,1000)=0 THEN TO_CLOB('NOTE-'||LEVEL) ELSE NULL END FROM DUAL CONNECT BY LEVEL<=200000");
            Execute(OracleFixture.SrcUser, OracleFixture.SrcPassword, "COMMIT");
        }

        private void PrepareStrictTables()
        {
            Execute(OracleFixture.SrcUser, OracleFixture.SrcPassword, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE STRICT_SRC PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;");
            Execute(OracleFixture.TgtUser, OracleFixture.TgtPassword, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE TGT_STRICT PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;");
            Execute(OracleFixture.SrcUser, OracleFixture.SrcPassword, "CREATE TABLE STRICT_SRC (ID NUMBER(18) PRIMARY KEY, NAME VARCHAR2(60))");
            Execute(OracleFixture.TgtUser, OracleFixture.TgtPassword, "CREATE TABLE TGT_STRICT (ID NUMBER(18) PRIMARY KEY, NAME VARCHAR2(20) NOT NULL)");
            Execute(OracleFixture.SrcUser, OracleFixture.SrcPassword,
                "INSERT INTO STRICT_SRC SELECT LEVEL,CASE WHEN MOD(LEVEL,2)=0 THEN RPAD('X',30,'X') ELSE 'OK-'||LEVEL END FROM DUAL CONNECT BY LEVEL<=100");
            Execute(OracleFixture.SrcUser, OracleFixture.SrcPassword, "COMMIT");
        }

        private PlanItem Plan(string mode, int workers = 4)
        {
            var columns = new List<ColumnMetadata>
            {
                new ColumnMetadata { Name = "ID", Type = "NUMBER(18)", Nullable = false, PrimaryKey = true },
                new ColumnMetadata { Name = "NAME", Type = "VARCHAR2(60)", Nullable = false },
                new ColumnMetadata { Name = "CREATED_AT", Type = "DATE" },
                new ColumnMetadata { Name = "EVENT_TS", Type = "TIMESTAMP(6)" },
                new ColumnMetadata { Name = "AMOUNT", Type = "NUMBER(12,2)" },
                new ColumnMetadata { Name = "NOTE", Type = "CLOB" }
            };
            var mapping = new Mapping { Id = "BIG", Source = "BIG_SRC", Target = "BIG_TGT", Mode = mode, CheckpointColumn = "ID", MergeKey = new List<string> { "ID" } };
            foreach (var column in columns) mapping.Columns.Add(new ColumnMapping { Source = column.Name, Target = column.Name });
            var item = new PlanItem
            {
                Key = "BIG", Mapping = mapping, Label = mapping.Label, ScopeTotal = 200000,
                SourceMetadata = new TableMetadata { Name = "BIG_SRC", Rows = 200000, AvgRowLength = 150, Columns = columns },
                TargetMetadata = new TableMetadata { Name = "BIG_TGT", Columns = columns },
                Ranges = workers == 1
                    ? new List<KeyRange> { new KeyRange { Rows = 200000 } }
                    : new List<KeyRange>
                    {
                        new KeyRange { From = "1", To = "50000", Rows = 50000 },
                        new KeyRange { From = "50001", To = "100000", Rows = 50000 },
                        new KeyRange { From = "100001", To = "150000", Rows = 50000 },
                        new KeyRange { From = "150001", To = "200000", Rows = 50000 }
                    }
            };
            item.WriteColumns = SqlGenerator.WriteColumns(mapping, item.TargetMetadata);
            return item;
        }

        private PlanItem StrictPlan(string policy)
        {
            var columns = new List<ColumnMetadata>
            {
                new ColumnMetadata { Name = "ID", Type = "NUMBER(18)", Nullable = false, PrimaryKey = true },
                new ColumnMetadata { Name = "NAME", Type = "VARCHAR2(20)", Nullable = false }
            };
            var mapping = new Mapping
            {
                Id = "STRICT", Source = "STRICT_SRC", Target = "TGT_STRICT", Mode = WriteModes.InsertOnly,
                CheckpointColumn = "ID", MergeKey = new List<string> { "ID" },
                Columns = new List<ColumnMapping>
                {
                    new ColumnMapping { Source = "ID", Target = "ID" },
                    new ColumnMapping { Source = "NAME", Target = "NAME" }
                }
            };
            var item = new PlanItem
            {
                Key = mapping.Id, Mapping = mapping, Label = mapping.Label, ScopeTotal = 100,
                SourceMetadata = new TableMetadata
                {
                    Name = "STRICT_SRC", Rows = 100,
                    Columns = new List<ColumnMetadata>
                    {
                        new ColumnMetadata { Name = "ID", Type = "NUMBER(18)", Nullable = false, PrimaryKey = true },
                        new ColumnMetadata { Name = "NAME", Type = "VARCHAR2(60)" }
                    }
                },
                TargetMetadata = new TableMetadata { Name = "TGT_STRICT", Columns = columns },
                ErrorTable = string.Equals(policy, ErrorPolicies.Continue, StringComparison.Ordinal) ? "ERR$_TGT_STRICT" : null,
                Ranges = new List<KeyRange> { new KeyRange { Rows = 100 } }
            };
            item.WriteColumns = SqlGenerator.WriteColumns(mapping, item.TargetMetadata);
            return item;
        }

        private RunSpec Spec(EndpointSpec source, EndpointSpec target, PlanItem plan, string mode)
        {
            return Spec(source, target, plan, "ORACLE_BIG_" + mode, "EXECUTE", "TARGET", ErrorPolicies.Retry);
        }

        private RunSpec Spec(EndpointSpec source, EndpointSpec target, PlanItem plan, string jobName,
            string runMode, string checkpointStore, string errorPolicy)
        {
            return new RunSpec
            {
                RunId = RunIds.New(DateTime.Now), RunMode = runMode, Source = source, Target = target,
                CheckpointStore = checkpointStore, ControlPrefix = "MIG_", Plan = new List<PlanItem> { plan },
                RetryDelays = new List<int> { 100, 200, 400 },
                Job = new MigrationJob
                {
                    JobName = jobName,
                    Strategy = new MigrationStrategy { CommitSize = 10000, FetchSize = 5000, Workers = plan.Ranges.Count, ErrorPolicy = errorPolicy },
                    Mappings = new List<Mapping> { plan.Mapping }
                }
            };
        }

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (!condition() && DateTime.UtcNow < until) await Task.Delay(25);
            Assert.True(condition());
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
                using (var command = connection.CreateCommand()) { command.CommandText = sql; command.CommandTimeout = 0; command.ExecuteNonQuery(); }
            }
        }

        private void KillSession(string user)
        {
            var password = Environment.GetEnvironmentVariable("ORACLE_IT_SYS_PW") ?? "oracle";
            using (var connection = new OracleConnection(Connection("SYSTEM", password)))
            {
                connection.Open();
                string session;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT SID||','||SERIAL# FROM V$SESSION WHERE USERNAME=:USERNAME AND ROWNUM=1";
                    command.Parameters.Add("USERNAME", user);
                    session = Convert.ToString(command.ExecuteScalar());
                }
                Assert.False(string.IsNullOrEmpty(session));
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "ALTER SYSTEM KILL SESSION '" + session + "' IMMEDIATE";
                    command.ExecuteNonQuery();
                }
            }
        }

        private long Scalar(string user, string password, string sql)
        {
            using (var connection = new OracleConnection(Connection(user, password)))
            {
                connection.Open();
                using (var command = connection.CreateCommand()) { command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            }
        }

        private string Connection(string user, string password)
        {
            return new OracleConnectionStringBuilder { DataSource = Db.Dsn, UserID = user, Password = password, Pooling = false }.ConnectionString;
        }

        private static string Checksum(string table)
        {
            return "SELECT SUM(ORA_HASH(TO_CHAR(ID)||'|'||NAME||'|'||TO_CHAR(CREATED_AT,'YYYYMMDDHH24MISS')||'|'||TO_CHAR(EVENT_TS,'YYYYMMDDHH24MISSFF6')||'|'||NVL(TO_CHAR(AMOUNT),'~')||'|'||NVL(DBMS_LOB.SUBSTR(NOTE,100,1),'~'))) FROM " + table;
        }

        private sealed class Listener : IRunListener
        {
            internal RunSnapshot Final;
            internal List<LogEntry> Logs = new List<LogEntry>();
            internal Action<CheckpointRecord> CheckpointAction;
            public void OnSnapshot(RunSnapshot snapshot) { }
            public void OnLog(LogEntry entry) { Logs.Add(entry); }
            public void OnCheckpoint(CheckpointRecord record) { CheckpointAction?.Invoke(record); }
            public void OnEnd(RunSnapshot final) { Final = final; }
        }
    }
}
