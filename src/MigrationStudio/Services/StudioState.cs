using System;
using System.Collections.Generic;
using System.Linq;
using MigrationStudio.Core.Adapters;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Sql;
using MigrationStudio.Logic;

namespace MigrationStudio.Services
{
    public enum ChangeScope
    {
        Job,
        Session,
        Settings
    }

    public sealed class ConnectionRoleState
    {
        public string Status { get; set; } = ConnStatus.Unknown;
        public ConnectionTestResult Result { get; set; }
        public bool MetaLoading { get; set; }
        public SchemaMetadata Metadata { get; set; }
        public string MetaError { get; set; }
    }

    public sealed class StudioUiState
    {
        public string SelMapping { get; set; }
        public string SelColumn { get; set; }
        public string ColFilter { get; set; } = "all";
        public string SqlTab { get; set; } = "check";
        public string ColSqlTab { get; set; } = "select";
        public string MappingFilter { get; set; } = "";
        public string ValTab { get; set; } = "pre";
        public string ValFilter { get; set; } = "all";
        public string RunMode { get; set; } = "EXECUTE";
        public HashSet<string> RunSelected { get; set; }

        /// <summary>실행 화면이 한 번이라도 본 매핑 id — 처음 보는 매핑은 기본으로 고르기 위해(RunLogic.SyncSelection).</summary>
        public HashSet<string> RunKnown { get; set; }
        public string LogFilter { get; set; } = "all";

        /// <summary>왼쪽 단계 막대 너비(px) — 본문과의 분할선을 끌어 바꾼다. 좁은 창에서 접힐 때는 이 값과 무관하게 52px.</summary>
        public double RailWidth { get; set; } = 208;

        // 분할 패널(SplitPane) 위쪽 비율 — 끌어서 바꾼 값을 화면을 다시 그려도 유지
        public double RunSplit { get; set; } = 0.55;
        public double TablesSplit { get; set; } = 0.6;
        public double SqlSplit { get; set; } = 0.5;
    }

    /// <summary>실행 전 검증 한 번의 결과(창마다 하나). JobVersion은 시작 때의 작업 버전.</summary>
    public sealed class PreValidationSession
    {
        public List<MigrationStudio.Core.Validation.ValidationItem> Items { get; set; } = new List<MigrationStudio.Core.Validation.ValidationItem>();
        public bool Running { get; set; }
        public DateTime At { get; set; }
        public long ElapsedMs { get; set; }
        public int JobVersion { get; set; }
        public string Error { get; set; }
    }

    /// <summary>실행 후 검증 한 번의 결과. RunId가 지금 실행과 다르면 화면은 숨긴다.</summary>
    public sealed class PostValidationSession
    {
        public string RunId { get; set; }
        public List<MigrationStudio.Core.Validation.PostItem> Items { get; set; } = new List<MigrationStudio.Core.Validation.PostItem>();
        public bool Running { get; set; }
        public DateTime At { get; set; }
        public string Error { get; set; }
    }

    public sealed class SqlCheckSession
    {
        public SqlValidationResult Result { get; set; }
        public DateTime At { get; set; }
        public string Sql { get; set; }
    }

    public sealed class SqlPreviewSession
    {
        public QueryResult Result { get; set; }
        public DateTime At { get; set; }
        public string Sql { get; set; }
        public string Error { get; set; }
    }

    public sealed class SqlDescribeSession
    {
        public List<QueryColumn> Columns { get; set; }
        public string Sql { get; set; }
    }

    internal sealed class StudioState
    {
        public MigrationJob Job { get; private set; }
        public string FilePath { get; set; }
        public bool Dirty { get; private set; }
        public MigrationSettings Settings { get; private set; }
        public string SettingsRevision { get; private set; }
        public string SettingsLoadError { get; private set; }
        public bool IsRunning { get; set; }

        /// <summary>작업이 바뀔 때마다 늘어난다. 검증 결과가 만들어진 뒤 바뀌었는지(Stale) 판단하는 데 쓴다.</summary>
        public int JobVersion { get; private set; }

        public PreValidationSession Pre { get; set; }

        public PostValidationSession Post { get; set; }

        /// <summary>이번 창이 보고 있는 실행(없으면 null). 에이전트 이벤트가 쌓인다.</summary>
        public RunView Run { get; set; }

        public readonly StudioUiState Ui = new StudioUiState();
        public readonly Dictionary<string, SqlCheckSession> SqlCheck = new Dictionary<string, SqlCheckSession>(StringComparer.Ordinal);
        public readonly Dictionary<string, SqlPreviewSession> SqlPreview = new Dictionary<string, SqlPreviewSession>(StringComparer.Ordinal);
        public readonly Dictionary<string, SqlDescribeSession> SqlDescribe = new Dictionary<string, SqlDescribeSession>(StringComparer.Ordinal);

        public readonly Dictionary<string, ConnectionRoleState> Conn = new Dictionary<string, ConnectionRoleState>(StringComparer.Ordinal)
        {
            { Roles.Source, new ConnectionRoleState() },
            { Roles.Target, new ConnectionRoleState() }
        };

        public event Action<ChangeScope> Changed;

        public int CurrentStep { get; set; }

        public StudioState(MigrationJob job)
        {
            Job = job ?? JobLogic.NewJob(null);
        }

