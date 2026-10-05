using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Hosting;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;

namespace MigrationStudio.Services
{
    /// <summary>실행 시작에 필요한 것(화면이 만든다).</summary>
    internal sealed class RunRequest
    {
        public MigrationJob Job { get; set; }
        public SchemaMetadata SourceMeta { get; set; }
        public SchemaMetadata TargetMeta { get; set; }
        /// <summary>실행할 매핑 id.</summary>
        public ISet<string> Selected { get; set; }
        /// <summary>DRY · EXECUTE · RESUME</summary>
        public string Mode { get; set; }
    }

    /// <summary>실행 시작·다시 붙기·살아 있는 에이전트 조회. 구현: RunService(RunLauncher 감싸기), DevHost 시험용 가짜.</summary>
    internal interface IRunService
    {
        /// <summary>계획 → 저장소 결정 → 에이전트 실행 → 연결 → 시작. 실패는 단계 이름이 든 메시지의 예외. <paramref name="stage"/>에 진행 단계 문구를 알린다.</summary>
        Task<IAgentClient> StartAsync(RunRequest request, IProgress<string> stage, CancellationToken ct);

        /// <summary>살아 있는 에이전트에 다시 붙는다(현재 스냅숏·최근 로그를 받음).</summary>
        Task<IAgentClient> AttachAsync(AgentRunInfo run, CancellationToken ct);

        /// <summary>살아 있는 에이전트 목록(PID가 살아 있는 것).</summary>
        List<AgentRunInfo> ListAlive();

        /// <summary>최근 실행 목록(끝난 것 포함, 최근 순).</summary>
        List<AgentRunInfo> ListRecent(int count);
    }
}
