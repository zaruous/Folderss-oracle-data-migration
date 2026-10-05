using System.Text.Json.Serialization;

namespace MigrationStudio.Core.Engine
{
    /// <summary>파이프 한 줄 JSON 메시지. 알 수 없는 type은 무시(앞으로 호환).</summary>
    public sealed class AgentMessage
    {
        public string Type { get; set; }
        public int Protocol { get; set; }
        public string Client { get; set; }
        public string Agent { get; set; }
        public int Pid { get; set; }
        public string State { get; set; }
        public string RunId { get; set; }
        public string StartedAt { get; set; }
        public RunSpec Spec { get; set; }
        public RunSnapshot Snapshot { get; set; }
        public LogEntry Entry { get; set; }
        public CheckpointRecord Record { get; set; }
        public string ExitReason { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public int LogTail { get; set; }

        [JsonExtensionData]
        public System.Collections.Generic.IDictionary<string, object> Extension { get; set; }
    }

    public static class AgentMessageTypes
    {
        public const string Hello = "hello";
        public const string Welcome = "welcome";
        public const string Start = "start";
        public const string Attach = "attach";
        public const string Pause = "pause";
        public const string Resume = "resume";
        public const string Stop = "stop";
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string Shutdown = "shutdown";
        public const string Snapshot = "snapshot";
        public const string Log = "log";
        public const string Checkpoint = "checkpoint";
        public const string End = "end";
        public const string Error = "error";
    }
}
