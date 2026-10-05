using System;
using System.Globalization;

namespace MigrationStudio.Core.Adapters.Oracle
{
    internal static class OracleColumnTypeFormatter
    {
        internal static string Format(
            string dataType,
            int dataLength,
            int charLength,
            string charUsed,
            int? dataPrecision,
            int? dataScale)
        {
            if (string.IsNullOrEmpty(dataType))
            {
                return "";
            }

            var upper = dataType.ToUpperInvariant();
            if (upper == "VARCHAR2" || upper == "CHAR")
            {
                var len = charLength > 0 ? charLength : dataLength;
                if (string.Equals(charUsed, "C", StringComparison.OrdinalIgnoreCase))
                {
                    return upper + "(" + len.ToString(CultureInfo.InvariantCulture) + " CHAR)";
                }

                return upper + "(" + len.ToString(CultureInfo.InvariantCulture) + ")";
            }

            if (upper == "NVARCHAR2" || upper == "NCHAR")
            {
                var len = charLength > 0 ? charLength : dataLength;
                return upper + "(" + len.ToString(CultureInfo.InvariantCulture) + ")";
            }

            if (upper == "NUMBER")
            {
                return FormatNumber(dataPrecision, dataScale);
            }

            if (upper == "FLOAT")
            {
                if (dataPrecision.HasValue)
                {
                    return "FLOAT(" + dataPrecision.Value.ToString(CultureInfo.InvariantCulture) + ")";
                }

                return "FLOAT";
            }

            if (upper == "RAW")
            {
                return "RAW(" + dataLength.ToString(CultureInfo.InvariantCulture) + ")";
            }

            return dataType;
        }

        private static string FormatNumber(int? precision, int? scale)
        {
            if (!precision.HasValue && !scale.HasValue)
            {
                return "NUMBER";
            }

            if (!precision.HasValue && scale.HasValue && scale.Value == 0)
            {
                return "NUMBER(38)";
            }

            if (precision.HasValue && (!scale.HasValue || scale.Value == 0))
            {
                return "NUMBER(" + precision.Value.ToString(CultureInfo.InvariantCulture) + ")";
            }

            if (precision.HasValue && scale.HasValue)
            {
                return "NUMBER(" + precision.Value.ToString(CultureInfo.InvariantCulture) + "," +
                       scale.Value.ToString(CultureInfo.InvariantCulture) + ")";
            }

            return "NUMBER";
        }
    }
}
