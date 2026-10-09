using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;

namespace MigrationStudio.Core.Hosting
{
    /// <summary>
    /// 실행 에이전트(MigrationAgent.exe)와 파이프로 이어진 클라이언트. 화면은 이 인터페이스만 안다.
    /// 이벤트는 스레드 풀 스레드에서 호출되므로 구독자가 UI 스레드로 보낸다. <see cref="IDisposable.Dispose"/>는 파이프만 닫고 에이전트를 죽이지 않는다.
    /// </summary>
    public interface IAgentClient : IDisposable
    {
        event Action<RunSnapshot> Snapshot;
        event Action<LogEntry> Log;
        event Action<CheckpointRecord> Checkpoint;
        /// <summary>실행이 끝남: (마지막 스냅숏, "done"|"stopped"|"failed").</summary>
        event Action<RunSnapshot, string> Ended;
        /// <summary>파이프가 끊김. 인수는 사유(에이전트가 죽었으면 종료 코드 포함). 자동 재연결은 하지 않는다.</summary>
        event Action<string> Disconnected;

        int Pid { get; }
        string RunId { get; }
        string PipeName { get; }

        void Pause();
        void Resume();
        void Stop();
        Task ShutdownAsync();
    }

    /// <summary>살아 있거나 최근에 끝난 에이전트 실행 한 건(runs\&lt;RUN_ID&gt;.json).</summary>
    public sealed class AgentRunInfo
    {
        public string RunId { get; set; }
        public int Pid { get; set; }
        public string Pipe { get; set; }
        public DateTime StartedAt { get; set; }
        public string AgentVersion { get; set; }
        /// <summary>waiting · running · done · stopped · failed · crashed</summary>
        public string State { get; set; }
        public string JobName { get; set; }
        public string Message { get; set; }
        /// <summary>변경동기화(SYNC)일 때 마지막으로 끝난 주기 번호·시각. 다시 붙기 전에 "얼마나 전에 돌았나"를 보기 위한 값.</summary>
        public int Cycle { get; set; }
        public DateTime? LastCycleAt { get; set; }
    }
}
