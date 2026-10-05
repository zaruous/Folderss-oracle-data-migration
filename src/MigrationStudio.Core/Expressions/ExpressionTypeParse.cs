using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MigrationStudio.Core.Expressions
{
    /// <summary>POC expression.js parseType / isChar / isDate (OracleType.cs와 별도).</summary>
    /// <summary>POC expression.js parseType (골든 types[] 기준).</summary>
    public static class ExpressionTypeParse
    {
        private static readonly Regex TypeRx = new Regex(
            @"^([A-Z0-9_ ]+?)\s*(?:\((\d+)(?:\s*,\s*(\d+))?\))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public sealed class ParsedType
        {
            public string Base;
            public int? Len;
            public int? Prec;
            public int? Scale;
            public int? Frac;
        }

        public static ParsedType Parse(string type)
        {
            if (string.IsNullOrEmpty(type))
            {
                return null;
            }

            var m = TypeRx.Match(type.Trim());
            if (!m.Success)
            {
                return new ParsedType { Base = type.ToUpperInvariant() };
            }

            var parsed = new ParsedType { Base = m.Groups[1].Value.ToUpperInvariant() };
            int? a = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : (int?)null;
            int? b = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : (int?)null;

            if (parsed.Base == "NUMBER")
            {
                parsed.Prec = a;
                parsed.Scale = b ?? (a == null ? (int?)null : 0);
            }
            else if (parsed.Base == "TIMESTAMP")
            {
                parsed.Frac = a ?? 6;
            }
            else
            {
                parsed.Len = a;
            }

            return parsed;
        }

        public static bool IsChar(ParsedType t)
        {
            if (t == null)
            {
                return false;
            }

            return t.Base == "VARCHAR2" || t.Base == "CHAR" || t.Base == "NVARCHAR2" || t.Base == "VARCHAR";
        }

        public static bool IsDate(ParsedType t)
        {
            if (t == null)
            {
                return false;
            }

            return t.Base == "DATE" || t.Base == "TIMESTAMP";
        }
    }
}
