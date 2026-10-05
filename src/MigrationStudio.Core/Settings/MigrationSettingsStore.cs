using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MigrationStudio.Core.Model;

namespace MigrationStudio.Core.Settings
{
    public static class MigrationSettingsStore
    {
        public const string SettingKey = "migration-settings";

        private static readonly Regex PortRx = new Regex(@"^\d+$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

        public static MigrationSettings Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return CreateDefaultSettings();
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return CreateDefaultSettings();
            }

            using (doc)
            {
                var root = doc.RootElement;
                var baseSettings = CreateDefaultSettings();

                int? version = null;
                if (root.TryGetProperty("version", out var verEl) && verEl.ValueKind == JsonValueKind.Number)
                {
                    version = verEl.GetInt32();
                }

                if (version == null || version.Value != MigrationSettings.CurrentVersion)
                {
                    var migrated = CreateDefaultSettings();
                    if (root.TryGetProperty("connections", out var connEl) && connEl.ValueKind == JsonValueKind.Array)
                    {
                        migrated.Connections = ParseConnections(connEl);
                    }

                    return migrated;
                }

                var result = new MigrationSettings
                {
                    Version = MigrationSettings.CurrentVersion,
                    Connections = root.TryGetProperty("connections", out var connections) && connections.ValueKind == JsonValueKind.Array
                        ? ParseConnections(connections)
                        : new List<ConnectionProfile>(),
                    Defaults = MergeDefaults(baseSettings.Defaults, root.TryGetProperty("defaults", out var defaults) ? defaults : default),
                    Agent = MergeAgent(baseSettings.Agent, root.TryGetProperty("agent", out var agent) ? agent : default)
                };

                return result;
            }
        }

        public static string Serialize(MigrationSettings settings)
        {
            var copy = settings ?? CreateDefaultSettings();
            NormalizeForSave(copy);
            return JsonSerializer.Serialize(copy, JsonOptions);
        }

        public static ConnectionProfile Find(MigrationSettings s, string id)
        {
            if (s == null || s.Connections == null || string.IsNullOrEmpty(id))
            {
                return null;
            }

            foreach (var c in s.Connections)
            {
                if (string.Equals(c.Id, id, StringComparison.Ordinal))
                {
                    return c;
                }
            }

            return null;
        }

        public static ConnectionProfile FindByName(MigrationSettings s, string name)
        {
            if (s == null || s.Connections == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            foreach (var c in s.Connections)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }

            return null;
        }

        public static ConnectionProfile NewProfile(MigrationSettings s)
        {
            var n = 1;
            while (FindByName(s, "NEW_CONNECTION_" + n.ToString(CultureInfo.InvariantCulture)) != null)
            {
                n++;
            }

            var name = "NEW_CONNECTION_" + n.ToString(CultureInfo.InvariantCulture);
            return new ConnectionProfile
            {
                Id = NewConnectionId(),
                Name = name,
                Kind = "oracle",
                Color = "",
                Host = "",
                Port = 1521,
                Service = "",
                User = "",
                LegacyPassword = "",
                ProtectedPassword = "",
                SavePassword = true,
                DefaultSchema = "",
                WriteBlocked = false
            };
        }

