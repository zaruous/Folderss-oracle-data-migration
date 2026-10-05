using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MigrationStudio.Core.Expressions
{
    internal static class ExpressionTokenizer
    {
        // POC TOKEN_RX — \w 대신 [A-Za-z0-9_]
        private static readonly Regex TokenRx = new Regex(
            @"\s+|--[^\n]*|/\*[\s\S]*?\*/|('(?:[^']|'')*')|(\d+(?:\.\d+)?)|(:[A-Za-z_][A-Za-z0-9_$#]*)|(""[^""]+""|[A-Za-z_][A-Za-z0-9_$#]*)(\s*\.\s*(?:""[^""]+""|[A-Za-z_][A-Za-z0-9_$#]*|\*))?|(\|\||<>|!=|>=|<=|[-+*\/(),=<>])",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static List<ExpressionToken> Tokenize(string src)
        {
            var outList = new List<ExpressionToken>();
            var pos = 0;
            while (pos < src.Length)
            {
                var m = TokenRx.Match(src, pos);
                if (!m.Success || m.Index != pos)
                {
                    if (src[pos] == '\'')
                    {
                        throw new SqlParseException("ORA-01756", "인용부호가 올바르게 끝나지 않았습니다", pos);
                    }

                    throw new SqlParseException("ORA-00911", "문자가 부적합합니다: '" + src[pos] + "'", pos);
                }

                var at = pos;
                pos = m.Index + m.Length;

                if (m.Groups[1].Success)
                {
                    var raw = m.Groups[1].Value;
                    var inner = raw.Substring(1, raw.Length - 2).Replace("''", "'");
                    outList.Add(new ExpressionToken { Type = "str", Value = inner, At = at, Raw = raw });
                }
                else if (m.Groups[2].Success)
                {
                    var raw = m.Groups[2].Value;
                    outList.Add(new ExpressionToken
                    {
                        Type = "num",
                        Value = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture),
                        At = at,
                        Raw = raw
                    });
                }
                else if (m.Groups[3].Success)
                {
                    var raw = m.Groups[3].Value;
                    outList.Add(new ExpressionToken
                    {
                        Type = "bind",
                        Value = raw.Substring(1).ToUpperInvariant(),
                        At = at,
                        Raw = raw
                    });
                }
                else if (m.Groups[4].Success)
                {
                    var idPart = m.Groups[4].Value;
                    var head = idPart.StartsWith("\"", StringComparison.Ordinal)
                        ? idPart.Substring(1, idPart.Length - 2)
                        : idPart.ToUpperInvariant();
                    string name;
                    if (m.Groups[5].Success)
                    {
                        var dotPart = m.Groups[5].Value;
                        var tail = dotPart.Replace(" ", "").Replace(".", "").Replace("\"", "");
                        if (tail == "*")
                        {
                            name = head + "." + "*";
                        }
                        else if (dotPart.IndexOf('"') >= 0)
                        {
                            name = head + "." + tail;
                        }
                        else
                        {
                            name = head + "." + tail.ToUpperInvariant();
                        }
                    }
                    else
                    {
                        name = head;
                    }

                    outList.Add(new ExpressionToken
                    {
                        Type = "id",
                        Value = name,
                        At = at,
                        Raw = m.Value,
                        Quoted = idPart.StartsWith("\"", StringComparison.Ordinal)
                    });
                }
                else if (m.Groups[6].Success)
                {
                    var raw = m.Groups[6].Value;
                    outList.Add(new ExpressionToken { Type = "op", Value = raw, At = at, Raw = raw });
                }
            }

            return outList;
        }
    }
}
