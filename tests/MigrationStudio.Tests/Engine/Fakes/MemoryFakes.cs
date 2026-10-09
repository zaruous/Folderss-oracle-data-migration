using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;

namespace MigrationStudio.Tests.Engine.Fakes
{
    internal sealed class MemoryTable
    {
        internal MemoryTable(string name, IEnumerable<ColumnMetadata> columns, IEnumerable<string> keys)
        {
            Name = name;
            Columns = columns.ToList();
            Keys = keys.ToList();
        }

        internal string Name { get; private set; }
        internal List<ColumnMetadata> Columns { get; private set; }
        internal List<string> Keys { get; private set; }
        internal List<object[]> Rows { get; private set; } = new List<object[]>();
        internal object Gate { get; private set; } = new object();

        internal int Ordinal(string name)
        {
            return Columns.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        internal void ReplaceRows(List<object[]> rows)
        {
            Rows = rows;
        }
    }

    internal sealed class MemorySourceFactory : ISourceFactory, ISourceClock
    {
        private readonly Dictionary<string, MemoryTable> _tables;
        private int _reads;

        internal MemorySourceFactory(params MemoryTable[] tables)
        {
            _tables = tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        }

        internal int FailOnRead { get; set; }
        internal int DelayMilliseconds { get; set; }
        internal int ReadCalls { get { return _reads; } }
        /// <summary>원본 DB 시각(지연 창 상한용). 시험이 시간을 직접 움직인다.</summary>
        internal Func<DateTime> Now { get; set; } = () => DateTime.Now;

        public Task<DateTime> NowAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Now());
        }

        public Task<ISourceReader> OpenAsync(PlanItem item, KeyRange range, string lastValue, int fetchSize, CancellationToken cancellationToken)
        {
            var table = _tables[item.Mapping.Source];
            var cp = table.Ordinal(item.Mapping.CheckpointColumn);
            IEnumerable<object[]> selected = cp < 0 ? table.Rows : table.Rows.Where(r => After(r[cp], lastValue) && AtOrAfter(r[cp], range.From) && BeforeOrEqual(r[cp], range.To) && BeforeOrEqual(r[cp], range.Upper)).OrderBy(r => r[cp]);
            var rows = selected.Select(r => Project(table, item, r, cp)).ToList();
            var columns = item.WriteColumns.Select(c => c.Name).ToList();
            if (cp >= 0) columns.Add("MIG_CP_HIDDEN");
            return Task.FromResult<ISourceReader>(new Reader(this, columns, rows, cp < 0 ? -1 : columns.Count - 1));
        }

        private object[] Project(MemoryTable table, PlanItem item, object[] row, int checkpointOrdinal)
        {
            var result = new object[item.WriteColumns.Count + (checkpointOrdinal < 0 ? 0 : 1)];
            for (var i = 0; i < item.WriteColumns.Count; i++)
            {
                var mapping = item.Mapping.FindColumn(item.WriteColumns[i].Name);
                var ordinal = mapping == null ? -1 : table.Ordinal(mapping.Source);
                result[i] = ordinal < 0 ? DBNull.Value : row[ordinal];
            }
            if (checkpointOrdinal >= 0) result[result.Length - 1] = row[checkpointOrdinal];
            return result;
        }

        private static bool After(object value, string bound)
        {
            if (bound == null) return true;
            return Compare(value, bound) > 0;
        }

        private static bool BeforeOrEqual(object value, string bound)
        {
            if (bound == null) return true;
            return Compare(value, bound) <= 0;
        }

        private static bool AtOrAfter(object value, string bound)
        {
            if (bound == null) return true;
            return Compare(value, bound) >= 0;
        }

