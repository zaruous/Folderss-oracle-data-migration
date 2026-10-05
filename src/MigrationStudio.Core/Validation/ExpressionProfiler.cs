using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Types;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Validation
{
    /// <summary>매핑·대상 열별 실측 통계(UI-MIG-003 SQL-2). 한 질의에 최대 8열.</summary>
    public sealed class ExpressionProfiler
    {
        private const int BatchSize = 8;
        private const long SampleRowThreshold = 1_000_000;

        private readonly IDatabaseAdapter _adapter;

        public ExpressionProfiler(IDatabaseAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        public async Task<Dictionary<string, Dictionary<string, ColumnStats>>> ProfileAsync(
            ValidationContext ctx,
            IList<MappingModel> mappings,
            Func<MappingModel, TableMetadata> sourceOf,
            CancellationToken ct)
        {
            var result = new Dictionary<string, Dictionary<string, ColumnStats>>(StringComparer.Ordinal);
            if (ctx == null || ctx.Source == null || ctx.SourceMeta == null || mappings == null)
            {
                return result;
            }

            foreach (var tm in mappings)
            {
                var source = sourceOf != null ? sourceOf(tm) : null;
                var target = ctx.TargetMeta != null ? ctx.TargetMeta.FindTable(tm.Target) : null;
                if (source == null || target == null || (source.IsVirtual && (source.Columns == null || source.Columns.Count == 0)))
                {
                    continue;
                }

                var cols = BuildProfileColumns(tm, source);
                if (cols.Count == 0)
                {
                    continue;
                }

                var measured = new Dictionary<string, ColumnStats>(StringComparer.Ordinal);
                var sample = ShouldSample(source, tm);
                var sqlCap = tm.IsSql;
                for (var i = 0; i < cols.Count; i += BatchSize)
                {
                    var batch = cols.Skip(i).Take(BatchSize).ToList();
                    var sql = ValidationSql.ProfileBatch(
                        tm,
                        ctx.SourceMeta.Schema,
                        batch.Select(c => c.Def).ToList(),
                        sample,
                        sqlCap);
                    var query = await QueryOneRowAsync(ctx, sql, ct).ConfigureAwait(false);
                    if (query == null)
                    {
                        continue;
                    }

                    foreach (var col in batch)
                    {
                        var stats = ReadStats(query, col);
                        if (stats != null)
                        {
                            measured[col.TargetName] = stats;
                        }
                    }
                }

                result[tm.Id] = measured;
            }

            return result;
        }

        private sealed class ColProfile
        {
            public string TargetName;
            public ValidationSql.ProfileColumn Def;
        }

        private static List<ColProfile> BuildProfileColumns(MappingModel tm, TableMetadata source)
        {
            var list = new List<ColProfile>();
            foreach (var cm in tm.Columns ?? new List<ColumnMapping>())
            {
                var expr = MappingService.ValueSource(cm);
                if (string.IsNullOrEmpty(expr))
                {
                    continue;
                }

                var info = MappingService.SourceInfo(cm, source.Columns);
                if (info != null && info.Error != null)
                {
                    continue;
                }

                var ot = OracleType.Parse(info != null ? info.Type : null);
                var prefix = SanitizePrefix(cm.Target);
                list.Add(new ColProfile
                {
                    TargetName = cm.Target,
                    Def = new ValidationSql.ProfileColumn
                    {
                        Prefix = prefix,
                        Expression = expr,
                        IsChar = ot != null && ot.IsChar,
                        IsNumber = ot != null && ot.IsNumber
                    }
                });
            }

            return list;
        }

        private static string SanitizePrefix(string name)
        {
            var n = (name ?? "C").ToUpperInvariant();
            var sb = new System.Text.StringBuilder();
            foreach (var ch in n)
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_')
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append('_');
                }
            }

            return sb.Length > 0 ? sb.ToString() : "C";
        }

        private static bool ShouldSample(TableMetadata source, MappingModel tm)
        {
            if (tm.IsSql)
            {
                return false;
            }

            return source.Rows != null && source.Rows.Value > SampleRowThreshold;
        }

        private async Task<QueryResult> QueryOneRowAsync(ValidationContext ctx, string sql, CancellationToken ct)
        {
            OracleSelectGuard.EnsureSelectOnly(sql);
            return await _adapter.QueryAsync(ctx.Source, ctx.SourceMeta.Schema, sql, null, 1, ct).ConfigureAwait(false);
        }

        private static ColumnStats ReadStats(QueryResult query, ColProfile col)
        {
            if (query == null || query.Columns == null || query.Rows == null || query.Rows.Count == 0)
            {
                return null;
            }

            var row = query.Rows[0];
            var stats = new ColumnStats();
            var p = col.Def.Prefix;
            if (col.Def.IsChar)
            {
                stats.Nulls = ReadLong(query, row, p + "_NULL_ROWS");
                stats.MaxLength = ReadInt(query, row, p + "_MAX_CHARS");
                var bytes = ReadInt(query, row, p + "_MAX_BYTES");
                if (bytes != null)
                {
                    stats.DigitsMaxLength = bytes;
                }

                stats.Blanks = ReadLong(query, row, p + "_BLANK_ROWS");
            }
            else if (col.Def.IsNumber)
            {
                stats.Max = ReadDecimal(query, row, p + "_MAX_ABS");
            }

            return stats;
        }

        private static int ColIndex(QueryResult query, string name)
        {
            for (var i = 0; i < query.Columns.Count; i++)
            {
                if (string.Equals(query.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static long? ReadLong(QueryResult query, string[] row, string colName)
        {
            var i = ColIndex(query, colName);
            if (i < 0 || i >= row.Length || string.IsNullOrEmpty(row[i]))
            {
                return null;
            }

            if (long.TryParse(row[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }

            return null;
        }

        private static int? ReadInt(QueryResult query, string[] row, string colName)
        {
            var l = ReadLong(query, row, colName);
            return l != null ? (int?)l.Value : null;
        }

        private static decimal? ReadDecimal(QueryResult query, string[] row, string colName)
        {
            var i = ColIndex(query, colName);
            if (i < 0 || i >= row.Length || string.IsNullOrEmpty(row[i]))
            {
                return null;
            }

            if (decimal.TryParse(row[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }

            return null;
        }
    }
}
