using System.Collections.Generic;
using MigrationStudio.Core.Expressions;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;

namespace MigrationStudio.Core.Mapping
{
    public sealed class TableMatch
    {
        public string Source { get; set; }
        public string Target { get; set; }
        public string Reason { get; set; }
    }

    public sealed class AutoMappedColumn
    {
        public ColumnMapping Mapping { get; set; }
        public string Reason { get; set; }
    }

    public sealed class CompatResult
    {
        public string Level { get; set; }
        public string Kind { get; set; }
        public string Message { get; set; }
    }

    public sealed class CheckMessage
    {
        public string Level { get; set; }
        public string Message { get; set; }
    }

    public sealed class ColumnCheck
    {
        public string Level { get; set; }
        public List<CheckMessage> Messages { get; set; } = new List<CheckMessage>();
        public string Type { get; set; }
    }

    public sealed class SourceValueInfo
    {
        public string Type { get; set; }
        public ColumnStats Stats { get; set; }
        public long? Nulls { get; set; }
        public IReadOnlyList<string> Refs { get; set; }
        public bool Nullable { get; set; }
        public SqlParseException Error { get; set; }
    }

    public sealed class ColumnResult
    {
        public ColumnMetadata Target { get; set; }
        public ColumnMapping Mapping { get; set; }
        public ColumnCheck Check { get; set; }
    }

    public sealed class MappingStatus
    {
        public int Mapped { get; set; }
        public int Total { get; set; }
        public string Level { get; set; }
        public int Errors { get; set; }
        public int Warns { get; set; }
        public List<ColumnResult> Results { get; set; } = new List<ColumnResult>();
    }
}
