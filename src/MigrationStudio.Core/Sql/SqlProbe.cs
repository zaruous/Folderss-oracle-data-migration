using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Model;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Sql
{
    public static class SqlProbe
    {
        private static readonly Regex IdRx = new Regex(@"^[A-Z0-9_$#]+$", RegexOptions.CultureInvariant);
        private static readonly Regex TrailingWsRx = new Regex(@"[;\s]+$", RegexOptions.CultureInvariant);

        public static string ExpressionCheck(MappingModel mapping, string sourceSchema, string valueExpression)
        {
            var expr = (valueExpression ?? "").Trim();
            var from = BuildFromClause(mapping, sourceSchema);
            return "SELECT " + expr + " AS V\nFROM " + from + "\nWHERE 1 = 0";
        }

        public static string SamplePreview(
            MappingModel mapping,
            string sourceSchema,
            IList<string> sourceExpressions,
            string resultExpression,
            int rows)
        {
            var srcExprs = sourceExpressions ?? Array.Empty<string>();
            var lines = new List<string>();
            for (var i = 0; i < srcExprs.Count; i++)
            {
                lines.Add("    " + srcExprs[i] + " AS SRC_" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            lines.Add("    " + (resultExpression ?? "") + " AS RESULT");
            var from = BuildFromClause(mapping, sourceSchema);
            var parsed = mapping != null && mapping.IsSql ? SelectParser.Parse(mapping.Sql) : null;
            BindParameter cpBind = null;
            if (mapping != null && mapping.IsSql && parsed != null)
            {
                cpBind = SqlSourceAnalyzer.CheckpointBind(mapping, parsed);
            }

            var prefix = mapping != null && mapping.IsSql ? "S." : "";
            var whereParts = SqlGenerator.BuildMappingWhereClauses(mapping, mapping != null && mapping.IsSql, cpBind, prefix, 1);
            var sql = "SELECT\n" + string.Join(",\n", lines) + "\nFROM " + from;
            if (whereParts.Count > 0)
            {
                sql += "\nWHERE " + string.Join("\n  AND ", whereParts);
            }

            var cp = mapping != null ? mapping.CheckpointColumn : null;
            if (!string.IsNullOrEmpty(cp) && cpBind == null)
            {
                sql += "\nORDER BY " + prefix + cp;
            }

            sql += "\nFETCH FIRST " + Math.Max(1, rows).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ROWS ONLY";
            return sql;
        }

        public static string SqlSourcePreview(MappingModel sqlMapping, int rows)
        {
            var inner = TrailingWsRx.Replace(SqlText.StripComments(sqlMapping != null ? sqlMapping.Sql : ""), "").Trim();
            var sql = "SELECT * FROM (\n" + inner + "\n) S";
            var cp = sqlMapping != null ? sqlMapping.CheckpointColumn : null;
            if (!string.IsNullOrEmpty(cp))
            {
                sql += "\nORDER BY S." + RequireIdentifier(cp, "체크포인트 열");
            }

            sql += "\nFETCH FIRST " + Math.Max(1, rows).ToString(System.Globalization.CultureInfo.InvariantCulture) + " ROWS ONLY";
            return sql;
        }

        public static string SqlSourceCount(MappingModel sqlMapping)
        {
            var inner = TrailingWsRx.Replace(SqlText.StripComments(sqlMapping != null ? sqlMapping.Sql : ""), "").Trim();
            return "SELECT COUNT(*) FROM (\n" + inner + "\n)";
        }

        public static string TableCount(string schema, string table)
        {
            var s = RequireIdentifier(schema, "스키마");
            var t = RequireIdentifier(table, "테이블");
            return "SELECT /*+ PARALLEL(4) */ COUNT(*) FROM " + s + "." + t;
        }

        private static string BuildFromClause(MappingModel mapping, string sourceSchema)
        {
            if (mapping != null && mapping.IsSql)
            {
                var inner = TrailingWsRx.Replace(SqlText.StripComments(mapping.Sql ?? ""), "").Trim();
                return "(\n    " + Indent(inner, 4) + "\n) S";
            }

            var schema = RequireIdentifier(sourceSchema, "스키마");
            var table = RequireIdentifier(mapping != null ? mapping.Source : "", "테이블");
            return schema + "." + table;
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
