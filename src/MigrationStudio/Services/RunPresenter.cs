using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Hosting;
using MigrationStudio.Core.Model;
using MigrationStudio.Logic;

namespace MigrationStudio.Services
{
    /// <summary>이번 창이 보고 있는 실행 한 건. 에이전트 이벤트가 쌓이는 곳(UI 스레드에서만 만진다).</summary>
    internal sealed class RunView
    {
        public IAgentClient Client { get; set; }
        public string RunId { get; set; }
        public string Mode { get; set; }
        public string State { get; set; } = RunStates.Idle;
        public bool Dry { get { return Mode == "DRY"; } }
        public RunSnapshot Snapshot { get; set; }
        public List<LogEntry> Log { get; } = new List<LogEntry>();
        public int Pid { get; set; }
        public string Stage { get; set; }
        public string Error { get; set; }
        public DateTime StartedAt { get; set; }
        /// <summary>끝난 뒤 마지막 스냅숏(결과 알림용).</summary>
        public RunSnapshot Final { get; set; }
        /// <summary>시작할 때의 전략(파이프라인 카드 문구에 쓴다).</summary>
        public int FetchSize { get; set; }
        public int CommitSize { get; set; }
        public int Workers { get; set; }
        /// <summary>재개 시 이미 끝난 행 수(작업 키별).</summary>
        public Dictionary<string, long> BaseRows { get; } = new Dictionary<string, long>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 에이전트 클라이언트의 이벤트(스레드 풀)를 UI 스레드의 <see cref="RunView"/>로 옮기고, 화면 갱신은 0.1초에 한 번으로 묶는다.
    /// 체크포인트는 작업 파일(Job.Checkpoints)에 0.5초마다 반영한다.
    /// </summary>
    internal sealed class RunPresenter
    {
        public const int MaxLogLines = 2000;

        private readonly StudioState _state;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private readonly Dictionary<string, CheckpointRecord> _pendingCheckpoints = new Dictionary<string, CheckpointRecord>(StringComparer.Ordinal);
        private DateTime _lastCheckpointFlush = DateTime.MinValue;
        private bool _dirty;
        private int _newLog;

        public RunPresenter(StudioState state, Dispatcher dispatcher)
        {
            _state = state;
            _dispatcher = dispatcher;
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (s, e) => Flush();
        }

        /// <summary>화면 갱신 요청(0.1초에 한 번). 인수: 새로 쌓인 로그 줄 수.</summary>
        public event Action<int> Render;

        /// <summary>상태가 바뀜(시작·일시정지·끝남 …). 화면 구조를 다시 만들어야 한다.</summary>
        public event Action StateChanged;

        public RunView View
        {
            get { return _state.Run; }
        }

        /// <summary>새 실행을 시작한다. 이전 실행의 클라이언트는 놓는다.</summary>
        public RunView Begin(string mode, int fetchSize, int commitSize, int workers)
        {
            Detach();
            var view = new RunView
            {
                Mode = mode,
                State = RunStates.Starting,
                StartedAt = DateTime.Now,
                FetchSize = fetchSize,
                CommitSize = commitSize,
                Workers = workers
            };
            _state.Run = view;
            _state.IsRunning = true;
            StateChanged?.Invoke();
            return view;
        }

        public void StartFailed(string message)
        {
            var view = _state.Run;
            if (view == null)
            {
                return;
            }

            view.State = RunStates.Idle;
            view.Error = message;
            _state.IsRunning = false;
            _timer.Stop();
            StateChanged?.Invoke();
        }

        /// <summary>시작되었거나 다시 붙은 에이전트 클라이언트를 연결한다.</summary>
        public void Attach(IAgentClient client, string mode, bool reattached)
        {
            var view = _state.Run ?? Begin(mode, 0, 0, 0);
            view.Client = client;
            view.RunId = client.RunId;
            view.Pid = client.Pid;
            view.Mode = mode;
            view.Error = null;
            if (view.State == RunStates.Starting || view.State == RunStates.Detached || reattached)
            {
                view.State = RunStates.Running;
            }

            _state.IsRunning = true;
            client.Snapshot += OnSnapshot;
            client.Log += OnLog;
            client.Checkpoint += OnCheckpoint;
            client.Ended += OnEnded;
            client.Disconnected += OnDisconnected;
            _timer.Start();
            StateChanged?.Invoke();
        }

        public void Detach()
        {
            var view = _state.Run;
            if (view != null && view.Client != null)
            {
                view.Client.Snapshot -= OnSnapshot;
                view.Client.Log -= OnLog;
                view.Client.Checkpoint -= OnCheckpoint;
                view.Client.Ended -= OnEnded;
                view.Client.Disconnected -= OnDisconnected;
                try
                {
                    view.Client.Dispose();
                }
                catch (Exception)
                {
                    // 파이프를 닫다 난 오류는 화면에 알릴 것이 없다
                }

                view.Client = null;
            }

            _timer.Stop();
        }

        public void Pause()
        {
            var view = _state.Run;
            if (view != null && view.Client != null && view.State == RunStates.Running)
            {
                view.State = RunStates.Pausing;
                view.Client.Pause();
                StateChanged?.Invoke();
            }
        }

