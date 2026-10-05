using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Validation
{
    /// <summary>검증 질의 문자열(UI-MIG-005). 식별자는 SqlProbe와 같은 규칙.</summary>
    public static class ValidationSql
    {
        private static readonly Regex IdRx = new Regex(@"^[A-Z0-9_$#]+$", RegexOptions.CultureInvariant);

        public static IList<string> ExistingTables(string owner, IEnumerable<string> names)
        {
            var list = (names ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => RequireIdentifier(n, "테이블"))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (list.Count == 0)
            {
                return Array.Empty<string>();
            }

            var chunks = new List<string>();
            for (var i = 0; i < list.Count; i += 1000)
            {
                var part = list.Skip(i).Take(1000).ToList();
                var inList = string.Join(", ", part.Select(n => "'" + n + "'"));
                var o = RequireIdentifier(owner, "스키마");
                chunks.Add(
                    "SELECT TABLE_NAME AS NAME FROM ALL_TABLES WHERE OWNER = '" + o + "' AND TABLE_NAME IN (" + inList + ")"
                    + " UNION ALL SELECT VIEW_NAME AS NAME FROM ALL_VIEWS WHERE OWNER = '" + o + "' AND VIEW_NAME IN (" + inList + ")");
            }

            return chunks;
        }

        public static string LengthProfile(
            MappingModel mapping,
            string sourceSchema,
            string expression,
            bool sample,
            bool useBytes,
            bool sqlSourceCap)
        {
            var expr = (expression ?? "").Trim();
            var from = BuildFrom(mapping, sourceSchema, sqlSourceCap);
            var sampleClause = sample ? " SAMPLE BLOCK (5)" : "";
            var lenFn = useBytes ? "LENGTHB" : "LENGTH";
            var cap = "";
            if (sqlSourceCap)
            {
                cap = "\nFETCH FIRST 1000000 ROWS ONLY";
            }

            return "SELECT MAX(" + lenFn + "(" + expr + ")) AS MAX_LEN\nFROM " + from + sampleClause + cap;
        }

        public static string NumberProfile(
            MappingModel mapping,
            string sourceSchema,
            string expression,
            bool sample,
            bool sqlSourceCap)
        {
            var expr = (expression ?? "").Trim();
            var from = BuildFrom(mapping, sourceSchema, sqlSourceCap);
            var sampleClause = sample ? " SAMPLE BLOCK (5)" : "";
            var cap = sqlSourceCap ? "\nFETCH FIRST 1000000 ROWS ONLY" : "";
            return "SELECT MAX(ABS(" + expr + ")) AS MAX_ABS,"
                + " MAX(LENGTH(TO_CHAR(TRUNC(ABS(" + expr + "))))) AS INT_DIGITS\nFROM "
                + from + sampleClause + cap;
        }

        public static string NullCount(
            MappingModel mapping,
            string sourceSchema,
            string expression,
            bool isChar,
            bool sample,
            bool sqlSourceCap)
        {
            var expr = (expression ?? "").Trim();
            var from = BuildFrom(mapping, sourceSchema, sqlSourceCap);
            var sampleClause = sample ? " SAMPLE BLOCK (5)" : "";
            var cap = sqlSourceCap ? "\nFETCH FIRST 1000000 ROWS ONLY" : "";
            var cond = isChar ? "TRIM(" + expr + ") IS NULL" : expr + " IS NULL";
            return "SELECT COUNT(*) AS CNT\nFROM " + from + sampleClause
                + "\nWHERE " + cond + cap;
        }

        public static string DuplicateKeys(
            MappingModel mapping,
            string sourceSchema,
            IList<string> keyColumns,
            bool sample,
            bool sqlSourceCap)
        {
            var keys = (keyColumns ?? Array.Empty<string>()).Where(k => !string.IsNullOrEmpty(k)).ToList();
            if (keys.Count == 0)
            {
                return "SELECT 1 AS CNT FROM DUAL WHERE 1 = 0";
            }

            var from = BuildFrom(mapping, sourceSchema, sqlSourceCap);
            var sampleClause = sample ? " SAMPLE BLOCK (5)" : "";
            var cap = sqlSourceCap ? "\nFETCH FIRST 1000000 ROWS ONLY" : "";
            var group = string.Join(", ", keys);
            return "SELECT " + group + ", COUNT(*) AS CNT\nFROM " + from + sampleClause
                + "\nGROUP BY " + group + "\nHAVING COUNT(*) > 1\nFETCH FIRST 10 ROWS ONLY" + cap;
        }

        public static string TargetRowCount(string schema, string table)
        {
            var s = RequireIdentifier(schema, "스키마");
            var t = RequireIdentifier(table, "테이블");
            return "SELECT COUNT(*) AS CNT FROM " + s + "." + t;
        }

        public static string OrphanRows(
            string sourceSchema,
            string childTable,
            IList<string> childFkCols,
            string parentTable,
            IList<string> parentPkCols)
        {
            var schema = RequireIdentifier(sourceSchema, "스키마");
            var child = RequireIdentifier(childTable, "자식 테이블");
            var parent = RequireIdentifier(parentTable, "부모 테이블");
            var fk = string.Join(", ", childFkCols.Select(c => RequireIdentifier(c, "FK 열")));
            var pk = string.Join(", ", parentPkCols.Select(c => "C." + RequireIdentifier(c, "PK 열")));
            var join = string.Join(" AND ", parentPkCols.Select(c =>
                "C." + RequireIdentifier(c, "PK") + " = O." + RequireIdentifier(c, "FK")));
            return "SELECT COUNT(*) AS ORPHAN_ROWS\nFROM " + schema + "." + child + " O\nWHERE NOT EXISTS (\n"
                + "    SELECT 1 FROM " + schema + "." + parent + " C WHERE " + join + "\n)";
        }

        public static string TablespaceFree()
        {
            return "SELECT U.DEFAULT_TABLESPACE,"
                + " (SELECT ROUND(SUM(F.BYTES) / 1024 / 1024 / 1024, 1)"
                + "    FROM USER_FREE_SPACE F"
                + "   WHERE F.TABLESPACE_NAME = U.DEFAULT_TABLESPACE) AS FREE_GB,"
                + " (SELECT DECODE(Q.MAX_BYTES, -1, NULL, ROUND((Q.MAX_BYTES - Q.BYTES) / 1024 / 1024 / 1024, 1))"
                + "    FROM USER_TS_QUOTAS Q"
                + "   WHERE Q.TABLESPACE_NAME = U.DEFAULT_TABLESPACE) AS QUOTA_LEFT_GB"
                + " FROM USER_USERS U";
        }

        public static string ErrorTableExists(string owner, string name)
        {
            var o = RequireIdentifier(owner, "스키마");
            var n = RequireIdentifier(name, "오류 테이블");
            return "SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = '" + o + "' AND TABLE_NAME = '" + n + "'";
        }

        public static string PendingCheckpoints(string jobName)
        {
            var j = (jobName ?? "").Replace("'", "''");
            return "SELECT TASK_KEY, CP_COLUMN, CP_VALUE, ROWS_DONE, ROWS_TOTAL, RUN_ID, UPDATED_AT"
                + " FROM MIG_CHECKPOINT"
                + " WHERE JOB_NAME = '" + j + "' AND STATUS <> 'done'";
        }

        public static string ProfileBatch(
            MappingModel mapping,
            string sourceSchema,
            IList<ProfileColumn> columns,
            bool sample,
            bool sqlSourceCap)
        {
            var from = BuildFrom(mapping, sourceSchema, sqlSourceCap);
            var sampleClause = sample ? " SAMPLE BLOCK (5)" : "";
            var cap = sqlSourceCap ? "\nFETCH FIRST 1000000 ROWS ONLY" : "";
            var lines = new List<string> { "SELECT COUNT(*) AS SAMPLE_ROWS" };
            foreach (var col in columns)
            {
                var expr = col.Expression;
                if (col.IsChar)
                {
                    lines.Add("SUM(CASE WHEN " + expr + " IS NULL THEN 1 ELSE 0 END) AS " + col.Prefix + "_NULL_ROWS");
                    lines.Add("MAX(LENGTH(" + expr + ")) AS " + col.Prefix + "_MAX_CHARS");
                    lines.Add("MAX(LENGTHB(" + expr + ")) AS " + col.Prefix + "_MAX_BYTES");
                    lines.Add("SUM(CASE WHEN TRIM(" + expr + ") IS NULL AND " + expr + " IS NOT NULL THEN 1 ELSE 0 END) AS " + col.Prefix + "_BLANK_ROWS");
                }
                else if (col.IsNumber)
                {
                    lines.Add("MAX(ABS(" + expr + ")) AS " + col.Prefix + "_MAX_ABS");
                }
            }

            return string.Join(",\n       ", lines) + "\nFROM " + from + sampleClause + cap;
        }

        private static string BuildFrom(MappingModel mapping, string sourceSchema, bool sqlSourceCap)
        {
            if (mapping != null && mapping.IsSql)
            {
                var inner = SqlText.StripComments(mapping.Sql ?? "").Trim();
                if (sqlSourceCap)
                {
                    return "(\n    SELECT * FROM (\n" + Indent(inner, 8) + "\n    ) S\n    FETCH FIRST 1000000 ROWS ONLY\n) S";
                }

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

        public sealed class ProfileColumn
        {
            public string Prefix { get; set; }
            public string Expression { get; set; }
            public bool IsChar { get; set; }
            public bool IsNumber { get; set; }
        }
    }
}
