using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Metadata;

namespace MigrationStudio.Testing.Adapters
{
    /// <summary>질의 문자열에서 테이블·열을 읽어 POC mock-metadata와 비슷한 샘플 행을 만든다.</summary>
    public sealed class SampleResponder
    {
        private static readonly Regex FromTableRx = new Regex(
            @"FROM\s+([A-Z0-9_$#]+)(?:\s+[A-Z0-9_$#]+)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly SchemaMetadata _source;

        public SampleResponder(SchemaMetadata source)
        {
            _source = source;
        }

        public QueryResult Respond(string sql, int maxRows)
        {
            var tableName = FindTableName(sql);
            var table = tableName != null && _source != null ? _source.FindTable(tableName) : null;
            var columns = new List<QueryColumn>();
            var rows = new List<string[]>();
            if (table == null)
            {
                return new QueryResult
                {
                    Columns = columns,
                    Rows = rows,
                    ElapsedMs = 2,
                    HasMore = false
                };
            }

            foreach (var c in table.Columns)
            {
                columns.Add(new QueryColumn { Name = c.Name, Type = c.Type, Nullable = c.Nullable });
            }

            var limit = Math.Max(1, maxRows);
            for (var i = 0; i < limit; i++)
            {
                var row = new string[table.Columns.Count];
                for (var c = 0; c < table.Columns.Count; c++)
                {
                    row[c] = SampleValue(table.Columns[c], 850001 + i);
                }

                rows.Add(row);
            }

            return new QueryResult
            {
                Columns = columns,
                Rows = rows,
                ElapsedMs = 3,
                HasMore = false
            };
        }

        private static string FindTableName(string sql)
        {
            var m = FromTableRx.Match(sql ?? "");
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }

        private static string SampleValue(ColumnMetadata col, long id)
        {
            var type = (col.Type ?? "").ToUpperInvariant();
            if (col.PrimaryKey || type.StartsWith("NUMBER", StringComparison.Ordinal))
            {
                return id.ToString(CultureInfo.InvariantCulture);
            }

            if (type.StartsWith("DATE", StringComparison.Ordinal) || type.StartsWith("TIMESTAMP", StringComparison.Ordinal))
            {
                return "2020-01-15 10:30:00";
            }

            if (col.Name.IndexOf("PHONE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "010-1234-" + (id % 10000).ToString("0000", CultureInfo.InvariantCulture);
            }

            if (col.Name.IndexOf("NM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                col.Name.IndexOf("NAME", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Sample " + id.ToString(CultureInfo.InvariantCulture);
            }

            return "VAL_" + id.ToString(CultureInfo.InvariantCulture);
        }
    }
}
