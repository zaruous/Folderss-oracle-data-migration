using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Expressions;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MappingModel = MigrationStudio.Core.Model.Mapping;
using MigrationStudio.Core.Text;
using MigrationStudio.Core.Types;

namespace MigrationStudio.Core.Mapping
{
    public static class MappingService
    {
        private static readonly Regex YnTargetRx = new Regex(@"_YN$", RegexOptions.CultureInvariant);
        private static readonly Regex YnSourceRx = new Regex(@"_YN$|_FLAG$", RegexOptions.CultureInvariant);
        private static readonly Regex PhoneTargetRx = new Regex(@"PHONE|MOBILE|TEL", RegexOptions.CultureInvariant);
        private static readonly Regex NameTargetRx = new Regex(@"(_NM|_NAME)$", RegexOptions.CultureInvariant);
        private static readonly Regex RegexpReplaceRx = new Regex(@"REGEXP_REPLACE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex TrimReplaceRx = new Regex(@"TRIM|REGEXP_REPLACE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NvlCaseRx = new Regex(@"\bNVL\s*\(|COALESCE|CASE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static List<TableMatch> AutoMatchTables(
            IEnumerable<TableMetadata> source,
            IEnumerable<TableMetadata> target,
            IEnumerable<MappingModel> existing)
        {
            var usedSrc = new HashSet<string>(StringComparer.Ordinal);
            var usedTgt = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in existing ?? Enumerable.Empty<MappingModel>())
            {
                if (!string.IsNullOrEmpty(m.Source))
                {
                    usedSrc.Add(m.Source);
                }

                if (!string.IsNullOrEmpty(m.Target))
                {
                    usedTgt.Add(m.Target);
                }
            }

            var outList = new List<TableMatch>();
            foreach (var s in source ?? Enumerable.Empty<TableMetadata>())
            {
                if (!string.Equals(s.Kind, "TABLE", StringComparison.Ordinal) || usedSrc.Contains(s.Name))
                {
                    continue;
                }

                var key = NameDictionary.NormalizeTable(s.Name);
                var free = (target ?? Enumerable.Empty<TableMetadata>())
                    .Where(t => !usedTgt.Contains(t.Name))
                    .ToList();

                TableMetadata hit = null;
                var reason = "이름 규칙(접두어 제외 같음)";
                foreach (var t in free)
                {
                    if (NameDictionary.NormalizeTable(t.Name) == key)
                    {
                        hit = t;
                        break;
                    }
                }

                if (hit == null && NameDictionary.Tables.TryGetValue(key, out var dictTarget))
                {
                    foreach (var t in free)
                    {
                        if (NameDictionary.NormalizeTable(t.Name) == dictTarget)
                        {
                            hit = t;
                            reason = "용어 사전(" + key + " → " + dictTarget + ")";
                            break;
                        }
                    }
                }

                if (hit != null)
                {
                    outList.Add(new TableMatch { Source = s.Name, Target = hit.Name, Reason = reason });
                    usedTgt.Add(hit.Name);
                }
            }

            return outList;
        }

        public static TableMatch SuggestTable(string sourceName, IEnumerable<TableMetadata> target, IEnumerable<MappingModel> existing)
        {
            var filtered = (existing ?? Enumerable.Empty<MappingModel>())
                .Where(x => !string.Equals(x.Source, sourceName, StringComparison.Ordinal))
                .ToList();
            var srcTable = new TableMetadata { Name = sourceName, Kind = "TABLE" };
            var matches = AutoMatchTables(new[] { srcTable }, target, filtered);
            return matches.Count > 0 ? matches[0] : null;
        }

        public static List<AutoMappedColumn> AutoMapColumns(IList<ColumnMetadata> source, IList<ColumnMetadata> target)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var srcCols = source ?? new List<ColumnMetadata>();
            var tgtCols = target ?? new List<ColumnMetadata>();
            var result = new List<AutoMappedColumn>();

