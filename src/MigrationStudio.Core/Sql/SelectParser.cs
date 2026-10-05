using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Expressions;

namespace MigrationStudio.Core.Sql
{
    public static class SelectParser
    {
        private static readonly Regex StarRx = new Regex(
            @"^([A-Za-z_][\w$#]*\.)?\*$",
            RegexOptions.CultureInvariant);
        private static readonly Regex AsAliasRx = new Regex(
            @"^([\s\S]*?)\s+AS\s+(""[^""]+""|[A-Za-z_][\w$#]*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex ImplicitAliasRx = new Regex(
            @"^([\s\S]*[^\s.])\s+(""[^""]+""|[A-Za-z_][\w$#]*)$",
            RegexOptions.CultureInvariant);
        private static readonly Regex EndNullRx = new Regex(@"^(END|NULL)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex FromTableRx = new Regex(
            @"(^|,|\bJOIN\b)\s*(\(|[A-Za-z_][\w$#]*(?:\.[A-Za-z_][\w$#]*)?)(?:\s+(?:AS\s+)?([A-Za-z_][\w$#]*))?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex OnClauseRx = new Regex(
            @"\bON\s+([\s\S]+?)(?=\b(?:LEFT|RIGHT|INNER|FULL|CROSS|JOIN)\b|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LeftBeforeJoinRx = new Regex(
            @"\bLEFT\s+(OUTER\s+)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex BindRx = new Regex(
            @"('(?:[^']|'')*')|:([A-Za-z_][\w$#]*)",
            RegexOptions.CultureInvariant);
        private static readonly Regex WordStartRx = new Regex(
            @"^[A-Za-z_][A-Za-z0-9_$#]*",
            RegexOptions.CultureInvariant);
        private static readonly Regex TrailingWsRx = new Regex(@"[;\s]+$", RegexOptions.CultureInvariant);

        private static readonly HashSet<string> JoinStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ON", "LEFT", "RIGHT", "INNER", "FULL", "CROSS", "JOIN", "WHERE", "OUTER", "GROUP", "ORDER", "USING"
        };

        private static readonly string[] ClauseStop =
        {
            "WHERE", "GROUP", "ORDER", "HAVING", "CONNECT", "UNION", "MINUS", "INTERSECT", "FETCH"
        };

        internal static IReadOnlyList<string> TopLevelWordNames(string sql)
        {
            return TopLevelWords(sql).Words.Select(w => w.Word).ToList();
        }

        internal static int TopLevelBalance(string sql)
        {
            return TopLevelWords(sql).Balanced;
        }

