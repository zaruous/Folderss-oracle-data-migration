using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;

namespace MigrationStudio.Core.Jobs
{
    public static class JobFile
    {
        public static MigrationJob Parse(string json)
        {
            if (json == null)
            {
                throw new JobFileException("JSON 오류: 입력이 비었습니다.");
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
                return ParseRoot(root);
            }
        }

        public static string Serialize(MigrationJob job, MigrationSettings settings)
        {
            var plain = ToPlain(job, settings);
            NormalizeForJsonWrite(plain);
            var tree = BuildYamlTree(plain);
            return JsonSerializer.Serialize(tree, JobJson.Options);
        }

        public static string ToYaml(MigrationJob job, MigrationSettings settings)
        {
            var plain = ToPlain(job, settings);
            var tree = BuildYamlTree(plain);
            return "# Folderss Migration Studio 작업 파일\n" + JobYaml.Emit(tree);
        }

        public static MigrationJob ToPlain(MigrationJob job, MigrationSettings settings)
        {
            if (job == null)
            {
                return JobSamples.BlankJob(null);
            }

            var copy = CloneJob(job);
            copy.Source = PlainConnection(copy.Source, settings);
            copy.Target = PlainConnection(copy.Target, settings);
            return copy;
        }

        internal static MigrationJob ParseRoot(JsonElement root)
        {
            var format = root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String
                ? fmt.GetString()
                : null;
            if (!string.Equals(format, MigrationJob.Format, StringComparison.Ordinal))
            {
                throw new JobFileException("작업 파일이 아닙니다(format: " + (format ?? "없음") + ")");
            }

            var version = 0;
            if (root.TryGetProperty("version", out var verEl) && verEl.ValueKind == JsonValueKind.Number)
            {
                version = verEl.GetInt32();
            }

            if (version > MigrationJob.CurrentVersion)
            {
                throw new JobFileException("더 새 버전(" + version.ToString(CultureInfo.InvariantCulture) + ")의 작업 파일입니다");
            }

            var baseStrategy = JobSamples.SampleJob().Strategy;
            var job = JobSamples.BlankJob(null);
            if (root.TryGetProperty("jobName", out var jn) && jn.ValueKind == JsonValueKind.String)
            {
                job.JobName = jn.GetString();
            }

            if (root.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
            {
                job.Description = desc.GetString();
            }

            if (version < 2)
            {
                job.Source = UpgradeV1Connection(root.TryGetProperty("source", out var srcV1) ? srcV1 : default);
                job.Target = UpgradeV1Connection(root.TryGetProperty("target", out var tgtV1) ? tgtV1 : default);
                job.Strategy = MergeStrategy(baseStrategy, root.TryGetProperty("strategy", out var stV1) ? stV1 : default);
                job.Mappings = UpgradeV1Mappings(root);
            }
            else
            {
                job.Source = MergeConnectionRef(
                    new ConnectionRef { ProfileId = null, Schema = "" },
                    root.TryGetProperty("source", out var src) ? src : default);
                job.Target = MergeConnectionRef(
                    new ConnectionRef { ProfileId = null, Schema = "" },
                    root.TryGetProperty("target", out var tgt) ? tgt : default);
                job.Strategy = MergeStrategy(baseStrategy, root.TryGetProperty("strategy", out var st) ? st : default);
                job.Mappings = ParseMappings(root);
            }

            job.Checkpoints = ParseCheckpoints(root.TryGetProperty("checkpoints", out var cps) ? cps : default);
            job.Version = MigrationJob.CurrentVersion;
            job.FormatName = MigrationJob.Format;
            return job;
        }

        internal static Model.Mapping MergeMappingFromJson(JsonElement m)
        {
            var sourceType = ReadString(m, "sourceType");
            return string.Equals(sourceType, SourceTypes.Sql, StringComparison.Ordinal)
                ? MergeSqlMapping(m)
                : MergeTableMapping(m);
        }

        internal static List<Model.Mapping> UpgradeV1Mappings(JsonElement data)
        {
            var mappings = new List<Model.Mapping>();
            if (data.TryGetProperty("tableMappings", out var tables) && tables.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in tables.EnumerateArray())
                {
                    var map = MergeTableMapping(m);
                    map.SourceType = SourceTypes.Table;
                    mappings.Add(map);
                }
            }

            if (data.TryGetProperty("sqlMappings", out var sqls) && sqls.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sqls.EnumerateArray())
                {
                    mappings.Add(UpgradeV1SqlMapping(s));
                }
            }

