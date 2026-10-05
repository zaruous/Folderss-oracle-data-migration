using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MigrationStudio.Core.Types
{
    /// <summary>
    /// Oracle 형식 표기 해석: "NUMBER(15,2)" → Base=NUMBER, Precision=15, Scale=2 / "VARCHAR2(30)" → Length=30 / "TIMESTAMP" → Fraction=6.
    /// "VARCHAR2(30 CHAR)"·"VARCHAR2(30 BYTE)"의 단위도 읽는다.
    /// </summary>
    public sealed class OracleType
    {
        // "TIMESTAMP(6) WITH TIME ZONE"처럼 뒤에 붙는 시간대 꼬리. 먼저 떼어 낸 뒤 나머지를 해석한다.
        private static readonly Regex ZoneTail = new Regex(@"\s+WITH\s+(LOCAL\s+)?TIME\s+ZONE$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Pattern = new Regex(@"^([A-Z0-9_ ]+?)\s*(?:\((\d+)(?:\s*,\s*(\d+))?(?:\s+(CHAR|BYTE))?\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public string Base { get; private set; }
        public int? Length { get; private set; }
        public int? Precision { get; private set; }
        public int? Scale { get; private set; }
        public int? Fraction { get; private set; }
        /// <summary>CHAR 또는 BYTE(문자 열 길이 단위). 표기에 없으면 null.</summary>
        public string LengthUnit { get; private set; }
        /// <summary>TIMESTAMP의 시간대: "TZ"(WITH TIME ZONE) · "LTZ"(WITH LOCAL TIME ZONE). 없으면 null.</summary>
        public string TimeZone { get; private set; }

        public bool IsChar
        {
            get { return Base == "VARCHAR2" || Base == "CHAR" || Base == "NVARCHAR2" || Base == "VARCHAR" || Base == "NCHAR"; }
        }

        public bool IsDate
        {
            get { return Base == "DATE" || Base == "TIMESTAMP"; }
        }

        public bool IsNumber
        {
            get { return Base == "NUMBER" || Base == "INTEGER" || Base == "FLOAT"; }
        }

        /// <summary>해석한다. null·빈 문자열이면 null.</summary>
        public static OracleType Parse(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
                return null;
            var text = type.Trim();
            string zone = null;
            var zm = ZoneTail.Match(text);
            if (zm.Success && text.StartsWith("TIMESTAMP", StringComparison.OrdinalIgnoreCase))
            {
                zone = zm.Groups[1].Success ? "LTZ" : "TZ";
                text = text.Substring(0, zm.Index);
            }
            var m = Pattern.Match(text);
            if (!m.Success)
                return new OracleType { Base = text.ToUpperInvariant(), TimeZone = zone };
            var result = new OracleType { Base = m.Groups[1].Value.Trim().ToUpperInvariant(), TimeZone = zone };
            int? a = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : (int?)null;
            int? b = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : (int?)null;
            if (m.Groups[4].Success)
                result.LengthUnit = m.Groups[4].Value.ToUpperInvariant();
            if (result.Base == "NUMBER")
            {
                result.Precision = a;
                result.Scale = b ?? (a == null ? (int?)null : 0);
            }
            else if (result.Base.StartsWith("TIMESTAMP", StringComparison.Ordinal))
            {
                result.Base = "TIMESTAMP";
                result.Fraction = a ?? 6;
            }
            else
            {
                result.Length = a;
            }
            return result;
        }

        /// <summary>문자로 바꿨을 때 필요한 최대 길이(형식 추정용). 모르면 4000.</summary>
        public static int DisplayLength(string type)
        {
            var t = Parse(type);
            if (t == null)
                return 4000;
            if (t.Length != null)
                return t.Length.Value;
            if (t.IsNumber)
                return (t.Precision ?? 38) + 2;
            if (t.IsDate)
                return 26;
            return 4000;
        }
    }
}
