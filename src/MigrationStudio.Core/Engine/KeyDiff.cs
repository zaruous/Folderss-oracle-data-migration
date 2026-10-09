using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MigrationStudio.Core.Engine
{
    /// <summary>대조용 키 한 줄. 원본·대상 모두 정규화한 키 문자열(Text)과 그 해시(Hash) 순서로 온다.</summary>
    public sealed class KeyEntry
    {
        /// <summary>정규화한 키 문자열의 해시. 스트림은 이 값의 오름차순이어야 한다(같은 값끼리는 순서 무관).</summary>
        public long Hash { get; set; }
        /// <summary>정규화한 키 문자열(여러 열이면 구분자로 이음). 같은 키인지는 이 값으로만 판단한다.</summary>
        public string Text { get; set; }
        /// <summary>대상 행 위치(대상 스트림만). 표시할 때 이 행만 고친다.</summary>
        public string RowId { get; set; }
        /// <summary>대상 행이 이미 표시돼 있나(대상 스트림만).</summary>
        public bool Marked { get; set; }
    }

    public interface IKeyStream : IDisposable
    {
        /// <summary>다음 키를 최대 <paramref name="maxRows"/>개. 끝이면 빈 목록.</summary>
        Task<List<KeyEntry>> ReadAsync(int maxRows, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 삭제 대조 저장소: 원본·대상 행 수, 해시 순서 키 스트림, 대상 표시·표시 해제. 표시는 자체 트랜잭션으로 커밋한다
    /// (같은 값을 다시 넣어도 결과가 같고, 원본에 다시 나타나면 표시를 지우므로 배치 경계에서 끊겨도 다음 대조가 맞춘다).
    /// </summary>
    public interface IReconcileStore
    {
        Task<long> CountAsync(PlanItem item, bool target, CancellationToken cancellationToken);
        Task<IKeyStream> OpenKeysAsync(PlanItem item, bool target, CancellationToken cancellationToken);
        /// <summary>표시(<paramref name="mark"/> = true) 또는 표시 해제. 실제로 바뀐 행 수.</summary>
        Task<int> SetMarkAsync(PlanItem item, IReadOnlyList<KeyEntry> keys, bool mark, CancellationToken cancellationToken);
    }

    public sealed class KeyDiffResult
    {
        public long SourceKeys { get; set; }
        public long TargetKeys { get; set; }
        /// <summary>원본에 없고 아직 표시 안 된 대상 행 수(표시 후보).</summary>
        public long MarkCount { get; set; }
        /// <summary>원본에 다시 있는데 표시돼 있는 대상 행 수(표시 해제 후보).</summary>
        public long UnmarkCount { get; set; }
        /// <summary>원본에 없고 이미 표시된 대상 행 수.</summary>
        public long AlreadyMarked { get; set; }
        /// <summary>원본에만 있는 키 수(대상에 아직 없음 — 지연 창·거부 행 등).</summary>
        public long SourceOnly { get; set; }
        /// <summary>표시 후보 중 모아 둔 것(최대 수집 한도까지). MarkCount가 더 크면 일부만 들어 있다.</summary>
        public List<KeyEntry> Mark { get; } = new List<KeyEntry>();
        public List<KeyEntry> Unmark { get; } = new List<KeyEntry>();
        /// <summary>모으지 못한 후보가 있나(수집 한도 초과).</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>
    /// 원본·대상 키 스트림을 해시 순서로 나란히 읽어 비교한다. 정렬을 키 값이 아니라 해시로 하는 이유: 두 DB의 문자 정렬 규칙(NLS_SORT)·
    /// 열 형식이 달라도 같은 정규화 문자열이면 같은 해시라 같은 순서가 된다. 해시가 같은 키들(충돌)은 그 묶음 안에서 문자열로 비교한다.
    /// 메모리는 한 번에 읽는 배치와 같은 해시 묶음, 모은 후보만큼만 쓴다.
    /// </summary>
    public static class KeyDiff
    {
        public const int BatchSize = 5000;

        /// <summary>
        /// 삭제 대조 키로 쓸 수 있는 대상 열 형식: 숫자·DATE·TIMESTAMP(시간대 없음)·문자. 두 DB에서 같은 문자열로 정규화할 수 있는 것만 —
        /// 정규화가 어긋나면 살아 있는 행이 "원본에 없음"으로 보여 표시된다.
        /// </summary>
        public static bool SupportsKeyType(string type)
        {
            var t = Types.OracleType.Parse(type);
            if (t == null) return false;
            if (t.IsNumber || t.IsChar) return true;
            return t.IsDate && t.TimeZone == null;
        }

        public static async Task<KeyDiffResult> RunAsync(IKeyStream source, IKeyStream target, int maxCollect, CancellationToken cancellationToken)
        {
            var result = new KeyDiffResult();
            var src = new Cursor(source);
            var tgt = new Cursor(target);
            await src.FillAsync(cancellationToken).ConfigureAwait(false);
            await tgt.FillAsync(cancellationToken).ConfigureAwait(false);
            while (src.HasCurrent || tgt.HasCurrent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long hash;
                if (!tgt.HasCurrent) hash = src.Current.Hash;
                else if (!src.HasCurrent) hash = tgt.Current.Hash;
                else hash = Math.Min(src.Current.Hash, tgt.Current.Hash);

                var sourceTexts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in await src.TakeGroupAsync(hash, cancellationToken).ConfigureAwait(false))
                {
                    result.SourceKeys++;
                    sourceTexts.Add(entry.Text);
                }

                var targetTexts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in await tgt.TakeGroupAsync(hash, cancellationToken).ConfigureAwait(false))
                {
                    result.TargetKeys++;
                    targetTexts.Add(entry.Text);
                    var inSource = sourceTexts.Contains(entry.Text);
                    if (!inSource && !entry.Marked)
                    {
                        result.MarkCount++;
                        Collect(result, result.Mark, entry, maxCollect);
                    }
                    else if (!inSource)
                    {
                        result.AlreadyMarked++;
                    }
                    else if (entry.Marked)
                    {
                        result.UnmarkCount++;
                        Collect(result, result.Unmark, entry, maxCollect);
                    }
                }

                foreach (var text in sourceTexts)
                {
                    if (!targetTexts.Contains(text)) result.SourceOnly++;
                }
            }

            return result;
        }

        private static void Collect(KeyDiffResult result, List<KeyEntry> list, KeyEntry entry, int maxCollect)
        {
            if (list.Count < maxCollect) list.Add(entry);
            else result.Truncated = true;
        }

        private sealed class Cursor
        {
            private readonly IKeyStream _stream;
            private List<KeyEntry> _batch = new List<KeyEntry>();
            private int _position;
            private bool _ended;
            private long _lastHash = long.MinValue;

            internal Cursor(IKeyStream stream) { _stream = stream; }

            internal bool HasCurrent { get { return _position < _batch.Count; } }
            internal KeyEntry Current { get { return _batch[_position]; } }

            internal async Task FillAsync(CancellationToken cancellationToken)
            {
                if (HasCurrent || _ended) return;
                _batch = await _stream.ReadAsync(BatchSize, cancellationToken).ConfigureAwait(false) ?? new List<KeyEntry>();
                _position = 0;
                if (_batch.Count == 0) _ended = true;
            }

            /// <summary>해시가 <paramref name="hash"/>인 연속 항목을 모두 꺼낸다(배치 경계를 넘어도).</summary>
            internal async Task<List<KeyEntry>> TakeGroupAsync(long hash, CancellationToken cancellationToken)
            {
                var group = new List<KeyEntry>();
                while (true)
                {
                    await FillAsync(cancellationToken).ConfigureAwait(false);
                    if (!HasCurrent || Current.Hash != hash) return group;
                    if (Current.Hash < _lastHash)
                    {
                        // 순서가 깨지면 같은 키를 다른 묶음에서 만나 "원본에 없음"으로 잘못 볼 수 있다 — 표시하기 전에 멈춘다.
                        throw new InvalidOperationException("키 스트림이 해시 순서가 아닙니다(" + Current.Hash + " < " + _lastHash + ")");
                    }
                    _lastHash = Current.Hash;
                    group.Add(Current);
                    _position++;
                }
            }
        }
    }
}
