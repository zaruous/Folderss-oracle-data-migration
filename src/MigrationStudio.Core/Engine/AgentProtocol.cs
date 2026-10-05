using System.Text.Json;
using System.Text.Json.Serialization;

namespace MigrationStudio.Core.Engine
{
    /// <summary>플러그인과 MigrationAgent.exe가 주고받는 파이프 프로토콜. 메시지 모양이 바뀌면 <see cref="Version"/>을 올린다.</summary>
    public static class AgentProtocol
    {
        public const int Version = 1;
        public const int MaxMessageBytes = 8 * 1024 * 1024;

        public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false
            };
            return options;
        }
    }
}
