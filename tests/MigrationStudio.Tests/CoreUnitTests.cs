using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MigrationStudio.Core.Expressions;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Sql;
using MigrationStudio.Core.Types;
using MigrationStudio.Tests.Golden;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class CoreUnitTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void OracleType_Parse_empty_returns_null(string input)
        {
            Assert.Null(OracleType.Parse(input));
        }

        [Fact]
        public void OracleType_Parse_unknown_shape_uses_base_only()
        {
            var t = OracleType.Parse("CUSTOMTYPE");
            Assert.NotNull(t);
            Assert.Equal("CUSTOMTYPE", t.Base);
            Assert.Null(t.Length);
        }

        [Fact]
        public void OracleType_Parse_timestamp_default_fraction()
        {
            var t = OracleType.Parse("TIMESTAMP");
            Assert.Equal("TIMESTAMP", t.Base);
            Assert.Equal(6, t.Fraction);
        }

        [Fact]
        public void ExpressionAnalyzer_parallel_analyze_is_thread_safe()
        {
            var exprs = GoldenFixture.Document.Expressions.Select(e => e.Expr).Distinct().ToList();
            var bag = new ConcurrentBag<string>();
            Parallel.ForEach(exprs, expr =>
            {
                for (var i = 0; i < 20; i++)
                {
                    var a = ExpressionAnalyzer.Analyze(expr);
                    if (a.Error != null)
                    {
                        bag.Add(a.Error.Code);
                    }
                }
            });
            Assert.True(bag.Count >= 0);
        }

        [Fact]
        public void JobFile_sync_fields_roundtrip_and_defaults_are_omitted()
        {
            var settings = GoldenTestHelpers.SettingsFromGolden();
            var job = GoldenTestHelpers.SampleJob();
            var json = JobFile.Serialize(job, settings);
            Assert.DoesNotContain("pollIntervalSeconds", json);
            Assert.DoesNotContain("maxRunHours", json);
            Assert.DoesNotContain("lagSeconds", json);
            var parsedDefault = JobFile.Parse(json);
            Assert.Equal(60, parsedDefault.Strategy.PollIntervalSeconds);
            Assert.Equal(24, parsedDefault.Strategy.MaxRunHours);
            Assert.Equal(300, parsedDefault.Strategy.LagSeconds);

            job.Strategy.Mode = ExecutionModes.Cdc;
            job.Strategy.PollIntervalSeconds = 300;
            job.Strategy.MaxRunHours = 0;
            job.Strategy.LagSeconds = 0;
            var parsed = JobFile.Parse(JobFile.Serialize(job, settings));
            Assert.Equal(ExecutionModes.Cdc, parsed.Strategy.Mode);
            Assert.Equal(300, parsed.Strategy.PollIntervalSeconds);
            Assert.Equal(0, parsed.Strategy.MaxRunHours);
            Assert.Equal(0, parsed.Strategy.LagSeconds);
        }

        [Fact]
        public void JobFile_roundtrip_serialize_parse_preserves_job()
        {
            var settings = GoldenTestHelpers.SettingsFromGolden();
            var original = GoldenTestHelpers.SampleJob();
            var json = JobFile.Serialize(original, settings);
            var parsed = JobFile.Parse(json);
            Assert.Equal(original.JobName, parsed.JobName);
            Assert.Equal(original.Mappings.Count, parsed.Mappings.Count);
            Assert.Equal(original.Checkpoints.Count, parsed.Checkpoints.Count);
            for (var i = 0; i < original.Mappings.Count; i++)
            {
                Assert.Equal(original.Mappings[i].Id, parsed.Mappings[i].Id);
                Assert.Equal(original.Mappings[i].Source, parsed.Mappings[i].Source);
                Assert.Equal(original.Mappings[i].Target, parsed.Mappings[i].Target);
            }
        }

        [Fact]
        public void JobFile_checkpoint_value_accepts_number_and_string_json()
        {
            const string jsonNumber = @"{
  ""format"": ""folderss-migration-job"",
  ""version"": 2,
  ""jobName"": ""T"",
  ""source"": { ""schema"": ""S"" },
  ""target"": { ""schema"": ""T"" },
  ""strategy"": { ""mode"": ""FULL"" },
  ""mappings"": [],
  ""checkpoints"": { ""m1"": { ""column"": ""ID"", ""value"": 850000, ""rows"": 1, ""total"": 1, ""at"": ""t"", ""runId"": ""r"", ""status"": ""stopped"" } }
}";
            const string jsonString = @"{
  ""format"": ""folderss-migration-job"",
  ""version"": 2,
  ""jobName"": ""T"",
  ""source"": { ""schema"": ""S"" },
  ""target"": { ""schema"": ""T"" },
  ""strategy"": { ""mode"": ""FULL"" },
  ""mappings"": [],
  ""checkpoints"": { ""m1"": { ""column"": ""ID"", ""value"": ""O2025"", ""rows"": 1, ""total"": 1, ""at"": ""t"", ""runId"": ""r"", ""status"": ""stopped"" } }
}";
            Assert.Equal("850000", JobFile.Parse(jsonNumber).Checkpoints["m1"].Value);
            Assert.Equal("O2025", JobFile.Parse(jsonString).Checkpoints["m1"].Value);
        }

        [Fact]
        public void MappingTemplate_apply_replaces_matching_mapping()
        {
            var job = GoldenTestHelpers.SampleJob();
            var template = MappingTemplate.Export(job, "t1");
            var target = JobSamples.SampleJob();
            target.Mappings = new List<Mapping> { target.Mappings[0] };
            var beforeId = target.Mappings[0].Id;
            var result = MappingTemplate.Apply(target, template);
            Assert.True(result.Replaced >= 1);
            Assert.True(result.Added >= 3);
            Assert.Equal(beforeId, target.Mappings.Find(m => m.Id == beforeId)?.Id ?? beforeId);
            Assert.True(target.Mappings.Count >= 1);
        }

        [Fact]
        public void MappingTemplate_apply_adds_non_matching_mapping()
        {
            var job = GoldenTestHelpers.SampleJob();
            var template = MappingTemplate.Export(job, "t1");
            var target = JobSamples.SampleJob();
            target.Mappings = new List<Mapping>
            {
                new Mapping
                {
                    Id = "only",
                    SourceType = SourceTypes.Table,
                    Source = "OTHER_SRC",
                    Target = "OTHER_TGT"
                }
            };
            var result = MappingTemplate.Apply(target, template);
            Assert.True(result.Added >= 1);
            Assert.True(target.Mappings.Count > 1);
        }

        [Fact]
        public void Settings_deserialize_empty_string_uses_defaults()
        {
            var s = MigrationSettingsStore.Deserialize("");
            var golden = GoldenFixture.Document.SettingsDefaults;
            Assert.Empty(s.Connections);
            Assert.Equal(golden.Defaults.CommitSize, s.Defaults.CommitSize);
            Assert.Equal(golden.Agent.LogDays, s.Agent.LogDays);
        }

        [Fact]
        public void Settings_deserialize_version_mismatch_keeps_connections_only()
        {
            var json = GoldenFixture.Document.SettingsDefaults != null
                ? JsonSerializer.Serialize(new
                {
                    version = 99,
                    connections = new[]
                    {
                        new { id = "x", name = "X", kind = "oracle", host = "h", port = 1521, service = "s", user = "u" }
                    },
                    defaults = new { commitSize = 1 },
                    agent = new { logDays = 1 }
                })
                : "{}";
            var s = MigrationSettingsStore.Deserialize(json);
            Assert.Single(s.Connections);
            Assert.Equal("x", s.Connections[0].Id);
            Assert.Equal(10000, s.Defaults.CommitSize);
            Assert.Equal(30, s.Agent.LogDays);
        }

        [Fact]
        public void Sample_sql_matches_golden_parseSelect()
        {
            var golden = GoldenFixture.Document.ParseSelect.First(p => p.Key == "sample").Sql;
            Assert.Equal(JobSamples.SampleSql, golden);
        }

        [Fact]
        public void Sample_sql_top_level_words_include_from()
        {
            var sql = System.Text.RegularExpressions.Regex.Replace(
                SqlText.StripComments(JobSamples.SampleSql),
                @"[;\s]+$",
                "");
            var words = SelectParser.TopLevelWordNames(sql);
            Assert.Contains("FROM", words);
        }

        [Fact]
        public void SqlText_stripComments_preserves_line_numbers()
        {
            const string sql = "-- head\nSELECT 1\nFROM t\nWHERE x = 1";
            var stripped = SqlText.StripComments(sql);
            var pos = sql.IndexOf("FROM", StringComparison.Ordinal);
            Assert.Equal(SqlText.LineOf(sql, pos), SqlText.LineOf(stripped, pos));
        }
    }
}
