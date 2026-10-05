using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;
using Oracle.ManagedDataAccess.Client;

namespace MigrationStudio.Core.Adapters.Oracle.Engine
{
    public sealed class OracleSourceProbe : ISourceProbe
    {
        private readonly EndpointSpec _source;
        private readonly EndpointSpec _target;

        public OracleSourceProbe(EndpointSpec source, EndpointSpec target)
        {
            _source = source;
            _target = target;
        }

        public async Task<long> CountAsync(PlanItem item, CancellationToken cancellationToken)
        {
            var from = item.Mapping.IsSql
                ? "(" + item.Mapping.Sql + ") S"
                : OracleEngineSql.Identifier(_source.Schema) + "." + OracleEngineSql.Identifier(item.Mapping.Source);
            var where = string.IsNullOrWhiteSpace(item.Mapping.Where) ? "" : " WHERE " + item.Mapping.Where;
            return await ScalarAsync(_source, "SELECT COUNT(*) FROM " + from + where, item, cancellationToken).ConfigureAwait(false);
        }

        public async Task<List<KeyRange>> RangesAsync(PlanItem item, int workers, string lastValue, CancellationToken cancellationToken)
        {
            var checkpoint = OracleEngineSql.Identifier(item.Mapping.CheckpointColumn);
            var from = item.Mapping.IsSql ? "(" + item.Mapping.Sql + ") S" : OracleEngineSql.Identifier(_source.Schema) + "." + OracleEngineSql.Identifier(item.Mapping.Source);
            var predicates = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Mapping.Where)) predicates.Add("(" + item.Mapping.Where.Trim() + ")");
            if (lastValue != null) predicates.Add(checkpoint + ">:LAST_ID");
            var sql = "SELECT MIN(K),MAX(K),COUNT(*) FROM (SELECT " + checkpoint + " K,NTILE(:WORKERS) OVER (ORDER BY " + checkpoint + ") B FROM " + from +
                      (predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates)) + ") GROUP BY B ORDER BY B";
            var ranges = new List<KeyRange>();
            using (var connection = await OpenAsync(_source, cancellationToken).ConfigureAwait(false))
            using (var command = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken))
            {
                command.Parameters.Add("WORKERS", OracleDbType.Int32).Value = workers;
                AddMappingBinds(command, item);
                if (lastValue != null) AddCheckpointBind(command, item, "LAST_ID", lastValue);
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        ranges.Add(new KeyRange
                        {
                            From = FormatValue(reader.GetValue(0)),
                            To = FormatValue(reader.GetValue(1)),
                            Rows = Convert.ToInt64(reader.GetValue(2))
                        });
                    }
                }
            }
            return ranges;
        }

        public Task<long> ExistingKeyCountAsync(PlanItem item, CancellationToken cancellationToken)
        {
            return ScalarAsync(_target, "SELECT COUNT(*) FROM " + OracleEngineSql.Identifier(_target.Schema) + "." + OracleEngineSql.Identifier(item.Mapping.Target), null, cancellationToken);
        }

        private static async Task<long> ScalarAsync(EndpointSpec endpoint, string sql, PlanItem item, CancellationToken cancellationToken)
        {
            using (var connection = await OpenAsync(endpoint, cancellationToken).ConfigureAwait(false))
            using (var command = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken))
            {
                if (item != null) AddMappingBinds(command, item);
                return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            }
        }

        private static void AddMappingBinds(OracleCommand command, PlanItem item)
        {
            foreach (var bind in item.Mapping.Binds.Where(b => command.CommandText.IndexOf(":" + b.Name, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var value = bind.FromCheckpoint ? null : bind.Value;
                var parameter = command.Parameters.Add(bind.Name, BindType(bind.Type));
                parameter.Value = ParseValue(value, parameter.OracleDbType);
            }
        }

        private static void AddCheckpointBind(OracleCommand command, PlanItem item, string name, string value)
        {
            foreach (OracleParameter existing in command.Parameters)
            {
                if (string.Equals(existing.ParameterName, name, StringComparison.OrdinalIgnoreCase))
                {
                    existing.Value = ParseValue(value, existing.OracleDbType);
                    return;
                }
            }
            var column = item.SourceMetadata != null ? item.SourceMetadata.FindColumn(item.Mapping.CheckpointColumn) : null;
            var parameter = command.Parameters.Add(name, BindType(column != null ? column.Type : null));
            parameter.Value = ParseValue(value, parameter.OracleDbType);
        }

        private static OracleDbType BindType(string type)
        {
            var text = (type ?? "VARCHAR2").ToUpperInvariant();
            if (text.StartsWith("NUMBER", StringComparison.Ordinal)) return OracleDbType.Decimal;
            if (text.StartsWith("DATE", StringComparison.Ordinal)) return OracleDbType.Date;
            if (text.StartsWith("TIMESTAMP", StringComparison.Ordinal)) return OracleDbType.TimeStamp;
            return OracleDbType.Varchar2;
        }

        private static object ParseValue(string value, OracleDbType type)
        {
            if (value == null) return DBNull.Value;
            if (type == OracleDbType.Decimal) return decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
            if (type == OracleDbType.Date || type == OracleDbType.TimeStamp) return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);
            return value;
        }

        private static string FormatValue(object value)
        {
            if (value is DateTime date) return date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static async Task<OracleConnection> OpenAsync(EndpointSpec endpoint, CancellationToken cancellationToken)
        {
            var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(endpoint.Connection));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
    }
}
