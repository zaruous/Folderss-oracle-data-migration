using System;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Sql;

namespace MigrationStudio.Core.Adapters.Oracle
{
    /// <summary>
    /// 원본 SELECT·WITH만 Oracle에 넘긴다. DDL은 DBMS_SQL.PARSE 단계에서 실행될 수 있어 연결 전에 막는다.
    /// </summary>
    public static class OracleSelectGuard
    {
        public const string SelectOnlyMessage = "SELECT 또는 WITH로 시작하는 문장만 쓸 수 있습니다";

        private static readonly Regex TrailingWsRx = new Regex(@"[;\s]+$", RegexOptions.CultureInvariant);
        private static readonly Regex SchemaRx = new Regex(@"^[A-Z0-9_$#]+$", RegexOptions.CultureInvariant);
        private static readonly Regex FirstWordRx = new Regex(@"^\s*(\S+)", RegexOptions.CultureInvariant);

        public static void EnsureSelectOnly(string sql)
        {
            var body = TrailingWsRx.Replace(SqlText.StripComments(sql ?? ""), "").Trim();
            if (body.IndexOf(';') >= 0)
            {
                throw new AdapterException(new InvalidOperationException(SelectOnlyMessage));
            }

            var m = FirstWordRx.Match(body);
            if (!m.Success)
            {
                throw new AdapterException(new InvalidOperationException(SelectOnlyMessage));
            }

            var word = m.Groups[1].Value.ToUpperInvariant();
            if (word != "SELECT" && word != "WITH")
            {
                throw new AdapterException(new InvalidOperationException(SelectOnlyMessage));
            }
        }

        /// <summary>ALTER SESSION에 이어 붙일 스키마 이름. 빈 값은 검사하지 않는다.</summary>
        public static string ValidateSchemaName(string schema)
        {
            if (string.IsNullOrWhiteSpace(schema))
            {
                return null;
            }

            var upper = schema.Trim().ToUpperInvariant();
            if (!SchemaRx.IsMatch(upper))
            {
                throw new AdapterException(new InvalidOperationException("스키마 이름에 쓸 수 없는 문자가 있습니다"));
            }

            return upper;
        }
    }
}
