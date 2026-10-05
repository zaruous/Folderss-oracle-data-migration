using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Model;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Sql
{
    public static class SqlSourceBinds
    {
        private static readonly Regex NumberSuffixRx = new Regex(@"_(ID|NO|SEQ|CNT)$", RegexOptions.CultureInvariant);
        private static readonly Regex DateSuffixRx = new Regex(@"_(DT|DATE|AT)$", RegexOptions.CultureInvariant);
        private static readonly Regex CheckpointPrefixRx = new Regex(@"^LAST_", RegexOptions.CultureInvariant);

        /// <summary>SQL에 있는 바인드 이름을 mapping.Binds에 반영한다. 사라진 이름은 지우지 않는다.</summary>
        public static IList<string> Sync(MappingModel mapping)
        {
            if (mapping == null)
            {
                return new List<string>();
            }

            var parsed = SelectParser.Parse(mapping.Sql);
            var names = parsed.Binds ?? new List<string>();
            if (mapping.Binds == null)
            {
                mapping.Binds = new List<BindParameter>();
            }

            foreach (var name in names)
            {
                if (mapping.Binds.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var upper = name.ToUpperInvariant();
                var type = NumberSuffixRx.IsMatch(upper)
                    ? "NUMBER"
                    : DateSuffixRx.IsMatch(upper)
                        ? "DATE"
                        : "VARCHAR2";
                mapping.Binds.Add(new BindParameter
                {
                    Name = name,
                    Type = type,
                    Value = "",
                    FromCheckpoint = CheckpointPrefixRx.IsMatch(upper)
                });
            }

            return names;
        }
    }
}
