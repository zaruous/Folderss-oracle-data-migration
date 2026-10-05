using System;
using System.Collections.Generic;
using System.Linq;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;

namespace MigrationStudio.Logic
{
    public sealed class LineNumberStyle
    {
        public int Line { get; set; }
        public bool IsError { get; set; }
    }

    public sealed class AliasRowModel
    {
        public string ResultColumn { get; set; }
        public string SelectExpr { get; set; }
        public string TypeHint { get; set; }
        public string TargetColumn { get; set; }
        public string Expr { get; set; }
        public string CheckLevel { get; set; }
        public string CheckMessage { get; set; }
    }

    public static class SqlEditorLogic
    {
        public static List<LineNumberStyle> LineStyles(string sql, SqlValidationResult validation)
        {
            var lines = new List<LineNumberStyle>();
            if (string.IsNullOrEmpty(sql))
            {
                return lines;
            }

            var count = sql.Split('\n').Length;
            var errLine = validation != null ? validation.ErrorLine : null;
            for (var i = 1; i <= count; i++)
            {
                lines.Add(new LineNumberStyle
                {
                    Line = i,
                    IsError = errLine != null && errLine.Value == i
                });
            }

            return lines;
        }

        public static string MissingTargetColumnsSummary(Mapping mapping, TableMetadata target)
        {
            if (mapping == null || target == null || target.Columns == null)
            {
                return "";
            }

            var missing = new List<string>();
            foreach (var t in target.Columns)
            {
                if (t.Nullable || !string.IsNullOrEmpty(t.DefaultValue))
                {
                    continue;
                }

                var cm = mapping.FindColumn(t.Name);
                var vs = cm != null ? MappingService.ValueSource(cm) : null;
                if (string.IsNullOrEmpty(vs))
                {
                    missing.Add(t.Name + (t.Nullable ? "" : " (NN)"));
                }
            }

            return missing.Count == 0 ? "" : string.Join(", ", missing);
        }

        public static List<AliasRowModel> AliasRows(Mapping mapping, SqlValidationResult validation, TableMetadata target)
        {
            var rows = new List<AliasRowModel>();
            if (validation == null || validation.Columns == null)
            {
                return rows;
            }

            var srcCols = validation.Columns.Select(c => new ColumnMetadata
            {
                Name = !string.IsNullOrEmpty(c.Alias) ? c.Alias : c.Name,
                Type = c.Type
            }).ToList();

            foreach (var rc in validation.Columns)
            {
                var alias = !string.IsNullOrEmpty(rc.Alias) ? rc.Alias : rc.Name;
                ColumnMapping cm = null;
                if (mapping != null && mapping.Columns != null)
                {
                    cm = mapping.Columns.Find(c => string.Equals(c.Source, alias, StringComparison.OrdinalIgnoreCase) ||
                                                     string.Equals(c.Target, alias, StringComparison.OrdinalIgnoreCase));
                }

                var tgtCol = target != null && cm != null ? target.Columns?.Find(c => c.Name == cm.Target) : null;
                var check = cm != null && tgtCol != null
                    ? MappingService.CheckColumn(cm, tgtCol, srcCols, mapping.Mode)
                    : null;
                rows.Add(new AliasRowModel
                {
                    ResultColumn = alias,
                    SelectExpr = rc.Expr ?? "",
                    TypeHint = rc.Type ?? "",
                    TargetColumn = cm != null ? cm.Target : "",
                    Expr = cm != null ? cm.Expr : "",
                    CheckLevel = check != null ? check.Level : CheckLevels.Info,
                    CheckMessage = check != null && check.Messages != null && check.Messages.Count > 0 ? check.Messages[0].Message : ""
                });
            }

            return rows;
        }

        public static string SqlChangedBanner(bool validated, string currentSql, string validatedSql)
        {
            if (!validated || string.IsNullOrEmpty(validatedSql))
            {
                return null;
            }

            return string.Equals(currentSql ?? "", validatedSql ?? "", StringComparison.Ordinal) ? null : "검증한 뒤 SQL이 바뀌었습니다";
        }
    }
}
