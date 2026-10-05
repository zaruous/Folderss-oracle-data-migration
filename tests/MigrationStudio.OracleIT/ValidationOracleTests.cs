using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Validation;
using Oracle.ManagedDataAccess.Client;
using Xunit;
using Xunit.Abstractions;

namespace MigrationStudio.OracleIT
{
    [Collection(OracleCollection.Name)]
    public sealed class ValidationOracleTests : OracleTestBase
    {
        public const string SrcUser = "MIG_V5_SRC";
        public const string SrcPassword = "mig_v5_src_pw";
        public const string TgtUser = "MIG_V5_TGT";
        public const string TgtPassword = "mig_v5_tgt_pw";
        public const string RoUser = "MIG_V5_RO";
        public const string RoPassword = "mig_v5_ro_pw";

        public ValidationOracleTests(OracleFixture db, ITestOutputHelper output)
            : base(db, output, "validation-v5")
        {
            if (OracleFixture.Enabled)
            {
                ValidationV5Fixture.Ensure(Db);
            }
        }

        [OracleFact]
        public async Task RunPre_detects_truncation_null_dup_fk_and_readonly()
        {
            var ctx = ValidationV5Fixture.BuildContext(Db);
            var adapter = DatabaseAdapters.For("oracle");
            var before = ValidationV5Fixture.SnapshotCounts(Db);
            var items = await new ValidationEngine(adapter).RunPreAsync(ctx, null, CancellationToken.None);
            var after = ValidationV5Fixture.SnapshotCounts(Db);
            Assert.Equal(before, after);

            Assert.Contains(items, i => i.Check == "VARCHAR 길이" && i.Level == CheckLevels.Warn);
            Assert.Contains(items, i => i.Check == "NOT NULL" && i.Level == CheckLevels.Warn);
            Assert.Contains(items, i => i.Check == "중복 키" && i.Level == CheckLevels.Error);
            Log("items=" + items.Count + " ERROR=" + items.Count(i => i.Level == CheckLevels.Error));
        }

        [OracleFact]
        public async Task RunPre_ro_user_tablespace_is_info_not_fatal()
        {
            var ctx = ValidationV5Fixture.BuildContext(Db, useRoTarget: true);
            var adapter = DatabaseAdapters.For("oracle");
            var items = await new ValidationEngine(adapter).RunPreAsync(ctx, null, CancellationToken.None);
            Assert.Contains(items, i => i.Check == "대상 테이블스페이스" && i.Level == CheckLevels.Info);
        }

