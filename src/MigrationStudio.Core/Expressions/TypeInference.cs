using System;
using System.Collections.Generic;
using System.Globalization;

namespace MigrationStudio.Core.Expressions
{
    public static class TypeInference
    {
        public static string Infer(ExpressionNode node, Func<string, string> columnType)
        {
            if (node == null)
            {
                return null;
            }

            return InferNode(node, columnType);
        }

        public static string Merge(IEnumerable<string> types)
        {
            var list = new List<string>();
            if (types != null)
            {
                foreach (var t in types)
                {
                    if (!string.IsNullOrEmpty(t))
                    {
                        list.Add(t);
                    }
                }
            }

            return MergeTypes(list);
        }

        private static string InferNode(ExpressionNode n, Func<string, string> columnType)
        {
            switch (n.Kind)
            {
                case "lit":
                    if (n.Value == null)
                    {
                        return null;
                    }

                    if (n.Value is double || n.Value is float || n.Value is int || n.Value is long || n.Value is decimal)
                    {
                        return "NUMBER";
                    }

                    return "CHAR(" + Convert.ToString(n.Value, CultureInfo.InvariantCulture).Length + ")";
                case "col":
                    return columnType(n.Name);
                case "bind":
                    return null;
                case "now":
                    return n.Timestamp ? "TIMESTAMP" : "DATE";
                case "cast":
                    return n.TypeName;
                case "neg":
                    return "NUMBER";
                case "case":
                {
                    var types = new List<string>();
                    if (n.Whens != null)
                    {
                        foreach (var w in n.Whens)
                        {
                            var t = InferNode(w.Value, columnType);
                            if (!string.IsNullOrEmpty(t))
                            {
                                types.Add(t);
                            }
                        }
                    }

                    if (n.Else != null)
                    {
                        var t = InferNode(n.Else, columnType);
                        if (!string.IsNullOrEmpty(t))
                        {
                            types.Add(t);
                        }
                    }

                    return MergeTypes(types);
                }
                case "bin":
                    if (n.Op == "||")
                    {
                        return "VARCHAR2(" + Math.Min(4000, Len(InferNode(n.Left, columnType), columnType) + Len(InferNode(n.Right, columnType), columnType)) + ")";
                    }

                    if (n.Op == "+" || n.Op == "-")
                    {
                        var l = InferNode(n.Left, columnType);
                        if (!string.IsNullOrEmpty(l) && ExpressionTypeParse.IsDate(ExpressionTypeParse.Parse(l)))
                        {
                            return l;
                        }

                        return "NUMBER";
                    }

                    if (n.Op == "*" || n.Op == "/")
                    {
                        return "NUMBER";
                    }

                    return null;
                case "fn":
                    return InferFunction(n, columnType);
            }

            return null;
        }

        private static string InferFunction(ExpressionNode n, Func<string, string> columnType)
        {
            string a0 = n.Args != null && n.Args.Count > 0 ? InferNode(n.Args[0], columnType) : null;
            switch (n.Name)
            {
                case "TRIM":
                case "LTRIM":
                case "RTRIM":
                case "UPPER":
                case "LOWER":
                case "INITCAP":
                case "REGEXP_REPLACE":
                case "REPLACE":
                case "REGEXP_SUBSTR":
                    return "VARCHAR2(" + Len(a0, columnType) + ")";
                case "SUBSTR":
                {
                    var l = n.Args != null && n.Args.Count > 2 && n.Args[2].Kind == "lit" && n.Args[2].Value != null
                        ? Convert.ToDouble(n.Args[2].Value, CultureInfo.InvariantCulture)
                        : (double)Len(a0, columnType);
                    return "VARCHAR2(" + Math.Min(l, Len(a0, columnType)) + ")";
                }
                case "LPAD":
                case "RPAD":
                {
                    var l = n.Args != null && n.Args.Count > 1 && n.Args[1].Kind == "lit" && n.Args[1].Value != null
                        ? Convert.ToDouble(n.Args[1].Value, CultureInfo.InvariantCulture)
                        : 4000;
                    return "VARCHAR2(" + l.ToString(CultureInfo.InvariantCulture) + ")";
                }
                case "NVL":
                case "COALESCE":
                    return MergeArgTypes(n, columnType, 0);
                case "NVL2":
                    return MergeArgTypes(n, columnType, 1);
                case "DECODE":
                    return InferDecode(n, columnType);
                case "TO_CHAR":
                    return "VARCHAR2(" + (a0 != null && ExpressionTypeParse.IsDate(ExpressionTypeParse.Parse(a0)) ? 26 : Len(a0, columnType)) + ")";
                case "TO_DATE":
                    return "DATE";
                case "TRUNC":
                    if (a0 != null && !ExpressionTypeParse.IsDate(ExpressionTypeParse.Parse(a0)))
                    {
                        return "NUMBER";
                    }

                    return "DATE";
                case "TO_TIMESTAMP":
                    return "TIMESTAMP";
                case "MAX":
                case "MIN":
                    return a0;
                default:
                    return "NUMBER";
            }
        }

