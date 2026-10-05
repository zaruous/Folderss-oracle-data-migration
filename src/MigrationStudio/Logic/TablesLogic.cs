using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Text;

namespace MigrationStudio.Logic
{
    public sealed class TableRowModel
    {
        public string Level { get; set; }
        public int Mapped { get; set; }
        public int Total { get; set; }
        public int Errors { get; set; }
        public int Warns { get; set; }
        public string RowCountDisplay { get; set; }
        public string RowCountTooltip { get; set; }
        public string SourceSubline { get; set; }
        public bool SourceBad { get; set; }
        public bool ShowDestructiveWarn { get; set; }
        public string MergeKeyHint { get; set; }
    }

    public sealed class SchemaBrowseLine
    {
        public string Kind { get; set; }
        public string Name { get; set; }
        public string RowShort { get; set; }
        public string LinkText { get; set; }
        public string LinkMappingId { get; set; }
        public bool ShowAddMapping { get; set; }
        public string AddSourceTable { get; set; }
    }

    public static class TablesLogic
    {
        public static TableRowModel RowModel(
            Mapping mapping,
            TableMetadata source,
            TableMetadata target,
            ConnectionProfile targetProfile,
            MappingStatus status)
        {
            var model = new TableRowModel();
            if (mapping == null)
            {
                model.Level = CheckLevels.Error;
                return model;
            }

            var st = status ?? new MappingStatus();
            var srcOk = source != null && (source.Columns == null || source.Columns.Count > 0);
            var sqlErr = mapping.IsSql && source != null && !string.IsNullOrEmpty(source.Comment) && !srcOk;
            if (source == null || sqlErr)
            {
                model.Level = CheckLevels.Error;
            }
            else if (st.Level == CheckLevels.Pass || st.Level == CheckLevels.Info)
            {
                model.Level = CheckLevels.Pass;
            }
            else
            {
                model.Level = st.Level;
            }

            model.Mapped = st.Mapped;
            model.Total = st.Total;
            model.Errors = st.Errors;
            model.Warns = st.Warns;

            if (source != null && source.Rows != null)
            {
                model.RowCountDisplay = (mapping.IsSql ? "~" : "") + Format.Number(source.Rows.Value);
                model.RowCountTooltip = mapping.IsSql
                    ? "추정(주 테이블 통계 − 체크포인트 조건)"
                    : "통계(NUM_ROWS)";
            }
            else
            {
                model.RowCountDisplay = "—";
            }

            if (mapping.IsSql)
            {
                model.SourceBad = source == null || !srcOk;
                if (model.SourceBad)
                {
                    model.SourceSubline = source != null && !string.IsNullOrEmpty(source.Comment)
                        ? source.Comment
                        : "SQL을 검증하세요";
                }
                else
                {
                    model.SourceSubline = (source.Comment ?? "") + " · 결과 열 " +
                                          (source.Columns != null ? source.Columns.Count.ToString(CultureInfo.InvariantCulture) : "0");
                }
            }
            else if (source != null)
            {
                model.SourceSubline = source.Comment ?? "";
            }

            var mode = WriteModes.Of(mapping.Mode);
            model.ShowDestructiveWarn = mode.Destructive && targetProfile != null &&
                                        string.Equals(targetProfile.Color, "red", StringComparison.Ordinal);

            if (mode.NeedsKey && mapping.MergeKey != null && mapping.MergeKey.Count > 0 && mapping.Columns != null)
            {
                var hints = new List<string>();
                foreach (var k in mapping.MergeKey)
                {
                    var cm = mapping.FindColumn(k);
                    hints.Add(cm != null && !string.IsNullOrEmpty(cm.Source) ? cm.Source : "?");
                }

                model.MergeKeyHint = "← " + string.Join(", ", hints);
            }

            return model;
        }

        public static long UsedSourceRows(IEnumerable<Mapping> mappings, Func<Mapping, TableMetadata> sourceOf)
        {
            long sum = 0;
            if (mappings == null)
            {
                return 0;
            }

            foreach (var m in mappings)
            {
                if (!m.Use)
                {
                    continue;
                }

                var src = sourceOf != null ? sourceOf(m) : null;
                if (src != null && src.Rows != null)
                {
                    sum += src.Rows.Value;
                }
            }

            return sum;
        }

        public static bool MatchesFilter(Mapping mapping, string filter)
        {
            if (mapping == null || string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            var f = filter.Trim();
            if (string.Equals(mapping.Source, f, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mapping.Target, f, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return (mapping.Label ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static List<SchemaBrowseLine> SourceBrowseLines(
            SchemaMetadata meta,
            IList<Mapping> mappings,
            bool isSource)
        {
            var lines = new List<SchemaBrowseLine>();
            if (meta == null || meta.Tables == null)
            {
                return lines;
            }

            foreach (var t in meta.Tables.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (!string.Equals(t.Kind, "TABLE", StringComparison.Ordinal) &&
                    !string.Equals(t.Kind, "VIEW", StringComparison.Ordinal))
                {
                    continue;
                }

                var line = new SchemaBrowseLine
                {
                    Kind = string.Equals(t.Kind, "VIEW", StringComparison.Ordinal) ? "VIEW" : "TBL",
                    Name = t.Name,
                    RowShort = t.Rows != null ? Format.Short(t.Rows.Value) : ""
                };

                if (isSource)
                {
                    var map = mappings != null
                        ? mappings.FirstOrDefault(m => !m.IsSql && string.Equals(m.Source, t.Name, StringComparison.OrdinalIgnoreCase))
                        : null;
                    if (map != null)
                    {
                        line.LinkText = "→ " + (map.IsSql ? "SQL " + map.Source : map.Target);
                        line.LinkMappingId = map.Id;
                    }
                    else if (string.Equals(t.Kind, "TABLE", StringComparison.Ordinal))
                    {
                        line.ShowAddMapping = true;
                        line.AddSourceTable = t.Name;
                    }
                }

                lines.Add(line);
            }

            return lines;
        }

        public static int UnmappedSourceTableCount(SchemaMetadata meta, IList<Mapping> mappings)
        {
            if (meta == null || meta.Tables == null)
            {
                return 0;
            }

            var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (mappings != null)
            {
                foreach (var m in mappings)
                {
                    if (!m.IsSql && !string.IsNullOrEmpty(m.Source))
                    {
                        mapped.Add(m.Source);
                    }
                }
            }

            return meta.Tables.Count(t =>
                string.Equals(t.Kind, "TABLE", StringComparison.Ordinal) && !mapped.Contains(t.Name));
        }

        public static string DefaultTemplateName(string jobName)
        {
            var n = (jobName ?? "").ToLowerInvariant();
            if (n.EndsWith("_migration", StringComparison.Ordinal))
            {
                n = n.Substring(0, n.Length - "_migration".Length);
            }

            return (string.IsNullOrEmpty(n) ? "mapping" : n) + "_mapping";
        }
    }
}
