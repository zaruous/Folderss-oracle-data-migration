using System.Collections.Generic;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Sql;

namespace MigrationStudio.Core.Validation
{
    public sealed class ValidationContext
    {
        public MigrationJob Job { get; set; }
        public MigrationSettings Settings { get; set; }
        public ConnectionTarget Source { get; set; }
        public ConnectionTarget Target { get; set; }
        public ConnectionProfile SourceProfile { get; set; }
        public ConnectionProfile TargetProfile { get; set; }
        public SchemaMetadata SourceMeta { get; set; }
        public SchemaMetadata TargetMeta { get; set; }
        public IDictionary<string, IList<QueryColumn>> SqlDescribe { get; set; }
        public ConnectionTestResult SourceTest { get; set; }
        public ConnectionTestResult TargetTest { get; set; }
    }
}
