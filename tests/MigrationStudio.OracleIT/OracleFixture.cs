using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Oracle.ManagedDataAccess.Client;
using Xunit;
using Xunit.Abstractions;

namespace MigrationStudio.OracleIT
{
    [CollectionDefinition(Name)]
    public sealed class OracleCollection : ICollectionFixture<OracleFixture>
    {
        public const string Name = "oracle";
    }

    public sealed class OracleFactAttribute : FactAttribute
    {
        public OracleFactAttribute()
        {
            if (!OracleFixture.Enabled)
            {
                Skip = OracleFixture.SkipReason;
            }
        }
    }

    /// <summary>ORACLE_IT_DSN이 없으면 건너뛴다. SYSTEM으로 MIG_IT_* 사용자만 만든다.</summary>
    public sealed class OracleFixture : IDisposable
    {
        public const string SkipReason = "ORACLE_IT_DSN(예: localhost:1521/xe)이 없어 실제 Oracle 시험을 건너뜁니다.";

        public const string SrcUser = "MIG_IT_SRC";
        public const string SrcPassword = "mig_it_src_pw";
        public const string TgtUser = "MIG_IT_TGT";
        public const string TgtPassword = "mig_it_tgt_pw";
        public const string RoUser = "MIG_IT_RO";
        public const string RoPassword = "mig_it_ro_pw";
        public const string SpecialUser = "MIG_IT_SPECIAL";
        public const string SpecialPassword = "Pw;x=1'y";
        public const string OtherUser = "MIG_IT_OTHER";
        public const string OtherPassword = "mig_it_other_pw";

