using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Sql;
using Oracle.ManagedDataAccess.Client;

namespace MigrationStudio.Core.Adapters.Oracle
{
    public sealed partial class OracleDatabaseAdapter
    {
        private static readonly Regex TrailingWsRx = new Regex(@"[;\s]+$", RegexOptions.CultureInvariant);

        public Task<SqlParseResult> ParseSqlAsync(ConnectionTarget target, string schema, string sql, CancellationToken cancellationToken)
        {
            return Task.Run(() => ParseSqlCore(target, schema, sql, cancellationToken), cancellationToken);
        }

        public Task<List<QueryColumn>> DescribeSqlAsync(ConnectionTarget target, string schema, string sql, CancellationToken cancellationToken)
        {
            return Task.Run(() => DescribeSqlCore(target, schema, sql, cancellationToken), cancellationToken);
        }

        public Task<QueryResult> QueryAsync(
            ConnectionTarget target,
            string schema,
            string sql,
            IList<SqlBind> binds,
            int maxRows,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => QueryCore(target, schema, sql, binds, maxRows, cancellationToken), cancellationToken);
        }

        public Task<long?> CountAsync(
            ConnectionTarget target,
            string schema,
            string sql,
            IList<SqlBind> binds,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => CountCore(target, schema, sql, binds, cancellationToken), cancellationToken);
        }

        private static SqlParseResult ParseSqlCore(ConnectionTarget target, string schema, string sql, CancellationToken cancellationToken)
        {
            OracleSelectGuard.EnsureSelectOnly(sql);
            var validation = ConnectionTarget.Validate(target);
            if (validation != null)
            {
                throw new AdapterException(new InvalidOperationException(validation));
            }

            OracleConnectionHelper.EnsureInBandBreak();
            var userSql = TrailingWsRx.Replace(sql ?? "", "").Trim();
            try
            {
                using (var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(target)))
                {
                    connection.Open();
                    cancellationToken.ThrowIfCancellationRequested();
                    OracleConnectionHelper.ApplySourceSession(connection, schema, cancellationToken);

                    using (var cmd = OracleConnectionHelper.CreateCommand(connection, ParseBlockSql, cancellationToken))
                    {
                        cmd.Parameters.Add("SQL_TEXT", OracleDbType.Varchar2, userSql, ParameterDirection.Input);
                        var errPos = new OracleParameter("ERR_POS", OracleDbType.Int32, ParameterDirection.Output);
                        cmd.Parameters.Add(errPos);
                        try
                        {
                            cmd.ExecuteNonQuery();
                            return new SqlParseResult { Ok = true };
                        }
                        catch (OracleException oex)
                        {
                            int? pos = null;
                            if (errPos.Value != null && errPos.Value != DBNull.Value)
                            {
                                pos = Convert.ToInt32(errPos.Value, CultureInfo.InvariantCulture);
                            }

                            return ToParseResult(oex, userSql, pos);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (OracleErrors.IsCancellation(ex))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (ex is OracleException oex)
                {
                    return ToParseResult(oex, userSql, null);
                }

                throw new AdapterException(ex);
            }
        }

        private static List<QueryColumn> DescribeSqlCore(ConnectionTarget target, string schema, string sql, CancellationToken cancellationToken)
        {
            OracleSelectGuard.EnsureSelectOnly(sql);
            var validation = ConnectionTarget.Validate(target);
            if (validation != null)
            {
                throw new AdapterException(new InvalidOperationException(validation));
            }

            OracleConnectionHelper.EnsureInBandBreak();
            var inner = TrailingWsRx.Replace(sql ?? "", "").Trim();
            var wrapped = "SELECT * FROM (\n" + inner + "\n) WHERE 1 = 0";
            return TraceQuery("describe", () =>
            {
                using (var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(target)))
                {
                    connection.Open();
                    cancellationToken.ThrowIfCancellationRequested();
                    OracleConnectionHelper.ApplySourceSession(connection, schema, cancellationToken);

                    using (var cmd = OracleConnectionHelper.CreateCommand(connection, wrapped, cancellationToken))
                    {
                        cmd.CommandTimeout = 120;
                        OracleSqlBinds.AddNullBindParameters(cmd, inner);
                        using (var reader = cmd.ExecuteReader(CommandBehavior.SchemaOnly))
                        {
                            var schemaTable = reader.GetSchemaTable();
                            return OracleSchemaReader.FromSchemaTable(schemaTable);
                        }
                    }
                }
            });
        }

        private static QueryResult QueryCore(
            ConnectionTarget target,
            string schema,
            string sql,
            IList<SqlBind> binds,
            int maxRows,
            CancellationToken cancellationToken)
        {
            OracleSelectGuard.EnsureSelectOnly(sql);
            var validation = ConnectionTarget.Validate(target);
            if (validation != null)
            {
                throw new AdapterException(new InvalidOperationException(validation));
            }

            OracleConnectionHelper.EnsureInBandBreak();
            var limit = Math.Max(0, maxRows) + 1;
            return TraceQuery("query", () =>
            {
                var sw = Stopwatch.StartNew();
                using (var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(target)))
                {
                    connection.Open();
                    cancellationToken.ThrowIfCancellationRequested();
                    OracleConnectionHelper.ApplySourceSession(connection, schema, cancellationToken);

                    using (var cmd = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken))
                    {
                        cmd.CommandTimeout = 120;
                        cmd.InitialLOBFetchSize = 4000;
                        OracleSqlBinds.AddParameters(cmd, sql, binds);
                        using (var reader = cmd.ExecuteReader(CommandBehavior.SequentialAccess))
                        {
                            var columns = new List<QueryColumn>();
                            for (var i = 0; i < reader.FieldCount; i++)
                            {
                                columns.Add(new QueryColumn
                                {
                                    Name = reader.GetName(i),
                                    Type = reader.GetDataTypeName(i),
                                    Nullable = true
                                });
                            }

                            var rows = new List<string[]>();
                            var hasMore = false;
                            while (reader.Read())
                            {
                                if (rows.Count >= limit)
                                {
                                    hasMore = true;
                                    break;
                                }

                                var row = new string[reader.FieldCount];
                                for (var c = 0; c < reader.FieldCount; c++)
                                {
                                    row[c] = ValueText.Format(reader.GetValue(c));
                                }

                                rows.Add(row);
                            }

                            sw.Stop();
                            return new QueryResult
                            {
                                Columns = columns,
                                Rows = rows,
                                ElapsedMs = sw.ElapsedMilliseconds,
                                HasMore = hasMore
                            };
                        }
                    }
                }
            });
        }

        private static long? CountCore(
            ConnectionTarget target,
            string schema,
            string sql,
            IList<SqlBind> binds,
            CancellationToken cancellationToken)
        {
            OracleSelectGuard.EnsureSelectOnly(sql);
            var validation = ConnectionTarget.Validate(target);
            if (validation != null)
            {
                throw new AdapterException(new InvalidOperationException(validation));
            }

            OracleConnectionHelper.EnsureInBandBreak();
            var inner = TrailingWsRx.Replace(sql ?? "", "").Trim();
            var countSql = "SELECT COUNT(*) FROM (\n" + inner + "\n)";
            return TraceQuery("count", () =>
            {
                using (var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(target)))
                {
                    connection.Open();
                    cancellationToken.ThrowIfCancellationRequested();
                    OracleConnectionHelper.ApplySourceSession(connection, schema, cancellationToken);

                    using (var cmd = OracleConnectionHelper.CreateCommand(connection, countSql, cancellationToken))
                    {
                        cmd.CommandTimeout = 60;
                        OracleSqlBinds.AddParameters(cmd, inner, binds);
                        var value = cmd.ExecuteScalar();
                        if (value == null || value is DBNull)
                        {
                            return (long?)null;
                        }

                        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    }
                }
            });
        }

        private const string ParseBlockSql = @"
