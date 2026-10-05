using System;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Validation;
using MigrationStudio.Logic;
using Xunit;

namespace MigrationStudio.Tests
{
    /// <summary>실제 Folderss + docker Oracle에 설치해 돌리다 잡은 오탐·표시 문제의 회귀 시험(2026-10-05).</summary>
    public sealed class RealOracleRegressionTests
    {
        [Theory]
        [InlineData("CLOB", "CLOB", CheckLevels.Pass)]
        [InlineData("BLOB", "BLOB", CheckLevels.Pass)]
        [InlineData("NCLOB", "NCLOB", CheckLevels.Pass)]
        [InlineData("RAW(16)", "RAW(32)", CheckLevels.Pass)]
        [InlineData("VARCHAR2(4000)", "CLOB", CheckLevels.Pass)]
        [InlineData("RAW(2000)", "BLOB", CheckLevels.Pass)]
        [InlineData("CLOB", "VARCHAR2(4000)", CheckLevels.Warn)]
        [InlineData("CLOB", "NCLOB", CheckLevels.Warn)]
        [InlineData("LONG", "CLOB", CheckLevels.Warn)]
        [InlineData("DATE", "CLOB", CheckLevels.Error)]
        [InlineData("CLOB", "NUMBER(10)", CheckLevels.Error)]
        public void Compat_lob_rules(string source, string target, string expected)
        {
            var r = MappingService.Compat(source, target, null);
            Assert.Equal(expected, r.Level);
        }

        [Fact]
        public void Tablespace_small_free_but_enough_is_pass()
        {
            // XE: USERS 여유 40MB, 필요 30MB — 소수 첫째 자리 반올림이면 0.0 GB로 보여 ERROR가 났다
            var item = ValidationEngine.TablespaceItem(new TablespaceInfo { Name = "USERS", FreeGb = 0.04 }, 0.03);
            Assert.Equal(CheckLevels.Warn, item.Level); // 70% 넘음
            Assert.Contains("0.04 GB", item.Detail);
            var ok = ValidationEngine.TablespaceItem(new TablespaceInfo { Name = "USERS", FreeGb = 0.5 }, 0.03);
            Assert.Equal(CheckLevels.Pass, ok.Level);
        }

        [Fact]
        public void Tablespace_unknown_autoextend_is_warn_not_error()
        {
            var item = ValidationEngine.TablespaceItem(new TablespaceInfo { Name = "USERS", FreeGb = 0.01 }, 0.03);
            Assert.Equal(CheckLevels.Warn, item.Level);
            Assert.Contains("자동 확장 여유는 확인 못 함", item.Detail);
        }

        [Fact]
        public void Tablespace_autoextend_counts_toward_capacity()
        {
            var pass = ValidationEngine.TablespaceItem(new TablespaceInfo { Name = "USERS", FreeGb = 0.01, AutoExtendGb = 5 }, 0.03);
            Assert.Equal(CheckLevels.Pass, pass.Level);
            Assert.Contains("자동 확장 5.00 GB", pass.Detail);
            var error = ValidationEngine.TablespaceItem(new TablespaceInfo { Name = "USERS", FreeGb = 0.01, AutoExtendGb = 0 }, 0.03);
            Assert.Equal(CheckLevels.Error, error.Level);
            Assert.Contains("ORA-01653", error.Detail);
        }

        [Fact]
        public void Tablespace_quota_shortfall_is_error()
        {
            var item = ValidationEngine.TablespaceItem(new TablespaceInfo { Name = "USERS", FreeGb = 10, AutoExtendGb = 10, QuotaLeftGb = 0.01 }, 0.03);
            Assert.Equal(CheckLevels.Error, item.Level);
            Assert.Contains("할당량", item.Detail);
        }

        [Fact]
        public void TestStatusLine_zero_latency_shows_under_1ms()
        {
            var session = new ConnectionSession { Status = ConnStatus.Ok };
            var result = new ConnectionTestResult { Ok = true, Version = "Oracle 12c", LatencyMs = 0, TestedAt = new DateTime(2020, 1, 1, 14, 20, 31) };
            Assert.Equal("ok|✓ Connected|Oracle 12c|<1|14:20:31", ConnectionLogic.TestStatusLine(session, result));
        }
    }
}