            foreach (var t in tgtCols)
            {
                ColumnMetadata s = null;
                string reason = null;

                foreach (var c in srcCols)
                {
                    if (c.Name == t.Name)
                    {
                        s = c;
                        reason = "같은 이름";
                        break;
                    }
                }

                if (s == null)
                {
                    foreach (var c in srcCols)
                    {
                        string mapped;
                        if (NameDictionary.Columns.TryGetValue(c.Name, out mapped) && mapped == t.Name)
                        {
                            s = c;
                            reason = "용어 사전";
                            break;
                        }
                    }
                }

                if (s == null)
                {
                    foreach (var rule in NameDictionary.SuffixRules)
                    {
                        var a = rule.Key;
                        var b = rule.Value;
                        foreach (var c in srcCols)
                        {
                            if (c.Name.EndsWith(a, StringComparison.Ordinal) &&
                                c.Name.Substring(0, c.Name.Length - a.Length) + b == t.Name)
                            {
                                s = c;
                                reason = "접미어 규칙(" + a + " ↔ " + b + ")";
                                break;
                            }

                            if (c.Name.EndsWith(b, StringComparison.Ordinal) &&
                                c.Name.Substring(0, c.Name.Length - b.Length) + a == t.Name)
                            {
                                s = c;
                                reason = "접미어 규칙(" + a + " ↔ " + b + ")";
                                break;
                            }
                        }

                        if (s != null)
                        {
                            break;
                        }
                    }
                }

                if (s != null && used.Contains(s.Name))
                {
                    s = null;
                    reason = null;
                }

                if (s != null)
                {
                    used.Add(s.Name);
                }

                var defaultVal = "";
                if (!string.IsNullOrEmpty(t.DefaultValue))
                {
                    defaultVal = StripDefaultQuotes(t.DefaultValue);
                }

                var cm = new ColumnMapping
                {
                    Target = t.Name,
                    Source = s != null ? s.Name : null,
                    Expr = s != null ? SuggestExpression(s, t) : "",
                    NullRule = DefaultNullRule(t),
                    DefaultValue = defaultVal
                };

                result.Add(new AutoMappedColumn { Mapping = cm, Reason = reason });
            }

            return result;
        }

        public static string DefaultNullRule(ColumnMetadata target)
        {
            if (target != null && target.Nullable)
            {
                return NullRules.Allow;
            }

            if (target != null && !string.IsNullOrEmpty(target.DefaultValue))
            {
                return NullRules.Default;
            }

            return NullRules.Reject;
        }

        public static string SuggestExpression(ColumnMetadata source, ColumnMetadata target)
        {
            if (source == null || target == null)
            {
                return "";
            }

            var st = OracleType.Parse(source.Type);
            var tt = OracleType.Parse(target.Type);
            if (st != null && tt != null && st.Base == "DATE" && tt.Base == "TIMESTAMP")
            {
                return "CAST(" + source.Name + " AS TIMESTAMP)";
            }

            if (YnTargetRx.IsMatch(target.Name) && !YnSourceRx.IsMatch(source.Name) && st != null && st.IsChar)
            {
                return "CASE\n    WHEN " + source.Name + " = 'A' THEN 'Y'\n    ELSE 'N'\nEND";
            }

            if (PhoneTargetRx.IsMatch(target.Name) && st != null && st.IsChar)
            {
                return "REGEXP_REPLACE(" + source.Name + ", '[^0-9]', '')";
            }

            if (NameTargetRx.IsMatch(target.Name) && st != null && st.IsChar)
            {
                return "TRIM(" + source.Name + ")";
            }

            return "";
        }

