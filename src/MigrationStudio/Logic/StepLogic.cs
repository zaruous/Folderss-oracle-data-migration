using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;

namespace MigrationStudio.Logic
{
    public sealed class StepInfo
    {
        public string State { get; set; } = "";
        public string Summary { get; set; } = "";
    }

    /// <summary>단계 막대 번호 원·요약(POC stepInfo와 같음).</summary>
    public static class StepLogic
    {
        public static StepInfo ForStep(
            int stepIndex,
            MigrationJob job,
            MigrationSettings settings,
            ConnectionSession source,
            ConnectionSession target,
            SchemaMetadata metaSource,
            SchemaMetadata metaTarget)
        {
            return ForStep(stepIndex, job, settings, source, target, metaSource, metaTarget, null);
        }

        public static StepInfo ForStep(
            int stepIndex,
            MigrationJob job,
            MigrationSettings settings,
            ConnectionSession source,
            ConnectionSession target,
            SchemaMetadata metaSource,
            SchemaMetadata metaTarget,
            PreSessionSummary pre)
        {
            return ForStep(stepIndex, job, settings, source, target, metaSource, metaTarget, pre, null);
        }

        public static StepInfo ForStep(
            int stepIndex,
            MigrationJob job,
            MigrationSettings settings,
            ConnectionSession source,
            ConnectionSession target,
            SchemaMetadata metaSource,
            SchemaMetadata metaTarget,
            PreSessionSummary pre,
            RunSummaryInput run)
        {
            if (stepIndex < 0 || stepIndex >= Labels.StepKeys.Length)
            {
                return new StepInfo();
            }

            switch (Labels.StepKeys[stepIndex])
            {
                case "connection":
                    return ConnectionStep(settings, job, source, target, metaSource, metaTarget);
                case "tables":
                    return TablesStep(job);
                case "columns":
                    return ColumnsStep(job, metaSource, metaTarget);
                case "validation":
                    return ValidationLogic.StepSummary(pre);
                case "run":
                    return RunStep(job, run);
                default:
                    return new StepInfo();
            }
        }

        private static StepInfo ConnectionStep(
            MigrationSettings settings,
            MigrationJob job,
            ConnectionSession source,
            ConnectionSession target,
            SchemaMetadata metaSource,
            SchemaMetadata metaTarget)
        {
            var srcProfile = ProfileForRole(settings, job, Roles.Source);
            var tgtProfile = ProfileForRole(settings, job, Roles.Target);
            if (srcProfile == null || tgtProfile == null)
            {
                return new StepInfo { State = "error", Summary = NameOrMissing(srcProfile) + " → " + NameOrMissing(tgtProfile) };
            }

            var a = source != null ? source.Status : ConnStatus.Unknown;
            var b = target != null ? target.Status : ConnStatus.Unknown;
            if (a == ConnStatus.Error || b == ConnStatus.Error)
            {
                return new StepInfo { State = "error", Summary = srcProfile.Name + " → " + tgtProfile.Name };
            }

            if (a == ConnStatus.Testing || b == ConnStatus.Testing)
            {
                return new StepInfo { State = "busy", Summary = srcProfile.Name + " → " + tgtProfile.Name };
            }

            if (a == ConnStatus.Ok && b == ConnStatus.Ok && metaSource != null && metaTarget != null)
            {
                return new StepInfo { State = "done", Summary = srcProfile.Name + " → " + tgtProfile.Name };
            }

            return new StepInfo { State = "", Summary = srcProfile.Name + " → " + tgtProfile.Name };
        }

        private static StepInfo TablesStep(MigrationJob job)
        {
            var n = job != null && job.Mappings != null ? job.Mappings.Count : 0;
            if (n == 0)
            {
                return new StepInfo { State = "", Summary = "매핑 없음" };
            }

            var used = job.Mappings.Count(m => m.Use);
            var sql = job.Mappings.Count(m => m.IsSql);
            var sub = n.ToString(CultureInfo.InvariantCulture) + "개 매핑";
            if (sql > 0)
            {
                sub += "(SQL " + sql.ToString(CultureInfo.InvariantCulture) + ")";
            }

            sub += " · 사용 " + used.ToString(CultureInfo.InvariantCulture);
            return new StepInfo { State = "done", Summary = sub };
        }

