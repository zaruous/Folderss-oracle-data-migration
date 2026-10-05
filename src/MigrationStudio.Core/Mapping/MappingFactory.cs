using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MigrationStudio.Core.Types;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Mapping
{
    public static class MappingFactory
    {
        private static readonly Regex SqlNameRx = new Regex(@"[^A-Z0-9_$#]", RegexOptions.CultureInvariant);

        public static MappingModel CreateTableMapping(
            SchemaMetadata sourceMeta,
            SchemaMetadata targetMeta,
            string source,
            string target,
            string mode)
        {
            var src = sourceMeta != null ? sourceMeta.FindTable(source) : null;
            var tgt = targetMeta != null ? targetMeta.FindTable(target) : null;
            var srcPk = src != null ? src.Columns.FirstOrDefault(c => c.PrimaryKey) : null;
            var mergeKey = tgt != null
                ? tgt.Columns.Where(c => c.PrimaryKey).Select(c => c.Name).ToList()
                : new List<string>();
            var resolvedMode = mode;
            if (string.IsNullOrEmpty(resolvedMode))
            {
                resolvedMode = tgt != null && tgt.Rows.HasValue && tgt.Rows.Value > 0 ? WriteModes.Merge : WriteModes.InsertOnly;
            }

            var mapped = src != null && tgt != null
                ? MappingService.AutoMapColumns(src.Columns, tgt.Columns)
                : new List<AutoMappedColumn>();

            return new MappingModel
            {
                Id = MappingModel.NewId(),
                Use = true,
                SourceType = SourceTypes.Table,
                Source = source,
                Target = target,
                Mode = resolvedMode,
                MergeKey = mergeKey,
                CheckpointColumn = srcPk != null ? srcPk.Name : null,
                Columns = mapped.Select(m => m.Mapping).ToList()
            };
        }

        public static MappingModel CreateSqlMapping(
            string name,
            string target,
            string mode,
            string startTable,
            SchemaMetadata sourceMeta,
            SchemaMetadata targetMeta)
        {
            var normalizedName = NormalizeSqlName(name);
            var start = string.IsNullOrEmpty(startTable)
                ? new StarterSqlResult { Sql = "SELECT\n    \nFROM \nWHERE ", Binds = new List<BindParameter>(), CheckpointColumn = null }
                : StarterSql(startTable, sourceMeta);
            var tgt = targetMeta != null ? targetMeta.FindTable(target) : null;
            var mergeKey = tgt != null
                ? tgt.Columns.Where(c => c.PrimaryKey).Select(c => c.Name).ToList()
                : new List<string>();

            var mapping = new MappingModel
            {
                Id = MappingModel.NewId(),
                Use = true,
                SourceType = SourceTypes.Sql,
                Source = normalizedName,
                Target = target,
                Mode = string.IsNullOrEmpty(mode) ? WriteModes.Merge : mode,
                Sql = start.Sql,
                Binds = start.Binds,
                CheckpointColumn = start.CheckpointColumn,
                MergeKey = mergeKey,
                Columns = new List<ColumnMapping>()
            };

            if (tgt != null && sourceMeta != null)
            {
                var virtualInfo = SqlSourceAnalyzer.Analyze(mapping, sourceMeta);
                if (virtualInfo.Table != null && virtualInfo.Table.Columns.Count > 0)
                {
                    mapping.Columns = MappingService.AutoMapColumns(virtualInfo.Table.Columns, tgt.Columns)
                        .Select(m => m.Mapping)
                        .ToList();
                }
            }

            return mapping;
        }

        public static StarterSqlResult StarterSql(string table, SchemaMetadata sourceMeta)
        {
            var t = sourceMeta != null ? sourceMeta.FindTable(table) : null;
            if (t == null)
            {
                return new StarterSqlResult { Sql = "SELECT\n    \nFROM \nWHERE ", Binds = new List<BindParameter>(), CheckpointColumn = null };
            }

            var pk = t.Columns.FirstOrDefault(c => c.PrimaryKey);
            var lines = t.Columns.Select(c => "    T." + c.Name + " AS " + c.Name).ToList();
            var sql = "SELECT\n" + string.Join(",\n", lines) + "\nFROM " + t.Name + " T";
            List<BindParameter> binds = new List<BindParameter>();
            string cp = null;
            if (pk != null)
            {
                sql += "\nWHERE T." + pk.Name + " > :LAST_ID";
                cp = pk.Name;
                var isNumber = OracleType.Parse(pk.Type) != null && OracleType.Parse(pk.Type).Base == "NUMBER";
                binds.Add(new BindParameter
                {
                    Name = "LAST_ID",
                    Type = isNumber ? "NUMBER" : "VARCHAR2",
                    Value = isNumber ? "0" : " ",
                    FromCheckpoint = true
                });
            }

            return new StarterSqlResult { Sql = sql, Binds = binds, CheckpointColumn = cp };
        }

        public static string NormalizeSqlName(string name)
        {
            var upper = (name ?? "").Trim().ToUpperInvariant();
            return SqlNameRx.Replace(upper, "_");
        }

        public sealed class StarterSqlResult
        {
            public string Sql;
            public List<BindParameter> Binds;
            public string CheckpointColumn;
        }
    }
}