        public static bool Enabled
        {
            get { return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORACLE_IT_DSN")); }
        }

        public string Dsn { get; }
        public string Host { get; }
        public int Port { get; }
        public string Service { get; }
        public string ReportPath { get; }

        private readonly string _systemConnectionString;

        public OracleFixture()
        {
            if (!Enabled)
            {
                return;
            }

            Dsn = Environment.GetEnvironmentVariable("ORACLE_IT_DSN").Trim();
            var hostPort = Dsn.Split('/')[0];
            Host = hostPort.Split(':')[0];
            Port = int.Parse(hostPort.Split(':')[1]);
            Service = Dsn.Split('/')[1];
            var sysPw = Environment.GetEnvironmentVariable("ORACLE_IT_SYS_PW") ?? "oracle";
            _systemConnectionString = new OracleConnectionStringBuilder
            {
                DataSource = Dsn,
                UserID = "system",
                Password = sysPw,
                Pooling = false
            }.ConnectionString;
            ReportPath = Environment.GetEnvironmentVariable("ORACLE_IT_REPORT")
                ?? Path.Combine(AppContext.BaseDirectory, "oracle-it-report.txt");
            File.WriteAllText(ReportPath, "# Migration Oracle IT " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + Dsn + Environment.NewLine);

            MigrationStudio.Core.Adapters.Oracle.OracleConnectionHelper.EnsureInBandBreak();
            var watch = Stopwatch.StartNew();
            Setup();
            Log("fixture", "준비 " + watch.ElapsedMilliseconds + "ms");
        }

        public void Log(string area, string line)
        {
            lock (this)
            {
                File.AppendAllText(ReportPath, "[" + area + "] " + line + Environment.NewLine);
            }
        }

        public void Dispose()
        {
            if (!Enabled)
            {
                return;
            }

            if (string.Equals(Environment.GetEnvironmentVariable("ORACLE_IT_KEEP"), "1", StringComparison.Ordinal))
            {
                Log("fixture", "ORACLE_IT_KEEP=1 — 사용자 유지");
                return;
            }

            try
            {
                using (var c = new OracleConnection(_systemConnectionString))
                {
                    c.Open();
                    DropUsers(c);
                }

                Log("fixture", "정리 완료");
            }
            catch (Exception ex)
            {
                Log("fixture", "정리 실패: " + ex.Message);
            }
        }

        private void Setup()
        {
            using (var c = new OracleConnection(_systemConnectionString))
            {
                c.Open();
                DropUsers(c);

                Exec(c, "CREATE USER " + SrcUser + " IDENTIFIED BY " + SrcPassword + " DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
                Exec(c, "GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW TO " + SrcUser);

                Exec(c, "CREATE USER " + TgtUser + " IDENTIFIED BY " + TgtPassword + " DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
                Exec(c, "GRANT CREATE SESSION, CREATE TABLE TO " + TgtUser);

                Exec(c, "CREATE USER " + RoUser + " IDENTIFIED BY " + RoPassword);
                Exec(c, "GRANT CREATE SESSION TO " + RoUser);

                Exec(c, "CREATE USER " + SpecialUser + " IDENTIFIED BY \"" + SpecialPassword + "\"");
                Exec(c, "GRANT CREATE SESSION TO " + SpecialUser);

                Exec(c, "CREATE USER " + OtherUser + " IDENTIFIED BY " + OtherPassword + " DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
                Exec(c, "GRANT CREATE SESSION, CREATE TABLE TO " + OtherUser);
                Exec(c, "CREATE TABLE " + OtherUser + ".SECRET (X NUMBER PRIMARY KEY)");
                Exec(c, "INSERT INTO " + OtherUser + ".SECRET VALUES (1)");
                Exec(c, "COMMIT");

                var s = SrcUser + ".";
                Exec(c, "CREATE TABLE " + s + "SRC_CUSTOMER ("
                      + " CUSTOMER_ID NUMBER(12) CONSTRAINT PK_SRC_CUSTOMER PRIMARY KEY,"
                      + " PHONE_NO VARCHAR2(30),"
                      + " STATUS_CD CHAR(1) DEFAULT 'Y' NOT NULL,"
                      + " REG_DT DATE NOT NULL,"
                      + " AMOUNT NUMBER(12,2),"
                      + " NOTE VARCHAR2(20 CHAR),"
                      + " EVENT_TS TIMESTAMP(6) WITH TIME ZONE)");
                Exec(c, "COMMENT ON TABLE " + s + "SRC_CUSTOMER IS '고객'");
                Exec(c, "COMMENT ON COLUMN " + s + "SRC_CUSTOMER.CUSTOMER_ID IS '고객 번호'");

                Exec(c, "CREATE TABLE " + s + "SRC_CUSTOMER_GRADE ("
                      + " GRADE_CD CHAR(1) PRIMARY KEY,"
                      + " GRADE_NM VARCHAR2(20))");

                Exec(c, "CREATE TABLE " + s + "SRC_ORDER ("
                      + " ORDER_ID NUMBER(10) PRIMARY KEY,"
                      + " CUSTOMER_ID NUMBER(12) NOT NULL,"
                      + " CONSTRAINT FK_SRC_ORDER_CUST FOREIGN KEY (CUSTOMER_ID) REFERENCES " + s + "SRC_CUSTOMER(CUSTOMER_ID))");

                Exec(c, "INSERT INTO " + s + "SRC_CUSTOMER VALUES (1, '010', 'A', DATE '2020-01-01', 100, '메모', SYSTIMESTAMP)");
                Exec(c, "INSERT INTO " + s + "SRC_CUSTOMER VALUES (2, '011', 'Y', DATE '2020-02-01', 200, '메모2', SYSTIMESTAMP)");
                Exec(c, "INSERT INTO " + s + "SRC_CUSTOMER_GRADE VALUES ('A', 'Gold')");
                Exec(c, "INSERT INTO " + s + "SRC_ORDER VALUES (100, 1)");
                Exec(c, "CREATE VIEW " + s + "V_CUSTOMER AS SELECT CUSTOMER_ID, PHONE_NO FROM " + s + "SRC_CUSTOMER");
                Exec(c, "COMMIT");
                Exec(c, "BEGIN DBMS_STATS.GATHER_SCHEMA_STATS('" + SrcUser + "'); END;");
            }
        }

        private static void DropUsers(OracleConnection c)
        {
            foreach (var u in new[] { SrcUser, TgtUser, RoUser, SpecialUser, OtherUser })
            {
                Exec(c, "BEGIN FOR s IN (SELECT SID, SERIAL# SN FROM V$SESSION WHERE USERNAME = '" + u + "') LOOP"
                      + " BEGIN EXECUTE IMMEDIATE 'ALTER SYSTEM KILL SESSION ''' || s.SID || ',' || s.SN || ''' IMMEDIATE';"
                      + " EXCEPTION WHEN OTHERS THEN IF SQLCODE != -31 THEN RAISE; END IF; END; END LOOP;"
                      + " EXECUTE IMMEDIATE 'DROP USER " + u + " CASCADE';"
                      + " EXCEPTION WHEN OTHERS THEN IF SQLCODE != -1918 THEN RAISE; END IF; END;");
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

    public abstract class OracleTestBase
    {
        protected readonly OracleFixture Db;
        private readonly ITestOutputHelper _output;
        private readonly string _area;

        protected OracleTestBase(OracleFixture db, ITestOutputHelper output, string area)
        {
            Db = db;
            _output = output;
            _area = area;
        }

        protected void Log(string line)
        {
            _output.WriteLine(line);
            Db.Log(_area, line);
        }

        protected static T Timed<T>(Func<T> work, Action<long> elapsedMs)
        {
            var w = Stopwatch.StartNew();
            var r = work();
            elapsedMs(w.ElapsedMilliseconds);
            return r;
        }
    }
}
