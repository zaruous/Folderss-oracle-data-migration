using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Settings;
using Oracle.ManagedDataAccess.Client;

namespace MigrationStudio.Core.Adapters.Oracle
{
    public sealed partial class OracleDatabaseAdapter : IDatabaseAdapter
    {
        internal static readonly OracleDatabaseAdapter Instance = new OracleDatabaseAdapter();

        /// <summary>질의별 ms 로그(OracleIT·성능 분석). null이면 기록하지 않음.</summary>
        public static Action<string, long> Trace;

        private OracleDatabaseAdapter()
        {
        }

        public string Kind
        {
            get { return "oracle"; }
        }

        public string Title
        {
            get { return "Oracle"; }
        }

        public Task<ConnectionTestResult> TestAsync(ConnectionTarget target, bool readOnly, CancellationToken cancellationToken)
        {
            return Task.Run(() => TestCore(target, readOnly, cancellationToken), cancellationToken);
        }

        public Task<SchemaMetadata> LoadMetadataAsync(ConnectionTarget target, string schema, CancellationToken cancellationToken)
        {
            return Task.Run(() => LoadMetadataCore(target, schema, cancellationToken), cancellationToken);
        }

        public Task<ControlStoreCheck> CheckControlStoreAsync(
            ConnectionTarget target,
            string schema,
            string prefix,
            string store,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => CheckControlStoreCore(target, schema, prefix, store, cancellationToken), cancellationToken);
        }

        private static ConnectionTestResult TestCore(ConnectionTarget target, bool readOnly, CancellationToken cancellationToken)
        {
            var result = new ConnectionTestResult { TestedAt = DateTime.Now };
            var validation = ConnectionTarget.Validate(target);
            if (validation != null)
            {
                result.Ok = false;
                result.Error = validation;
                return result;
            }

            OracleConnectionHelper.EnsureInBandBreak();
            try
            {
                using (var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(target)))
                {
                    connection.Open();
                    cancellationToken.ThrowIfCancellationRequested();

                    result.ServerVersion = connection.ServerVersion;
                    result.Version = OracleVersionText.FromServerVersion(result.ServerVersion);

                    result.Banner = TryScalar(connection,
                        "SELECT BANNER FROM V$VERSION WHERE ROWNUM = 1",
                        cancellationToken);

                    ReadEnvContext(connection, result, cancellationToken);
                    result.Nls = ReadNls(connection, cancellationToken);
                    result.LatencyMs = MeasureLatency(connection, cancellationToken);

                    if (readOnly)
                    {
                        using (var ro = OracleConnectionHelper.CreateCommand(connection, "SET TRANSACTION READ ONLY", cancellationToken))
                        {
                            ro.ExecuteNonQuery();
                        }

                        using (var rb = OracleConnectionHelper.CreateCommand(connection, "ROLLBACK", cancellationToken))
                        {
                            rb.ExecuteNonQuery();
                        }
                    }

                    result.Ok = true;
                }
            }
            catch (Exception ex)
            {
                if (OracleErrors.IsCancellation(ex))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                result.Ok = false;
                result.Error = OracleErrors.Describe(ex);
                result.ErrorCode = OracleErrors.CodeOf(ex);
            }

