using System;

namespace MigrationStudio.Core.Adapters
{
    public sealed class NlsInfo
    {
        public string CharacterSet { get; set; }
        public string NCharCharacterSet { get; set; }
        public string LengthSemantics { get; set; }
    }

    public sealed class ConnectionTestResult
    {
        public bool Ok;
        public string Version;
        public string Banner;
        public string ServerVersion;
        public string DbName;
        public string ServiceName;
        public string CurrentSchema;
        public int? LatencyMs;
        public NlsInfo Nls;
        public DateTime TestedAt;
        public string Error;
        public string ErrorCode;
    }

    public sealed class ControlStoreCheck
    {
        public bool TablesExist;
        public bool CanCreate;
        public string Resolved;
        public string Reason;
    }

    public sealed class AdapterKind
    {
        public string Value { get; set; }
        public string Label { get; set; }
        public bool Planned { get; set; }
    }
}
