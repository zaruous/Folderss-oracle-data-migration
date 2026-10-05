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
