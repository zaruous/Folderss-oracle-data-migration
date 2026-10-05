using System.Collections.Generic;
using MigrationStudio.Core.Expressions;
using MigrationStudio.Core.Metadata;

namespace MigrationStudio.Core.Sql
{
    public sealed class SqlError
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public int? Line { get; set; }
    }

    public sealed class SelectItem
    {
        public string Text { get; set; }
        public string Expr { get; set; }
        public string Alias { get; set; }
        public string Name { get; set; }
        public bool Star { get; set; }
        public int Position { get; set; }
        public ExpressionNode Node { get; set; }
        public IReadOnlyList<string> Refs { get; set; }
        public SqlParseException Error { get; set; }
    }

    public sealed class TableRef
    {
        public string Schema { get; set; }
        public string Name { get; set; }
        public string Alias { get; set; }
        public string Join { get; set; }
        public string On { get; set; }
        public bool Subquery { get; set; }
    }

    public sealed class WhereClause
    {
        public string Text { get; set; }
        public ExpressionNode Node { get; set; }
        public SqlParseException Error { get; set; }
    }

    public sealed class SelectStatement
    {
        public List<SqlError> Errors { get; set; } = new List<SqlError>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<SelectItem> Items { get; set; } = new List<SelectItem>();
        public List<TableRef> Tables { get; set; } = new List<TableRef>();
        public List<string> Binds { get; set; } = new List<string>();
        public WhereClause Where { get; set; }
        public string OrderBy { get; set; }
        public bool GroupBy { get; set; }
    }

    public sealed class ResultColumn
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Expr { get; set; }
        public string Alias { get; set; }
        public bool? Nullable { get; set; }
        public ColumnStats Stats { get; set; }
        public ColumnMetadata Source { get; set; }
        public string Error { get; set; }
    }

    public sealed class DescribeResult
    {
        public List<SqlError> Errors { get; set; } = new List<SqlError>();
        public List<ResultColumn> Columns { get; set; } = new List<ResultColumn>();

        internal DescribeRuntime Runtime { get; set; }
    }

    internal sealed class DescribeRuntime
    {
        public List<ResolvedTableRef> Tables { get; set; } = new List<ResolvedTableRef>();
    }

    internal sealed class ResolvedTableRef
    {
        public TableRef Ref { get; set; }
        public TableMetadata Meta { get; set; }
    }

    public sealed class SqlSourceInfo
    {
        public TableMetadata Table { get; set; }
        public long? BaseRows { get; set; }
        public SqlError Error { get; set; }
        public SelectStatement Statement { get; set; }
    }

    public sealed class CheckItem
    {
        public string Check { get; set; }
        public string Level { get; set; }
        public string Detail { get; set; }
    }

    public sealed class SqlValidationResult
    {
        public string Level { get; set; }
        public List<CheckItem> Items { get; set; } = new List<CheckItem>();
        public SelectStatement Statement { get; set; }
        public List<ResultColumn> Columns { get; set; } = new List<ResultColumn>();
        public int Mapped { get; set; }
        public int Total { get; set; }
        public int? ErrorLine { get; set; }
    }

    public sealed class WriteColumn
    {
        public string Name { get; set; }
        public string Expr { get; set; }
        public ColumnMetadata Column { get; set; }
    }

    public sealed class SourceSelectOptions
    {
        public int FetchSize { get; set; } = 5000;
        public int Workers { get; set; } = 1;
    }
}
