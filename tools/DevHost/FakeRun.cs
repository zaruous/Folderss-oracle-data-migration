using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Hosting;
using MigrationStudio.Services;

namespace DevHost
{
    /// <summary>
    /// Oracle·에이전트 없이 실행 화면을 보이는 시험용 가짜 에이전트 클라이언트. 고정한 상태의 스냅숏·로그를 이벤트로 보낸다
    /// (running · paused · done · stopped · failed). 실제 진행은 하지 않는다.
    /// </summary>
    internal sealed class FakeAgentClient : IAgentClient
    {
        private readonly string _state;

        public FakeAgentClient(string state)
        {
            _state = state;
            RunId = "R-20261003-144805";
            Pid = 18244;
            PipeName = "folderss-migration-" + RunId;
        }

        public event Action<RunSnapshot> Snapshot;
        public event Action<LogEntry> Log;
        public event Action<CheckpointRecord> Checkpoint;
        public event Action<RunSnapshot, string> Ended;
        #pragma warning disable CS0067
        public event Action<string> Disconnected;
#pragma warning restore CS0067

        public int Pid { get; private set; }
        public string RunId { get; private set; }
        public string PipeName { get; private set; }

        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
        public Task ShutdownAsync() { return Task.CompletedTask; }
        public void Dispose() { }

        /// <summary>연결 직후 상태를 한 번에 보낸다.</summary>
        public void Emit()
        {
            var snap = Build(_state);
            foreach (var e in Logs(_state))
            {
                if (Log != null)
                {
                    Log(e);
                }
            }

            if (Checkpoint != null && snap.Tasks.Count > 0)
            {
                Checkpoint(new CheckpointRecord
                {
                    Job = "CUSTOMER_MIGRATION",
                    TaskKey = "tm-customer",
                    Column = "CUSTOMER_ID",
                    Value = "1080000",
                    RowsDone = 1080000,
                    RowsTotal = 1240325,
                    Status = _state == "done" ? "done" : _state == "running" ? "running" : "stopped",
                    RunId = RunId,
                    UpdatedAt = DateTime.Now
                });
            }

            if (_state == "done" || _state == "stopped" || _state == "failed")
            {
                if (Ended != null)
                {
                    Ended(snap, _state);
                }
            }
            else if (Snapshot != null)
            {
                Snapshot(snap);
            }
        }

        private static List<LogEntry> Logs(string state)
        {
            var t = DateTime.Today.AddHours(14).AddMinutes(48).AddSeconds(5);
            Func<int, string, string, LogEntry> e = (s, tag, text) => new LogEntry { At = t.AddSeconds(s), Tag = tag, Text = text };
            var list = new List<LogEntry>
            {
                e(0, "START", "실행 R-20261003-144805 · 이관 실행 · 작업 3개\n에이전트 MigrationAgent.exe 시작(PID 18244, 파이프 folderss-migration-R-20261003-144805) · 창·Folderss를 닫아도 계속\nBatch size=10,000 · Fetch=5,000 · Workers=4 · 오류 정책=계속 + 오류 테이블 · 체크포인트=대상 DB(MIG_CHECKPOINT)"),
                e(0, "START", "SRC_CUSTOMER → TB_MEMBER  (INSERT + UPDATE)\n체크포인트에서 재개: CUSTOMER_ID > 850000 · 남은 390,325행"),
                e(0, "INFO", "MERGE enabled · 대상 기존 58,225행은 갱신 예상"),
                e(1, "WARN", "거부 1행 → NEXT_APP.ERR$_TB_MEMBER\nORA-01400: NULL을 NOT NULL 컬럼에 넣을 수 없음(공백만 있는 이름 등)"),
                e(8, "INFO", "Committed 500,000 rows · 체크포인트 CUSTOMER_ID = 500000"),
                e(20, "INFO", "Committed 1,000,000 rows · 체크포인트 CUSTOMER_ID = 1000000")
            };
            if (state == "paused")
            {
                list.Add(e(27, "PAUSE", "커밋 경계에서 일시 정지 · SRC_CUSTOMER → TB_MEMBER · 마지막 커밋 CUSTOMER_ID = 1080000"));
            }

            if (state == "stopped")
            {
                list.Add(e(27, "STOP", "사용자가 중지 · 진행 중 배치 9,812행 롤백\n체크포인트 저장: CUSTOMER_ID = 1080000 — [체크포인트에서 재개]로 이어서 실행"));
            }

            if (state == "failed")
            {
                list.Add(e(27, "ERROR", "ORA-03113: 통신 채널에 EOF가 있습니다 · 진행 중 배치 54,016행 롤백\n다시 연결 재시도 3/3 실패\n체크포인트: CUSTOMER_ID = 1080000 (재개하면 여기부터)"));
            }

            if (state == "done")
            {
                list.Add(e(33, "DONE", "SRC_CUSTOMER → TB_MEMBER  (00:00:33)\nInserted : 1,182,063\nUpdated  : 58,225\nRejected : 37  → NEXT_APP.ERR$_TB_MEMBER"));
                list.Add(e(40, "DONE", "전체 3개 작업 · 9,660,448행 · 00:00:40 · 거부 37행"));
            }

            return list;
        }

