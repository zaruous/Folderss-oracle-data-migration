using System;
using System.Collections.Generic;
using MigrationStudio.Core.Adapters.Oracle;

namespace MigrationStudio.Core.Adapters
{
    public static class DatabaseAdapters
    {
        private static readonly IReadOnlyList<AdapterKind> KindsList = new List<AdapterKind>
        {
            new AdapterKind { Value = "oracle", Label = "Oracle", Planned = false },
            new AdapterKind { Value = "postgresql", Label = "PostgreSQL (예정)", Planned = true },
            new AdapterKind { Value = "sqlserver", Label = "SQL Server (예정)", Planned = true },
            new AdapterKind { Value = "mysql", Label = "MySQL (예정)", Planned = true },
            new AdapterKind { Value = "mariadb", Label = "MariaDB (예정)", Planned = true }
        };

        public static IReadOnlyList<AdapterKind> Kinds
        {
            get { return KindsList; }
        }

        public static IDatabaseAdapter For(string kind)
        {
            var value = string.IsNullOrWhiteSpace(kind) ? "oracle" : kind.Trim();
            if (string.Equals(value, "oracle", StringComparison.OrdinalIgnoreCase))
            {
                return OracleDatabaseAdapter.Instance;
            }

            foreach (var k in KindsList)
            {
                if (string.Equals(k.Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    if (k.Planned)
                    {
                        var name = k.Label.Replace(" (예정)", "");
                        throw new NotSupportedException(name + " 어댑터는 아직 지원하지 않습니다");
                    }
                }
            }

            return OracleDatabaseAdapter.Instance;
        }
    }
}
