using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Expressions;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Sql
{
    public static class SqlSourceAnalyzer
    {
        private static readonly Regex RegexpReplaceRx = new Regex(
            @"REGEXP_REPLACE",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex TrimReplaceRx = new Regex(
            @"TRIM|REGEXP_REPLACE",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NvlCaseRx = new Regex(
            @"\bNVL\s*\(|COALESCE|\bCASE\b|DECODE",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NullBlankRx = new Regex(@"NULL|빈 문자열", RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, SqlSourceInfo> VirtualCache = new Dictionary<string, SqlSourceInfo>(StringComparer.Ordinal);

        public static DescribeResult Describe(SelectStatement statement, SchemaMetadata source)
        {
            return BuildDescribe(statement, source);
        }

        public static SqlSourceInfo Analyze(MappingModel mapping, SchemaMetadata source, IList<QueryColumn> described)
        {
            if (described == null)
            {
                return Analyze(mapping, source);
            }

            var bindKey = string.Join(",", (mapping.Binds ?? new List<BindParameter>())
                .Select(b => b.Name + "=" + b.Value));
            var schemaKey = source != null ? source.Schema : "";
            // DESCRIBE 결과도 키에 넣는다 — 빠지면 같은 SQL을 다른 열 목록으로(또는 빈 목록으로) 먼저 분석한 결과가 그대로 돌아온다.
            var describedKey = string.Join(",", described.Select(c => c.Name + ":" + c.Type + ":" + (c.Nullable ? "1" : "0")));
            var key = schemaKey + "\u0000" + mapping.Source + "\u0000" + mapping.Sql + "\u0000" + bindKey + "\u0000D" + describedKey;
            lock (VirtualCache)
            {
                if (VirtualCache.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            var parsed = SelectParser.Parse(mapping.Sql);
            SqlSourceInfo info;
            if (parsed.Errors.Count > 0 || source == null)
            {
                var first = parsed.Errors.Count > 0 ? parsed.Errors[0] : null;
                info = new SqlSourceInfo
                {
                    Table = new TableMetadata
                    {
                        Name = mapping.Source,
                        Kind = "SQL",
                        Columns = new List<ColumnMetadata>(),
                        Rows = null,
                        AvgRowLength = 100,
                        Comment = first != null ? first.Code + ": " + first.Message : ""
                    },
                    Error = first ?? new SqlError { Message = "메타데이터 없음" },
                    Statement = parsed
                };
            }
            else
            {
                var ctx = BuildDescribe(parsed, source);
                var resultCols = ResultColumnsFromDescribed(described, source);
                var from = string.Join(" + ", parsed.Tables
                    .Where(t => !t.Subquery)
                    .Select(t => t.Name + (t.Alias != null ? " " + t.Alias : "")));
                var mainMeta = ctx.Runtime.Tables.FirstOrDefault(t => t.Ref.Join == null)?.Meta
                    ?? (ctx.Runtime.Tables.Count > 0 ? ctx.Runtime.Tables[0].Meta : null);
                info = new SqlSourceInfo
                {
                    Table = new TableMetadata
                    {
                        Name = mapping.Source,
                        Kind = "SQL",
                        Columns = resultCols.Where(c => c.Error == null).Select(ToColumn).ToList(),
                        Rows = EstimateRowsFromContext(mapping, parsed, ctx.Runtime),
                        AvgRowLength = 110,
                        Comment = "FROM " + from
                    },
                    BaseRows = mainMeta != null ? mainMeta.Rows : null,
                    Error = ctx.Errors.Count > 0 ? ctx.Errors[0] : null,
                    Statement = parsed
                };
            }

            lock (VirtualCache)
            {
                if (VirtualCache.Count > 100)
                {
                    VirtualCache.Clear();
                }

                VirtualCache[key] = info;
            }

            return info;
        }

        public static SqlSourceInfo Analyze(MappingModel mapping, SchemaMetadata source)
        {
            var bindKey = string.Join(",", (mapping.Binds ?? new List<BindParameter>())
                .Select(b => b.Name + "=" + b.Value));
            var schemaKey = source != null ? source.Schema : "";
            var key = schemaKey + "\u0000" + mapping.Source + "\u0000" + mapping.Sql + "\u0000" + bindKey;
            lock (VirtualCache)
            {
                if (VirtualCache.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            var parsed = SelectParser.Parse(mapping.Sql);
            SqlSourceInfo info;
            if (parsed.Errors.Count > 0 || source == null)
            {
                var first = parsed.Errors.Count > 0 ? parsed.Errors[0] : null;
                info = new SqlSourceInfo
                {
                    Table = new TableMetadata
                    {
                        Name = mapping.Source,
                        Kind = "SQL",
                        Columns = new List<ColumnMetadata>(),
                        Rows = null,
                        AvgRowLength = 100,
                        Comment = first != null ? first.Code + ": " + first.Message : ""
                    },
                    Error = first ?? new SqlError { Message = "메타데이터 없음" },
                    Statement = parsed
                };
            }
            else
            {
                var ctx = BuildDescribe(parsed, source);
                var from = string.Join(" + ", parsed.Tables
                    .Where(t => !t.Subquery)
                    .Select(t => t.Name + (t.Alias != null ? " " + t.Alias : "")));
                var mainMeta = ctx.Runtime.Tables.FirstOrDefault(t => t.Ref.Join == null)?.Meta
                    ?? (ctx.Runtime.Tables.Count > 0 ? ctx.Runtime.Tables[0].Meta : null);
                info = new SqlSourceInfo
                {
                    Table = new TableMetadata
                    {
                        Name = mapping.Source,
                        Kind = "SQL",
                        Columns = ctx.Columns.Where(c => c.Error == null).Select(ToColumn).ToList(),
                        Rows = EstimateRowsFromContext(mapping, parsed, ctx.Runtime),
                        AvgRowLength = 110,
                        Comment = "FROM " + from
                    },
                    BaseRows = mainMeta != null ? mainMeta.Rows : null,
                    Error = ctx.Errors.Count > 0 ? ctx.Errors[0] : null,
                    Statement = parsed
                };
            }

            lock (VirtualCache)
            {
                if (VirtualCache.Count > 100)
                {
                    VirtualCache.Clear();
                }

                VirtualCache[key] = info;
            }

            return info;
        }

        public static long? EstimateRows(MappingModel mapping, SelectStatement statement, DescribeResult describe)
        {
            if (describe?.Runtime != null)
            {
                return EstimateRowsFromContext(mapping, statement, describe.Runtime);
            }

            return null;
        }

        public static BindParameter CheckpointBind(MappingModel mapping, SelectStatement statement)
        {
            var b = (mapping.Binds ?? new List<BindParameter>()).FirstOrDefault(x => x.FromCheckpoint);
            if (b == null || statement.Binds == null)
            {
                return null;
            }

            return statement.Binds.Contains(b.Name, StringComparer.OrdinalIgnoreCase) ? b : null;
        }

        public static SqlValidationResult Validate(
            MappingModel mapping,
            SchemaMetadata source,
            SchemaMetadata target,
            IList<QueryColumn> described)
        {
            if (described == null)
            {
                return Validate(mapping, source, target);
            }

            return ValidateWithDescribed(mapping, source, target, described);
        }

        public static SqlValidationResult Validate(MappingModel mapping, SchemaMetadata source, SchemaMetadata target)
        {
            var items = new List<CheckItem>();
            void Add(string check, string level, string detail)
            {
                items.Add(new CheckItem { Check = check, Level = level, Detail = detail });
            }

            var parsed = SelectParser.Parse(mapping.Sql);
            if (parsed.Errors.Count > 0)
            {
                Add(
                    "SQL 구문",
                    CheckLevels.Error,
                    string.Join("\n", parsed.Errors.Select(e =>
                        e.Code + ": " + e.Message + (e.Line != null ? " (" + e.Line + "번 줄)" : ""))));
                return new SqlValidationResult
                {
                    Level = CheckLevels.Error,
                    Items = items,
                    Statement = parsed,
                    Columns = new List<ResultColumn>(),
                    Mapped = 0,
                    Total = 0,
                    ErrorLine = parsed.Errors[0].Line
                };
            }

            var fromTables = string.Join(", ", parsed.Tables
                .Where(t => !t.Subquery)
                .Select(t => t.Name + (t.Alias != null ? " " + t.Alias : "")));
            Add(
                "SQL 구문",
                CheckLevels.Pass,
                "SELECT · 결과 열 " + parsed.Items.Count + "개 · FROM " + fromTables +
                (parsed.Warnings.Count > 0 ? "\n" + string.Join("\n", parsed.Warnings) : ""));

            var ctx = BuildDescribe(parsed, source);
            Add(
                "원본 객체·열",
                ctx.Errors.Count > 0 ? CheckLevels.Error : CheckLevels.Pass,
                ctx.Errors.Count > 0
                    ? string.Join("\n", ctx.Errors.Select(e => e.Code + ": " + e.Message))
                    : "참조한 테이블 " + ctx.Runtime.Tables.Count + "개, 열이 모두 있음");

            var cols = ctx.Columns;
            Add("결과 열 수", cols.Count > 0 ? CheckLevels.Pass : CheckLevels.Error, cols.Count + "개");

            var noAlias = parsed.Items.Where(it => !it.Star && it.Alias == null && !(it.Node != null && it.Node.Kind == "col")).ToList();
            var names = cols.Select(c => c.Name).ToList();
            var dup = names.Where((n, k) => names.IndexOf(n) != k).ToList();
            var dupSet = dup.Distinct(StringComparer.Ordinal).ToList();
            Add(
                "별칭",
                dupSet.Count > 0 ? CheckLevels.Error : noAlias.Count > 0 ? CheckLevels.Warn : CheckLevels.Pass,
                dupSet.Count > 0
                    ? "별칭이 겹칩니다: " + string.Join(", ", dupSet)
                    : noAlias.Count > 0
                        ? "별칭 없는 식 " + noAlias.Count + "개: " + string.Join(", ", noAlias.Select(x =>
                            Regex.Replace(x.Expr, @"\s+", " ").Length <= 40
                                ? Regex.Replace(x.Expr, @"\s+", " ")
                                : Regex.Replace(x.Expr, @"\s+", " ").Substring(0, 40))) + " — AS로 이름을 붙이세요"
                        : "모든 결과 열에 이름이 있음");

            var binds = parsed.Binds ?? new List<string>();
            var missing = binds.Where(b => !(mapping.Binds ?? new List<BindParameter>()).Any(x =>
                string.Equals(x.Name, b, StringComparison.OrdinalIgnoreCase) &&
                (!string.IsNullOrWhiteSpace(x.Value) || x.FromCheckpoint))).ToList();
            Add(
                "바인드 변수",
                missing.Count > 0 ? CheckLevels.Error : CheckLevels.Pass,
                binds.Count > 0
                    ? string.Join(", ", binds.Select(b => ":" + b)) +
                      (missing.Count > 0
                          ? " — 값 없음: " + string.Join(", ", missing.Select(b => ":" + b)) + " (ORA-01008)"
                          : "")
                    : "없음");

            var targetTable = target != null ? target.FindTable(mapping.Target) : null;
            var mapped = 0;
            var total = 0;
            if (targetTable == null)
            {
                Add("대상 컬럼 매핑", CheckLevels.Error, "대상 테이블을 고르세요");
            }
            else
            {
                total = targetTable.Columns.Count;
                var virtualTable = new TableMetadata
                {
                    Name = mapping.Source,
                    Columns = cols.Where(c => c.Error == null).Select(ToColumn).ToList()
                };
                var st = MappingService.Status(mapping, virtualTable, targetTable);
                mapped = st.Mapped;
                var unused = cols.Where(c => !(mapping.Columns ?? new List<ColumnMapping>()).Any(x =>
                    string.Equals(x.Source, c.Name, StringComparison.Ordinal) ||
                    ExpressionAnalyzer.Analyze(x.Expr ?? "").Refs.Contains(c.Name))).ToList();
                var nn = st.Results.Where(r => r.Check.Messages.Any(x => x.Level == CheckLevels.Error)).ToList();
                Add(
                    "대상 컬럼 매핑",
                    nn.Count > 0 ? CheckLevels.Error : unused.Count > 0 ? CheckLevels.Warn : CheckLevels.Pass,
                    st.Mapped + " / " + st.Total + " 매핑" +
                    (nn.Count > 0
                        ? "\n" + string.Join("\n", nn.Select(r =>
                            r.Target.Name + ": " + r.Check.Messages.First(x => x.Level == CheckLevels.Error).Message))
                        : "") +
                    (unused.Count > 0 ? "\n대상에 쓰지 않는 결과 열: " + string.Join(", ", unused.Select(c => c.Name)) : ""));

                var typeIssues = new List<(ColumnResult R, CheckMessage X)>();
                var nullIssues = new List<(ColumnResult R, CheckMessage X)>();
                foreach (var r in st.Results)
                {
                    foreach (var x in r.Check.Messages)
                    {
                        if (x.Level == CheckLevels.Pass || x.Level == CheckLevels.Info || x.Level == CheckLevels.Error)
                        {
                            continue;
                        }

                        if (NullBlankRx.IsMatch(x.Message))
                        {
                            nullIssues.Add((r, x));
                        }
                        else
                        {
                            typeIssues.Add((r, x));
                        }
                    }
                }

                Add(
                    "형식 호환성",
                    CheckLevels.Worst(typeIssues.Select(i => i.X.Level)),
                    typeIssues.Count > 0
                        ? string.Join("\n", typeIssues.Select(i =>
                            (i.R.Mapping.Source ?? "식") + " → " + i.R.Target.Name + ": " + i.X.Message))
                        : mapped + "개 열 모두 호환");
                if (nullIssues.Count > 0)
                {
                    Add(
                        "NULL 처리",
                        CheckLevels.Worst(nullIssues.Select(i => i.X.Level)),
                        string.Join("\n", nullIssues.Select(i => i.R.Target.Name + ": " + i.X.Message)));
                }

                var needKey = WriteModes.Of(mapping.Mode).NeedsKey;
                var covered = new HashSet<string>(st.Results
                    .Where(r => !string.IsNullOrEmpty(MappingService.ValueSource(r.Mapping)))
                    .Select(r => r.Target.Name), StringComparer.Ordinal);
                var keys = (mapping.MergeKey ?? new List<string>()).Where(k => covered.Contains(k)).ToList();
                if (needKey)
                {
                    Add(
                        "병합 키",
                        keys.Count > 0 ? CheckLevels.Pass : CheckLevels.Error,
                        keys.Count > 0
                            ? string.Join(", ", keys) + " (대상 PK " + string.Join(", ",
                                targetTable.Columns.Where(t => t.PrimaryKey).Select(t => t.Name)) + ")"
                            : WriteModes.Of(mapping.Mode).Label + "에는 매핑된 병합 키가 필요합니다");
                }
            }

            if (!string.IsNullOrEmpty(mapping.CheckpointColumn))
            {
                var col = cols.FirstOrDefault(c => c.Name == mapping.CheckpointColumn);
                var cp = CheckpointBind(mapping, parsed);
                if (col == null)
                {
                    Add(
                        "체크포인트",
                        CheckLevels.Warn,
                        mapping.CheckpointColumn + "을(를) 결과 열에서 찾지 못함 — 중단하면 처음부터 다시 해야 함");
                }
                else if (cp != null)
                {
                    Add(
                        "체크포인트",
                        CheckLevels.Pass,
                        "SQL을 감싸 ORDER BY S." + col.Name + "로 읽고, 재개할 때 마지막 커밋 값을 :" + cp.Name +
                        "에 넣음(= " + Regex.Replace(col.Expr, @"\s+", " ") + ")");
                }
                else
                {
                    Add(
                        "체크포인트",
                        CheckLevels.Info,
                        "SQL 안에 체크포인트 바인드 변수가 없어 바깥에서 S." + col.Name + " > :LAST_ID로 거름.\n큰 원본이면 SQL의 WHERE에 " +
                        Regex.Replace(col.Expr, @"\s+", " ") + " > :LAST_ID를 넣고 CP로 표시하면 인덱스를 탐");
                }
            }
            else
            {
                Add("체크포인트", CheckLevels.Info, "체크포인트 열이 없음 — 중단하면 처음부터 다시 실행");
            }

            return new SqlValidationResult
            {
                Level = CheckLevels.Worst(items.Select(i => i.Level)),
                Items = items,
                Statement = parsed,
                Columns = cols,
                Mapped = mapped,
                Total = total
            };
        }

        private static long? EstimateRowsFromContext(MappingModel mapping, SelectStatement parsed, DescribeRuntime ctx)
        {
            var main = ctx.Tables.FirstOrDefault(t => t.Ref.Join == null) ?? (ctx.Tables.Count > 0 ? ctx.Tables[0] : null);
            if (main == null || main.Meta == null || main.Meta.Rows == null)
            {
                return null;
            }

            var rows = main.Meta.Rows.Value;
            var pk = main.Meta.Columns.FirstOrDefault(c => c.PrimaryKey);
            if (parsed.Where != null && pk != null)
            {
                var rx = new Regex(
                    @"(?:\w+\.)?" + Regex.Escape(pk.Name) + @"\s*>\s*:(\w+)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var hit = rx.Match(parsed.Where.Text);
                if (hit.Success)
                {
                    var bindName = hit.Groups[1].Value.ToUpperInvariant();
                    var b = (mapping.Binds ?? new List<BindParameter>()).FirstOrDefault(x =>
                        string.Equals(x.Name, bindName, StringComparison.OrdinalIgnoreCase));
                    if (b != null && Regex.IsMatch(b.Value ?? "", @"^\d+$"))
                    {
                        rows = Math.Max(0, rows - long.Parse(b.Value));
                    }
                }
            }

            if (parsed.GroupBy)
            {
                rows = (long)Math.Round(rows / 10.0);
            }

            return rows;
        }

        private static DescribeResult BuildDescribe(SelectStatement parsed, SchemaMetadata srcMeta)
        {
            var errors = new List<SqlError>();
            var tables = new List<ResolvedTableRef>();
            foreach (var t in parsed.Tables)
            {
                if (t.Subquery)
                {
                    continue;
                }

                var meta = srcMeta != null ? srcMeta.FindTable(t.Name) : null;
                if (meta == null)
                {
                    errors.Add(new SqlError
                    {
                        Code = "ORA-00942",
                        Message = "테이블 또는 뷰가 존재하지 않습니다: " + (t.Schema != null ? t.Schema + "." : "") + t.Name
                    });
                }
                else
                {
                    tables.Add(new ResolvedTableRef { Ref = t, Meta = meta });
                }
            }

            ResolveResult Resolve(string name)
            {
                var dot = name.LastIndexOf('.');
                if (dot > 0)
                {
                    var q = name.Substring(0, dot);
                    var c = name.Substring(dot + 1);
                    var t = tables.FirstOrDefault(x =>
                        string.Equals(x.Ref.Alias, q, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Ref.Name, q, StringComparison.OrdinalIgnoreCase));
                    if (t == null)
                    {
                        return new ResolveResult { Error = "ORA-00904: \"" + q + "\": 부적합한 식별자" };
                    }

                    var col = t.Meta.Columns.FirstOrDefault(x => string.Equals(x.Name, c, StringComparison.OrdinalIgnoreCase));
                    return col != null
                        ? new ResolveResult { Col = col, Table = t }
                        : new ResolveResult { Error = "ORA-00904: \"" + name + "\": 부적합한 식별자" };
                }

                var hits = tables.Where(tbl => tbl.Meta.Columns.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))).ToList();
                if (hits.Count > 1)
                {
                    return new ResolveResult { Error = "ORA-00918: 열의 정의가 애매합니다: " + name };
                }

                if (hits.Count == 0)
                {
                    return new ResolveResult { Error = "ORA-00904: \"" + name + "\": 부적합한 식별자" };
                }

                var hit = hits[0];
                var colHit = hit.Meta.Columns.First(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                return new ResolveResult { Col = colHit, Table = hit };
            }

            var columns = new List<ResultColumn>();
            foreach (var it in parsed.Items)
            {
                if (it.Star)
                {
                    var q = it.Text.Contains(".") ? it.Text.Split('.')[0].ToUpperInvariant() : null;
                    foreach (var t in tables)
                    {
                        if (q != null &&
                            !string.Equals(t.Ref.Alias, q, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(t.Ref.Name, q, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        foreach (var c in t.Meta.Columns)
                        {
                            columns.Add(new ResultColumn
                            {
                                Name = c.Name,
                                Type = c.Type,
                                Expr = c.Name,
                                Source = c,
                                Stats = c.Stats != null ? c.Stats.Clone() : new ColumnStats(),
                                Nullable = null
                            });
                        }
                    }

                    continue;
                }

                if (it.Error != null)
                {
                    columns.Add(new ResultColumn
                    {
                        Name = it.Name,
                        Type = null,
                        Expr = it.Expr,
                        Error = it.Error.Message,
                        Alias = it.Alias,
                        Nullable = null
                    });
                    continue;
                }

                string bad = null;
                foreach (var r in it.Refs ?? Array.Empty<string>())
                {
                    var x = Resolve(r);
                    if (x.Error != null)
                    {
                        bad = x.Error;
                        break;
                    }
                }

                if (bad != null)
                {
                    errors.Add(new SqlError { Code = bad.Substring(0, 9), Message = bad.Substring(11) + " (" + it.Name + ")" });
                    columns.Add(new ResultColumn
                    {
                        Name = it.Name,
                        Type = null,
                        Expr = it.Expr,
                        Error = bad,
                        Alias = it.Alias,
                        Nullable = null
                    });
                    continue;
                }

                var type = TypeInference.Infer(it.Node, n =>
                {
                    var x = Resolve(n);
                    return x.Col != null ? x.Col.Type : null;
                });
                ResolveResult single = it.Refs != null && it.Refs.Count == 1 ? Resolve(it.Refs[0]) : null;
                var stats = single != null && single.Col != null && single.Col.Stats != null
                    ? single.Col.Stats.Clone()
                    : new ColumnStats();
                if (RegexpReplaceRx.IsMatch(it.Expr) && stats.DigitsMaxLength != null)
                {
                    stats.MaxLength = stats.DigitsMaxLength;
                }

                if (TrimReplaceRx.IsMatch(it.Expr))
                {
                    stats.Nulls = (stats.Nulls ?? 0) + (stats.Blanks ?? 0);
                    stats.Blanks = 0;
                }

                if (NvlCaseRx.IsMatch(it.Expr))
                {
                    stats.Nulls = 0;
                }

                var outer = single != null && single.Table != null && single.Table.Ref.Join == "LEFT";
                if (outer)
                {
                    stats.Nulls = null;
                }

                columns.Add(new ResultColumn
                {
                    Name = it.Name,
                    Type = type,
                    Expr = it.Expr,
                    Alias = it.Alias,
                    Source = single != null ? single.Col : null,
                    Stats = stats,
                    Nullable = outer || single == null || single.Col == null || single.Col.Nullable
                });
            }

            if (parsed.Where != null && parsed.Where.Node != null)
            {
                foreach (var n in ExpressionAnalyzer.Walk(parsed.Where.Node))
                {
                    if (n.Kind != "col")
                    {
                        continue;
                    }

                    var x = Resolve(n.Name);
                    if (x.Error != null)
                    {
                        errors.Add(new SqlError { Code = x.Error.Substring(0, 9), Message = x.Error.Substring(11) + " (WHERE)" });
                    }
                }
            }

            return new DescribeResult
            {
                Errors = errors,
                Columns = columns,
                Runtime = new DescribeRuntime { Tables = tables }
            };
        }

        private sealed class ResolveResult
        {
            public ColumnMetadata Col;
            public ResolvedTableRef Table;
            public string Error;
        }

        private static ColumnMetadata ToColumn(ResultColumn c)
        {
            return new ColumnMetadata
            {
                Name = c.Name,
                Type = c.Type,
                Nullable = c.Nullable ?? c.Source?.Nullable ?? true,
                PrimaryKey = false,
                DefaultValue = null,
                Comment = Regex.Replace(c.Expr ?? "", @"\s+", " "),
                Stats = c.Stats ?? new ColumnStats()
            };
        }

        private static SqlValidationResult ValidateWithDescribed(
            MappingModel mapping,
            SchemaMetadata source,
            SchemaMetadata target,
            IList<QueryColumn> described)
        {
            var items = new List<CheckItem>();
            void Add(string check, string level, string detail)
            {
                items.Add(new CheckItem { Check = check, Level = level, Detail = detail });
            }

            var parsed = SelectParser.Parse(mapping.Sql);
            if (parsed.Errors.Count > 0)
            {
                Add(
                    "SQL 구문",
                    CheckLevels.Error,
                    string.Join("\n", parsed.Errors.Select(e =>
                        e.Code + ": " + e.Message + (e.Line != null ? " (" + e.Line + "번 줄)" : ""))));
                return new SqlValidationResult
                {
                    Level = CheckLevels.Error,
                    Items = items,
                    Statement = parsed,
                    Columns = new List<ResultColumn>(),
                    Mapped = 0,
                    Total = 0,
                    ErrorLine = parsed.Errors[0].Line
                };
            }

            var fromTables = string.Join(", ", parsed.Tables
                .Where(t => !t.Subquery)
                .Select(t => t.Name + (t.Alias != null ? " " + t.Alias : "")));
            Add(
                "SQL 구문",
                CheckLevels.Pass,
                "SELECT · 결과 열 " + described.Count + "개 · FROM " + fromTables +
                (parsed.Warnings.Count > 0 ? "\n" + string.Join("\n", parsed.Warnings) : ""));

            Add(
                "원본 객체·열",
                CheckLevels.Pass,
                "Oracle DESCRIBE 기준 결과 열 " + described.Count + "개");

            var cols = ResultColumnsFromDescribed(described, source);
            Add("결과 열 수", cols.Count > 0 ? CheckLevels.Pass : CheckLevels.Error, cols.Count + "개");

            return FinishValidate(mapping, target, parsed, items, cols);
        }

        private static SqlValidationResult FinishValidate(
            MappingModel mapping,
            SchemaMetadata target,
            SelectStatement parsed,
            List<CheckItem> items,
            List<ResultColumn> cols)
        {
            void Add(string check, string level, string detail)
            {
                items.Add(new CheckItem { Check = check, Level = level, Detail = detail });
            }

            var noAlias = parsed.Items.Where(it => !it.Star && it.Alias == null && !(it.Node != null && it.Node.Kind == "col")).ToList();
            var names = cols.Select(c => c.Name).ToList();
            var dup = names.Where((n, k) => names.IndexOf(n) != k).ToList();
            var dupSet = dup.Distinct(StringComparer.Ordinal).ToList();
            Add(
                "별칭",
                dupSet.Count > 0 ? CheckLevels.Error : noAlias.Count > 0 ? CheckLevels.Warn : CheckLevels.Pass,
                dupSet.Count > 0
                    ? "별칭이 겹칩니다: " + string.Join(", ", dupSet)
                    : noAlias.Count > 0
                        ? "별칭 없는 식 " + noAlias.Count + "개: " + string.Join(", ", noAlias.Select(x =>
                            Regex.Replace(x.Expr, @"\s+", " ").Length <= 40
                                ? Regex.Replace(x.Expr, @"\s+", " ")
                                : Regex.Replace(x.Expr, @"\s+", " ").Substring(0, 40))) + " — AS로 이름을 붙이세요"
                        : "모든 결과 열에 이름이 있음");

            var binds = parsed.Binds ?? new List<string>();
            var missing = binds.Where(b => !(mapping.Binds ?? new List<BindParameter>()).Any(x =>
                string.Equals(x.Name, b, StringComparison.OrdinalIgnoreCase) &&
                (!string.IsNullOrWhiteSpace(x.Value) || x.FromCheckpoint))).ToList();
            Add(
                "바인드 변수",
                missing.Count > 0 ? CheckLevels.Error : CheckLevels.Pass,
                binds.Count > 0
                    ? string.Join(", ", binds.Select(b => ":" + b)) +
                      (missing.Count > 0
                          ? " — 값 없음: " + string.Join(", ", missing.Select(b => ":" + b)) + " (ORA-01008)"
                          : "")
                    : "없음");

            var targetTable = target != null ? target.FindTable(mapping.Target) : null;
            var mapped = 0;
            var total = 0;
            if (targetTable == null)
            {
                Add("대상 컬럼 매핑", CheckLevels.Error, "대상 테이블을 고르세요");
            }
            else
            {
                total = targetTable.Columns.Count;
                var virtualTable = new TableMetadata
                {
                    Name = mapping.Source,
                    Columns = cols.Where(c => c.Error == null).Select(ToColumn).ToList()
                };
                var st = MappingService.Status(mapping, virtualTable, targetTable);
                mapped = st.Mapped;
                var unused = cols.Where(c => !(mapping.Columns ?? new List<ColumnMapping>()).Any(x =>
                    string.Equals(x.Source, c.Name, StringComparison.Ordinal) ||
                    ExpressionAnalyzer.Analyze(x.Expr ?? "").Refs.Contains(c.Name))).ToList();
                var nn = st.Results.Where(r => r.Check.Messages.Any(x => x.Level == CheckLevels.Error)).ToList();
                Add(
                    "대상 컬럼 매핑",
                    nn.Count > 0 ? CheckLevels.Error : unused.Count > 0 ? CheckLevels.Warn : CheckLevels.Pass,
                    st.Mapped + " / " + st.Total + " 매핑" +
                    (nn.Count > 0
                        ? "\n" + string.Join("\n", nn.Select(r =>
                            r.Target.Name + ": " + r.Check.Messages.First(x => x.Level == CheckLevels.Error).Message))
                        : "") +
                    (unused.Count > 0 ? "\n대상에 쓰지 않는 결과 열: " + string.Join(", ", unused.Select(c => c.Name)) : ""));

                var typeIssues = new List<(ColumnResult R, CheckMessage X)>();
                var nullIssues = new List<(ColumnResult R, CheckMessage X)>();
                foreach (var r in st.Results)
                {
                    foreach (var x in r.Check.Messages)
                    {
                        if (x.Level == CheckLevels.Pass || x.Level == CheckLevels.Info || x.Level == CheckLevels.Error)
                        {
                            continue;
                        }

                        if (NullBlankRx.IsMatch(x.Message))
                        {
                            nullIssues.Add((r, x));
                        }
                        else
                        {
                            typeIssues.Add((r, x));
                        }
                    }
                }

                Add(
                    "형식 호환성",
                    CheckLevels.Worst(typeIssues.Select(i => i.X.Level)),
                    typeIssues.Count > 0
                        ? string.Join("\n", typeIssues.Select(i =>
                            (i.R.Mapping.Source ?? "식") + " → " + i.R.Target.Name + ": " + i.X.Message))
                        : mapped + "개 열 모두 호환");
                if (nullIssues.Count > 0)
                {
                    Add(
                        "NULL 처리",
                        CheckLevels.Worst(nullIssues.Select(i => i.X.Level)),
                        string.Join("\n", nullIssues.Select(i => i.R.Target.Name + ": " + i.X.Message)));
                }

                var needKey = WriteModes.Of(mapping.Mode).NeedsKey;
                var covered = new HashSet<string>(st.Results
                    .Where(r => !string.IsNullOrEmpty(MappingService.ValueSource(r.Mapping)))
                    .Select(r => r.Target.Name), StringComparer.Ordinal);
                var keys = (mapping.MergeKey ?? new List<string>()).Where(k => covered.Contains(k)).ToList();
                if (needKey)
                {
                    Add(
                        "병합 키",
                        keys.Count > 0 ? CheckLevels.Pass : CheckLevels.Error,
                        keys.Count > 0
                            ? string.Join(", ", keys) + " (대상 PK " + string.Join(", ",
                                targetTable.Columns.Where(t => t.PrimaryKey).Select(t => t.Name)) + ")"
                            : WriteModes.Of(mapping.Mode).Label + "에는 매핑된 병합 키가 필요합니다");
                }
            }

            if (!string.IsNullOrEmpty(mapping.CheckpointColumn))
            {
                var col = cols.FirstOrDefault(c => c.Name == mapping.CheckpointColumn);
                var cp = CheckpointBind(mapping, parsed);
                if (col == null)
                {
                    Add(
                        "체크포인트",
                        CheckLevels.Warn,
                        mapping.CheckpointColumn + "을(를) 결과 열에서 찾지 못함 — 중단하면 처음부터 다시 해야 함");
                }
                else if (cp != null)
                {
                    Add(
                        "체크포인트",
                        CheckLevels.Pass,
                        "SQL을 감싸 ORDER BY S." + col.Name + "로 읽고, 재개할 때 마지막 커밋 값을 :" + cp.Name +
                        "에 넣음(= " + Regex.Replace(col.Expr ?? col.Name, @"\s+", " ") + ")");
                }
                else
                {
                    Add(
                        "체크포인트",
                        CheckLevels.Info,
                        "SQL 안에 체크포인트 바인드 변수가 없어 바깥에서 S." + col.Name + " > :LAST_ID로 거름.\n큰 원본이면 SQL의 WHERE에 " +
                        Regex.Replace(col.Expr ?? col.Name, @"\s+", " ") + " > :LAST_ID를 넣고 CP로 표시하면 인덱스를 탐");
                }
            }
            else
            {
                Add("체크포인트", CheckLevels.Info, "체크포인트 열이 없음 — 중단하면 처음부터 다시 실행");
            }

            return new SqlValidationResult
            {
                Level = CheckLevels.Worst(items.Select(i => i.Level)),
                Items = items,
                Statement = parsed,
                Columns = cols,
                Mapped = mapped,
                Total = total
            };
        }

        private static List<ResultColumn> ResultColumnsFromDescribed(IList<QueryColumn> described, SchemaMetadata source)
        {
            var list = new List<ResultColumn>();
            if (described == null)
            {
                return list;
            }

            foreach (var c in described)
            {
                var metaCol = FindColumnInMetadata(source, c.Name);
                list.Add(new ResultColumn
                {
                    Name = c.Name,
                    Type = c.Type,
                    Expr = c.Name,
                    Alias = c.Name,
                    Nullable = c.Nullable,
                    Source = metaCol,
                    Stats = metaCol != null && metaCol.Stats != null ? metaCol.Stats.Clone() : new ColumnStats()
                });
            }

            return list;
        }

        private static ColumnMetadata FindColumnInMetadata(SchemaMetadata source, string name)
        {
            if (source == null || source.Tables == null)
            {
                return null;
            }

            foreach (var t in source.Tables)
            {
                var col = t.FindColumn(name);
                if (col != null)
                {
                    return col;
                }
            }

            return null;
        }
    }
}
