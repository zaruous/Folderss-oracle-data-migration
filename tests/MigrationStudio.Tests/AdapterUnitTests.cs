using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Adapters.Oracle;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Storage;
using MigrationStudio.Tests.Golden;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class AdapterUnitTests
    {
        [Theory]
        [InlineData("11.2.0.4.0", "Oracle 11g")]
        [InlineData("12.1.0.2.0", "Oracle 12c")]
        [InlineData("18.0.0.0.0", "Oracle 18c")]
        [InlineData("19.0.0.0.0", "Oracle 19c")]
        [InlineData("21.0.0.0.0", "Oracle 21c")]
        [InlineData("23.4.0.0.0", "Oracle 23ai")]
        [InlineData("weird", "Oracle weird")]
        public void OracleVersionText_maps_major(string server, string expected)
        {
            Assert.Equal(expected, OracleVersionText.FromServerVersion(server));
        }

        [Fact]
        public void ConnectionTarget_Describe_omits_password()
        {
            var t = new ConnectionTarget
            {
                User = "LEGACY_APP",
                Host = "10.10.10.21",
                Port = 1521,
                Service = "LEGACY",
                Password = "secret"
            };
            Assert.Equal("LEGACY_APP@10.10.10.21:1521/LEGACY", t.Describe());
            Assert.Equal(t.Describe(), t.ToString());
        }

        [Fact]
        public void ConnectionTarget_validate_matches_profile_messages()
        {
            var t = new ConnectionTarget { Host = "", Port = 1521, Service = "xe", User = "u", Password = "p" };
            Assert.Contains("호스트", ConnectionTarget.Validate(t));
            t.Host = "localhost";
            t.Port = 70000;
            Assert.Contains("포트", ConnectionTarget.Validate(t));
            t.Port = 1521;
            t.Password = "";
            Assert.Equal("비밀번호를 입력하세요", ConnectionTarget.Validate(t));
        }

        [Theory]
        [InlineData("VARCHAR2", 30, 20, "C", null, null, "VARCHAR2(20 CHAR)")]
        [InlineData("VARCHAR2", 30, 30, "B", null, null, "VARCHAR2(30)")]
        [InlineData("NVARCHAR2", 40, 25, null, null, null, "NVARCHAR2(25)")]
        [InlineData("NUMBER", 0, 0, null, null, null, "NUMBER")]
        [InlineData("NUMBER", 0, 0, null, null, 0, "NUMBER(38)")]
        [InlineData("NUMBER", 0, 0, null, 12, 0, "NUMBER(12)")]
        [InlineData("NUMBER", 0, 0, null, 12, 2, "NUMBER(12,2)")]
        [InlineData("FLOAT", 0, 0, null, 63, null, "FLOAT(63)")]
        [InlineData("RAW", 16, 0, null, null, null, "RAW(16)")]
        [InlineData("DATE", 7, 0, null, null, null, "DATE")]
        [InlineData("TIMESTAMP(6) WITH TIME ZONE", 0, 0, null, null, null, "TIMESTAMP(6) WITH TIME ZONE")]
        public void Oracle_column_type_format_rules(
            string dataType,
            int dataLength,
            int charLength,
            string charUsed,
            int? precision,
            int? scale,
            string expected)
        {
            Assert.Equal(expected, OracleColumnTypeFormatter.Format(dataType, dataLength, charLength, charUsed, precision, scale));
        }

        [Fact]
        public void OracleErrors_describe_cancellation_and_connect()
        {
            Assert.Equal("실행을 취소했습니다.", OracleErrors.Describe(new OperationCanceledException()));
            Assert.Equal("실행을 취소했습니다.",
                OracleErrors.Describe(new Exception("ORA-01013: user requested cancel of current operation")));
            const string generic = "ORA-50201: Oracle Communication: Failed to connect to server or failed to parse connect string";
            var error = new Exception(generic,
                new Exception(generic, new Exception("ORA-12514: TNS:listener does not currently know of service requested in connect descriptor")));
            Assert.Equal(
                "ORA-12514: TNS:listener does not currently know of service requested in connect descriptor (ORA-50201)",
                OracleErrors.Describe(error));
            Assert.Equal("첫 줄", OracleErrors.Describe(new Exception("첫 줄\r\n둘째 줄")));
        }

        [Fact]
        public void DatabaseAdapters_for_oracle_and_planned()
        {
            Assert.Equal("oracle", DatabaseAdapters.For(null).Kind);
            Assert.Throws<NotSupportedException>(() => DatabaseAdapters.For("postgresql"));
        }

        [Fact]
        public void MetadataCache_roundtrip_and_atomic()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ms-p2-" + Guid.NewGuid().ToString("N"));
            try
            {
                var meta = new SchemaMetadata
                {
                    Schema = "LEGACY_APP",
                    Tables = new List<TableMetadata>
                    {
                        new TableMetadata { Name = "T1", Kind = "TABLE", Comment = "한글 주석" }
                    },
                    LoadedAt = DateTime.Now,
                    ElapsedMs = 12
                };
                MetadataCache.Save(dir, "cn-test", meta);
                var loaded = MetadataCache.TryLoad(dir, "cn-test", "LEGACY_APP");
                Assert.NotNull(loaded);
                Assert.True(loaded.Cached);
                Assert.Equal("T1", loaded.Tables[0].Name);
                Assert.Equal("한글 주석", loaded.Tables[0].Comment);
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        }

        [Fact]
        public void MetadataCache_broken_file_returns_null()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ms-p2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "metadata"));
            var path = MetadataCache.PathFor(dir, "p", "S");
            File.WriteAllText(path, "{ not json");
            Assert.Null(MetadataCache.TryLoad(dir, "p", "S"));
        }

        [Fact]
        public void JobDraftStore_roundtrip_and_bad_file()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ms-p2-" + Guid.NewGuid().ToString("N"));
            try
            {
                var job = GoldenTestHelpers.SampleJob();
                var draft = new JobDraft { Job = job, Dirty = true };
                JobDraftStore.Save(dir, draft);
                var loaded = JobDraftStore.TryLoad(dir);
                Assert.NotNull(loaded);
                Assert.Equal(job.JobName, loaded.Job.JobName);
                Assert.True(loaded.Dirty);

                File.WriteAllText(JobDraftStore.PathFor(dir), "{ broken");
                Assert.Null(JobDraftStore.TryLoad(dir));
                Assert.True(File.Exists(JobDraftStore.PathFor(dir) + ".bad"));
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        }

        [Fact]
        public void LoadMetadata_honors_precanceled_token()
        {
            var adapter = DatabaseAdapters.For("oracle");
            var target = new ConnectionTarget
            {
                Host = "localhost",
                Port = 1521,
                Service = "xe",
                User = "u",
                Password = "p"
            };
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                adapter.LoadMetadataAsync(target, "S", cts.Token).GetAwaiter().GetResult());
        }
    }
}
