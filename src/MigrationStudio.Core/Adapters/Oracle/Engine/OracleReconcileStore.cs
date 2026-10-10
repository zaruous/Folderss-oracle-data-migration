using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MigrationStudio.Core.Types;
using Oracle.ManagedDataAccess.Client;

namespace MigrationStudio.Core.Adapters.Oracle.Engine
{
    /// <summary>
    /// 삭제 대조의 Oracle 쪽. 키는 대상 열 형식 기준으로 같은 문자열이 되게 정규화하고(숫자 TM9·날짜 YYYYMMDDHH24MISS[FF6]·CHAR는 RTRIM),
    /// 그 문자열의 UTF-8 바이트 해시(ORA_HASH) 순서로 내보낸다 — 원본·대상 DB의 문자 집합·정렬 규칙이 달라도 같은 키는 같은 해시가 된다.
    /// 원본은 읽기 전용 세션, 대상 표시는 ROWID + 키 문자열을 함께 확인하는 UPDATE(대조와 표시 사이에 행이 바뀌었으면 건너뜀).
    /// </summary>
    public sealed class OracleReconcileStore : IReconcileStore
    {
        private const int MarkChunk = 1000;
        private readonly EndpointSpec _source;
        private readonly EndpointSpec _target;

        public OracleReconcileStore(EndpointSpec source, EndpointSpec target)
        {
            _source = source;
            _target = target;
        }

        public async Task<long> CountAsync(PlanItem item, bool target, CancellationToken cancellationToken)
        {
            var sql = target ? TargetCountSql(item, _target.Schema) : SourceCountSql(item, _source.Schema);
            using (var connection = await OpenAsync(target, cancellationToken).ConfigureAwait(false))
            using (var command = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken))
            {
                command.CommandTimeout = 0;
                return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
        }

        public async Task<IKeyStream> OpenKeysAsync(PlanItem item, bool target, CancellationToken cancellationToken)
        {
            var sql = target ? TargetKeysSql(item, _target.Schema) : SourceKeysSql(item, _source.Schema);
            var connection = await OpenAsync(target, cancellationToken).ConfigureAwait(false);
            OracleCommand command = null;
            try
            {
                command = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken);
                command.CommandTimeout = 0;
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                reader.FetchSize = 1024 * 1024;
                return new KeyStream(connection, command, reader, target);
            }
            catch
            {
                command?.Dispose();
                connection.Dispose();
                throw;
            }
        }

