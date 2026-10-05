using System;
using System.Globalization;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace MigrationStudio.Core.Adapters.Oracle
{
    internal static class ValueText
    {
        internal static string Format(object value)
        {
            if (value == null || value is DBNull)
            {
                return null;
            }

            if (value is OracleString os)
            {
                return os.IsNull ? null : os.Value;
            }

            if (value is OracleDecimal od)
            {
                return od.IsNull ? null : od.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (value is OracleDate odt)
            {
                return odt.IsNull ? null : odt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (value is OracleTimeStamp ts)
            {
                return ts.IsNull ? null : ts.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (value is OracleTimeStampTZ tstz)
            {
                return tstz.IsNull ? null : tstz.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (value is OracleTimeStampLTZ tsltz)
            {
                return tsltz.IsNull ? null : tsltz.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (value is OracleBinary raw)
            {
                if (raw.IsNull)
                {
                    return null;
                }

                var bytes = raw.Value;
                var n = Math.Min(bytes.Length, 16);
                var sb = new StringBuilder(n * 2);
                for (var i = 0; i < n; i++)
                {
                    sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
                }

                return sb.ToString();
            }

            if (value is string s)
            {
                return s;
            }

            if (value is DateTime dt)
            {
                return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (value is IFormattable f)
            {
                return f.ToString(null, CultureInfo.InvariantCulture);
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
