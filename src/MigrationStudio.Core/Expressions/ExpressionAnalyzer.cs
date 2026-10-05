using System;
using System.Collections.Generic;

namespace MigrationStudio.Core.Expressions
{
    public static class ExpressionAnalyzer
    {
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, ExpressionAnalysis> Cache = new Dictionary<string, ExpressionAnalysis>(StringComparer.Ordinal);

        public static ExpressionAnalysis Analyze(string expression)
        {
            var key = expression ?? "";
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            ExpressionAnalysis result;
            try
            {
                var ast = ExpressionParser.Parse(key);
                var refs = new List<string>();
                var binds = new List<string>();
                var refSet = new HashSet<string>(StringComparer.Ordinal);
                var bindSet = new HashSet<string>(StringComparer.Ordinal);
                foreach (var n in Walk(ast))
                {
                    if (n.Kind == "col" && refSet.Add(n.Name))
                    {
                        refs.Add(n.Name);
                    }

                    if (n.Kind == "bind" && bindSet.Add(n.Name))
                    {
                        binds.Add(n.Name);
                    }
                }

                result = new ExpressionAnalysis(ast, refs, binds, null);
            }
            catch (SqlParseException ex)
            {
                result = new ExpressionAnalysis(null, Array.Empty<string>(), Array.Empty<string>(), ex);
            }

            lock (CacheLock)
            {
                if (Cache.Count > 500)
                {
                    Cache.Clear();
                }

                Cache[key] = result;
            }

            return result;
        }

        public static IEnumerable<ExpressionNode> Walk(ExpressionNode node)
        {
            if (node == null)
            {
                yield break;
            }

            yield return node;

            if (node.Operand != null)
            {
                foreach (var x in Walk(node.Operand))
                {
                    yield return x;
                }
            }

            if (node.Left != null)
            {
                foreach (var x in Walk(node.Left))
                {
                    yield return x;
                }
            }

            if (node.Right != null)
            {
                foreach (var x in Walk(node.Right))
                {
                    yield return x;
                }
            }

            if (node.Pattern != null)
            {
                foreach (var x in Walk(node.Pattern))
                {
                    yield return x;
                }
            }

            if (node.Low != null)
            {
                foreach (var x in Walk(node.Low))
                {
                    yield return x;
                }
            }

            if (node.High != null)
            {
                foreach (var x in Walk(node.High))
                {
                    yield return x;
                }
            }

            if (node.Subject != null)
            {
                foreach (var x in Walk(node.Subject))
                {
                    yield return x;
                }
            }

            if (node.Else != null)
            {
                foreach (var x in Walk(node.Else))
                {
                    yield return x;
                }
            }

            if (node.Args != null)
            {
                foreach (var a in node.Args)
                {
                    foreach (var x in Walk(a))
                    {
                        yield return x;
                    }
                }
            }

            if (node.Items != null)
            {
                foreach (var a in node.Items)
                {
                    foreach (var x in Walk(a))
                    {
                        yield return x;
                    }
                }
            }

            if (node.Whens != null)
            {
                foreach (var w in node.Whens)
                {
                    foreach (var x in Walk(w.Condition))
                    {
                        yield return x;
                    }

                    foreach (var x in Walk(w.Value))
                    {
                        yield return x;
                    }
                }
            }
        }
    }
}