        public async Task<int> SetMarkAsync(PlanItem item, IReadOnlyList<KeyEntry> keys, bool mark, CancellationToken cancellationToken)
        {
            var sql = MarkSql(item, _target.Schema, mark);
            var literal = mark && !IsSysdate(item.Mapping.MarkValue);
            var changed = 0;
            using (var connection = await OpenAsync(true, cancellationToken).ConfigureAwait(false))
            {
                for (var offset = 0; offset < keys.Count; offset += MarkChunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = keys.Skip(offset).Take(MarkChunk).ToList();
                    using (var transaction = connection.BeginTransaction())
                    using (var command = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken))
                    {
                        command.CommandTimeout = 0;
                        command.Transaction = transaction;
                        command.ArrayBindCount = chunk.Count;
                        if (literal)
                        {
                            AddArray(command, "MV", chunk.Select(k => item.Mapping.MarkValue.Trim()).ToArray());
                        }
                        AddArray(command, "RID", chunk.Select(k => k.RowId).ToArray());
                        AddArray(command, "K", chunk.Select(k => k.Text).ToArray());
                        changed += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        transaction.Commit();
                    }
                }
            }
            return changed;
        }

        /// <summary>문자열 배열 바인딩. 크기를 정하지 않으면 ODP.NET이 배열 원소 길이를 잘못 잡을 수 있어 가장 긴 값으로 정한다(대상 쓰기와 같은 방식).</summary>
        private static void AddArray(OracleCommand command, string name, string[] values)
        {
            var parameter = command.Parameters.Add(name, OracleDbType.Varchar2);
            parameter.Size = Math.Min(32767, Math.Max(1, values.Max(v => (v ?? "").Length) * 4));
            parameter.Value = values.Select(v => (object)v ?? DBNull.Value).ToArray();
        }

        private async Task<OracleConnection> OpenAsync(bool target, CancellationToken cancellationToken)
        {
            OracleConnectionHelper.EnsureInBandBreak();
            var endpoint = target ? _target : _source;
            var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(endpoint.Connection));
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (!target)
                {
                    OracleConnectionHelper.ApplySourceSession(connection, endpoint.Schema, cancellationToken);
                }
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        // ---------- SQL (시험에서 문자열로 고정) ----------

        internal static string SourceCountSql(PlanItem item, string schema)
        {
            return "SELECT COUNT(*) FROM " + OracleEngineSql.Identifier(schema) + "." + OracleEngineSql.Identifier(item.Mapping.Source) + SourceWhere(item);
        }

        internal static string TargetCountSql(PlanItem item, string schema)
        {
            var where = DeleteModes.IsMark(item.Mapping.DeleteMode) ? " WHERE " + OracleEngineSql.Identifier(item.Mapping.MarkColumn) + " IS NULL" : "";
            return "SELECT COUNT(*) FROM " + OracleEngineSql.Identifier(schema) + "." + OracleEngineSql.Identifier(item.Mapping.Target) + where;
        }

        internal static string SourceKeysSql(PlanItem item, string schema)
        {
            var keys = KeyColumns(item);
            var text = string.Join(" || CHR(31) || ", keys.Select(k => Normalize(k.Expr, k.Column.Type, false)));
            return "SELECT ORA_HASH(UTL_I18N.STRING_TO_RAW(K, 'AL32UTF8')) AS H, K FROM (\n" +
                   "  SELECT " + text + " AS K\n" +
                   "  FROM " + OracleEngineSql.Identifier(schema) + "." + OracleEngineSql.Identifier(item.Mapping.Source) + SourceWhere(item) + "\n" +
                   ") WHERE K IS NOT NULL ORDER BY H";
        }

        internal static string TargetKeysSql(PlanItem item, string schema)
        {
            var mark = DeleteModes.IsMark(item.Mapping.DeleteMode)
                ? "CASE WHEN " + OracleEngineSql.Identifier(item.Mapping.MarkColumn) + " IS NULL THEN 0 ELSE 1 END"
                : "0";
            return "SELECT ORA_HASH(UTL_I18N.STRING_TO_RAW(K, 'AL32UTF8')) AS H, K, MK, RID FROM (\n" +
                   "  SELECT " + TargetKeyText(item) + " AS K, " + mark + " AS MK, CAST(ROWID AS VARCHAR2(4000)) AS RID\n" +
                   "  FROM " + OracleEngineSql.Identifier(schema) + "." + OracleEngineSql.Identifier(item.Mapping.Target) + "\n" +
                   ") WHERE K IS NOT NULL ORDER BY H";
        }

        /// <summary>표시 또는 표시 해제. ROWID만 믿지 않고 키 문자열도 맞을 때만 고친다(대조 뒤 행이 지워지고 그 자리에 다른 행이 들어온 경우).</summary>
        internal static string MarkSql(PlanItem item, string schema, bool mark)
        {
            var column = OracleEngineSql.Identifier(item.Mapping.MarkColumn);
            var value = !mark ? "NULL" : IsSysdate(item.Mapping.MarkValue) ? "SYSDATE" : ":MV";
            return "UPDATE " + OracleEngineSql.Identifier(schema) + "." + OracleEngineSql.Identifier(item.Mapping.Target) +
                   " SET " + column + " = " + value +
                   " WHERE ROWID = :RID AND " + column + (mark ? " IS NULL" : " IS NOT NULL") +
                   " AND " + TargetKeyText(item) + " = :K";
        }

        internal static bool IsSysdate(string value)
        {
            return string.Equals((value ?? "").Trim(), "SYSDATE", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 키 정규화: 대상 열 형식으로 맞춘 뒤 문자열로. 원본 식은 대상 형식으로 CAST해서 같은 값이 같은 문자열이 되게 한다
        /// (원본 VARCHAR2 '001' → 대상 NUMBER 1). 숫자는 NLS와 무관하게 TM9 + 소수점 '.'.
        /// </summary>
        internal static string Normalize(string expr, string targetType, bool target)
        {
            var type = OracleType.Parse(targetType);
            if (type == null || !KeyDiff.SupportsKeyType(targetType))
            {
                throw new InvalidOperationException("삭제 대조 키로 쓸 수 없는 형식: " + targetType);
            }
            if (type.IsNumber)
            {
                return "TO_CHAR(" + (target ? expr : "CAST(" + expr + " AS NUMBER)") + ", 'TM9', 'NLS_NUMERIC_CHARACTERS=''.,''')";
            }
            if (type.Base == "DATE")
            {
                return "TO_CHAR(" + (target ? expr : "CAST(" + expr + " AS DATE)") + ", 'YYYYMMDDHH24MISS')";
            }
            if (type.Base == "TIMESTAMP")
            {
                return "TO_CHAR(" + (target ? expr : "CAST(" + expr + " AS TIMESTAMP)") + ", 'YYYYMMDDHH24MISSFF6')";
            }
            // CHAR·NCHAR는 대상이 공백을 채워 두므로 양쪽 모두 뒤 공백을 뗀다. VARCHAR2는 뒤 공백도 키의 일부라 그대로.
            var fixedWidth = type.Base == "CHAR" || type.Base == "NCHAR";
            var text = target ? expr : "TO_CHAR(" + expr + ")";
            return fixedWidth ? "RTRIM(" + text + ")" : text;
        }

        private static string TargetKeyText(PlanItem item)
        {
            return string.Join(" || CHR(31) || ", KeyColumns(item).Select(k => Normalize(OracleEngineSql.Identifier(k.Name), k.Column.Type, true)));
        }

        private static List<WriteColumn> KeyColumns(PlanItem item)
        {
            var keys = item.Mapping.MergeKey ?? new List<string>();
            if (keys.Count == 0)
            {
                throw new InvalidOperationException(item.Label + ": 삭제 대조에는 병합 키가 필요합니다.");
            }
            var list = new List<WriteColumn>();
            foreach (var key in keys)
            {
                var column = item.WriteColumns.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));
                if (column == null || column.Column == null || string.IsNullOrWhiteSpace(column.Expr))
                {
                    throw new InvalidOperationException(item.Label + ": 병합 키 " + key + "가 쓰기 열에 없습니다.");
                }
                list.Add(column);
            }
            return list;
        }

        private static string SourceWhere(PlanItem item)
        {
            return string.IsNullOrWhiteSpace(item.Mapping.Where) ? "" : " WHERE (" + item.Mapping.Where.Trim() + ")";
        }

        private sealed class KeyStream : IKeyStream
        {
            private readonly OracleConnection _connection;
            private readonly OracleCommand _command;
            private readonly OracleDataReader _reader;
            private readonly bool _target;

            internal KeyStream(OracleConnection connection, OracleCommand command, OracleDataReader reader, bool target)
            {
                _connection = connection;
                _command = command;
                _reader = reader;
                _target = target;
            }

            public async Task<List<KeyEntry>> ReadAsync(int maxRows, CancellationToken cancellationToken)
            {
                var rows = new List<KeyEntry>(Math.Min(maxRows, 5000));
                while (rows.Count < maxRows && await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var entry = new KeyEntry
                    {
                        Hash = Convert.ToInt64(_reader.GetDecimal(0), CultureInfo.InvariantCulture),
                        Text = _reader.GetString(1)
                    };
                    if (_target)
                    {
                        entry.Marked = Convert.ToInt32(_reader.GetDecimal(2), CultureInfo.InvariantCulture) != 0;
                        entry.RowId = _reader.GetString(3);
                    }
                    rows.Add(entry);
                }
                return rows;
            }

            public void Dispose()
            {
                _reader.Dispose();
                _command.Dispose();
                _connection.Dispose();
            }
        }
    }
}
