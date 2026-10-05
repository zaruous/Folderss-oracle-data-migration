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
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Adapters.Oracle.Engine
{
    public sealed class OracleSourceFactory : ISourceFactory
    {
        private readonly EndpointSpec _endpoint;

        public OracleSourceFactory(EndpointSpec endpoint)
        {
            _endpoint = endpoint;
        }

        public async Task<ISourceReader> OpenAsync(PlanItem item, KeyRange range, string lastValue, int fetchSize, CancellationToken cancellationToken)
        {
            OracleConnectionHelper.EnsureInBandBreak();
            var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(_endpoint.Connection));
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                OracleConnectionHelper.ApplySourceSession(connection, _endpoint.Schema, cancellationToken);
                var sql = BuildSql(item, fetchSize, _endpoint.Schema);
                var command = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken);
                command.CommandTimeout = 0;
                command.InitialLONGFetchSize = -1;
                AddBinds(command, item, lastValue, range);
                var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
                var average = item.SourceMetadata != null && item.SourceMetadata.AvgRowLength > 0 ? item.SourceMetadata.AvgRowLength : 100;
                reader.FetchSize = Math.Max(1, average * Math.Max(1, fetchSize));
                return new OracleSourceReader(connection, command, reader, string.IsNullOrEmpty(item.Mapping.CheckpointColumn) ? -1 : item.WriteColumns.Count);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        internal static string BuildSql(PlanItem item, int fetchSize)
        {
            return BuildSql(item, fetchSize, "");
        }

        internal static string BuildSql(PlanItem item, int fetchSize, string sourceSchema)
        {
            var options = new SourceSelectOptions { FetchSize = fetchSize, Workers = item.Ranges.Count > 1 ? item.Ranges.Count : 1 };
            var sql = SqlGenerator.BuildSourceSelect(item.Mapping, sourceSchema, item.SourceMetadata, item.TargetMetadata, options);
            var marker = "\nFROM ";
            var at = sql.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                throw new InvalidOperationException("원본 SELECT를 만들 수 없습니다.");
            }
            if (string.IsNullOrEmpty(item.Mapping.CheckpointColumn))
            {
                return ExecutableSql(sql);
            }
            var prefix = item.Mapping.IsSql ? "S." : "";
            sql = sql.Replace(prefix + item.Mapping.CheckpointColumn + " > :LAST_ID",
                "(:LAST_ID IS NULL OR " + prefix + item.Mapping.CheckpointColumn + " > :LAST_ID)", StringComparison.Ordinal);
            sql = sql.Insert(at, ",\n    " + prefix + item.Mapping.CheckpointColumn + " AS MIG_CP_HIDDEN");
            if (item.Ranges.Count > 1)
            {
                var order = "\nORDER BY ";
                var orderAt = sql.LastIndexOf(order, StringComparison.Ordinal);
                var predicate = prefix + item.Mapping.CheckpointColumn + " >= :RANGE_FROM";
                if (sql.IndexOf("\nWHERE ", StringComparison.Ordinal) >= 0)
                {
                    sql = sql.Replace("\nWHERE ", "\nWHERE " + predicate + "\n  AND ", StringComparison.Ordinal);
                }
                else
                {
                    sql = sql.Insert(orderAt, "\nWHERE " + predicate);
                }
            }
            return ExecutableSql(sql);
        }

        private static string ExecutableSql(string sql)
        {
            // ODP.NET은 주석 안의 :이름도 바인드로 해석할 수 있어 실행용 SQL에서는 설명 주석을 뺀다.
            return string.Join("\n", sql.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        }

        private static void AddBinds(OracleCommand command, PlanItem item, string lastValue, KeyRange range)
        {
            var mapping = item.Mapping;
            var checkpointType = item.SourceMetadata != null && item.SourceMetadata.FindColumn(mapping.CheckpointColumn) != null
                ? item.SourceMetadata.FindColumn(mapping.CheckpointColumn).Type : "VARCHAR2";
            foreach (var bind in mapping.Binds)
            {
                var value = bind.FromCheckpoint ? lastValue : bind.Value;
                AddBind(command, bind.Name, bind.Type, value);
            }
            if (command.CommandText.IndexOf(":LAST_ID", StringComparison.OrdinalIgnoreCase) >= 0 && !Contains(command, "LAST_ID"))
            {
                AddBind(command, "LAST_ID", checkpointType, lastValue);
            }
            if (command.CommandText.IndexOf(":RANGE_TO", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddBind(command, "RANGE_TO", checkpointType, range.To);
            }
            if (command.CommandText.IndexOf(":RANGE_FROM", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddBind(command, "RANGE_FROM", checkpointType, range.From);
            }
        }

        private static bool Contains(OracleCommand command, string name)
        {
            foreach (OracleParameter parameter in command.Parameters)
            {
                if (string.Equals(parameter.ParameterName, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void AddBind(OracleCommand command, string name, string type, string value)
        {
            var baseType = (type ?? "VARCHAR2").ToUpperInvariant();
            var parameter = command.Parameters.Add(name, baseType.StartsWith("NUMBER", StringComparison.Ordinal) ? OracleDbType.Decimal :
                baseType.StartsWith("DATE", StringComparison.Ordinal) ? OracleDbType.Date :
                baseType.StartsWith("TIMESTAMP", StringComparison.Ordinal) ? OracleDbType.TimeStamp : OracleDbType.Varchar2);
            if (value == null)
            {
                parameter.Value = DBNull.Value;
            }
            else if (parameter.OracleDbType == OracleDbType.Decimal)
            {
                parameter.Value = decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
            }
            else if (parameter.OracleDbType == OracleDbType.Date || parameter.OracleDbType == OracleDbType.TimeStamp)
            {
                parameter.Value = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);
            }
            else
            {
                parameter.Value = value;
            }
        }
    }

    internal sealed class OracleSourceReader : ISourceReader
    {
        private readonly OracleConnection _connection;
        private readonly OracleCommand _command;
        private readonly OracleDataReader _reader;
        private readonly List<string> _columns = new List<string>();

        internal OracleSourceReader(OracleConnection connection, OracleCommand command, OracleDataReader reader, int checkpointOrdinal)
        {
            _connection = connection;
            _command = command;
            _reader = reader;
            CheckpointOrdinal = checkpointOrdinal;
            for (var i = 0; i < reader.FieldCount; i++) _columns.Add(reader.GetName(i));
        }

        public IReadOnlyList<string> Columns { get { return _columns; } }
        public int CheckpointOrdinal { get; private set; }

        public async Task<List<object[]>> ReadAsync(int maxRows, CancellationToken cancellationToken)
        {
            var rows = new List<object[]>(maxRows);
            while (rows.Count < maxRows && await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var values = new object[_reader.FieldCount];
                for (var i = 0; i < values.Length; i++) values[i] = ReadValue(i);
                rows.Add(values);
            }
            return rows;
        }

        private object ReadValue(int ordinal)
        {
            if (_reader.IsDBNull(ordinal)) return DBNull.Value;
            var name = _reader.GetDataTypeName(ordinal).ToUpperInvariant();
            if (name == "LONG" || name == "LONG RAW" || name == "XMLTYPE" || name.Contains("OBJECT", StringComparison.Ordinal))
                throw new UnsupportedColumnTypeException(_reader.GetName(ordinal) + " 열의 " + name + " 형식은 이관 엔진이 지원하지 않습니다.");

            // ODP.NET의 GetDataTypeName은 "TimeStampTZ"처럼 공급자 이름을 돌려주기도 해서 "TIME ZONE" 문자열로는 못 가른다 —
            // 실제 Oracle(TIMESTAMP(6) WITH TIME ZONE 열)에서 GetOracleTimeStamp로 읽다 "Specified cast is not valid"가 났다.
            // 공급자 고유 형식(OracleTimeStampTZ 등)으로 가르면 이름 표기와 상관없다.
            try
            {
                var kind = _reader.GetProviderSpecificFieldType(ordinal);
                if (kind == typeof(OracleDecimal)) return _reader.GetOracleDecimal(ordinal);
                if (kind == typeof(OracleDate)) return _reader.GetDateTime(ordinal);
                if (kind == typeof(OracleTimeStampTZ)) return _reader.GetOracleTimeStampTZ(ordinal);
                if (kind == typeof(OracleTimeStampLTZ)) return _reader.GetOracleTimeStampLTZ(ordinal);
                if (kind == typeof(OracleTimeStamp)) return _reader.GetOracleTimeStamp(ordinal).Value;
                if (kind == typeof(OracleClob))
                {
                    using (var clob = _reader.GetOracleClob(ordinal)) return clob.Value;
                }
                if (kind == typeof(OracleBlob))
                {
                    using (var blob = _reader.GetOracleBlob(ordinal)) return blob.Value;
                }
                if (kind == typeof(OracleBinary)) return (byte[])_reader.GetValue(ordinal);
                if (kind == typeof(OracleString)) return _reader.GetString(ordinal);
                return _reader.GetValue(ordinal);
            }
            catch (InvalidCastException ex)
            {
                // 어느 열인지 모르면 로그만 보고 고칠 수 없다
                throw new InvalidOperationException(_reader.GetName(ordinal) + " 열(" + name + ") 값을 읽지 못했습니다: " + ex.Message, ex);
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _command.Dispose();
            _connection.Dispose();
        }
    }
}
