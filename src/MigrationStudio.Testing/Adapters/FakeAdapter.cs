using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Sql;

namespace MigrationStudio.Testing.Adapters
{
    public sealed class FakeAdapterCall
    {
        public string Method;
        public string Sql;
    }

    /// <summary>Oracle 없이 화면·서비스를 시험하는 어댑터.</summary>
    public sealed class FakeAdapter : IDatabaseAdapter
    {
        public SchemaMetadata Metadata;
        public TimeSpan Delay = TimeSpan.Zero;
        public Exception FailNext;
        public Dictionary<string, QueryResult> QueryResults = new Dictionary<string, QueryResult>(StringComparer.Ordinal);
        public Dictionary<string, long?> CountResults = new Dictionary<string, long?>(StringComparer.Ordinal);
        public List<FakeAdapterCall> Calls = new List<FakeAdapterCall>();
        public Func<string, QueryResult> QueryResponder;

        public FakeAdapter(SchemaMetadata metadata)
        {
            Metadata = metadata;
        }

        public string Kind
        {
            get { return "oracle"; }
        }

        public string Title
        {
            get { return "Fake Oracle"; }
        }

        public Task<ConnectionTestResult> TestAsync(ConnectionTarget target, bool readOnly, CancellationToken cancellationToken)
        {
            return Run(() =>
            {
                MaybeFail();
                return new ConnectionTestResult
                {
                    Ok = true,
                    Version = "Oracle 19c",
                    LatencyMs = 1,
                    TestedAt = DateTime.Now
                };
            }, cancellationToken);
        }

        public Task<SchemaMetadata> LoadMetadataAsync(ConnectionTarget target, string schema, CancellationToken cancellationToken)
        {
            return Run(() =>
            {
                MaybeFail();
                return Metadata;
            }, cancellationToken);
        }

        public Task<ControlStoreCheck> CheckControlStoreAsync(
            ConnectionTarget target,
            string schema,
            string prefix,
            string store,
            CancellationToken cancellationToken)
        {
            return Run(() =>
            {
                MaybeFail();
                return new ControlStoreCheck { Resolved = CheckpointStores.Local, Reason = "시험용" };
            }, cancellationToken);
        }

        public Task<SqlParseResult> ParseSqlAsync(ConnectionTarget target, string schema, string sql, CancellationToken cancellationToken)
        {
            return Run(() =>
            {
                MaybeFail();
                OracleSelectGuard.EnsureSelectOnly(sql);
                var parsed = SelectParser.Parse(sql);
                if (parsed.Errors.Count > 0)
                {
                    var e = parsed.Errors[0];
                    return new SqlParseResult
                    {
                        Ok = false,
                        ErrorCode = e.Code,
                        Message = e.Message,
                        Line = e.Line
                    };
                }

                return new SqlParseResult { Ok = true };
            }, cancellationToken);
        }

        public Task<List<QueryColumn>> DescribeSqlAsync(ConnectionTarget target, string schema, string sql, CancellationToken cancellationToken)
        {
            return Run(() =>
            {
                MaybeFail();
                OracleSelectGuard.EnsureSelectOnly(sql);
                var parsed = SelectParser.Parse(sql);
                var described = SqlSourceAnalyzer.Describe(parsed, Metadata);
                return described.Columns
                    .Where(c => c.Error == null)
                    .Select(c => new QueryColumn
                    {
                        Name = c.Name,
                        Type = c.Type,
                        Nullable = c.Nullable ?? true
                    })
                    .ToList();
            }, cancellationToken);
        }

        public Task<QueryResult> QueryAsync(
            ConnectionTarget target,
            string schema,
            string sql,
            IList<SqlBind> binds,
            int maxRows,
            CancellationToken cancellationToken)
        {
            return Run(() =>
            {
                MaybeFail();
                OracleSelectGuard.EnsureSelectOnly(sql);
                Calls.Add(new FakeAdapterCall { Method = "QueryAsync", Sql = sql });
                QueryResult result;
                if (QueryResponder != null && QueryResponder(sql) != null)
                {
                    result = QueryResponder(sql);
                }
                else if (QueryResults.TryGetValue(sql, out result))
                {
                }
                else
                {
                    result = new QueryResult
                    {
                        Columns = new List<QueryColumn>(),
                        Rows = new List<string[]>(),
                        ElapsedMs = 1,
                        HasMore = false
                    };
                }

                return result;
            }, cancellationToken);
        }

        public Task<long?> CountAsync(
            ConnectionTarget target,
            string schema,
            string sql,
            IList<SqlBind> binds,
            CancellationToken cancellationToken)
        {
            return Run(() =>
            {
                MaybeFail();
                Calls.Add(new FakeAdapterCall { Method = "CountAsync", Sql = sql });
                if (CountResults.TryGetValue(sql, out var count))
                {
                    return count;
                }

                if (QueryResponder != null)
                {
                    var query = QueryResponder(sql);
                    if (query != null && query.Rows != null && query.Rows.Count > 0)
                    {
                        var idx = 0;
                        if (query.Columns != null)
                        {
                            for (var i = 0; i < query.Columns.Count; i++)
                            {
                                if (string.Equals(query.Columns[i].Name, "CNT", System.StringComparison.OrdinalIgnoreCase))
                                {
                                    idx = i;
                                    break;
                                }
                            }
                        }

                        var cell = query.Rows[0][idx];
                        if (long.TryParse(cell, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                        {
                            return parsed;
                        }
                    }
                }

                return (long?)null;
            }, cancellationToken);
        }

        private void MaybeFail()
        {
            if (FailNext != null)
            {
                var ex = FailNext;
                FailNext = null;
                throw ex;
            }
        }

        private Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Delay > TimeSpan.Zero)
                {
                    Thread.Sleep(Delay);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return work();
            }, cancellationToken);
        }
    }
}