        public static SelectStatement Parse(string sqlRaw)
        {
            var res = new SelectStatement();
            var sql = TrailingWsRx.Replace(SqlText.StripComments(sqlRaw ?? ""), "");
            void Err(string code, string msg, int? at)
            {
                res.Errors.Add(new SqlError
                {
                    Code = code,
                    Message = msg,
                    Line = at == null ? (int?)null : SqlText.LineOf(sqlRaw, at.Value)
                });
            }

            if (string.IsNullOrWhiteSpace(sql))
            {
                Err("ORA-00900", "SQL 문이 없습니다", 0);
                return res;
            }

            var quoteCount = 0;
            foreach (var ch in sql)
            {
                if (ch == '\'')
                {
                    quoteCount++;
                }
            }

            if (quoteCount % 2 != 0)
            {
                Err("ORA-01756", "인용부호가 올바르게 끝나지 않았습니다", sql.LastIndexOf('\''));
            }

            var top = TopLevelWords(sql);
            if (top.Balanced > 0)
            {
                Err("ORA-00907", "오른쪽 괄호가 없습니다", sql.Length);
            }

            if (top.Balanced < 0)
            {
                Err("ORA-00933", "괄호가 맞지 않습니다", sql.LastIndexOf(')'));
            }

            var first = top.Words.Count > 0 ? top.Words[0] : null;
            if (first == null || (first.Word != "SELECT" && first.Word != "WITH"))
            {
                Err("ORA-00900", "SELECT 문만 매핑할 수 있습니다", first != null ? first.At : 0);
                return res;
            }

            if (first.Word == "WITH")
            {
                res.Warnings.Add("WITH 절은 POC 미리보기에서 계산하지 않습니다(실제 구현은 Oracle이 실행)");
            }

            TopLevelWord sel = null;
            TopLevelWord from = null;
            var selIndex = -1;
            for (var wi = 0; wi < top.Words.Count; wi++)
            {
                var w = top.Words[wi];
                if (w.Word == "SELECT" && sel == null)
                {
                    sel = w;
                    selIndex = wi;
                }

                if (w.Word == "FROM" && sel != null && w.At > sel.At && from == null)
                {
                    from = w;
                }
            }

            if (from == null)
            {
                Err("ORA-00923", "FROM 키워드가 필요합니다", sql.Length);
                return res;
            }

            TopLevelWord After(TopLevelWord w)
            {
                for (var i = 0; i < top.Words.Count; i++)
                {
                    var x = top.Words[i];
                    if (x.At <= w.At)
                    {
                        continue;
                    }

                    foreach (var stop in ClauseStop)
                    {
                        if (x.Word == stop)
                        {
                            return x;
                        }
                    }
                }

                return null;
            }

            var listStart = sel.End;
            if (selIndex + 1 < top.Words.Count)
            {
                var second = top.Words[selIndex + 1];
                if ((second.Word == "DISTINCT" || second.Word == "UNIQUE") &&
                    string.IsNullOrWhiteSpace(sql.Substring(sel.End, second.At - sel.End)))
                {
                    listStart = second.End;
                }
            }

            var list = sql.Substring(listStart, from.At - listStart);
            if (string.IsNullOrWhiteSpace(list))
            {
                Err("ORA-00936", "선택 목록이 비어 있습니다", from.At);
            }
            else
            {
                foreach (var p in SplitTopLevel(list, listStart))
                {
                    res.Items.Add(ParseSelectItem(p.Text, p.At));
                }
            }

            var fromEnd = After(from);
            var fromText = sql.Substring(from.End, (fromEnd != null ? fromEnd.At : sql.Length) - from.End);
            res.Tables = FromTables(fromText);

            TopLevelWord whereWord = null;
            for (var i = 0; i < top.Words.Count; i++)
            {
                var w = top.Words[i];
                if (w.Word == "WHERE" && w.At > from.At)
                {
                    whereWord = w;
                    break;
                }
            }

            if (whereWord != null)
            {
                var end = After(whereWord);
                var whereText = sql.Substring(whereWord.End, (end != null ? end.At : sql.Length) - whereWord.End);
                var c = ExpressionAnalyzer.Analyze(whereText.Trim());
                res.Where = new WhereClause
                {
                    Text = whereText.Trim(),
                    Node = c.Node,
                    Error = c.Error
                };
                if (c.Error != null)
                {
                    var pos = whereWord.End + (c.Error.Position >= 0 ? c.Error.Position : 0);
                    Err(c.Error.Code, "WHERE: " + StripOraPrefix(c.Error), pos);
                }
            }

            TopLevelWord order = null;
            for (var i = 0; i < top.Words.Count; i++)
            {
                var w = top.Words[i];
                if (w.Word != "ORDER" || i + 1 >= top.Words.Count)
                {
                    continue;
                }

                if (top.Words[i + 1].Word == "BY" && w.At > from.At)
                {
                    order = w;
                    break;
                }
            }

            if (order != null)
            {
                var orderByIdx = top.Words.IndexOf(order);
                var byWord = top.Words[orderByIdx + 1];
                res.OrderBy = sql.Substring(byWord.End).Trim();
            }

            res.GroupBy = false;
            foreach (var w in top.Words)
            {
                if (w.Word == "GROUP")
                {
                    res.GroupBy = true;
                    break;
                }
            }

            if (res.GroupBy)
            {
                res.Warnings.Add("GROUP BY 결과는 POC 미리보기에서 집계하지 않습니다");
            }

            foreach (var w in top.Words)
            {
                if (w.Word == "UNION" || w.Word == "MINUS" || w.Word == "INTERSECT")
                {
                    res.Warnings.Add("집합 연산(UNION 등)은 첫 SELECT만 미리봅니다");
                    break;
                }
            }

            foreach (var it in res.Items)
            {
                if (it.Error == null)
                {
                    continue;
                }

                var at = it.Position;
                if (it.Error.Position >= 0)
                {
                    at = it.Position + it.Error.Position;
                }

                Err(
                    it.Error.Code,
                    (it.Alias ?? "항목") + ": " + StripOraPrefix(it.Error),
                    at);
            }

            var bindSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bindList = new List<string>();
            BindRx.Replace(sql, m =>
            {
                if (m.Groups[2].Success)
                {
                    var b = m.Groups[2].Value.ToUpperInvariant();
                    if (bindSet.Add(b))
                    {
                        bindList.Add(b);
                    }
                }

                return m.Value;
            });
            res.Binds = bindList;
            return res;
        }

