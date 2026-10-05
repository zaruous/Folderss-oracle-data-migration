using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MigrationStudio.Core.Types;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Validation
{
    /// <summary>실행 후 검증 P01~P06 질의(UI-MIG-005 SQL-5~8).</summary>
    public static class PostValidationSql
    {
        private const int BucketCount = 1024;
        private static readonly Regex IdRx = new Regex(@"^[A-Z0-9_$#]+$", RegexOptions.CultureInvariant);
        private static readonly Regex TrailingWsRx = new Regex(@"[;\s]+$", RegexOptions.CultureInvariant);

        public static string SourceScopeCount(MappingModel mapping, string sourceSchema)
        {
            if (mapping != null && mapping.IsSql)
            {
                return SqlProbe.SqlSourceCount(mapping);
            }

            var schema = RequireIdentifier(sourceSchema, "스키마");
            var table = RequireIdentifier(mapping != null ? mapping.Source : "", "테이블");
            var where = BuildScopeWhere(mapping, false, "");
            return "SELECT COUNT(*) AS CNT FROM " + schema + "." + table + where;
        }

        public static string TargetRowCount(string schema, string table, string extraWhere)
        {
            var s = RequireIdentifier(schema, "스키마");
            var t = RequireIdentifier(table, "테이블");
            var w = string.IsNullOrWhiteSpace(extraWhere) ? "" : " WHERE " + extraWhere.Trim();
            return "SELECT COUNT(*) AS CNT FROM " + s + "." + t + w;
        }

        public static string ErrorTableCount(string schema, string errorTable, string runId)
        {
            var s = RequireIdentifier(schema, "스키마");
            var e = RequireIdentifier(errorTable, "오류 테이블");
            var tag = (runId ?? "").Replace("'", "''");
            return "SELECT COUNT(*) AS CNT FROM " + s + "." + e + " WHERE ORA_ERR_TAG$ = '" + tag + "'";
        }

        public static string RejectSummary(string schema, string errorTable, string runId)
        {
            var s = RequireIdentifier(schema, "스키마");
            var e = RequireIdentifier(errorTable, "오류 테이블");
            var tag = (runId ?? "").Replace("'", "''");
            return "SELECT ORA_ERR_NUMBER$ AS ERR_NUM, MIN(ORA_ERR_MESG$) AS ERR_MSG, COUNT(*) AS ROWS_CNT"
                + " FROM " + s + "." + e
                + " WHERE ORA_ERR_TAG$ = '" + tag + "'"
                + " GROUP BY ORA_ERR_NUMBER$ ORDER BY 3 DESC";
        }

        public static string RejectKeyColumn(string schema, string errorTable, string runId, string keyColumn)
        {
            var s = RequireIdentifier(schema, "스키마");
            var e = RequireIdentifier(errorTable, "오류 테이블");
            var k = RequireIdentifier(keyColumn, "키 열");
            var tag = (runId ?? "").Replace("'", "''");
            return "SELECT " + k + " AS KEY_VAL FROM " + s + "." + e + " WHERE ORA_ERR_TAG$ = '" + tag + "'";
        }

        public static string KeyHashBuckets(string fromClause, string keyHashExpr, string extraWhere)
        {
            var where = string.IsNullOrWhiteSpace(extraWhere) ? "" : " WHERE " + extraWhere.Trim();
            return "SELECT MOD(ORA_HASH(" + keyHashExpr + "), " + BucketCount.ToString(CultureInfo.InvariantCulture) + ") AS BUCKET,"
                + " COUNT(*) AS CNT, SUM(ORA_HASH(" + keyHashExpr + ")) AS HSUM"
                + " FROM " + fromClause + where
                + " GROUP BY MOD(ORA_HASH(" + keyHashExpr + "), " + BucketCount.ToString(CultureInfo.InvariantCulture) + ")";
        }

        public static string KeysInBucket(string fromClause, string keyHashExpr, IList<string> keySelectExprs, int bucket, string extraWhere, int maxRows)
        {
            var cols = string.Join(", ", keySelectExprs);
            var whereParts = new List<string>
            {
                "MOD(ORA_HASH(" + keyHashExpr + "), " + BucketCount.ToString(CultureInfo.InvariantCulture) + ") = "
                + bucket.ToString(CultureInfo.InvariantCulture)
            };
            if (!string.IsNullOrWhiteSpace(extraWhere))
            {
                whereParts.Add("(" + extraWhere.Trim() + ")");
            }

            return "SELECT " + cols + " FROM " + fromClause + " WHERE " + string.Join(" AND ", whereParts)
                + " FETCH FIRST " + Math.Max(1, maxRows).ToString(CultureInfo.InvariantCulture) + " ROWS ONLY";
        }

        public static string TargetDuplicateKeyCount(string schema, string table, IList<string> keyColumns, string extraWhere)
        {
            var keys = (keyColumns ?? Array.Empty<string>()).Where(k => !string.IsNullOrEmpty(k)).ToList();
            if (keys.Count == 0)
            {
                return "SELECT 0 AS CNT FROM DUAL";
            }

            var s = RequireIdentifier(schema, "스키마");
            var t = RequireIdentifier(table, "테이블");
            var group = string.Join(", ", keys.Select(k => RequireIdentifier(k, "키")));
            var where = string.IsNullOrWhiteSpace(extraWhere) ? "" : " WHERE " + extraWhere.Trim();
            return "SELECT COUNT(*) AS CNT FROM (SELECT 1 FROM " + s + "." + t + where
                + " GROUP BY " + group + " HAVING COUNT(*) > 1)";
        }

        public static string SampleSourceRows(PlanItem item, string sourceSchema, int rows)
        {
            var mapping = item.Mapping;
            var target = item.TargetMetadata;
            var cols = item.WriteColumns ?? SqlGenerator.WriteColumns(mapping, target);
            var lines = cols.Select(c => "    " + Indent(c.Expr, 4) + " AS " + c.Name).ToList();
            var where = BuildScopeWhere(mapping, mapping != null && mapping.IsSql, "S.");
            var n = Math.Max(1, rows).ToString(CultureInfo.InvariantCulture);
            if (mapping != null && mapping.IsSql)
            {
                var from = BuildTransformedFrom(mapping, sourceSchema, item.SourceMetadata, target);
                return "SELECT * FROM (\n    SELECT\n" + string.Join(",\n", lines) + "\n    FROM " + from + where
                    + "\n    ORDER BY DBMS_RANDOM.VALUE\n) WHERE ROWNUM <= " + n;
            }

            var scope = item.ScopeTotal > 0 ? item.ScopeTotal : (item.SourceMetadata != null && item.SourceMetadata.Rows != null ? item.SourceMetadata.Rows.Value : 0);
            var sampleClause = scope >= 10000 ? " SAMPLE (0.05)" : "";
            var baseFrom = RequireIdentifier(sourceSchema, "스키마") + "." + RequireIdentifier(mapping.Source, "테이블") + sampleClause;
            return "SELECT\n" + string.Join(",\n", lines) + "\nFROM " + baseFrom + where
                + "\nORDER BY DBMS_RANDOM.VALUE\nFETCH FIRST " + n + " ROWS ONLY";
        }

        public static string TargetRowsByKeys(string schema, string table, IList<string> columns, IList<string> keyColumns, int bindCount)
        {
            var s = RequireIdentifier(schema, "스키마");
            var t = RequireIdentifier(table, "테이블");
            var colList = string.Join(", ", (columns ?? Array.Empty<string>()).Select(c => RequireIdentifier(c, "열")));
            var keys = (keyColumns ?? Array.Empty<string>()).Select(k => RequireIdentifier(k, "키")).ToList();
            if (keys.Count == 0)
            {
                return "SELECT " + colList + " FROM " + s + "." + t + " WHERE 1 = 0";
            }

            var inList = string.Join(", ", Enumerable.Range(1, Math.Max(1, bindCount)).Select(i => ":K" + i.ToString(CultureInfo.InvariantCulture)));
            return "SELECT " + colList + " FROM " + s + "." + t + " WHERE " + keys[0] + " IN (" + inList + ")";
        }

        public static string DataHashSum(string fromClause, IList<WriteColumn> columns, string keyColumnForReject, IList<string> rejectKeys)
        {
            var concat = HashConcatExpression(columns);
            var where = BuildRejectNotIn(keyColumnForReject, rejectKeys);
            return "SELECT SUM(ORA_HASH(" + concat + ")) AS HSUM FROM " + fromClause + where;
        }

        public static string NullCountColumn(string fromClause, string columnExpr, string extraWhere)
        {
            var where = string.IsNullOrWhiteSpace(extraWhere) ? "" : " WHERE " + extraWhere.Trim();
            return "SELECT COUNT(*) - COUNT(" + columnExpr + ") AS NULL_CNT FROM " + fromClause + where;
        }

        public static string SourceKeysPaged(string fromClause, IList<string> keySelectExprs, string orderColumn, int maxRows)
        {
            var cols = string.Join(", ", keySelectExprs);
            var order = RequireIdentifier(orderColumn, "정렬 열");
            return "SELECT " + cols + " FROM " + fromClause + " ORDER BY " + order
                + " FETCH FIRST " + Math.Max(1, maxRows).ToString(CultureInfo.InvariantCulture) + " ROWS ONLY";
        }

        public static string TargetKeyExistsCount(string schema, string table, string keyColumn, int bindCount)
        {
            var s = RequireIdentifier(schema, "스키마");
            var t = RequireIdentifier(table, "테이블");
            var k = RequireIdentifier(keyColumn, "키");
            var inList = string.Join(", ", Enumerable.Range(1, Math.Max(1, bindCount)).Select(i => ":K" + i.ToString(CultureInfo.InvariantCulture)));
            return "SELECT COUNT(*) AS CNT FROM " + s + "." + t + " WHERE " + k + " IN (" + inList + ")";
        }

        public static string HashConcatExpression(IList<WriteColumn> columns)
        {
            var parts = new List<string>();
            foreach (var col in columns ?? Array.Empty<WriteColumn>())
            {
                parts.Add(HashPart(col.Name, col.Expr, col.Column));
            }

            return parts.Count == 0 ? "NULL" : string.Join(" || '|' || ", parts);
        }

        public static string KeyHashExpression(IList<string> keyExprs)
        {
            var list = (keyExprs ?? Array.Empty<string>()).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
            if (list.Count == 0)
            {
                return "NULL";
            }

            if (list.Count == 1)
            {
                return list[0];
            }

            return string.Join(" || '|' || ", list);
        }

        public static string BuildTransformedFrom(MappingModel mapping, string sourceSchema, TableMetadata sourceTable, TableMetadata targetTable)
        {
            var cols = SqlGenerator.WriteColumns(mapping, targetTable);
            var lines = cols.Select(c => "    " + Indent(c.Expr, 4) + " AS " + c.Name).ToList();
            string from;
            if (mapping != null && mapping.IsSql)
            {
                var inner = TrailingWsRx.Replace(SqlText.StripComments(mapping.Sql ?? ""), "").Trim();
                from = "(\n    " + Indent(inner, 4) + "\n) S";
            }
            else
            {
                var schema = RequireIdentifier(sourceSchema, "스키마");
                var table = RequireIdentifier(mapping != null ? mapping.Source : "", "테이블");
                from = schema + "." + table;
            }

            return "(\n    SELECT\n" + string.Join(",\n", lines) + "\n    FROM " + from + "\n) V";
        }

        public static string TargetScopeBetween(string keyColumn, string minLiteral, string maxLiteral)
        {
            var k = RequireIdentifier(keyColumn, "키");
            return k + " BETWEEN " + minLiteral + " AND " + maxLiteral;
        }

        private static string HashPart(string alias, string expr, ColumnMetadata targetCol)
        {
            var e = string.IsNullOrWhiteSpace(expr) ? RequireIdentifier(alias, "열") : expr.Trim();
            var ot = OracleType.Parse(targetCol != null ? targetCol.Type : null);
            if (ot != null && ot.Base == "TIMESTAMP")
            {
                return "TO_CHAR(" + e + ", 'YYYYMMDDHH24MISSFF6')";
            }

            if (ot != null && ot.Base == "DATE")
            {
                return "TO_CHAR(" + e + ", 'YYYYMMDDHH24MISS')";
            }

            if (ot != null && ot.Base == "NUMBER")
            {
                return "TO_CHAR(" + e + ")";
            }

            return "NVL(TO_CHAR(" + e + "), '')";
        }

        private static string BuildScopeWhere(MappingModel mapping, bool isSql, string prefix)
        {
            if (mapping == null || string.IsNullOrWhiteSpace(mapping.Where))
            {
                return "";
            }

            return " WHERE (" + mapping.Where.Trim() + ")";
        }

        private static string BuildRejectNotIn(string keyColumn, IList<string> rejectKeys)
        {
            if (rejectKeys == null || rejectKeys.Count == 0 || string.IsNullOrEmpty(keyColumn))
            {
                return "";
            }

            var k = RequireIdentifier(keyColumn, "키");
            var list = string.Join(", ", rejectKeys.Select(r => "'" + (r ?? "").Replace("'", "''") + "'"));
            return " WHERE TO_CHAR(" + k + ") NOT IN (" + list + ")";
        }

        private static string RequireIdentifier(string name, string label)
        {
            var upper = (name ?? "").Trim().ToUpperInvariant();
            if (!IdRx.IsMatch(upper))
            {
                throw new ArgumentException(label + " 이름에 쓸 수 없는 문자가 있습니다");
            }

            return upper;
        }

        private static string Indent(string s, int n)
        {
            var pad = new string(' ', n);
            return (s ?? "").Replace("\n", "\n" + pad);
        }
    }
}