            return result;
        }

        private static void ReadEnvContext(OracleConnection connection, ConnectionTestResult result, CancellationToken cancellationToken)
        {
            using (var cmd = OracleConnectionHelper.CreateCommand(connection,
                "SELECT SYS_CONTEXT('USERENV', 'DB_NAME'), SYS_CONTEXT('USERENV', 'SERVICE_NAME'), SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM DUAL",
                cancellationToken))
            using (var reader = cmd.ExecuteReader())
            {
                if (reader.Read())
                {
                    result.DbName = ReadString(reader, 0);
                    result.ServiceName = ReadString(reader, 1);
                    result.CurrentSchema = ReadString(reader, 2);
                }
            }
        }

        private static NlsInfo ReadNls(OracleConnection connection, CancellationToken cancellationToken)
        {
            var nls = new NlsInfo();
            using (var cmd = OracleConnectionHelper.CreateCommand(connection,
                "SELECT PARAMETER, VALUE FROM NLS_DATABASE_PARAMETERS WHERE PARAMETER IN ('NLS_CHARACTERSET', 'NLS_NCHAR_CHARACTERSET', 'NLS_LENGTH_SEMANTICS')",
                cancellationToken))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var key = ReadString(reader, 0);
                    var val = ReadString(reader, 1);
                    if (string.Equals(key, "NLS_CHARACTERSET", StringComparison.Ordinal))
                    {
                        nls.CharacterSet = val;
                    }
                    else if (string.Equals(key, "NLS_NCHAR_CHARACTERSET", StringComparison.Ordinal))
                    {
                        nls.NCharCharacterSet = val;
                    }
                    else if (string.Equals(key, "NLS_LENGTH_SEMANTICS", StringComparison.Ordinal))
                    {
                        nls.LengthSemantics = val;
                    }
                }
            }

            return nls;
        }

        private static int? MeasureLatency(OracleConnection connection, CancellationToken cancellationToken)
        {
            var samples = new long[3];
            for (var i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                using (var cmd = OracleConnectionHelper.CreateCommand(connection, "SELECT 1 FROM DUAL", cancellationToken))
                {
                    cmd.ExecuteScalar();
                }

                sw.Stop();
                samples[i] = sw.ElapsedMilliseconds;
            }

            Array.Sort(samples);
            return (int)Math.Round((double)samples[1], MidpointRounding.AwayFromZero);
        }

        private static string TryScalar(OracleConnection connection, string sql, CancellationToken cancellationToken)
        {
            try
            {
                using (var cmd = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken))
                {
                    var value = cmd.ExecuteScalar();
                    return value == null || value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                }
            }
            catch (OracleException ex) when (ex.Number == 942)
            {
                return null;
            }
        }

        private static SchemaMetadata LoadMetadataCore(ConnectionTarget target, string schema, CancellationToken cancellationToken)
        {
            var validation = ConnectionTarget.Validate(target);
            if (validation != null)
            {
                throw new AdapterException(new InvalidOperationException(validation));
            }

            OracleConnectionHelper.EnsureInBandBreak();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using (var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(target)))
                {
                    connection.Open();
                    cancellationToken.ThrowIfCancellationRequested();

                    var owner = NormalizeSchema(schema);
                    if (string.IsNullOrEmpty(owner))
                    {
                        owner = ReadCurrentSchema(connection, cancellationToken);
                    }

                    var meta = new SchemaMetadata
                    {
                        Schema = owner,
                        LoadedAt = DateTime.Now,
                        Cached = false
                    };

                    var currentUser = ReadCurrentSchema(connection, cancellationToken);
                    var ownSchema = string.Equals(owner, currentUser, StringComparison.OrdinalIgnoreCase);

                    var tables = TraceQuery("tables", () => LoadTables(connection, owner, ownSchema, cancellationToken));
                    TraceQuery("columns", () => { LoadColumns(connection, owner, ownSchema, tables, cancellationToken); return 0; });
                    var cs = OracleConnectionHelper.BuildConnectionString(target);
                    System.Threading.Tasks.Task.WaitAll(new[]
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            using (var c2 = new OracleConnection(cs))
                            {
                                c2.Open();
                                TraceQuery("constraints", () => { ApplyConstraints(c2, owner, ownSchema, tables, cancellationToken); return 0; });
                            }
                        }, cancellationToken),
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            using (var c3 = new OracleConnection(cs))
                            {
                                c3.Open();
                                TraceQuery("statistics", () => { ApplyStatistics(c3, owner, ownSchema, tables, cancellationToken); return 0; });
                            }
                        }, cancellationToken)
                    });

                    meta.Tables = tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
                    if (ownSchema)
                    {
                        meta.Tablespace = TryLoadTablespace(connection, cancellationToken);
                    }

                    stopwatch.Stop();
                    meta.ElapsedMs = stopwatch.ElapsedMilliseconds;
                    return meta;
                }
            }
            catch (Exception ex)
            {
                if (OracleErrors.IsCancellation(ex))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new AdapterException(ex);
            }
        }

        private static string NormalizeSchema(string schema)
        {
            if (string.IsNullOrWhiteSpace(schema))
            {
                return null;
            }

            return schema.Trim().ToUpperInvariant();
        }

        private static string ReadCurrentSchema(OracleConnection connection, CancellationToken cancellationToken)
        {
            using (var cmd = OracleConnectionHelper.CreateCommand(connection,
                "SELECT SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM DUAL", cancellationToken))
            {
                var value = cmd.ExecuteScalar();
                return value == null || value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture).ToUpperInvariant();
            }
        }

        private static Dictionary<string, TableMetadata> LoadTables(OracleConnection connection, string owner, bool ownSchema, CancellationToken cancellationToken)
        {
            var sql = ownSchema
                ? "SELECT T.TABLE_NAME AS OBJECT_NAME, 'TABLE' AS KIND, T.NUM_ROWS, T.AVG_ROW_LEN, T.TABLESPACE_NAME, C.COMMENTS " +
                  "FROM USER_TABLES T " +
                  "LEFT JOIN USER_TAB_COMMENTS C ON C.TABLE_NAME = T.TABLE_NAME " +
                  "WHERE T.NESTED = 'NO' AND T.SECONDARY = 'N' AND T.TABLE_NAME NOT LIKE 'BIN$%' " +
                  "UNION ALL " +
                  "SELECT V.VIEW_NAME, 'VIEW', NULL, NULL, NULL, C.COMMENTS " +
                  "FROM USER_VIEWS V " +
                  "LEFT JOIN USER_TAB_COMMENTS C ON C.TABLE_NAME = V.VIEW_NAME " +
                  "ORDER BY 1"
                : "SELECT T.TABLE_NAME AS OBJECT_NAME, 'TABLE' AS KIND, T.NUM_ROWS, T.AVG_ROW_LEN, T.TABLESPACE_NAME, C.COMMENTS " +
                  "FROM ALL_TABLES T " +
                  "LEFT JOIN ALL_TAB_COMMENTS C ON C.OWNER = T.OWNER AND C.TABLE_NAME = T.TABLE_NAME " +
                  "WHERE T.OWNER = :OWNER AND T.NESTED = 'NO' AND T.SECONDARY = 'N' AND T.TABLE_NAME NOT LIKE 'BIN$%' " +
                  "UNION ALL " +
                  "SELECT V.VIEW_NAME, 'VIEW', NULL, NULL, NULL, C.COMMENTS " +
                  "FROM ALL_VIEWS V " +
                  "LEFT JOIN ALL_TAB_COMMENTS C ON C.OWNER = V.OWNER AND C.TABLE_NAME = V.VIEW_NAME " +
                  "WHERE V.OWNER = :OWNER " +
                  "ORDER BY 1";

            var map = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken))
            {
                if (!ownSchema)
                {
                    cmd.Parameters.Add("OWNER", owner);
                }

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var name = ReadString(reader, 0);
                        var table = new TableMetadata
                        {
                            Name = name,
                            Kind = ReadString(reader, 1) ?? "TABLE",
                            Rows = ReadNullableLong(reader, 2),
                            AvgRowLength = ReadInt(reader, 3),
                            TablespaceName = ReadString(reader, 4),
                            Comment = ReadString(reader, 5) ?? ""
                        };
                        map[name] = table;
                    }
                }
            }

            return map;
        }

        private static void LoadColumns(
            OracleConnection connection,
            string owner,
            bool ownSchema,
            Dictionary<string, TableMetadata> tables,
            CancellationToken cancellationToken)
        {
            var sql = ownSchema
                ? "SELECT C.TABLE_NAME, C.COLUMN_ID, C.COLUMN_NAME, C.DATA_TYPE, C.DATA_LENGTH, C.CHAR_LENGTH, C.CHAR_USED, " +
                  "C.DATA_PRECISION, C.DATA_SCALE, C.NULLABLE, C.DATA_DEFAULT, CC.COMMENTS " +
                  "FROM USER_TAB_COLUMNS C " +
                  "LEFT JOIN USER_COL_COMMENTS CC ON CC.TABLE_NAME = C.TABLE_NAME AND CC.COLUMN_NAME = C.COLUMN_NAME " +
                  "ORDER BY C.TABLE_NAME, C.COLUMN_ID"
                : "SELECT C.TABLE_NAME, C.COLUMN_ID, C.COLUMN_NAME, C.DATA_TYPE, C.DATA_LENGTH, C.CHAR_LENGTH, C.CHAR_USED, " +
                  "C.DATA_PRECISION, C.DATA_SCALE, C.NULLABLE, C.DATA_DEFAULT, CC.COMMENTS " +
                  "FROM ALL_TAB_COLUMNS C " +
                  "LEFT JOIN ALL_COL_COMMENTS CC ON CC.OWNER = C.OWNER AND CC.TABLE_NAME = C.TABLE_NAME AND CC.COLUMN_NAME = C.COLUMN_NAME " +
                  "WHERE C.OWNER = :OWNER " +
                  "ORDER BY C.TABLE_NAME, C.COLUMN_ID";

            using (var cmd = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken))
            {
                cmd.InitialLONGFetchSize = 4000;
                if (!ownSchema)
                {
                    cmd.Parameters.Add("OWNER", owner);
                }

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var tableName = ReadString(reader, 0);
                        TableMetadata table;
                        if (!tables.TryGetValue(tableName, out table))
                        {
                            continue;
                        }

                        var precision = ReadNullableInt(reader, 7);
                        var scale = ReadNullableInt(reader, 8);
                        if (scale.HasValue && (scale.Value == 127 || scale.Value == -127))
                        {
                            if (!precision.HasValue)
                            {
                                scale = null;
                            }
                        }

                        var column = new ColumnMetadata
                        {
                            Name = ReadString(reader, 2),
                            Type = OracleColumnTypeFormatter.Format(
                                ReadString(reader, 3),
                                ReadInt(reader, 4),
                                ReadInt(reader, 5),
                                ReadString(reader, 6),
                                precision,
                                scale),
                            Nullable = !string.Equals(ReadString(reader, 9), "N", StringComparison.OrdinalIgnoreCase),
                            DefaultValue = TrimDefault(ReadString(reader, 10)),
                            Comment = ReadString(reader, 11) ?? ""
                        };
                        table.Columns.Add(column);
                    }
                }
            }
        }

        private static void ApplyConstraints(
            OracleConnection connection,
            string owner,
            bool ownSchema,
            Dictionary<string, TableMetadata> tables,
            CancellationToken cancellationToken)
        {
            var sql = ownSchema
                ? "SELECT K.TABLE_NAME, K.CONSTRAINT_NAME, K.CONSTRAINT_TYPE, K.R_OWNER, K.R_CONSTRAINT_NAME, KC.COLUMN_NAME, KC.POSITION " +
                  "FROM USER_CONSTRAINTS K " +
                  "JOIN USER_CONS_COLUMNS KC ON KC.CONSTRAINT_NAME = K.CONSTRAINT_NAME " +
                  "WHERE K.CONSTRAINT_TYPE IN ('P', 'U', 'R') AND K.STATUS = 'ENABLED' " +
                  "ORDER BY K.TABLE_NAME, K.CONSTRAINT_NAME, KC.POSITION"
                : "SELECT K.TABLE_NAME, K.CONSTRAINT_NAME, K.CONSTRAINT_TYPE, K.R_OWNER, K.R_CONSTRAINT_NAME, KC.COLUMN_NAME, KC.POSITION " +
                  "FROM ALL_CONSTRAINTS K " +
                  "JOIN ALL_CONS_COLUMNS KC ON KC.OWNER = K.OWNER AND KC.CONSTRAINT_NAME = K.CONSTRAINT_NAME " +
                  "WHERE K.OWNER = :OWNER AND K.CONSTRAINT_TYPE IN ('P', 'U', 'R') AND K.STATUS = 'ENABLED' " +
                  "ORDER BY K.TABLE_NAME, K.CONSTRAINT_NAME, KC.POSITION";

            var pkByConstraint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fkBuilders = new Dictionary<string, ForeignKeyMetadata>(StringComparer.OrdinalIgnoreCase);
            var externalRefs = new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase);
            var fkRows = new List<Tuple<string, string, string, string, string>>();

            using (var cmd = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken))
            {
                if (!ownSchema)
                {
                    cmd.Parameters.Add("OWNER", owner);
                }

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var tableName = ReadString(reader, 0);
                        var constraintName = ReadString(reader, 1);
                        var type = ReadString(reader, 2);
                        var rOwner = ReadString(reader, 3);
                        var rConstraint = ReadString(reader, 4);
                        var columnName = ReadString(reader, 5);

                        TableMetadata table;
                        if (!tables.TryGetValue(tableName, out table))
                        {
                            continue;
                        }

                        if (type == "P")
                        {
                            var col = table.FindColumn(columnName);
                            if (col != null)
                            {
                                col.PrimaryKey = true;
                            }

                            pkByConstraint[constraintName] = tableName;
                        }
                        else if (type == "U")
                        {
                            pkByConstraint[constraintName] = tableName;
                        }
                        else if (type == "R")
                        {
                            fkRows.Add(Tuple.Create(tableName, constraintName, rOwner, rConstraint, columnName));
                            if (!string.IsNullOrEmpty(rOwner) && !string.IsNullOrEmpty(rConstraint)
                                && !string.Equals(rOwner, owner, StringComparison.OrdinalIgnoreCase))
                            {
                                var extKey = rOwner + "\0" + rConstraint;
                                if (!externalRefs.ContainsKey(extKey))
                                {
                                    externalRefs[extKey] = Tuple.Create(rOwner, rConstraint);
                                }
                            }
                        }
                    }
                }
            }

            var externalTable = ResolveExternalRefTables(connection, externalRefs.Values, cancellationToken);

            foreach (var row in fkRows)
            {
                TableMetadata table;
                if (!tables.TryGetValue(row.Item1, out table))
                {
                    continue;
                }

                string refTable = null;
                if (!string.IsNullOrEmpty(row.Item4))
                {
                    if (string.IsNullOrEmpty(row.Item3) || string.Equals(row.Item3, owner, StringComparison.OrdinalIgnoreCase))
                    {
                        pkByConstraint.TryGetValue(row.Item4, out refTable);
                    }
                    else
                    {
                        var extKey = row.Item3 + "\0" + row.Item4;
                        externalTable.TryGetValue(extKey, out refTable);
                    }
                }

                var key = row.Item1 + "\0" + row.Item2;
                ForeignKeyMetadata fk;
                if (!fkBuilders.TryGetValue(key, out fk))
                {
                    fk = new ForeignKeyMetadata { Name = row.Item2, RefTable = refTable };
                    fkBuilders[key] = fk;
                    table.ForeignKeys.Add(fk);
                }

                fk.Columns.Add(row.Item5);
            }
        }

        private static Dictionary<string, string> ResolveExternalRefTables(
            OracleConnection connection,
            IEnumerable<Tuple<string, string>> refs,
            CancellationToken cancellationToken)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<Tuple<string, string>>();
            foreach (var r in refs)
            {
                list.Add(r);
            }

            if (list.Count == 0)
            {
                return map;
            }

            var inParts = new List<string>();
            for (var i = 0; i < list.Count; i++)
            {
                inParts.Add("(:O" + i.ToString(CultureInfo.InvariantCulture) + ", :C" + i.ToString(CultureInfo.InvariantCulture) + ")");
            }

            var sql = "SELECT OWNER, CONSTRAINT_NAME, TABLE_NAME FROM ALL_CONSTRAINTS WHERE (OWNER, CONSTRAINT_NAME) IN ("
                + string.Join(", ", inParts) + ")";
            using (var cmd = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken))
            {
                for (var i = 0; i < list.Count; i++)
                {
                    cmd.Parameters.Add("O" + i.ToString(CultureInfo.InvariantCulture), list[i].Item1);
                    cmd.Parameters.Add("C" + i.ToString(CultureInfo.InvariantCulture), list[i].Item2);
                }

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var o = ReadString(reader, 0);
                        var c = ReadString(reader, 1);
                        var t = ReadString(reader, 2);
                        map[o + "\0" + c] = t;
                    }
                }
            }

            return map;
        }

        private static T TraceQuery<T>(string name, Func<T> action)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                return action();
            }
            finally
            {
                sw.Stop();
                if (Trace != null)
                {
                    Trace(name, sw.ElapsedMilliseconds);
                }
            }
        }

        private static void ApplyStatistics(
            OracleConnection connection,
            string owner,
            bool ownSchema,
            Dictionary<string, TableMetadata> tables,
            CancellationToken cancellationToken)
        {
            var sql = ownSchema
                ? "SELECT TABLE_NAME, COLUMN_NAME, NUM_NULLS, NUM_DISTINCT FROM USER_TAB_COL_STATISTICS"
                : "SELECT TABLE_NAME, COLUMN_NAME, NUM_NULLS, NUM_DISTINCT FROM ALL_TAB_COL_STATISTICS WHERE OWNER = :OWNER";

            using (var cmd = OracleConnectionHelper.CreateCommand(connection, sql, cancellationToken))
            {
                if (!ownSchema)
                {
                    cmd.Parameters.Add("OWNER", owner);
                }
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var tableName = ReadString(reader, 0);
                        var columnName = ReadString(reader, 1);
                        TableMetadata table;
                        if (!tables.TryGetValue(tableName, out table))
                        {
                            continue;
                        }

                        var col = table.FindColumn(columnName);
                        if (col == null)
                        {
                            continue;
                        }

                        col.Stats.Nulls = ReadNullableLong(reader, 2);
                        col.Stats.Distinct = ReadNullableLong(reader, 3);
                    }
                }
            }
        }

        private static TablespaceInfo TryLoadTablespace(OracleConnection connection, CancellationToken cancellationToken)
        {
            try
            {
                string tsName;
                using (var cmd = OracleConnectionHelper.CreateCommand(connection,
                    "SELECT DEFAULT_TABLESPACE FROM USER_USERS", cancellationToken))
                {
                    var value = cmd.ExecuteScalar();
                    tsName = value == null || value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                }

                if (string.IsNullOrEmpty(tsName))
                {
                    return null;
                }

                var info = new TablespaceInfo { Name = tsName };

                using (var freeCmd = OracleConnectionHelper.CreateCommand(connection,
                    "SELECT NVL(SUM(BYTES), 0) FROM USER_FREE_SPACE WHERE TABLESPACE_NAME = :TS", cancellationToken))
                {
                    freeCmd.Parameters.Add("TS", tsName);
                    var freeBytes = Convert.ToDecimal(freeCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                    // 소수 셋째 자리까지 — 첫째 자리면 XE의 USERS(여유 27MB)가 0.0이 되어 검증이 "여유 0 GB"라고 한다
                    info.FreeGb = Math.Round((double)(freeBytes / 1024m / 1024m / 1024m), 3, MidpointRounding.AwayFromZero);
                }

                // 데이터 파일 자동 확장 여유 — DBA_DATA_FILES는 권한이 있어야 읽힌다(없으면 ORA-00942 → 모름으로 둔다)
                try
                {
                    using (var autoCmd = OracleConnectionHelper.CreateCommand(connection,
                        "SELECT NVL(SUM(CASE WHEN AUTOEXTENSIBLE = 'YES' AND MAXBYTES > BYTES THEN MAXBYTES - BYTES ELSE 0 END), 0)"
                        + " FROM DBA_DATA_FILES WHERE TABLESPACE_NAME = :TS", cancellationToken))
                    {
                        autoCmd.Parameters.Add("TS", tsName);
                        var autoBytes = Convert.ToDecimal(autoCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                        info.AutoExtendGb = Math.Round((double)(autoBytes / 1024m / 1024m / 1024m), 3, MidpointRounding.AwayFromZero);
                    }
                }
                catch (OracleException)
                {
                    info.AutoExtendGb = null;
                }

                using (var quotaCmd = OracleConnectionHelper.CreateCommand(connection,
                    "SELECT MAX_BYTES, BYTES FROM USER_TS_QUOTAS WHERE TABLESPACE_NAME = :TS", cancellationToken))
                {
                    quotaCmd.Parameters.Add("TS", tsName);
                    using (var reader = quotaCmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            var maxBytes = ReadNullableLong(reader, 0);
                            var usedBytes = ReadNullableLong(reader, 1) ?? 0;
                            if (maxBytes.HasValue && maxBytes.Value >= 0)
                            {
                                info.QuotaLeftGb = Math.Round((maxBytes.Value - usedBytes) / 1024.0 / 1024.0 / 1024.0, 1, MidpointRounding.AwayFromZero);
                            }
                        }
                    }
                }

                return info;
            }
            catch (OracleException)
            {
                return null;
            }
        }

        private static ControlStoreCheck CheckControlStoreCore(
            ConnectionTarget target,
            string schema,
            string prefix,
            string store,
            CancellationToken cancellationToken)
        {
            var validation = ConnectionTarget.Validate(target);
            if (validation != null)
            {
                throw new AdapterException(new InvalidOperationException(validation));
            }

            var normalizedStore = string.IsNullOrWhiteSpace(store) ? CheckpointStores.Auto : store.Trim().ToUpperInvariant();
            var prefixUpper = (prefix ?? "").Trim().ToUpperInvariant();
            var owner = NormalizeSchema(schema);
            if (string.IsNullOrEmpty(owner))
            {
                owner = null;
            }

            OracleConnectionHelper.EnsureInBandBreak();
            try
            {
                using (var connection = new OracleConnection(OracleConnectionHelper.BuildConnectionString(target)))
                {
                    connection.Open();
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(owner))
                    {
                        owner = ReadCurrentSchema(connection, cancellationToken);
                    }

                    var check = new ControlStoreCheck();
                    check.TablesExist = CheckpointTableExists(connection, owner, prefixUpper, cancellationToken);
                    check.CanCreate = CanCreateCheckpointTable(connection, owner, cancellationToken);

                    if (string.Equals(normalizedStore, CheckpointStores.Target, StringComparison.Ordinal))
                    {
                        check.Resolved = CheckpointStores.Target;
                        check.Reason = DescribeControlReason(check, CheckpointStores.Target);
                        return check;
                    }

                    if (string.Equals(normalizedStore, CheckpointStores.Local, StringComparison.Ordinal))
                    {
                        check.Resolved = CheckpointStores.Local;
                        check.Reason = DescribeControlReason(check, CheckpointStores.Local);
                        return check;
                    }

                    if (check.TablesExist || check.CanCreate)
                    {
                        check.Resolved = CheckpointStores.Target;
                        check.Reason = check.TablesExist
                            ? prefixUpper + "CHECKPOINT 테이블이 있어 대상 DB에 저장합니다"
                            : "대상 스키마에 제어 테이블을 만들 수 있어 DB에 저장합니다";
                    }
                    else
                    {
                        check.Resolved = CheckpointStores.Local;
                        check.Reason = "제어 테이블을 만들 권한이 없어 로컬 파일에 저장합니다";
                    }

                    return check;
                }
            }
            catch (Exception ex)
            {
                if (OracleErrors.IsCancellation(ex))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new AdapterException(ex);
            }
        }

        private static string DescribeControlReason(ControlStoreCheck check, string resolved)
        {
            if (string.Equals(resolved, CheckpointStores.Local, StringComparison.Ordinal))
            {
                return "로컬 파일 저장으로 설정되어 있습니다";
            }

            if (check.TablesExist)
            {
                return "제어 테이블이 이미 있어 대상 DB에 저장합니다";
            }

            return "대상 DB 저장으로 설정되어 있습니다";
        }

        private static bool CheckpointTableExists(OracleConnection connection, string owner, string prefix, CancellationToken cancellationToken)
        {
            using (var cmd = OracleConnectionHelper.CreateCommand(connection,
                "SELECT COUNT(*) FROM ALL_TABLES WHERE OWNER = :OWNER AND TABLE_NAME = :TNAME", cancellationToken))
            {
                cmd.Parameters.Add("OWNER", owner);
                cmd.Parameters.Add("TNAME", prefix + "CHECKPOINT");
                var count = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                return count > 0;
            }
        }

        private static bool CanCreateCheckpointTable(OracleConnection connection, string owner, CancellationToken cancellationToken)
        {
            var current = ReadCurrentSchema(connection, cancellationToken);
            var ownSchema = string.Equals(owner, current, StringComparison.OrdinalIgnoreCase);
            var hasCreateAny = HasSessionPrivilege(connection, "CREATE ANY TABLE", cancellationToken);
            if (hasCreateAny)
            {
                return true;
            }

            return ownSchema && HasSessionPrivilege(connection, "CREATE TABLE", cancellationToken);
        }

        private static bool HasSessionPrivilege(OracleConnection connection, string privilege, CancellationToken cancellationToken)
        {
            using (var cmd = OracleConnectionHelper.CreateCommand(connection,
                "SELECT COUNT(*) FROM SESSION_PRIVS WHERE PRIVILEGE = :PRIV", cancellationToken))
            {
                cmd.Parameters.Add("PRIV", privilege);
                var count = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                return count > 0;
            }
        }

        private static string TrimDefault(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim();
        }

        private static string ReadString(OracleDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return null;
            }

            return reader.GetString(ordinal);
        }

        private static int ReadInt(OracleDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return 0;
            }

            return Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static int? ReadNullableInt(OracleDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return null;
            }

            return Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static long? ReadNullableLong(OracleDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
            {
                return null;
            }

            return Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }
    }
}
