using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Sql;
using MigrationStudio.Core.Types;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Engine
{
    public static class RunPlanner
    {
        public static List<MappingModel> OrderByFk(IEnumerable<MappingModel> mappings, SchemaMetadata targetMetadata)
        {
            var input = (mappings ?? Array.Empty<MappingModel>()).ToList();
            var result = new List<MappingModel>();
            var waiting = new List<MappingModel>(input);
            for (var guard = 0; guard < 100 && waiting.Count > 0; guard++)
            {
                var moved = false;
                for (var i = 0; i < waiting.Count; i++)
                {
                    var mapping = waiting[i];
                    var table = targetMetadata != null ? targetMetadata.FindTable(mapping.Target) : null;
                    var parents = table == null ? new List<string>() : table.ForeignKeys.Select(f => f.RefTable).ToList();
                    var blocked = waiting.Any(other => other != mapping && parents.Any(p => string.Equals(p, other.Target, StringComparison.OrdinalIgnoreCase)));
                    if (blocked)
                    {
                        continue;
                    }

                    result.Add(mapping);
                    waiting.RemoveAt(i--);
                    moved = true;
                }

                if (!moved)
                {
                    // 순환 FK는 사용자가 정한 입력 순서가 가장 예측 가능하다.
                    result.AddRange(input.Where(waiting.Contains));
                    waiting.Clear();
                }
            }

            result.AddRange(waiting);
            return result;
        }

        public static async Task<List<PlanItem>> BuildAsync(MigrationJob job, MigrationSettings settings, RunMetadata metadata,
            IEnumerable<string> selectedIds, string runMode, ISourceProbe probe, ICheckpointStore store, CancellationToken cancellationToken)
        {
            var ids = new HashSet<string>(selectedIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            var selected = job.Mappings.Where(m => ids.Contains(m.Id)).ToList();
            var ordered = OrderByFk(selected, metadata != null ? metadata.Target : null);
            var plans = new List<PlanItem>();
            var usedErrorTables = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapping in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = mapping.IsSql && metadata != null && metadata.SqlSources.ContainsKey(mapping.Id)
                    ? metadata.SqlSources[mapping.Id]
                    : metadata != null && metadata.Source != null ? metadata.Source.FindTable(mapping.Source) : null;
                var target = metadata != null && metadata.Target != null ? metadata.Target.FindTable(mapping.Target) : null;
                var item = new PlanItem
                {
                    Key = mapping.Id,
                    Mapping = mapping,
                    Label = mapping.Label,
                    SourceMetadata = source,
                    TargetMetadata = target,
                    WriteColumns = SqlGenerator.WriteColumns(mapping, target),
                    ErrorTable = ErrorTableNamer.Resolve(job.Strategy, mapping.Target, usedErrorTables)
                };
                item.ScopeTotal = !mapping.IsSql && string.IsNullOrWhiteSpace(mapping.Where) &&
                    source != null && source.Rows.HasValue && source.Rows.Value > 0
                    ? source.Rows.Value
                    : await probe.CountAsync(item, cancellationToken).ConfigureAwait(false);
                item.TargetRowsBefore = await probe.ExistingKeyCountAsync(item, cancellationToken).ConfigureAwait(false);

                var workers = Math.Max(1, job.Strategy.Workers);
                CheckpointRecord checkpoint = null;
                var records = new List<CheckpointRecord>();
                if (string.Equals(runMode, "RESUME", StringComparison.Ordinal))
                {
                    records = await store.ListAsync(job.JobName, cancellationToken).ConfigureAwait(false);
                    checkpoint = await store.GetAsync(job.JobName, mapping.Id, cancellationToken).ConfigureAwait(false);
                    if (checkpoint == null)
                    {
                        checkpoint = records.FirstOrDefault(r => r.TaskKey != null && r.TaskKey.StartsWith(mapping.Id + "#", StringComparison.Ordinal));
                    }

                    if (checkpoint == null)
                    {
                        item.Notes = "저장된 체크포인트가 없어 처음부터 실행";
                    }
                    else
                    {
                        item.ResumeFrom = checkpoint.Value;
                        item.BaseRows = records.Where(r => r.TaskKey == mapping.Id || r.TaskKey.StartsWith(mapping.Id + "#", StringComparison.Ordinal)).Sum(r => r.RowsDone);
                    }
                }

                var checkpointColumn = source != null ? source.FindColumn(mapping.CheckpointColumn) : null;
                if (workers > 1 && (!CanSplit(checkpointColumn) || !MergeKeyMatchesCheckpoint(mapping)))
                {
                    workers = 1;
                    item.Notes = !MergeKeyMatchesCheckpoint(mapping)
                        ? "작업자 수를 1로 낮춤: 병합 키와 체크포인트 열이 다름"
                        : "작업자 수를 1로 낮춤: 체크포인트 열이 숫자·날짜가 아님";
                }

                var savedRanges = records.Where(r => r.TaskKey != null && r.TaskKey.StartsWith(mapping.Id + "#", StringComparison.Ordinal))
                    .OrderBy(r => r.TaskKey, StringComparer.Ordinal).ToList();
                if (savedRanges.Count > 0)
                {
                    item.Ranges = savedRanges.Select(r => new KeyRange { From = r.RangeFrom, To = r.RangeTo, Rows = r.RowsTotal, Last = r.Value, BaseRows = r.RowsDone }).ToList();
                }
                else if (workers > 1)
                {
                    item.Ranges = await probe.RangesAsync(item, workers, item.ResumeFrom, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    item.Ranges.Add(new KeyRange { From = null, To = null, Rows = item.ScopeTotal, Last = item.ResumeFrom, BaseRows = item.BaseRows });
                }

                plans.Add(item);
            }

            return plans;
        }

        private static bool CanSplit(ColumnMetadata column)
        {
            if (column == null || string.IsNullOrEmpty(column.Type))
            {
                return false;
            }

            var type = OracleType.Parse(column.Type);
            return type != null && (type.IsNumber || type.IsDate);
        }

        private static bool MergeKeyMatchesCheckpoint(MappingModel mapping)
        {
            if (!string.Equals(mapping.Mode, WriteModes.Merge, StringComparison.Ordinal))
            {
                return true;
            }

            var targetCheckpoint = mapping.Columns
                .Where(c => string.Equals(c.Source, mapping.CheckpointColumn, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Target).FirstOrDefault() ?? mapping.CheckpointColumn;
            return mapping.MergeKey.Count == 1 && string.Equals(mapping.MergeKey[0], targetCheckpoint, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static class ErrorTableNamer
    {
        public static string Resolve(MigrationStrategy strategy, string table)
        {
            if (strategy == null || !string.Equals(strategy.ErrorPolicy, ErrorPolicies.Continue, StringComparison.Ordinal))
            {
                return null;
            }

            var configured = (strategy.ErrorTable ?? "ERR$_").Trim();
            if (!configured.EndsWith("_", StringComparison.Ordinal))
            {
                return configured;
            }

            var name = configured + table;
            return name.Length <= 30 ? name : "ERR$_" + (table ?? "").Substring(0, Math.Min(25, (table ?? "").Length));
        }

        public static string Resolve(MigrationStrategy strategy, string table, ISet<string> used)
        {
            var baseName = Resolve(strategy, table);
            if (baseName == null || used == null || used.Add(baseName))
            {
                return baseName;
            }

            for (var i = 1; i < 1000; i++)
            {
                var suffix = "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var candidate = baseName.Substring(0, Math.Min(baseName.Length, 30 - suffix.Length)) + suffix;
                if (used.Add(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("오류 테이블 이름 충돌을 해결할 수 없습니다.");
        }
    }
}
