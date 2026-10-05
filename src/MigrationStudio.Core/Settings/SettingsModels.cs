using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MigrationStudio.Core.Settings
{
    public sealed class ConnectionProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; } = "oracle";
        public string Color { get; set; } = "";
        public string Host { get; set; }
        public int Port { get; set; } = 1521;
        public string Service { get; set; }
        public string User { get; set; }

        [JsonPropertyName("protectedPassword")]
        public string ProtectedPassword { get; set; }

        /// <summary>POC·구버전 JSON의 평문 password(읽기만; 저장 시 protectedPassword).</summary>
        [JsonPropertyName("password")]
        public string LegacyPassword { get; set; }

        public bool SavePassword { get; set; } = true;
        public string DefaultSchema { get; set; }
        public bool WriteBlocked { get; set; }
    }

    public sealed class MigrationDefaults
    {
        public int CommitSize { get; set; } = 10000;
        public int FetchSize { get; set; } = 5000;
        public int Workers { get; set; } = 4;
        public string ErrorPolicy { get; set; } = "CONTINUE";
        public string ErrorTable { get; set; } = "ERR$_";
        public string CheckpointStore { get; set; } = "AUTO";
        public string ControlPrefix { get; set; } = "MIG_";
    }

    public sealed class AgentSettings
    {
        public string OnHostExit { get; set; } = "CONTINUE";
        public int MaxConcurrent { get; set; } = 1;
        public int LogDays { get; set; } = 30;
    }

    public sealed class MigrationSettings
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        public List<ConnectionProfile> Connections { get; set; } = new List<ConnectionProfile>();
        public MigrationDefaults Defaults { get; set; } = new MigrationDefaults();
        public AgentSettings Agent { get; set; } = new AgentSettings();
    }
}