        public static CompatResult Compat(string sourceType, string targetType, ColumnStats stats)
        {
            var s = OracleType.Parse(sourceType);
            var t = OracleType.Parse(targetType);
            if (s == null)
            {
                return new CompatResult
                {
                    Level = CheckLevels.Info,
                    Message = "결과 형식을 추정하지 못했습니다 — 실행 전 DESCRIBE로 다시 확인"
                };
            }

            if (t == null)
            {
                return new CompatResult { Level = CheckLevels.Error, Message = "대상 형식을 알 수 없습니다" };
            }

            if (s.IsChar && t.IsChar)
            {
                if (s.Length != null && t.Length != null && s.Length.Value > t.Length.Value)
                {
                    int? real = stats != null ? stats.MaxLength : null;
                    if (real != null && real.Value <= t.Length.Value)
                    {
                        return new CompatResult
                        {
                            Level = CheckLevels.Warn,
                            Kind = "truncate",
                            Message = sourceType + " → " + targetType + " 잘림 위험 · 실측 최대 " + real.Value + "자라 지금 데이터는 들어감"
                        };
                    }

                    var msg = sourceType + " → " + targetType + " 잘림 위험";
                    if (real != null)
                    {
                        msg += " · 실측 최대 " + real.Value + "자(ORA-12899)";
                    }

                    return new CompatResult { Level = CheckLevels.Warn, Kind = "truncate", Message = msg };
                }

                if (s.Base == "VARCHAR2" && t.Base == "CHAR")
                {
                    return new CompatResult { Level = CheckLevels.Pass, Message = "VARCHAR2 → CHAR: 뒤를 공백으로 채움" };
                }

                return new CompatResult { Level = CheckLevels.Pass, Message = "호환" };
            }

            if (s.Base == "NUMBER" && t.Base == "NUMBER")
            {
                if (t.Precision == null)
                {
                    return new CompatResult { Level = CheckLevels.Pass, Message = "호환(대상 정밀도 제한 없음)" };
                }

                if (s.Precision == null)
                {
                    return new CompatResult
                    {
                        Level = CheckLevels.Warn,
                        Kind = "precision",
                        Message = "원본 NUMBER 정밀도 미지정 → " + targetType + " 넘칠 수 있음(ORA-01438)"
                    };
                }

                var sScale = s.Scale ?? 0;
                var tScale = t.Scale ?? 0;
                var si = s.Precision.Value - sScale;
                var ti = t.Precision.Value - tScale;
                if (si > ti)
                {
                    decimal? real = stats != null ? stats.Max : null;
                    var fits = false;
                    if (real != null)
                    {
                        var trunc = decimal.Truncate(real.Value);
                        fits = trunc.ToString(CultureInfo.InvariantCulture).TrimStart('-').Length <= ti;
                    }

                    var msg = "정수부 " + si + "자리 → " + ti + "자리";
                    if (fits)
                    {
                        msg += " · 실측 최대 " + Format.Number(real.Value) + "이라 지금 데이터는 들어감";
                    }
                    else
                    {
                        msg += " · ORA-01438 위험";
                    }

                    return new CompatResult { Level = CheckLevels.Warn, Kind = "precision", Message = msg };
                }

                if (sScale > tScale)
                {
                    return new CompatResult
                    {
                        Level = CheckLevels.Warn,
                        Kind = "precision",
                        Message = "소수부 " + sScale + "자리 → " + tScale + "자리로 반올림"
                    };
                }

                return new CompatResult
                {
                    Level = CheckLevels.Pass,
                    Message = s.Precision == t.Precision ? "호환" : "호환(정밀도 넓어짐)"
                };
            }

            if (s.IsDate && t.IsDate)
            {
                if (s.Base == "TIMESTAMP" && t.Base == "DATE")
                {
                    return new CompatResult { Level = CheckLevels.Warn, Message = "TIMESTAMP → DATE: 소수 초가 사라짐" };
                }

                return new CompatResult
                {
                    Level = CheckLevels.Pass,
                    Message = s.Base == t.Base ? "호환" : "DATE → TIMESTAMP 암시 변환"
                };
            }

            if (s.Base == "NUMBER" && t.IsChar)
            {
                var need = (s.Precision ?? 38) + ((s.Scale ?? 0) > 0 ? 2 : 1);
                if (t.Length != null && t.Length.Value < need)
                {
                    return new CompatResult
                    {
                        Level = CheckLevels.Warn,
                        Message = "숫자 → 문자: " + need + "자 필요, 대상 " + t.Length.Value + "자"
                    };
                }

                return new CompatResult { Level = CheckLevels.Pass, Message = "숫자 → 문자 암시 변환" };
            }

            if (s.IsChar && t.Base == "NUMBER")
            {
                return new CompatResult { Level = CheckLevels.Warn, Message = "문자 → 숫자: 숫자가 아닌 값은 ORA-01722로 거부됨" };
            }

            if (s.IsChar && t.IsDate)
            {
                return new CompatResult { Level = CheckLevels.Warn, Message = "문자 → 날짜: TO_DATE(값, 형식)을 쓰세요(NLS에 따라 달라짐)" };
            }

            if (s.IsDate && t.IsChar)
            {
                return new CompatResult { Level = CheckLevels.Warn, Message = "날짜 → 문자: TO_CHAR(값, 형식)을 쓰세요(NLS에 따라 달라짐)" };
            }

            return new CompatResult
            {
                Level = CheckLevels.Error,
                Message = sourceType + " → " + targetType + " 형식이 호환되지 않음(ORA-00932)"
            };
        }

