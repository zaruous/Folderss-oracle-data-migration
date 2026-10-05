using System;
using System.Globalization;

namespace MigrationStudio.Core.Text
{
    /// <summary>화면·SQL 주석에 쓰는 숫자 형식(POC MS.fmt.n과 같음).</summary>
    public static class Format
    {
        public static string Number(decimal value)
        {
            return decimal.Round(value, 0, MidpointRounding.AwayFromZero)
                .ToString("N0", CultureInfo.GetCultureInfo("en-US"));
        }

        public static string Number(long value)
        {
            return Number((decimal)value);
        }

        /// <summary>짧은 행 수(1.24M · 48K · 850).</summary>
        public static string Short(long value)
        {
            var abs = Math.Abs(value);
            if (abs >= 1_000_000)
            {
                var m = value / 1_000_000d;
                return m.ToString(abs >= 10_000_000 ? "0" : "0.##", CultureInfo.InvariantCulture) + "M";
            }

            if (abs >= 1_000)
            {
                var k = value / 1_000d;
                return k.ToString(abs >= 10_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "K";
            }

            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
