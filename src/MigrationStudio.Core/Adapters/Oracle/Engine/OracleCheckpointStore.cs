using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;
using Oracle.ManagedDataAccess.Client;

namespace MigrationStudio.Core.Adapters.Oracle.Engine
{
    public sealed class OracleCheckpointStore : ICheckpointStore
    {
        private readonly EndpointSpec _endpoint;
        private readonly string _prefix;

        public OracleCheckpointStore(EndpointSpec endpoint, string controlPrefix)
        {
            _endpoint = endpoint;
            _prefix = OracleEngineSql.Prefix(controlPrefix);
        }

        public async Task<CheckpointRecord> GetAsync(string job, string taskKey, CancellationToken cancellationToken)
        {
            var list = await QueryAsync(job, taskKey, cancellationToken).ConfigureAwait(false);
            return list.Count == 0 ? null : list[0];
        }

        public Task<List<CheckpointRecord>> ListAsync(string job, CancellationToken cancellationToken)
        {
            return QueryAsync(job, null, cancellationToken);
        }

        public Task SaveLocalAsync(CheckpointRecord record, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("대상 DB 체크포인트 저장은 대상 세션의 트랜잭션에서 해야 합니다.");
        }

        public async Task DeleteAsync(string job, string taskKey, CancellationToken cancellationToken)
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var command = OracleConnectionHelper.CreateCommand(connection,
                "DELETE FROM " + Schema + "." + _prefix + "CHECKPOINT WHERE JOB_NAME=:JOB_NAME AND TASK_KEY=:TASK_KEY", cancellationToken))
            {
                command.Parameters.Add("JOB_NAME", job);
                command.Parameters.Add("TASK_KEY", taskKey);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<List<CheckpointRecord>> QueryAsync(string job, string taskKey, CancellationToken cancellationToken)
        {
            var result = new List<CheckpointRecord>();
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var command = OracleConnectionHelper.CreateCommand(connection,
                "SELECT JOB_NAME,TASK_KEY,CP_COLUMN,CP_VALUE,RANGE_FROM,RANGE_TO,ROWS_DONE,ROWS_TOTAL,STATUS,RUN_ID,UPDATED_AT FROM " +
                Schema + "." + _prefix + "CHECKPOINT WHERE JOB_NAME=:JOB_NAME" + (taskKey == null ? "" : " AND TASK_KEY=:TASK_KEY") + " ORDER BY TASK_KEY", cancellationToken))
            {
                command.Parameters.Add("JOB_NAME", job);
                if (taskKey != null) command.Parameters.Add("TASK_KEY", taskKey);
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        result.Add(new CheckpointRecord
                        {
                            Job = reader.GetString(0), TaskKey = reader.GetString(1), Column = reader.GetString(2),
                            Value = reader.IsDBNull(3) ? null : reader.GetString(3), RangeFrom = reader.IsDBNull(4) ? null : reader.GetString(4),
                            RangeTo = reader.IsDBNull(5) ? null : reader.GetString(5), RowsDone = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                            RowsTotal = reader.IsDBNull(7) ? 0 : reader.GetInt64(7), Status = reader.IsDBNull(8) ? null : reader.GetString(8),
                            RunId = reader.IsDBNull(9) ? null : reader.GetString(9), UpdatedAt = reader.IsDBNull(10) ? DateTime.MinValue : reader.GetDateTime(10)
                        });
                    }
                }
            }
            return result;
        }

        private string Schema { get { return OracleEngineSql.Identifier(_endpoint.Schema); } }

        private async Task<OracleConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(_endpoint.Connection));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
    }
}
