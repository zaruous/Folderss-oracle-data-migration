using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using MigrationStudio.Core.Metadata;

namespace MigrationStudio.Tests.Golden
{
    public sealed class GoldenDocument
    {
        public SchemaMetadata Source { get; set; }
        public SchemaMetadata Target { get; set; }
        public List<GoldenTypeCase> Types { get; set; }
        public List<GoldenCompatCase> Compat { get; set; }
        public List<GoldenExpressionCase> Expressions { get; set; }
        public List<GoldenTableMatch> AutoMatchTables { get; set; }
        public List<GoldenAutoMapCase> AutoMapColumns { get; set; }
        public JsonElement SampleJob { get; set; }
        public List<GoldenMappingStatusCase> MappingStatus { get; set; }
        public List<GoldenParseSelectCase> ParseSelect { get; set; }
        public List<GoldenDescribeCase> Describe { get; set; }
        public GoldenVirtualSourceCase VirtualSource { get; set; }
        public List<GoldenSqlValidateCase> SqlValidate { get; set; }
        public GoldenSqlGen Sqlgen { get; set; }
        public JsonElement JobV1 { get; set; }
        public GoldenJobV1Upgraded JobV1Upgraded { get; set; }
        public string YamlSample { get; set; }
        public GoldenSettingsDefaults SettingsDefaults { get; set; }
    }

    public sealed class GoldenTypeCase
    {
        public string Input { get; set; }
        public string Base { get; set; }
        public int? Length { get; set; }
        public int? Precision { get; set; }
        public int? Scale { get; set; }
        public bool IsChar { get; set; }
        public bool IsDate { get; set; }
    }

    public sealed class GoldenCompatCase
    {
        public string Source { get; set; }
        public string Target { get; set; }
        public ColumnStats Stats { get; set; }
        public GoldenCompatResult Result { get; set; }
    }

    public sealed class GoldenCompatResult
    {
        public string Level { get; set; }
        public string Kind { get; set; }

        [JsonPropertyName("msg")]
        public string Message { get; set; }
    }

    public sealed class GoldenExpressionCase
    {
        public string Expr { get; set; }
        public GoldenExpressionError Error { get; set; }
        public List<string> Refs { get; set; }
        public List<string> Binds { get; set; }
        public string Type { get; set; }
    }