        public void ReloadSettings(LoadedSettings loaded)
        {
            Settings = loaded != null ? loaded.Settings : MigrationSettingsStore.CreateDefaultSettings();
            SettingsRevision = loaded != null ? loaded.Revision : "";
            SettingsLoadError = loaded != null ? loaded.Error : null;
            ResetConnectionTests();
            Raise(ChangeScope.Settings);
        }

        public void ResetConnectionTests()
        {
            foreach (var pair in Conn)
            {
                pair.Value.Status = ConnStatus.Unknown;
                pair.Value.Result = null;
                pair.Value.MetaError = null;
            }

            Raise(ChangeScope.Session);
        }

        public void MarkChanged()
        {
            MarkChanged(true);
        }

        /// <param name="affectsValidation">
        /// false면 저장은 필요하지만(Dirty) 검증 결과는 그대로 유효한 변경 — 실행이 남기는 체크포인트 갱신 등.
        /// 이것까지 JobVersion을 올리면 이관을 한 번 돌릴 때마다 "검증 뒤 작업이 바뀜"으로 다시 묻는다.
        /// </param>
        public void MarkChanged(bool affectsValidation)
        {
            if (affectsValidation)
            {
                JobVersion++;
            }

            Dirty = true;
            Raise(ChangeScope.Job);
            DraftAutosave.Schedule(this);
        }

        public void MarkUiChanged()
        {
            Raise(ChangeScope.Session);
        }

        public void SetClean()
        {
            Dirty = false;
            Raise(ChangeScope.Job);
        }

        public void ReplaceJob(MigrationJob job, string filePath, bool dirty)
        {
            Job = job ?? JobLogic.NewJob(Settings);
            FilePath = filePath;
            Dirty = dirty;
            ClearSession();
            Ui.SelMapping = null;
            Ui.SelColumn = null;
            Raise(ChangeScope.Job);
            Raise(ChangeScope.Session);
        }

        public void ClearSessionForRole(string role)
        {
            ConnectionRoleState state;
            if (!Conn.TryGetValue(role, out state))
            {
                return;
            }

            state.Status = ConnStatus.Unknown;
            state.Result = null;
            state.Metadata = null;
            state.MetaError = null;
            state.MetaLoading = false;
            Raise(ChangeScope.Session);
        }

        private void ClearSession()
        {
            foreach (var pair in Conn)
            {
                pair.Value.Status = ConnStatus.Unknown;
                pair.Value.Result = null;
                pair.Value.Metadata = null;
                pair.Value.MetaError = null;
                pair.Value.MetaLoading = false;
            }

            SqlCheck.Clear();
            SqlPreview.Clear();
            SqlDescribe.Clear();
            Pre = null;
            Post = null;
        }

        public void RemoveMappingSession(string mappingId)
        {
            if (string.IsNullOrEmpty(mappingId))
            {
                return;
            }

            SqlCheck.Remove(mappingId);
            SqlPreview.Remove(mappingId);
            SqlDescribe.Remove(mappingId);
            if (Job != null && Job.Checkpoints != null)
            {
                Job.Checkpoints.Remove(mappingId);
            }
        }

        public ConnectionProfile ProfileForRole(string role)
        {
            if (Settings == null || Job == null)
            {
                return null;
            }

            var id = role == Roles.Source
                ? Job.Source != null ? Job.Source.ProfileId : null
                : Job.Target != null ? Job.Target.ProfileId : null;
            return string.IsNullOrEmpty(id) ? null : MigrationSettingsStore.Find(Settings, id);
        }

        public SchemaMetadata SourceMeta()
        {
            ConnectionRoleState s;
            return Conn.TryGetValue(Roles.Source, out s) ? s.Metadata : null;
        }

        public SchemaMetadata TargetMeta()
        {
            ConnectionRoleState s;
            return Conn.TryGetValue(Roles.Target, out s) ? s.Metadata : null;
        }

        public TableMetadata SourceOf(Mapping mapping)
        {
            if (mapping == null || Job == null)
            {
                return null;
            }

            var sourceMeta = SourceMeta();
            if (!mapping.IsSql)
            {
                if (sourceMeta == null || string.IsNullOrEmpty(mapping.Source))
                {
                    return null;
                }

                return sourceMeta.FindTable(mapping.Source);
            }

            SqlDescribeSession describe;
            IList<QueryColumn> described = null;
            if (SqlDescribe.TryGetValue(mapping.Id, out describe))
            {
                described = describe.Columns;
            }

            var info = SqlSourceAnalyzer.Analyze(mapping, sourceMeta, described);
            return info != null ? info.Table : null;
        }

        public TableMetadata TargetTable(string name)
        {
            var meta = TargetMeta();
            if (meta == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            return meta.FindTable(name);
        }

        public Mapping Mapping(string id)
        {
            if (Job == null || Job.Mappings == null || string.IsNullOrEmpty(id))
            {
                return null;
            }

            return Job.Mappings.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        }

        public List<Mapping> SqlMappings()
        {
            if (Job == null || Job.Mappings == null)
            {
                return new List<Mapping>();
            }

            return Job.Mappings.Where(m => m.IsSql).ToList();
        }

        public void Notify(ChangeScope scope)
        {
            var handler = Changed;
            if (handler != null)
            {
                handler(scope);
            }
        }

        private void Raise(ChangeScope scope)
        {
            Notify(scope);
        }
    }
}
