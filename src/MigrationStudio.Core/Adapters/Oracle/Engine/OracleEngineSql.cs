using System;
using System.Text.RegularExpressions;

namespace MigrationStudio.Core.Adapters.Oracle.Engine
{
    internal static class OracleEngineSql
    {
        private static readonly Regex IdentifierPattern = new Regex("^[A-Z][A-Z0-9_$#]{0,127}$", RegexOptions.CultureInvariant);
        private static readonly Regex PrefixPattern = new Regex("^[A-Z0-9_$#]{1,10}$", RegexOptions.CultureInvariant);

        internal static string Identifier(string value)
        {
            var text = (value ?? "").Trim().ToUpperInvariant();
            if (!IdentifierPattern.IsMatch(text))
            {
                throw new ArgumentException("Oracle 식별자 형식이 올바르지 않습니다: " + value);
            }
            return text;
        }

        internal static string Prefix(string value)
        {
            var text = (value ?? "").Trim().ToUpperInvariant();
            if (!PrefixPattern.IsMatch(text))
            {
                throw new ArgumentException("제어 테이블 접두어는 영문 대문자·숫자·_·$·# 1~10자여야 합니다.");
            }
            return text;
        }

        internal static string Literal(string value)
        {
            return "'" + (value ?? "").Replace("'", "''", StringComparison.Ordinal) + "'";
        }
    }
}