        private static SelectItem ParseSelectItem(string raw, int at)
        {
            var lead = raw.Length - raw.TrimStart().Length;
            var text = raw.Trim();
            var item = new SelectItem
            {
                Text = text,
                Position = at + lead,
                Alias = null,
                Expr = text
            };

            if (StarRx.IsMatch(text))
            {
                item.Star = true;
                item.Name = text;
                return item;
            }

            Match m = AsAliasRx.Match(text);
            if (!m.Success)
            {
                var t = ImplicitAliasRx.Match(text);
                if (t.Success &&
                    !EndNullRx.IsMatch(t.Groups[2].Value) &&
                    ExpressionAnalyzer.Analyze(t.Groups[1].Value.Trim()).Error == null &&
                    ExpressionAnalyzer.Analyze(text).Error != null)
                {
                    m = t;
                }
            }

            if (m.Success)
            {
                item.Expr = m.Groups[1].Value.Trim();
                var aliasToken = m.Groups[2].Value;
                item.Alias = aliasToken.StartsWith("\"", StringComparison.Ordinal)
                    ? aliasToken.Substring(1, aliasToken.Length - 2)
                    : aliasToken.ToUpperInvariant();
            }

            var c = ExpressionAnalyzer.Analyze(item.Expr);
            item.Node = c.Node;
            item.Refs = c.Refs;
            item.Error = c.Error;

            if (item.Alias != null)
            {
                item.Name = item.Alias;
            }
            else if (c.Node != null && c.Node.Kind == "col")
            {
                var colName = c.Node.Name;
                var dot = colName.LastIndexOf('.');
                item.Name = dot >= 0 ? colName.Substring(dot + 1) : colName;
            }
            else
            {
                var compact = Regex.Replace(item.Expr, @"\s+", "");
                item.Name = compact.Length <= 30
                    ? compact.ToUpperInvariant()
                    : compact.Substring(0, 30).ToUpperInvariant();
            }

            return item;
        }