        private static string MergeArgTypes(ExpressionNode n, Func<string, string> columnType, int start)
        {
            var types = new List<string>();
            if (n.Args != null)
            {
                for (var i = start; i < n.Args.Count; i++)
                {
                    var t = InferNode(n.Args[i], columnType);
                    if (!string.IsNullOrEmpty(t))
                    {
                        types.Add(t);
                    }
                }
            }

            return MergeTypes(types);
        }

        private static string InferDecode(ExpressionNode n, Func<string, string> columnType)
        {
            var types = new List<string>();
            if (n.Args != null)
            {
                for (var k = 0; k < n.Args.Count; k++)
                {
                    if (k > 1 && k % 2 == 0)
                    {
                        var t = InferNode(n.Args[k], columnType);
                        if (!string.IsNullOrEmpty(t))
                        {
                            types.Add(t);
                        }
                    }
                }

                if (n.Args.Count % 2 == 0)
                {
                    var t = InferNode(n.Args[n.Args.Count - 1], columnType);
                    if (!string.IsNullOrEmpty(t))
                    {
                        types.Add(t);
                    }
                }
            }

            return MergeTypes(types);
        }

        private static int Len(string t, Func<string, string> columnType)
        {
            var p = ExpressionTypeParse.Parse(t);
            if (p == null)
            {
                return 4000;
            }

            if (p.Len != null)
            {
                return p.Len.Value;
            }

            if (p.Base == "NUMBER")
            {
                return (p.Prec ?? 38) + 2;
            }

            if (ExpressionTypeParse.IsDate(p))
            {
                return 26;
            }

            return 4000;
        }

        private static string MergeTypes(List<string> types)
        {
            if (types == null || types.Count == 0)
            {
                return null;
            }

            var parsed = new List<ExpressionTypeParse.ParsedType>();
            foreach (var t in types)
            {
                parsed.Add(ExpressionTypeParse.Parse(t));
            }

            if (parsed.TrueForAll(ExpressionTypeParse.IsChar))
            {
                var max = 0;
                foreach (var p in parsed)
                {
                    if ((p.Len ?? 0) > max)
                    {
                        max = p.Len ?? 0;
                    }
                }

                var allChar = parsed.TrueForAll(p => p.Base == "CHAR") && parsed.TrueForAll(p => (p.Len ?? 0) == max);

                return (allChar ? "CHAR(" : "VARCHAR2(") + (max == 0 ? 4000 : max) + ")";
            }

            if (parsed.TrueForAll(p => p.Base == "NUMBER"))
            {
                var precs = new List<int?>();
                foreach (var p in parsed)
                {
                    precs.Add(p.Prec);
                }

                foreach (var p in precs)
                {
                    if (p == null)
                    {
                        return "NUMBER";
                    }
                }

                var maxPrec = 0;
                foreach (var p in precs)
                {
                    if (p.Value > maxPrec)
                    {
                        maxPrec = p.Value;
                    }
                }

                var hasScale = false;
                foreach (var p in parsed)
                {
                    // POC: parsed.some((p) => p.scale) — scale 0은 falsy
                    if (p.Scale != null && p.Scale.Value != 0)
                    {
                        hasScale = true;
                        break;
                    }
                }

                if (hasScale)
                {
                    var maxScale = 0;
                    foreach (var p in parsed)
                    {
                        var sc = p.Scale ?? 0;
                        if (sc > maxScale)
                        {
                            maxScale = sc;
                        }
                    }

                    return "NUMBER(" + maxPrec + "," + maxScale + ")";
                }

                return "NUMBER(" + maxPrec + ")";
            }

            if (parsed.TrueForAll(ExpressionTypeParse.IsDate))
            {
                foreach (var p in parsed)
                {
                    if (p.Base == "TIMESTAMP")
                    {
                        return "TIMESTAMP";
                    }
                }

                return "DATE";
            }

            return types[0];
        }
    }
}