        private static int Compare(object value, string bound)
        {
            if (value is DateTime date) return date.CompareTo(DateTime.Parse(bound, CultureInfo.InvariantCulture));
            if (value is IConvertible && decimal.TryParse(bound, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            {
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture).CompareTo(number);
            }
            return string.Compare(Convert.ToString(value, CultureInfo.InvariantCulture), bound, StringComparison.Ordinal);
        }

        private sealed class Reader : ISourceReader
        {
            private readonly MemorySourceFactory _owner;
            private readonly List<object[]> _rows;
            private int _position;

            internal Reader(MemorySourceFactory owner, IReadOnlyList<string> columns, List<object[]> rows, int checkpointOrdinal)
            {
                _owner = owner;
                Columns = columns;
                _rows = rows;
                CheckpointOrdinal = checkpointOrdinal;
            }

            public IReadOnlyList<string> Columns { get; private set; }
            public int CheckpointOrdinal { get; private set; }

            public async Task<List<object[]>> ReadAsync(int maxRows, CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref _owner._reads);
                if (_owner.DelayMilliseconds > 0) await Task.Delay(_owner.DelayMilliseconds, cancellationToken);
                if (_owner.FailOnRead == call)
                    throw new InvalidOperationException("ORA-03113: 통신 채널에 EOF가 있습니다");
                var take = Math.Min(maxRows, _rows.Count - _position);
                var result = take <= 0 ? new List<object[]>() : _rows.GetRange(_position, take);
                _position += take;
                return result;
            }

            public void Dispose() { }
        }
    }

    internal sealed class MemoryTargetFactory : ITargetFactory
    {
        private readonly Dictionary<string, MemoryTable> _tables;

        internal MemoryTargetFactory(params MemoryTable[] tables)
        {
            _tables = tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        }

        internal MemoryCheckpointStore Checkpoints { get; set; }
        internal int DelayMilliseconds { get; set; }
        internal int FailWrites { get; set; }
        internal string FailureMessage { get; set; } = "ORA-03113: 통신 채널에 EOF가 있습니다";
        internal bool FailBeforeCommitOnce { get; set; }
        internal int OpenCount { get; private set; }
        internal int WriteCalls { get; private set; }

        public Task<ITargetSession> OpenAsync(CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.FromResult<ITargetSession>(new Session(this));
        }

        private sealed class Session : ITargetSession
        {
            private readonly MemoryTargetFactory _owner;
            private readonly List<Action> _changes = new List<Action>();
            private readonly List<CheckpointRecord> _checkpoints = new List<CheckpointRecord>();

            internal Session(MemoryTargetFactory owner) { _owner = owner; }

            public async Task<WriteResult> WriteBatchAsync(PlanItem item, IReadOnlyList<WriteColumn> columns, List<object[]> rows, CancellationToken cancellationToken)
            {
                _owner.WriteCalls++;
                if (_owner.DelayMilliseconds > 0) await Task.Delay(_owner.DelayMilliseconds, cancellationToken);
                if (_owner.FailWrites-- > 0) throw new InvalidOperationException(_owner.FailureMessage);
                var table = _owner._tables[item.Mapping.Target];
                var result = new WriteResult();
                lock (_owner._tables)
                {
                    foreach (var source in rows)
                    {
                        var row = new object[table.Columns.Count];
                        for (var c = 0; c < columns.Count; c++) row[table.Ordinal(columns[c].Name)] = source[c];
                        var error = Validate(table, row, item.Mapping.Mode);
                        if (error != null)
                        {
                            result.Rejected++;
                            if (result.Rejects.Count < 20) result.Rejects.Add(new RejectedRow { Index = result.Written + result.Rejected - 1, Message = error, Values = row });
                            continue;
                        }
                        var existing = Find(table, row);
                        if (string.Equals(item.Mapping.Mode, WriteModes.Merge, StringComparison.Ordinal) && existing != null)
                        {
                            result.Updated++;
                            var written = columns.Select(c => table.Ordinal(c.Name)).ToArray();
                            _changes.Add(() => Replace(table, row, written));
                        }
                        else
                        {
                            if (string.Equals(item.Mapping.Mode, WriteModes.DeleteInsert, StringComparison.Ordinal)) _changes.Add(() => Delete(table, row));
                            result.Inserted++;
                            _changes.Add(() => table.Rows.Add((object[])row.Clone()));
                        }
                        result.Written++;
                    }
                }
                return result;
            }

            public Task SaveCheckpointAsync(CheckpointRecord record, CancellationToken cancellationToken)
            {
                _checkpoints.Add(record);
                return Task.CompletedTask;
            }

            public Task CommitAsync()
            {
                if (_owner.FailBeforeCommitOnce)
                {
                    _owner.FailBeforeCommitOnce = false;
                    throw new InvalidOperationException("ORA-03113: 통신 채널에 EOF가 있습니다");
                }
                lock (_owner._tables)
                {
                    foreach (var change in _changes) change();
                    foreach (var checkpoint in _checkpoints) _owner.Checkpoints?.Save(checkpoint);
                }
                _changes.Clear();
                _checkpoints.Clear();
                return Task.CompletedTask;
            }

            public Task RollbackAsync()
            {
                _changes.Clear();
                _checkpoints.Clear();
                return Task.CompletedTask;
            }

            public Task TruncateAsync(PlanItem item, CancellationToken cancellationToken)
            {
                _owner._tables[item.Mapping.Target].Rows.Clear();
                return Task.CompletedTask;
            }

            public Task<long> CountExistingKeysAsync(PlanItem item, List<object[]> rows, CancellationToken cancellationToken)
            {
                var table = _owner._tables[item.Mapping.Target];
                int count;
                lock (_owner._tables)
                {
                    count = rows.Count(r =>
                    {
                        var candidate = new object[table.Columns.Count];
                        for (var c = 0; c < item.WriteColumns.Count; c++) candidate[table.Ordinal(item.WriteColumns[c].Name)] = r[c];
                        return Find(table, candidate) != null;
                    });
                }
                return Task.FromResult((long)count);
            }

            private static string Validate(MemoryTable table, object[] row, string mode)
            {
                for (var i = 0; i < table.Columns.Count; i++)
                {
                    var value = row[i];
                    if (!table.Columns[i].Nullable && (value == null || value == DBNull.Value)) return "NOT NULL";
                    var text = value as string;
                    var length = MigrationStudio.Core.Types.OracleType.Parse(table.Columns[i].Type)?.Length;
                    if (text != null && length.HasValue && text.Length > length.Value) return "길이 초과";
                }
                if (string.Equals(mode, WriteModes.InsertOnly, StringComparison.Ordinal) && Find(table, row) != null) return "중복 키";
                return null;
            }

            private static object[] Find(MemoryTable table, object[] row)
            {
                return table.Rows.FirstOrDefault(r => table.Keys.All(k => Equals(r[table.Ordinal(k)], row[table.Ordinal(k)])));
            }

            /// <summary>Oracle MERGE처럼 쓰기 열만 바꾼다 — 쓰지 않는 열(삭제 표시 열 등)은 그대로 둔다.</summary>
            private static void Replace(MemoryTable table, object[] row, int[] written)
            {
                var old = Find(table, row);
                if (old == null) return;
                foreach (var ordinal in written) old[ordinal] = row[ordinal];
            }

            private static void Delete(MemoryTable table, object[] row)
            {
                var old = Find(table, row);
                if (old != null) table.Rows.Remove(old);
            }

            public void Dispose() { }
        }
    }

    internal sealed class MemoryCheckpointStore : ICheckpointStore
    {
        private readonly List<CheckpointRecord> _records = new List<CheckpointRecord>();

        internal bool FailNextSave { get; set; }

        internal void Save(CheckpointRecord record)
        {
            lock (_records)
            {
                _records.RemoveAll(r => r.Job == record.Job && r.TaskKey == record.TaskKey);
                _records.Add(Clone(record));
            }
        }

        public Task<CheckpointRecord> GetAsync(string job, string taskKey, CancellationToken cancellationToken)
        {
            lock (_records) return Task.FromResult(Clone(_records.FirstOrDefault(r => r.Job == job && r.TaskKey == taskKey)));
        }

        public Task<List<CheckpointRecord>> ListAsync(string job, CancellationToken cancellationToken)
        {
            lock (_records) return Task.FromResult(_records.Where(r => r.Job == job).Select(Clone).ToList());
        }

        public Task SaveLocalAsync(CheckpointRecord record, CancellationToken cancellationToken)
        {
            if (FailNextSave) { FailNextSave = false; throw new InvalidOperationException("체크포인트 파일 충돌"); }
            Save(record);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string job, string taskKey, CancellationToken cancellationToken)
        {
            lock (_records) _records.RemoveAll(r => r.Job == job && r.TaskKey == taskKey);
            return Task.CompletedTask;
        }

        private static CheckpointRecord Clone(CheckpointRecord r)
        {
            if (r == null) return null;
            return new CheckpointRecord { Job = r.Job, TaskKey = r.TaskKey, Column = r.Column, Value = r.Value, RangeFrom = r.RangeFrom,
                RangeTo = r.RangeTo, RowsDone = r.RowsDone, RowsTotal = r.RowsTotal, Status = r.Status, RunId = r.RunId, UpdatedAt = r.UpdatedAt };
        }
    }

    /// <summary>
    /// 메모리 삭제 대조 저장소. 키 정규화는 값의 불변 문화권 문자열, 해시는 바꿔 끼울 수 있다(충돌 시험용).
    /// 표시 열은 매핑의 MarkColumn, 값은 MarkValue(SYSDATE면 대조 시각 대신 고정 시각).
    /// </summary>
    internal sealed class MemoryReconcileStore : IReconcileStore
    {
        private readonly MemoryTable _source;
        private readonly MemoryTable _target;

        internal MemoryReconcileStore(MemoryTable source, MemoryTable target)
        {
            _source = source;
            _target = target;
        }

        internal Func<string, long> Hash { get; set; } = text => (long)(uint)StringComparer.Ordinal.GetHashCode(text);
        internal int FailOpen { get; set; }
        internal int MarkCalls { get; private set; }
        internal int CountCalls { get; private set; }
        /// <summary>원본 키 스트림을 비우기(원본 접속 실수 흉내).</summary>
        internal bool EmptySource { get; set; }

        public Task<long> CountAsync(PlanItem item, bool target, CancellationToken cancellationToken)
        {
            CountCalls++;
            var table = target ? _target : _source;
            var mark = target && DeleteModes.IsMark(item.Mapping.DeleteMode) ? table.Ordinal(item.Mapping.MarkColumn) : -1;
            lock (table.Gate) return Task.FromResult((long)table.Rows.Count(r => mark < 0 || r[mark] == null || r[mark] == DBNull.Value));
        }

        public Task<IKeyStream> OpenKeysAsync(PlanItem item, bool target, CancellationToken cancellationToken)
        {
            if (FailOpen-- > 0) throw new InvalidOperationException("ORA-00942: 테이블 또는 뷰가 존재하지 않습니다");
            var table = target ? _target : _source;
            var keyColumns = item.Mapping.MergeKey.Select(k =>
            {
                var cm = item.Mapping.FindColumn(k);
                return target ? table.Ordinal(k) : table.Ordinal(cm != null ? cm.Source : k);
            }).ToList();
            var mark = target && DeleteModes.IsMark(item.Mapping.DeleteMode) ? table.Ordinal(item.Mapping.MarkColumn) : -1;
            List<KeyEntry> entries;
            lock (table.Gate)
            {
                entries = (EmptySource && !target ? new List<object[]>() : table.Rows).Select(r =>
                {
                    var text = string.Join("\u001f", keyColumns.Select(o => Convert.ToString(r[o], CultureInfo.InvariantCulture)));
                    return new KeyEntry
                    {
                        Text = text, Hash = Hash(text),
                        RowId = target ? text : null,
                        Marked = mark >= 0 && r[mark] != null && r[mark] != DBNull.Value
                    };
                }).OrderBy(e => e.Hash).ToList();
            }
            return Task.FromResult<IKeyStream>(new Stream(entries));
        }

        public Task<int> SetMarkAsync(PlanItem item, IReadOnlyList<KeyEntry> keys, bool mark, CancellationToken cancellationToken)
        {
            MarkCalls++;
            var markOrdinal = _target.Ordinal(item.Mapping.MarkColumn);
            var keyColumns = item.Mapping.MergeKey.Select(k => _target.Ordinal(k)).ToList();
            var wanted = new HashSet<string>(keys.Select(k => k.RowId), StringComparer.Ordinal);
            var changed = 0;
            lock (_target.Gate)
            {
                foreach (var row in _target.Rows)
                {
                    var text = string.Join("\u001f", keyColumns.Select(o => Convert.ToString(row[o], CultureInfo.InvariantCulture)));
                    var isMarked = row[markOrdinal] != null && row[markOrdinal] != DBNull.Value;
                    if (!wanted.Contains(text) || isMarked == mark) continue;
                    row[markOrdinal] = mark ? (object)item.Mapping.MarkValue : null;
                    changed++;
                }
            }
            return Task.FromResult(changed);
        }

        /// <summary>한 번에 2개씩만 돌려줘서 같은 해시 묶음이 배치 경계를 넘는 경우도 지나가게 한다.</summary>
        private sealed class Stream : IKeyStream
        {
            private readonly List<KeyEntry> _entries;
            private int _position;

            internal Stream(List<KeyEntry> entries) { _entries = entries; }

            public Task<List<KeyEntry>> ReadAsync(int maxRows, CancellationToken cancellationToken)
            {
                var take = Math.Min(Math.Min(maxRows, 2), _entries.Count - _position);
                var result = take <= 0 ? new List<KeyEntry>() : _entries.GetRange(_position, take);
                _position += Math.Max(0, take);
                return Task.FromResult(result);
            }

            public void Dispose() { }
        }
    }

    internal sealed class ManualClock : IRunClock
    {
        internal ManualClock(DateTime now) { Now = now; }
        public DateTime Now { get; private set; }
        internal void Advance(TimeSpan span) { Now = Now.Add(span); }
    }

    internal sealed class CaptureListener : IRunListener
    {
        internal List<LogEntry> Logs { get; private set; } = new List<LogEntry>();
        internal List<RunSnapshot> Snapshots { get; private set; } = new List<RunSnapshot>();
        internal List<CheckpointRecord> Checkpoints { get; private set; } = new List<CheckpointRecord>();
        internal RunSnapshot Final { get; private set; }
        internal bool Throw { get; set; }

        public void OnSnapshot(RunSnapshot snapshot) { if (Throw) throw new InvalidOperationException(); Snapshots.Add(snapshot); }
        public void OnLog(LogEntry entry) { if (Throw) throw new InvalidOperationException(); Logs.Add(entry); }
        public void OnCheckpoint(CheckpointRecord record) { if (Throw) throw new InvalidOperationException(); Checkpoints.Add(record); }
        public void OnEnd(RunSnapshot final) { if (Throw) throw new InvalidOperationException(); Final = final; }
    }
}
