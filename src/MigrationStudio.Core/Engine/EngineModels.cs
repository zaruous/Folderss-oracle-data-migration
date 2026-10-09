using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Sql;
using MappingModel = MigrationStudio.Core.Model.Mapping;

namespace MigrationStudio.Core.Engine
{
    public static class RunIds
    {
        public static string New(DateTime now)
        {
            return "R-" + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>실행 모드(RunSpec.RunMode). SYNC는 CDC(변경동기화): 워터마크 이후 행을 주기마다 반복해서 읽는다.</summary>
    public static class RunModes
    {
        public const string Dry = "DRY";
        public const string Execute = "EXECUTE";
        public const string Resume = "RESUME";
        public const string Sync = "SYNC";
    }

    public sealed class EndpointSpec
    {
        public ConnectionTarget Connection { get; set; }
        public string Schema { get; set; }
        public string ProfileName { get; set; }
        public string Color { get; set; }
    }

    public sealed class RunSpec
    {
        public string RunId { get; set; }
        public string RunMode { get; set; }
        public MigrationJob Job { get; set; }
        public EndpointSpec Source { get; set; }
        public EndpointSpec Target { get; set; }
        public string CheckpointStore { get; set; }
        public string ControlPrefix { get; set; } = "MIG_";
        public string DataDirectory { get; set; }
        public List<PlanItem> Plan { get; set; } = new List<PlanItem>();
        public string OnHostExit { get; set; }
        public List<int> RetryDelays { get; set; } = new List<int> { 1000, 2000, 4000 };
    }

    public sealed class PlanItem
    {
        public string Key { get; set; }
        public MappingModel Mapping { get; set; }
        public string Label { get; set; }
        public long ScopeTotal { get; set; }
        public string ResumeFrom { get; set; }
        public long BaseRows { get; set; }
        public List<KeyRange> Ranges { get; set; } = new List<KeyRange>();
        public long TargetRowsBefore { get; set; }
        public string ErrorTable { get; set; }
        public string Notes { get; set; }
        public TableMetadata SourceMetadata { get; set; }
        public TableMetadata TargetMetadata { get; set; }
        public List<WriteColumn> WriteColumns { get; set; } = new List<WriteColumn>();
    }

    public sealed class KeyRange
    {
        public string From { get; set; }
        public string To { get; set; }
        public long Rows { get; set; }
        public string Last { get; set; }
        public long BaseRows { get; set; }
    }

    public sealed class WriteResult
    {
        public int Written { get; set; }
        public int Rejected { get; set; }
        public int Updated { get; set; }
        public int Inserted { get; set; }
        public List<RejectedRow> Rejects { get; set; } = new List<RejectedRow>();
    }

    public sealed class RejectedRow
    {
        public int Index { get; set; }
        public string Message { get; set; }
        public object[] Values { get; set; }
    }

    public sealed class CheckpointRecord
    {
        public string Job { get; set; }
        public string TaskKey { get; set; }
        public string Column { get; set; }
        public string Value { get; set; }
        public string RangeFrom { get; set; }
        public string RangeTo { get; set; }
        public long RowsDone { get; set; }
        public long RowsTotal { get; set; }
        public string Status { get; set; }
        public string RunId { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public sealed class LogEntry
    {
        public DateTime At { get; set; }
        public string Tag { get; set; }
        public string Text { get; set; }
    }

    public sealed class RunSnapshot
    {
        public string RunId { get; set; }
        public string State { get; set; }
        public string Mode { get; set; }
        public double Elapsed { get; set; }
        public double Rate { get; set; }
        public List<TaskSnapshot> Tasks { get; set; } = new List<TaskSnapshot>();
        public PipelineSnapshot Pipeline { get; set; } = new PipelineSnapshot();
        public TotalsSnapshot Totals { get; set; } = new TotalsSnapshot();
        /// <summary>SYNC(변경동기화)일 때만. 주기 번호·시각과 주기 누적 합계.</summary>
        public SyncSnapshot Sync { get; set; }
    }

    public sealed class SyncSnapshot
    {
        /// <summary>지금 돌고 있거나 마지막으로 끝난 주기 번호(1부터).</summary>
        public int Cycle { get; set; }
        /// <summary>cycle(주기 실행 중) · waiting(다음 주기 대기).</summary>
        public string Phase { get; set; }
        public DateTime? LastCycleAt { get; set; }
        public DateTime? NextCycleAt { get; set; }
        public int IntervalSeconds { get; set; }
        public int MaxRunHours { get; set; }
        public long Written { get; set; }
        public long Inserted { get; set; }
        public long Updated { get; set; }
        public long Rejected { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    public sealed class TaskSnapshot
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Status { get; set; }
        public long Total { get; set; }
        public long Read { get; set; }
        public long Written { get; set; }
        public long Pending { get; set; }
        public long Inserted { get; set; }
        public long Updated { get; set; }
        public long Rejected { get; set; }
        public int Commits { get; set; }
        public string Checkpoint { get; set; }
        public double Elapsed { get; set; }
        public double RateNow { get; set; }
        public List<RangeSnapshot> Ranges { get; set; } = new List<RangeSnapshot>();
    }

    public sealed class RangeSnapshot
    {
        public string From { get; set; }
        public string To { get; set; }
        public string Last { get; set; }
        public long Rows { get; set; }
        public long Done { get; set; }
        public string Status { get; set; }
    }

    public sealed class PipelineSnapshot
    {
        public double BufferPercent { get; set; }
        public int WritingWorkers { get; set; }
        public double ReadRowsPerSecond { get; set; }
        public double CommitsPerSecond { get; set; }
        public int MaxBufferedBatches { get; set; }
    }

    public sealed class TotalsSnapshot
    {
        public long Done { get; set; }
        public long Total { get; set; }
        public double Pct { get; set; }
        public double? EtaSeconds { get; set; }
    }

    public interface ISourceReader : IDisposable
    {
        IReadOnlyList<string> Columns { get; }
        int CheckpointOrdinal { get; }
        Task<List<object[]>> ReadAsync(int maxRows, CancellationToken cancellationToken);
    }

    public interface ISourceFactory
    {
        Task<ISourceReader> OpenAsync(PlanItem item, KeyRange range, string lastValue, int fetchSize, CancellationToken cancellationToken);
    }

    public interface ISourceProbe
    {
        Task<long> CountAsync(PlanItem item, CancellationToken cancellationToken);
        Task<List<KeyRange>> RangesAsync(PlanItem item, int workers, string lastValue, CancellationToken cancellationToken);
        Task<long> ExistingKeyCountAsync(PlanItem item, CancellationToken cancellationToken);
    }

    public interface ITargetSession : IDisposable
    {
        Task<WriteResult> WriteBatchAsync(PlanItem item, IReadOnlyList<WriteColumn> columns, List<object[]> rows, CancellationToken cancellationToken);
        Task SaveCheckpointAsync(CheckpointRecord record, CancellationToken cancellationToken);
        Task CommitAsync();
        Task RollbackAsync();
        Task TruncateAsync(PlanItem item, CancellationToken cancellationToken);
        Task<long> CountExistingKeysAsync(PlanItem item, List<object[]> rows, CancellationToken cancellationToken);
    }

    public interface ITargetFactory
    {
        Task<ITargetSession> OpenAsync(CancellationToken cancellationToken);
    }

    public interface ITargetPreparation
    {
        Task PrepareAsync(RunSpec spec, PlanItem item, CancellationToken cancellationToken);
    }

    public interface ICheckpointStore
    {
        Task<CheckpointRecord> GetAsync(string job, string taskKey, CancellationToken cancellationToken);
        Task<List<CheckpointRecord>> ListAsync(string job, CancellationToken cancellationToken);
        Task SaveLocalAsync(CheckpointRecord record, CancellationToken cancellationToken);
        Task DeleteAsync(string job, string taskKey, CancellationToken cancellationToken);
    }

    public interface IRunClock
    {
        DateTime Now { get; }
    }

    public interface IRunListener
    {
        void OnSnapshot(RunSnapshot snapshot);
        void OnLog(LogEntry entry);
        void OnCheckpoint(CheckpointRecord record);
        void OnEnd(RunSnapshot final);
    }

    public interface IRunRecorder
    {
        Task StartAsync(RunSpec spec, CancellationToken cancellationToken);
        Task TaskStartedAsync(RunSpec spec, PlanItem item, CancellationToken cancellationToken);
        Task TaskEndedAsync(RunSpec spec, TaskSnapshot task, CancellationToken cancellationToken);
        Task EndAsync(RunSpec spec, RunSnapshot snapshot, string message, CancellationToken cancellationToken);
    }

    public sealed class SystemRunClock : IRunClock
    {
        public DateTime Now { get { return DateTime.Now; } }
    }

    public sealed class NullRunListener : IRunListener
    {
        public void OnSnapshot(RunSnapshot snapshot) { }
        public void OnLog(LogEntry entry) { }
        public void OnCheckpoint(CheckpointRecord record) { }
        public void OnEnd(RunSnapshot final) { }
    }

    public sealed class NullRunRecorder : IRunRecorder
    {
        public Task StartAsync(RunSpec spec, CancellationToken cancellationToken) { return Task.CompletedTask; }
        public Task TaskStartedAsync(RunSpec spec, PlanItem item, CancellationToken cancellationToken) { return Task.CompletedTask; }
        public Task TaskEndedAsync(RunSpec spec, TaskSnapshot task, CancellationToken cancellationToken) { return Task.CompletedTask; }
        public Task EndAsync(RunSpec spec, RunSnapshot snapshot, string message, CancellationToken cancellationToken) { return Task.CompletedTask; }
    }

    public sealed class RunMetadata
    {
        public SchemaMetadata Source { get; set; }
        public SchemaMetadata Target { get; set; }
        public Dictionary<string, TableMetadata> SqlSources { get; set; } = new Dictionary<string, TableMetadata>(StringComparer.Ordinal);
    }

    public sealed class ControlStoreUnavailableException : Exception
    {
        public ControlStoreUnavailableException(string message, Exception inner) : base(message, inner) { }
        public ControlStoreUnavailableException(string message) : base(message) { }
    }

    public sealed class UnsupportedColumnTypeException : Exception
    {
        public UnsupportedColumnTypeException(string message) : base(message) { }
    }
}
