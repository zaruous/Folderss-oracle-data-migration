using System;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;
using Oracle.ManagedDataAccess.Client;

namespace MigrationStudio.Core.Adapters.Oracle.Engine
{
    public sealed class OracleRunRecorder : IRunRecorder
    {
        private readonly EndpointSpec _endpoint;
        private readonly string _prefix;

        public OracleRunRecorder(EndpointSpec endpoint, string controlPrefix)
        {
            _endpoint = endpoint;
            _prefix = OracleEngineSql.Prefix(controlPrefix);
        }

        public Task StartAsync(RunSpec spec, CancellationToken cancellationToken)
        {
            return ExecuteAsync("INSERT INTO " + Table("RUN") + " (RUN_ID,JOB_NAME,RUN_MODE,STATUS) VALUES (:RUN_ID,:JOB_NAME,:RUN_MODE,'running')", cancellationToken,
                new object[] { "RUN_ID", spec.RunId, "JOB_NAME", spec.Job.JobName, "RUN_MODE", spec.RunMode });
        }

        public Task TaskStartedAsync(RunSpec spec, PlanItem item, CancellationToken cancellationToken)
        {
            return ExecuteAsync("INSERT INTO " + Table("RUN_TASK") + " (RUN_ID,TASK_KEY,LABEL,STATUS,STARTED_AT) VALUES (:RUN_ID,:TASK_KEY,:LABEL,'run',SYSTIMESTAMP)", cancellationToken,
                new object[] { "RUN_ID", spec.RunId, "TASK_KEY", item.Key, "LABEL", item.Label });
        }

        public Task TaskEndedAsync(RunSpec spec, TaskSnapshot task, CancellationToken cancellationToken)
        {
            return ExecuteAsync("UPDATE " + Table("RUN_TASK") + " SET STATUS=:STATUS,ROWS_READ=:ROWS_READ,INSERTED=:INSERTED,UPDATED=:UPDATED,REJECTED=:REJECTED,ENDED_AT=SYSTIMESTAMP WHERE RUN_ID=:RUN_ID AND TASK_KEY=:TASK_KEY", cancellationToken,
                new object[] { "STATUS", task.Status, "ROWS_READ", task.Read, "INSERTED", task.Inserted, "UPDATED", task.Updated, "REJECTED", task.Rejected, "RUN_ID", spec.RunId, "TASK_KEY", task.Key });
        }

        public Task EndAsync(RunSpec spec, RunSnapshot snapshot, string message, CancellationToken cancellationToken)
        {
            return ExecuteAsync("UPDATE " + Table("RUN") + " SET STATUS=:STATUS,ENDED_AT=SYSTIMESTAMP,MESSAGE=:MESSAGE WHERE RUN_ID=:RUN_ID", cancellationToken,
                new object[] { "STATUS", snapshot.State, "MESSAGE", message, "RUN_ID", spec.RunId });
        }

        private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, object[] parameters)
        {
            using (var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(_endpoint.Connection)))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var command = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken))
                {
                    for (var i = 0; i < parameters.Length; i += 2) command.Parameters.Add((string)parameters[i], parameters[i + 1] ?? DBNull.Value);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private string Table(string suffix)
        {
            return OracleEngineSql.Identifier(_endpoint.Schema) + "." + _prefix + suffix;
        }
    }
}