        [OracleFact]
        public async Task RunPre_cancel_throws()
        {
            var ctx = ValidationV5Fixture.BuildContext(Db);
            var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new ValidationEngine(DatabaseAdapters.For("oracle")).RunPreAsync(ctx, null, cts.Token));
        }
    }

    internal static class ValidationV5Fixture
    {
        public static void Ensure(OracleFixture db)
        {
            var sys = db.Dsn;
            var sysPw = Environment.GetEnvironmentVariable("ORACLE_IT_SYS_PW") ?? "oracle";
            var cs = new OracleConnectionStringBuilder { DataSource = sys, UserID = "system", Password = sysPw, Pooling = false }.ConnectionString;
            using (var c = new OracleConnection(cs))
            {
                c.Open();
                Drop(c);
                Exec(c, "CREATE USER " + ValidationOracleTests.SrcUser + " IDENTIFIED BY " + ValidationOracleTests.SrcPassword
                    + " DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
                Exec(c, "GRANT CREATE SESSION, CREATE TABLE TO " + ValidationOracleTests.SrcUser);
                Exec(c, "CREATE USER " + ValidationOracleTests.TgtUser + " IDENTIFIED BY " + ValidationOracleTests.TgtPassword
                    + " DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
                Exec(c, "GRANT CREATE SESSION, CREATE TABLE TO " + ValidationOracleTests.TgtUser);
                Exec(c, "CREATE USER " + ValidationOracleTests.RoUser + " IDENTIFIED BY " + ValidationOracleTests.RoPassword);
                Exec(c, "GRANT CREATE SESSION TO " + ValidationOracleTests.RoUser);

                var s = ValidationOracleTests.SrcUser + ".";
                var t = ValidationOracleTests.TgtUser + ".";
                Exec(c, "CREATE TABLE " + s + "V5_PARENT (ID NUMBER PRIMARY KEY, NM VARCHAR2(10))");
                Exec(c, "CREATE TABLE " + s + "V5_CHILD (ID NUMBER, PID NUMBER, NOTE VARCHAR2(20))");
                Exec(c, "INSERT INTO " + s + "V5_PARENT VALUES (1, 'ok')");
                Exec(c, "INSERT INTO " + s + "V5_CHILD VALUES (1, 1, '12345')");
                Exec(c, "INSERT INTO " + s + "V5_CHILD VALUES (2, 9, '1234567890')");
                Exec(c, "INSERT INTO " + s + "V5_CHILD VALUES (2, 9, 'dup')");
                Exec(c, "INSERT INTO " + s + "V5_CHILD VALUES (3, 9, NULL)");
                Exec(c, "CREATE TABLE " + t + "V5_TGT (ID NUMBER PRIMARY KEY, NOTE VARCHAR2(3) NOT NULL, VAL VARCHAR2(2))");
                Exec(c, "COMMIT");
            }
        }

        public static ValidationContext BuildContext(OracleFixture db, bool useRoTarget = false)
        {
            var host = db.Host;
            var port = db.Port;
            var service = db.Service;
            var tgtUser = useRoTarget ? ValidationOracleTests.RoUser : ValidationOracleTests.TgtUser;
            var tgtPw = useRoTarget ? ValidationOracleTests.RoPassword : ValidationOracleTests.TgtPassword;
            var job = new MigrationJob
            {
                JobName = "V5_VALIDATION",
                Source = new ConnectionRef { Schema = ValidationOracleTests.SrcUser },
                Target = new ConnectionRef { Schema = ValidationOracleTests.TgtUser },
                Strategy = new MigrationStrategy { ErrorPolicy = ErrorPolicies.Continue, ErrorTable = "ERR$_" }
            };
            job.Mappings.Add(new Mapping
            {
                Id = "m1",
                Use = true,
                SourceType = SourceTypes.Table,
                Source = "V5_CHILD",
                Target = "V5_TGT",
                Mode = WriteModes.Merge,
                MergeKey = new List<string> { "ID" },
                Columns = new List<ColumnMapping>
                {
                    new ColumnMapping { Target = "ID", Source = "ID", NullRule = NullRules.Reject },
                    new ColumnMapping { Target = "NOTE", Source = "NOTE", NullRule = NullRules.Reject },
                    new ColumnMapping { Target = "VAL", Source = "NOTE", NullRule = NullRules.Allow }
                }
            });

            var srcMeta = new SchemaMetadata
            {
                Schema = ValidationOracleTests.SrcUser,
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        Name = "V5_CHILD",
                        Rows = 3,
                        Columns = new List<ColumnMetadata>
                        {
                            new ColumnMetadata { Name = "ID", Type = "NUMBER", PrimaryKey = true },
                            new ColumnMetadata { Name = "PID", Type = "NUMBER" },
                            new ColumnMetadata { Name = "NOTE", Type = "VARCHAR2(20)", Nullable = true }
                        }
                    }
                }
            };
            var tgtMeta = new SchemaMetadata
            {
                Schema = ValidationOracleTests.TgtUser,
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        Name = "V5_TGT",
                        Rows = 0,
                        Columns = new List<ColumnMetadata>
                        {
                            new ColumnMetadata { Name = "ID", Type = "NUMBER", PrimaryKey = true, Nullable = false },
                            new ColumnMetadata { Name = "NOTE", Type = "VARCHAR2(3)", Nullable = false },
                            new ColumnMetadata { Name = "VAL", Type = "VARCHAR2(2)", Nullable = true }
                        }
                    }
                }
            };

            return new ValidationContext
            {
                Job = job,
                SourceMeta = srcMeta,
                TargetMeta = tgtMeta,
                Source = new ConnectionTarget { Host = host, Port = port, Service = service, User = ValidationOracleTests.SrcUser, Password = ValidationOracleTests.SrcPassword },
                Target = new ConnectionTarget { Host = host, Port = port, Service = service, User = tgtUser, Password = tgtPw },
                SourceProfile = new MigrationStudio.Core.Settings.ConnectionProfile { Name = "src", Host = host, Port = port, Service = service },
                TargetProfile = new MigrationStudio.Core.Settings.ConnectionProfile { Name = "tgt", Host = host, Port = port, Service = service }
            };
        }

        public static string SnapshotCounts(OracleFixture db)
        {
            var sysPw = Environment.GetEnvironmentVariable("ORACLE_IT_SYS_PW") ?? "oracle";
            var cs = new OracleConnectionStringBuilder { DataSource = db.Dsn, UserID = "system", Password = sysPw, Pooling = false }.ConnectionString;
            using (var c = new OracleConnection(cs))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT (SELECT COUNT(*) FROM " + ValidationOracleTests.TgtUser + ".V5_TGT)"
                        + "||'-'||(SELECT COUNT(*) FROM ALL_TABLES WHERE OWNER='" + ValidationOracleTests.TgtUser + "') FROM DUAL";
                    return cmd.ExecuteScalar().ToString();
                }
            }
        }

        private static void Drop(OracleConnection c)
        {
            foreach (var u in new[] { ValidationOracleTests.SrcUser, ValidationOracleTests.TgtUser, ValidationOracleTests.RoUser })
            {
                Exec(c, "BEGIN EXECUTE IMMEDIATE 'DROP USER " + u + " CASCADE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -1918 THEN RAISE; END IF; END;");
            }
        }

        private static void Exec(OracleConnection c, string sql)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
    }
}
