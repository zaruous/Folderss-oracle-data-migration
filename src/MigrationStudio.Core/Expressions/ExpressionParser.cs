using System;
using System.Collections.Generic;

namespace MigrationStudio.Core.Expressions
{
    internal static class ExpressionParser
    {
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            "SELECT", "FROM", "WHERE", "AS", "THEN", "ELSE", "END", "WHEN", "AND", "OR", "ON", "JOIN",
            "GROUP", "ORDER", "BY", "HAVING", "IS", "IN", "LIKE", "BETWEEN"
        };

        internal static ExpressionNode Parse(string src)
        {
            var toks = ExpressionTokenizer.Tokenize(src);
            var state = new ParseState { Src = src, Toks = toks };
            if (toks.Count == 0)
            {
                throw new SqlParseException("ORA-00936", "식이 없습니다", 0);
            }

            var ast = state.Expr(1);
            if (state.Index < toks.Count)
            {
                throw state.Fail("ORA-00933", "SQL 명령어가 올바르게 종료되지 않았습니다");
            }

            return ast;
        }

        private sealed class ParseState
        {
            internal string Src;
            internal List<ExpressionToken> Toks;
            internal int Index;

            internal ExpressionToken Peek(int offset)
            {
                var i = Index + offset;
                return i >= 0 && i < Toks.Count ? Toks[i] : null;
            }

            internal bool IsKw(ExpressionToken tok, string w)
            {
                return tok != null && tok.Type == "id" && !tok.Quoted && (string)tok.Value == w;
            }

            internal bool IsOp(ExpressionToken tok, string o)
            {
                return tok != null && tok.Type == "op" && (string)tok.Value == o;
            }

            internal ExpressionToken Next()
            {
                if (Index >= Toks.Count)
                {
                    return null;
                }

                return Toks[Index++];
            }

            internal SqlParseException Fail(string code, string msg)
            {
                var tok = Peek(0);
                var suffix = tok != null ? " ('" + tok.Raw + "' 근처)" : " (식 끝)";
                var at = tok != null ? tok.At : Src.Length;
                return new SqlParseException(code, msg + suffix, at);
            }

            internal void ExpectOp(string o)
            {
                if (!IsOp(Peek(0), o))
                {
                    throw Fail(o == ")" ? "ORA-00907" : "ORA-00936", o == ")" ? "오른쪽 괄호가 없습니다" : "'" + o + "'가 필요합니다");
                }

                Next();
            }

            internal void ExpectKw(string w)
            {
                if (!IsKw(Peek(0), w))
                {
                    throw Fail(w == "END" ? "ORA-00905" : "ORA-00905", "키워드가 없습니다: " + w);
                }

                Next();
            }

            internal ExpressionNode Expr(int minPrec)
            {
                ExpressionNode left;
                if (IsKw(Peek(0), "NOT"))
                {
                    Next();
                    left = new ExpressionNode { Kind = "not", Operand = Expr(4) };
                }
                else
                {
                    left = Unary();
                }

                while (true)
                {
                    var tok = Peek(0);
                    if (tok == null)
                    {
                        break;
                    }

                    var prec = 0;
                    var notNext = IsKw(tok, "NOT") &&
                        (IsKw(Peek(1), "LIKE") || IsKw(Peek(1), "IN") || IsKw(Peek(1), "BETWEEN"));
                    if (IsKw(tok, "OR"))
                    {
                        prec = 1;
                    }
                    else if (IsKw(tok, "AND"))
                    {
                        prec = 2;
                    }
                    else if (tok.Type == "op" && IsCompareOp((string)tok.Value))
                    {
                        prec = 4;
                    }
                    else if (IsKw(tok, "IS") || IsKw(tok, "LIKE") || IsKw(tok, "IN") || IsKw(tok, "BETWEEN") || notNext)
                    {
                        prec = 4;
                    }
                    else if (tok.Type == "op" && (tok.Value as string == "||" || tok.Value as string == "+" || tok.Value as string == "-"))
                    {
                        prec = 5;
                    }
                    else if (tok.Type == "op" && (tok.Value as string == "*" || tok.Value as string == "/"))
                    {
                        prec = 6;
                    }

                    if (prec == 0 || prec < minPrec)
                    {
                        break;
                    }

                    Next();
                    if (IsKw(tok, "IS"))
                    {
                        var neg = IsKw(Peek(0), "NOT");
                        if (neg)
                        {
                            Next();
                        }

                        ExpectKw("NULL");
                        left = new ExpressionNode { Kind = "isnull", Operand = left, Negated = neg };
                        continue;
                    }

                    var negated = false;
                    var word = tok;
                    if (notNext)
                    {
                        negated = true;
                        word = Next();
                    }

                    if (IsKw(word, "LIKE"))
                    {
                        left = new ExpressionNode { Kind = "like", Operand = left, Pattern = Expr(5), Negated = negated };
                    }
                    else if (IsKw(word, "IN"))
                    {
                        ExpectOp("(");
                        var list = new List<ExpressionNode> { Expr(1) };
                        while (IsOp(Peek(0), ","))
                        {
                            Next();
                            list.Add(Expr(1));
                        }

                        ExpectOp(")");
                        left = new ExpressionNode { Kind = "in", Operand = left, Items = list, Negated = negated };
                    }
                    else if (IsKw(word, "BETWEEN"))
                    {
                        var lo = Expr(5);
                        ExpectKw("AND");
                        left = new ExpressionNode { Kind = "between", Operand = left, Low = lo, High = Expr(5), Negated = negated };
                    }
                    else
                    {
                        string op;
                        if (IsKw(tok, "AND"))
                        {
                            op = "AND";
                        }
                        else if (IsKw(tok, "OR"))
                        {
                            op = "OR";
                        }
                        else
                        {
                            op = (string)tok.Value;
                        }

                        left = new ExpressionNode
                        {
                            Kind = "bin",
                            Op = op,
                            Left = left,
                            Right = Expr(prec + 1)
                        };
                    }
                }

                return left;
            }