DECLARE
    C PLS_INTEGER := DBMS_SQL.OPEN_CURSOR;
BEGIN
    DBMS_SQL.PARSE(C, :SQL_TEXT, DBMS_SQL.NATIVE);
    DBMS_SQL.CLOSE_CURSOR(C);
EXCEPTION
    WHEN OTHERS THEN
        :ERR_POS := DBMS_SQL.LAST_ERROR_POSITION;
        IF DBMS_SQL.IS_OPEN(C) THEN DBMS_SQL.CLOSE_CURSOR(C); END IF;
        RAISE;
END;";

        private static SqlParseResult ToParseResult(OracleException ex, string sql, int? position)
        {
            var code = OracleErrors.CodeOf(ex) ?? "ORA-00900";
            var message = OracleErrors.Describe(ex);
            if (message != null && message.StartsWith(code, StringComparison.Ordinal))
            {
                var idx = message.IndexOf(':');
                if (idx >= 0 && idx + 1 < message.Length)
                {
                    message = message.Substring(idx + 1).Trim();
                }
            }

            int? line = null;
            if (position.HasValue && position.Value > 0)
            {
                line = SqlText.LineOf(sql, position.Value - 1);
            }

            return new SqlParseResult
            {
                Ok = false,
                ErrorCode = code,
                Message = message,
                Line = line,
                Position = position
            };
        }
    }
}
