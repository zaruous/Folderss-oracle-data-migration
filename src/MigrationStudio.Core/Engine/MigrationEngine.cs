using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MigrationStudio.Core.Types;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Engine
{
    public sealed class MigrationEngine
    {
        private const int ChannelCapacity = 2;
        private readonly RunSpec _spec;
        private readonly ISourceFactory _source;
        private readonly ITargetFactory _target;
        private readonly ICheckpointStore _store;
        private readonly IRunListener _listener;
        private readonly IRunClock _clock;
        private readonly IRunRecorder _recorder;
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly List<TaskState> _tasks = new List<TaskState>();
        private readonly HashSet<TrackedTargetSession> _openTargetSessions = new HashSet<TrackedTargetSession>();
        private DateTime _startedAt;
        private DateTime _lastSnapshotAt;
        private long _lastRateRows;
        private DateTime _lastRateAt;
        private double _rate;
        private bool _pauseRequested;
        private int _activeWorkers;
        private int _pausedWorkers;
        private string _state = "idle";
        private bool _ended;
        // SYNC(변경동기화) 주기 상태
        private int _cycle;
        private string _syncPhase;
        private DateTime? _lastCycleAt;
        private DateTime? _nextCycleAt;
        private long _syncWritten;
        private long _syncInserted;
        private long _syncUpdated;
        private long _syncRejected;
        private int _consecutiveFailures;
        private bool _windowWarned;
        private const int MaxConsecutiveFailures = 5;

        private DateTime? _lastReconcileAt;
        private bool _reconcileWarned;
        /// <summary>한 번의 대조에서 모아 두는 표시 후보 상한(메모리). 넘으면 표시하지 않고 멈춘다.</summary>
        internal const int MaxReconcileCollect = 200000;
        /// <summary>표시 상한의 바닥값 — 작은 테이블에서 비율 상한이 1~2행이 되어 정상 삭제까지 막지 않게.</summary>
        internal const int MinDeleteCap = 10;

        /// <summary>삭제 대조 저장소(원본·대상 키 비교, 대상 표시). 없으면 대조하지 않는다(삭제 표시 매핑이 있으면 한 번 경고).</summary>
        public IReconcileStore Reconciler { get; set; }

        /// <summary>주기 사이 대기. 시험에서는 가짜 시계를 움직이는 함수로 바꿔 끼운다.</summary>
        public Func<TimeSpan, CancellationToken, Task> CycleDelay { get; set; } = (span, token) => Task.Delay(span, token);

        public MigrationEngine(RunSpec spec, ISourceFactory source, ITargetFactory target,
            ICheckpointStore store, IRunListener listener, IRunClock clock)
            : this(spec, source, target, store, listener, clock, new NullRunRecorder())
        {
        }

        public MigrationEngine(RunSpec spec, ISourceFactory source, ITargetFactory target,
            ICheckpointStore store, IRunListener listener, IRunClock clock, IRunRecorder recorder)
        {
            _spec = spec ?? throw new ArgumentNullException(nameof(spec));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _listener = listener ?? new NullRunListener();
            _clock = clock ?? new SystemRunClock();
            // 실행 기록(MIG_RUN·MIG_RUN_TASK)은 이관의 부가 정보다 — 기록이 안 된다고 이관을 멈추지 않고, 한 번 경고한 뒤 기록을 끈다
            _recorder = recorder == null ? (IRunRecorder)new NullRunRecorder() : new BestEffortRecorder(this, recorder);
            foreach (var item in _spec.Plan)
            {
                _tasks.Add(new TaskState(item));
            }
        }

        public string State
        {
            get { lock (_gate) { return _state; } }
        }

        public async Task RunAsync(CancellationToken hostCancel)
        {
            lock (_gate)
            {
                if (_state != "idle")
                {
                    return;
                }

                _state = "running";
                _startedAt = _clock.Now;
                _lastSnapshotAt = _startedAt;
                _lastRateAt = _startedAt;
            }

            using (hostCancel.Register(Stop))
            {
                try
                {
                    Log("START", "실행 " + _spec.RunId + " · " + ModeText(_spec.RunMode) + " · 작업 " + _spec.Plan.Count + "개\n" +
                        "Batch size=" + Number(CommitSize()) + " · Fetch=" + Number(FetchSize()) + " · Workers=" + Workers() +
                        " · 오류 정책=" + PolicyText(ErrorPolicy()) + " · 체크포인트=" + StoreText() +
                        (IsSync() ? "\n주기=" + PollInterval().TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "초 · 최대 실행=" +
                            (MaxRunHours() > 0 ? MaxRunHours() + "시간" : "무기한(중지할 때까지)") + " · 창을 닫아도 에이전트가 계속 동기화" : ""));
                    EmitSnapshot(true);
                    var preparation = _target as ITargetPreparation;
                    if (preparation != null && !IsDry())
                    {
                        foreach (var item in _spec.Plan)
                        {
                            await preparation.PrepareAsync(_spec, item, _stop.Token).ConfigureAwait(false);
                        }
                    }
                    if (!IsDry())
                    {
                        await _recorder.StartAsync(_spec, _stop.Token).ConfigureAwait(false);
                    }

                    if (IsSync())
                    {
                        await RunSyncAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        await RunPassAsync(_stop.Token).ConfigureAwait(false);
                        if (!_stop.IsCancellationRequested && _tasks.All(t => t.Status == "done") && IsIncrementalStrategy())
                        {
                            // 1회성 증분은 실행할 때마다 삭제 표시 매핑만 대조한다(행 수 차이는 실행 후 검증 P01이 보여 준다).
                            await ReconcileAsync(false, _stop.Token).ConfigureAwait(false);
                        }
                        if (_stop.IsCancellationRequested)
                        {
                            Finish("stopped", null);
                        }
                        else if (_tasks.Any(t => t.Status == "failed"))
                        {
                            Finish("failed", null);
                        }
                        else
                        {
                            var snapshot = Snapshot();
                            Log("DONE", "전체 " + _tasks.Count + "개 작업 · " + Number(snapshot.Tasks.Sum(t => t.Written)) +
                                "행 · " + Duration(snapshot.Elapsed) + (IsDry() ? " · Dry Run(대상 변경 없음)" : ""));
                            Finish("done", null);
                        }
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested || hostCancel.IsCancellationRequested)
                {
                    Finish("stopped", null);
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        var active = _tasks.FirstOrDefault(t => t.Status == "run" || t.Status == "paused");
                        if (active != null) active.Status = "failed";
                    }
                    Log("ERROR", SafeError(ex));
                    Finish("failed", SafeError(ex));
                }
            }
        }

        /// <summary>계획한 작업을 순서대로 한 번 돈다. 하나가 실패·중지되면 뒤는 건너뛴다.</summary>
        private async Task RunPassAsync(CancellationToken cancellationToken)
        {
            for (var i = 0; i < _tasks.Count; i++)
            {
                if (_stop.IsCancellationRequested)
                {
                    break;
                }

                var task = _tasks[i];
                await RunItemAsync(task, cancellationToken).ConfigureAwait(false);
                if (task.Status == "failed" || task.Status == "stopped")
                {
                    for (var j = i + 1; j < _tasks.Count; j++)
                    {
                        _tasks[j].Status = "skipped";
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// SYNC(변경동기화): [한 번 돌기 → 워터마크 올리기 → 대기]를 중지·최대 실행 시간·연속 오류 상한까지 반복한다.
        /// 끝은 stopped(사용자) · done(최대 실행 시간) · failed(데이터 오류 또는 일시 오류 연속 상한)뿐이다.
        /// </summary>
        private async Task RunSyncAsync()
        {
            var interval = PollInterval();
            var maxHours = MaxRunHours();
            while (!_stop.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _cycle++;
                    _syncPhase = "cycle";
                    _nextCycleAt = null;
                    foreach (var task in _tasks)
                    {
                        task.ResetForCycle();
                    }
                }
                if (_cycle > 1)
                {
                    Log("CYCLE", "주기 " + _cycle + " 시작");
                }

                await RunPassAsync(_stop.Token).ConfigureAwait(false);
                if (_stop.IsCancellationRequested)
                {
                    break;
                }

                // 이번 주기에 커밋한 마지막 키가 다음 주기의 워터마크. 커밋이 없었으면(변경 없음·실패) 지난 워터마크 그대로.
                foreach (var task in _tasks)
                {
                    task.Item = NextCycleItem(task.Item, task.Checkpoint ?? task.Item.ResumeFrom);
                }

                var failed = _tasks.FirstOrDefault(t => t.Status == "failed");
                if (failed != null)
                {
                    var transient = failed.LastError != null && IsTransient(failed.LastError);
                    int failures;
                    lock (_gate) { failures = ++_consecutiveFailures; }
                    if (!transient || failures > MaxConsecutiveFailures)
                    {
                        Log("ERROR", transient
                            ? "일시 오류가 " + MaxConsecutiveFailures + "번 연속 — 동기화를 멈춤 · 워터마크는 마지막 커밋 값 그대로이니 원인을 고친 뒤 다시 시작"
                            : "데이터·설정 오류는 다시 돌려도 같음 — 동기화를 멈춤");
                        Finish("failed", null);
                        return;
                    }

                    var backoff = TimeSpan.FromTicks(Math.Min(interval.Ticks * (1L << (failures - 1)), TimeSpan.FromMinutes(30).Ticks));
                    Log("WARN", "일시 오류 · " + Duration(backoff.TotalSeconds) + " 뒤 다시 시도 " + failures + "/" + MaxConsecutiveFailures + " · 워터마크 유지");
                    await WaitBetweenCyclesAsync(backoff).ConfigureAwait(false);
                    continue;
                }

                long cycleWritten, cycleInserted, cycleUpdated, cycleRejected;
                lock (_gate)
                {
                    _consecutiveFailures = 0;
                    _lastCycleAt = _clock.Now;
                    cycleWritten = _tasks.Sum(t => t.Written);
                    cycleInserted = _tasks.Sum(t => t.Inserted);
                    cycleUpdated = _tasks.Sum(t => t.Updated);
                    cycleRejected = _tasks.Sum(t => t.Rejected);
                    _syncWritten += cycleWritten;
                    _syncInserted += cycleInserted;
                    _syncUpdated += cycleUpdated;
                    _syncRejected += cycleRejected;
                }
                Log("CYCLE", "주기 " + _cycle + " 완료 · 새 " + Number(cycleWritten) + "행 (삽입 " + Number(cycleInserted) + " · 갱신 " + Number(cycleUpdated) +
                    (cycleRejected > 0 ? " · 거부 " + Number(cycleRejected) : "") + ") · 누적 " + Number(_syncWritten) + "행");

                if (_lastReconcileAt == null || (_clock.Now - _lastReconcileAt.Value).TotalMinutes >= ReconcileIntervalMinutes())
                {
                    // 동기화는 삭제를 따르지 않는 매핑도 행 수를 세어 "원본에 없는 대상 행"을 숨기지 않는다.
                    await ReconcileAsync(true, _stop.Token).ConfigureAwait(false);
                    if (_stop.IsCancellationRequested)
                    {
                        break;
                    }
                }

                if (maxHours > 0 && (_clock.Now - _startedAt).TotalHours >= maxHours)
                {
                    Log("DONE", "최대 실행 시간 " + maxHours + "시간 도달 — 동기화 종료 · 주기 " + _cycle + "회 · 누적 " + Number(_syncWritten) + "행" +
                        " · 다시 시작하면 워터마크 다음부터 이어 감");
                    Finish("done", null);
                    return;
                }

                await WaitBetweenCyclesAsync(interval).ConfigureAwait(false);
            }

            Finish("stopped", null);
        }

        /// <summary>
        /// 지연 창 상한. 증분·동기화 전략이고 체크포인트 열이 날짜·시각일 때만: 원본 시각 − 지연 창. 워터마크보다 뒤로 가지 않는다
        /// (시계가 어긋나거나 지연 창이 주기보다 길면 이번엔 읽을 것이 없고 워터마크는 그대로). 원본 시각을 못 읽으면 창 없이 돈다(한 번 경고).
        /// </summary>
        private async Task<string> WindowUpperAsync(PlanItem item, CancellationToken cancellationToken)
        {
            var mode = _spec.Job.Strategy != null ? _spec.Job.Strategy.Mode : null;
            if (!IsSync() && !string.Equals(mode, ExecutionModes.Incremental, StringComparison.Ordinal) && !string.Equals(mode, ExecutionModes.Cdc, StringComparison.Ordinal))
            {
                return null;
            }
            var column = item.SourceMetadata != null && !string.IsNullOrEmpty(item.Mapping.CheckpointColumn) ? item.SourceMetadata.FindColumn(item.Mapping.CheckpointColumn) : null;
            var type = column != null && !string.IsNullOrEmpty(column.Type) ? OracleType.Parse(column.Type) : null;
            if (type == null || !type.IsDate)
            {
                return null;
            }
            var clock = _source as ISourceClock;
            if (clock == null)
            {
                if (!_windowWarned)
                {
                    _windowWarned = true;
                    Log("WARN", "원본 시각을 읽을 수 없어 지연 창 없이 읽음 — 수정시각보다 커밋이 늦은 행을 놓칠 수 있음");
                }
                return null;
            }
            var now = await clock.NowAsync(cancellationToken).ConfigureAwait(false);
            var upper = now.AddSeconds(-LagSeconds());
            if (item.ResumeFrom != null && DateTime.TryParse(item.ResumeFrom, CultureInfo.InvariantCulture, DateTimeStyles.None, out var watermark) && upper < watermark)
            {
                upper = watermark;
            }
            return CheckpointValue(new object[] { upper }, 0);
        }

        /// <summary>
        /// 삭제 대조. MARK 매핑: 원본·대상 키를 비교해 원본에 없는 대상 행을 표시하고, 원본에 다시 나타난 표시 행은 표시를 지운다
        /// (승인됐고 Dry Run이 아니고 상한 안일 때만). NONE 매핑(<paramref name="countUnmarked"/>일 때): 원본·대상 행 수만 센다.
        /// 대조가 실패해도 이관·동기화는 계속한다 — 삭제 반영은 부가 단계이고, 다음 대조가 다시 맞춘다.
        /// </summary>
        private async Task ReconcileAsync(bool countUnmarked, CancellationToken cancellationToken)
        {
            _lastReconcileAt = _clock.Now;
            foreach (var task in _tasks)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var item = task.Item;
                var mark = DeleteModes.IsMark(item.Mapping.DeleteMode);
                if (!mark && !countUnmarked)
                {
                    continue;
                }

                if (item.Mapping.IsSql)
                {
                    // SQL 원본은 테이블 이름이 없어 키·행 수를 따로 셀 수 없다(삭제 표시는 검증에서 막힘). 따라가지 않는 SQL 매핑은 실행 후 검증 P01이 센다.
                    continue;
                }

                if (Reconciler == null)
                {
                    if (mark && !_reconcileWarned)
                    {
                        _reconcileWarned = true;
                        Log("WARN", "삭제 대조를 할 수 없는 실행 환경 — 원본에서 지운 행을 표시하지 않음");
                    }
                    continue;
                }

                try
                {
                    var snapshot = mark
                        ? await ReconcileMarkAsync(task, cancellationToken).ConfigureAwait(false)
                        : await ReconcileCountAsync(task, cancellationToken).ConfigureAwait(false);
                    lock (_gate)
                    {
                        snapshot.TotalMarked = (task.Reconcile != null ? task.Reconcile.TotalMarked : 0) + snapshot.Marked;
                        task.Reconcile = snapshot;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        task.Reconcile = new ReconcileSnapshot
                        {
                            At = _clock.Now, Mode = item.Mapping.DeleteMode, Error = SafeError(ex),
                            TotalMarked = task.Reconcile != null ? task.Reconcile.TotalMarked : 0
                        };
                    }
                    Log("WARN", item.Label + ": 삭제 대조 실패 — 표시하지 않음, 다음 대조에서 다시 · " + SafeError(ex));
                }
            }
            EmitSnapshot(true);
        }

        private async Task<ReconcileSnapshot> ReconcileCountAsync(TaskState task, CancellationToken cancellationToken)
        {
            var source = await Reconciler.CountAsync(task.Item, false, cancellationToken).ConfigureAwait(false);
            var target = await Reconciler.CountAsync(task.Item, true, cancellationToken).ConfigureAwait(false);
            if (target > source)
            {
                Log("INFO", task.Item.Label + ": 대상이 원본보다 " + Number(target - source) + "행 많음 (원본 " + Number(source) + " · 대상 " + Number(target) +
                    ") — 삭제를 따라가지 않는 매핑이라 원본에서 지운 행이 남아 있을 수 있음");
            }
            return new ReconcileSnapshot { At = _clock.Now, Mode = DeleteModes.None, SourceRows = source, TargetRows = target };
        }

        private async Task<ReconcileSnapshot> ReconcileMarkAsync(TaskState task, CancellationToken cancellationToken)
        {
            var item = task.Item;
            KeyDiffResult diff;
            using (var source = await Reconciler.OpenKeysAsync(item, false, cancellationToken).ConfigureAwait(false))
            using (var target = await Reconciler.OpenKeysAsync(item, true, cancellationToken).ConfigureAwait(false))
            {
                diff = await KeyDiff.RunAsync(source, target, MaxReconcileCollect, cancellationToken).ConfigureAwait(false);
            }

            var snapshot = new ReconcileSnapshot
            {
                At = _clock.Now, Mode = DeleteModes.Mark, SourceRows = diff.SourceKeys,
                TargetRows = diff.TargetKeys - diff.AlreadyMarked - diff.UnmarkCount,
                MarkCandidates = diff.MarkCount, UnmarkCandidates = diff.UnmarkCount,
                AlreadyMarked = diff.AlreadyMarked, SourceOnly = diff.SourceOnly,
                Approved = !string.IsNullOrEmpty(item.Mapping.DeleteApprovedAt),
                Samples = diff.Mark.Take(5).Select(k => (k.Text ?? "").Replace(ReconcileKeySeparator, ", ")).ToList()
            };
            var cap = Math.Max(MinDeleteCap, (long)Math.Ceiling(Math.Max(0, DeleteMaxRatio()) * Math.Max(0, snapshot.TargetRows)));
            if (diff.MarkCount > 0 && diff.SourceKeys == 0)
            {
                snapshot.Blocked = "원본 키가 0개 — 접속·스키마·조건을 확인하세요(전부 지워진 것으로 보고 표시하지 않음)";
            }
            else if (diff.MarkCount > cap)
            {
                snapshot.Blocked = "표시할 행 " + Number(diff.MarkCount) + "이 상한 " + Number(cap) + "(살아 있는 대상의 " +
                    (DeleteMaxRatio() * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%) 초과 — 조건·다중 매핑·원본 접속을 확인하세요";
            }
            else if (diff.Truncated)
            {
                snapshot.Blocked = "표시 후보가 " + Number(MaxReconcileCollect) + "행을 넘어 한 번에 처리하지 않음";
            }

            var apply = !IsDry() && snapshot.Approved && snapshot.Blocked == null;
            if (apply)
            {
                if (diff.Mark.Count > 0) snapshot.Marked = await Reconciler.SetMarkAsync(item, diff.Mark, true, cancellationToken).ConfigureAwait(false);
                if (diff.Unmark.Count > 0) snapshot.Unmarked = await Reconciler.SetMarkAsync(item, diff.Unmark, false, cancellationToken).ConfigureAwait(false);
            }

            var head = item.Label + ": 삭제 대조 · 원본 " + Number(snapshot.SourceRows) + " · 대상 " + Number(snapshot.TargetRows) + " · ";
            if (snapshot.Blocked != null)
            {
                Log("WARN", head + "원본에 없는 대상 " + Number(diff.MarkCount) + "행을 표시하지 않음 — " + snapshot.Blocked);
            }
            else if (apply)
            {
                if (snapshot.Marked > 0 || snapshot.Unmarked > 0)
                {
                    Log("DELETE", head + "삭제 표시 " + Number(snapshot.Marked) + "행" + (snapshot.Unmarked > 0 ? " · 다시 나타나 표시 해제 " + Number(snapshot.Unmarked) + "행" : "") +
                        " (" + item.Mapping.MarkColumn + ")");
                }
            }
            else if (diff.MarkCount > 0 || diff.UnmarkCount > 0)
            {
                Log(IsDry() ? "DRY" : "WARN", head + (IsDry() ? "표시 예정 " : "승인 전이라 표시하지 않음 · 대기 ") + Number(diff.MarkCount) + "행" +
                    (diff.UnmarkCount > 0 ? " · 표시 해제 " + Number(diff.UnmarkCount) + "행" : "") +
                    (snapshot.Samples.Count > 0 ? " (예: " + string.Join(" / ", snapshot.Samples) + ")" : "") +
                    (snapshot.Approved ? "" : " — 실행 화면에서 승인하면 다음 실행부터 표시"));
            }
            return snapshot;
        }

        /// <summary>여러 열 키의 정규화 문자열 구분자(CHR(31)). 표본을 보여 줄 때 ", "로 바꾼다.</summary>
        internal const string ReconcileKeySeparator = "\u001f";

        /// <summary>다음 주기의 계획: 같은 매핑, 워터마크 다음부터, 범위 하나. 읽을 행 수는 미리 세지 않는다(주기마다 COUNT를 돌리지 않기 위해).</summary>
        private static PlanItem NextCycleItem(PlanItem item, string watermark)
        {
            return new PlanItem
            {
                Key = item.Key, Mapping = item.Mapping, Label = item.Label,
                ScopeTotal = 0, ResumeFrom = watermark, BaseRows = 0,
                Ranges = new List<KeyRange>(),
                TargetRowsBefore = item.TargetRowsBefore, ErrorTable = item.ErrorTable, Notes = null,
                SourceMetadata = item.SourceMetadata, TargetMetadata = item.TargetMetadata, WriteColumns = item.WriteColumns
            };
        }

        /// <summary>1초 조각으로 기다리며 중지·일시 정지에 바로 반응한다(긴 주기 동안 일시 정지 요청이 묻히지 않게).</summary>
        private async Task WaitBetweenCyclesAsync(TimeSpan span)
        {
            DateTime until;
            lock (_gate)
            {
                _syncPhase = "waiting";
                until = _clock.Now + span;
                _nextCycleAt = until;
            }
            EmitSnapshot(true);
            while (!_stop.IsCancellationRequested)
            {
                var remaining = until - _clock.Now;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var slice = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
                await CycleDelay(slice, _stop.Token).ConfigureAwait(false);
                PauseBetweenCycles();
            }
        }

        private void PauseBetweenCycles()
        {
            lock (_gate)
            {
                if (!_pauseRequested)
                {
                    return;
                }
                _state = "paused";
            }
            Log("PAUSE", "주기 사이에서 일시 정지");
            EmitSnapshot(true);
            lock (_gate)
            {
                while (_pauseRequested && !_stop.IsCancellationRequested)
                {
                    Monitor.Wait(_gate, 100);
                }
            }
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (_state != "running")
                {
                    return;
                }

                _pauseRequested = true;
                _state = "pausing";
            }
            EmitSnapshot(true);
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (_state != "paused" && _state != "pausing")
                {
                    return;
                }

                _pauseRequested = false;
                _pausedWorkers = 0;
                _state = "running";
                foreach (var task in _tasks.Where(t => t.Status == "paused"))
                {
                    task.Status = "run";
                }

                Monitor.PulseAll(_gate);
            }
            Log("RESUME", "이어서 실행");
            EmitSnapshot(true);
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_state != "running" && _state != "pausing" && _state != "paused")
                {
                    return;
                }

                _state = "stopped";
                _pauseRequested = false;
                Monitor.PulseAll(_gate);
            }
            _stop.Cancel();
            _ = DisposeStoppedSessionsAsync();
        }

        public RunSnapshot Snapshot()
        {
            lock (_gate)
            {
                var elapsed = _startedAt == default ? 0 : Math.Max(0, (_clock.Now - _startedAt).TotalSeconds);
                var snapshot = new RunSnapshot
                {
                    RunId = _spec.RunId,
                    State = _state,
                    Mode = _spec.RunMode,
                    Elapsed = elapsed,
                    Rate = _rate,
                    Pipeline = new PipelineSnapshot
                    {
                        BufferPercent = _tasks.Count == 0 ? 0 : _tasks.Max(t => t.Buffered) * 100.0 / ChannelCapacity,
                        WritingWorkers = _tasks.Sum(t => t.WritingWorkers),
                        ReadRowsPerSecond = _rate,
                        CommitsPerSecond = elapsed > 0 ? _tasks.Sum(t => t.Commits) / elapsed : 0,
                        MaxBufferedBatches = _tasks.Count == 0 ? 0 : _tasks.SelectMany(t => t.Ranges).Select(r => r.MaxBuffered).DefaultIfEmpty(0).Max()
                    }
                };
                foreach (var task in _tasks)
                {
                    snapshot.Tasks.Add(task.Copy(_clock.Now));
                }

                snapshot.Totals.Total = _tasks.Sum(t => t.Item.ScopeTotal);
                snapshot.Totals.Done = _tasks.Sum(t => t.Item.BaseRows + t.Written);
                snapshot.Totals.Pct = snapshot.Totals.Total == 0 ? 100 : snapshot.Totals.Done * 100.0 / snapshot.Totals.Total;
                snapshot.Totals.EtaSeconds = _rate > 0 ? Math.Max(0, snapshot.Totals.Total - snapshot.Totals.Done) / _rate : (double?)null;
                if (IsSync())
                {
                    var inCycle = _syncPhase == "cycle";
                    snapshot.Sync = new SyncSnapshot
                    {
                        Cycle = _cycle,
                        Phase = _syncPhase,
                        LastCycleAt = _lastCycleAt,
                        NextCycleAt = _nextCycleAt,
                        IntervalSeconds = (int)PollInterval().TotalSeconds,
                        MaxRunHours = MaxRunHours(),
                        Written = _syncWritten + (inCycle ? _tasks.Sum(t => t.Written) : 0),
                        Inserted = _syncInserted + (inCycle ? _tasks.Sum(t => t.Inserted) : 0),
                        Updated = _syncUpdated + (inCycle ? _tasks.Sum(t => t.Updated) : 0),
                        Rejected = _syncRejected + (inCycle ? _tasks.Sum(t => t.Rejected) : 0),
                        ConsecutiveFailures = _consecutiveFailures
                    };
                }
                return snapshot;
            }
        }

        private async Task RunItemAsync(TaskState task, CancellationToken cancellationToken)
        {
            task.Status = "run";
            task.StartedAt = _clock.Now;
            var startLines = new List<string>
            {
                task.Item.Label + "  (" + ModeLabel(task.Item.Mapping.Mode) + (task.Item.Mapping.IsSql ? " · SQL 매핑" : "") + ")"
            };
            if (task.Item.BaseRows > 0)
            {
                startLines.Add("체크포인트에서 재개: " + task.Item.Mapping.CheckpointColumn + " > " + task.Item.ResumeFrom +
                    " · 남은 " + Number(Math.Max(0, task.Item.ScopeTotal - task.Item.BaseRows)) + "행");
            }
            else if (task.Item.ResumeFrom != null)
            {
                startLines.Add("증분: " + task.Item.Mapping.CheckpointColumn + " > " + task.Item.ResumeFrom + " (워터마크)" +
                    (task.Item.ScopeTotal > 0 ? " · 새 " + Number(task.Item.ScopeTotal) + "행" : ""));
            }
            var upper = await WindowUpperAsync(task.Item, cancellationToken).ConfigureAwait(false);
            if (upper != null)
            {
                startLines.Add("지연 창: " + task.Item.Mapping.CheckpointColumn + " <= " + upper + " (원본 시각 − " + LagSeconds() + "초) — 그 뒤 행은 다음 주기에");
            }
            else if (!string.IsNullOrEmpty(task.Item.Mapping.CheckpointColumn))
            {
                startLines.Add("범위: " + task.Item.Mapping.CheckpointColumn + " 순서로 " + Number(task.Item.ScopeTotal) + "행");
            }
            else
            {
                startLines.Add("범위: " + Number(task.Item.ScopeTotal) + "행 (체크포인트 없음 — 중단하면 처음부터)");
            }
            Log(task.Item.BaseRows > 0 ? "RESUME" : "START", string.Join("\n", startLines));
            if (!string.IsNullOrEmpty(task.Item.Notes))
            {
                Log("WARN", task.Item.Notes);
            }
            if (IsDry())
            {
                Log("DRY", "쓰기 없음 — 원본 읽기·변환·매핑만 하고 쓰기 문은 만들기만 함");
            }
            else if (string.Equals(task.Item.Mapping.Mode, WriteModes.Merge, StringComparison.Ordinal))
            {
                Log("INFO", "MERGE enabled · 대상 기존 " + Number(task.Item.TargetRowsBefore) + "행은 갱신 예상");
            }
            if (!IsDry() && _cycle <= 1)
            {
                // MIG_RUN_TASK는 (RUN_ID, TASK_KEY)가 키라 동기화에서는 첫 주기에만 넣고, 끝날 때마다 갱신만 한다.
                await _recorder.TaskStartedAsync(_spec, task.Item, cancellationToken).ConfigureAwait(false);
            }

            if (!IsDry() && string.Equals(task.Item.Mapping.Mode, WriteModes.TruncateInsert, StringComparison.Ordinal) && task.Item.ResumeFrom != null && task.Item.BaseRows == 0)
            {
                // 워터마크 이후만 읽는 증분에서 TRUNCATE를 하면 대상의 기존 행이 사라지고 새 행만 남는다 — 검증(증분 기준)이 막지만 엔진도 지킨다.
                task.Status = "failed";
                task.EndedAt = _clock.Now;
                Log("ERROR", task.Item.Label + ": 증분 이관(워터마크 " + task.Item.ResumeFrom + ")에서는 TRUNCATE + INSERT를 쓸 수 없음 — INSERT+UPDATE로 바꾸거나 전체 이관으로 실행");
                await _recorder.TaskEndedAsync(_spec, task.Copy(_clock.Now), CancellationToken.None).ConfigureAwait(false);
                EmitSnapshot(true);
                return;
            }

            if (!IsDry() && string.Equals(task.Item.Mapping.Mode, WriteModes.TruncateInsert, StringComparison.Ordinal) && task.Item.BaseRows == 0)
            {
                using (var session = await OpenTargetAsync(cancellationToken).ConfigureAwait(false))
                {
                    await session.TruncateAsync(task.Item, cancellationToken).ConfigureAwait(false);
                }
                Log("INFO", "TRUNCATE TABLE " + _spec.Target.Schema + "." + task.Item.Mapping.Target + " 완료");
            }

            var ranges = task.Item.Ranges.Count == 0
                ? new List<KeyRange> { new KeyRange { Rows = Math.Max(0, task.Item.ScopeTotal - task.Item.BaseRows), Last = task.Item.ResumeFrom } }
                : task.Item.Ranges;
            foreach (var range in ranges)
            {
                range.Upper = upper;
            }
            task.Ranges = ranges.Select((r, index) => new RangeState(r,
                ranges.Count == 1 && index == 0 ? task.Item.BaseRows : 0)).ToList();
            lock (_gate) { _activeWorkers = ranges.Count; }
            var workers = new List<Task>();
            using (var itemCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                for (var i = 0; i < ranges.Count; i++)
                {
                    var workerNumber = i + 1;
                    workers.Add(RunItemWorkerAsync(task, task.Ranges[i], workerNumber, ranges.Count, itemCancel));
                }

                try
                {
                    await Task.WhenAll(workers).ConfigureAwait(false);
                    if (_stop.IsCancellationRequested)
                    {
                        task.Status = "stopped";
                        await MarkStoppedAsync(task, CancellationToken.None).ConfigureAwait(false);
                        if (!IsDry())
                        {
                            await _recorder.TaskEndedAsync(_spec, task.Copy(_clock.Now), CancellationToken.None).ConfigureAwait(false);
                        }
                        return;
                    }
                    task.Status = "done";
                    task.EndedAt = _clock.Now;
                    if (upper != null && !IsDry())
                    {
                        // 상한까지 다 읽었으니 워터마크는 마지막 행 값이 아니라 상한이다 — 변경이 없던 주기에도 워터마크가 앞으로 간다.
                        lock (_gate)
                        {
                            foreach (var range in task.Ranges)
                            {
                                range.Last = upper;
                            }
                            task.Checkpoint = MinimumCheckpoint(task.Ranges);
                        }
                    }
                    await FinalizeCheckpointsAsync(task, "done", cancellationToken).ConfigureAwait(false);
                    Log("DONE", task.Item.Label + "  (" + Duration((task.EndedAt - task.StartedAt).TotalSeconds) + ")\n" +
                        (IsDry() ? "예상 " : "") + "Inserted : " + Number(task.Inserted) + "\n" +
                        (IsDry() ? "예상 " : "") + "Updated  : " + Number(task.Updated) + "\n" +
                        (IsDry() ? "예상 " : "") + "Rejected : " + Number(task.Rejected) +
                        (task.Rejected > 0 && !IsDry() && !string.IsNullOrEmpty(task.Item.ErrorTable) ? " → " + _spec.Target.Schema + "." + task.Item.ErrorTable : ""));
                    if (!IsDry())
                    {
                        await _recorder.TaskEndedAsync(_spec, task.Copy(_clock.Now), cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    task.Status = "stopped";
                    await MarkStoppedAsync(task, CancellationToken.None).ConfigureAwait(false);
                    if (!IsDry())
                    {
                        await _recorder.TaskEndedAsync(_spec, task.Copy(_clock.Now), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    task.Status = "failed";
                    task.LastError = ex;
                    Log("ERROR", SafeError(ex) + (task.Checkpoint == null ? "" : "\n체크포인트: " + task.Item.Mapping.CheckpointColumn + " = " + task.Checkpoint + " (재개하면 여기부터)"));
                    if (!IsDry())
                    {
                        await _recorder.TaskEndedAsync(_spec, task.Copy(_clock.Now), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    lock (_gate) { _activeWorkers = 0; _pausedWorkers = 0; }
                    EmitSnapshot(true);
                }
            }
        }

        private async Task RunItemWorkerAsync(TaskState task, RangeState range, int workerNumber, int workerCount,
            CancellationTokenSource itemCancel)
        {
            try
            {
                await RunWorkerAsync(task, range, workerNumber, workerCount, itemCancel.Token).ConfigureAwait(false);
            }
            catch
            {
                itemCancel.Cancel();
                throw;
            }
        }

        private async Task RunWorkerAsync(TaskState task, RangeState range, int workerNumber, int workerCount, CancellationToken cancellationToken)
        {
            var retry = 0;
            var last = range.Last ?? task.Item.ResumeFrom;
            while (true)
            {
                try
                {
                    await RunWorkerAttemptAsync(task, range, workerNumber, workerCount, last, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!IsTransient(ex) || !string.Equals(ErrorPolicy(), ErrorPolicies.Retry, StringComparison.Ordinal) || retry >= 3)
                    {
                        if (string.Equals(ErrorPolicy(), ErrorPolicies.Retry, StringComparison.Ordinal) && !IsTransient(ex))
                        {
                            throw new InvalidOperationException("데이터 오류는 재시도로 해결되지 않음 · " + SafeError(ex), ex);
                        }
                        throw;
                    }

                    retry++;
                    if (string.Equals(_spec.CheckpointStore, "TARGET", StringComparison.Ordinal))
                    {
                        var taskKey = workerCount == 1 ? task.Item.Key : task.Item.Key + "#" + workerNumber.ToString(CultureInfo.InvariantCulture);
                        var stored = await _store.GetAsync(_spec.Job.JobName, taskKey, cancellationToken).ConfigureAwait(false);
                        if (stored != null && stored.Value != null)
                        {
                            var recovered = Math.Max(0, stored.RowsDone - range.Written);
                            lock (_gate)
                            {
                                range.Written = Math.Max(range.Written, stored.RowsDone);
                                task.Written += recovered;
                                task.Inserted += recovered;
                            }
                            range.Last = stored.Value;
                        }
                    }
                    last = range.Last ?? last;
                    if (ex.Data.Contains("RolledRows"))
                    {
                        var warning = SafeError(ex) + " · 진행 중 배치 " + Number(Convert.ToInt64(ex.Data["RolledRows"], CultureInfo.InvariantCulture)) +
                            "행 롤백 · 다시 연결해 재시도 " + retry + "/3";
                        Log("WARN", warning);
                    }
                    else
                    {
                        Log("WARN", SafeError(ex) + " · 다시 연결해 재시도 " + retry + "/3");
                    }
                    var delay = retry <= _spec.RetryDelays.Count ? _spec.RetryDelays[retry - 1] : 0;
                    if (delay > 0)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task RunWorkerAttemptAsync(TaskState task, RangeState range, int workerNumber, int workerCount,
            string last, CancellationToken cancellationToken)
        {
            var channel = Channel.CreateBounded<List<object[]>>(new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
            using (var attemptCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var reader = await _source.OpenAsync(task.Item, range.Range, last, FetchSize(), attemptCancel.Token).ConfigureAwait(false))
            using (var session = await OpenTargetAsync(attemptCancel.Token).ConfigureAwait(false))
            {
                long attemptRead = 0;
                long attemptCommitted = 0;
                Exception producerError = null;
                var producer = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            var rows = await reader.ReadAsync(FetchSize(), attemptCancel.Token).ConfigureAwait(false);
                            if (rows.Count == 0)
                            {
                                break;
                            }
                            lock (_gate) { task.Read += rows.Count; range.Read += rows.Count; attemptRead += rows.Count; }
                            await channel.Writer.WriteAsync(rows, attemptCancel.Token).ConfigureAwait(false);
                            lock (_gate)
                            {
                                range.Buffered = channel.Reader.Count;
                                range.MaxBuffered = Math.Max(range.MaxBuffered, range.Buffered);
                                task.Buffered = task.Ranges.Sum(r => r.Buffered);
                            }
                            EmitSnapshot(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        producerError = ex;
                    }
                    finally
                    {
                        channel.Writer.TryComplete(producerError);
                    }
                }, CancellationToken.None);

                var pending = new List<object[]>();
                try
                {
                    await foreach (var rows in channel.Reader.ReadAllAsync(attemptCancel.Token).ConfigureAwait(false))
                    {
                        lock (_gate)
                        {
                            range.Buffered = channel.Reader.Count;
                            task.Buffered = task.Ranges.Sum(r => r.Buffered);
                        }
                        pending.AddRange(rows);
                        while (pending.Count >= CommitSize())
                        {
                            await CommitAsync(task, range, workerNumber, workerCount, reader, session,
                                pending.GetRange(0, CommitSize()), attemptCancel.Token).ConfigureAwait(false);
                            attemptCommitted += CommitSize();
                            pending.RemoveRange(0, CommitSize());
                        }
                    }
                    if (pending.Count > 0)
                    {
                        await CommitAsync(task, range, workerNumber, workerCount, reader, session, pending, attemptCancel.Token).ConfigureAwait(false);
                        attemptCommitted += pending.Count;
                        pending.Clear();
                    }
                    await producer.ConfigureAwait(false);
                    range.Status = "done";
                }
                catch (Exception ex)
                {
                    attemptCancel.Cancel();
                    try { await session.RollbackAsync().ConfigureAwait(false); } catch { }
                    try { await producer.ConfigureAwait(false); } catch { }
                    lock (_gate)
                    {
                        var uncommitted = Math.Max(0, attemptRead - attemptCommitted);
                        range.RolledBack = uncommitted;
                        ex.Data["RolledRows"] = uncommitted;
                        task.Read -= uncommitted;
                        range.Read -= uncommitted;
                        task.Buffered = 0;
                        range.Buffered = 0;
                    }
                    throw;
                }
            }
        }

        private async Task CommitAsync(TaskState task, RangeState range, int workerNumber, int workerCount,
            ISourceReader reader, ITargetSession session, List<object[]> rows, CancellationToken cancellationToken)
        {
            if (rows.Count == 0)
            {
                return;
            }
            lock (_gate) { task.WritingWorkers++; }
            try
            {
                WriteResult result;
                if (IsDry())
                {
                    result = await DryResultAsync(task.Item, session, rows, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var item = task.Item;
                    // 로컬 파일 저장소는 커밋 뒤에 체크포인트를 쓰므로 그 사이에 죽으면 한 배치가 다시 온다 — 재개든 증분(워터마크)이든
                    // 저장된 키 다음부터 읽는 첫 배치는 MERGE로 써서 중복 키(ORA-00001)를 흡수한다.
                    if (string.Equals(_spec.CheckpointStore, "LOCAL", StringComparison.Ordinal) &&
                        task.Item.ResumeFrom != null && task.Commits == 0 &&
                        string.Equals(item.Mapping.Mode, WriteModes.InsertOnly, StringComparison.Ordinal))
                    {
                        item = ResumeMergeItem(item);
                    }
                    result = await session.WriteBatchAsync(item, item.WriteColumns, rows, cancellationToken).ConfigureAwait(false);
                    if (result.Rejected > 0 && !string.Equals(ErrorPolicy(), ErrorPolicies.Continue, StringComparison.Ordinal))
                    {
                        await session.RollbackAsync().ConfigureAwait(false);
                        throw new InvalidOperationException("행 오류 " + Number(result.Rejected) + "건 · 진행 중 배치 " + Number(rows.Count) + "행 롤백");
                    }
                }

                var checkpoint = CheckpointValue(rows[rows.Count - 1], reader.CheckpointOrdinal);
                var record = new CheckpointRecord
                {
                    Job = _spec.Job.JobName,
                    TaskKey = workerCount == 1 ? task.Item.Key : task.Item.Key + "#" + workerNumber.ToString(CultureInfo.InvariantCulture),
                    Column = task.Item.Mapping.CheckpointColumn,
                    Value = checkpoint,
                    RangeFrom = range.Range.From,
                    RangeTo = range.Range.To,
                    RowsDone = range.Written + rows.Count,
                    RowsTotal = range.Range.Rows,
                    Status = "running",
                    RunId = _spec.RunId,
                    UpdatedAt = _clock.Now
                };
                var hasCheckpoint = !string.IsNullOrEmpty(record.Column) && record.Value != null;
                if (!IsDry() && hasCheckpoint && string.Equals(_spec.CheckpointStore, "TARGET", StringComparison.Ordinal))
                {
                    await session.SaveCheckpointAsync(record, cancellationToken).ConfigureAwait(false);
                }
                if (!IsDry())
                {
                    await session.CommitAsync().ConfigureAwait(false);
                    if (hasCheckpoint && string.Equals(_spec.CheckpointStore, "LOCAL", StringComparison.Ordinal))
                    {
                        await _store.SaveLocalAsync(record, cancellationToken).ConfigureAwait(false);
                    }
                    if (hasCheckpoint) SafeListener(() => _listener.OnCheckpoint(record));
                }

                lock (_gate)
                {
                    range.RolledBack = 0;
                    task.Written += rows.Count;
                    range.Written += rows.Count;
                    task.Inserted += result.Inserted;
                    task.Updated += result.Updated;
                    task.Rejected += result.Rejected;
                    task.Commits++;
                    range.Last = checkpoint;
                    task.Checkpoint = MinimumCheckpoint(task.Ranges);
                }
                if (result.Rejected > 0)
                {
                    Log("WARN", (IsDry() ? "거부 예상 " : "거부 ") + Number(result.Rejected) + "행" +
                        (!IsDry() && !string.IsNullOrEmpty(task.Item.ErrorTable) ? " → " + _spec.Target.Schema + "." + task.Item.ErrorTable : ""));
                }
                MaybeCommitLog(task);
                EmitSnapshot(false);
                await PauseAtBoundaryAsync(task, range, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) { task.WritingWorkers--; }
            }
        }

        private async Task<WriteResult> DryResultAsync(PlanItem item, ITargetSession session, List<object[]> rows, CancellationToken cancellationToken)
        {
            var rejected = 0;
            for (var r = 0; r < rows.Count; r++)
            {
                for (var c = 0; c < item.WriteColumns.Count && c < rows[r].Length; c++)
                {
                    var value = rows[r][c];
                    var column = item.WriteColumns[c].Column;
                    if ((value == null || value == DBNull.Value) && column != null && !column.Nullable)
                    {
                        rejected++;
                        break;
                    }
                    var text = value as string;
                    var type = column != null ? OracleType.Parse(column.Type) : null;
                    if (text != null && type != null && type.IsChar && type.Length.HasValue && text.Length > type.Length.Value)
                    {
                        rejected++;
                        break;
                    }
                }
            }
            var existing = 0L;
            if (string.Equals(item.Mapping.Mode, WriteModes.Merge, StringComparison.Ordinal) || string.Equals(item.Mapping.Mode, WriteModes.DeleteInsert, StringComparison.Ordinal))
            {
                existing = await session.CountExistingKeysAsync(item, rows, cancellationToken).ConfigureAwait(false);
            }
            existing = Math.Min(existing, rows.Count - rejected);
            return new WriteResult { Written = rows.Count, Rejected = rejected, Updated = (int)existing, Inserted = rows.Count - rejected - (int)existing };
        }

        private void MaybeCommitLog(TaskState task)
        {
            var step = NiceStep(task.Item.ScopeTotal);
            var done = task.Item.BaseRows + task.Written;
            if (done >= task.Item.ScopeTotal || done / step <= task.LastLogged / step)
            {
                return;
            }
            task.LastLogged = done;
            Log(IsDry() ? "DRY" : "INFO", IsDry()
                ? Number(done) + "행 읽기·변환"
                : "Committed " + Number(done) + " rows" + (task.Checkpoint == null ? "" : " · 체크포인트 " + task.Item.Mapping.CheckpointColumn + " = " + task.Checkpoint));
        }

        private Task PauseAtBoundaryAsync(TaskState task, RangeState range, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!_pauseRequested)
                {
                    return Task.CompletedTask;
                }
                _pausedWorkers++;
                range.Status = "paused";
                var workersAtBoundary = task.Ranges.Count(r => r.Status != "done");
                if (_pausedWorkers >= workersAtBoundary)
                {
                    _state = "paused";
                    task.Status = "paused";
                    Log("PAUSE", "커밋 경계에서 일시 정지" + (task.Checkpoint == null ? "" : " · 마지막 커밋 " + task.Item.Mapping.CheckpointColumn + " = " + task.Checkpoint));
                    EmitSnapshot(true);
                }
                while (_pauseRequested && !cancellationToken.IsCancellationRequested)
                {
                    Monitor.Wait(_gate, 100);
                }
                cancellationToken.ThrowIfCancellationRequested();
                range.Status = "run";
                return Task.CompletedTask;
            }
        }

        private async Task MarkStoppedAsync(TaskState task, CancellationToken cancellationToken)
        {
            task.Status = "stopped";
            foreach (var range in task.Ranges)
            {
                if (range.Last == null || IsDry())
                {
                    continue;
                }
                var record = new CheckpointRecord
                {
                    Job = _spec.Job.JobName,
                    TaskKey = task.Ranges.Count == 1 ? task.Item.Key : task.Item.Key + "#" + (task.Ranges.IndexOf(range) + 1).ToString(CultureInfo.InvariantCulture),
                    Column = task.Item.Mapping.CheckpointColumn,
                    Value = range.Last,
                    RangeFrom = range.Range.From,
                    RangeTo = range.Range.To,
                    RowsDone = range.Written,
                    RowsTotal = range.Range.Rows,
                    Status = "stopped",
                    RunId = _spec.RunId,
                    UpdatedAt = _clock.Now
                };
                if (string.Equals(_spec.CheckpointStore, "LOCAL", StringComparison.Ordinal))
                {
                    await _store.SaveLocalAsync(record, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    using (var session = await OpenTargetAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await session.SaveCheckpointAsync(record, cancellationToken).ConfigureAwait(false);
                        await session.CommitAsync().ConfigureAwait(false);
                    }
                }
                SafeListener(() => _listener.OnCheckpoint(record));
            }
            var rolled = task.Ranges.Sum(r => r.RolledBack);
            Log("STOP", "사용자가 중지" + (rolled > 0 ? " · 진행 중 배치 " + Number(rolled) + "행 롤백" : "") +
                (task.Checkpoint == null ? "" : "\n체크포인트 저장: " + task.Item.Mapping.CheckpointColumn + " = " + task.Checkpoint + " — [체크포인트에서 재개]로 이어서 실행"));
        }

        private async Task FinalizeCheckpointsAsync(TaskState task, string status, CancellationToken cancellationToken)
        {
            if (IsDry() || string.IsNullOrEmpty(task.Item.Mapping.CheckpointColumn)) return;
            for (var i = 0; i < task.Ranges.Count; i++)
            {
                var range = task.Ranges[i];
                if (range.Last == null) continue;
                var record = new CheckpointRecord
                {
                    Job = _spec.Job.JobName,
                    TaskKey = task.Ranges.Count == 1 ? task.Item.Key : task.Item.Key + "#" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    Column = task.Item.Mapping.CheckpointColumn,
                    Value = range.Last,
                    RangeFrom = range.Range.From,
                    RangeTo = range.Range.To,
                    RowsDone = range.Written,
                    RowsTotal = range.Range.Rows,
                    Status = status,
                    RunId = _spec.RunId,
                    UpdatedAt = _clock.Now
                };
                if (string.Equals(_spec.CheckpointStore, "LOCAL", StringComparison.Ordinal))
                {
                    await _store.SaveLocalAsync(record, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    using (var session = await OpenTargetAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await session.SaveCheckpointAsync(record, cancellationToken).ConfigureAwait(false);
                        await session.CommitAsync().ConfigureAwait(false);
                    }
                }
                SafeListener(() => _listener.OnCheckpoint(record));
            }
        }

        private void EmitSnapshot(bool force)
        {
            lock (_gate)
            {
                var now = _clock.Now;
                if (!force && (now - _lastSnapshotAt).TotalMilliseconds < 250)
                {
                    return;
                }
                var rows = _tasks.Sum(t => t.Written);
                var seconds = Math.Max(0.001, (now - _lastRateAt).TotalSeconds);
                var instant = Math.Max(0, rows - _lastRateRows) / seconds;
                _rate = _rate == 0 ? instant : _rate * 0.7 + instant * 0.3;
                _lastRateRows = rows;
                _lastRateAt = now;
                _lastSnapshotAt = now;
            }
            SafeListener(() => _listener.OnSnapshot(Snapshot()));
        }

        private void Finish(string state, string message)
        {
            lock (_gate)
            {
                if (_ended)
                {
                    return;
                }
                _state = state;
                _ended = true;
                _rate = 0;
            }
            var snapshot = Snapshot();
            if (!IsDry())
            {
                try { _recorder.EndAsync(_spec, snapshot, message, CancellationToken.None).GetAwaiter().GetResult(); } catch { }
            }
            EmitSnapshot(true);
            SafeListener(() => _listener.OnEnd(Snapshot()));
        }

        private void Log(string tag, string text)
        {
            var entry = new LogEntry { At = _clock.Now, Tag = tag, Text = Redact(text) };
            SafeListener(() => _listener.OnLog(entry));
        }

        private void SafeListener(Action action)
        {
            try { action(); } catch { }
        }

        private string Redact(string text)
        {
            foreach (var password in new[] { _spec.Source?.Connection?.Password, _spec.Target?.Connection?.Password })
            {
                if (!string.IsNullOrEmpty(password))
                {
                    text = text.Replace(password, "***", StringComparison.Ordinal);
                }
            }
            return text;
        }

        /// <summary>
        /// 실행 기록기 감싸개: 대상에 MIG_RUN을 못 쓰면(권한·테이블 없음·일시 장애) WARN 한 줄 남기고 이후 기록을 건너뛴다.
        /// 기록기 예외가 이관 자체를 실패시키던 것(시험 어댑터·로컬 체크포인트 모드)을 막는다.
        /// </summary>
        private sealed class BestEffortRecorder : IRunRecorder
        {
            private readonly MigrationEngine _engine;
            private readonly IRunRecorder _inner;
            private volatile bool _disabled;

            internal BestEffortRecorder(MigrationEngine engine, IRunRecorder inner)
            {
                _engine = engine;
                _inner = inner;
            }

            public Task StartAsync(RunSpec spec, CancellationToken cancellationToken)
            {
                return Guard(() => _inner.StartAsync(spec, cancellationToken), cancellationToken);
            }

            public Task TaskStartedAsync(RunSpec spec, PlanItem item, CancellationToken cancellationToken)
            {
                return Guard(() => _inner.TaskStartedAsync(spec, item, cancellationToken), cancellationToken);
            }

            public Task TaskEndedAsync(RunSpec spec, TaskSnapshot task, CancellationToken cancellationToken)
            {
                return Guard(() => _inner.TaskEndedAsync(spec, task, cancellationToken), cancellationToken);
            }

            public Task EndAsync(RunSpec spec, RunSnapshot snapshot, string message, CancellationToken cancellationToken)
            {
                return Guard(() => _inner.EndAsync(spec, snapshot, message, cancellationToken), cancellationToken);
            }

            private async Task Guard(Func<Task> call, CancellationToken cancellationToken)
            {
                if (_disabled)
                {
                    return;
                }

                try
                {
                    await call().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _disabled = true;
                    _engine.Log("WARN", "실행 기록(MIG_RUN)을 쓰지 못해 이번 실행은 기록 없이 계속합니다: " + _engine.SafeError(ex));
                }
            }
        }

        private string SafeError(Exception ex)
        {
            var text = Redact(OracleErrors.Describe(ex));
            // Oracle 오류는 ORA 코드로 충분하지만, 그 밖의 예외는 "Value cannot be null." 한 줄로는 어디서 났는지 알 수 없다 — 형식과 첫 프레임을 붙인다
            if (ex != null && !(ex is Oracle.ManagedDataAccess.Client.OracleException) && !(ex is OperationCanceledException))
            {
                var inner = ex;
                while (inner.InnerException != null && !(inner is Oracle.ManagedDataAccess.Client.OracleException))
                {
                    inner = inner.InnerException;
                }

                text += " [" + inner.GetType().Name + FirstFrame(inner) + "]";
            }

            return text;
        }

        private static string FirstFrame(Exception ex)
        {
            try
            {
                var trace = new System.Diagnostics.StackTrace(ex, false);
                var frame = trace.GetFrame(0);
                var method = frame != null ? frame.GetMethod() : null;
                return method != null && method.DeclaringType != null ? " @ " + method.DeclaringType.Name + "." + method.Name : "";
            }
            catch
            {
                return "";
            }
        }

        private async Task<ITargetSession> OpenTargetAsync(CancellationToken cancellationToken)
        {
            var inner = await _target.OpenAsync(cancellationToken).ConfigureAwait(false);
            var tracked = new TrackedTargetSession(this, inner);
            lock (_gate) { _openTargetSessions.Add(tracked); }
            return tracked;
        }

        private async Task DisposeStoppedSessionsAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            TrackedTargetSession[] sessions;
            lock (_gate)
            {
                if (_ended || !_stop.IsCancellationRequested) return;
                sessions = _openTargetSessions.ToArray();
            }
            foreach (var session in sessions)
            {
                session.Dispose();
            }
        }

        private void Untrack(TrackedTargetSession session)
        {
            lock (_gate) { _openTargetSessions.Remove(session); }
        }

        private static bool IsTransient(Exception ex)
        {
            var code = OracleErrors.CodeOf(ex);
            return code == "ORA-03113" || code == "ORA-03114" || code == "ORA-12170" || code == "ORA-12571" ||
                   code == "ORA-12537" || code == "ORA-12543" || code == "ORA-01033" ||
                   OracleErrors.Describe(ex).StartsWith("DB 연결이 끊겼습니다.", StringComparison.Ordinal);
        }

        private PlanItem ResumeMergeItem(PlanItem item)
        {
            var mapping = new MappingModel
            {
                Id = item.Mapping.Id, SourceType = item.Mapping.SourceType, Source = item.Mapping.Source,
                Sql = item.Mapping.Sql, Target = item.Mapping.Target, Mode = WriteModes.Merge,
                CheckpointColumn = item.Mapping.CheckpointColumn, Where = item.Mapping.Where,
                MergeKey = new List<string>(item.Mapping.MergeKey), Columns = item.Mapping.Columns
            };
            if (mapping.MergeKey.Count == 0 && !string.IsNullOrEmpty(mapping.CheckpointColumn))
            {
                var mapped = mapping.Columns.FirstOrDefault(c => string.Equals(c.Source, mapping.CheckpointColumn, StringComparison.OrdinalIgnoreCase));
                if (mapped != null) mapping.MergeKey.Add(mapped.Target);
            }
            return new PlanItem
            {
                Key = item.Key, Mapping = mapping, Label = item.Label, ScopeTotal = item.ScopeTotal,
                ResumeFrom = item.ResumeFrom, BaseRows = item.BaseRows, Ranges = item.Ranges,
                TargetRowsBefore = item.TargetRowsBefore, ErrorTable = item.ErrorTable, Notes = item.Notes,
                SourceMetadata = item.SourceMetadata, TargetMetadata = item.TargetMetadata, WriteColumns = item.WriteColumns
            };
        }

        private static string CheckpointValue(object[] row, int ordinal)
        {
            if (ordinal < 0 || ordinal >= row.Length || row[ordinal] == null || row[ordinal] == DBNull.Value)
            {
                return null;
            }
            var value = row[ordinal];
            if (value is DateTime date) return date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string MinimumCheckpoint(IEnumerable<RangeState> ranges)
        {
            var values = ranges.Select(r => r.Last ?? r.Range.From).Where(v => v != null).ToList();
            if (values.Count == 0) return null;
            if (values.All(v => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
            {
                return values.OrderBy(v => decimal.Parse(v, NumberStyles.Number, CultureInfo.InvariantCulture)).First();
            }
            if (values.All(v => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            {
                return values.OrderBy(v => DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.None)).First();
            }
            return values.OrderBy(v => v, StringComparer.Ordinal).First();
        }

        private int FetchSize() { return Math.Max(1, _spec.Job.Strategy.FetchSize); }
        private int CommitSize() { return Math.Max(1, _spec.Job.Strategy.CommitSize); }
        private int Workers() { return Math.Max(1, _spec.Job.Strategy.Workers); }
        private string ErrorPolicy() { return _spec.Job.Strategy.ErrorPolicy; }
        private bool IsDry() { return string.Equals(_spec.RunMode, "DRY", StringComparison.Ordinal); }
        private bool IsSync() { return string.Equals(_spec.RunMode, RunModes.Sync, StringComparison.Ordinal); }
        private TimeSpan PollInterval() { return TimeSpan.FromSeconds(Math.Max(1, _spec.Job.Strategy.PollIntervalSeconds)); }
        private int MaxRunHours() { return Math.Max(0, _spec.Job.Strategy.MaxRunHours); }
        private int LagSeconds() { return Math.Max(0, _spec.Job.Strategy.LagSeconds); }
        private int ReconcileIntervalMinutes() { return Math.Max(1, _spec.Job.Strategy.ReconcileIntervalMinutes); }
        private double DeleteMaxRatio() { return _spec.Job.Strategy.DeleteMaxRatio; }
        private bool IsIncrementalStrategy()
        {
            var mode = _spec.Job.Strategy != null ? _spec.Job.Strategy.Mode : null;
            return string.Equals(mode, ExecutionModes.Incremental, StringComparison.Ordinal) || string.Equals(mode, ExecutionModes.Cdc, StringComparison.Ordinal);
        }
        private string StoreText() { return string.Equals(_spec.CheckpointStore, "LOCAL", StringComparison.Ordinal) ? "로컬 파일" : "대상 DB(" + _spec.ControlPrefix + "CHECKPOINT)"; }
        private static string Number(long n) { return n.ToString("N0", CultureInfo.GetCultureInfo("en-US")); }
        private static string Duration(double seconds) { return TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture); }
        private static string ModeText(string mode) { return mode == "DRY" ? "Dry Run(쓰기 없음)" : mode == "RESUME" ? "체크포인트에서 재개" : mode == RunModes.Sync ? "CDC(변경동기화) · 주기 반복" : "이관 실행"; }
        private static string ModeLabel(string mode) { return mode == WriteModes.Merge ? "INSERT+UPDATE" : mode; }
        private static string PolicyText(string policy) { return policy == ErrorPolicies.Continue ? "계속 + 오류 테이블" : policy == ErrorPolicies.Retry ? "3회 재시도" : "오류 시 중지"; }
        private static long NiceStep(long total)
        {
            if (total <= 8) return 1;
            var target = total / 8.0;
            var power = Math.Pow(10, Math.Floor(Math.Log10(target)));
            return new[] { 1.0, 2.0, 5.0, 10.0 }.Select(m => (long)(m * power)).OrderBy(v => Math.Abs(v - target)).First();
        }

        private sealed class TaskState
        {
            public TaskState(PlanItem item) { Item = item; Status = "wait"; }
            public PlanItem Item;
            public string Status;
            public long Read;
            public long Written;
            public long Inserted;
            public long Updated;
            public long Rejected;
            public int Commits;
            public string Checkpoint;
            public DateTime StartedAt;
            public DateTime EndedAt;
            public int Buffered;
            public int WritingWorkers;
            public long LastLogged;
            public Exception LastError;
            /// <summary>마지막 삭제 대조 결과. 주기가 바뀌어도 유지한다.</summary>
            public ReconcileSnapshot Reconcile;
            public List<RangeState> Ranges = new List<RangeState>();

            /// <summary>다음 주기를 위해 이번 주기 수치를 비운다. Item(워터마크)은 호출자가 바꾼다.</summary>
            public void ResetForCycle()
            {
                Status = "wait";
                Read = 0; Written = 0; Inserted = 0; Updated = 0; Rejected = 0; Commits = 0;
                Checkpoint = null; Buffered = 0; WritingWorkers = 0; LastLogged = 0; LastError = null;
                StartedAt = default(DateTime); EndedAt = default(DateTime);
                Ranges = new List<RangeState>();
            }

            public TaskSnapshot Copy(DateTime now)
            {
                return new TaskSnapshot
                {
                    Key = Item.Key, Label = Item.Label, Status = Status, Total = Item.ScopeTotal,
                    Read = Read, Written = Written, Pending = Math.Max(0, Read - Written), Inserted = Inserted,
                    Updated = Updated, Rejected = Rejected, Commits = Commits, Checkpoint = Checkpoint,
                    Elapsed = StartedAt == default ? 0 : ((EndedAt == default ? now : EndedAt) - StartedAt).TotalSeconds,
                    RateNow = StartedAt == default ? 0 : Written / Math.Max(0.001, ((EndedAt == default ? now : EndedAt) - StartedAt).TotalSeconds),
                    Ranges = Ranges.Select(r => new RangeSnapshot { From = r.Range.From, To = r.Range.To, Last = r.Last, Rows = r.Range.Rows, Done = r.Written, Status = r.Status }).ToList(),
                    Reconcile = Reconcile
                };
            }
        }

        private sealed class RangeState
        {
            public RangeState(KeyRange range, long singleRangeBase)
            {
                Range = range;
                Last = range.Last;
                Written = range.BaseRows > 0 ? range.BaseRows : singleRangeBase;
                Status = "run";
            }
            public KeyRange Range;
            public string Last;
            public long Read;
            public long Written;
            public string Status;
            public int Buffered;
            public int MaxBuffered;
            public long RolledBack;
        }

        private sealed class TrackedTargetSession : ITargetSession
        {
            private readonly MigrationEngine _owner;
            private ITargetSession _inner;

            internal TrackedTargetSession(MigrationEngine owner, ITargetSession inner)
            {
                _owner = owner;
                _inner = inner;
            }

            public Task<WriteResult> WriteBatchAsync(PlanItem item, IReadOnlyList<WriteColumn> columns, List<object[]> rows, CancellationToken cancellationToken) { return _inner.WriteBatchAsync(item, columns, rows, cancellationToken); }
            public Task SaveCheckpointAsync(CheckpointRecord record, CancellationToken cancellationToken) { return _inner.SaveCheckpointAsync(record, cancellationToken); }
            public Task CommitAsync() { return _inner.CommitAsync(); }
            public Task RollbackAsync() { return _inner.RollbackAsync(); }
            public Task TruncateAsync(PlanItem item, CancellationToken cancellationToken) { return _inner.TruncateAsync(item, cancellationToken); }
            public Task<long> CountExistingKeysAsync(PlanItem item, List<object[]> rows, CancellationToken cancellationToken) { return _inner.CountExistingKeysAsync(item, rows, cancellationToken); }

            public void Dispose()
            {
                var inner = Interlocked.Exchange(ref _inner, null);
                if (inner == null) return;
                _owner.Untrack(this);
                inner.Dispose();
            }
        }
    }
}
