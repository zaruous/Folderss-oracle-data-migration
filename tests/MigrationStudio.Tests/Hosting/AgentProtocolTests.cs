using System;
using System.Text;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Hosting;
using Xunit;

namespace MigrationStudio.Tests.Hosting
{
    public sealed class AgentProtocolTests
    {
        [Fact]
        public void Message_roundtrip_preserves_fields()
        {
            var original = new AgentMessage
            {
                Type = AgentMessageTypes.Start,
                Spec = new RunSpec { RunId = "R-1", RunMode = "EXECUTE" }
            };
            var json = Encoding.UTF8.GetString(AgentMessageCodec.Serialize(original)).Trim();
            var parsed = AgentMessageCodec.Deserialize(json);
            Assert.Equal(original.Type, parsed.Type);
            Assert.Equal(original.Spec.RunId, parsed.Spec.RunId);
        }

        [Fact]
        public void Unknown_type_deserializes_without_throw()
        {
            var parsed = AgentMessageCodec.Deserialize("{\"type\":\"future-message\",\"value\":1}");
            Assert.Equal("future-message", parsed.Type);
        }

        [Fact]
        public void Oversized_message_rejected()
        {
            var big = new string('x', AgentProtocol.MaxMessageBytes);
            Assert.Throws<InvalidOperationException>(() => AgentMessageCodec.Serialize(new AgentMessage { Type = "log", Message = big }));
        }

        [Fact]
        public void Log_format_multiline_indents()
        {
            var line = AgentLogFormat.FormatLine(new LogEntry { At = new DateTime(2026, 10, 3, 14, 48, 5, 123), Tag = "INFO", Text = "a\nb" });
            Assert.Contains("    b", line, StringComparison.Ordinal);
        }

        [Fact]
        public void Run_guard_sanitize_job_name()
        {
            var name = RunGuard.SanitizeJobName("작업/TEST#1");
            Assert.DoesNotContain("/", name, StringComparison.Ordinal);
            Assert.True(name.Length <= 128);
        }
    }
}
