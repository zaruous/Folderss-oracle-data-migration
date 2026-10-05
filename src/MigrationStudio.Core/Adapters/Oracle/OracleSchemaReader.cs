using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;

namespace MigrationStudio.Core.Adapters.Oracle
{
    internal static class OracleSchemaReader
    {
        internal static List<QueryColumn> FromSchemaTable(DataTable table)
        {
            var list = new List<QueryColumn>();
            if (table == null)
            {
                return list;
            }

            foreach (DataRow row in table.Rows)
            {
                var name = Convert.ToString(row["ColumnName"], CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var size = ReadInt(row, "ColumnSize");
                var precision = ReadNullableInt(row, "NumericPrecision");
                var scale = ReadNullableInt(row, "NumericScale");
                var allowNull = row["AllowDBNull"] is bool b && b;
                var dataType = ReadDataTypeName(row);

                list.Add(new QueryColumn
                {
                    Name = name,
                    Type = FormatProviderType(dataType, size, precision, scale),
                    Nullable = allowNull
                });
            }

            return list;
        }

        private static string ReadDataTypeName(DataRow row)
        {
            if (row.Table.Columns.Contains("DataTypeName") && row["DataTypeName"] != DBNull.Value)
            {
                return Convert.ToString(row["DataTypeName"], CultureInfo.InvariantCulture);
            }

            if (row.Table.Columns.Contains("DataType") && row["DataType"] is Type clr)
            {
                if (clr == typeof(string))
                {
                    return "VARCHAR2";
                }

                if (clr == typeof(decimal) || clr == typeof(int) || clr == typeof(long))
                {
                    return "NUMBER";
                }

                if (clr == typeof(DateTime))
                {
                    return "DATE";
                }

                return clr.Name.ToUpperInvariant();
            }

            if (row.Table.Columns.Contains("ProviderType") && row["ProviderType"] != DBNull.Value)
            {
                return Convert.ToString(row["ProviderType"], CultureInfo.InvariantCulture);
            }

            return "";
        }

        private static string FormatProviderType(string dataType, int size, int? precision, int? scale)
        {
            if (string.IsNullOrEmpty(dataType))
            {
                return "";
            }

            var upper = dataType.ToUpperInvariant();
            if (upper == "STRING" || upper == "VARCHAR2" || upper == "NVARCHAR2" || upper == "CHAR" || upper == "NCHAR")
            {
                var baseName = upper == "STRING" ? "VARCHAR2" : upper;
                return size > 0 ? baseName + "(" + size.ToString(CultureInfo.InvariantCulture) + ")" : baseName;
            }

            if (upper == "DECIMAL" || upper == "NUMBER" || upper == "INT16" || upper == "INT32" || upper == "INT64")
            {
                if (precision.HasValue && scale.HasValue && scale.Value > 0)
                {
                    return "NUMBER(" + precision.Value.ToString(CultureInfo.InvariantCulture) + "," +
                           scale.Value.ToString(CultureInfo.InvariantCulture) + ")";
                }

                if (precision.HasValue)
                {
                    return "NUMBER(" + precision.Value.ToString(CultureInfo.InvariantCulture) + ")";
                }

                return "NUMBER";
            }

            if (upper.StartsWith("TIMESTAMP", StringComparison.Ordinal))
            {
                return upper.Replace(" ", "");
            }

            if (upper == "DATETIME" || upper == "DATE")
            {
                return "DATE";
            }

            if (upper == "RAW")
            {
                return size > 0 ? "RAW(" + size.ToString(CultureInfo.InvariantCulture) + ")" : "RAW";
            }

            return upper;
        }

        private static int ReadInt(DataRow row, string column)
        {
            if (!row.Table.Columns.Contains(column) || row[column] is DBNull)
            {
                return 0;
            }

            return Convert.ToInt32(row[column], CultureInfo.InvariantCulture);
        }

        private static int? ReadNullableInt(DataRow row, string column)
        {
            if (!row.Table.Columns.Contains(column) || row[column] is DBNull)
            {
                return null;
            }

            return Convert.ToInt32(row[column], CultureInfo.InvariantCulture);
        }
    }
}
