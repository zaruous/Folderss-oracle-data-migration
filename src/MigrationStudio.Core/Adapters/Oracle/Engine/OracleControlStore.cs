using System;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;
using Oracle.ManagedDataAccess.Client;

namespace MigrationStudio.Core.Adapters.Oracle.Engine
{
    public sealed class OracleControlStore
    {
        private readonly EndpointSpec _endpoint;
        private readonly string _prefix;

        public OracleControlStore(EndpointSpec endpoint, string controlPrefix)
        {
            _endpoint = endpoint;
            _prefix = OracleEngineSql.Prefix(controlPrefix);
        }

        public async Task EnsureControlTablesAsync(CancellationToken cancellationToken)
        {
            try
            {
                using (var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(_endpoint.Connection)))
                {
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await EnsureAsync(connection, _prefix + "RUN", "CREATE TABLE " + Schema + "." + _prefix + "RUN (RUN_ID VARCHAR2(30) PRIMARY KEY,JOB_NAME VARCHAR2(100) NOT NULL,RUN_MODE VARCHAR2(10) NOT NULL,STATUS VARCHAR2(10) NOT NULL,AGENT_PID NUMBER,HOST_NAME VARCHAR2(100),STARTED_AT TIMESTAMP DEFAULT SYSTIMESTAMP,ENDED_AT TIMESTAMP,MESSAGE VARCHAR2(4000))", cancellationToken).ConfigureAwait(false);
                    await EnsureAsync(connection, _prefix + "RUN_TASK", "CREATE TABLE " + Schema + "." + _prefix + "RUN_TASK (RUN_ID VARCHAR2(30) NOT NULL,TASK_KEY VARCHAR2(100) NOT NULL,LABEL VARCHAR2(400),STATUS VARCHAR2(10),ROWS_READ NUMBER DEFAULT 0,INSERTED NUMBER DEFAULT 0,UPDATED NUMBER DEFAULT 0,REJECTED NUMBER DEFAULT 0,STARTED_AT TIMESTAMP,ENDED_AT TIMESTAMP,CONSTRAINT PK_" + _prefix + "RUN_TASK PRIMARY KEY (RUN_ID,TASK_KEY))", cancellationToken).ConfigureAwait(false);
                    await EnsureAsync(connection, _prefix + "CHECKPOINT", "CREATE TABLE " + Schema + "." + _prefix + "CHECKPOINT (JOB_NAME VARCHAR2(100) NOT NULL,TASK_KEY VARCHAR2(100) NOT NULL,CP_COLUMN VARCHAR2(128) NOT NULL,CP_VALUE VARCHAR2(4000),RANGE_FROM VARCHAR2(4000),RANGE_TO VARCHAR2(4000),ROWS_DONE NUMBER,ROWS_TOTAL NUMBER,STATUS VARCHAR2(10),RUN_ID VARCHAR2(30),UPDATED_AT TIMESTAMP DEFAULT SYSTIMESTAMP,CONSTRAINT PK_" + _prefix + "CHECKPOINT PRIMARY KEY (JOB_NAME,TASK_KEY))", cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ControlStoreUnavailableException("대상 스키마에 " + _prefix + " 제어 테이블을 만들 권한이 없습니다. 체크포인트 저장소를 '로컬 파일'로 바꾸거나 DBA에게 요청하세요", ex);
            }
        }

        public async Task EnsureErrorTableAsync(PlanItem item, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(item.ErrorTable)) return;
            try
            {
                using (var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(_endpoint.Connection)))
                {
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                    if (await ExistsAsync(connection, item.ErrorTable, cancellationToken).ConfigureAwait(false)) return;
                    using (var command = OracleConnectionHelper.CreateCommand(connection,
                        "BEGIN DBMS_ERRLOG.CREATE_ERROR_LOG(dml_table_name=>:TABLE_NAME,err_log_table_name=>:ERROR_NAME,err_log_table_owner=>:OWNER); END;", cancellationToken))
                    {
                        command.Parameters.Add("TABLE_NAME", item.Mapping.Target);
                        command.Parameters.Add("ERROR_NAME", item.ErrorTable);
                        command.Parameters.Add("OWNER", Schema);
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("대상 스키마에 " + item.ErrorTable + " 오류 테이블을 만들 권한이 없습니다. DBA에게 DBMS_ERRLOG 실행 권한과 테이블 생성 권한을 요청하세요.", ex);
            }
        }

        private async Task EnsureAsync(OracleConnection connection, string table, string ddl, CancellationToken cancellationToken)
        {
            if (await ExistsAsync(connection, table, cancellationToken).ConfigureAwait(false)) return;
            using (var command = OracleConnectionHelper.CreateCommand(connection, ddl, cancellationToken))
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<bool> ExistsAsync(OracleConnection connection, string table, CancellationToken cancellationToken)
        {
            using (var command = OracleConnectionHelper.CreateCommand(connection, "SELECT COUNT(*) FROM ALL_TABLES WHERE OWNER=:OWNER AND TABLE_NAME=:TABLE_NAME", cancellationToken))
            {
                command.Parameters.Add("OWNER", Schema);
                command.Parameters.Add("TABLE_NAME", table);
                return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
            }
        }

        private string Schema { get { return OracleEngineSql.Identifier(_endpoint.Schema); } }
    }
}
