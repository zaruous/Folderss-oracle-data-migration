using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Metadata;

namespace MigrationStudio.Testing.Adapters
{
    /// <summary>검증 질의에 POC mock-metadata와 같은 통계를 돌려준다.</summary>
    public sealed class ValidationResponder
    {
        private static readonly Regex ProfileRx = new Regex(
            @"MAX\(LENGTH(?:B)?\((?<expr>[^)]+)\)\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly SchemaMetadata _source;
        private readonly SchemaMetadata _target;

        public ValidationResponder(SchemaMetadata source, SchemaMetadata target)
        {
            _source = source;
            _target = target;
        }

        public QueryResult Respond(string sql)
        {
            var upper = sql ?? "";
            if (upper.IndexOf("ALL_TABLES", StringComparison.OrdinalIgnoreCase) >= 0
                && upper.IndexOf("UNION ALL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return TableListResult(ParseInList(sql));
            }

            if (upper.IndexOf("USER_USERS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var ts = _target != null ? _target.Tablespace : null;
                return RowResult(
                    new[] { "DEFAULT_TABLESPACE", "FREE_GB", "QUOTA_LEFT_GB" },
                    new[] { ts != null ? ts.Name : "NEXT_DATA", (ts != null ? ts.FreeGb : 182.4).ToString(CultureInfo.InvariantCulture), "" });
            }

            if (upper.IndexOf("HAVING COUNT(*) > 1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return EmptyResult(new[] { "CNT" });
            }

            if (upper.IndexOf("ORPHAN_ROWS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RowResult(new[] { "ORPHAN_ROWS" }, new[] { "0" });
            }

            if (upper.IndexOf("SAMPLE_ROWS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ProfileResult(sql);
            }

            if (upper.IndexOf("MAX_LEN", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RowResult(new[] { "MAX_LEN" }, new[] { "13" });
            }

            if (upper.IndexOf("MAX_ABS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RowResult(new[] { "MAX_ABS", "INT_DIGITS" }, new[] { "4820000", "7" });
            }

            if (upper.IndexOf(" AS CNT", StringComparison.OrdinalIgnoreCase) >= 0 && upper.IndexOf("IS NULL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RowResult(new[] { "CNT" }, new[] { "0" });
            }

            if (upper.IndexOf("SELECT COUNT(*)", StringComparison.OrdinalIgnoreCase) >= 0
                && upper.IndexOf("GROUP BY", StringComparison.OrdinalIgnoreCase) < 0)
            {
                var table = FindTableName(sql);
                if (table != null && _target != null)
                {
                    var t = _target.FindTable(table);
                    if (t != null && t.Rows != null)
                    {
                        return RowResult(new[] { "CNT" }, new[] { t.Rows.Value.ToString(CultureInfo.InvariantCulture) });
                    }
                }

                return RowResult(new[] { "CNT" }, new[] { "0" });
            }

            return new QueryResult
            {
                Columns = new List<QueryColumn>(),
                Rows = new List<string[]>(),
                ElapsedMs = 1,
                HasMore = false
            };
        }

        private QueryResult ProfileResult(string sql)
        {
            var cols = new List<QueryColumn>
            {
                new QueryColumn { Name = "SAMPLE_ROWS" }
            };
            var values = new List<string> { "62016" };

            foreach (Match m in Regex.Matches(sql, @"(?<pfx>[A-Z0-9_]+)_NULL_ROWS"))
            {
                var pfx = m.Groups["pfx"].Value;
                cols.Add(new QueryColumn { Name = pfx + "_NULL_ROWS" });
                cols.Add(new QueryColumn { Name = pfx + "_MAX_CHARS" });
                cols.Add(new QueryColumn { Name = pfx + "_MAX_BYTES" });
                cols.Add(new QueryColumn { Name = pfx + "_BLANK_ROWS" });
                var nulls = pfx.IndexOf("MEMBER_NAME", StringComparison.OrdinalIgnoreCase) >= 0 ? "37" : "0";
                values.Add(nulls);
                values.Add(pfx.IndexOf("MOBILE", StringComparison.OrdinalIgnoreCase) >= 0 ? "13" : "20");
                values.Add("13");
                values.Add("0");
            }

            foreach (Match m in Regex.Matches(sql, @"(?<pfx>[A-Z0-9_]+)_MAX_ABS"))
            {
                cols.Add(new QueryColumn { Name = m.Groups["pfx"].Value + "_MAX_ABS" });
                values.Add("4820000");
            }

            return RowResult(cols.Select(c => c.Name).ToArray(), values.ToArray());
        }

        private static QueryResult TableListResult(IList<string> names)
        {
            var rows = new List<string[]>();
            foreach (var n in names)
            {
                rows.Add(new[] { n.ToUpperInvariant() });
            }

            return new QueryResult
            {
                Columns = new List<QueryColumn> { new QueryColumn { Name = "NAME" } },
                Rows = rows,
                ElapsedMs = 1,
                HasMore = false
            };
        }

        private static IList<string> ParseInList(string sql)
        {
            var m = Regex.Match(sql ?? "", @"IN\s*\(([^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!m.Success)
            {
                return Array.Empty<string>();
            }

            return m.Groups[1].Value.Split(',')
                .Select(s => s.Trim().Trim('\'').ToUpperInvariant())
                .Where(s => s.Length > 0)
                .ToList();
        }

        private static string FindTableName(string sql)
        {
            var m = Regex.Match(sql ?? "", @"FROM\s+([A-Z0-9_$#]+)\.([A-Z0-9_$#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return m.Success ? m.Groups[2].Value.ToUpperInvariant() : null;
        }

        private static QueryResult RowResult(string[] columns, string[] values)
        {
            return new QueryResult
            {
                Columns = columns.Select(c => new QueryColumn { Name = c }).ToList(),
                Rows = new List<string[]> { values },
                ElapsedMs = 1,
                HasMore = false
            };
        }

        private static QueryResult EmptyResult(string[] columns)
        {
            return new QueryResult
            {
                Columns = columns.Select(c => new QueryColumn { Name = c }).ToList(),
                Rows = new List<string[]>(),
                ElapsedMs = 1,
                HasMore = false
            };
        }
    }
}
