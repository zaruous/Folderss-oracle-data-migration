using System;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Logic;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class PluginLogicTests
    {
        [Fact]
        public void StepLogic_ConnectionMissingProfiles_IsError()
        {
            var job = JobLogic.NewJob(null);
            var info = StepLogic.ForStep(0, job, MigrationSettingsStore.CreateDefaultSettings(), null, null, null, null);
            Assert.Equal("error", info.State);
            Assert.Contains("접속 없음", info.Summary);
        }

        [Fact]
        public void StepLogic_ConnectionBothOkWithMeta_IsDone()
        {
            var settings = MigrationSettingsStore.CreateDefaultSettings();
            var src = new ConnectionProfile { Id = "a", Name = "SRC" };
            var tgt = new ConnectionProfile { Id = "b", Name = "TGT" };
            settings.Connections.Add(src);
            settings.Connections.Add(tgt);
            var job = JobLogic.NewJob(settings);
            JobLogic.ApplyProfileSelection(job, Roles.Source, src);
            JobLogic.ApplyProfileSelection(job, Roles.Target, tgt);

            var s = new ConnectionSession { Status = ConnStatus.Ok };
            var t = new ConnectionSession { Status = ConnStatus.Ok };
            var info = StepLogic.ForStep(0, job, settings, s, t, new Core.Metadata.SchemaMetadata(), new Core.Metadata.SchemaMetadata());
            Assert.Equal("done", info.State);
            Assert.Equal("SRC → TGT", info.Summary);
        }

        [Fact]
        public void ConnectionLogic_ComboItem_IncludesWriteBlocked()
        {
            var p = new ConnectionProfile { Name = "X", Host = "h", Service = "s", WriteBlocked = true };
            var text = ConnectionLogic.ComboItemText(p);
            Assert.Contains("쓰기 금지", text);
        }

        [Fact]
        public void ConnectionLogic_TestLine_FormatsOk()
        {
            var session = new ConnectionSession { Status = ConnStatus.Ok };
            var result = new ConnectionTestResult { Ok = true, Version = "Oracle 12c", LatencyMs = 23, TestedAt = new DateTime(2020, 1, 1, 14, 20, 31) };
            var line = ConnectionLogic.TestStatusLine(session, result);
            Assert.Equal("ok|✓ Connected|Oracle 12c|23|14:20:31", line);
        }

        [Fact]
        public void ConnectionLogic_MetaLine_CacheSuffix()
        {
            var meta = new Core.Metadata.SchemaMetadata
            {
                Schema = "LEGACY_APP",
                Cached = true,
                LoadedAt = new DateTime(2020, 1, 1, 14, 20, 31),
                Tables = new System.Collections.Generic.List<Core.Metadata.TableMetadata>()
            };
            var line = ConnectionLogic.MetaStatusLine(meta, new ConnectionSession(), false);
            Assert.Equal("LEGACY_APP · 테이블 0 · 뷰 0 · 컬럼 0 (캐시 · 14:20:31)", line);
        }

        [Fact]
        public void JobLogic_ResolveConnections_ByNameCaseInsensitive()
        {
            var settings = MigrationSettingsStore.CreateDefaultSettings();
            var p = new ConnectionProfile { Id = "id1", Name = "MyConn", Host = "h", Port = 1521, Service = "s", User = "u" };
            settings.Connections.Add(p);
            var job = JobLogic.NewJob(settings);
            job.Source = new ConnectionRef { Name = "myconn", Host = "h" };
            var changed = JobLogic.ResolveConnections(job, settings);
            Assert.True(changed);
            Assert.Equal("id1", job.Source.ProfileId);
        }

        [Fact]
        public void JobLogic_ProfileFromMissing_DuplicateNameGetsSuffix()
        {
            var settings = MigrationSettingsStore.CreateDefaultSettings();
            settings.Connections.Add(new ConnectionProfile { Id = "x", Name = "LEGACY" });
            var missing = new ConnectionRef { Name = "LEGACY" };
            var created = JobLogic.ProfileFromMissing(missing, settings);
            Assert.Equal("LEGACY_2", created.Name);
        }

        [Fact]
        public void SettingsLogic_CannotDeleteProfileUsedByJob()
        {
            var settings = MigrationSettingsStore.CreateDefaultSettings();
            var p = SettingsLogic.AddProfile(settings);
            var job = JobLogic.NewJob(settings);
            job.Source = new ConnectionRef { ProfileId = p.Id };
            Assert.False(SettingsLogic.CanDeleteProfile(job, p));
        }

        [Fact]
        public void SettingsLogic_ValidateAll_ReturnsFailedProfileId()
        {
            var settings = MigrationSettingsStore.CreateDefaultSettings();
            var p = SettingsLogic.AddProfile(settings);
            p.Host = "";
            string err;
            var id = SettingsLogic.ValidateAll(settings, out err);
            Assert.Equal(p.Id, id);
            Assert.Contains("호스트", err);
        }
    }
}
