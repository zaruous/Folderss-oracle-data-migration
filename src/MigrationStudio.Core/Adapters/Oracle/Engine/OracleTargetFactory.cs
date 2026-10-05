using System;
using System.Collections.Generic;
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
    public sealed class OracleTargetFactory : ITargetFactory, ITargetPreparation
    {
        private readonly EndpointSpec _endpoint;
        private readonly string _controlPrefix;
        private readonly string _runId;

        public OracleTargetFactory(EndpointSpec endpoint, string controlPrefix, string runId)
        {
            _endpoint = endpoint;
            _controlPrefix = OracleEngineSql.Prefix(controlPrefix);
            _runId = runId;
        }

        public async Task<ITargetSession> OpenAsync(CancellationToken cancellationToken)
        {
            OracleConnectionHelper.EnsureInBandBreak();
            var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(_endpoint.Connection));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return new OracleTargetSession(connection, _endpoint.Schema, _controlPrefix, _runId);
        }

        public async Task PrepareAsync(RunSpec spec, PlanItem item, CancellationToken cancellationToken)
        {
            var control = new OracleControlStore(_endpoint, _controlPrefix);
            if (string.Equals(spec.CheckpointStore, "TARGET", StringComparison.Ordinal))
            {
                await control.EnsureControlTablesAsync(cancellationToken).ConfigureAwait(false);
            }
            if (string.Equals(spec.Job.Strategy.ErrorPolicy, ErrorPolicies.Continue, StringComparison.Ordinal))
            {
                await control.EnsureErrorTableAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal sealed class OracleTargetSession : ITargetSession
    {
        private readonly OracleConnection _connection;
        private readonly string _schema;
        private readonly string _prefix;
        private readonly string _runId;
        private OracleTransaction _transaction;

        internal OracleTargetSession(OracleConnection connection, string schema, string prefix, string runId)
        {
            _connection = connection;
            _schema = OracleEngineSql.Identifier(schema);
            _prefix = prefix;
            _runId = runId;
        }

        public async Task<WriteResult> WriteBatchAsync(PlanItem item, IReadOnlyList<WriteColumn> columns, List<object[]> rows, CancellationToken cancellationToken)
        {
            EnsureTransaction();
            var updated = string.Equals(item.Mapping.Mode, WriteModes.Merge, StringComparison.Ordinal)
                ? await CountExistingKeysAsync(item, rows, cancellationToken).ConfigureAwait(false) : 0;
            var mode = item.Mapping.Mode;
            if (string.Equals(mode, WriteModes.DeleteInsert, StringComparison.Ordinal))
            {
                await ExecuteDeleteAsync(item, columns, rows, cancellationToken).ConfigureAwait(false);
                mode = WriteModes.InsertOnly;
            }
            if (string.Equals(mode, WriteModes.TruncateInsert, StringComparison.Ordinal)) mode = WriteModes.InsertOnly;
            var names = columns.Select(c => c.Name).ToList();
            var sql = SqlGenerator.BuildWriteSql(_schema, item.Mapping.Target, names, mode, item.Mapping.MergeKey, item.ErrorTable);
            sql = sql.Replace("('RUN_ID')", "(" + OracleEngineSql.Literal(_runId) + ")", StringComparison.Ordinal);
            var written = await ExecuteInChunksAsync(sql, item, columns, rows, cancellationToken).ConfigureAwait(false);
            return new WriteResult
            {
                Written = written,
                Rejected = rows.Count - written,
                Updated = (int)Math.Min(updated, written),
                Inserted = written - (int)Math.Min(updated, written)
            };
        }

        public async Task SaveCheckpointAsync(CheckpointRecord record, CancellationToken cancellationToken)
        {
            EnsureTransaction();
            var sql = "MERGE INTO " + _schema + "." + _prefix + "CHECKPOINT C USING (SELECT :JOB_NAME JOB_NAME, :TASK_KEY TASK_KEY FROM DUAL) S " +
                      "ON (C.JOB_NAME=S.JOB_NAME AND C.TASK_KEY=S.TASK_KEY) " +
                      "WHEN MATCHED THEN UPDATE SET C.CP_COLUMN=:CP_COLUMN,C.CP_VALUE=:CP_VALUE,C.RANGE_FROM=:RANGE_FROM,C.RANGE_TO=:RANGE_TO,C.ROWS_DONE=:ROWS_DONE,C.ROWS_TOTAL=:ROWS_TOTAL,C.STATUS=:STATUS,C.RUN_ID=:RUN_ID,C.UPDATED_AT=SYSTIMESTAMP " +
                      "WHEN NOT MATCHED THEN INSERT (JOB_NAME,TASK_KEY,CP_COLUMN,CP_VALUE,RANGE_FROM,RANGE_TO,ROWS_DONE,ROWS_TOTAL,STATUS,RUN_ID,UPDATED_AT) " +
                      "VALUES (:JOB_NAME,:TASK_KEY,:CP_COLUMN,:CP_VALUE,:RANGE_FROM,:RANGE_TO,:ROWS_DONE,:ROWS_TOTAL,:STATUS,:RUN_ID,SYSTIMESTAMP)";
            using (var command = Command(sql, cancellationToken))
            {
                Add(command, "JOB_NAME", record.Job); Add(command, "TASK_KEY", record.TaskKey); Add(command, "CP_COLUMN", record.Column);
                Add(command, "CP_VALUE", record.Value); Add(command, "RANGE_FROM", record.RangeFrom); Add(command, "RANGE_TO", record.RangeTo);
                Add(command, "ROWS_DONE", record.RowsDone); Add(command, "ROWS_TOTAL", record.RowsTotal); Add(command, "STATUS", record.Status); Add(command, "RUN_ID", record.RunId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public Task CommitAsync()
        {
            if (_transaction == null) return Task.CompletedTask;
            _transaction.Commit();
            _transaction.Dispose();
            _transaction = null;
            return Task.CompletedTask;
        }

        public Task RollbackAsync()
        {
            if (_transaction == null) return Task.CompletedTask;
            _transaction.Rollback();
            _transaction.Dispose();
            _transaction = null;
            return Task.CompletedTask;
        }

        public async Task TruncateAsync(PlanItem item, CancellationToken cancellationToken)
        {
            using (var command = Command("TRUNCATE TABLE " + _schema + "." + OracleEngineSql.Identifier(item.Mapping.Target), cancellationToken))
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<long> CountExistingKeysAsync(PlanItem item, List<object[]> rows, CancellationToken cancellationToken)
        {
            var keys = item.Mapping.MergeKey;
            if (keys == null || keys.Count == 0 || rows.Count == 0) return 0;
            var ordinals = keys.Select(k => item.WriteColumns.FindIndex(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))).ToList();
            if (ordinals.Any(i => i < 0)) return 0;
            long count = 0;
            for (var offset = 0; offset < rows.Count; offset += 1000)
            {
                var take = Math.Min(1000, rows.Count - offset);
                var tuples = new List<string>();
                using (var command = Command("", cancellationToken))
                {
                    for (var r = 0; r < take; r++)
                    {
                        var names = new List<string>();
                        for (var k = 0; k < keys.Count; k++)
                        {
                            var name = "P" + r + "_" + k;
                            names.Add(":" + name);
                            Add(command, name, rows[offset + r][ordinals[k]]);
                        }
                        tuples.Add(keys.Count == 1 ? names[0] : "(" + string.Join(",", names) + ")");
                    }
                    var keySql = keys.Count == 1 ? OracleEngineSql.Identifier(keys[0]) : "(" + string.Join(",", keys.Select(OracleEngineSql.Identifier)) + ")";
                    command.CommandText = "SELECT COUNT(*) FROM " + _schema + "." + OracleEngineSql.Identifier(item.Mapping.Target) + " WHERE " + keySql + " IN (" + string.Join(",", tuples) + ")";
                    count += Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                }
            }
            return count;
        }

        private async Task<int> ExecuteArrayAsync(string sql, PlanItem item, IReadOnlyList<WriteColumn> columns, List<object[]> rows, CancellationToken cancellationToken)
        {
            using (var command = Command(sql, cancellationToken))
            {
                command.ArrayBindCount = rows.Count;
                for (var c = 0; c < columns.Count; c++)
                {
                    var parameter = command.Parameters.Add(columns[c].Name, DbTypeFor(columns[c].Column != null ? columns[c].Column.Type : null));
                    var type = columns[c].Column != null ? OracleType.Parse(columns[c].Column.Type) : null;
                    if (type != null && type.IsChar) parameter.Size = Math.Min(32767, Math.Max(1, (type.Length ?? 4000) * 4));
                    var values = new object[rows.Count];
                    for (var r = 0; r < rows.Count; r++) values[r] = c < rows[r].Length && rows[r][c] != null ? rows[r][c] : DBNull.Value;
                    parameter.Value = values;
                }
                return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ExecuteDeleteAsync(PlanItem item, IReadOnlyList<WriteColumn> columns, List<object[]> rows,
            CancellationToken cancellationToken)
        {
            if (item.Mapping.MergeKey.Count == 0) throw new InvalidOperationException("DELETE_INSERT에는 삭제 키가 필요합니다.");
            using (var command = Command(DeleteSql(item), cancellationToken))
            {
                command.ArrayBindCount = rows.Count;
                foreach (var key in item.Mapping.MergeKey)
                {
                    var ordinal = columns.ToList().FindIndex(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));
                    if (ordinal < 0) throw new InvalidOperationException("DELETE_INSERT 키 " + key + "가 쓰기 열에 없습니다.");
                    var column = columns[ordinal];
                    var parameter = command.Parameters.Add(key, DbTypeFor(column.Column != null ? column.Column.Type : null));
                    parameter.Value = rows.Select(r => ordinal < r.Length && r[ordinal] != null ? r[ordinal] : DBNull.Value).ToArray();
                }
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<int> ExecuteInChunksAsync(string sql, PlanItem item, IReadOnlyList<WriteColumn> columns, List<object[]> rows, CancellationToken cancellationToken)
        {
            if (sql.IndexOf("LOG ERRORS", StringComparison.Ordinal) >= 0)
            {
                // Oracle은 배열 DML과 LOG ERRORS를 함께 실행하면 ORA-38909를 내므로 오류 계속 정책만 행 단위로 보낸다.
                return await ExecuteRowsAsync(sql, columns, rows, cancellationToken).ConfigureAwait(false);
            }
            // ODP.NET의 대형 LOB 배열은 네이티브 메모리를 크게 잡으므로 한 트랜잭션 안에서 작은 배열로 나눈다.
            var hasLob = columns.Any(c =>
            {
                var type = c.Column != null ? OracleType.Parse(c.Column.Type) : null;
                return type != null && (type.Base == "CLOB" || type.Base == "BLOB");
            });
            var size = hasLob ? 2000 : rows.Count;
            var written = 0;
            for (var offset = 0; offset < rows.Count; offset += size)
            {
                written += await ExecuteArrayAsync(sql, item, columns, rows.GetRange(offset, Math.Min(size, rows.Count - offset)), cancellationToken).ConfigureAwait(false);
            }
            return written;
        }

        private async Task<int> ExecuteRowsAsync(string sql, IReadOnlyList<WriteColumn> columns, List<object[]> rows,
            CancellationToken cancellationToken)
        {
            var written = 0;
            using (var command = Command(sql, cancellationToken))
            {
                for (var c = 0; c < columns.Count; c++)
                {
                    var parameter = command.Parameters.Add(columns[c].Name, DbTypeFor(columns[c].Column != null ? columns[c].Column.Type : null));
                    var type = columns[c].Column != null ? OracleType.Parse(columns[c].Column.Type) : null;
                    if (type != null && type.IsChar) parameter.Size = Math.Min(32767, Math.Max(1, (type.Length ?? 4000) * 4));
                }
                foreach (var row in rows)
                {
                    for (var c = 0; c < columns.Count; c++)
                    {
                        command.Parameters[c].Value = c < row.Length && row[c] != null ? row[c] : DBNull.Value;
                    }
                    written += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            return written;
        }

        private string DeleteSql(PlanItem item)
        {
            if (item.Mapping.MergeKey.Count == 0) throw new InvalidOperationException("DELETE_INSERT에는 삭제 키가 필요합니다.");
            return "DELETE FROM " + _schema + "." + OracleEngineSql.Identifier(item.Mapping.Target) + " WHERE " +
                   string.Join(" AND ", item.Mapping.MergeKey.Select(k => OracleEngineSql.Identifier(k) + " = :" + OracleEngineSql.Identifier(k)));
        }

        private OracleCommand Command(string sql, CancellationToken cancellationToken)
        {
            var command = OracleConnectionHelper.CreateCommand(_connection, sql, cancellationToken);
            command.CommandTimeout = 0;
            command.Transaction = _transaction;
            return command;
        }

        private void EnsureTransaction()
        {
            if (_transaction == null) _transaction = _connection.BeginTransaction();
        }

        private static void Add(OracleCommand command, string name, object value)
        {
            command.Parameters.Add(name, value ?? DBNull.Value);
        }

        private static OracleDbType DbTypeFor(string text)
        {
            var type = OracleType.Parse(text);
            if (type == null) return OracleDbType.Varchar2;
            if (type.IsChar) return type.Base == "NCHAR" || type.Base == "NVARCHAR2" ? OracleDbType.NVarchar2 : OracleDbType.Varchar2;
            if (type.IsNumber) return OracleDbType.Decimal;
            if (type.Base == "DATE") return OracleDbType.Date;
            if (type.Base == "TIMESTAMP") return type.TimeZone == "TZ" ? OracleDbType.TimeStampTZ : type.TimeZone == "LTZ" ? OracleDbType.TimeStampLTZ : OracleDbType.TimeStamp;
            if (type.Base == "CLOB") return OracleDbType.Clob;
            if (type.Base == "BLOB") return OracleDbType.Blob;
            if (type.Base == "RAW") return OracleDbType.Raw;
            throw new UnsupportedColumnTypeException("대상 열의 " + text + " 형식은 이관 엔진이 지원하지 않습니다.");
        }

        public void Dispose()
        {
            if (_transaction != null) { try { _transaction.Rollback(); } catch { } _transaction.Dispose(); }
            _connection.Dispose();
        }
    }
}
