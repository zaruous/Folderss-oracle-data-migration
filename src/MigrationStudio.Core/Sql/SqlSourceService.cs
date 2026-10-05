using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Sql
{
    public sealed class SqlSourceService
    {
        private readonly IDatabaseAdapter _adapter;

        public SqlSourceService(IDatabaseAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        public async Task<SqlValidationResult> ValidateAsync(
            MappingModel sqlMapping,
            ConnectionTarget sourceTarget,
            string sourceSchema,
            SchemaMetadata sourceMeta,
            SchemaMetadata targetMeta,
            CancellationToken ct)
        {
            var parse = await _adapter.ParseSqlAsync(sourceTarget, sourceSchema, sqlMapping.Sql, ct).ConfigureAwait(false);
            if (!parse.Ok)
            {
                return ParseFailure(parse, sqlMapping);
            }

            var described = await _adapter.DescribeSqlAsync(sourceTarget, sourceSchema, sqlMapping.Sql, ct).ConfigureAwait(false);
            return SqlSourceAnalyzer.Validate(sqlMapping, sourceMeta, targetMeta, described);
        }

        public Task<QueryResult> PreviewAsync(
            MappingModel sqlMapping,
            ConnectionTarget sourceTarget,
            string sourceSchema,
            int rows,
            CancellationToken ct)
        {
            var sql = SqlProbe.SqlSourcePreview(sqlMapping, rows);
            var binds = (sqlMapping.Binds ?? new List<BindParameter>()).Select(SqlBind.From).ToList();
            return _adapter.QueryAsync(sourceTarget, sourceSchema, sql, binds, rows, ct);
        }

        public async Task<long?> EstimateRowsAsync(
            MappingModel sqlMapping,
            ConnectionTarget sourceTarget,
            string sourceSchema,
            SchemaMetadata sourceMeta,
            CancellationToken ct)
        {
            var local = SqlSourceAnalyzer.Analyze(sqlMapping, sourceMeta);
            if (local != null && local.Table != null && local.Table.Rows != null)
            {
                return local.Table.Rows;
            }

            return await _adapter.CountAsync(
                sourceTarget,
                sourceSchema,
                sqlMapping.Sql,
                (sqlMapping.Binds ?? new List<BindParameter>()).Select(SqlBind.From).ToList(),
                ct).ConfigureAwait(false);
        }

        public static TableMetadata VirtualTable(MappingModel sqlMapping, IList<QueryColumn> described, SchemaMetadata sourceMeta)
        {
            var info = SqlSourceAnalyzer.Analyze(sqlMapping, sourceMeta, described);
            return info != null ? info.Table : null;
        }

        private static SqlValidationResult ParseFailure(SqlParseResult parse, MappingModel mapping)
        {
            var parsed = SelectParser.Parse(mapping.Sql);
            var items = new List<CheckItem>
            {
                new CheckItem
                {
                    Check = "SQL 구문",
                    Level = CheckLevels.Error,
                    Detail = parse.ErrorCode + ": " + parse.Message +
                             (parse.Line != null ? " (" + parse.Line.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "번 줄)" : "")
                }
            };
            return new SqlValidationResult
            {
                Level = CheckLevels.Error,
                Items = items,
                Statement = parsed,
                Columns = new List<ResultColumn>(),
                Mapped = 0,
                Total = 0,
                ErrorLine = parse.Line
            };
        }
    }
}
