using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Sql;
using MigrationStudio.Core.Text;
using MigrationStudio.Core.Types;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Validation
{
    /// <summary>실행 후 검증 P01~P06. 양쪽 DB에서 집계해 비교한다.</summary>
    public sealed class PostValidationEngine : IPostValidationRunner
    {
        private const int SampleRows = 100;
        private const int MaxBucketKeys = 10000;
        private const int KeyPageSize = 10000;
        private const int KeyInBatch = 1000;
        private const long LargeTableRows = 500_000;
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(10);

        private readonly IDatabaseAdapter _adapter;
        private readonly PostValidationContext _context;

        public PostValidationEngine(IDatabaseAdapter adapter, PostValidationContext context)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<List<PostItem>> RunPostAsync(PostValidationRequest request, Action<PostItem> onItem, CancellationToken ct)
        {
            return RunAsync(request, onItem, ct);
        }

        public async Task<List<PostItem>> RunAsync(PostValidationRequest request, Action<PostItem> onItem, CancellationToken ct)
        {
            var items = new List<PostItem>();
            if (request == null || request.Final == null || request.Final.Tasks == null)
            {
                Emit(items, onItem, PostItemOf("—", "실행 결과", "", "", CheckLevels.Info, "끝난 작업이 없습니다", null));
                return items;
            }

            var planByKey = (_context.Plan ?? new List<PlanItem>()).ToDictionary(p => p.Key, StringComparer.Ordinal);
            foreach (var task in request.Final.Tasks)
            {
                ct.ThrowIfCancellationRequested();
                if (IsSkipStatus(task.Status))
                {
                    continue;
                }

                PlanItem plan;
                if (!planByKey.TryGetValue(task.Key, out plan))
                {
                    continue;
                }

                var group = !string.IsNullOrEmpty(task.Label) ? task.Label : plan.Label;
                var mappingId = plan.Key;

                if (request.Dry)
                {
                    Emit(items, onItem, PostItemOf(group, "행 수", Format.Number(task.Total), "—", CheckLevels.Skip,
                        "Dry Run은 대상에 쓰지 않아 건너뜀", mappingId));
                    continue;
                }

                if (IsIncrementalJob())
                {
                    // 증분·CDC 실행은 "이번에 쓴 행"이 원본 전체가 아니다 — 대상 전체와 원본 범위의 행 수를 비교하고,
                    // 키·데이터 비교(P02~P06)는 실행 범위를 정할 수 없어 건너뛴다(전체 이관 실행에서 검증).
                    await RunIncrementalP01Async(items, onItem, plan, group, ct).ConfigureAwait(false);
                    Emit(items, onItem, PostItemOf(group, "키·데이터 비교", "—", "—", CheckLevels.Skip,
                        "증분·CDC 실행은 행 수만 비교 — PK 누락·중복·샘플·해시는 전체 이관 실행 뒤에 검증", mappingId));
                    continue;
                }

                var baseRows = BaseRowsFor(task.Key);
                var partial = !string.Equals(task.Status, "done", StringComparison.Ordinal);
                await RunP01Async(items, onItem, request, plan, task, group, baseRows, partial, ct).ConfigureAwait(false);
                if (partial)
                {
                    continue;
                }

                if (IsLarge(plan, task))
                {
                    Emit(items, onItem, PostItemOf(group, "진행", "", "", CheckLevels.Info,
                        "큰 테이블이라 오래 걸릴 수 있습니다(취소 가능)", mappingId));
                }

                await RunP02Async(items, onItem, request, plan, task, group, baseRows, ct).ConfigureAwait(false);
                await RunP03Async(items, onItem, plan, task, group, baseRows, ct).ConfigureAwait(false);
                await RunP04Async(items, onItem, plan, task, group, ct).ConfigureAwait(false);
                await RunP05Async(items, onItem, request, plan, task, group, baseRows, ct).ConfigureAwait(false);
                await RunP06Async(items, onItem, plan, task, group, baseRows, ct).ConfigureAwait(false);
            }

            if (items.Count == 0)
            {
                Emit(items, onItem, PostItemOf("—", "실행 결과", "", "", CheckLevels.Info, "끝난 작업이 없습니다", null));
            }

            return items;
        }

        private async Task RunP01Async(List<PostItem> items, Action<PostItem> onItem, PostValidationRequest request, PlanItem plan,
            TaskSnapshot task, string group, long baseRows, bool partial, CancellationToken ct)
        {
            var mappingId = plan.Key;
            var expect = task.Total;
            var srcCount = await CountOnSourceAsync(plan, ct).ConfigureAwait(false);
            var accounted = baseRows + task.Written + task.Rejected;
            var srcText = srcCount != null ? Format.Number(srcCount.Value) : Format.Number(expect);
            var tgtText = Format.Number(task.Written) + (task.Rejected > 0 ? " + 거부 " + Format.Number(task.Rejected) : "");

            if (partial)
            {
                Emit(items, onItem, PostItemOf(group, "행 수", srcText, tgtText, CheckLevels.Warn,
                    "중지됨: " + Format.Number(accounted) + " / " + Format.Number(expect) + " — 재개한 뒤 다시 검증하세요", mappingId));
                return;
            }

            var level = accounted == expect && (srcCount == null || srcCount.Value == expect) ? CheckLevels.Pass : CheckLevels.Error;
            var detail = level == CheckLevels.Pass ? "MATCH" : "원본 " + srcText + "행 · 반영 " + Format.Number(accounted) + "행";
            if (level == CheckLevels.Pass && task.Rejected > 0)
            {
                detail += " (거부 " + Format.Number(task.Rejected) + "행은 " + (plan.ErrorTable ?? "오류 테이블") + ")";
            }

            if (level == CheckLevels.Pass && !string.IsNullOrEmpty(plan.ErrorTable))
            {
                var errCnt = await CountOnTargetAsync(PostValidationSql.ErrorTableCount(_context.TargetMeta.Schema, plan.ErrorTable, request.RunId), ct)
                    .ConfigureAwait(false);
                if (errCnt != null && errCnt.Value != task.Rejected)
                {
                    level = CheckLevels.Warn;
                    detail = "오류 테이블 " + Format.Number(errCnt.Value) + "행 ≠ 엔진 보고 " + Format.Number(task.Rejected) + "행";
                }
            }

            var targetRowsBefore = await InferTargetRowsBeforeAsync(plan, task, ct).ConfigureAwait(false);
            if (level == CheckLevels.Pass && targetRowsBefore == 0 && IsEmptyTargetMode(plan.Mapping)
                && task.Rejected == 0)
            {
                var tgtTotal = await CountOnTargetAsync(PostValidationSql.TargetRowCount(_context.TargetMeta.Schema, plan.Mapping.Target, null), ct)
                    .ConfigureAwait(false);
                var want = task.Written + baseRows;
                if (tgtTotal != null && tgtTotal.Value != want)
                {
                    level = CheckLevels.Error;
                    detail = "대상 행 수 " + Format.Number(tgtTotal.Value) + " ≠ 기록 " + Format.Number(want);
                }
            }

            Emit(items, onItem, PostItemOf(group, "행 수", srcText, tgtText, level, detail, mappingId));
        }

        private bool IsIncrementalJob()
        {
            var mode = _context.Job != null && _context.Job.Strategy != null ? _context.Job.Strategy.Mode : null;
            return string.Equals(mode, ExecutionModes.Incremental, StringComparison.Ordinal) || string.Equals(mode, ExecutionModes.Cdc, StringComparison.Ordinal);
        }

        /// <summary>
        /// 증분·CDC의 행 수 비교: 원본 범위 전체 ↔ 대상 전체(삭제 표시 매핑이면 표시 안 된 행만). 대상이 많으면 원본에서 지운 행이 남은 것일 수 있어
        /// 숨기지 않고 WARN으로 보인다 — 삭제 처리를 "따라가지 않음"으로 둔 매핑에서 차이가 조용히 쌓이지 않게.
        /// </summary>
        private async Task RunIncrementalP01Async(List<PostItem> items, Action<PostItem> onItem, PlanItem plan, string group, CancellationToken ct)
        {
            var mark = DeleteModes.IsMark(plan.Mapping.DeleteMode) && !string.IsNullOrWhiteSpace(plan.Mapping.MarkColumn);
            var srcCount = await CountOnSourceAsync(plan, ct).ConfigureAwait(false);
            var tgtCount = await CountOnTargetAsync(PostValidationSql.TargetRowCount(_context.TargetMeta.Schema, plan.Mapping.Target,
                mark ? RequireId(plan.Mapping.MarkColumn) + " IS NULL" : null), ct).ConfigureAwait(false);
            var srcText = srcCount != null ? Format.Number(srcCount.Value) : "?";
            var tgtText = tgtCount != null ? Format.Number(tgtCount.Value) : "?";
            if (srcCount == null || tgtCount == null)
            {
                Emit(items, onItem, PostItemOf(group, "행 수", srcText, tgtText, CheckLevels.Info, "행 수를 읽지 못함 — 권한·접속 확인", plan.Key));
                return;
            }

            var diff = tgtCount.Value - srcCount.Value;
            string level;
            string detail;
            if (diff == 0)
            {
                level = CheckLevels.Pass;
                detail = "MATCH — 대상 " + (mark ? "살아 있는 행" : "전체") + " = 원본 범위";
            }
            else if (diff > 0)
            {
                level = CheckLevels.Warn;
                detail = "원본에 없는 대상 행 " + Format.Number(diff) + " — " + (mark
                    ? "원본에서 지웠지만 아직 표시 안 됨(대조 주기 전·승인 전·상한 초과)"
                    : "삭제 처리가 '따라가지 않음'이라 원본에서 지운 행이 대상에 남아 있을 수 있음");
            }
            else
            {
                level = CheckLevels.Warn;
                detail = "대상이 원본보다 " + Format.Number(-diff) + "행 적음 — 아직 반영 안 된 변경(지연 창 이후·주기 사이)이거나 거부된 행";
            }

            Emit(items, onItem, PostItemOf(group, "행 수", srcText, tgtText, level, detail, plan.Key));
        }

        private async Task RunP02Async(List<PostItem> items, Action<PostItem> onItem, PostValidationRequest request, PlanItem plan,
            TaskSnapshot task, string group, long baseRows, CancellationToken ct)
        {
            var mappingId = plan.Key;
            var keys = ResolveTargetKeys(plan);
            if (keys.Count == 0)
            {
                Emit(items, onItem, PostItemOf(group, "PK 누락", "—", "—", CheckLevels.Skip, "비교할 키가 없습니다", mappingId));
                return;
            }

            var targetRowsBefore = await InferTargetRowsBeforeAsync(plan, task, ct).ConfigureAwait(false);
            var fullBucket = targetRowsBefore == 0 && IsEmptyTargetMode(plan.Mapping);
            var rejectKeys = await LoadRejectKeysAsync(plan, request.RunId, keys[0], ct).ConfigureAwait(false);
            var srcFrom = PostValidationSql.BuildTransformedFrom(plan.Mapping, _context.SourceMeta.Schema, plan.SourceMetadata, plan.TargetMetadata);
            var srcKeyExprs = SourceKeyExpressions(plan, keys);
            var srcHash = PostValidationSql.KeyHashExpression(srcKeyExprs);
            var srcWhere = BuildSourceRejectWhere(keys[0], rejectKeys);
            var tgtSchema = _context.TargetMeta.Schema;
            var tgtTable = plan.Mapping.Target;
            var tgtHash = PostValidationSql.KeyHashExpression(keys.Select(k => RequireId(k)).ToList());
            var tgtWhere = BuildTargetScopeWhere(plan, task, keys, fullBucket, rejectKeys);

            if (fullBucket)
            {
                var srcBuckets = await LoadBucketsAsync(_context.Source, _context.SourceMeta.Schema,
                    PostValidationSql.KeyHashBuckets(srcFrom, srcHash, srcWhere), ct).ConfigureAwait(false);
                var tgtFrom = tgtSchema + "." + RequireId(tgtTable);
                var tgtBuckets = await LoadBucketsAsync(_context.Target, tgtSchema,
                    PostValidationSql.KeyHashBuckets(tgtFrom, tgtHash, tgtWhere), ct).ConfigureAwait(false);
                var missing = CompareBuckets(srcBuckets, tgtBuckets);
                if (missing.Count == 0)
                {
                    Emit(items, onItem, PostItemOf(group, "PK 누락", "0", "0", CheckLevels.Pass,
                        "원본 키가 대상에 모두 있음(거부 행 제외) — 키 해시 버킷 1,024개 비교(전체 버킷)", mappingId));
                    return;
                }

                if (missing.Count > MaxBucketKeys)
                {
                    Emit(items, onItem, PostItemOf(group, "PK 누락", "?", "?", CheckLevels.Warn,
                        "불일치 버킷 " + missing.Count + "개 — 키 비교 생략", mappingId));
                    return;
                }

                var missCnt = await CompareBucketKeysAsync(plan, srcFrom, srcHash, srcKeyExprs, keys, missing, srcWhere, ct)
                    .ConfigureAwait(false);
                var level = missCnt == 0 ? CheckLevels.Pass : CheckLevels.Error;
                Emit(items, onItem, PostItemOf(group, "PK 누락", Format.Number(missCnt), "0", level,
                    level == CheckLevels.Pass
                        ? "원본 키가 대상에 모두 있음(거부 행 제외) — 키 해시 버킷 1,024개 비교(전체 버킷)"
                        : "대상에 없는 원본 키 " + Format.Number(missCnt) + "건", mappingId));
                return;
            }

            var sampleTail = "";
            var maxKeys = KeyPageSize;
            var totalKeys = await CountOnSourceAsync(plan, ct).ConfigureAwait(false);
            if (totalKeys != null && totalKeys.Value > 1_000_000)
            {
                maxKeys = 100_000;
                sampleTail = " · 표본 100,000키";
            }

            var missingKeys = await FindMissingKeysPresenceAsync(plan, keys, rejectKeys, maxKeys, ct).ConfigureAwait(false);
            var lvl = missingKeys == 0 ? CheckLevels.Pass : CheckLevels.Error;
            Emit(items, onItem, PostItemOf(group, "PK 누락", Format.Number(missingKeys), "0", lvl,
                lvl == CheckLevels.Pass
                    ? "원본 키가 대상에 모두 있음(거부 행 제외) — 원본 키의 대상 존재 확인" + sampleTail
                    : "대상에 없는 원본 키 " + Format.Number(missingKeys) + "건" + sampleTail, mappingId));
        }

        private async Task RunP03Async(List<PostItem> items, Action<PostItem> onItem, PlanItem plan, TaskSnapshot task,
            string group, long baseRows, CancellationToken ct)
        {
            var keys = ResolveTargetKeys(plan);
            var scope = BuildTargetScopeWhere(plan, task, keys, false, null);
            var sql = PostValidationSql.TargetDuplicateKeyCount(_context.TargetMeta.Schema, plan.Mapping.Target, keys, scope);
            var dup = await CountOnTargetAsync(sql, ct).ConfigureAwait(false);
            var cnt = dup ?? 0;
            Emit(items, onItem, PostItemOf(group, "중복 키", "0", Format.Number(cnt), cnt == 0 ? CheckLevels.Pass : CheckLevels.Error,
                cnt == 0 ? "대상 키 중복 없음" : "중복 키 " + Format.Number(cnt) + "건", plan.Key));
        }

        private async Task RunP04Async(List<PostItem> items, Action<PostItem> onItem, PlanItem plan, TaskSnapshot task, string group, CancellationToken ct)
        {
            var mappingId = plan.Key;
            var cols = plan.WriteColumns ?? SqlGenerator.WriteColumns(plan.Mapping, plan.TargetMetadata);
            var keys = ResolveTargetKeys(plan);
            if (cols.Count == 0 || keys.Count == 0)
            {
                Emit(items, onItem, PostItemOf(group, "샘플 데이터", "—", "—", CheckLevels.Skip, "비교할 열이 없습니다", mappingId));
                return;
            }

            var sampleSql = PostValidationSql.SampleSourceRows(plan, _context.SourceMeta.Schema, SampleRows);
            var sample = await QueryAsync(_context.Source, _context.SourceMeta.Schema, sampleSql, null, SampleRows, ct).ConfigureAwait(false);
            if (sample == null || sample.Rows == null || sample.Rows.Count == 0)
            {
                Emit(items, onItem, PostItemOf(group, "샘플 데이터", "0행", "0행", CheckLevels.Warn, "원본에서 샘플을 가져오지 못했습니다", mappingId));
                return;
            }

            var mismatches = 0;
            var details = new List<string>();
            for (var i = 0; i < sample.Rows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = sample.Rows[i];
                var keyVal = ReadColumn(sample, row, keys[0]);
                var tgtSql = PostValidationSql.TargetRowsByKeys(_context.TargetMeta.Schema, plan.Mapping.Target,
                    cols.Select(c => c.Name).ToList(), keys, 1);
                var binds = new List<SqlBind> { new SqlBind { Name = "K1", Value = keyVal } };
                var tgt = await QueryAsync(_context.Target, _context.TargetMeta.Schema, tgtSql, binds, 2, ct).ConfigureAwait(false);
                if (tgt == null || tgt.Rows == null || tgt.Rows.Count == 0)
                {
                    mismatches++;
                    if (details.Count < 3)
                    {
                        details.Add("키 " + keyVal + ": 대상에 없음");
                    }

                    continue;
                }

                var tgtRow = tgt.Rows[0];
                for (var c = 0; c < cols.Count; c++)
                {
                    var sVal = NormalizeCell(ReadColumn(sample, row, cols[c].Name));
                    var tVal = NormalizeCell(ReadColumn(tgt, tgtRow, cols[c].Name));
                    if (!string.Equals(sVal, tVal, StringComparison.Ordinal))
                    {
                        mismatches++;
                        if (details.Count < 3)
                        {
                            details.Add("키 " + keyVal + " · " + cols[c].Name + ": '" + Trunc(sVal) + "' ≠ '" + Trunc(tVal) + "'");
                        }

                        break;
                    }
                }
            }

            var level = mismatches == 0 ? CheckLevels.Pass : (mismatches <= 3 ? CheckLevels.Warn : CheckLevels.Error);
            var detail = mismatches == 0
                ? "무작위 " + SampleRows + "행: 원본에 변환식을 적용한 값 = 대상 값"
                : string.Join("\n", details) + (mismatches > details.Count ? "\n… 외 " + (mismatches - details.Count) + "건" : "");
            Emit(items, onItem, PostItemOf(group, "샘플 데이터", SampleRows + "행", SampleRows + "행", level, detail, mappingId));
        }

        private async Task RunP05Async(List<PostItem> items, Action<PostItem> onItem, PostValidationRequest request, PlanItem plan,
            TaskSnapshot task, string group, long baseRows, CancellationToken ct)
        {
            var mappingId = plan.Key;
            var cols = plan.WriteColumns ?? SqlGenerator.WriteColumns(plan.Mapping, plan.TargetMetadata);
            if (cols.Count == 0)
            {
                Emit(items, onItem, PostItemOf(group, "해시", "—", "—", CheckLevels.Skip, "매핑 열이 없습니다", mappingId));
                return;
            }

            var keys = ResolveTargetKeys(plan);
            var keyCol = keys.Count > 0 ? keys[0] : null;
            var rejectKeys = await LoadRejectKeysAsync(plan, request.RunId, keyCol, ct).ConfigureAwait(false);
            var srcFrom = PostValidationSql.BuildTransformedFrom(plan.Mapping, _context.SourceMeta.Schema, plan.SourceMetadata, plan.TargetMetadata);
            var srcSql = PostValidationSql.DataHashSum(srcFrom, cols, keyCol, rejectKeys);
            var srcHash = await ScalarLongAsync(_context.Source, _context.SourceMeta.Schema, srcSql, ct).ConfigureAwait(false);

            var targetRowsBefore = await InferTargetRowsBeforeAsync(plan, task, ct).ConfigureAwait(false);
            var fullBucket = targetRowsBefore == 0 && IsEmptyTargetMode(plan.Mapping);
            string tgtSql;
            string tgtNote = null;
            var tgtFrom = _context.TargetMeta.Schema + "." + RequireId(plan.Mapping.Target);
            if (fullBucket)
            {
                tgtSql = PostValidationSql.DataHashSum(tgtFrom, cols, keyCol, rejectKeys);
            }
            else if (IsContinuousKey(plan, keys))
            {
                var minMax = await SourceKeyMinMaxAsync(plan, keys[0], ct).ConfigureAwait(false);
                if (minMax == null)
                {
                    Emit(items, onItem, PostItemOf(group, "해시", "—", "—", CheckLevels.Skip,
                        "대상에 이관 밖 행이 있어 해시 비교를 건너뜀", mappingId));
                    return;
                }

                var between = PostValidationSql.TargetScopeBetween(keys[0], minMax.Item1, minMax.Item2);
                tgtNote = "범위 안에 이관 밖 행이 있으면 불일치할 수 있음";
                tgtSql = "SELECT SUM(ORA_HASH(" + PostValidationSql.HashConcatExpression(cols) + ")) AS HSUM FROM "
                    + tgtFrom + " WHERE " + between;
                if (rejectKeys != null && rejectKeys.Count > 0 && keyCol != null)
                {
                    var notIn = string.Join(", ", rejectKeys.Select(r => "'" + r.Replace("'", "''") + "'"));
                    tgtSql += " AND TO_CHAR(" + RequireId(keyCol) + ") NOT IN (" + notIn + ")";
                }
            }
            else
            {
                Emit(items, onItem, PostItemOf(group, "해시", "—", "—", CheckLevels.Skip,
                    "대상에 이관 밖 행이 있어 해시 비교를 건너뜀", mappingId));
                return;
            }

            var tgtHash = await ScalarLongAsync(_context.Target, _context.TargetMeta.Schema, tgtSql, ct).ConfigureAwait(false);
            var match = srcHash != null && tgtHash != null && srcHash.Value == tgtHash.Value;
            var level = match ? CheckLevels.Pass : (tgtNote != null ? CheckLevels.Warn : CheckLevels.Error);
            var detail = match
                ? "SUM(ORA_HASH(매핑 열 연결)) 일치 — 거부 행 제외"
                : "해시 불일치 · 원본=" + (srcHash != null ? srcHash.Value.ToString(CultureInfo.InvariantCulture) : "?")
                  + " · 대상=" + (tgtHash != null ? tgtHash.Value.ToString(CultureInfo.InvariantCulture) : "?");
            if (tgtNote != null && !match)
            {
                detail += " · " + tgtNote;
            }

            Emit(items, onItem, PostItemOf(group, "해시",
                srcHash != null ? srcHash.Value.ToString(CultureInfo.InvariantCulture) : "—",
                tgtHash != null ? tgtHash.Value.ToString(CultureInfo.InvariantCulture) : "—",
                level, detail, mappingId));
        }

        private async Task RunP06Async(List<PostItem> items, Action<PostItem> onItem, PlanItem plan, TaskSnapshot task,
            string group, long baseRows, CancellationToken ct)
        {
            var nullableCols = PickNullCheckColumns(plan);
            if (nullableCols.Count == 0)
            {
                return;
            }

            var targetRowsBefore = await InferTargetRowsBeforeAsync(plan, task, ct).ConfigureAwait(false);
            var fullBucket = targetRowsBefore == 0 && IsEmptyTargetMode(plan.Mapping);
            var keys = ResolveTargetKeys(plan);
            var tgtScope = fullBucket ? null : BuildTargetScopeWhere(plan, task, keys, false, null);
            if (!fullBucket && string.IsNullOrEmpty(tgtScope))
            {
                Emit(items, onItem, PostItemOf(group, "NULL 수", "—", "—", CheckLevels.Skip,
                    "대상 범위를 정할 수 없어 NULL 수 비교를 건너뜀", plan.Key));
                return;
            }

            var srcFrom = PostValidationSql.BuildTransformedFrom(plan.Mapping, _context.SourceMeta.Schema, plan.SourceMetadata, plan.TargetMetadata);
            foreach (var col in nullableCols)
            {
                var cm = plan.Mapping.FindColumn(col.Name);
                var expr = cm != null ? MappingService.ValueSource(cm) : col.Name;
                if (string.IsNullOrWhiteSpace(expr))
                {
                    continue;
                }

                var srcSql = PostValidationSql.NullCountColumn(srcFrom, expr, null);
                var tgtSql = PostValidationSql.NullCountColumn(
                    _context.TargetMeta.Schema + "." + RequireId(plan.Mapping.Target), RequireId(col.Name), tgtScope);
                var srcNulls = await ScalarLongAsync(_context.Source, _context.SourceMeta.Schema, srcSql, ct).ConfigureAwait(false);
                var tgtNulls = await ScalarLongAsync(_context.Target, _context.TargetMeta.Schema, tgtSql, ct).ConfigureAwait(false);
                if (srcNulls == null || tgtNulls == null)
                {
                    continue;
                }

                var level = srcNulls.Value == tgtNulls.Value ? CheckLevels.Pass : CheckLevels.Warn;
                var note = cm != null && !string.IsNullOrWhiteSpace(cm.Expr) ? "변환 후 NULL 수" : "NULL 수";
                Emit(items, onItem, PostItemOf(group, "NULL 수 · " + col.Name,
                    Format.Number(srcNulls.Value), Format.Number(tgtNulls.Value), level,
                    level == CheckLevels.Pass ? note + " 일치" : note + " 불일치", plan.Key));
            }
        }

        private List<ColumnMetadata> PickNullCheckColumns(PlanItem plan)
        {
            var target = plan.TargetMetadata;
            if (target == null || target.Columns == null)
            {
                return new List<ColumnMetadata>();
            }

            var withExpr = new List<ColumnMetadata>();
            var others = new List<ColumnMetadata>();
            foreach (var t in target.Columns)
            {
                if (!t.Nullable)
                {
                    continue;
                }

                var cm = plan.Mapping.FindColumn(t.Name);
                if (cm == null || string.IsNullOrEmpty(MappingService.ValueSource(cm)))
                {
                    continue;
                }

                if (cm != null && !string.IsNullOrWhiteSpace(cm.Expr))
                {
                    withExpr.Add(t);
                }
                else
                {
                    others.Add(t);
                }
            }

            var list = withExpr.Concat(others).Take(3).ToList();
            return list;
        }

        private async Task<long> FindMissingKeysPresenceAsync(PlanItem plan, IList<string> keys, IList<string> rejectKeys, int maxKeys, CancellationToken ct)
        {
            var srcFrom = PostValidationSql.BuildTransformedFrom(plan.Mapping, _context.SourceMeta.Schema, plan.SourceMetadata, plan.TargetMetadata);
            var keyExprs = SourceKeyExpressions(plan, keys);
            var orderCol = keys[0];
            var sql = PostValidationSql.SourceKeysPaged(srcFrom, keyExprs, orderCol, maxKeys);
            var page = await QueryAsync(_context.Source, _context.SourceMeta.Schema, sql, null, maxKeys, ct).ConfigureAwait(false);
            if (page == null || page.Rows == null)
            {
                return 0;
            }

            var reject = new HashSet<string>(rejectKeys ?? Array.Empty<string>(), StringComparer.Ordinal);
            var missing = 0L;
            var batch = new List<string>();
            foreach (var row in page.Rows)
            {
                var keyVal = ReadColumn(page, row, keys[0]);
                if (reject.Contains(keyVal))
                {
                    continue;
                }

                batch.Add(keyVal);
                if (batch.Count >= KeyInBatch)
                {
                    missing += await CountMissingInTargetAsync(plan, keys[0], batch, ct).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                missing += await CountMissingInTargetAsync(plan, keys[0], batch, ct).ConfigureAwait(false);
            }

            return missing;
        }

        private async Task<long> CountMissingInTargetAsync(PlanItem plan, string keyColumn, List<string> keys, CancellationToken ct)
        {
            var sql = PostValidationSql.TargetKeyExistsCount(_context.TargetMeta.Schema, plan.Mapping.Target, keyColumn, keys.Count);
            var binds = new List<SqlBind>();
            for (var i = 0; i < keys.Count; i++)
            {
                binds.Add(new SqlBind { Name = "K" + (i + 1).ToString(CultureInfo.InvariantCulture), Value = keys[i] });
            }

            var found = await ScalarLongAsync(_context.Target, _context.TargetMeta.Schema, sql, ct, binds).ConfigureAwait(false);
            return keys.Count - (found ?? 0);
        }

        private async Task<long> CompareBucketKeysAsync(PlanItem plan, string srcFrom, string srcHash, IList<string> srcKeyExprs, IList<string> keys,
            List<int> buckets, string srcWhere, CancellationToken ct)
        {
            long missing = 0;
            foreach (var bucket in buckets)
            {
                ct.ThrowIfCancellationRequested();
                var keySql = PostValidationSql.KeysInBucket(srcFrom, srcHash, srcKeyExprs, bucket, srcWhere, MaxBucketKeys);
                var srcKeys = await QueryAsync(_context.Source, _context.SourceMeta.Schema, keySql, null, MaxBucketKeys, ct).ConfigureAwait(false);
                if (srcKeys == null || srcKeys.Rows == null)
                {
                    continue;
                }

                var batch = new List<string>();
                foreach (var row in srcKeys.Rows)
                {
                    batch.Add(ReadColumn(srcKeys, row, keys[0]));
                    if (batch.Count >= KeyInBatch)
                    {
                        missing += await CountMissingInTargetAsync(plan, keys[0], batch, ct).ConfigureAwait(false);
                        batch.Clear();
                    }
                }

                if (batch.Count > 0)
                {
                    missing += await CountMissingInTargetAsync(plan, keys[0], batch, ct).ConfigureAwait(false);
                }
            }

            return missing;
        }

        private static List<int> CompareBuckets(Dictionary<int, BucketStat> src, Dictionary<int, BucketStat> tgt)
        {
            var bad = new List<int>();
            var all = new HashSet<int>(src.Keys);
            all.UnionWith(tgt.Keys);
            foreach (var b in all)
            {
                BucketStat s;
                BucketStat t;
                src.TryGetValue(b, out s);
                tgt.TryGetValue(b, out t);
                var sc = s != null ? s.Count : 0;
                var tc = t != null ? t.Count : 0;
                var sh = s != null ? s.HashSum : 0;
                var th = t != null ? t.HashSum : 0;
                if (sc != tc || sh != th)
                {
                    bad.Add(b);
                }
            }

            return bad;
        }

        private async Task<Dictionary<int, BucketStat>> LoadBucketsAsync(ConnectionTarget target, string schema, string sql, CancellationToken ct)
        {
            var result = new Dictionary<int, BucketStat>();
            var query = await QueryAsync(target, schema, sql, null, 2000, ct).ConfigureAwait(false);
            if (query == null || query.Rows == null)
            {
                return result;
            }

            foreach (var row in query.Rows)
            {
                var bucket = ParseInt(ReadColumn(query, row, "BUCKET"));
                if (bucket == null)
                {
                    continue;
                }

                result[bucket.Value] = new BucketStat
                {
                    Count = ParseLong(ReadColumn(query, row, "CNT")) ?? 0,
                    HashSum = ParseLong(ReadColumn(query, row, "HSUM")) ?? 0
                };
            }

            return result;
        }

        private async Task<IList<string>> LoadRejectKeysAsync(PlanItem plan, string runId, string keyColumn, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(plan.ErrorTable) || string.IsNullOrEmpty(keyColumn))
            {
                return Array.Empty<string>();
            }

            var sql = PostValidationSql.RejectKeyColumn(_context.TargetMeta.Schema, plan.ErrorTable, runId, keyColumn);
            var query = await QueryAsync(_context.Target, _context.TargetMeta.Schema, sql, null, 5000, ct).ConfigureAwait(false);
            if (query == null || query.Rows == null)
            {
                return Array.Empty<string>();
            }

            return query.Rows.Select(r => ReadColumn(query, r, "KEY_VAL")).Where(v => v != null).ToList();
        }

        private async Task<Tuple<string, string>> SourceKeyMinMaxAsync(PlanItem plan, string keyColumn, CancellationToken ct)
        {
            var srcFrom = PostValidationSql.BuildTransformedFrom(plan.Mapping, _context.SourceMeta.Schema, plan.SourceMetadata, plan.TargetMetadata);
            var col = RequireId(keyColumn);
            var sql = "SELECT MIN(" + col + ") AS MIN_K, MAX(" + col + ") AS MAX_K FROM " + srcFrom;
            var query = await QueryAsync(_context.Source, _context.SourceMeta.Schema, sql, null, 1, ct).ConfigureAwait(false);
            if (query == null || query.Rows == null || query.Rows.Count == 0)
            {
                return null;
            }

            var min = ReadColumn(query, query.Rows[0], "MIN_K");
            var max = ReadColumn(query, query.Rows[0], "MAX_K");
            if (string.IsNullOrEmpty(min) || string.IsNullOrEmpty(max))
            {
                return null;
            }

            var cm = plan.TargetMetadata != null ? plan.TargetMetadata.FindColumn(keyColumn) : null;
            var ot = OracleType.Parse(cm != null ? cm.Type : null);
            if (ot != null && ot.Base == "NUMBER")
            {
                return Tuple.Create(min, max);
            }

            if (ot != null && (ot.Base == "DATE" || ot.Base == "TIMESTAMP"))
            {
                return Tuple.Create("TO_DATE('" + min.Replace("'", "''") + "','YYYY-MM-DD HH24:MI:SS')",
                    "TO_DATE('" + max.Replace("'", "''") + "','YYYY-MM-DD HH24:MI:SS')");
            }

            return Tuple.Create("'" + min.Replace("'", "''") + "'", "'" + max.Replace("'", "''") + "'");
        }

        private readonly Dictionary<string, long> _targetRowsBeforeCache = new Dictionary<string, long>(StringComparer.Ordinal);

        private async Task<long> InferTargetRowsBeforeAsync(PlanItem plan, TaskSnapshot task, CancellationToken ct)
        {
            long cached;
            if (_targetRowsBeforeCache.TryGetValue(plan.Key, out cached))
            {
                return cached;
            }

            if (string.Equals(plan.Mapping.Mode, WriteModes.TruncateInsert, StringComparison.Ordinal) && BaseRowsFor(plan.Key) == 0)
            {
                _targetRowsBeforeCache[plan.Key] = 0;
                return 0;
            }

            var tgtTotal = await CountOnTargetAsync(
                PostValidationSql.TargetRowCount(_context.TargetMeta.Schema, plan.Mapping.Target, null), ct).ConfigureAwait(false);
            var now = tgtTotal ?? 0;
            var before = Math.Max(0, now - task.Inserted);
            _targetRowsBeforeCache[plan.Key] = before;
            return before;
        }

        private bool IsEmptyTargetMode(MappingModel mapping)
        {
            var mode = mapping != null ? mapping.Mode : WriteModes.InsertOnly;
            return string.Equals(mode, WriteModes.InsertOnly, StringComparison.Ordinal)
                || string.Equals(mode, WriteModes.TruncateInsert, StringComparison.Ordinal);
        }

        private bool IsContinuousKey(PlanItem plan, IList<string> keys)
        {
            if (keys == null || keys.Count != 1)
            {
                return false;
            }

            var col = plan.TargetMetadata != null ? plan.TargetMetadata.FindColumn(keys[0]) : null;
            var ot = OracleType.Parse(col != null ? col.Type : null);
            return ot != null && (ot.Base == "NUMBER" || ot.Base == "DATE" || ot.Base == "TIMESTAMP");
        }

        private string BuildTargetScopeWhere(PlanItem plan, TaskSnapshot task, IList<string> keys, bool fullTable, IList<string> rejectKeys)
        {
            if (fullTable)
            {
                return rejectKeys != null && rejectKeys.Count > 0 && keys.Count > 0
                    ? "TO_CHAR(" + RequireId(keys[0]) + ") NOT IN (" + string.Join(", ", rejectKeys.Select(r => "'" + r.Replace("'", "''") + "'")) + ")"
                    : null;
            }

            return null;
        }

        private string BuildSourceRejectWhere(string keyColumn, IList<string> rejectKeys)
        {
            if (rejectKeys == null || rejectKeys.Count == 0)
            {
                return null;
            }

            return "TO_CHAR(" + RequireId(keyColumn) + ") NOT IN (" + string.Join(", ", rejectKeys.Select(r => "'" + r.Replace("'", "''") + "'")) + ")";
        }

        private List<string> SourceKeyExpressions(PlanItem plan, IList<string> targetKeys)
        {
            var list = new List<string>();
            foreach (var k in targetKeys)
            {
                var cm = plan.Mapping.FindColumn(k);
                var expr = cm != null ? MappingService.ValueSource(cm) : k;
                list.Add(string.IsNullOrWhiteSpace(expr) ? RequireId(k) : expr);
            }

            return list;
        }

        private List<string> ResolveTargetKeys(PlanItem plan)
        {
            var m = plan.Mapping;
            if (m != null && m.MergeKey != null && m.MergeKey.Count > 0)
            {
                return m.MergeKey.ToList();
            }

            var pk = plan.TargetMetadata != null
                ? plan.TargetMetadata.Columns.Where(c => c.PrimaryKey).Select(c => c.Name).ToList()
                : new List<string>();
            return pk;
        }

        private long BaseRowsFor(string taskKey)
        {
            long v;
            if (_context.BaseRows != null && _context.BaseRows.TryGetValue(taskKey, out v))
            {
                return v;
            }

            return 0;
        }

        private static bool IsSkipStatus(string status)
        {
            return string.Equals(status, "wait", StringComparison.Ordinal)
                || string.Equals(status, "skipped", StringComparison.Ordinal);
        }

        private bool IsLarge(PlanItem plan, TaskSnapshot task)
        {
            if (task.Total >= LargeTableRows)
            {
                return true;
            }

            return plan.SourceMetadata != null && plan.SourceMetadata.Rows != null && plan.SourceMetadata.Rows.Value >= LargeTableRows;
        }

        private async Task<long?> CountOnSourceAsync(PlanItem plan, CancellationToken ct)
        {
            var sql = PostValidationSql.SourceScopeCount(plan.Mapping, _context.SourceMeta.Schema);
            return await CountAsync(_context.Source, _context.SourceMeta.Schema, sql, ct).ConfigureAwait(false);
        }

        private Task<long?> CountOnTargetAsync(string sql, CancellationToken ct)
        {
            return CountAsync(_context.Target, _context.TargetMeta.Schema, sql, ct);
        }

        private async Task<long?> CountAsync(ConnectionTarget target, string schema, string sql, CancellationToken ct)
        {
            OracleSelectGuard.EnsureSelectOnly(sql);
            var query = await QueryAsync(target, schema, sql, null, 1, ct).ConfigureAwait(false);
            if (query == null || query.Rows == null || query.Rows.Count == 0)
            {
                return null;
            }

            var row = query.Rows[0];
            var named = ParseLong(ReadColumn(query, row, "CNT"));
            if (named != null)
            {
                return named;
            }

            return ParseLong(ReadColumn(query, row, 0));
        }

        private async Task<long?> ScalarLongAsync(ConnectionTarget target, string schema, string sql, CancellationToken ct, IList<SqlBind> binds = null)
        {
            var query = await QueryAsync(target, schema, sql, binds, 1, ct).ConfigureAwait(false);
            if (query == null || query.Rows == null || query.Rows.Count == 0)
            {
                return null;
            }

            return ParseLong(ReadColumn(query, query.Rows[0], 0));
        }

        private async Task<QueryResult> QueryAsync(ConnectionTarget target, string schema, string sql, IList<SqlBind> binds, int maxRows, CancellationToken ct)
        {
            OracleSelectGuard.EnsureSelectOnly(sql);
            return await WithTimeout(token => _adapter.QueryAsync(target, schema, sql, binds, maxRows, token), ct).ConfigureAwait(false);
        }

        private static async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                linked.CancelAfter(QueryTimeout);
                try
                {
                    return await work(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw;
                    }

                    throw new TimeoutException("질의 시간 초과(10분)");
                }
            }
        }

        private static void Emit(List<PostItem> items, Action<PostItem> onItem, PostItem item)
        {
            items.Add(item);
            if (onItem != null)
            {
                onItem(item);
            }
        }

        private static PostItem PostItemOf(string group, string check, string source, string target, string level, string detail, string mappingId)
        {
            return new PostItem
            {
                Group = group,
                Check = check,
                Source = source ?? "",
                Target = target ?? "",
                Level = level,
                Detail = detail,
                MappingId = mappingId
            };
        }

        private static string ReadColumn(QueryResult query, string[] row, string name)
        {
            var i = ColIndex(query, name);
            return i >= 0 && i < row.Length ? row[i] : null;
        }

        private static string ReadColumn(QueryResult query, string[] row, int index)
        {
            return index >= 0 && index < row.Length ? row[index] : null;
        }

        private static int ColIndex(QueryResult query, string name)
        {
            for (var i = 0; i < query.Columns.Count; i++)
            {
                if (string.Equals(query.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string NormalizeCell(string value)
        {
            if (value == null)
            {
                return "";
            }

            return value.Trim();
        }

        private static string Trunc(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            return s.Length <= 40 ? s : s.Substring(0, 37) + "...";
        }

        private static long? ParseLong(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return null;
            }

            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }

            if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                return (long)d;
            }

            return null;
        }

        private static int? ParseInt(string s)
        {
            var l = ParseLong(s);
            return l != null ? (int?)l.Value : null;
        }

        private static string RequireId(string name)
        {
            return (name ?? "").Trim().ToUpperInvariant();
        }

        private sealed class BucketStat
        {
            public long Count;
            public long HashSum;
        }
    }
}
