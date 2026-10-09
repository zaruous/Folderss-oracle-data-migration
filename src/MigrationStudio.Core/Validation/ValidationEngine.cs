using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Sql;
using MigrationStudio.Core.Text;
using MigrationStudio.Core.Types;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Validation
{
    public sealed class ValidationEngine
    {
        private static readonly string[] SqlChecks =
        {
            "SQL 구문", "원본 객체·열", "결과 열 수", "별칭", "바인드 변수", "체크포인트"
        };

        private static readonly Regex TruncRx = new Regex(@"잘림", RegexOptions.CultureInvariant);
        private static readonly Regex PrecRx = new Regex(@"정수부|소수부|정밀도", RegexOptions.CultureInvariant);
        private static readonly Regex NnRx = new Regex(@"NOT NULL|NULL이 오면|NULL \d|NULL [\d,]+행|빈 문자열", RegexOptions.CultureInvariant);

        private const long SampleRowThreshold = 1_000_000;
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

        private readonly IDatabaseAdapter _adapter;

        public ValidationEngine(IDatabaseAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        public async Task<List<ValidationItem>> RunPreAsync(
            ValidationContext ctx,
            Action<ValidationItem> onItem,
            CancellationToken ct)
        {
            var items = new List<ValidationItem>();
            if (ctx == null)
            {
                return items;
            }

            var job = ctx.Job;
            var used = (job != null && job.Mappings != null)
                ? job.Mappings.Where(m => m.Use).ToList()
                : new List<MappingModel>();
            var tables = used.Where(m => !m.IsSql).ToList();
            var sqls = used.Where(m => m.IsSql).ToList();

            await RunConnectionAsync(ctx, items, onItem, ct).ConfigureAwait(false);

            var srcMeta = ctx.SourceMeta;
            var tgtMeta = ctx.TargetMeta;
            if (srcMeta == null || tgtMeta == null)
            {
                Emit(items, onItem, Item("객체", "메타데이터", "", CheckLevels.Error,
                    "원본·대상 메타데이터를 먼저 불러오세요", Fix("connection")));
                return items;
            }

            await RunObjectsAsync(ctx, items, onItem, used, tables, srcMeta, tgtMeta, ct).ConfigureAwait(false);
            if (used.Count == 0)
            {
                return items;
            }

            await RunSqlMappingsAsync(ctx, items, onItem, sqls, srcMeta, tgtMeta, ct).ConfigureAwait(false);
            EmitIncrementalChecks(ctx, items, onItem, used, srcMeta);
            await EmitSyncRunChecksAsync(ctx, items, onItem, ct).ConfigureAwait(false);

            Dictionary<string, Dictionary<string, ColumnStats>> measured = null;
            if (ctx.Source != null)
            {
                var profiler = new ExpressionProfiler(_adapter);
                measured = await profiler.ProfileAsync(ctx, used, m => SourceOf(m, srcMeta, ctx), ct).ConfigureAwait(false);
            }

            var issueBuckets = await RunColumnMappingsAsync(ctx, items, onItem, used, srcMeta, tgtMeta, null, ct).ConfigureAwait(false);
            MergeMeasuredIssues(issueBuckets, used, srcMeta, tgtMeta, ctx, measured);
            await RunFormatAsync(items, onItem, issueBuckets, measured).ConfigureAwait(false);
            await RunConstraintsAsync(ctx, items, onItem, used, srcMeta, tgtMeta, measured, ct).ConfigureAwait(false);
            await RunPlanningAsync(ctx, items, onItem, used, srcMeta, tgtMeta, ct).ConfigureAwait(false);

            return items;
        }

        private async Task RunConnectionAsync(
            ValidationContext ctx,
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            CancellationToken ct)
        {
            await RunConnectionRoleAsync(ctx, items, onItem, true, ct).ConfigureAwait(false);
            await RunConnectionRoleAsync(ctx, items, onItem, false, ct).ConfigureAwait(false);
        }

        private async Task RunConnectionRoleAsync(
            ValidationContext ctx,
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            bool source,
            CancellationToken ct)
        {
            var label = source ? "원본 접속" : "대상 접속";
            var profile = source ? ctx.SourceProfile : ctx.TargetProfile;
            var target = source ? ctx.Source : ctx.Target;
            if (profile == null || target == null)
            {
                Emit(items, onItem, Item("접속", label, "", CheckLevels.Error,
                    "마이그레이션 설정에서 접속을 고르세요", Fix("connection")));
                return;
            }

            if (!source && profile.WriteBlocked)
            {
                Emit(items, onItem, Item("접속", label, profile.Name, CheckLevels.Error,
                    "\"쓰기 금지\"로 설정된 접속은 대상으로 쓸 수 없습니다(마이그레이션 설정)", Fix("connection")));
                return;
            }

            var cached = source ? ctx.SourceTest : ctx.TargetTest;
            ConnectionTestResult result;
            if (cached != null)
            {
                result = cached;
            }
            else
            {
                try
                {
                    result = await WithQueryTimeout(
                        token => _adapter.TestAsync(target, source, token),
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Emit(items, onItem, Item("접속", label, profile.Name, CheckLevels.Error, ex.Message, Fix("connection")));
                    return;
                }
            }

            if (result.Ok)
            {
                var detail = (result.Version ?? "Oracle") + " · "
                    + (result.LatencyMs != null ? result.LatencyMs.Value.ToString(CultureInfo.InvariantCulture) : "?")
                    + " ms · " + profile.Host + ":" + profile.Port.ToString(CultureInfo.InvariantCulture) + "/" + profile.Service
                    + (source ? " · 읽기 전용(SELECT만)" : "");
                Emit(items, onItem, Item("접속", label, profile.Name, CheckLevels.Pass, detail, null));
            }
            else
            {
                Emit(items, onItem, Item("접속", label, profile.Name, CheckLevels.Error,
                    result.Error ?? "접속 실패", Fix("connection")));
            }
        }

        private async Task RunObjectsAsync(
            ValidationContext ctx,
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            List<MappingModel> used,
            List<MappingModel> tables,
            SchemaMetadata srcMeta,
            SchemaMetadata tgtMeta,
            CancellationToken ct)
        {
            var missingSrc = tables.Where(m => srcMeta.FindTable(m.Source) == null).Select(m => m.Source).ToList();
            if (tables.Count > 0)
            {
                var level = missingSrc.Count > 0 ? CheckLevels.Error : CheckLevels.Pass;
                var detail = missingSrc.Count > 0
                    ? "ORA-00942: 없음 — " + string.Join(", ", missingSrc) + " — 메타데이터를 다시 불러오세요"
                    : string.Join(", ", tables.Select(m => m.Source)) + " (" + tables.Count.ToString(CultureInfo.InvariantCulture) + "개)";
                Emit(items, onItem, Item("객체", "원본 테이블 존재", srcMeta.Schema, level, detail,
                    missingSrc.Count > 0 ? Fix("tables") : null));
            }

            if (ctx.Source != null && tables.Count > 0)
            {
                await VerifyExistingAsync(ctx, items, onItem, srcMeta.Schema, tables.Select(m => m.Source), missingSrc, true, ct)
                    .ConfigureAwait(false);
            }

            var targets = used.Select(m => m.Target).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToList();
            var missingTgt = targets.Where(n => tgtMeta.FindTable(n) == null).ToList();
            var noTarget = used.Any(m => string.IsNullOrEmpty(m.Target));
            var tgtLevel = missingTgt.Count > 0 || noTarget ? CheckLevels.Error : CheckLevels.Pass;
            var tgtDetail = missingTgt.Count > 0
                ? "ORA-00942: 없음 — " + string.Join(", ", missingTgt) + " — 메타데이터를 다시 불러오세요"
                : noTarget
                    ? "대상 테이블을 고르지 않은 매핑이 있습니다"
                    : string.Join(", ", targets);
            Emit(items, onItem, Item("객체", "대상 테이블 존재", tgtMeta.Schema, tgtLevel, tgtDetail,
                missingTgt.Count > 0 ? Fix("tables") : null));

            if (ctx.Target != null && targets.Count > 0)
            {
                await VerifyExistingAsync(ctx, items, onItem, tgtMeta.Schema, targets, missingTgt, false, ct)
                    .ConfigureAwait(false);
            }

            if (used.Count == 0)
            {
                Emit(items, onItem, Item("객체", "이관 대상", "", CheckLevels.Error, "사용하는 매핑이 없습니다", Fix("tables")));
            }
        }

        private async Task VerifyExistingAsync(
            ValidationContext ctx,
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            string schema,
            IEnumerable<string> names,
            IList<string> alreadyMissing,
            bool source,
            CancellationToken ct)
        {
            try
            {
                var chunks = ValidationSql.ExistingTables(schema, names);
                var found = new HashSet<string>(StringComparer.Ordinal);
                foreach (var sql in chunks)
                {
                    var qr = await SafeQueryAsync(source ? ctx.Source : ctx.Target, schema, sql, ct).ConfigureAwait(false);
                    if (qr == null)
                    {
                        return;
                    }

                    foreach (var row in qr.Rows ?? new List<string[]>())
                    {
                        if (row.Length > 0)
                        {
                            found.Add(row[0].ToUpperInvariant());
                        }
                    }
                }

                var expected = names.Select(n => n.ToUpperInvariant()).Distinct().ToList();
                var dbMissing = expected.Where(n => !found.Contains(n) && (alreadyMissing == null || !alreadyMissing.Contains(n, StringComparer.OrdinalIgnoreCase))).ToList();
                if (dbMissing.Count == 0)
                {
                    return;
                }

                var label = source ? "원본 테이블 존재" : "대상 테이블 존재";
                Emit(items, onItem, Item("객체", label, schema, CheckLevels.Error,
                    "ORA-00942: 없음 — " + string.Join(", ", dbMissing) + " — 메타데이터를 다시 불러오세요", Fix("tables")));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }

        private async Task RunSqlMappingsAsync(
            ValidationContext ctx,
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            List<MappingModel> sqls,
            SchemaMetadata srcMeta,
            SchemaMetadata tgtMeta,
            CancellationToken ct)
        {
            if (sqls.Count == 0 || ctx.Source == null)
            {
                foreach (var m in sqls)
                {
                    if (ctx.Source == null)
                    {
                        Emit(items, onItem, Item("매핑", "원본 SQL", LabelOf(m), CheckLevels.Error,
                            "원본 접속이 없어 SQL을 검증할 수 없습니다", Fix("sql", m.Id)));
                    }
                }

                return;
            }

            var svc = new SqlSourceService(_adapter);
            foreach (var m in sqls)
            {
                SqlValidationResult v;
                try
                {
                    v = await svc.ValidateAsync(m, ctx.Source, srcMeta.Schema, srcMeta, tgtMeta, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Emit(items, onItem, Item("매핑", "원본 SQL", LabelOf(m), CheckLevels.Error, ex.Message, Fix("sql", m.Id)));
                    continue;
                }

                var own = v.Items.Where(i => SqlChecks.Contains(i.Check)).ToList();
                var bad = own.Where(i => i.Level != CheckLevels.Pass).ToList();
                var level = CheckLevels.Worst(own.Select(i => i.Level));
                string detail;
                if (bad.Count > 0)
                {
                    detail = string.Join("\n", bad.Select(i => i.Check + ": " + (i.Detail ?? "").Split('\n')[0]));
                }
                else
                {
                    var binds = m.Binds != null ? string.Join(", ", m.Binds.Select(b => ":" + b.Name)) : "없음";
                    if (string.IsNullOrEmpty(binds))
                    {
                        binds = "없음";
                    }

                    detail = "구문·객체·별칭 통과 · 결과 열 " + (v.Columns != null ? v.Columns.Count.ToString(CultureInfo.InvariantCulture) : "0")
                        + "개 · 바인드 " + binds;
                }

                Emit(items, onItem, Item("매핑", "원본 SQL", LabelOf(m), level, detail, Fix("sql", m.Id), m.Id));
            }
        }

        private sealed class IssueEntry
        {
            public MappingModel Mapping;
            public TableMetadata Source;
            public ColumnResult Result;
            public CheckMessage Message;
        }

        private sealed class IssueBuckets
        {
            public List<IssueEntry> Compat = new List<IssueEntry>();
            public List<IssueEntry> Trunc = new List<IssueEntry>();
            public List<IssueEntry> Prec = new List<IssueEntry>();
            public List<IssueEntry> Nn = new List<IssueEntry>();
        }

        private async Task<IssueBuckets> RunColumnMappingsAsync(
            ValidationContext ctx,
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            List<MappingModel> used,
            SchemaMetadata srcMeta,
            SchemaMetadata tgtMeta,
            Dictionary<string, Dictionary<string, ColumnStats>> measured,
            CancellationToken ct)
        {
            var buckets = new IssueBuckets();
            foreach (var tm in used)
            {
                var s = SourceOf(tm, srcMeta, ctx);
                var t = tgtMeta.FindTable(tm.Target);
                if (s == null || t == null || (s.IsVirtual && (s.Columns == null || s.Columns.Count == 0)))
                {
                    continue;
                }

                IReadOnlyDictionary<string, ColumnStats> mapMeasured = null;
                if (measured != null && measured.TryGetValue(tm.Id, out var md))
                {
                    mapMeasured = md;
                }

                var st = MappingService.Status(tm, s, t, mapMeasured);
                var unmapped = st.Results
                    .Where(r => string.IsNullOrEmpty(MappingService.ValueSource(r.Mapping))
                        && !(r.Mapping.DefaultValue != null && r.Mapping.NullRule != NullRules.Allow))
                    .Select(r => r.Target.Name + (r.Check.Level == CheckLevels.Error ? "(NOT NULL — 제약 참고)" : ""))
                    .ToList();
                var keyMissing = WriteModes.Of(tm.Mode).NeedsKey && (tm.MergeKey == null || tm.MergeKey.Count == 0);
                var colLevel = keyMissing ? CheckLevels.Error : unmapped.Count > 0 ? CheckLevels.Info : CheckLevels.Pass;
                var colDetail = st.Mapped + " / " + st.Total + " 컬럼"
                    + (unmapped.Count > 0 ? " · 비워 둠: " + string.Join(", ", unmapped) : "")
                    + (keyMissing ? " · " + WriteModes.Of(tm.Mode).Label + "에 병합 키가 없음" : "");
                Emit(items, onItem, Item("매핑", "컬럼 매핑", LabelOf(tm), colLevel, colDetail, Fix("columns", tm.Id), tm.Id));

                foreach (var r in st.Results)
                {
                    foreach (var msg in r.Check.Messages)
                    {
                        if (msg.Level == CheckLevels.Pass || msg.Level == CheckLevels.Info)
                        {
                            continue;
                        }

                        var entry = new IssueEntry { Mapping = tm, Source = s, Result = r, Message = msg };
                        if (NnRx.IsMatch(msg.Message))
                        {
                            buckets.Nn.Add(entry);
                        }
                        else if (TruncRx.IsMatch(msg.Message))
                        {
                            buckets.Trunc.Add(entry);
                        }
                        else if (PrecRx.IsMatch(msg.Message))
                        {
                            buckets.Prec.Add(entry);
                        }
                        else
                        {
                            buckets.Compat.Add(entry);
                        }
                    }
                }
            }

            await Task.CompletedTask;
            return buckets;
        }

        private static void MergeMeasuredIssues(
            IssueBuckets buckets,
            IList<MappingModel> used,
            SchemaMetadata srcMeta,
            SchemaMetadata tgtMeta,
            ValidationContext ctx,
            Dictionary<string, Dictionary<string, ColumnStats>> measured)
        {
            if (measured == null)
            {
                return;
            }

            foreach (var tm in used)
            {
                var s = SourceOf(tm, srcMeta, ctx);
                var t = tgtMeta.FindTable(tm.Target);
                if (s == null || t == null)
                {
                    continue;
                }

                measured.TryGetValue(tm.Id, out var mapMeasured);
                var st = MappingService.Status(tm, s, t, mapMeasured);
                foreach (var r in st.Results)
                {
                    foreach (var msg in r.Check.Messages)
                    {
                        if (msg.Level == CheckLevels.Pass || msg.Level == CheckLevels.Info)
                        {
                            continue;
                        }

                        var entry = new IssueEntry { Mapping = tm, Source = s, Result = r, Message = msg };
                        if (NnRx.IsMatch(msg.Message))
                        {
                            if (!buckets.Nn.Any(x => x.Result.Target.Name == r.Target.Name && x.Mapping.Id == tm.Id))
                            {
                                buckets.Nn.Add(entry);
                            }
                        }
                        else if (TruncRx.IsMatch(msg.Message))
                        {
                            if (!buckets.Trunc.Any(x => x.Result.Target.Name == r.Target.Name && x.Mapping.Id == tm.Id))
                            {
                                buckets.Trunc.Add(entry);
                            }
                        }
                        else if (PrecRx.IsMatch(msg.Message))
                        {
                            if (!buckets.Prec.Any(x => x.Result.Target.Name == r.Target.Name && x.Mapping.Id == tm.Id))
                            {
                                buckets.Prec.Add(entry);
                            }
                        }
                        else if (!buckets.Compat.Any(x => x.Result.Target.Name == r.Target.Name && x.Mapping.Id == tm.Id))
                        {
                            buckets.Compat.Add(entry);
                        }
                    }
                }
            }
        }

        private Task RunFormatAsync(
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            IssueBuckets buckets,
            Dictionary<string, Dictionary<string, ColumnStats>> measured)
        {
            if (buckets.Compat.Count > 0)
            {
                foreach (var e in buckets.Compat)
                {
                    Emit(items, onItem, FormatItem("데이터 형식 호환성", e, null));
                }
            }
            else
            {
                Emit(items, onItem, Item("형식", "데이터 형식 호환성", "", CheckLevels.Pass, "매핑한 열의 형식이 모두 호환됨", null));
            }

            if (buckets.Trunc.Count > 0)
            {
                foreach (var e in buckets.Trunc)
                {
                    var sample = SampleTag(e.Source, e.Mapping);
                    Emit(items, onItem, FormatItem("VARCHAR 길이", e, sample));
                }
            }
            else
            {
                Emit(items, onItem, Item("형식", "VARCHAR 길이", "", CheckLevels.Pass, "문자 열 길이가 모두 충분함", null));
            }

            if (buckets.Prec.Count > 0)
            {
                foreach (var e in buckets.Prec)
                {
                    var sample = SampleTag(e.Source, e.Mapping);
                    Emit(items, onItem, FormatItem("NUMBER 정밀도", e, sample));
                }
            }
            else
            {
                Emit(items, onItem, Item("형식", "NUMBER 정밀도", "", CheckLevels.Pass, "숫자 정밀도가 모두 충분함", null));
            }

            return Task.CompletedTask;
        }

        private ValidationItem FormatItem(string check, IssueEntry e, string sample)
        {
            var info = MappingService.SourceInfo(e.Result.Mapping, e.Source.Columns);
            var typeLine = (e.Result.Mapping.Source ?? "식") + " "
                + ((info != null && info.Type != null) ? info.Type : "?") + " → "
                + e.Result.Target.Name + " " + e.Result.Target.Type;
            var msg = e.Message.Message;
            if (check == "VARCHAR 길이")
            {
                msg = Regex.Replace(msg, @"^.*?잘림 위험", "잘림 위험(Truncation Risk)");
            }

            return Item("형식", check, e.Mapping.Target, e.Message.Level, typeLine + "\n" + msg,
                Fix("columns", e.Mapping.Id, e.Result.Target.Name), e.Mapping.Id, sample);
        }

        private async Task RunConstraintsAsync(
            ValidationContext ctx,
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            List<MappingModel> used,
            SchemaMetadata srcMeta,
            SchemaMetadata tgtMeta,
            Dictionary<string, Dictionary<string, ColumnStats>> measured,
            CancellationToken ct)
        {
            var nnIssues = new List<IssueEntry>();
            foreach (var tm in used)
            {
                var s = SourceOf(tm, srcMeta, ctx);
                var t = tgtMeta.FindTable(tm.Target);
                if (s == null || t == null)
                {
                    continue;
                }

                IReadOnlyDictionary<string, ColumnStats> mapMeasured = null;
                if (measured != null && measured.TryGetValue(tm.Id, out var md))
                {
                    mapMeasured = md;
                }

                var st = MappingService.Status(tm, s, t, mapMeasured);
                foreach (var r in st.Results)
                {
                    if (!r.Target.Nullable && r.Check.Level == CheckLevels.Error)
                    {
                        var err = r.Check.Messages.FirstOrDefault(m => m.Level == CheckLevels.Error);
                        if (err != null)
                        {
                            nnIssues.Add(new IssueEntry { Mapping = tm, Source = s, Result = r, Message = err });
                            continue;
                        }
                    }

                    foreach (var msg in r.Check.Messages)
                    {
                        if (msg.Level == CheckLevels.Pass || msg.Level == CheckLevels.Info)
                        {
                            continue;
                        }

                        if (NnRx.IsMatch(msg.Message))
                        {
                            nnIssues.Add(new IssueEntry { Mapping = tm, Source = s, Result = r, Message = msg });
                        }
                    }
                }
            }

            if (nnIssues.Count > 0)
            {
                foreach (var e in nnIssues)
                {
                    var sample = SampleTag(e.Source, e.Mapping);
                    Emit(items, onItem, Item("제약", "NOT NULL", e.Mapping.Target + "." + e.Result.Target.Name,
                        e.Message.Level, e.Message.Message, Fix("columns", e.Mapping.Id, e.Result.Target.Name), e.Mapping.Id, sample));
                }
            }
            else
            {
                Emit(items, onItem, Item("제약", "NOT NULL", "", CheckLevels.Pass, "NOT NULL 컬럼에 모두 값이 들어감", null));
            }

            foreach (var tm in used)
            {
                var t = tgtMeta.FindTable(tm.Target);
                var s = SourceOf(tm, srcMeta, ctx);
                if (t == null || s == null || (s.IsVirtual && (s.Columns == null || s.Columns.Count == 0)))
                {
                    continue;
                }

                await EmitPkAsync(items, onItem, tm, s, t, ct).ConfigureAwait(false);
                await EmitDuplicateAsync(ctx, items, onItem, tm, s, t, ct).ConfigureAwait(false);
                await EmitFksAsync(items, onItem, tm, t, used, tgtMeta, ctx, srcMeta, ct).ConfigureAwait(false);
            }
        }

        private Task EmitPkAsync(
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            MappingModel tm,
            TableMetadata s,
            TableMetadata t,
            CancellationToken ct)
        {
            var pk = t.Columns.Where(c => c.PrimaryKey).Select(c => c.Name).ToList();
            var keys = tm.MergeKey ?? new List<string>();
            var needKey = WriteModes.Of(tm.Mode).NeedsKey;
            var pkMapped = pk.All(k =>
            {
                var c = tm.FindColumn(k);
                return c != null && !string.IsNullOrEmpty(MappingService.ValueSource(c));
            });
            var level = pkMapped ? CheckLevels.Pass : CheckLevels.Error;
            var detail = "대상 PK " + string.Join(", ", pk)
                + (pkMapped
                    ? " ← " + string.Join(", ", pk.Select(k =>
                    {
                        var c = tm.FindColumn(k);
                        return c != null ? (c.Source ?? "식") : "?";
                    }))
                    : " 매핑 없음");
            if (needKey && keys.Count > 0 && string.Join(",", keys) != string.Join(",", pk))
            {
                level = CheckLevels.Worst(new[] { level, CheckLevels.Warn });
                detail += "\n병합 키(" + string.Join(", ", keys) + ")가 PK가 아님 — 한 행이 여러 대상 행과 맞으면 ORA-30926";
            }

            Emit(items, onItem, Item("제약", "PK / Unique Key", tm.Target, level, detail, Fix("columns", tm.Id), tm.Id));
            return Task.CompletedTask;
        }

        private async Task EmitDuplicateAsync(
            ValidationContext ctx,
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            MappingModel tm,
            TableMetadata s,
            TableMetadata t,
            CancellationToken ct)
        {
            var keys = tm.MergeKey ?? new List<string>();
            if (keys.Count == 0)
            {
                keys = t.Columns.Where(c => c.PrimaryKey).Select(c => c.Name).ToList();
            }

            var sample = SampleTag(s, tm);
            var sqlCap = tm.IsSql;
            var useSample = !tm.IsSql && s.Rows != null && s.Rows.Value > SampleRowThreshold;

            if (string.Equals(tm.Mode, WriteModes.InsertOnly, StringComparison.Ordinal))
            {
                long? rows = t.Rows;
                if (rows == null && ctx.Target != null)
                {
                    var sql = ValidationSql.TargetRowCount(ctx.TargetMeta.Schema, tm.Target);
                    rows = await SafeCountAsync(ctx.Target, ctx.TargetMeta.Schema, sql, ct).ConfigureAwait(false);
                }

                if (rows != null && rows.Value > 0)
                {
                    Emit(items, onItem, Item("제약", "중복 키", tm.Target, CheckLevels.Warn,
                        "대상에 이미 " + Format.Number(rows.Value) + "행 있음 — INSERT ONLY면 같은 키는 ORA-00001로 거부됨. INSERT + UPDATE를 검토하세요",
                        Fix("tables", tm.Id), tm.Id, null));
                    return;
                }
            }

            if (ctx.Source == null || keys.Count == 0)
            {
                Emit(items, onItem, Item("제약", "중복 키", tm.IsSql ? "SQL " + tm.Source : tm.Source, CheckLevels.Pass,
                    (tm.IsSql ? "원본 SQL 결과의 " : "원본 ") + string.Join(", ", keys) + " 중복 0건"
                    + (tm.IsSql ? "(SQL을 감싼 GROUP BY 집계 — JOIN이 행을 늘리지 않음)" : "")
                    + (string.Equals(tm.Mode, WriteModes.Merge, StringComparison.Ordinal) && t.Rows != null
                        ? " · 대상 기존 " + Format.Number(t.Rows.Value) + "행은 갱신됨" : ""),
                    Fix("tables", tm.Id), tm.Id, sample));
                return;
            }

            var dupSql = ValidationSql.DuplicateKeys(tm, ctx.SourceMeta.Schema, MapKeyColumns(tm, keys), useSample, sqlCap);
            var dupCount = 0;
            try
            {
                var qr = await SafeQueryAsync(ctx.Source, ctx.SourceMeta.Schema, dupSql, ct).ConfigureAwait(false);
                dupCount = qr != null && qr.Rows != null ? qr.Rows.Count : 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                dupCount = 0;
            }

            if (dupCount > 0)
            {
                Emit(items, onItem, Item("제약", "중복 키", tm.Target, CheckLevels.Error,
                    "원본 병합 키 중복 " + dupCount.ToString(CultureInfo.InvariantCulture) + "건 — MERGE 시 ORA-30926",
                    Fix("columns", tm.Id), tm.Id, sample));
            }
            else
            {
                var srcKey = s.IsVirtual
                    ? string.Join(", ", keys.Select(k =>
                    {
                        var c = tm.FindColumn(k);
                        return c != null ? c.Source : null;
                    }).Where(x => !string.IsNullOrEmpty(x)))
                    : string.Join(", ", s.Columns.Where(c => c.PrimaryKey).Select(c => c.Name));
                if (string.IsNullOrEmpty(srcKey))
                {
                    srcKey = "키";
                }

                var passDetail = (s.IsVirtual ? "원본 SQL 결과의 " : "원본 ") + srcKey + " 중복 0건"
                    + (s.IsVirtual ? "(SQL을 감싼 GROUP BY 집계 — JOIN이 행을 늘리지 않음)" : "")
                    + (string.Equals(tm.Mode, WriteModes.Merge, StringComparison.Ordinal) && t.Rows != null
                        ? " · 대상 기존 " + Format.Number(t.Rows.Value) + "행은 갱신됨" : "");
                if (useSample)
                {
                    passDetail += " · 표본 5% 기준";
                }

                Emit(items, onItem, Item("제약", "중복 키", tm.IsSql ? "SQL " + tm.Source : tm.Source, CheckLevels.Pass,
                    passDetail, null, tm.Id, useSample ? "표본 5%" : null));
            }
        }

        private async Task EmitFksAsync(
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            MappingModel tm,
            TableMetadata t,
            List<MappingModel> used,
            SchemaMetadata tgtMeta,
            ValidationContext ctx,
            SchemaMetadata srcMeta,
            CancellationToken ct)
        {
            foreach (var fk in t.ForeignKeys ?? new List<ForeignKeyMetadata>())
            {
                var parent = used.FirstOrDefault(x => string.Equals(x.Target, fk.RefTable, StringComparison.OrdinalIgnoreCase));
                var p = tgtMeta.FindTable(fk.RefTable);
                var level = parent != null || (p != null && p.Rows != null && p.Rows.Value > 0)
                    ? CheckLevels.Pass
                    : CheckLevels.Warn;
                var detail = fk.Name + " → " + fk.RefTable
                    + (parent != null ? " · 부모 작업이 먼저 실행됨"
                        : p != null && p.Rows != null && p.Rows.Value > 0
                            ? " · 부모에 " + Format.Number(p.Rows.Value) + "행 있음"
                            : " · 부모 테이블이 비어 있고 이번 작업에 없음");

                if (level == CheckLevels.Warn && ctx.Source != null && parent == null)
                {
                    var parentSrc = FindParentSourceTable(used, fk.RefTable);
                    if (parentSrc != null)
                    {
                        var orphanSql = ValidationSql.OrphanRows(
                            srcMeta.Schema,
                            tm.Source,
                            fk.Columns,
                            parentSrc,
                            tgtMeta.FindTable(fk.RefTable)?.Columns.Where(c => c.PrimaryKey).Select(c => c.Name).ToList()
                                ?? fk.Columns);
                        var orphans = await SafeCountAsync(ctx.Source, srcMeta.Schema, orphanSql, ct).ConfigureAwait(false);
                        if (orphans != null && orphans.Value > 0)
                        {
                            level = CheckLevels.Warn;
                            detail += " · 고아 " + Format.Number(orphans.Value) + "행";
                        }
                    }
                }

                Emit(items, onItem, Item("제약", "참조 무결성(FK)", tm.Target + "." + string.Join(",", fk.Columns),
                    level, detail, null, tm.Id));
            }
        }

        private static string FindParentSourceTable(List<MappingModel> used, string targetParent)
        {
            var hit = used.FirstOrDefault(m => string.Equals(m.Target, targetParent, StringComparison.OrdinalIgnoreCase));
            return hit != null ? hit.Source : null;
        }

        private static IList<string> MapKeyColumns(MappingModel tm, IList<string> targetKeys)
        {
            var list = new List<string>();
            foreach (var k in targetKeys)
            {
                var cm = tm.FindColumn(k);
                if (cm != null && !string.IsNullOrEmpty(cm.Source))
                {
                    list.Add(cm.Source);
                }
                else if (cm != null && !string.IsNullOrWhiteSpace(cm.Expr))
                {
                    list.Add(cm.Expr.Trim());
                }
                else
                {
                    list.Add(k);
                }
            }

            return list;
        }

        private async Task RunPlanningAsync(
            ValidationContext ctx,
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            List<MappingModel> used,
            SchemaMetadata srcMeta,
            SchemaMetadata tgtMeta,
            CancellationToken ct)
        {
            var plan = PlannedMappings(used, tgtMeta);
            double bytes = 0;
            foreach (var p in plan)
            {
                var t = tgtMeta.FindTable(p.Target);
                var s = SourceOf(p, srcMeta, ctx);
                bytes += ((s != null && s.Rows != null) ? s.Rows.Value : 0) * ((t != null && t.AvgRowLength > 0) ? t.AvgRowLength : 100) * 1.35;
            }

            // 여유 공간은 시시각각 바뀌고 메타데이터는 캐시에서 올 수 있다 — 접속이 있으면 지금 값을 다시 읽고, 못 읽으면 메타데이터의 것을 쓴다
            TablespaceInfo ts = null;
            if (ctx.Target != null)
            {
                ts = await TryLoadTablespaceAsync(ctx, ct).ConfigureAwait(false);
            }

            if (ts == null)
            {
                ts = tgtMeta.Tablespace;
            }

            var needGb = bytes / 1024d / 1024d / 1024d;
            if (ts == null)
            {
                Emit(items, onItem, Item("공간·실행", "대상 테이블스페이스", tgtMeta.Tablespace != null ? tgtMeta.Tablespace.Name : "—",
                    CheckLevels.Info,
                    "테이블스페이스 정보를 읽지 못했습니다 — 대상 계정 권한(USER_FREE_SPACE) 확인",
                    null));
            }
            else
            {
                Emit(items, onItem, TablespaceItem(ts, needGb));
            }

            var byTarget = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var p in plan)
            {
                if (!byTarget.TryGetValue(p.Target, out var labels))
                {
                    labels = new List<string>();
                    byTarget[p.Target] = labels;
                }

                labels.Add(LabelOf(p));
            }

            foreach (var pair in byTarget.Where(x => x.Value.Count > 1))
            {
                Emit(items, onItem, Item("공간·실행", "같은 대상에 쓰는 작업", pair.Key, CheckLevels.Warn,
                    string.Join("\n", pair.Value) + "\n나중 작업이 앞 작업의 값을 덮어씁니다 — 하나만 쓰세요", Fix("run")));
            }

            if (ctx.TargetProfile != null && string.Equals(ctx.TargetProfile.Color, "red", StringComparison.OrdinalIgnoreCase))
            {
                var risky = used.Where(m => WriteModes.Of(m.Mode).Destructive).ToList();
                Emit(items, onItem, Item("공간·실행", "운영 DB 쓰기", ctx.TargetProfile.Name,
                    risky.Count > 0 ? CheckLevels.Warn : CheckLevels.Info,
                    risky.Count > 0
                        ? string.Join("\n", risky.Select(m => m.Target + ": " + WriteModes.Of(m.Mode).Label + " — 되돌릴 수 없음, 실행 전에 한 번 더 묻습니다"))
                        : "대상이 운영 DB(빨강)입니다",
                    risky.Count > 0 ? Fix("tables") : null));
            }

            if (jobContinue(ctx))
            {
                var names = plan.Select(p => ErrorTableNamer.Resolve(ctx.Job.Strategy, p.Target))
                    .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToList();
                var existsDetail = "없으면 실행 전에 만듭니다(DBMS_ERRLOG.CREATE_ERROR_LOG) · 거부 행은 RUN_ID와 함께 남음";
                if (ctx.Target != null && names.Count > 0)
                {
                    foreach (var name in names)
                    {
                        var sql = ValidationSql.ErrorTableExists(ctx.TargetMeta.Schema, name);
                        var qr = await SafeQueryAsync(ctx.Target, ctx.TargetMeta.Schema, sql, ct).ConfigureAwait(false);
                        if (qr != null && qr.Rows != null && qr.Rows.Count == 0)
                        {
                            existsDetail = "없으면 실행 전에 만듭니다(DBMS_ERRLOG.CREATE_ERROR_LOG) · 거부 행은 RUN_ID와 함께 남음";
                        }
                    }
                }

                Emit(items, onItem, Item("공간·실행", "오류 테이블", string.Join(", ", names), CheckLevels.Info, existsDetail, null));
            }

            if (ctx.Job != null && ctx.Job.Checkpoints != null)
            {
                foreach (var pair in ctx.Job.Checkpoints)
                {
                    var tm = ctx.Job.Mappings.FirstOrDefault(m => string.Equals(m.Id, pair.Key, StringComparison.Ordinal));
                    if (tm == null || string.Equals(pair.Value.Status, "done", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var cp = pair.Value;
                    var cpVal = cp.Value;
                    long cpNum;
                    if (long.TryParse(cp.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out cpNum))
                    {
                        cpVal = Format.Number(cpNum);
                    }

                    Emit(items, onItem, Item("공간·실행", "체크포인트", LabelOf(tm), CheckLevels.Info,
                        "지난 실행(" + cp.RunId + ")이 " + cp.Column + " = " + cpVal
                        + "에서 멈춤 (" + cp.At + ") — 실행 화면의 [체크포인트에서 재개]로 이어서 할 수 있음",
                        Fix("run", tm.Id), tm.Id));
                }
            }
        }

        /// <summary>
        /// 증분 이관(전략 모드 INCREMENTAL)일 때만 내는 매핑별 검사 "증분 기준". 전체 이관이면 아무것도 내지 않아 기존 결과 모양이 바뀌지 않는다.
        /// 체크포인트 열이 없으면 워터마크를 만들 수 없고, TRUNCATE + INSERT는 워터마크 이후 행만 남기므로 둘 다 ERROR.
        /// </summary>
        private static void EmitIncrementalChecks(
            ValidationContext ctx,
            List<ValidationItem> items,
            Action<ValidationItem> onItem,
            List<MappingModel> used,
            SchemaMetadata srcMeta)
        {
            if (ctx.Job == null || ctx.Job.Strategy == null)
            {
                return;
            }

            var cdc = string.Equals(ctx.Job.Strategy.Mode, ExecutionModes.Cdc, StringComparison.Ordinal);
            if (!cdc && !string.Equals(ctx.Job.Strategy.Mode, ExecutionModes.Incremental, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var tm in used)
            {
                if (string.IsNullOrEmpty(tm.CheckpointColumn))
                {
                    Emit(items, onItem, Item("매핑", "증분 기준", LabelOf(tm), CheckLevels.Error,
                        "체크포인트 열이 없어 워터마크를 만들 수 없음 — 증가하는 키·수정시각 열을 체크포인트로 고르거나 전체 이관으로 실행",
                        Fix("columns", tm.Id), tm.Id));
                    continue;
                }

                if (string.Equals(tm.Mode, WriteModes.TruncateInsert, StringComparison.Ordinal))
                {
                    Emit(items, onItem, Item("매핑", "증분 기준", LabelOf(tm), CheckLevels.Error,
                        "TRUNCATE + INSERT는 워터마크 이후 행만 남겨 대상의 기존 행이 사라짐 — INSERT+UPDATE로 바꾸거나 전체 이관으로 실행",
                        Fix("tables", tm.Id), tm.Id));
                    continue;
                }

                var source = SourceOf(tm, srcMeta, ctx);
                var column = source != null ? source.FindColumn(tm.CheckpointColumn) : null;
                var type = column != null && !string.IsNullOrEmpty(column.Type) ? OracleType.Parse(column.Type) : null;
                var ordered = type != null && (type.IsNumber || type.IsDate);
                // 변경동기화는 같은 행이 수정될 때마다 다시 오므로 INSERT ONLY면 두 번째부터 중복 키 — 1회성 증분보다 한 단계 높여 WARN
                var insertOnly = string.Equals(tm.Mode, WriteModes.InsertOnly, StringComparison.Ordinal);
                var level = !ordered ? CheckLevels.Warn
                    : insertOnly && cdc ? CheckLevels.Warn
                    : insertOnly ? CheckLevels.Info
                    : CheckLevels.Pass;
                var detail = tm.CheckpointColumn + (column != null ? " (" + column.Type + ")" : "") + " > 워터마크 행만 읽음";
                if (!ordered)
                {
                    detail += column == null
                        ? " · 원본에서 열을 찾지 못해 형식을 확인 못 함"
                        : " · 숫자·날짜가 아닌 열은 사전순 비교라 증분 기준으로 어긋날 수 있음";
                }

                if (insertOnly)
                {
                    detail += cdc
                        ? " · 변경동기화에서 INSERT ONLY는 수정된 행이 다시 올 때 중복 키(ORA-00001) — INSERT+UPDATE 권장"
                        : " · INSERT ONLY는 워터마크 이후 행이 모두 새 행일 때만 안전 — 수정된 행도 다시 오면 INSERT+UPDATE";
                }

                if (tm.IsSql)
                {
                    // 조인된 어느 쪽이 바뀌어도 결과 행이 바뀐다 — 수정시각 하나로는 못 잡는다. 사용자가 GREATEST(a.UPD_AT, b.UPD_AT) 같은 결과 열을 만들었을 때만 맞다.
                    level = CheckLevels.Rank(level) < CheckLevels.Rank(CheckLevels.Warn) ? CheckLevels.Warn : level;
                    detail += " · SQL 원본: 조인한 모든 테이블의 변경이 체크포인트 열에 반영돼야 함(예: GREATEST(a.UPD_AT, b.UPD_AT))";
                }

                if (type != null && type.IsDate)
                {
                    detail += " · 지연 창 " + ctx.Job.Strategy.LagSeconds + "초(원본 시각 기준)";
                }
                else if (ordered)
                {
                    detail += " · 숫자 키는 지연 창이 없어 커밋이 늦은 행을 놓칠 수 있음(수정시각 열을 권장)";
                }

                Emit(items, onItem, Item("매핑", "증분 기준", LabelOf(tm), level, detail,
                    level == CheckLevels.Pass ? null : Fix("columns", tm.Id), tm.Id));
            }
        }

        /// <summary>
        /// 변경동기화(CDC) 실행 조건: 재시작 때 사람이 필요한 "비밀번호 저장 안 함" 접속(무기한이면 ERROR), 원본 시계와 이 PC 시계 차(정보).
        /// 지연 창은 원본 시계로 재므로 시계 차가 정확성을 해치지는 않지만, 화면의 "다음 주기" 시각은 이 PC 시계라 차이를 알려 둔다.
        /// </summary>
        private async Task EmitSyncRunChecksAsync(ValidationContext ctx, List<ValidationItem> items, Action<ValidationItem> onItem, CancellationToken ct)
        {
            if (ctx.Job == null || ctx.Job.Strategy == null)
            {
                return;
            }

            var cdc = string.Equals(ctx.Job.Strategy.Mode, ExecutionModes.Cdc, StringComparison.Ordinal);
            var incremental = string.Equals(ctx.Job.Strategy.Mode, ExecutionModes.Incremental, StringComparison.Ordinal);
            if (!cdc && !incremental)
            {
                return;
            }

            if (cdc)
            {
                var unsaved = new List<string>();
                if (ctx.SourceProfile != null && !ctx.SourceProfile.SavePassword) unsaved.Add("원본 " + ctx.SourceProfile.Name);
                if (ctx.TargetProfile != null && !ctx.TargetProfile.SavePassword) unsaved.Add("대상 " + ctx.TargetProfile.Name);
                if (unsaved.Count > 0)
                {
                    var unlimited = ctx.Job.Strategy.MaxRunHours <= 0;
                    Emit(items, onItem, Item("공간·실행", "동기화 접속", string.Join(", ", unsaved),
                        unlimited ? CheckLevels.Error : CheckLevels.Warn,
                        "비밀번호를 저장하지 않는 접속은 에이전트가 다시 시작할 때 사람이 입력해야 함"
                        + (unlimited ? " — 무기한 동기화에는 쓸 수 없음: 비밀번호를 저장하거나 최대 실행 시간을 두세요" : " — 상시 운영이면 비밀번호 저장을 켜세요"),
                        Fix("connection")));
                }
            }

            if (ctx.Source != null && ctx.SourceMeta != null)
            {
                var qr = await SafeQueryAsync(ctx.Source, ctx.SourceMeta.Schema, "SELECT TO_CHAR(SYSTIMESTAMP, 'YYYY-MM-DD HH24:MI:SS') AS NOW_AT FROM DUAL", ct).ConfigureAwait(false);
                var text = qr != null && qr.Rows != null && qr.Rows.Count > 0 && qr.Rows[0] != null && qr.Rows[0].Length > 0 ? qr.Rows[0][0] : null;
                DateTime sourceNow;
                if (!string.IsNullOrEmpty(text) && DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out sourceNow))
                {
                    var skew = (DateTime.Now - sourceNow).TotalSeconds;
                    var level = Math.Abs(skew) > Math.Max(60, ctx.Job.Strategy.LagSeconds) ? CheckLevels.Warn : CheckLevels.Info;
                    Emit(items, onItem, Item("공간·실행", "원본 시계", ctx.SourceProfile != null ? ctx.SourceProfile.Name : "", level,
                        "원본 " + text + " · 이 PC와 " + Math.Round(Math.Abs(skew)).ToString(CultureInfo.InvariantCulture) + "초 차이"
                        + " — 지연 창(" + ctx.Job.Strategy.LagSeconds + "초)은 원본 시계로 재므로 정확성에는 영향 없음"
                        + (level == CheckLevels.Warn ? ". 차이가 커서 화면의 다음 주기 시각이 어긋나 보일 수 있음" : ""),
                        null));
                }
            }
        }

        private static bool jobContinue(ValidationContext ctx)
        {
            return ctx.Job != null && ctx.Job.Strategy != null
                && string.Equals(ctx.Job.Strategy.ErrorPolicy, ErrorPolicies.Continue, StringComparison.Ordinal);
        }

        /// <summary>
        /// 테이블스페이스 판정. 현재 여유(USER_FREE_SPACE)는 데이터 파일이 자동 확장되면 늘어나므로, 자동 확장 여유를 모르는 채
        /// "여유보다 크다"만으로 ERROR를 내면 작은 DB(XE 등)에서 멀쩡한 이관을 막는다 — 할당량 부족이나 자동 확장까지 합쳐도 모자랄 때만 ERROR,
        /// 자동 확장을 모르면 WARN.
        /// </summary>
        public static ValidationItem TablespaceItem(TablespaceInfo ts, double needGb)
        {
            var capacity = ts.FreeGb + (ts.AutoExtendGb ?? 0);
            if (ts.QuotaLeftGb != null && ts.QuotaLeftGb.Value < capacity)
            {
                capacity = ts.QuotaLeftGb.Value;
            }

            string level;
            var note = "";
            if (ts.QuotaLeftGb != null && needGb > ts.QuotaLeftGb.Value)
            {
                level = CheckLevels.Error;
                note = " — 계정 할당량(QUOTA) 부족(ORA-01536)";
            }
            else if (needGb > capacity)
            {
                if (ts.AutoExtendGb != null)
                {
                    level = CheckLevels.Error;
                    note = " — 자동 확장을 합쳐도 모자람(ORA-01653)";
                }
                else
                {
                    level = CheckLevels.Warn;
                    note = " — 자동 확장 여유는 확인 못 함(DBA_DATA_FILES 권한). 모자라면 ORA-01653";
                }
            }
            else if (needGb > capacity * 0.7)
            {
                level = CheckLevels.Warn;
            }
            else
            {
                level = CheckLevels.Pass;
            }

            var detail = "필요 약 " + Gb(needGb) + "(행 × 평균 행 길이 × 1.35, 인덱스 포함) / 여유 " + Gb(ts.FreeGb);
            if (ts.AutoExtendGb != null)
            {
                detail += " + 자동 확장 " + Gb(ts.AutoExtendGb.Value);
            }

            if (ts.QuotaLeftGb != null)
            {
                detail += " · 할당량 남음 " + Gb(ts.QuotaLeftGb.Value);
            }

            return Item("공간·실행", "대상 테이블스페이스", ts.Name, level, detail + note, null);
        }

        private static string Gb(double gb)
        {
            return gb.ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        private async Task<TablespaceInfo> TryLoadTablespaceAsync(ValidationContext ctx, CancellationToken ct)
        {
            try
            {
                var sql = ValidationSql.TablespaceFree();
                var qr = await SafeQueryAsync(ctx.Target, ctx.TargetMeta.Schema, sql, ct).ConfigureAwait(false);
                if (qr == null || qr.Rows == null || qr.Rows.Count == 0)
                {
                    return null;
                }

                var row = qr.Rows[0];
                var free = ParseDouble(Cell(qr, row, 1));
                if (free == null)
                {
                    return null;
                }

                var info = new TablespaceInfo
                {
                    Name = Cell(qr, row, 0),
                    FreeGb = free.Value,
                    QuotaLeftGb = ParseDouble(Cell(qr, row, 2))
                };

                // 자동 확장 여유는 DBA_DATA_FILES 권한이 있을 때만 — 없으면(ORA-00942) SafeQueryAsync가 null을 주고 "모름"으로 남긴다
                var auto = await SafeQueryAsync(ctx.Target, ctx.TargetMeta.Schema, ValidationSql.TablespaceAutoExtend(), ct).ConfigureAwait(false);
                if (auto != null && auto.Rows != null && auto.Rows.Count > 0)
                {
                    info.AutoExtendGb = ParseDouble(Cell(auto, auto.Rows[0], 0));
                }

                return info;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static string Cell(QueryResult qr, string[] row, int index)
        {
            return index < row.Length ? row[index] : "";
        }

        private static double? ParseDouble(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return null;
            }

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }

            return null;
        }

        private async Task<QueryResult> SafeQueryAsync(ConnectionTarget target, string schema, string sql, CancellationToken ct)
        {
            try
            {
                OracleSelectGuard.EnsureSelectOnly(sql);
                return await WithQueryTimeout(
                    token => _adapter.QueryAsync(target, schema, sql, null, 1000, token),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// <see cref="ValidationSql"/>의 셈 SQL(TargetRowCount·OrphanRows)은 이미 <c>SELECT COUNT(*)</c>다. 어댑터의 CountAsync는 주어진
        /// SQL을 <c>SELECT COUNT(*) FROM (…)</c>로 한 번 더 감싸므로 "결과 행 수"(항상 1)가 나온다 — 첫 행 첫 셀을 직접 읽는다.
        /// 실제 Oracle에서 빈 대상 테이블이 "이미 1행 있음"으로 나오던 원인.
        /// </summary>
        private async Task<long?> SafeCountAsync(ConnectionTarget target, string schema, string sql, CancellationToken ct)
        {
            try
            {
                OracleSelectGuard.EnsureSelectOnly(sql);
                var qr = await SafeQueryAsync(target, schema, sql, ct).ConfigureAwait(false);
                if (qr == null || qr.Rows == null || qr.Rows.Count == 0)
                {
                    return null;
                }

                var cell = Cell(qr, qr.Rows[0], 0);
                long value;
                if (long.TryParse(cell, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                {
                    return value;
                }

                var dec = ParseDouble(cell);
                return dec.HasValue ? (long?)(long)dec.Value : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<T> WithQueryTimeout<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
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

                    throw new TimeoutException("질의 시간 초과(60초)");
                }
            }
        }

        private static TableMetadata SourceOf(MappingModel m, SchemaMetadata srcMeta, ValidationContext ctx)
        {
            if (m == null)
            {
                return null;
            }

            if (!m.IsSql)
            {
                return srcMeta != null ? srcMeta.FindTable(m.Source) : null;
            }

            IList<QueryColumn> described = null;
            if (ctx.SqlDescribe != null && ctx.SqlDescribe.TryGetValue(m.Id, out var cols))
            {
                described = cols;
            }

            return SqlSourceService.VirtualTable(m, described != null ? described.ToList() : new List<QueryColumn>(), srcMeta);
        }

        private static string LabelOf(MappingModel m)
        {
            return (m.IsSql ? "SQL " : "") + m.Source + " → " + (m.Target ?? "?");
        }

        private static List<MappingModel> PlannedMappings(IList<MappingModel> used, SchemaMetadata tgtMeta)
        {
            var list = used.ToList();
            var outList = new List<MappingModel>();
            var done = new HashSet<string>(StringComparer.Ordinal);
            var pending = list.ToList();
            var guard = 0;
            while (pending.Count > 0 && guard++ < 100)
            {
                var idx = pending.FindIndex(x =>
                {
                    var t = tgtMeta.FindTable(x.Target);
                    var parents = (t != null ? t.ForeignKeys : null) ?? new List<ForeignKeyMetadata>();
                    var refs = parents.Select(f => f.RefTable).Where(r => !string.Equals(r, x.Target, StringComparison.OrdinalIgnoreCase)).ToList();
                    return refs.All(p => done.Contains(p) || !pending.Any(y => string.Equals(y.Target, p, StringComparison.OrdinalIgnoreCase)));
                });
                var next = pending[idx < 0 ? 0 : idx];
                pending.RemoveAt(idx < 0 ? 0 : idx);
                outList.Add(next);
                done.Add(next.Target);
            }

            return outList;
        }

        private static string SampleTag(TableMetadata source, MappingModel tm)
        {
            if (tm.IsSql)
            {
                return "앞 100만 행";
            }

            if (source.Rows != null && source.Rows.Value > SampleRowThreshold)
            {
                return "표본 5%";
            }

            return null;
        }

        private static ValidationItem Item(
            string group,
            string check,
            string target,
            string level,
            string detail,
            FixAction fix,
            string mappingId = null,
            string sample = null)
        {
            return new ValidationItem
            {
                Group = group,
                Check = check,
                Target = target ?? "",
                Level = level,
                Detail = detail,
                Fix = fix,
                MappingId = mappingId,
                Sample = sample
            };
        }

        private static FixAction Fix(string page, string mappingId = null, string column = null)
        {
            return new FixAction { Page = page, MappingId = mappingId, Column = column };
        }

        private static void Emit(List<ValidationItem> items, Action<ValidationItem> onItem, ValidationItem item)
        {
            items.Add(item);
            if (onItem != null)
            {
                onItem(item);
            }
        }
    }
}
