using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Expressions;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;

namespace MigrationStudio.Logic
{
    public static class ColumnsLogic
    {
        public static readonly string[] SnippetNames =
        {
            "TRIM", "NVL", "REGEXP_REPLACE", "CASE", "CAST", "TO_DATE", "DECODE", "SUBSTR", "UPPER"
        };

        public static string WrapSnippet(string name, string baseExpr)
        {
            var b = string.IsNullOrWhiteSpace(baseExpr) ? "COL" : baseExpr.Trim();
            switch (name)
            {
                case "TRIM": return "TRIM(" + b + ")";
                case "NVL": return "NVL(" + b + ", '')";
                case "REGEXP_REPLACE": return "REGEXP_REPLACE(" + b + ", '[^0-9]', '')";
                case "CASE":
                    return "CASE\n    WHEN " + b + " = 'A' THEN 'Y'\n    ELSE 'N'\nEND";
                case "CAST": return "CAST(" + b + " AS TIMESTAMP)";
                case "TO_DATE": return "TO_DATE(" + b + ", 'YYYYMMDD')";
                case "DECODE": return "DECODE(" + b + ", 'A', 'Y', 'N')";
                case "SUBSTR": return "SUBSTR(" + b + ", 1, 20)";
                case "UPPER": return "UPPER(" + b + ")";
                default: return b;
            }
        }

        public static void ApplySourceColumnChange(ColumnMapping cm, string prevSource, string newSource, ColumnMetadata srcCol, ColumnMetadata tgtCol)
        {
            if (cm == null)
            {
                return;
            }

            cm.Source = string.IsNullOrEmpty(newSource) ? null : newSource;
            if (string.IsNullOrEmpty(newSource))
            {
                cm.Expr = "";
                return;
            }

            if (!string.IsNullOrEmpty(prevSource) && !string.IsNullOrEmpty(cm.Expr))
            {
                var comp = ExpressionAnalyzer.Analyze(cm.Expr);
                if (comp != null && comp.Error == null && comp.Refs != null &&
                    comp.Refs.All(r => string.Equals(r, prevSource, StringComparison.OrdinalIgnoreCase)))
                {
                    cm.Expr = Regex.Replace(cm.Expr, "\\b" + Regex.Escape(prevSource) + "\\b", newSource,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    return;
                }
            }

            if (string.IsNullOrEmpty(prevSource) && srcCol != null && tgtCol != null)
            {
                cm.Expr = MappingService.SuggestExpression(srcCol, tgtCol);
            }
        }

        public static bool PassesFilter(ColumnResult row, string filter)
        {
            if (row == null)
            {
                return false;
            }

            switch (filter ?? "all")
            {
                case "unmapped":
                    return string.IsNullOrEmpty(MappingService.ValueSource(row.Mapping));
                case "issues":
                    return row.Check != null && (row.Check.Level == CheckLevels.Error || row.Check.Level == CheckLevels.Warn);
                case "errors":
                    return row.Check != null && row.Check.Level == CheckLevels.Error;
                case "warnings":
                    return row.Check != null && row.Check.Level == CheckLevels.Warn;
                default:
                    return true;
            }
        }

        /// <summary>삭제 표시 열 후보: 대상의 NULL 허용 열 중 이 매핑이 값을 쓰지 않는 열(쓰면 이관이 표시를 덮어씀).</summary>
        public static List<string> MarkColumnCandidates(Mapping m, TableMetadata target)
        {
            var list = new List<string>();
            if (m == null || target == null || target.Columns == null)
            {
                return list;
            }

            var written = new HashSet<string>(Core.Sql.SqlGenerator.WriteColumns(m, target).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var c in target.Columns)
            {
                if (c.Nullable && !written.Contains(c.Name))
                {
                    list.Add(c.Name);
                }
            }

            return list;
        }

        /// <summary>
        /// 삭제 처리 설정을 바꾼다. 실제로 바뀌면 승인을 지운다 — 승인은 "이 설정으로 이 행들을 표시해도 된다"는 확인이라 설정이 바뀌면 다시 받아야 한다.
        /// 바뀌었으면 true.
        /// </summary>
        public static bool ChangeDeleteSetting(Mapping m, string mode, string markColumn, string markValue)
        {
            if (m == null)
            {
                return false;
            }

            mode = string.IsNullOrEmpty(mode) ? DeleteModes.None : mode;
            if (string.Equals(m.DeleteMode ?? DeleteModes.None, mode, StringComparison.Ordinal)
                && string.Equals(m.MarkColumn ?? "", markColumn ?? "", StringComparison.Ordinal)
                && string.Equals((m.MarkValue ?? "").Trim(), (markValue ?? "").Trim(), StringComparison.Ordinal))
            {
                return false;
            }

            m.DeleteMode = mode;
            m.MarkColumn = string.IsNullOrEmpty(markColumn) ? null : markColumn;
            m.MarkValue = string.IsNullOrWhiteSpace(markValue) ? null : markValue.Trim();
            m.DeleteApprovedAt = null;
            return true;
        }

        public static List<string> CheckpointCandidates(TableMetadata source)
        {
            var list = new List<string> { "(없음 — 재개 불가)" };
            if (source == null || source.Columns == null)
            {
                return list;
            }

            foreach (var c in source.Columns)
            {
                var t = (c.Type ?? "").ToUpperInvariant();
                if (c.PrimaryKey || t.StartsWith("NUMBER", StringComparison.Ordinal) ||
                    t.StartsWith("DATE", StringComparison.Ordinal) || t.StartsWith("TIMESTAMP", StringComparison.Ordinal))
                {
                    list.Add(c.Name);
                }
            }

            return list;
        }

        public static string MappingPickerLabel(Mapping m, TableMetadata src, TableMetadata tgt)
        {
            if (m == null)
            {
                return "";
            }

            var st = MappingService.Status(m, src, tgt);
            var err = st.Errors > 0 ? " · 오류" : "";
            return m.Label + "   " + st.Mapped + "/" + st.Total + err;
        }
    }
}