            private static bool IsCompareOp(string v)
            {
                return v == "=" || v == "<>" || v == "!=" || v == "<" || v == ">" || v == "<=" || v == ">=";
            }

            internal ExpressionNode Unary()
            {
                if (IsOp(Peek(0), "-"))
                {
                    Next();
                    return new ExpressionNode { Kind = "neg", Operand = Unary() };
                }

                if (IsOp(Peek(0), "+"))
                {
                    Next();
                    return Unary();
                }

                return Primary();
            }

            internal string TypeName()
            {
                var t = Next();
                if (t == null || t.Type != "id")
                {
                    throw Fail("ORA-00902", "데이터 형식이 부적합합니다");
                }

                var s = (string)t.Value;
                if (IsOp(Peek(0), "("))
                {
                    Next();
                    var a = Next();
                    if (a == null || a.Type != "num")
                    {
                        throw Fail("ORA-00902", "데이터 형식이 부적합합니다");
                    }

                    s += "(" + a.Value;
                    if (IsOp(Peek(0), ","))
                    {
                        Next();
                        s += "," + Next().Value;
                    }

                    ExpectOp(")");
                    s += ")";
                }

                return s;
            }

            internal ExpressionNode CaseExpr()
            {
                ExpressionNode subject = null;
                if (!IsKw(Peek(0), "WHEN"))
                {
                    subject = Expr(1);
                }

                var whens = new List<CaseWhen>();
                while (IsKw(Peek(0), "WHEN"))
                {
                    Next();
                    var cond = Expr(1);
                    ExpectKw("THEN");
                    whens.Add(new CaseWhen { Condition = cond, Value = Expr(1) });
                }

                if (whens.Count == 0)
                {
                    throw Fail("ORA-00905", "키워드가 없습니다: WHEN");
                }

                ExpressionNode els = null;
                if (IsKw(Peek(0), "ELSE"))
                {
                    Next();
                    els = Expr(1);
                }

                ExpectKw("END");
                return new ExpressionNode { Kind = "case", Subject = subject, Whens = whens, Else = els };
            }

            internal ExpressionNode Primary()
            {
                var tok = Next();
                if (tok == null)
                {
                    throw Fail("ORA-00936", "식이 없습니다");
                }

                if (tok.Type == "num")
                {
                    return new ExpressionNode { Kind = "lit", Value = tok.Value };
                }

                if (tok.Type == "str")
                {
                    var sv = (string)tok.Value;
                    return new ExpressionNode
                    {
                        Kind = "lit",
                        Value = sv == "" ? null : sv,
                        IsString = true
                    };
                }

                if (tok.Type == "bind")
                {
                    return new ExpressionNode { Kind = "bind", Name = (string)tok.Value };
                }

                if (IsOp(tok, "("))
                {
                    var e = Expr(1);
                    ExpectOp(")");
                    return e;
                }

                if (tok.Type != "id")
                {
                    Index--;
                    throw Fail("ORA-00936", "식이 없습니다");
                }

                string w = tok.Quoted ? null : (string)tok.Value;
                if (w == "NULL")
                {
                    return new ExpressionNode { Kind = "lit", Value = null };
                }

                if (w == "CASE")
                {
                    return CaseExpr();
                }

                if (w == "SYSDATE" || w == "CURRENT_DATE")
                {
                    return new ExpressionNode { Kind = "now", Timestamp = false };
                }

                if (w == "SYSTIMESTAMP" || w == "CURRENT_TIMESTAMP")
                {
                    return new ExpressionNode { Kind = "now", Timestamp = true };
                }

                if (w == "CAST")
                {
                    ExpectOp("(");
                    var e = Expr(1);
                    ExpectKw("AS");
                    var type = TypeName();
                    ExpectOp(")");
                    return new ExpressionNode { Kind = "cast", Operand = e, TypeName = type };
                }

                if (w != null && Reserved.Contains(w))
                {
                    Index--;
                    throw Fail("ORA-00936", "식이 없습니다");
                }

                if (IsOp(Peek(0), "("))
                {
                    Next();
                    var args = new List<ExpressionNode>();
                    if (IsOp(Peek(0), "*"))
                    {
                        Next();
                        args.Add(new ExpressionNode { Kind = "lit", Value = 1.0 });
                    }
                    else if (!IsOp(Peek(0), ")"))
                    {
                        if (IsKw(Peek(0), "DISTINCT"))
                        {
                            Next();
                        }

                        args.Add(Expr(1));
                        while (IsOp(Peek(0), ","))
                        {
                            Next();
                            args.Add(Expr(1));
                        }
                    }

                    ExpectOp(")");
                    return new ExpressionNode
                    {
                        Kind = "fn",
                        Name = (string)tok.Value,
                        Args = args,
                        Position = tok.At
                    };
                }

                return new ExpressionNode
                {
                    Kind = "col",
                    Name = (string)tok.Value,
                    Position = tok.At
                };
            }
        }
    }
}