        private static StepInfo ColumnsStep(MigrationJob job, SchemaMetadata metaSource, SchemaMetadata metaTarget)
        {
            if (metaSource == null || metaTarget == null || job == null || job.Mappings == null || job.Mappings.Count == 0)
            {
                return new StepInfo { State = "", Summary = "—" };
            }

            var mapped = 0;
            var total = 0;
            var err = 0;
            var warn = 0;
            foreach (var m in job.Mappings)
            {
                var src = FindTable(metaSource, m);
                var tgt = FindTableByName(metaTarget, m.Target);
                var st = MappingService.Status(m, src, tgt);
                mapped += st.Mapped;
                total += st.Total;
                err += st.Errors;
                warn += st.Warns;
            }

            if (total == 0)
            {
                return new StepInfo { State = "", Summary = "—" };
            }

            var state = err > 0 ? "error" : warn > 0 ? "warn" : "done";
            var summary = "컬럼 " + mapped.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);
            if (err > 0)
            {
                summary += " · 오류 " + err.ToString(CultureInfo.InvariantCulture);
            }
            else if (warn > 0)
            {
                summary += " · 경고 " + warn.ToString(CultureInfo.InvariantCulture);
            }

            return new StepInfo { State = state, Summary = summary };
        }

        private static StepInfo RunStep(MigrationJob job, RunSummaryInput run)
        {
            if (run != null && run.State != RunStates.Idle && !string.IsNullOrEmpty(run.State))
            {
                return RunLogic.StepSummary(run);
            }

            if (job == null || job.Checkpoints == null)
            {
                return new StepInfo { State = "", Summary = "대기" };
            }

            CheckpointInfo cp = null;
            foreach (var pair in job.Checkpoints)
            {
                if (pair.Value != null && !string.Equals(pair.Value.Status, "done", StringComparison.Ordinal))
                {
                    cp = pair.Value;
                    break;
                }
            }

            if (cp == null)
            {
                return new StepInfo { State = "", Summary = "대기" };
            }

            var pct = cp.Total > 0 ? (int)Math.Floor(100.0 * cp.Rows / cp.Total) : 0;
            return new StepInfo { State = "warn", Summary = "재개 가능 · " + pct.ToString(CultureInfo.InvariantCulture) + "%" };
        }

        private static TableMetadata FindTable(SchemaMetadata meta, Mapping mapping)
        {
            if (meta == null || mapping == null || mapping.IsSql || string.IsNullOrEmpty(mapping.Source))
            {
                return null;
            }

            return FindTableByName(meta, mapping.Source);
        }

        private static TableMetadata FindTableByName(SchemaMetadata meta, string tableName)
        {
            if (meta == null || string.IsNullOrEmpty(tableName))
            {
                return null;
            }

            return meta.Tables != null ? meta.Tables.Find(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase)) : null;
        }

        private static ConnectionProfile ProfileForRole(MigrationSettings settings, MigrationJob job, string role)
        {
            if (settings == null || job == null)
            {
                return null;
            }

            var id = role == Roles.Source ? job.Source != null ? job.Source.ProfileId : null : job.Target != null ? job.Target.ProfileId : null;
            return string.IsNullOrEmpty(id) ? null : MigrationSettingsStore.Find(settings, id);
        }

        private static string NameOrMissing(ConnectionProfile profile)
        {
            return profile != null && !string.IsNullOrEmpty(profile.Name) ? profile.Name : "접속 없음";
        }
    }

    public static class ConnStatus
    {
        public const string Unknown = "unknown";
        public const string Testing = "testing";
        public const string Ok = "ok";
        public const string Error = "error";
    }

    public sealed class ConnectionSession
    {
        public string Status { get; set; } = ConnStatus.Unknown;
        public bool MetaLoading { get; set; }
        public string MetaError { get; set; }
    }
}
