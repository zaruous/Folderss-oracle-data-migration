using System;
using System.Collections.Generic;
using System.Globalization;
using MigrationStudio.Core.Model;

namespace MigrationStudio.Core.Adapters
{
    public sealed class QueryColumn
    {
        public string Name;
        public string Type;
        public bool Nullable;
    }

    public sealed class SqlParseResult
    {
        public bool Ok;
        public string ErrorCode;
        public string Message;
        public int? Line;
        public int? Position;
    }

    public sealed class QueryResult
    {
        public List<QueryColumn> Columns;
        public List<string[]> Rows;
        public long ElapsedMs;
        public bool HasMore;
    }

    public sealed class SqlBind
    {
        public string Name;
        public string Type;
        public string Value;

        public static SqlBind From(BindParameter bind)
        {
            if (bind == null)
            {
                throw new ArgumentNullException(nameof(bind));
            }

            return new SqlBind
            {
                Name = bind.Name,
                Type = bind.Type,
                Value = bind.Value
            };
        }
    }
}