        public void Resume()
        {
            var view = _state.Run;
            if (view != null && view.Client != null && view.State == RunStates.Paused)
            {
                view.Client.Resume();
            }
        }

        public void Stop()
        {
            var view = _state.Run;
            if (view != null && view.Client != null && RunLogic.IsActive(view.State))
            {
                view.Client.Stop();
            }
        }

        // ---------- 이벤트(스레드 풀) → UI 스레드 ----------

        private void OnSnapshot(RunSnapshot snap)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                var view = _state.Run;
                if (view == null || snap == null)
                {
                    return;
                }

                view.Snapshot = snap;
                // 다시 붙은 실행은 모드를 모르고 시작한다 — 에이전트 스냅숏이 말해 주는 모드(DRY/EXECUTE/RESUME)를 따른다
                if (!string.IsNullOrEmpty(snap.Mode) && !string.Equals(view.Mode, snap.Mode, StringComparison.Ordinal))
                {
                    view.Mode = snap.Mode;
                    StateChanged?.Invoke();
                }

                var next = Map(snap.State);
                if (next != null && next != view.State && RunLogic.IsActive(view.State) || next != null && view.State == RunStates.Starting)
                {
                    view.State = next;
                    StateChanged?.Invoke();
                }

                _dirty = true;
            }));
        }

        private void OnLog(LogEntry entry)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                var view = _state.Run;
                if (view == null || entry == null)
                {
                    return;
                }

                view.Log.Add(entry);
                if (view.Log.Count > MaxLogLines)
                {
                    view.Log.RemoveRange(0, view.Log.Count - MaxLogLines);
                }

                _newLog++;
                _dirty = true;
            }));
        }

        private void OnCheckpoint(CheckpointRecord record)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (record == null || record.TaskKey == null)
                {
                    return;
                }

                var key = record.TaskKey;
                var hash = key.IndexOf('#');
                if (hash > 0)
                {
                    key = key.Substring(0, hash);
                }

                _pendingCheckpoints[key] = record;
                _dirty = true;
            }));
        }

        private void OnEnded(RunSnapshot snap, string reason)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                var view = _state.Run;
                if (view == null)
                {
                    return;
                }

                view.Snapshot = snap ?? view.Snapshot;
                view.Final = view.Snapshot;
                view.State = Map(reason) ?? RunStates.Failed;
                _state.IsRunning = false;
                FlushCheckpoints(true);
                _timer.Stop();
                _dirty = true;
                StateChanged?.Invoke();
                Flush();

                // 최종 결과를 받았으니 에이전트는 더 기다릴 필요 없다(결과·로그는 runs/·logs/에 남는다).
                // 안 보내면 --idle-exit(기본 10분)까지 살아 있어 다음 실행이 "동시 실행 한도"에 걸릴 수 있다.
                var client = view.Client;
                if (client != null)
                {
                    client.ShutdownAsync().ContinueWith(t => { var ignored = t.Exception; }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                }
            }));
        }

        private void OnDisconnected(string reason)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                var view = _state.Run;
                if (view == null || !RunLogic.IsActive(view.State))
                {
                    return;
                }

                view.State = RunStates.Detached;
                view.Error = reason;
                _state.IsRunning = false;
                StateChanged?.Invoke();
            }));
        }

        private static string Map(string engineState)
        {
            switch (engineState)
            {
                case "running": return RunStates.Running;
                case "pausing": return RunStates.Pausing;
                case "paused": return RunStates.Paused;
                case "done": return RunStates.Done;
                case "stopped": return RunStates.Stopped;
                case "failed": return RunStates.Failed;
                default: return null;
            }
        }

        private void Flush()
        {
            if (_dirty)
            {
                _dirty = false;
                var n = _newLog;
                _newLog = 0;
                FlushCheckpoints(false);
                Render?.Invoke(n);
            }
        }

        /// <summary>쌓인 체크포인트를 작업 파일에 반영(0.5초마다, 끝날 때는 즉시).</summary>
        private void FlushCheckpoints(bool force)
        {
            if (_pendingCheckpoints.Count == 0 || (!force && (DateTime.Now - _lastCheckpointFlush).TotalMilliseconds < 500))
            {
                return;
            }

            _lastCheckpointFlush = DateTime.Now;
            var job = _state.Job;
            if (job.Checkpoints == null)
            {
                job.Checkpoints = new Dictionary<string, CheckpointInfo>(StringComparer.Ordinal);
            }

            foreach (var pair in _pendingCheckpoints)
            {
                var r = pair.Value;
                job.Checkpoints[pair.Key] = new CheckpointInfo
                {
                    Column = r.Column,
                    Value = r.Value,
                    Rows = r.RowsDone,
                    Total = r.RowsTotal,
                    At = r.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                    RunId = r.RunId,
                    Status = r.Status
                };
            }

            _pendingCheckpoints.Clear();
            // 체크포인트는 매핑·전략을 바꾸는 게 아니다 — 검증을 무효로 만들지 않는다
            _state.MarkChanged(false);
        }
    }
}