        public static string ValueSource(ColumnMapping mapping)
        {
            if (mapping == null)
            {
                return "";
            }

            if (!string.IsNullOrWhiteSpace(mapping.Expr))
            {
                return mapping.Expr.Trim();
            }

            return mapping.Source ?? "";
        }

        public static SourceValueInfo SourceInfo(ColumnMapping mapping, IList<ColumnMetadata> sourceColumns)
        {
            return SourceInfo(mapping, sourceColumns, null);
        }

        public static SourceValueInfo SourceInfo(ColumnMapping mapping, IList<ColumnMetadata> sourceColumns, ColumnStats measured)
        {
            var src = ValueSource(mapping);
            if (string.IsNullOrEmpty(src))
            {
                return null;
            }

            var compiled = ExpressionAnalyzer.Analyze(src);
            if (compiled.Error != null)
            {
                return new SourceValueInfo { Error = compiled.Error };
            }

            var resolver = ColTypeResolver(sourceColumns);
            foreach (var r in compiled.Refs)
            {
                if (resolver(r) == null)
                {
                    return new SourceValueInfo
                    {
                        Error = new SqlParseException("ORA-00904", "\"" + r + "\": 부적합한 식별자", -1)
                    };
                }
            }

            var type = TypeInference.Infer(compiled.Node, resolver);
            ColumnMetadata refCol = null;
            if (compiled.Refs.Count == 1)
            {
                refCol = FindColumnByName(sourceColumns, compiled.Refs[0]);
            }

            ColumnStats stats = null;
            if (measured != null)
            {
                stats = measured.Clone();
            }
            else if (refCol != null && refCol.Stats != null)
            {
                stats = refCol.Stats.Clone();
                if (stats != null && RegexpReplaceRx.IsMatch(src) && stats.DigitsMaxLength != null)
                {
                    stats.MaxLength = stats.DigitsMaxLength;
                }
            }

            long? nulls = stats != null ? (stats.Nulls ?? 0) : (long?)null;
            if (stats != null && TrimReplaceRx.IsMatch(src))
            {
                nulls = (nulls ?? 0) + (stats.Blanks ?? 0);
            }

            if (NvlCaseRx.IsMatch(src))
            {
                nulls = 0;
            }

            return new SourceValueInfo
            {
                Type = type,
                Stats = stats,
                Nulls = nulls,
                Refs = compiled.Refs,
                Nullable = refCol == null || refCol.Nullable
            };
        }

        public static ColumnCheck CheckColumn(
            ColumnMapping mapping,
            ColumnMetadata target,
            IList<ColumnMetadata> sourceColumns,
            string mode)
        {
            return CheckColumn(mapping, target, sourceColumns, mode, null);
        }

