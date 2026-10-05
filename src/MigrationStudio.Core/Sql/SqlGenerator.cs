using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Expressions;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Text;
using MigrationStudio.Core.Types;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Sql
{
    public static class SqlGenerator
    {
        private static readonly Regex QuotedLiteralRx = new Regex(@"^'.*'$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex SysKeywordRx = new Regex(@"^(SYSDATE|SYSTIMESTAMP|NULL)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NumberRx = new Regex(@"^-?\d+(\.\d+)?$", RegexOptions.CultureInvariant);
        private static readonly Regex TrailingWsRx = new Regex(@"[;\s]+$", RegexOptions.CultureInvariant);

        public static string Literal(string value, string type)
        {
            if (value == null || value == "")
            {
                return "NULL";
            }

            var s = value.Trim();
            if (QuotedLiteralRx.IsMatch(s) || SysKeywordRx.IsMatch(s))
            {
                return s;
            }

            var t = OracleType.Parse(type);
            if (t != null && t.Base == "NUMBER" && NumberRx.IsMatch(s))
            {
                return s;
            }

            return "'" + s.Replace("'", "''") + "'";
        }

        public static string ValueExpression(ColumnMapping mapping, ColumnMetadata target)
        {
            var e = MappingService.ValueSource(mapping);
            var isTs = target != null && OracleType.Parse(target.Type)?.Base == "TIMESTAMP";
            var now = isTs ? "SYSTIMESTAMP" : "SYSDATE";
            if (string.IsNullOrEmpty(e))
            {
                if (mapping != null && mapping.NullRule == NullRules.Default && !string.IsNullOrEmpty(mapping.DefaultValue))
                {
                    return Literal(mapping.DefaultValue, target != null ? target.Type : null);
                }

                if (mapping != null && mapping.NullRule == NullRules.Custom && !string.IsNullOrEmpty(mapping.DefaultValue))
                {
                    return mapping.DefaultValue;
                }

                if (mapping != null && mapping.NullRule == NullRules.Sysdate)
                {
                    return now;
                }

                return null;
            }

            switch (mapping != null ? mapping.NullRule : NullRules.Allow)
            {
                case NullRules.Default:
                    return !string.IsNullOrEmpty(mapping.DefaultValue)
                        ? "NVL(" + e + ", " + Literal(mapping.DefaultValue, target != null ? target.Type : null) + ")"
                        : e;
                case NullRules.Sysdate:
                    return "NVL(" + e + ", " + now + ")";
                case NullRules.Empty:
                    return "NVL(" + e + ", '')";
                case NullRules.Custom:
                    return !string.IsNullOrEmpty(mapping.DefaultValue) ? "NVL(" + e + ", " + mapping.DefaultValue + ")" : e;
                default:
                    return e;
            }
        }

        public static List<WriteColumn> WriteColumns(MappingModel mapping, TableMetadata target)
        {
            var list = new List<WriteColumn>();
            if (target == null || target.Columns == null)
            {
                return list;
            }

            foreach (var t in target.Columns)
            {
                var cm = mapping != null ? mapping.FindColumn(t.Name) : null;
                if (cm == null)
                {
                    continue;
                }

                var expr = ValueExpression(cm, t);
                if (expr == null)
                {
                    continue;
                }

                list.Add(new WriteColumn { Name = t.Name, Expr = expr, Column = t });
            }

            return list;
        }

        public static string BuildSourceSelect(
            MappingModel mapping,
            string sourceSchema,
            TableMetadata sourceTable,
            TableMetadata targetTable,
            SourceSelectOptions options)
        {
            options = options ?? new SourceSelectOptions();
            var isSql = mapping != null && mapping.IsSql;
            var q = isSql ? "S." : "";
            var cols = WriteColumns(mapping, targetTable);
            var lines = cols.Select(c => "    " + Indent(c.Expr, 4) + " AS " + c.Name).ToList();
            var cp = mapping != null ? mapping.CheckpointColumn : null;
            BindParameter cpBind = null;
            if (isSql && mapping != null)
            {
                cpBind = SqlSourceAnalyzer.CheckpointBind(mapping, SelectParser.Parse(mapping.Sql));
            }

            var where = BuildMappingWhereClauses(mapping, isSql, cpBind, q, options.Workers);

            string from;
            if (isSql)
            {
                var inner = TrailingWsRx.Replace(SqlText.StripComments(mapping.Sql ?? ""), "");
                from = "(\n    " + Indent(inner, 4) + "\n) S";
            }
            else
            {
                from = sourceSchema + "." + (sourceTable != null ? sourceTable.Name : "");
            }

            var fetch = mapping != null && mapping.FetchSize != null
                ? mapping.FetchSize.Value
                : options.FetchSize;
            var sql = "-- 원본 읽기: Fetch " + Format.Number(fetch) + "행씩 스트리밍\n";
            if (isSql)
            {
                sql += "-- 원본 SQL을 인라인 뷰 S로 감싸 변환식·NULL 처리·체크포인트를 붙임\n";
            }

            if (!string.IsNullOrEmpty(cp))
            {
                sql += cpBind != null
                    ? "-- :" + cpBind.Name + " = 체크포인트(SQL 안 조건, 없으면 입력한 값)\n"
                    : "-- :LAST_ID = 체크포인트(없으면 처음부터)\n";
            }

            if (options.Workers > 1 && !string.IsNullOrEmpty(cp))
            {
                sql += "-- 작업자 " + options.Workers + "개: " + cp + " 범위를 나눠 :RANGE_TO까지씩 읽음\n";
            }

            sql += "SELECT\n" + string.Join(",\n", lines) + "\nFROM " + from;
            if (where.Count > 0)
            {
                sql += "\nWHERE " + string.Join("\n  AND ", where);
            }

            if (!string.IsNullOrEmpty(cp))
            {
                sql += "\nORDER BY " + q + cp;
            }

            return sql;
        }

        public static string BuildWriteSql(
            string targetSchema,
            string targetTable,
            IList<string> columns,
            string mode,
            IList<string> keys,
            string errorTable)
        {
            var t = targetSchema + "." + targetTable;
            var log = !string.IsNullOrEmpty(errorTable)
                ? "\nLOG ERRORS INTO " + targetSchema + "." + errorTable + " ('RUN_ID') REJECT LIMIT UNLIMITED"
                : "";
            var colList = columns ?? Array.Empty<string>();
            var insertCols = string.Join(",\n", colList.Select(c => "    " + c));
            var insertVals = string.Join(",\n", colList.Select(c => "    :" + c));
            var insert = "INSERT INTO " + t + " (\n" + insertCols + "\n)\nVALUES (\n" + insertVals + "\n)" + log;
            var keyList = (keys ?? Array.Empty<string>()).Where(k => colList.Contains(k)).ToList();

            switch (mode)
            {
                case WriteModes.Merge:
                {
                    if (keyList.Count == 0)
                    {
                        return "-- 병합 키를 정하세요(MERGE는 키로 대상 행을 찾습니다)";
                    }

                    var others = colList.Where(c => !keyList.Contains(c)).ToList();
                    var merge = "MERGE INTO " + t + " T\nUSING (\n    SELECT\n" +
                                string.Join(",\n", colList.Select(c => "        :" + c + " AS " + c)) +
                                "\n    FROM DUAL\n) S\nON (\n" +
                                string.Join("\n    AND ", keyList.Select(k => "    T." + k + " = S." + k)) + "\n)\n";
                    if (others.Count > 0)
                    {
                        merge += "WHEN MATCHED THEN\n    UPDATE SET\n" +
                                 string.Join(",\n", others.Select(c => "        T." + c + " = S." + c)) + "\n";
                    }

                    merge += "WHEN NOT MATCHED THEN\n    INSERT (\n" +
                             string.Join(",\n", colList.Select(c => "        " + c)) + "\n    )\n    VALUES (\n" +
                             string.Join(",\n", colList.Select(c => "        S." + c)) + "\n    )" + log;
                    return merge;
                }
                case WriteModes.TruncateInsert:
                    return "-- 시작할 때 한 번(되돌릴 수 없음)\nTRUNCATE TABLE " + t + ";\n\n-- 배치마다\n" + insert;
                case WriteModes.DeleteInsert:
                    if (keyList.Count == 0)
                    {
                        return "-- 삭제 키를 정하세요";
                    }

                    return "-- 배치마다: 같은 키의 대상 행을 지우고 넣음\nDELETE FROM " + t + "\nWHERE " +
                           string.Join("\n  AND ", keyList.Select(k => k + " = :" + k)) + ";\n\n" + insert;
                default:
                    return insert;
            }
        }

        public static string ErrorTableFor(MigrationStrategy strategy, string targetTable)
        {
            if (strategy == null || strategy.ErrorPolicy != ErrorPolicies.Continue)
            {
                return null;
            }

            var baseName = (strategy.ErrorTable ?? "ERR$_").Trim();
            if (baseName.EndsWith("_", StringComparison.Ordinal))
            {
                var combined = baseName + targetTable;
                return combined.Length <= 128 ? combined : combined.Substring(0, 128);
            }

            return baseName;
        }

        private static string Indent(string s, int n)
        {
            var pad = new string(' ', n);
            return (s ?? "").Replace("\n", "\n" + pad);
        }

        internal static List<string> BuildMappingWhereClauses(
            MappingModel mapping,
            bool isSql,
            BindParameter cpBind,
            string columnPrefix,
            int workers)
        {
            var q = columnPrefix ?? "";
            var cp = mapping != null ? mapping.CheckpointColumn : null;
            var where = new List<string>();
            if (!string.IsNullOrEmpty(cp) && cpBind == null)
            {
                where.Add(q + cp + " > :LAST_ID");
            }

            if (mapping != null && !string.IsNullOrWhiteSpace(mapping.Where))
            {
                where.Add("(" + mapping.Where.Trim() + ")");
            }

            if (workers > 1 && !string.IsNullOrEmpty(cp))
            {
                where.Add(q + cp + " <= :RANGE_TO");
            }

            return where;
        }
    }
}
