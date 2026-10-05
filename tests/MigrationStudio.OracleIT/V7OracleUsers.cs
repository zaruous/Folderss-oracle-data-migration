using System;
using Oracle.ManagedDataAccess.Client;

namespace MigrationStudio.OracleIT
{
    /// <summary>P6c 실행 후 검증 Oracle IT(MIG_IT_* · MIG_V6_*와 분리).</summary>
    public static class V7OracleUsers
    {
        public const string SrcUser = "MIG_V7_SRC";
        public const string SrcPassword = "mig_v7_src_pw";
        public const string TgtUser = "MIG_V7_TGT";
        public const string TgtPassword = "mig_v7_tgt_pw";

        public static void Ensure(string dsn)
        {
            if (string.IsNullOrWhiteSpace(dsn))
            {
                return;
            }

            var sysPw = Environment.GetEnvironmentVariable("ORACLE_IT_SYS_PW") ?? "oracle";
            var cs = new OracleConnectionStringBuilder
            {
                DataSource = dsn.Trim(),
                UserID = "system",
                Password = sysPw,
                Pooling = false
            }.ConnectionString;

            using (var c = new OracleConnection(cs))
            {
                c.Open();
                Drop(c, SrcUser);
                Drop(c, TgtUser);
                Exec(c, "CREATE USER " + SrcUser + " IDENTIFIED BY " + SrcPassword + " DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
                Exec(c, "GRANT CREATE SESSION, CREATE TABLE TO " + SrcUser);
                Exec(c, "CREATE USER " + TgtUser + " IDENTIFIED BY " + TgtPassword + " DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
                Exec(c, "GRANT CREATE SESSION, CREATE TABLE TO " + TgtUser);
            }
        }

        private static void Drop(OracleConnection c, string user)
        {
            Exec(c, "BEGIN EXECUTE IMMEDIATE 'DROP USER " + user + " CASCADE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -1918 THEN RAISE; END IF; END;");
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