        public static ColumnCheck CheckColumn(
            ColumnMapping mapping,
            ColumnMetadata target,
            IList<ColumnMetadata> sourceColumns,
            string mode,
            IReadOnlyDictionary<string, ColumnStats> measured)
        {
            ColumnStats colMeasured = null;
            if (measured != null && target != null && measured.TryGetValue(target.Name, out var m))
            {
                colMeasured = m;
            }

            var msgs = new List<CheckMessage>();
            var src = ValueSource(mapping);
            string type = null;

            if (string.IsNullOrEmpty(src))
            {
                if (target != null && !target.Nullable)
                {
                    if (mapping != null &&
                        (mapping.NullRule == NullRules.Default || mapping.NullRule == NullRules.Custom) &&
                        !string.IsNullOrEmpty(mapping.DefaultValue))
                    {
                        var fill = mapping.NullRule == NullRules.Default
                            ? "기본값 '" + mapping.DefaultValue + "'"
                            : "식 " + mapping.DefaultValue;
                        msgs.Add(new CheckMessage { Level = CheckLevels.Pass, Message = "원본 없이 " + fill + "로 채움" });
                    }
                    else if (mapping != null && mapping.NullRule == NullRules.Sysdate)
                    {
                        msgs.Add(new CheckMessage { Level = CheckLevels.Pass, Message = "원본 없이 SYSDATE로 채움" });
                    }
                    else if (target.DefaultValue != null && mode != WriteModes.Merge)
                    {
                        msgs.Add(new CheckMessage { Level = CheckLevels.Info, Message = "DB 기본값 DEFAULT " + target.DefaultValue + " 사용" });
                    }
                    else
                    {
                        msgs.Add(new CheckMessage
                        {
                            Level = CheckLevels.Error,
                            Message = "NOT NULL 컬럼에 값이 없음 — 모든 행이 ORA-01400으로 거부됨. 원본 컬럼이나 기본값을 정하세요"
                        });
                    }
                }
                else
                {
                    msgs.Add(new CheckMessage { Level = CheckLevels.Info, Message = "매핑 안 함 — NULL로 둠" });
                }

                return new ColumnCheck
                {
                    Level = WorstMessageLevels(msgs),
                    Messages = msgs,
                    Type = type
                };
            }

            var info = SourceInfo(mapping, sourceColumns, colMeasured);
            if (info != null && info.Error != null)
            {
                msgs.Add(new CheckMessage { Level = CheckLevels.Error, Message = info.Error.Message });
                return new ColumnCheck { Level = CheckLevels.Error, Messages = msgs, Type = type };
            }

            type = info.Type;
            var c = Compat(info.Type, target.Type, info.Stats);
            msgs.Add(new CheckMessage { Level = c.Level, Message = c.Message });

            if (target != null && !target.Nullable)
            {
                var n = info.Nulls;
                if (mapping != null && mapping.NullRule == NullRules.Empty)
                {
                    msgs.Add(new CheckMessage
                    {
                        Level = CheckLevels.Warn,
                        Message = "Oracle에서 빈 문자열('')은 NULL — NOT NULL 컬럼이라 행이 거부됨"
                    });
                }
                else if (mapping != null && mapping.NullRule == NullRules.Allow && (n == null || n > 0))
                {
                    var msg = "NULL이 오면 ORA-01400으로 실패";
                    if (n != null && n > 0)
                    {
                        msg += " (예상 " + Format.Number(n.Value) + "행)";
                    }

                    msg += " — NULL 처리를 정하세요";
                    msgs.Add(new CheckMessage { Level = CheckLevels.Warn, Message = msg });
                }
                else if (mapping != null && mapping.NullRule == NullRules.Reject && n != null && n > 0)
                {
                    msgs.Add(new CheckMessage
                    {
                        Level = CheckLevels.Warn,
                        Message = "NULL " + Format.Number(n.Value) + "행은 거부되어 오류 테이블로 감"
                    });
                }
            }

            if (mapping != null && mapping.NullRule == NullRules.Empty && target != null && target.Nullable)
            {
                msgs.Add(new CheckMessage
                {
                    Level = CheckLevels.Info,
                    Message = "Oracle에서 빈 문자열('')은 NULL과 같음"
                });
            }

            return new ColumnCheck
            {
                Level = WorstMessageLevels(msgs),
                Messages = msgs,
                Type = type
            };
        }

