using System;
using Oracle.ManagedDataAccess.Client;

namespace MigrationStudio.OracleIT
{
    /// <summary>P6b 전용 Oracle 사용자(MIG_IT_*와 분리).</summary>
    public static class V6OracleUsers
    {
        public const string SrcUser = "MIG_V6_SRC";
        public const string SrcPassword = "mig_v6_src_pw";
        public const string TgtUser = "MIG_V6_TGT";
        public const string TgtPassword = "mig_v6_tgt_pw";
        public const string RoUser = "MIG_V6_RO";
        public const string RoPassword = "mig_v6_ro_pw";

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
                Drop(c, RoUser);
                Exec(c, "CREATE USER " + SrcUser + " IDENTIFIED BY " + SrcPassword + " DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
                Exec(c, "GRANT CREATE SESSION, CREATE TABLE TO " + SrcUser);
                Exec(c, "CREATE USER " + TgtUser + " IDENTIFIED BY " + TgtPassword + " DEFAULT TABLESPACE USERS QUOTA UNLIMITED ON USERS");
                Exec(c, "GRANT CREATE SESSION, CREATE TABLE TO " + TgtUser);
                Exec(c, "CREATE USER " + RoUser + " IDENTIFIED BY " + RoPassword);
                Exec(c, "GRANT CREATE SESSION TO " + RoUser);
            }
        }

        public static void DropAll(string dsn)
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
                Drop(c, RoUser);
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
