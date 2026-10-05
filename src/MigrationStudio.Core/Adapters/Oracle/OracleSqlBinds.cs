using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Sql;
using Oracle.ManagedDataAccess.Client;

namespace MigrationStudio.Core.Adapters.Oracle
{
    internal static class OracleSqlBinds
    {
        private static readonly Regex DateRx = new Regex(
            @"^\d{4}-\d{2}-\d{2}(\s+\d{2}:\d{2}:\d{2})?$",
            RegexOptions.CultureInvariant);

        internal static void AddParameters(OracleCommand cmd, string sql, IList<SqlBind> binds)
        {
            var names = CollectBindNames(sql, binds);
            foreach (var name in names)
            {
                var bind = binds != null
                    ? binds.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                    : null;
                cmd.Parameters.Add(CreateParameter(name, bind));
            }
        }

        internal static IEnumerable<string> CollectBindNames(string sql, IList<SqlBind> binds)
        {
            var fromSql = SelectParser.Parse(sql).Binds ?? new List<string>();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in fromSql)
            {
                set.Add(n);
            }

            if (binds != null)
            {
                foreach (var b in binds)
                {
                    if (!string.IsNullOrEmpty(b.Name))
                    {
                        set.Add(b.Name);
                    }
                }
            }

            return set;
        }

        internal static OracleParameter CreateParameter(string name, SqlBind bind)
        {
            var p = new OracleParameter(name, OracleDbType.Varchar2);
            if (bind == null || string.IsNullOrWhiteSpace(bind.Value))
            {
                p.Value = DBNull.Value;
                return p;
            }

            var type = (bind.Type ?? "VARCHAR2").Trim().ToUpperInvariant();
            switch (type)
            {
                case "NUMBER":
                {
                    p.OracleDbType = OracleDbType.Decimal;
                    if (!decimal.TryParse(bind.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var num))
                    {
                        throw new AdapterException(new InvalidOperationException("바인드 :" + name + " 값이 숫자가 아닙니다"));
                    }

                    p.Value = num;
                    break;
                }
                case "DATE":
                {
                    p.OracleDbType = OracleDbType.TimeStamp;
                    var text = bind.Value.Trim();
                    if (!DateRx.IsMatch(text))
                    {
                        throw new AdapterException(new InvalidOperationException("바인드 :" + name + " 날짜 형식이 올바르지 않습니다"));
                    }

                    if (text.Length <= 10)
                    {
                        text += " 00:00:00";
                    }

                    p.Value = DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    break;
                }
                default:
                    p.Value = bind.Value;
                    break;
            }

            return p;
        }

        /// <summary>SchemaOnly DESCRIBE — 실행하지 않으므로 값 없이 NULL VARCHAR2로만 달아 준다.</summary>
        internal static void AddNullBindParameters(OracleCommand cmd, string sql)
        {
            foreach (var name in CollectBindNames(sql, null))
            {
                var p = new OracleParameter(name, OracleDbType.Varchar2) { Value = DBNull.Value };
                cmd.Parameters.Add(p);
            }
        }
    }
}