        public static RunSnapshot Build(string state)
        {
            var snap = new RunSnapshot { RunId = "R-20261003-144805", State = state == "failed" ? "failed" : state, Mode = "EXECUTE" };
            Func<string, string, string, long, long, long, long, long, long, string, TaskSnapshot> t =
                (key, label, status, total, written, ins, upd, rej, commits, cp) => new TaskSnapshot
                {
                    Key = key, Label = label, Status = status, Total = total, Read = written, Written = written, Inserted = ins, Updated = upd, Rejected = rej,
                    Commits = (int)commits, Checkpoint = cp, Pending = status == "run" ? 6500 : 0, RateNow = status == "run" ? 38138 : 0
                };
            switch (state)
            {
                case "done":
                    snap.Tasks.Add(t("tm-customer", "SRC_CUSTOMER → TB_MEMBER", "done", 1240325, 1240325, 1182063, 58225, 37, 125, "CUSTOMER_ID = 1240325"));
                    snap.Tasks.Add(t("tm-order", "SRC_ORDER → TB_SALES_ORDER", "done", 8420117, 8420117, 8420117, 0, 0, 843, "ORDER_NO = O2025123199999"));
                    snap.Tasks.Add(t("tm-grade", "SRC_CUSTOMER_GRADE → TB_MEMBER_GRADE", "done", 6, 6, 6, 0, 0, 1, "—"));
                    snap.Elapsed = 40;
                    snap.Totals = new TotalsSnapshot { Done = 9660448, Total = 9660448, Pct = 100, EtaSeconds = 0 };
                    break;
                case "stopped":
                case "failed":
                    snap.Tasks.Add(t("tm-customer", "SRC_CUSTOMER → TB_MEMBER", state == "failed" ? "failed" : "stopped", 1240325, 1080000, 1029269, 50699, 32, 108, "CUSTOMER_ID = 1080000"));
                    snap.Tasks.Add(t("tm-order", "SRC_ORDER → TB_SALES_ORDER", "skipped", 8420117, 0, 0, 0, 0, 0, "—"));
                    snap.Tasks.Add(t("tm-grade", "SRC_CUSTOMER_GRADE → TB_MEMBER_GRADE", "skipped", 6, 0, 0, 0, 0, 0, "—"));
                    snap.Elapsed = 27;
                    snap.Totals = new TotalsSnapshot { Done = 1080000, Total = 9660448, Pct = 11.2 };
                    break;
                default:
                    snap.Tasks.Add(t("tm-customer", "SRC_CUSTOMER → TB_MEMBER", state == "paused" ? "paused" : "run", 1240325, 1080000, 1029269, 50699, 32, 108, "CUSTOMER_ID = 1080000"));
                    snap.Tasks.Add(t("tm-order", "SRC_ORDER → TB_SALES_ORDER", "wait", 8420117, 0, 0, 0, 0, 0, "—"));
                    snap.Tasks.Add(t("tm-grade", "SRC_CUSTOMER_GRADE → TB_MEMBER_GRADE", "wait", 6, 0, 0, 0, 0, 0, "—"));
                    snap.Elapsed = 29;
                    snap.Rate = state == "paused" ? 0 : 38138;
                    snap.Totals = new TotalsSnapshot { Done = 1080000, Total = 9660448, Pct = 11.2, EtaSeconds = 225 };
                    break;
            }

            snap.Pipeline = new PipelineSnapshot { BufferPercent = 62, WritingWorkers = 4, ReadRowsPerSecond = 37457, CommitsPerSecond = 3 };
            return snap;
        }
    }

    /// <summary>--fake-run &lt;상태&gt;: 시작하면 고정한 상태의 가짜 에이전트가 붙는다.</summary>
    internal sealed class FakeRunService : IRunService
    {
        private readonly string _state;

        public FakeRunService(string state)
        {
            _state = string.IsNullOrEmpty(state) ? "running" : state;
        }

        public Task<IAgentClient> StartAsync(RunRequest request, IProgress<string> stage, CancellationToken ct)
        {
            return Task.FromResult<IAgentClient>(new FakeAgentClient(_state));
        }

        public Task<IAgentClient> AttachAsync(AgentRunInfo run, CancellationToken ct)
        {
            return Task.FromResult<IAgentClient>(new FakeAgentClient(_state));
        }

        public List<AgentRunInfo> ListAlive()
        {
            return new List<AgentRunInfo>();
        }

        public List<AgentRunInfo> ListRecent(int count)
        {
            return new List<AgentRunInfo>();
        }
    }
}
