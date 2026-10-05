using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Model;

namespace MigrationStudio.Core.Jobs
{
    public static class MappingTemplate
    {
        public const string Format = "folderss-migration-mapping";

        public static string Export(MigrationJob job, string name)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            var templateName = name;
            if (string.IsNullOrEmpty(templateName))
            {
                templateName = (job.JobName ?? "").ToLowerInvariant() + "_mapping";
            }

            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "format", Format },
                { "version", MigrationJob.CurrentVersion },
                { "name", templateName },
                { "sourceSchema", job.Source != null ? job.Source.Schema : "" },
                { "targetSchema", job.Target != null ? job.Target.Schema : "" },
                {
                    "dictionary",
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "tables", NameDictionary.Tables.ToDictionary(p => p.Key, p => p.Value) },
                        { "columns", NameDictionary.Columns.ToDictionary(p => p.Key, p => p.Value) }
                    }
                },
                { "mappings", ExportMappings(job.Mappings) }
            };

            return JsonSerializer.Serialize(payload, JobJson.Options);
        }

        public static TemplateApplyResult Apply(MigrationJob job, string json)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                var at = ex.LineNumber.HasValue
                    ? " (line " + ex.LineNumber.Value.ToString(CultureInfo.InvariantCulture) + ")"
                    : "";
                throw new JobFileException("JSON 오류: " + ex.Message + at, ex);
            }

            using (doc)
            {
                var root = doc.RootElement;
                var format = root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String
                    ? fmt.GetString()
                    : null;
                if (!string.Equals(format, Format, StringComparison.Ordinal))
                {
                    throw new JobFileException("매핑 템플릿이 아닙니다(format: " + (format ?? "없음") + ")");
                }

                var version = 0;
                if (root.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number)
                {
                    version = ver.GetInt32();
                }

                List<Model.Mapping> incoming;
                if (version < 2)
                {
                    incoming = JobFile.UpgradeV1Mappings(root);
                }
                else
                {
                    incoming = new List<Model.Mapping>();
                    if (root.TryGetProperty("mappings", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in arr.EnumerateArray())
                        {
                            incoming.Add(JobFile.MergeMappingFromJson(m));
                        }
                    }
                }

                var result = new TemplateApplyResult();
                if (job.Mappings == null)
                {
                    job.Mappings = new List<Model.Mapping>();
                }

                foreach (var templateMap in incoming)
                {
                    var next = JsonSerializer.Deserialize<Model.Mapping>(
                        JsonSerializer.Serialize(templateMap, JobJson.Options),
                        JobJson.Options);
                    if (next == null)
                    {
                        continue;
                    }

                    next.Id = Model.Mapping.NewId();
                    var at = job.Mappings.FindIndex(x =>
                        string.Equals(x.SourceType, next.SourceType, StringComparison.Ordinal) &&
                        string.Equals(x.Source, next.Source, StringComparison.Ordinal) &&
                        string.Equals(x.Target, next.Target, StringComparison.Ordinal));
                    if (at >= 0)
                    {
                        next.Id = job.Mappings[at].Id;
                        job.Mappings[at] = next;
                        result.Replaced++;
                    }
                    else
                    {
                        job.Mappings.Add(next);
                        result.Added++;
                    }
                }

                return result;
            }
        }

        private static List<object> ExportMappings(List<Model.Mapping> mappings)
        {
            var list = new List<object>();
            if (mappings == null)
            {
                return list;
            }

            foreach (var m in mappings)
            {
                var json = JsonSerializer.Serialize(m, JobJson.Options);
                using var doc = JsonDocument.Parse(json);
                var copy = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("id") || prop.NameEquals("use"))
                    {
                        continue;
                    }

                    copy[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                }

                list.Add(copy);
            }

            return list;
        }
    }
}