            if (data.TryGetProperty("mappings", out var unified) && unified.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in unified.EnumerateArray())
                {
                    var sourceType = ReadString(m, "sourceType");
                    mappings.Add(string.Equals(sourceType, SourceTypes.Sql, StringComparison.Ordinal)
                        ? MergeSqlMapping(m)
                        : MergeTableMapping(m));
                }
            }

            return mappings;
        }

        private static void NormalizeForJsonWrite(MigrationJob job)
        {
            if (job.Checkpoints == null)
            {
                return;
            }

            foreach (var cp in job.Checkpoints.Values)
            {
                if (cp != null && cp.Value != null)
                {
                    cp.Value = cp.Value.Trim();
                }
            }
        }

        private static MigrationJob CloneJob(MigrationJob job)
        {
            return JsonSerializer.Deserialize<MigrationJob>(
                JsonSerializer.Serialize(job, JobJson.Options),
                JobJson.Options) ?? JobSamples.BlankJob(null);
        }

        private static ConnectionRef PlainConnection(ConnectionRef c, MigrationSettings settings)
        {
            c = c ?? new ConnectionRef();
            var profile = settings != null && !string.IsNullOrEmpty(c.ProfileId)
                ? MigrationSettingsStore.Find(settings, c.ProfileId)
                : null;
            if (profile != null)
            {
                return new ConnectionRef
                {
                    ProfileId = c.ProfileId,
                    Schema = c.Schema,
                    Name = profile.Name,
                    Kind = profile.Kind,
                    Host = profile.Host,
                    Port = profile.Port.ToString(CultureInfo.InvariantCulture),
                    Service = profile.Service,
                    User = profile.User
                };
            }

            return new ConnectionRef
            {
                ProfileId = c.ProfileId,
                Schema = c.Schema,
                Name = c.Name,
                Kind = c.Kind,
                Host = c.Host,
                Port = NormalizePort(c.Port),
                Service = c.Service,
                User = c.User,
                Color = c.Color
            };
        }

        private static string NormalizePort(string port)
        {
            if (string.IsNullOrEmpty(port))
            {
                return port;
            }

            return port;
        }

        private static ConnectionRef MergeConnectionRef(ConnectionRef defaults, JsonElement el)
        {
            var c = defaults ?? new ConnectionRef();
            if (el.ValueKind != JsonValueKind.Object)
            {
                return c;
            }

            c.ProfileId = ReadStringOrNull(el, "profileId") ?? c.ProfileId;
            c.Schema = ReadString(el, "schema") ?? c.Schema;
            c.Name = ReadString(el, "name") ?? c.Name;
            c.Kind = ReadString(el, "kind") ?? c.Kind;
            c.Host = ReadString(el, "host") ?? c.Host;
            c.Port = ReadPort(el, "port") ?? c.Port;
            c.Service = ReadString(el, "service") ?? c.Service;
            c.User = ReadString(el, "user") ?? c.User;
            c.Color = ReadString(el, "color") ?? c.Color;
            return c;
        }

        private static MigrationStrategy MergeStrategy(MigrationStrategy defaults, JsonElement el)
        {
            var s = new MigrationStrategy
            {
                Mode = defaults.Mode,
                IncrementalBy = defaults.IncrementalBy,
                CommitSize = defaults.CommitSize,
                FetchSize = defaults.FetchSize,
                ErrorPolicy = defaults.ErrorPolicy,
                ErrorTable = defaults.ErrorTable,
                Workers = defaults.Workers
            };

            if (el.ValueKind != JsonValueKind.Object)
            {
                return s;
            }

            if (el.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String)
            {
                s.Mode = mode.GetString();
            }

            if (el.TryGetProperty("incrementalBy", out var inc) && inc.ValueKind == JsonValueKind.String)
            {
                s.IncrementalBy = inc.GetString();
            }

            if (el.TryGetProperty("commitSize", out var cs) && cs.ValueKind == JsonValueKind.Number)
            {
                s.CommitSize = cs.GetInt32();
            }

            if (el.TryGetProperty("fetchSize", out var fs) && fs.ValueKind == JsonValueKind.Number)
            {
                s.FetchSize = fs.GetInt32();
            }

            if (el.TryGetProperty("errorPolicy", out var ep) && ep.ValueKind == JsonValueKind.String)
            {
                s.ErrorPolicy = ep.GetString();
            }

            if (el.TryGetProperty("errorTable", out var et) && et.ValueKind == JsonValueKind.String)
            {
                s.ErrorTable = et.GetString();
            }

            if (el.TryGetProperty("workers", out var w) && w.ValueKind == JsonValueKind.Number)
            {
                s.Workers = w.GetInt32();
            }

            return s;
        }

        private static List<Model.Mapping> ParseMappings(JsonElement data)
        {
            var list = new List<Model.Mapping>();
            if (!data.TryGetProperty("mappings", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var m in arr.EnumerateArray())
            {
                var sourceType = ReadString(m, "sourceType");
                if (string.Equals(sourceType, SourceTypes.Sql, StringComparison.Ordinal))
                {
                    list.Add(MergeSqlMapping(m));
                }
                else
                {
                    list.Add(MergeTableMapping(m));
                }
            }

            return list;
        }

        private static Model.Mapping MergeTableMapping(JsonElement m)
        {
            var map = DefaultTableMapping();
            ApplyMappingCommon(map, m);
            map.SourceType = SourceTypes.Table;
            map.Sql = null;
            map.Binds = new List<BindParameter>();
            map.FetchSize = null;
            map.CommitSize = null;
            return map;
        }

        private static Model.Mapping MergeSqlMapping(JsonElement m)
        {
            var map = DefaultSqlMapping();
            ApplyMappingCommon(map, m);
            map.SourceType = SourceTypes.Sql;
            if (m.TryGetProperty("sql", out var sql) && sql.ValueKind == JsonValueKind.String)
            {
                map.Sql = sql.GetString();
            }

            map.Binds = ParseBinds(m);
            map.FetchSize = ReadNullableInt(m, "fetchSize");
            map.CommitSize = ReadNullableInt(m, "commitSize");
            return map;
        }

        private static void ApplyMappingCommon(Model.Mapping map, JsonElement m)
        {
            map.Id = ReadString(m, "id");
            if (string.IsNullOrEmpty(map.Id))
            {
                map.Id = Model.Mapping.NewId();
            }

            if (m.TryGetProperty("use", out var use) && (use.ValueKind == JsonValueKind.True || use.ValueKind == JsonValueKind.False))
            {
                map.Use = use.GetBoolean();
            }

            map.Source = ReadString(m, "source") ?? map.Source;
            map.Target = ReadString(m, "target") ?? map.Target;
            map.Mode = ReadString(m, "mode") ?? map.Mode;
            map.MergeKey = ReadStringArray(m, "mergeKey");
            map.CheckpointColumn = ReadStringOrNull(m, "checkpointColumn");
            map.Where = ReadString(m, "where") ?? map.Where;
            map.Columns = ParseColumns(m);
        }

        private static Model.Mapping DefaultTableMapping()
        {
            return new Model.Mapping
            {
                Id = Model.Mapping.NewId(),
                Use = true,
                SourceType = SourceTypes.Table,
                Source = "",
                Target = "",
                Mode = WriteModes.InsertOnly,
                MergeKey = new List<string>(),
                CheckpointColumn = null,
                Where = "",
                Columns = new List<ColumnMapping>()
            };
        }

        private static Model.Mapping DefaultSqlMapping()
        {
            return new Model.Mapping
            {
                Id = Model.Mapping.NewId(),
                Use = true,
                SourceType = SourceTypes.Sql,
                Source = "SQLMAP_1",
                Sql = "SELECT\n    \nFROM \nWHERE ",
                Binds = new List<BindParameter>(),
                Target = "",
                Mode = WriteModes.Merge,
                MergeKey = new List<string>(),
                CheckpointColumn = null,
                Where = "",
                FetchSize = null,
                CommitSize = null,
                Columns = new List<ColumnMapping>()
            };
        }

        private static List<BindParameter> ParseBinds(JsonElement m)
        {
            var list = new List<BindParameter>();
            if (!m.TryGetProperty("binds", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var b in arr.EnumerateArray())
            {
                list.Add(new BindParameter
                {
                    Name = ReadString(b, "name"),
                    Type = ReadString(b, "type") ?? "VARCHAR2",
                    Value = ReadString(b, "value") ?? "",
                    FromCheckpoint = b.TryGetProperty("fromCheckpoint", out var fc) && fc.ValueKind == JsonValueKind.True
                });
            }

            return list;
        }

        private static List<ColumnMapping> ParseColumns(JsonElement m)
        {
            var list = new List<ColumnMapping>();
            if (!m.TryGetProperty("columns", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var c in arr.EnumerateArray())
            {
                list.Add(new ColumnMapping
                {
                    Target = ReadString(c, "target"),
                    Source = ReadStringOrNullAllowNull(c, "source"),
                    Expr = ReadString(c, "expr") ?? "",
                    NullRule = ReadString(c, "nullRule") ?? NullRules.Allow,
                    DefaultValue = ReadString(c, "defaultValue") ?? ""
                });
            }

            return list;
        }

        private static Dictionary<string, CheckpointInfo> ParseCheckpoints(JsonElement el)
        {
            var dict = new Dictionary<string, CheckpointInfo>(StringComparer.Ordinal);
            if (el.ValueKind != JsonValueKind.Object)
            {
                return dict;
            }

            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var cp = prop.Value;
                dict[prop.Name] = new CheckpointInfo
                {
                    Column = ReadString(cp, "column"),
                    Value = ReadFlexibleString(cp, "value"),
                    Rows = ReadInt64(cp, "rows"),
                    Total = ReadInt64(cp, "total"),
                    At = ReadString(cp, "at"),
                    RunId = ReadString(cp, "runId"),
                    Status = ReadString(cp, "status")
                };
            }

            return dict;
        }

        private static ConnectionRef UpgradeV1Connection(JsonElement c)
        {
            if (c.ValueKind != JsonValueKind.Object)
            {
                return new ConnectionRef { ProfileId = null };
            }

            return new ConnectionRef
            {
                ProfileId = null,
                Schema = ReadString(c, "schema") ?? "",
                Name = ReadString(c, "name"),
                Kind = ReadString(c, "kind"),
                Host = ReadString(c, "host"),
                Port = ReadPort(c, "port"),
                Service = ReadString(c, "service"),
                User = ReadString(c, "user"),
                Color = ReadString(c, "color")
            };
        }

        private static Model.Mapping UpgradeV1SqlMapping(JsonElement s)
        {
            var columns = new List<ColumnMapping>();
            if (s.TryGetProperty("aliasMap", out var alias) && alias.ValueKind == JsonValueKind.Object)
            {
                foreach (var pair in alias.EnumerateObject())
                {
                    columns.Add(new ColumnMapping
                    {
                        Target = pair.Value.GetString(),
                        Source = pair.Name,
                        Expr = "",
                        NullRule = NullRules.Allow,
                        DefaultValue = ""
                    });
                }
            }

            var map = new Model.Mapping
            {
                Id = ReadString(s, "id") ?? Model.Mapping.NewId(),
                Use = s.TryGetProperty("use", out var use) && use.ValueKind == JsonValueKind.False ? false : true,
                SourceType = SourceTypes.Sql,
                Source = ReadString(s, "name") ?? "",
                Sql = ReadString(s, "sql"),
                Binds = ParseBinds(s),
                Target = ReadString(s, "targetTable") ?? "",
                Mode = ReadString(s, "writeStrategy") ?? WriteModes.Merge,
                MergeKey = ReadStringArray(s, "mergeKey"),
                CheckpointColumn = ReadStringOrNull(s, "checkpointColumn"),
                FetchSize = ReadNullableInt(s, "fetchSize"),
                CommitSize = ReadNullableInt(s, "commitSize"),
                Where = "",
                Columns = columns
            };
            return map;
        }

        private static string ReadString(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return v.GetString();
        }

        private static string ReadStringOrNull(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v))
            {
                return null;
            }

            if (v.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static string ReadStringOrNullAllowNull(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v))
            {
                return null;
            }

            if (v.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static string ReadPort(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v))
            {
                return null;
            }

            return ReadFlexibleString(v);
        }

        private static string ReadFlexibleString(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v))
            {
                return null;
            }

            return ReadFlexibleString(v);
        }

        private static string ReadFlexibleString(JsonElement v)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.String:
                    return v.GetString();
                case JsonValueKind.Number:
                    if (v.TryGetInt64(out var l))
                    {
                        return l.ToString(CultureInfo.InvariantCulture);
                    }

                    return v.GetRawText();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                default:
                    return null;
            }
        }

        private static int? ReadNullableInt(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (v.ValueKind == JsonValueKind.Number)
            {
                return v.GetInt32();
            }

            return null;
        }

        private static long ReadInt64(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
            {
                return 0;
            }

            return v.GetInt64();
        }

        private static List<string> ReadStringArray(JsonElement el, string name)
        {
            var list = new List<string>();
            if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    list.Add(item.GetString());
                }
            }

            return list;
        }

        private static Dictionary<string, object> BuildYamlTree(MigrationJob job)
        {
            var root = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "format", job.FormatName },
                { "version", job.Version },
                { "jobName", job.JobName },
                { "description", job.Description },
                { "source", ConnectionDict(job.Source) },
                { "target", ConnectionDict(job.Target) },
                { "strategy", StrategyDict(job.Strategy) },
                { "mappings", job.Mappings.Select(MappingDict).ToList() },
                { "checkpoints", CheckpointsDict(job.Checkpoints) }
            };
            return root;
        }

        private static Dictionary<string, object> ConnectionDict(ConnectionRef c)
        {
            c = c ?? new ConnectionRef();
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["profileId"] = c.ProfileId;
            d["schema"] = c.Schema ?? "";
            if (c.Name != null)
            {
                d["name"] = c.Name;
            }

            if (c.Kind != null)
            {
                d["kind"] = c.Kind;
            }

            if (c.Host != null)
            {
                d["host"] = c.Host;
            }

            if (c.Port != null)
            {
                d["port"] = c.Port;
            }

            if (c.Service != null)
            {
                d["service"] = c.Service;
            }

            if (c.User != null)
            {
                d["user"] = c.User;
            }

            return d;
        }

        private static Dictionary<string, object> StrategyDict(MigrationStrategy s)
        {
            s = s ?? new MigrationStrategy();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "mode", s.Mode },
                { "incrementalBy", s.IncrementalBy },
                { "commitSize", s.CommitSize },
                { "fetchSize", s.FetchSize },
                { "errorPolicy", s.ErrorPolicy },
                { "errorTable", s.ErrorTable },
                { "workers", s.Workers }
            };
        }

        private static Dictionary<string, object> MappingDict(Model.Mapping m)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "id", m.Id },
                { "use", m.Use },
                { "sourceType", m.SourceType },
                { "source", m.Source }
            };

            if (m.IsSql)
            {
                d["sql"] = m.Sql;
                d["binds"] = (m.Binds ?? new List<BindParameter>()).Select(BindDict).ToList();
            }

            d["target"] = m.Target;
            d["mode"] = m.Mode;
            d["mergeKey"] = m.MergeKey ?? new List<string>();
            d["checkpointColumn"] = m.CheckpointColumn;
            d["where"] = m.Where ?? "";

            if (m.IsSql)
            {
                if (m.FetchSize != null)
                {
                    d["fetchSize"] = m.FetchSize.Value;
                }

                if (m.CommitSize != null)
                {
                    d["commitSize"] = m.CommitSize.Value;
                }
            }

            d["columns"] = (m.Columns ?? new List<ColumnMapping>()).Select(ColumnDict).ToList();
            return d;
        }

        private static Dictionary<string, object> ColumnDict(ColumnMapping c)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "target", c.Target },
                { "source", c.Source },
                { "expr", c.Expr ?? "" },
                { "nullRule", c.NullRule },
                { "defaultValue", c.DefaultValue ?? "" }
            };
        }

        private static Dictionary<string, object> BindDict(BindParameter b)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", b.Name },
                { "type", b.Type },
                { "value", b.Value ?? "" },
                { "fromCheckpoint", b.FromCheckpoint }
            };
        }

        private static Dictionary<string, object> CheckpointsDict(Dictionary<string, CheckpointInfo> checkpoints)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            if (checkpoints == null)
            {
                return d;
            }

            foreach (var pair in checkpoints)
            {
                var cp = pair.Value;
                if (cp == null)
                {
                    continue;
                }

                var entry = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "column", cp.Column },
                    { "value", YamlCheckpointValue(cp.Value) },
                    { "rows", cp.Rows },
                    { "total", cp.Total },
                    { "at", cp.At },
                    { "runId", cp.RunId },
                    { "status", cp.Status }
                };
                d[pair.Key] = entry;
            }

            return d;
        }

        private static object YamlCheckpointValue(string value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                return n;
            }

            return value;
        }
    }
}
