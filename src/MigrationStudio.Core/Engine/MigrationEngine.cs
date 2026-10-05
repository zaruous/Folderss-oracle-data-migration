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
            _recorder = recorder ?? new NullRunRecorder();
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
                        " · 오류 정책=" + PolicyText(ErrorPolicy()) + " · 체크포인트=" + StoreText());
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

                    for (var i = 0; i < _spec.Plan.Count; i++)
                    {
                        if (_stop.IsCancellationRequested)
                        {
                            break;
                        }

                        var task = _tasks[i];
                        await RunItemAsync(task, _stop.Token).ConfigureAwait(false);
                        if (task.Status == "failed" || task.Status == "stopped")
                        {
                            for (var j = i + 1; j < _tasks.Count; j++)
                            {
                                _tasks[j].Status = "skipped";
                            }
                            break;
                        }
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

                snapshot.Totals.Total = _spec.Plan.Sum(p => p.ScopeTotal);
                snapshot.Totals.Done = _tasks.Sum(t => t.Item.BaseRows + t.Written);
                snapshot.Totals.Pct = snapshot.Totals.Total == 0 ? 100 : snapshot.Totals.Done * 100.0 / snapshot.Totals.Total;
                snapshot.Totals.EtaSeconds = _rate > 0 ? Math.Max(0, snapshot.Totals.Total - snapshot.Totals.Done) / _rate : (double?)null;
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
            if (!IsDry())
            {
                await _recorder.TaskStartedAsync(_spec, task.Item, cancellationToken).ConfigureAwait(false);
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
                    if (string.Equals(_spec.CheckpointStore, "LOCAL", StringComparison.Ordinal) &&
                        string.Equals(_spec.RunMode, "RESUME", StringComparison.Ordinal) && task.Commits == 0 &&
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
                var instant = (rows - _lastRateRows) / seconds;
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

        private string SafeError(Exception ex)
        {
            return Redact(OracleErrors.Describe(ex));
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
        private string StoreText() { return string.Equals(_spec.CheckpointStore, "LOCAL", StringComparison.Ordinal) ? "로컬 파일" : "대상 DB(" + _spec.ControlPrefix + "CHECKPOINT)"; }
        private static string Number(long n) { return n.ToString("N0", CultureInfo.GetCultureInfo("en-US")); }
        private static string Duration(double seconds) { return TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture); }
        private static string ModeText(string mode) { return mode == "DRY" ? "Dry Run(쓰기 없음)" : mode == "RESUME" ? "체크포인트에서 재개" : "이관 실행"; }
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
            public List<RangeState> Ranges = new List<RangeState>();

            public TaskSnapshot Copy(DateTime now)
            {
                return new TaskSnapshot
                {
                    Key = Item.Key, Label = Item.Label, Status = Status, Total = Item.ScopeTotal,
                    Read = Read, Written = Written, Pending = Math.Max(0, Read - Written), Inserted = Inserted,
                    Updated = Updated, Rejected = Rejected, Commits = Commits, Checkpoint = Checkpoint,
                    Elapsed = StartedAt == default ? 0 : ((EndedAt == default ? now : EndedAt) - StartedAt).TotalSeconds,
                    RateNow = StartedAt == default ? 0 : Written / Math.Max(0.001, ((EndedAt == default ? now : EndedAt) - StartedAt).TotalSeconds),
                    Ranges = Ranges.Select(r => new RangeSnapshot { From = r.Range.From, To = r.Range.To, Last = r.Last, Rows = r.Range.Rows, Done = r.Written, Status = r.Status }).ToList()
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
