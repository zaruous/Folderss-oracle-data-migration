using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;

namespace MigrationStudio.Tests.Golden
{
    internal static class GoldenTestHelpers
    {
        public static MigrationSettings SettingsFromGolden()
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Golden", "poc-golden.json");
            var json = System.IO.File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var settingsJson = doc.RootElement.GetProperty("settingsDefaults").GetRawText();
            return MigrationSettingsStore.Deserialize(settingsJson);
        }

        public static MigrationJob SampleJob()
        {
            return JobSamples.SampleJob();
        }

        public static TableMetadata FindSourceTable(SchemaMetadata schema, string name)
        {
            return schema.FindTable(name);
        }

        public static TableMetadata FindTargetTable(SchemaMetadata schema, string name)
        {
            return schema.FindTable(name);
        }

        public static Mapping CloneMapping(Mapping mapping)
        {
            return JsonSerializer.Deserialize<Mapping>(
                JsonSerializer.Serialize(mapping, JobJson.Options),
                JobJson.Options);
        }

        public static IEnumerable<Mapping> TableMappings(MigrationJob job)
        {
            return job.Mappings.Where(m => !m.IsSql);
        }

        public static Mapping SqlSampleMapping(MigrationJob job)
        {
            return job.Mappings.First(m => m.IsSql);
        }

        public static string ParseSelectSql(GoldenDocument golden, string key)
        {
            var entry = golden.ParseSelect.First(p => p.Key == key);
            return entry.Sql;
        }

        public static Mapping BuildSqlValidateMapping(GoldenDocument golden, MigrationJob job, string key)
        {
            var sm = CloneMapping(SqlSampleMapping(job));
            var sampleSql = ParseSelectSql(golden, "sample");
            var sampleOrdered = sampleSql + "\nORDER BY C.CUSTOMER_ID";
            var missingParen = ParseSelectSql(golden, "missingParen");

            switch (key)
            {
                case "sample":
                    return sm;
                case "ordered":
                    sm.Sql = sampleOrdered;
                    return sm;
                case "noCheckpointBind":
                    sm.Binds = new List<BindParameter>();
                    sm.Sql = sampleSql.Replace("WHERE C.CUSTOMER_ID > :LAST_ID", "");
                    return sm;
                case "missingBindValue":
                    sm.Binds = new List<BindParameter>
                    {
                        new BindParameter
                        {
                            Name = "LAST_ID",
                            Type = "NUMBER",
                            Value = "",
                            FromCheckpoint = false
                        }
                    };
                    return sm;
                case "broken":
                    sm.Sql = missingParen;
                    return sm;
                case "noTarget":
                    sm.Target = "";
                    return sm;
                default:
                    throw new ArgumentOutOfRangeException(nameof(key), key, "unknown sqlValidate key");
            }
        }

        public static Func<string, string> CustomerOrderColumnTypes(SchemaMetadata source)
        {
            var customer = source.FindTable("SRC_CUSTOMER");
            var order = source.FindTable("SRC_ORDER");
            var columns = customer.Columns.Concat(order.Columns).ToList();
            return n =>
            {
                var baseName = n;
                var dot = n.LastIndexOf('.');
                if (dot >= 0)
                {
                    baseName = n.Substring(dot + 1);
                }

                var col = columns.FirstOrDefault(c => string.Equals(c.Name, baseName, StringComparison.Ordinal));
                return col?.Type;
            };
        }
    }
}
