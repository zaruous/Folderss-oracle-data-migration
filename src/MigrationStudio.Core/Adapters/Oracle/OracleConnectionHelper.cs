using System;
using System.Threading;
using Oracle.ManagedDataAccess.Client;

namespace MigrationStudio.Core.Adapters.Oracle
{
    internal static class OracleConnectionHelper
    {
        private static int _breakConfigured;

        internal static string BuildConnectionString(ConnectionTarget target)
        {
            var dsn = (target.Host ?? "").Trim() + ":" +
                      target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                      (target.Service ?? "").Trim();
            var builder = new OracleConnectionStringBuilder
            {
                DataSource = dsn,
                UserID = target.User,
                Password = target.Password,
                Pooling = false,
                ConnectionTimeout = 15
            };
            return builder.ConnectionString;
        }

        /// <summary>
        /// 취소(DbCommand.Cancel)를 in-band break로 보낸다. OOB(TCP 긴급 데이터)는 Docker Desktop 포트 포워딩 등에서
        /// 서버에 닿지 않아 취소가 무시될 수 있다(DB Helper DbSession.UseInBandBreak와 같음).
        /// </summary>
        internal static void EnsureInBandBreak()
        {
            if (Interlocked.Exchange(ref _breakConfigured, 1) != 0)
            {
                return;
            }

            try
            {
                OracleConfiguration.DisableOOB = true;
            }
            catch (InvalidOperationException)
            {
            }
        }

        internal static OracleCommand CreateCommand(OracleConnection connection, string sql, CancellationToken cancellationToken)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.BindByName = true;
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    try
                    {
                        cmd.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                });
            }

            return cmd;
        }

        /// <summary>원본 읽기 세션: CURRENT_SCHEMA(검증된 이름) + READ ONLY.</summary>
        internal static void ApplySourceSession(OracleConnection connection, string schema, CancellationToken cancellationToken)
        {
            var owner = OracleSelectGuard.ValidateSchemaName(schema);
            if (!string.IsNullOrEmpty(owner))
            {
                using (var alter = CreateCommand(connection, "ALTER SESSION SET CURRENT_SCHEMA = " + owner, cancellationToken))
                {
                    alter.ExecuteNonQuery();
                }
            }

            using (var ro = CreateCommand(connection, "SET TRANSACTION READ ONLY", cancellationToken))
            {
                ro.ExecuteNonQuery();
            }
        }
    }
}
