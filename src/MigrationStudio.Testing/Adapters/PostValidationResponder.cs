using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Adapters;

namespace MigrationStudio.Testing.Adapters
{
    /// <summary>실행 후 검증 질의에 맞춘 가짜 응답(단위 시험).</summary>
    public sealed class PostValidationResponder
    {
        private readonly long _scopeRows;
        private readonly long _written;
        private readonly long _rejected;

        public PostValidationResponder(long scopeRows, long written, long rejected)
        {
            _scopeRows = scopeRows;
            _written = written;
            _rejected = rejected;
        }

        public QueryResult Respond(string sql)
        {
            var upper = sql ?? "";
            if (upper.IndexOf("MOD(ORA_HASH", StringComparison.OrdinalIgnoreCase) >= 0
                && upper.IndexOf("GROUP BY", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return BucketMatch();
            }

            if (upper.IndexOf("HAVING COUNT(*) > 1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Row("CNT", "0");
            }

            if (upper.IndexOf("SUM(ORA_HASH", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Row("HSUM", "999888777");
            }

            if (upper.IndexOf("DBMS_RANDOM", StringComparison.OrdinalIgnoreCase) >= 0
                || upper.IndexOf("SAMPLE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SampleRows();
            }

            if (upper.IndexOf("NULL_CNT", StringComparison.OrdinalIgnoreCase) >= 0
                || (upper.IndexOf("COUNT(*) - COUNT(", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return Row("NULL_CNT", "3");
            }

            if (upper.IndexOf("ORA_ERR_TAG$", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Row("CNT", _rejected.ToString(CultureInfo.InvariantCulture));
            }

            if (upper.IndexOf(" IN (:K1", StringComparison.OrdinalIgnoreCase) >= 0
                && upper.IndexOf("SELECT ID", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new QueryResult
                {
                    Columns = new List<QueryColumn>
                    {
                        new QueryColumn { Name = "ID" },
                        new QueryColumn { Name = "NAME" }
                    },
                    Rows = new List<string[]> { new[] { "1", "A" } },
                    ElapsedMs = 1,
                    HasMore = false
                };
            }

            if (upper.IndexOf("SELECT COUNT(*)", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (upper.IndexOf("IN (", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var m = Regex.Match(upper, @"IN\s*\(([^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    var binds = m.Success ? m.Groups[1].Value.Split(',').Length : 0;
                    return Row("CNT", binds.ToString(CultureInfo.InvariantCulture));
                }

                if (upper.IndexOf("GROUP BY", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Row("CNT", "0");
                }

                if (upper.IndexOf("ERR$_", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Row("CNT", _rejected.ToString(CultureInfo.InvariantCulture));
                }

                if (upper.IndexOf("_TGT", StringComparison.OrdinalIgnoreCase) >= 0
                    || upper.IndexOf("TB_", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Row("CNT", (_written).ToString(CultureInfo.InvariantCulture));
                }

                return Row("CNT", _scopeRows.ToString(CultureInfo.InvariantCulture));
            }

            return new QueryResult { Columns = new List<QueryColumn>(), Rows = new List<string[]>(), ElapsedMs = 1, HasMore = false };
        }

        private QueryResult BucketMatch()
        {
            var rows = new List<string[]>();
            for (var i = 0; i < 3; i++)
            {
                rows.Add(new[] { i.ToString(CultureInfo.InvariantCulture), "10", "12345" });
            }

            return new QueryResult
            {
                Columns = new List<QueryColumn>
                {
                    new QueryColumn { Name = "BUCKET" },
                    new QueryColumn { Name = "CNT" },
                    new QueryColumn { Name = "HSUM" }
                },
                Rows = rows,
                ElapsedMs = 1,
                HasMore = false
            };
        }

        private QueryResult SampleRows()
        {
            return new QueryResult
            {
                Columns = new List<QueryColumn>
                {
                    new QueryColumn { Name = "ID" },
                    new QueryColumn { Name = "NAME" }
                },
                Rows = new List<string[]> { new[] { "1", "A" } },
                ElapsedMs = 1,
                HasMore = false
            };
        }

        private static QueryResult Row(string col, string val)
        {
            return new QueryResult
            {
                Columns = new List<QueryColumn> { new QueryColumn { Name = col } },
                Rows = new List<string[]> { new[] { val } },
                ElapsedMs = 1,
                HasMore = false
            };
        }
    }
}