        private static List<TableRef> FromTables(string text)
        {
            var tables = new List<TableRef>();
            var ons = new List<string>();
            foreach (Match om in OnClauseRx.Matches(text))
            {
                ons.Add(om.Groups[1].Value.Trim());
            }

            var searchFrom = 0;
            while (searchFrom <= text.Length)
            {
                var m = FromTableRx.Match(text, searchFrom);
                if (!m.Success)
                {
                    break;
                }

                if (m.Groups[2].Value == "(")
                {
                    tables.Add(new TableRef { Subquery = true });
                    searchFrom = m.Index + m.Length;
                    continue;
                }

                var full = m.Groups[2].Value.ToUpperInvariant();
                string alias = null;
                if (m.Groups[3].Success && !JoinStop.Contains(m.Groups[3].Value))
                {
                    alias = m.Groups[3].Value.ToUpperInvariant();
                }
                else if (m.Groups[3].Success)
                {
                    searchFrom = m.Index + m.Length - m.Groups[3].Length;
                    continue;
                }

                var parts = full.Split('.');
                var kind = m.Groups[1].Value.ToUpperInvariant();
                string join = null;
                if (kind == "JOIN")
                {
                    var before = text.Substring(0, m.Index);
                    join = LeftBeforeJoinRx.IsMatch(before) ? "LEFT" : "INNER";
                }
                else if (kind == ",")
                {
                    join = "CROSS";
                }

                tables.Add(new TableRef
                {
                    Schema = parts.Length > 1 ? parts[0] : null,
                    Name = parts[parts.Length - 1],
                    Alias = alias,
                    Join = join
                });
                searchFrom = m.Index + m.Length;
            }

            var k = 0;
            foreach (var t in tables)
            {
                if (t.Join != null && t.Join != "CROSS")
                {
                    t.On = k < ons.Count ? ons[k] : null;
                    k++;
                }
            }

            return tables;
        }

        private sealed class TopLevelWord
        {
            public string Word;
            public int At;
            public int End;
        }

        private sealed class TopLevelParts
        {
            public List<TopLevelWord> Words = new List<TopLevelWord>();
            public int Balanced;
        }

        private sealed class SplitPart
        {
            public string Text;
            public int At;
        }

        private static TopLevelParts TopLevelWords(string sql)
        {
            var outWords = new TopLevelParts();
            var depth = 0;
            var i = 0;
            while (i < sql.Length)
            {
                var ch = sql[i];
                if (ch == '\'')
                {
                    i = SkipStringLiteral(sql, i);
                    continue;
                }

                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth--;
                }
                else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || ch == '_')
                {
                    var prevOk = i == 0 || !IsIdentBoundary(sql[i - 1]);
                    var j = i + 1;
                    while (j < sql.Length && IsIdentContinue(sql[j]))
                    {
                        j++;
                    }

                    if (j > i)
                    {
                        if (depth == 0 && prevOk)
                        {
                            var word = sql.Substring(i, j - i).ToUpperInvariant();
                            outWords.Words.Add(new TopLevelWord
                            {
                                Word = word,
                                At = i,
                                End = j
                            });
                        }

                        i = j;
                        continue;
                    }
                }

                i++;
            }

            outWords.Balanced = depth;
            return outWords;
        }

        private static int SkipStringLiteral(string sql, int start)
        {
            var i = start + 1;
            while (i < sql.Length)
            {
                if (sql[i] == '\'')
                {
                    if (i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }

                    return i + 1;
                }

                i++;
            }

            return sql.Length;
        }

        private static bool IsIdentBoundary(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                   c == '_' || c == '$' || c == '#' || c == '.' || c == ':';
        }

        private static bool IsIdentContinue(char c)
        {
            return IsIdentBoundary(c);
        }

        private static List<SplitPart> SplitTopLevel(string text, int baseAt)
        {
            var parts = new List<SplitPart>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '\'')
                {
                    i = SkipStringLiteral(text, i) - 1;
                    continue;
                }

                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    depth--;
                }
                else if (ch == ',' && depth == 0)
                {
                    parts.Add(new SplitPart { Text = text.Substring(start, i - start), At = baseAt + start });
                    start = i + 1;
                }
            }

            parts.Add(new SplitPart { Text = text.Substring(start), At = baseAt + start });
            return parts;
        }

        private static string StripOraPrefix(SqlParseException ex)
        {
            if (ex == null)
            {
                return "";
            }

            var prefix = ex.Code + ": ";
            var full = ex.Message;
            return full.StartsWith(prefix, StringComparison.Ordinal) ? full.Substring(prefix.Length) : full;
        }
    }
}
