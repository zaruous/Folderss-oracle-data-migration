using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MigrationStudio.Core.Jobs
{
    /// <summary>POC job.js toYaml — 작업 파일 보기·저장용 최소 YAML.</summary>
    internal static class JobYaml
    {
        private static readonly Regex ScalarQuoteRx = new Regex(
            @"^[\s]|[\s]$|[:#\[\]{},&*!|>'""%@`]|^(true|false|null|yes|no|-?\d[\d.]*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex SafeKeyRx = new Regex(@"^[\w$.-]+$", RegexOptions.CultureInvariant);

        public static string Emit(object value)
        {
            var body = ToYaml(value, 0);
            if (body.StartsWith("\n", StringComparison.Ordinal))
            {
                body = body.Substring(1);
            }

            return body + "\n";
        }

        private static string ToYaml(object value, int depth)
        {
            var pad = new string(' ', depth * 2);
            if (value == null)
            {
                return " null";
            }

            if (value is string s)
            {
                return " " + Scalar(s);
            }

            if (value is bool b)
            {
                return " " + (b ? "true" : "false");
            }

            if (value is int || value is long || value is decimal || value is double || value is float)
            {
                return " " + Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            if (value is IDictionary dict)
            {
                var keys = new List<string>();
                foreach (DictionaryEntry entry in dict)
                {
                    keys.Add(Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                }

                if (keys.Count == 0)
                {
                    return " {}";
                }

                var sb = new StringBuilder();
                foreach (var key in keys)
                {
                    var v = dict[key];
                    var keyText = SafeKeyRx.IsMatch(key) ? key : "'" + key.Replace("'", "''") + "'";
                    if (v is string vs && vs.Contains("\n"))
                    {
                        sb.Append('\n').Append(pad).Append(keyText).Append(": |\n");
                        foreach (var line in vs.Split('\n'))
                        {
                            sb.Append(pad).Append("  ").Append(line).Append('\n');
                        }

                        sb.Length -= 1;
                    }
                    else if (v != null && IsComplex(v))
                    {
                        sb.Append('\n').Append(pad).Append(keyText).Append(':').Append(ToYaml(v, depth + 1));
                    }
                    else
                    {
                        sb.Append('\n').Append(pad).Append(keyText).Append(": ").Append(Scalar(v));
                    }
                }

                return sb.ToString();
            }

            if (value is IEnumerable list && value is not string)
            {
                var items = new List<object>();
                foreach (var item in list)
                {
                    items.Add(item);
                }

                if (items.Count == 0)
                {
                    return " []";
                }

                var sb = new StringBuilder();
                foreach (var item in items)
                {
                    if (item != null && IsComplex(item))
                    {
                        var inner = ToYaml(item, depth + 1);
                        if (inner.StartsWith("\n", StringComparison.Ordinal))
                        {
                            inner = inner.Substring(1);
                        }

                        sb.Append('\n').Append(pad).Append("- ").Append(inner.TrimStart());
                    }
                    else
                    {
                        sb.Append('\n').Append(pad).Append("- ").Append(Scalar(item));
                    }
                }

                return sb.ToString();
            }

            return " " + Scalar(value);
        }

        private static bool IsComplex(object value)
        {
            return value is IDictionary || (value is IEnumerable e && value is not string && value is not IDictionary);
        }

        private static string Scalar(object v)
        {
            if (v == null)
            {
                return "null";
            }

            if (v is bool b)
            {
                return b ? "true" : "false";
            }

            if (v is int || v is long || v is decimal || v is double || v is float)
            {
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            }

            var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            if (s.Length == 0 || ScalarQuoteRx.IsMatch(s))
            {
                return "'" + s.Replace("'", "''") + "'";
            }

            return s;
        }
    }
}