        public static MappingStatus Status(MappingModel mapping, TableMetadata source, TableMetadata target)
        {
            return Status(mapping, source, target, null);
        }

        public static MappingStatus Status(
            MappingModel mapping,
            TableMetadata source,
            TableMetadata target,
            IReadOnlyDictionary<string, ColumnStats> measured)
        {
            if (source == null || target == null)
            {
                return new MappingStatus
                {
                    Mapped = 0,
                    Total = 0,
                    Level = CheckLevels.Error,
                    Errors = 1,
                    Warns = 0,
                    Results = new List<ColumnResult>()
                };
            }

            var results = new List<ColumnResult>();
            foreach (var t in target.Columns ?? new List<ColumnMetadata>())
            {
                var cm = mapping != null ? mapping.FindColumn(t.Name) : null;
                if (cm == null)
                {
                    cm = new ColumnMapping
                    {
                        Target = t.Name,
                        Source = null,
                        Expr = "",
                        NullRule = DefaultNullRule(t),
                        DefaultValue = ""
                    };
                }

                var check = CheckColumn(cm, t, source.Columns, mapping != null ? mapping.Mode : null, measured);
                results.Add(new ColumnResult { Target = t, Mapping = cm, Check = check });
            }

            var mapped = 0;
            foreach (var r in results)
            {
                var vs = ValueSource(r.Mapping);
                if (!string.IsNullOrEmpty(vs) ||
                    (r.Mapping.NullRule != NullRules.Allow && !string.IsNullOrEmpty(r.Mapping.DefaultValue)))
                {
                    mapped++;
                }
            }

            var levels = results.Select(r => r.Check.Level).ToList();
            if (mapping != null && WriteModes.Of(mapping.Mode).NeedsKey &&
                (mapping.MergeKey == null || mapping.MergeKey.Count == 0))
            {
                levels.Add(CheckLevels.Error);
            }

            var errors = levels.Count(l => l == CheckLevels.Error);
            var warns = levels.Count(l => l == CheckLevels.Warn);

            return new MappingStatus
            {
                Mapped = mapped,
                Total = target.Columns != null ? target.Columns.Count : 0,
                Level = CheckLevels.Worst(levels),
                Errors = errors,
                Warns = warns,
                Results = results
            };
        }

        private static Func<string, string> ColTypeResolver(IList<ColumnMetadata> cols)
        {
            return name =>
            {
                var bare = name;
                var dot = name.LastIndexOf('.');
                if (dot >= 0)
                {
                    bare = name.Substring(dot + 1);
                }

                var c = FindColumnByName(cols, bare);
                return c != null ? c.Type : null;
            };
        }

        private static ColumnMetadata FindColumnByName(IList<ColumnMetadata> cols, string name)
        {
            if (cols == null)
            {
                return null;
            }

            foreach (var c in cols)
            {
                if (c.Name == name)
                {
                    return c;
                }
            }

            return null;
        }

        private static string StripDefaultQuotes(string defaultValue)
        {
            if (string.IsNullOrEmpty(defaultValue))
            {
                return "";
            }

            var v = defaultValue;
            if (v.StartsWith("'", StringComparison.Ordinal))
            {
                v = v.Substring(1);
            }

            if (v.EndsWith("'", StringComparison.Ordinal))
            {
                v = v.Substring(0, v.Length - 1);
            }

            return v;
        }

        private static string WorstMessageLevels(IList<CheckMessage> msgs)
        {
            var levels = msgs.Select(m => m.Level);
            return CheckLevels.Worst(levels);
        }
    }
}
