using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;

namespace MigrationStudio.Core.Validation
{
    /// <summary>오류 테이블 거부 행을 ORA 번호별로 집계(FUNC03).</summary>
    public static class RejectSummary
    {
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(2);

        public static async Task<string> FormatAsync(
            IDatabaseAdapter adapter,
            ConnectionTarget target,
            string schema,
            string errorTable,
            string runId,
            CancellationToken ct)
        {
            if (adapter == null || target == null || string.IsNullOrEmpty(errorTable) || string.IsNullOrEmpty(runId))
            {
                return "";
            }

            try
            {
                var sql = PostValidationSql.RejectSummary(schema, errorTable, runId);
                OracleSelectGuard.EnsureSelectOnly(sql);
                QueryResult query;
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    linked.CancelAfter(QueryTimeout);
                    query = await adapter.QueryAsync(target, schema, sql, null, 50, linked.Token).ConfigureAwait(false);
                }

                if (query == null || query.Rows == null || query.Rows.Count == 0)
                {
                    return "";
                }

                var parts = new List<string>();
                foreach (var row in query.Rows)
                {
                    var num = Read(query, row, "ERR_NUM");
                    var cnt = Read(query, row, "ROWS_CNT");
                    if (string.IsNullOrEmpty(num) || string.IsNullOrEmpty(cnt))
                    {
                        continue;
                    }

                    parts.Add("ORA-" + num.PadLeft(5, '0') + " ×" + cnt);
                }

                return string.Join(" · ", parts);
            }
            catch
            {
                return "";
            }
        }

        private static string Read(QueryResult query, string[] row, string name)
        {
            for (var i = 0; i < query.Columns.Count; i++)
            {
                if (string.Equals(query.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return i < row.Length ? row[i] : null;
                }
            }

            return null;
        }
    }
}