    public sealed class GoldenExpressionError
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public int? Position { get; set; }
    }

    public sealed class GoldenTableMatch
    {
        public string Source { get; set; }
        public string Target { get; set; }
        public string Reason { get; set; }
    }

    public sealed class GoldenAutoMapCase
    {
        public string Source { get; set; }
        public string Target { get; set; }
        public List<GoldenAutoMappedColumn> Columns { get; set; }
    }

    public sealed class GoldenAutoMappedColumn
    {
        public string Target { get; set; }
        public string Source { get; set; }
        public string Expr { get; set; }
        public string NullRule { get; set; }
        public string DefaultValue { get; set; }
        public string Reason { get; set; }
    }

    public sealed class GoldenMappingStatusCase
    {
        public string Mapping { get; set; }
        public int Mapped { get; set; }
        public int Total { get; set; }
        public string Level { get; set; }
        public int Errors { get; set; }
        public int Warns { get; set; }
        public List<GoldenColumnStatus> Columns { get; set; }
    }

    public sealed class GoldenColumnStatus
    {
        public string Target { get; set; }
        public string Level { get; set; }
        public string Type { get; set; }
        public List<GoldenCheckMessage> Msgs { get; set; }
    }

    public sealed class GoldenCheckMessage
    {
        public string Level { get; set; }

        [JsonPropertyName("msg")]
        public string Message { get; set; }
    }

    public sealed class GoldenParseSelectCase
    {
        public string Key { get; set; }
        public string Sql { get; set; }
        public List<GoldenSqlError> Errors { get; set; }
        public List<string> Warnings { get; set; }
        public List<GoldenSelectItem> Items { get; set; }
        public List<JsonElement> Tables { get; set; }
        public List<string> Binds { get; set; }
        public string Where { get; set; }
        public string OrderBy { get; set; }
        public bool GroupBy { get; set; }
    }

    public sealed class GoldenSqlError
    {
        public string Code { get; set; }

        [JsonPropertyName("msg")]
        public string Message { get; set; }
        public int? Line { get; set; }
    }

    public sealed class GoldenSelectItem
    {
        public string Name { get; set; }
        public string Alias { get; set; }
        public string Expr { get; set; }
        public bool Star { get; set; }
        public string Error { get; set; }
    }

    public sealed class GoldenDescribeCase
    {
        public string Key { get; set; }
        public List<GoldenSqlError> Errors { get; set; }
        public List<GoldenDescribeColumn> Columns { get; set; }
    }

    public sealed class GoldenDescribeColumn
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Expr { get; set; }
        public bool? Nullable { get; set; }
        public string Error { get; set; }
        public GoldenDescribeStats Stats { get; set; }
    }

    public sealed class GoldenDescribeStats
    {
        public long? Nulls { get; set; }
        public long? Blanks { get; set; }
        public int? MaxLength { get; set; }
    }

    public sealed class GoldenVirtualSourceCase
    {
        public string Mapping { get; set; }
        public string Name { get; set; }
        public long? Rows { get; set; }
        public long? BaseRows { get; set; }
        public string Comment { get; set; }
        public List<GoldenVirtualColumn> Columns { get; set; }
    }

    public sealed class GoldenVirtualColumn
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public bool Nullable { get; set; }
        public string Comment { get; set; }
    }

    public sealed class GoldenSqlValidateCase
    {
        public string Key { get; set; }
        public string Level { get; set; }
        public int Mapped { get; set; }
        public int Total { get; set; }
        public int? ErrorLine { get; set; }
        public List<GoldenCheckItem> Items { get; set; }
    }

    public sealed class GoldenCheckItem
    {
        public string Check { get; set; }
        public string Level { get; set; }
        public string Detail { get; set; }
    }

    public sealed class GoldenSqlGen
    {
        public string SourceSelectTableWorkers4 { get; set; }
        public string SourceSelectTableWorkers1Where { get; set; }
        public string SourceSelectSql { get; set; }
        public List<GoldenWriteColumn> WriteColumnsCustomer { get; set; }
        public Dictionary<string, string> Write { get; set; }
        public List<GoldenLiteralCase> Literal { get; set; }
        public List<GoldenValueExprCase> ValueExpr { get; set; }
        public List<GoldenErrorTableCase> ErrorTableFor { get; set; }
    }

    public sealed class GoldenWriteColumn
    {
        public string Name { get; set; }
        public string Expr { get; set; }
    }

    public sealed class GoldenLiteralCase
    {
        public string Value { get; set; }
        public string Type { get; set; }
        public string Result { get; set; }
    }

    public sealed class GoldenValueExprCase
    {
        public string Rule { get; set; }
        public string Source { get; set; }
        public string Expr { get; set; }
        public string DefaultValue { get; set; }
        public string TargetType { get; set; }
        public string Result { get; set; }
    }

    public sealed class GoldenErrorTableCase
    {
        public GoldenErrorTableStrategy Strategy { get; set; }
        public string Target { get; set; }
        public string Result { get; set; }
    }

    public sealed class GoldenErrorTableStrategy
    {
        public string ErrorPolicy { get; set; }
        public string ErrorTable { get; set; }
    }

    public sealed class GoldenJobV1Upgraded
    {
        public int Version { get; set; }
        public GoldenConnectionRef Source { get; set; }
        public GoldenConnectionRef Target { get; set; }
        public List<GoldenUpgradedMapping> Mappings { get; set; }
        public Dictionary<string, JsonElement> Checkpoints { get; set; }
    }

    public sealed class GoldenConnectionRef
    {
        public string ProfileId { get; set; }
        public string Schema { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Host { get; set; }
        public string Port { get; set; }
        public string Service { get; set; }
        public string User { get; set; }
        public string Color { get; set; }
    }

    public sealed class GoldenUpgradedMapping
    {
        public string Id { get; set; }
        public bool Use { get; set; }
        public string SourceType { get; set; }
        public string Source { get; set; }
        public string Sql { get; set; }
        public string Target { get; set; }
        public string Mode { get; set; }
        public List<string> MergeKey { get; set; }
        public string CheckpointColumn { get; set; }
        public List<GoldenUpgradedColumn> Columns { get; set; }
    }

    public sealed class GoldenUpgradedColumn
    {
        public string Target { get; set; }
        public string Source { get; set; }
        public string Expr { get; set; }
        public string NullRule { get; set; }
        public string DefaultValue { get; set; }
    }

    public sealed class GoldenSettingsDefaults
    {
        public int Version { get; set; }
        public MigrationStudio.Core.Settings.MigrationDefaults Defaults { get; set; }
        public MigrationStudio.Core.Settings.AgentSettings Agent { get; set; }
    }
}
