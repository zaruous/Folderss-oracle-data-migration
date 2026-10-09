using System;
using System.Collections.Generic;
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
    /// <summary>
    /// 삭제 대조의 실제 SQL: 키 정규화(숫자 TM9 · 길이가 다른 CHAR의 RTRIM) · UTL_I18N + ORA_HASH 순서 · ROWID + 키 문자열로 표시/해제.
    /// 원본 REC_SRC 1~20, 대상 REC_TGT 1~23 중 5·23은 이미 표시 → 21·22 표시, 23 그대로, 5(원본에 있음)는 표시 해제.
    /// </summary>
    [Collection(OracleCollection.Name)]
    public sealed class ReconcileOracleTests : OracleTestBase
    {
        public ReconcileOracleTests(OracleFixture db, ITestOutputHelper output)
            : base(db, output, "reconcile")
        {
        }

        [OracleFact]
        public async Task Key_diff_marks_missing_rows_and_unmarks_reappeared_ones()
        {
            Execute(OracleFixture.SrcUser, OracleFixture.SrcPassword, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE REC_SRC PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;");
            Execute(OracleFixture.TgtUser, OracleFixture.TgtPassword, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE REC_TGT PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;");
            Execute(OracleFixture.SrcUser, OracleFixture.SrcPassword, "CREATE TABLE REC_SRC (ID NUMBER(18), CODE CHAR(3), NAME VARCHAR2(30), PRIMARY KEY (ID, CODE))");
            Execute(OracleFixture.TgtUser, OracleFixture.TgtPassword, "CREATE TABLE REC_TGT (ID NUMBER(18), CODE CHAR(5), NAME VARCHAR2(30), DEL_YN CHAR(1), PRIMARY KEY (ID, CODE))");
            Execute(OracleFixture.SrcUser, OracleFixture.SrcPassword, "INSERT INTO REC_SRC SELECT LEVEL, 'A', 'N' || LEVEL FROM DUAL CONNECT BY LEVEL <= 20");
            Execute(OracleFixture.SrcUser, OracleFixture.SrcPassword, "COMMIT");
            Execute(OracleFixture.TgtUser, OracleFixture.TgtPassword, "INSERT INTO REC_TGT SELECT LEVEL, 'A', 'N' || LEVEL, CASE WHEN LEVEL IN (5, 23) THEN 'Y' END FROM DUAL CONNECT BY LEVEL <= 23");
            Execute(OracleFixture.TgtUser, OracleFixture.TgtPassword, "COMMIT");

            var item = Item();
            var store = new OracleReconcileStore(Endpoint(OracleFixture.SrcUser, OracleFixture.SrcPassword), Endpoint(OracleFixture.TgtUser, OracleFixture.TgtPassword));
            Log("source keys sql=" + OracleReconcileStore.SourceKeysSql(item, OracleFixture.SrcUser));
            Log("target keys sql=" + OracleReconcileStore.TargetKeysSql(item, OracleFixture.TgtUser));
            Assert.Equal(20, await store.CountAsync(item, false, CancellationToken.None));
            Assert.Equal(21, await store.CountAsync(item, true, CancellationToken.None));

            KeyDiffResult diff;
            using (var source = await store.OpenKeysAsync(item, false, CancellationToken.None))
            using (var target = await store.OpenKeysAsync(item, true, CancellationToken.None))
            {
                diff = await KeyDiff.RunAsync(source, target, 1000, CancellationToken.None);
            }
            Log("diff mark=" + diff.MarkCount + " unmark=" + diff.UnmarkCount + " already=" + diff.AlreadyMarked + " sourceOnly=" + diff.SourceOnly);
            Assert.Equal(20, diff.SourceKeys);
            Assert.Equal(23, diff.TargetKeys);
            Assert.Equal(2, diff.MarkCount);
            Assert.Equal(1, diff.UnmarkCount);
            Assert.Equal(1, diff.AlreadyMarked);
            Assert.Equal(0, diff.SourceOnly);

            Assert.Equal(2, await store.SetMarkAsync(item, diff.Mark, true, CancellationToken.None));
            Assert.Equal(1, await store.SetMarkAsync(item, diff.Unmark, false, CancellationToken.None));
            // 다시 해도 바뀌지 않는다(이미 표시·이미 해제)
            Assert.Equal(0, await store.SetMarkAsync(item, diff.Mark, true, CancellationToken.None));

            Assert.Equal(3, Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, "SELECT COUNT(*) FROM REC_TGT WHERE DEL_YN = 'Y' AND ID IN (21, 22, 23)"));
            Assert.Equal(0, Scalar(OracleFixture.TgtUser, OracleFixture.TgtPassword, "SELECT COUNT(*) FROM REC_TGT WHERE DEL_YN IS NOT NULL AND ID <= 20"));
        }

        private static PlanItem Item()
        {
            var target = new TableMetadata
            {
                Name = "REC_TGT",
                Columns = new List<ColumnMetadata>
                {
                    new ColumnMetadata { Name = "ID", Type = "NUMBER(18)", Nullable = false, PrimaryKey = true },
                    new ColumnMetadata { Name = "CODE", Type = "CHAR(5)", Nullable = false, PrimaryKey = true },
                    new ColumnMetadata { Name = "NAME", Type = "VARCHAR2(30)", Nullable = true },
                    new ColumnMetadata { Name = "DEL_YN", Type = "CHAR(1)", Nullable = true }
                }
            };
            var mapping = new Mapping
            {
                Id = "M-REC", Source = "REC_SRC", Target = "REC_TGT", Mode = WriteModes.Merge,
                MergeKey = new List<string> { "ID", "CODE" },
                DeleteMode = DeleteModes.Mark, MarkColumn = "DEL_YN", MarkValue = "Y",
                Columns = new List<ColumnMapping>
                {
                    new ColumnMapping { Source = "ID", Target = "ID" },
                    new ColumnMapping { Source = "CODE", Target = "CODE" },
                    new ColumnMapping { Source = "NAME", Target = "NAME" }
                }
            };
            return new PlanItem { Key = mapping.Id, Mapping = mapping, Label = mapping.Label, TargetMetadata = target, WriteColumns = SqlGenerator.WriteColumns(mapping, target) };
        }

        private EndpointSpec Endpoint(string user, string password)
        {
            return new EndpointSpec
            {
                Schema = user,
                Connection = new ConnectionTarget { Host = Db.Host, Port = Db.Port, Service = Db.Service, User = user, Password = password }
            };
        }

        private string Connection(string user, string password)
        {
            return new OracleConnectionStringBuilder { DataSource = Db.Dsn, UserID = user, Password = password, Pooling = false }.ConnectionString;
        }

        private void Execute(string user, string password, string sql)
        {
            using (var connection = new OracleConnection(Connection(user, password)))
            {
                connection.Open();
                using (var command = connection.CreateCommand()) { command.CommandText = sql; command.CommandTimeout = 0; command.ExecuteNonQuery(); }
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
    }
}
