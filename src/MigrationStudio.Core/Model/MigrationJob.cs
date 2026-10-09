using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MigrationStudio.Core.Model
{
    /// <summary>
    /// 이관 작업 하나 = 접속 2개(마이그레이션 설정의 접속 참조) + 전략 + 매핑 N + 체크포인트.
    /// 작업 파일(JSON, <see cref="Jobs.JobFile"/>)의 모양 그대로다. 비밀번호는 들어가지 않는다.
    /// </summary>
    public sealed class MigrationJob
    {
        public const string Format = "folderss-migration-job";
        public const int CurrentVersion = 2;

        [JsonPropertyName("format")]
        public string FormatName { get; set; } = Format;
        public int Version { get; set; } = CurrentVersion;
        public string JobName { get; set; } = "NEW_MIGRATION";
        public string Description { get; set; } = "";
        public ConnectionRef Source { get; set; } = new ConnectionRef();
        public ConnectionRef Target { get; set; } = new ConnectionRef();
        public MigrationStrategy Strategy { get; set; } = new MigrationStrategy();
        public List<Mapping> Mappings { get; set; } = new List<Mapping>();

        /// <summary>매핑 id → 마지막 체크포인트 사본(기준 저장소는 대상 MIG_CHECKPOINT 또는 로컬 파일).</summary>
        public Dictionary<string, CheckpointInfo> Checkpoints { get; set; } = new Dictionary<string, CheckpointInfo>(StringComparer.Ordinal);

        public Mapping FindMapping(string id)
        {
            return Mappings.Find(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 작업이 쓰는 접속: 마이그레이션 설정의 접속 id와 이 작업의 스키마.
    /// 이름·주소는 다른 PC에서 작업을 열 때 이름으로 설정의 접속을 찾기 위한 사본이다(비밀번호 없음).
    /// </summary>
    public sealed class ConnectionRef
    {
        public string ProfileId { get; set; }
        public string Schema { get; set; } = "";
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Host { get; set; }
        public string Port { get; set; }
        public string Service { get; set; }
        public string User { get; set; }
        public string Color { get; set; }
    }

    public sealed class MigrationStrategy
    {
        public string Mode { get; set; } = ExecutionModes.Full;
        public string IncrementalBy { get; set; } = "PK";
        public int CommitSize { get; set; } = 10000;
        public int FetchSize { get; set; } = 5000;
        public string ErrorPolicy { get; set; } = ErrorPolicies.Continue;
        /// <summary>오류 테이블 이름 또는 접두어(끝이 _면 접두어: ERR$_ → ERR$_TB_MEMBER).</summary>
        public string ErrorTable { get; set; } = "ERR$_";
        public int Workers { get; set; } = 4;

        /// <summary>CDC(변경동기화)에서 주기 사이 대기 초. 주기마다 워터마크 이후 행을 읽어 MERGE한다.</summary>
        public int PollIntervalSeconds { get; set; } = 60;

        /// <summary>CDC(변경동기화) 최대 실행 시간(시간). 0이면 무기한 — 사용자가 명시적으로 골랐을 때만.</summary>
        public int MaxRunHours { get; set; } = 24;
    }

    /// <summary>
    /// 매핑 하나: 원본(테이블 또는 SQL) → 대상 테이블. 원본이 SQL이면 결과 별칭이 원본 컬럼이다.
    /// </summary>
    public sealed class Mapping
    {
        public string Id { get; set; }
        public bool Use { get; set; } = true;
        public string SourceType { get; set; } = SourceTypes.Table;

        /// <summary>원본 테이블 이름, 또는 SQL 원본의 이름(목록·로그·체크포인트에 씀).</summary>
        public string Source { get; set; } = "";

        /// <summary>SQL 원본의 SELECT 문(테이블 원본이면 null).</summary>
        public string Sql { get; set; }

        public List<BindParameter> Binds { get; set; } = new List<BindParameter>();
        public string Target { get; set; } = "";
        public string Mode { get; set; } = WriteModes.InsertOnly;
        public List<string> MergeKey { get; set; } = new List<string>();

        /// <summary>읽는 순서이자 재개 기준 열(테이블 원본은 원본 컬럼, SQL 원본은 결과 열). 없으면 재개 불가.</summary>
        public string CheckpointColumn { get; set; }

        /// <summary>원본 조건(WHERE 없이). SQL 원본이면 감싼 결과(S)에 건다.</summary>
        public string Where { get; set; } = "";

        /// <summary>SQL 원본만: null이면 작업 전략 값.</summary>
        public int? FetchSize { get; set; }
        public int? CommitSize { get; set; }

        public List<ColumnMapping> Columns { get; set; } = new List<ColumnMapping>();

        [JsonIgnore]
        public bool IsSql
        {
            get { return string.Equals(SourceType, SourceTypes.Sql, StringComparison.Ordinal); }
        }

        /// <summary>목록·로그에 쓰는 이름: "SRC_CUSTOMER → TB_MEMBER", "SQL SQLMAP_MEMBER → TB_MEMBER".</summary>
        [JsonIgnore]
        public string Label
        {
            get { return (IsSql ? "SQL " : "") + Source + " → " + (string.IsNullOrEmpty(Target) ? "?" : Target); }
        }

        public ColumnMapping FindColumn(string target)
        {
            return Columns.Find(c => string.Equals(c.Target, target, StringComparison.Ordinal));
        }

        public static string NewId()
        {
            return "tm-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        }
    }

    /// <summary>대상 컬럼 하나의 값: 원본 컬럼(또는 별칭), 변환식, NULL 처리.</summary>
    public sealed class ColumnMapping
    {
        public string Target { get; set; }
        public string Source { get; set; }
        /// <summary>변환식(Oracle 식). 비우면 원본 컬럼 값을 그대로 쓴다.</summary>
        public string Expr { get; set; } = "";
        public string NullRule { get; set; } = NullRules.Allow;
        /// <summary>NULL 처리가 기본값이면 값, 사용자 식이면 식.</summary>
        public string DefaultValue { get; set; } = "";
    }

    public sealed class BindParameter
    {
        public string Name { get; set; }
        /// <summary>NUMBER · VARCHAR2 · DATE</summary>
        public string Type { get; set; } = "VARCHAR2";
        public string Value { get; set; } = "";
        /// <summary>재개할 때 이 변수에 체크포인트 값을 넣는다.</summary>
        public bool FromCheckpoint { get; set; }
    }

    /// <summary>마지막으로 커밋된 배치의 체크포인트 키(재개하면 그 다음부터 읽는다).</summary>
    public sealed class CheckpointInfo
    {
        public string Column { get; set; }
        public string Value { get; set; }
        public long Rows { get; set; }
        public long Total { get; set; }
        public string At { get; set; }
        public string RunId { get; set; }
        /// <summary>running · stopped · done</summary>
        public string Status { get; set; }
    }
}
