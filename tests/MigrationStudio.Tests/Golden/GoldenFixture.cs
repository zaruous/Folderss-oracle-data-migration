using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MigrationStudio.Tests.Golden
{
    public static class GoldenFixture
    {
        public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

        public static GoldenDocument Document { get; } = Load();

        private static JsonSerializerOptions CreateOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            };
        }

        private static GoldenDocument Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Golden", "poc-golden.json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Golden file not found at output directory: " + path, path);
            }

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var golden = JsonSerializer.Deserialize<GoldenDocument>(json, JsonOptions);
            if (golden == null)
            {
                throw new InvalidOperationException("Failed to deserialize poc-golden.json");
            }

            if (root.TryGetProperty("sampleJob", out var sampleJob))
            {
                golden.SampleJob = sampleJob.Clone();
            }

            if (root.TryGetProperty("jobV1", out var jobV1))
            {
                golden.JobV1 = jobV1.Clone();
            }

            if (root.TryGetProperty("settingsDefaults", out var settings))
            {
                golden.SettingsDefaults = new GoldenSettingsDefaults
                {
                    Version = settings.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number
                        ? ver.GetInt32()
                        : 1,
                    Defaults = settings.TryGetProperty("defaults", out var defs)
                        ? JsonSerializer.Deserialize<MigrationStudio.Core.Settings.MigrationDefaults>(defs.GetRawText(), JsonOptions)
                        : new MigrationStudio.Core.Settings.MigrationDefaults(),
                    Agent = settings.TryGetProperty("agent", out var agent)
                        ? JsonSerializer.Deserialize<MigrationStudio.Core.Settings.AgentSettings>(agent.GetRawText(), JsonOptions)
                        : new MigrationStudio.Core.Settings.AgentSettings()
                };
            }

            return golden;
        }
    }
}
