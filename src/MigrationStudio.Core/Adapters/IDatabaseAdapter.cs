using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Metadata;

namespace MigrationStudio.Core.Adapters
{
    public interface IDatabaseAdapter
    {
        string Kind { get; }
        string Title { get; }

        Task<ConnectionTestResult> TestAsync(ConnectionTarget target, bool readOnly, CancellationToken cancellationToken);

        Task<SchemaMetadata> LoadMetadataAsync(ConnectionTarget target, string schema, CancellationToken cancellationToken);

        Task<ControlStoreCheck> CheckControlStoreAsync(ConnectionTarget target, string schema, string prefix, string store, CancellationToken cancellationToken);

        Task<SqlParseResult> ParseSqlAsync(ConnectionTarget target, string schema, string sql, CancellationToken cancellationToken);

        Task<List<QueryColumn>> DescribeSqlAsync(ConnectionTarget target, string schema, string sql, CancellationToken cancellationToken);

        Task<QueryResult> QueryAsync(
            ConnectionTarget target,
            string schema,
            string sql,
            IList<SqlBind> binds,
            int maxRows,
            CancellationToken cancellationToken);

        Task<long?> CountAsync(
            ConnectionTarget target,
            string schema,
            string sql,
            IList<SqlBind> binds,
            CancellationToken cancellationToken);
    }
}