        public static List<string> ValidateProfile(ConnectionProfile p, IEnumerable<ConnectionProfile> all)
        {
            var errors = new List<string>();
            if (p == null)
            {
                return errors;
            }

            if (string.IsNullOrWhiteSpace(p.Name))
            {
                errors.Add("접속 이름을 입력하세요");
            }
            else
            {
                foreach (var x in all ?? Enumerable.Empty<ConnectionProfile>())
                {
                    if (!ReferenceEquals(x, p) && !string.Equals(x.Id, p.Id, StringComparison.Ordinal) &&
                        string.Equals(x.Name, p.Name, StringComparison.Ordinal))
                    {
                        errors.Add("같은 이름의 접속이 있습니다: " + p.Name);
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(p.Host))
            {
                errors.Add("호스트를 입력하세요");
            }

            var portText = p.Port.ToString(CultureInfo.InvariantCulture);
            if (!PortRx.IsMatch(portText) || p.Port < 1 || p.Port > 65535)
            {
                errors.Add("포트는 1–65535 숫자");
            }

            if (string.IsNullOrWhiteSpace(p.Service))
            {
                errors.Add("서비스명을 입력하세요");
            }

            if (string.IsNullOrWhiteSpace(p.User))
            {
                errors.Add("사용자를 입력하세요");
            }

            return errors;
        }

        public static void ApplyDefaults(MigrationStrategy strategy, MigrationDefaults defaults)
        {
            if (strategy == null || defaults == null)
            {
                return;
            }

            strategy.CommitSize = defaults.CommitSize;
            strategy.FetchSize = defaults.FetchSize;
            strategy.Workers = defaults.Workers;
            strategy.ErrorPolicy = defaults.ErrorPolicy;
            strategy.ErrorTable = defaults.ErrorTable;
        }

        public static MigrationSettings CreateDefaultSettings()
        {
            return new MigrationSettings
            {
                Version = MigrationSettings.CurrentVersion,
                Connections = new List<ConnectionProfile>(),
                Defaults = new MigrationDefaults(),
                Agent = new AgentSettings()
            };
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }

        private static List<ConnectionProfile> ParseConnections(JsonElement connections)
        {
            var list = new List<ConnectionProfile>();
            foreach (var el in connections.EnumerateArray())
            {
                list.Add(ParseConnection(el));
            }

            return list;
        }

        private static ConnectionProfile ParseConnection(JsonElement el)
        {
            var p = new ConnectionProfile();
            if (el.TryGetProperty("id", out var id))
            {
                p.Id = id.GetString();
            }

            if (el.TryGetProperty("name", out var name))
            {
                p.Name = name.GetString();
            }

            if (el.TryGetProperty("kind", out var kind))
            {
                p.Kind = kind.GetString() ?? "oracle";
            }

            if (el.TryGetProperty("color", out var color))
            {
                p.Color = color.GetString() ?? "";
            }

            if (el.TryGetProperty("host", out var host))
            {
                p.Host = host.GetString();
            }

            p.Port = ReadPort(el);

            if (el.TryGetProperty("service", out var service))
            {
                p.Service = service.GetString();
            }

            if (el.TryGetProperty("user", out var user))
            {
                p.User = user.GetString();
            }

            if (el.TryGetProperty("protectedPassword", out var prot))
            {
                p.ProtectedPassword = prot.GetString();
            }

            if (el.TryGetProperty("password", out var password))
            {
                p.LegacyPassword = password.GetString();
            }

            if (el.TryGetProperty("savePassword", out var savePw))
            {
                p.SavePassword = savePw.ValueKind == JsonValueKind.True;
            }

            if (el.TryGetProperty("defaultSchema", out var schema))
            {
                p.DefaultSchema = schema.GetString();
            }

            if (el.TryGetProperty("writeBlocked", out var blocked))
            {
                p.WriteBlocked = blocked.ValueKind == JsonValueKind.True;
            }

            return p;
        }

        private static int ReadPort(JsonElement el)
        {
            if (!el.TryGetProperty("port", out var port))
            {
                return 1521;
            }

            if (port.ValueKind == JsonValueKind.Number)
            {
                return port.GetInt32();
            }

            if (port.ValueKind == JsonValueKind.String)
            {
                int n;
                if (int.TryParse(port.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                {
                    return n;
                }
            }

            return 1521;
        }

        private static MigrationDefaults MergeDefaults(MigrationDefaults baseDefaults, JsonElement saved)
        {
            var d = new MigrationDefaults
            {
                CommitSize = baseDefaults.CommitSize,
                FetchSize = baseDefaults.FetchSize,
                Workers = baseDefaults.Workers,
                ErrorPolicy = baseDefaults.ErrorPolicy,
                ErrorTable = baseDefaults.ErrorTable,
                CheckpointStore = baseDefaults.CheckpointStore,
                ControlPrefix = baseDefaults.ControlPrefix
            };

            if (saved.ValueKind != JsonValueKind.Object)
            {
                return d;
            }

            if (saved.TryGetProperty("commitSize", out var commit) && commit.ValueKind == JsonValueKind.Number)
            {
                d.CommitSize = commit.GetInt32();
            }

            if (saved.TryGetProperty("fetchSize", out var fetch) && fetch.ValueKind == JsonValueKind.Number)
            {
                d.FetchSize = fetch.GetInt32();
            }

            if (saved.TryGetProperty("workers", out var workers) && workers.ValueKind == JsonValueKind.Number)
            {
                d.Workers = workers.GetInt32();
            }

            if (saved.TryGetProperty("errorPolicy", out var policy) && policy.ValueKind == JsonValueKind.String)
            {
                d.ErrorPolicy = policy.GetString();
            }

            if (saved.TryGetProperty("errorTable", out var errTable) && errTable.ValueKind == JsonValueKind.String)
            {
                d.ErrorTable = errTable.GetString();
            }

            if (saved.TryGetProperty("checkpointStore", out var cp) && cp.ValueKind == JsonValueKind.String)
            {
                d.CheckpointStore = cp.GetString();
            }

            if (saved.TryGetProperty("controlPrefix", out var prefix) && prefix.ValueKind == JsonValueKind.String)
            {
                d.ControlPrefix = prefix.GetString();
            }

            return d;
        }

        private static AgentSettings MergeAgent(AgentSettings baseAgent, JsonElement saved)
        {
            var a = new AgentSettings
            {
                OnHostExit = baseAgent.OnHostExit,
                MaxConcurrent = baseAgent.MaxConcurrent,
                LogDays = baseAgent.LogDays
            };

            if (saved.ValueKind != JsonValueKind.Object)
            {
                return a;
            }

            if (saved.TryGetProperty("onHostExit", out var exit) && exit.ValueKind == JsonValueKind.String)
            {
                a.OnHostExit = exit.GetString();
            }

            if (saved.TryGetProperty("maxConcurrent", out var max) && max.ValueKind == JsonValueKind.Number)
            {
                a.MaxConcurrent = max.GetInt32();
            }

            if (saved.TryGetProperty("logDays", out var days) && days.ValueKind == JsonValueKind.Number)
            {
                a.LogDays = days.GetInt32();
            }

            return a;
        }

        private static void NormalizeForSave(MigrationSettings settings)
        {
            if (settings.Connections == null)
            {
                settings.Connections = new List<ConnectionProfile>();
            }

            foreach (var p in settings.Connections)
            {
                if (string.IsNullOrEmpty(p.ProtectedPassword) && !string.IsNullOrEmpty(p.LegacyPassword))
                {
                    p.ProtectedPassword = p.LegacyPassword;
                }

                p.LegacyPassword = null;
            }
        }

        private static string NewConnectionId()
        {
            return "cn-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        }
    }
}
