using System;
using System.Text.RegularExpressions;

namespace MigrationStudio.Core.Sql
{
    public static class SqlText
    {
        private static readonly Regex StripCommentsRx = new Regex(
            @"('(?:[^']|'')*')|--[^\n]*|/\*[\s\S]*?\*/",
            RegexOptions.CultureInvariant);

        public static string StripComments(string sql)
        {
            if (sql == null)
            {
                return "";
            }

            return StripCommentsRx.Replace(sql, m =>
            {
                if (m.Groups[1].Success)
                {
                    return m.Groups[1].Value;
                }

                return Regex.Replace(m.Value, "[^\n]", " ");
            });
        }

        public static int LineOf(string sql, int position)
        {
            if (sql == null)
            {
                return 1;
            }

            var at = Math.Max(0, position);
            var slice = sql.Substring(0, at);
            var lines = 1;
            for (var i = 0; i < slice.Length; i++)
            {
                if (slice[i] == '\n')
                {
                    lines++;
                }
            }

            return lines;
        }
    }
}
