using System.Collections.Generic;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;

namespace MigrationStudio.Core.Validation
{
    /// <summary>실행 후 검증에 필요한 접속·메타데이터·작업·실행 스냅숏.</summary>
    public sealed class PostValidationContext
    {
        public MigrationJob Job { get; set; }
        public ConnectionTarget Source { get; set; }
        public ConnectionTarget Target { get; set; }
        public SchemaMetadata SourceMeta { get; set; }
        public SchemaMetadata TargetMeta { get; set; }
        public List<PlanItem> Plan { get; set; } = new List<PlanItem>();
        /// <summary>재개 시 이미 처리된 행(작업 키별). 없으면 0으로 본다.</summary>
        public Dictionary<string, long> BaseRows { get; set; } = new Dictionary<string, long>(System.StringComparer.Ordinal);
        public Dictionary<string, IList<QueryColumn>> SqlDescribe { get; set; } = new Dictionary<string, IList<QueryColumn>>(System.StringComparer.Ordinal);
    }
}
