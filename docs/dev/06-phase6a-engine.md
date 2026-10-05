# 작업 지시서 P6a — 이관 엔진 코어 (작업id `P6a`)

`docs/dev/00-common.md`를 먼저 읽어라. P1~P5가 끝난 저장소에서 시작한다.
기준: 설계서 README 4.5·6장(파이프라인·병렬·상태·체크포인트·제어 테이블), `UI-MIG-006` 4장(FUNC01~10·SQL-1~5·로그 형식·5장 기타), POC `backend/engine.js`(상태 전이·로그 문구·집계 흉내).
**이 작업은 화면도 하위 프로세스도 없다.** `MigrationStudio.Core.Engine`에 엔진을 만들고, 인터페이스 뒤의 메모리 가짜와 실제 Oracle 두 가지로 시험한다. 에이전트 프로세스·파이프(P6b)와 실행 화면(P6c)이 이 엔진을 그대로 쓴다.

## 1. 범위

| 넣음 | 빼고 나중에 |
|---|---|
| 실행 계획 `RunPlanner`(FK 순서·범위 행 수·재개 위치·작업자 범위), 실행 사양 `RunSpec` | 에이전트 exe·파이프·Job Object·다시 붙기(P6b) |
| 엔진 `MigrationEngine`(상태 기계·파이프라인·병렬 작업자·오류 정책·재시도·Dry Run), 이벤트 `IRunListener` | 실행 화면(P6c) · 실행 후 검증(P6c) |
| 체크포인트 저장소 두 가지(대상 제어 테이블 · 로컬 파일)와 제어 테이블 생성, 오류 테이블 준비 | CDC |
| 읽기·쓰기 인터페이스(`ISourceReader`·`ITargetSession`)와 Oracle 구현, 메모리 가짜 | |
| 시험: 엔진 논리(가짜) + 실제 Oracle 대용량·재개·거부 | |

## 2. 설계 (이 구조를 지켜라 — 바꾸고 싶으면 보고서에 제안)

### 2.1 계층

```
MigrationEngine ──(인터페이스)──▶ ISourceReader · ITargetSession · ICheckpointStore · IRunClock
       │                            ▲Oracle 구현(Core/Adapters/Oracle)   ▲Memory 구현(src/MigrationStudio.Testing)
       └─▶ IRunListener (OnSnapshot · OnLog · OnCheckpoint · OnEnd)   // 호출 스레드 = 엔진 스레드, 구현이 필요하면 마샬링
```
엔진은 Oracle 형식이나 ODP.NET을 직접 모른다. 값은 `object[]`(열 순서 = 쓰기 열 순서) 행으로 오가고, 형식 맞춤은 읽기·쓰기 구현이 한다.

### 2.2 `RunSpec`·`RunPlan` (`Core.Engine`, JSON 직렬화 가능 — P6b가 파이프로 보낸다)
```csharp
public sealed class RunSpec
{
    public string RunId;                  // "R-20261003-144805" (정적 `RunIds.New(DateTime now)`)
    public string RunMode;                // DRY | EXECUTE | RESUME
    public MigrationJob Job;              // 작업 사본(접속 참조 포함, 비밀번호 없음)
    public EndpointSpec Source, Target;   // { ConnectionTarget(비밀번호 포함) · Schema · ProfileName · Color }  — 파이프로만 전달, 파일·로그 금지
    public string CheckpointStore;        // 이미 결정된 값: TARGET | LOCAL (AUTO는 시작 전에 풀어서 보냄)
    public string ControlPrefix;          // MIG_
    public string DataDirectory;          // 로컬 체크포인트·로그 위치
    public List<PlanItem> Plan;           // 실행 순서대로
    public string OnHostExit;             // CONTINUE | STOP (P6b용 — 엔진은 무시)
}
public sealed class PlanItem
{
    public string Key;                    // 매핑 id
    public Mapping Mapping;               // 사본
    public string Label;                  // "SQL SQLMAP_MEMBER → TB_MEMBER"
    public long ScopeTotal;               // 원본 범위 행 수(분모)
    public string ResumeFrom;             // 재개 위치(체크포인트 값 문자열) 또는 null
    public long BaseRows;                 // 재개 시 이미 끝난 행 수
    public List<KeyRange> Ranges;         // 작업자 범위(1개면 단일 작업자). { From(null=처음), To(null=끝), Rows, Last(그 범위의 마지막 체크포인트 값) }
    public long TargetRowsBefore;         // 실행 시작 전 대상 행 수(계획 단계 COUNT — 실행 후 검증이 "이관 밖 행"을 가려내는 데 씀)
    public string ErrorTable;             // CONTINUE 정책일 때만 "스키마.ERR$_…"
}
```
`RunPlanner`(정적 + 비동기 일부):
- `OrderByFk(list, targetMeta)` — POC `orderByFk`·`plannedMappings` 이식(순환 FK는 입력 순서 유지, 100회 가드). 골든 밖 단위 시험.
- `BuildAsync(job, settings, metas, selectedIds, runMode, ISourceProbe probe, ICheckpointStore store, ct)` → `List<PlanItem>`: 선택 매핑만(`use` 무시, 선택이 기준), FK 순서, `ScopeTotal`(테이블: 메타데이터 `Rows`, 정확도가 필요하면 `probe.CountAsync` — 통계가 없거나(null) 0이면 COUNT; SQL: P4 `CountAsync`, 비싸면 추정), 재개면 체크포인트 읽기(`store.GetAsync`)로 `ResumeFrom`·`BaseRows`·`Ranges[].Last`, 작업자 수 > 1이고 체크포인트 열이 숫자·날짜면 NTILE 범위 계산(UI-MIG-006 SQL-1; **이미 저장된 범위가 있으면 새로 나누지 않고 그것을 쓴다**). 체크포인트 열이 없거나 문자 열이면 단일 작업자 + 경고 로그 `[WARN] 작업자 n → 1: 체크포인트 열이 숫자·날짜가 아니라 범위를 나눌 수 없음`.
- 재개 모드인데 체크포인트가 없는 매핑은 처음부터(계획에 `ResumeFrom = null`, 이유 문자열 `Notes`).
- `ISourceProbe`(계획용 읽기): `CountAsync(PlanItem 입력)`, `RangesAsync(...)`, `ExistingKeyCountAsync`(Dry Run용). Oracle 구현은 P4 `QueryAsync`로.

### 2.3 인터페이스 (`Core.Engine`)
```csharp
public interface ISourceReader : IDisposable        // 범위 하나를 읽는 커서
{
    /// 열 이름 목록(원본 SELECT 별칭 = 쓰기 열 이름, 순서 고정)
    IReadOnlyList<string> Columns { get; }
    /// 최대 maxRows행을 읽어 돌려준다. 끝이면 빈 목록. 취소 시 OperationCanceledException.
    Task<List<object[]>> ReadAsync(int maxRows, CancellationToken ct);
}
public interface ISourceFactory { Task<ISourceReader> OpenAsync(PlanItem item, KeyRange range, string lastValue, int fetchSize, CancellationToken ct); }

public interface ITargetSession : IDisposable        // 대상 연결 하나(작업자 하나가 쓴다). 자동 커밋 끔
{
    Task<WriteResult> WriteBatchAsync(PlanItem item, IReadOnlyList<WriteColumn> columns, List<object[]> rows, CancellationToken ct);   // 배열 바인드, 트랜잭션 열린 채로
    Task SaveCheckpointAsync(CheckpointRecord record, CancellationToken ct);                       // TARGET 저장소일 때 같은 트랜잭션에서
    Task CommitAsync();  Task RollbackAsync();
    Task TruncateAsync(PlanItem item, CancellationToken ct);                                        // TRUNCATE_INSERT 시작 때 한 번(DDL — 암묵 커밋)
    Task<long> CountExistingKeysAsync(PlanItem item, List<object[]> rows, CancellationToken ct);    // MERGE 갱신 수·Dry Run용
}
public sealed class WriteResult { public int Written; public int Rejected; public int Updated; public int Inserted; public List<RejectedRow> Rejects; /*Dry·테스트용 샘플 최대 20*/ }
public interface ITargetFactory { Task<ITargetSession> OpenAsync(CancellationToken ct); }

public interface ICheckpointStore { Task<CheckpointRecord> GetAsync(string job, string taskKey, ct); Task<List<CheckpointRecord>> ListAsync(string job, ct); Task SaveLocalAsync(CheckpointRecord r, ct); Task DeleteAsync(string job, string taskKey, ct); }
public sealed class CheckpointRecord { Job, TaskKey, Column, Value, RangeFrom, RangeTo, RowsDone, RowsTotal, Status("running"|"stopped"|"done"), RunId, UpdatedAt }
public interface IRunClock { DateTime Now { get; } }   // 시험이 시간을 제어
public interface IRunListener { void OnSnapshot(RunSnapshot s); void OnLog(LogEntry e); void OnCheckpoint(CheckpointRecord r); void OnEnd(RunSnapshot final); }
```
- `ICheckpointStore`의 TARGET 구현은 읽기·삭제·목록만 한다(저장은 `ITargetSession.SaveCheckpointAsync`가 배치 트랜잭션 안에서). LOCAL 구현은 `DataDirectory\checkpoints\<작업>.json`(작업 이름의 파일명 불가 문자는 `_`)에 **원자적 쓰기**(임시 파일 → 바꿔치기).
- 작업자마다 `ISourceReader` 하나와 `ITargetSession` 하나(접속 2 × 작업자 수).

### 2.4 엔진 (`MigrationEngine`)
```csharp
public sealed class MigrationEngine
{
    public MigrationEngine(RunSpec spec, ISourceFactory source, ITargetFactory target, ICheckpointStore store, IRunListener listener, IRunClock clock);
    public string State { get; }                  // idle · running · pausing · paused · done · stopped · failed (README 6.4와 같은 이름·전이)
    public Task RunAsync(CancellationToken hostCancel);   // 끝까지(또는 중지·실패까지). 예외를 밖으로 던지지 않는다 — 실패는 failed 상태 + [ERROR] 로그 + OnEnd
    public void Pause(); public void Resume(); public void Stop();   // 어느 스레드에서든. Pause = 커밋 경계에서 멈춤, Stop = 진행 중 배치 롤백
    public RunSnapshot Snapshot();
}
```
**동작 규칙(전부 시험 대상)**
1. **순서**: 계획 순서대로 매핑을 하나씩. 한 매핑 안에서만 작업자 병렬. 매핑이 `failed`·`stopped`면 나머지는 `skipped`(실행 안 함 — 상태 문구 `skipped`).
2. **파이프라인(매핑·작업자 하나)**: `ISourceReader.ReadAsync(fetchSize)` → 같은 스레드가 아니라 **`Channel<List<object[]>>`(bounded, 용량 = 2)** 로 쓰기 쪽에 넘김 → 쓰기 쪽은 커밋 크기만큼 모아 `WriteBatchAsync` → (TARGET 저장소면 `SaveCheckpointAsync`) → `CommitAsync` → (LOCAL 저장소면 `SaveLocalAsync` — 커밋 **뒤**) → `OnCheckpoint`. 읽기가 빨라도 쓰기 쪽 버퍼가 차면 읽기가 기다린다(메모리 상한 = (Fetch + 2 × 커밋 크기) × 행 크기 × 작업자).
3. **체크포인트 값**: 쓴 배치의 **마지막 행의 체크포인트 열 값**(원본 SELECT가 `ORDER BY 체크포인트`이므로 단조). 값은 `object`가 아니라 문자열로(숫자 InvariantCulture, 날짜 `yyyy-MM-dd HH:mm:ss.ffffff`). 체크포인트 열이 쓰기 열에 없을 수 있으니(대상에 안 쓰는 원본 키) 엔진은 읽기 쪽에 "체크포인트 값 열"을 따로 요청한다(`ISourceReader.CheckpointOrdinal` — 읽기 열 목록의 마지막에 숨은 열로 붙는 방식 권장; P1 `SqlGenerator.BuildSourceSelect`가 이미 체크포인트 열을 별칭 없이 넣는지 확인하고, 아니면 이 목적의 `WithCheckpointColumn` 옵션을 더하라 — 골든 불변).
4. **작업자 범위 체크포인트**: `TaskKey = <매핑id>#<n>`(단일 작업자는 `<매핑id>`). 표시용 전체 체크포인트 = 모든 범위가 끊김 없이 끝난 위치의 최소 `Last`(범위가 하나라도 시작 전이면 그 범위의 `From`).
5. **쓰기 방식**: `INSERT_ONLY`·`MERGE`는 `SqlGenerator.BuildWriteSql` 그대로. `TRUNCATE_INSERT`는 **새로 시작할 때만** 시작 시 한 번 TRUNCATE(재개면 하지 않음), 이후 INSERT. `DELETE_INSERT`는 배치마다 `DELETE … WHERE 키 = :키`(배열 바인드) 후 INSERT(둘 다 같은 트랜잭션). **INSERT_ONLY + LOCAL 저장소의 재개 첫 배치**는 MERGE로 쓴다(README 6.5 — 되풀이 처리되는 한 배치의 ORA-00001 흡수).
6. **오류 정책** (UI-MIG-006 5장): `CONTINUE` = 쓰기 문에 `LOG ERRORS INTO … ('<RUN_ID>') REJECT LIMIT UNLIMITED`(P1 SqlGenerator가 이미 만든다 — 태그에 RUN_ID가 들어가는지 확인), 거부 = 배치 행 수 − 반영 행 수. `STOP` = 행 오류가 하나라도 있으면 그 배치 롤백 후 `failed`. `RETRY` = 일시 오류만 3회 재시도, 데이터 오류는 즉시 `failed`(문구: "데이터 오류는 재시도로 해결되지 않음"). **일시 오류**(ORA-03113·03114·12170·12571·12537·12543·01033 + `OracleErrors`의 끊김 판정): 진행 중 배치 롤백 → 읽기·쓰기 세션 다시 열기 → 마지막 체크포인트부터 다시 읽기, 3회까지(`[WARN] … 다시 연결해 재시도 n/3`), 모두 실패하면 `failed`. 재시도 간격 1·2·4초(`IRunClock`/`Task.Delay`는 시험에서 줄일 수 있게 옵션 `RetryDelays`).
7. **Pause**: `Pause()` → 상태 `pausing`; 각 작업자가 **다음 커밋 경계**(진행 중 배치를 커밋한 직후)에서 멈추고, 모두 멈추면 `paused` + `[PAUSE] 커밋 경계에서 일시 정지 · 마지막 커밋 …`. 세션·커서는 유지. `Resume()` → `running` + `[RESUME]`. 일시정지가 오래 걸리는 쓰기 중이어도 커밋 후에는 반드시 멈춘다.
8. **Stop**: 취소 토큰으로 읽기·쓰기를 끊고 진행 중 배치 **롤백**(체크포인트도 같이 되돌아감) → TARGET 저장소는 롤백 뒤 별도 트랜잭션으로 `STATUS='stopped'` 표시 → `[STOP] 사용자가 중지 · 진행 중 배치 n행 롤백` + `체크포인트 저장: 열 = 값 — [체크포인트에서 재개]로 이어서 실행` → `stopped`. 중지 요청 후 5초 안에 끝나지 않는 쓰기가 있으면 `ITargetSession.Dispose`로 연결을 끊는다(Oracle은 서버가 롤백).
9. **Dry Run**(`RunMode = DRY`): 읽기·변환·매핑은 그대로, **쓰기 문은 실행하지 않는다**(`WriteBatchAsync`를 부르지 않고 엔진이 직접 집계): 예상 갱신 수 = `CountExistingKeysAsync`(MERGE·DELETE_INSERT), 예상 거부 수 = 엔진이 NOT NULL 열의 null·대상 길이 초과(문자열 길이 > 대상 길이 — 형식에서 읽은 길이)를 센다. 체크포인트·제어 테이블·오류 테이블·TRUNCATE 모두 만들지 않는다. 로그는 `[DRY]` 꼬리표, `예상 Inserted/Updated/Rejected`.
10. **종료**: 모든 매핑이 `done` → `done` + `[DONE] 전체 n개 작업 · N행 · 경과 …`. 작업별 `[DONE]` 로그 형식·`Inserted/Updated/Rejected` 줄은 UI-MIG-006 4.2 그대로(`Rejected : 37 → NEXT_APP.ERR$_TB_MEMBER`). TARGET 저장소는 작업이 끝날 때 체크포인트 `STATUS='done'`. 모든 행이 `Written`이어도 `Rejected`가 있으면 작업 상태는 `done`(거부는 결과 숫자).
11. **로그**: 형식·꼬리표·색 이름은 UI-MIG-006 4.2. `LogEntry { At, Tag(START|INFO|WARN|ERROR|DONE|PAUSE|RESUME|STOP|DRY), Text }`. 로그 파일은 이 엔진이 쓰지 않는다(P6b의 에이전트가 `DataDirectory\logs\<RUN_ID>.log`에 쓴다). `Committed n rows` INFO는 원본 범위의 약 1/8마다(POC `niceStep`).
12. **스냅숏**(`RunSnapshot`): 상태·경과·전체 속도(지수평활 0.7/0.3)·작업별 `{ Key, Label, Status(wait|run|paused|done|stopped|failed|skipped), Total, Read, Written, Pending, Inserted, Updated, Rejected, Commits, Checkpoint, Elapsed, RateNow, Ranges[] }`·파이프라인 지표(버퍼 사용률, 쓰기 중 작업자 수, 단계별 처리량 — 원본 읽기 행/초·커밋 횟수) · `Totals { Done, Total, Pct, EtaSeconds }`. `OnSnapshot`은 **0.25초마다 한 번**(변화가 있을 때만)과 상태 전이 때. 엔진 내부 락은 짧게(스냅숏은 복사본).
13. **예외 정책**: 엔진 코드·구현이 던진 모든 예외는 잡아 `failed`로 끝낸다. 리스너가 던진 예외는 무시(엔진을 멈추지 않음). 비밀번호·연결 문자열이 로그·예외 문장에 들어가지 않는다(시험: 로그 전체에서 비밀번호 문자열 검색).

### 2.5 제어 테이블·오류 테이블 (`Core.Engine.ControlStore`, Oracle 구현)
- `EnsureControlTablesAsync(ITargetSession-ish, schema, prefix)`: README 6.6의 DDL(`RANGE_FROM`·`RANGE_TO` 포함)을 **없으면** 만든다(`ALL_TABLES`로 확인, 접두어는 `[A-Z0-9_$#]{1,10}` 검증). 만들 권한이 없으면 `ControlStoreUnavailableException`(엔진 시작 전 계획 단계에서 `CheckControlStoreAsync`(P2)가 이미 TARGET을 못 고르게 한다 — 사용자가 TARGET을 강제했는데 못 만들면 시작 실패 + 문구 "대상 스키마에 MIG_ 제어 테이블을 만들 권한이 없습니다. 체크포인트 저장소를 '로컬 파일'로 바꾸거나 DBA에게 요청하세요").
- 실행 기록: TARGET이면 `MIG_RUN`·`MIG_RUN_TASK` INSERT/UPDATE(UI-MIG-006 SQL-2·4·5), LOCAL이면 `DataDirectory\runs-history\<RUN_ID>.json`(P6b가 `runs\`와 함께 관리하니 엔진은 `IRunListener`로만 알린다 — 엔진은 MIG_RUN*을 쓰지 않고 `IRunRecorder`(선택 인터페이스, 기본 no-op)를 부른다. TARGET 구현은 이 작업에서 같이 만든다).
- 오류 테이블: CONTINUE 정책 + 비 Dry 실행의 **시작 직전**, 대상 테이블마다 없으면 `DBMS_ERRLOG.CREATE_ERROR_LOG(dml_table_name => …, err_log_table_name => …, err_log_table_owner => 스키마)`. 이름은 `ErrorTableNamer.Resolve(strategy, table)`(P1 `SqlGenerator.ErrorTableFor` 결과가 30자를 넘으면 `ERR$_` + 테이블 앞 25자로 줄이고 충돌하면 `_1`…; 단위 시험). 권한이 없으면 `failed`(시작 실패)가 아니라 실행 시작 전 **검증 C13**이 INFO로 알렸어도 실제로 못 만들면 시작을 막고 문구로 안내.

### 2.6 Oracle 구현 규칙 (`Core/Adapters/Oracle/Engine/*.cs`)
- **읽기**: `OracleCommand`(원본 SELECT, `BindByName`, `InitialLONGFetchSize`), `OracleDataReader.FetchSize = 행 길이 × fetchSize`(행 길이는 메타데이터 `AvgRowLength`, 없으면 100), 세션은 `SET TRANSACTION READ ONLY`. 값은 `GetOracleValue` 대신 형식별: NUMBER → `OracleDecimal`(정밀도 보존), DATE·TIMESTAMP → `DateTime`(TIMESTAMP는 `OracleTimeStamp`로 소수 초 보존), TIMESTAMP WITH TIME ZONE → `OracleTimeStampTZ`, VARCHAR2·CHAR·NCHAR → string, CLOB → string(전체, 큰 값은 `OracleClob.Value`), BLOB·RAW → byte[], 그 밖(LONG·XMLTYPE·객체)은 계획 단계에서 `UnsupportedColumnTypeException`(어느 열·형식인지 한국어 문장).
- **쓰기**: 배열 바인드(`ArrayBindCount = 행 수`), 열마다 `OracleParameter`에 `OracleDbType`을 대상 열 형식에서(VARCHAR2→Varchar2, NUMBER→Decimal/OracleDecimal, DATE→Date, TIMESTAMP→TimeStamp, TIMESTAMP TZ→TimeStampTZ, CLOB→Clob, BLOB→Blob, RAW→Raw)와 크기(문자 열은 대상 길이×4 상한 32767). null은 `DBNull`. `BindByName`. 명령은 **트랜잭션을 명시**(`BeginTransaction`)하고 `WriteBatchAsync`는 커밋하지 않는다. 한 배치 안에서 DML 오류 로깅 문장의 `ExecuteNonQuery` 반환값 = 반영 행 수(`Written`), `Rejected = rows.Count − Written`(MERGE는 소스 행 하나당 최대 1행 반영이라는 가정 — **다르면 보고**). 갱신 수는 `CountExistingKeysAsync`(배치 키로 `SELECT COUNT(*) … WHERE (키…) IN (…)` 1,000개 단위; 복합 키는 `(a, b) IN ((:1,:2), …)`) — MERGE에서만, 쓰기 **전에** 조회.
- 명령 타임아웃 0(무제한, 취소는 `Cancel()`), `UseInBandBreak`(P2) 사용.
- 연결 끊김 감지·`OracleErrors`(P2) 재사용. 풀링 끔.
- 한 매핑 안에서 같은 대상 테이블에 쓰는 작업자 여러 개가 같은 키를 쓸 일은 없다(범위가 겹치지 않음). MERGE ON 키가 체크포인트 열과 다를 때 범위 분할이 키 충돌을 막지 못함 → 병합 키 ≠ 체크포인트 열이면 **작업자를 1로 낮추고 경고 로그**.

### 2.7 시험 지원 (`src/MigrationStudio.Testing`, P4 FakeAdapter와 같은 프로젝트)
`MemoryTable`(열·PK·행), `MemorySourceFactory`(테이블 또는 SQL 결과를 키 순서로 읽음, 범위·`LAST_ID` 지원, **N번째 읽기에서 예외 주입**), `MemoryTargetFactory`(`ITargetSession` 구현: INSERT/MERGE/DELETE_INSERT/TRUNCATE 의미, PK 중복·NOT NULL·길이 초과 → 거부 규칙, **트랜잭션**(커밋 전 변경은 롤백 가능), `SaveCheckpointAsync`가 같은 트랜잭션, **쓰기 후 커밋 전·커밋 후 체크포인트 전 지점에서 충돌 주입**), `MemoryCheckpointStore`, `ManualClock`. 모두 결정적(난수 없음).

## 3. 시험

**엔진 논리 — 가짜로(Oracle 없이), 빠르게**: ① 전체 복사(INSERT_ONLY·MERGE·TRUNCATE_INSERT·DELETE_INSERT) 결과 = 원본 ② MERGE 두 번 실행해도 결과 같음, Updated 수 ③ 거부 규칙(NOT NULL·길이·중복 키) + 정책 CONTINUE/STOP/RETRY ④ 일시 오류 주입 → 재시도 1회로 성공, 3회 초과 시 `failed` ⑤ Pause → Resume → 같은 결과, Pause 중 스냅숏 `paused`, 커밋 경계(진행 중 배치가 반영된 뒤 멈춤) ⑥ Stop 중 쓰기 → 롤백, 체크포인트 = 마지막 커밋 키, 이어서 재개하면 원본 전체와 같음(TARGET·LOCAL 둘 다) ⑦ **충돌 주입**: TARGET 저장소는 "쓰기 후 커밋 전" 충돌 시 재개가 중복·누락 없이 이어짐(exactly-once), LOCAL은 "커밋 후 체크포인트 전" 충돌 시 마지막 배치가 한 번 더 처리되고 INSERT_ONLY여도 오류 없이 결과가 같음(재개 첫 배치 MERGE) ⑧ 병렬 작업자 4: 범위 합 = 전체, 범위별 체크포인트, 한 범위만 중간에 죽이고 재개 → 같은 범위로 이어서 완성 ⑨ 병합 키 ≠ 체크포인트 열 → 작업자 1 + 경고 ⑩ Dry Run: 대상 불변, 예상 수 ⑪ FK 순서(부모 → 자식)·순환 ⑫ 로그 문구(START·DONE 형식은 UI-MIG-006 4.2와 글자 단위로 비교) ⑬ 스냅숏 Totals·ETA ⑭ **비밀번호가 로그·예외에 없음** ⑮ 리스너 예외가 엔진을 안 멈춤 ⑯ 메모리 상한(큰 원본 100만 행을 읽는 동안 쓰기 쪽 버퍼 용량 초과 안 함 — 읽기 호출 수 − 쓰기 호출 수 ≤ 용량).

**실제 Oracle(OracleIT)** — `MIG_IT_SRC` → `MIG_IT_TGT`(P2 준비물, 이 작업에서 큰 표 `BIG_SRC` 200,000행을 SRC 쪽에 만든다 — 다양한 형식: NUMBER(18)·VARCHAR2(60)·DATE·TIMESTAMP(6)·NUMBER(12,2)·CLOB 작은 값·NULL 섞임):
1. 200,000행 INSERT_ONLY 복사 → 대상 행 수·체크섬(`SUM(ORA_HASH(열들))`)이 원본과 같음, **처리 속도(행/초)와 걸린 시간을 보고**
2. 같은 복사를 MERGE로 다시 → 변화 없음, Updated = 200,000
3. 커밋 크기 10,000 / 작업자 4 — 결과 같음, 접속 수 확인
4. **중간 중지 후 재개**: 80,000행쯤에서 `Stop()` → 대상 행 수 = 체크포인트 값과 일치(커밋된 것만), 재개 → 최종 체크섬 같음(TARGET 저장소와 LOCAL 저장소 각각)
5. 거부: 일부러 대상 열을 짧게(VARCHAR2(20))·NOT NULL로 만든 `TGT_STRICT`에 복사 → CONTINUE 정책에서 `Rejected`가 SQL로 센 값(`ERR$_` 테이블의 `ORA_ERR_TAG$ = RUN_ID` 개수)과 일치, STOP 정책이면 실패하고 체크포인트까지만
6. SQL 원본(JOIN) → 대상, `:LAST_ID` 바인드 재개
7. 일시정지·이어서, Dry Run(대상 불변·예상 수가 실제 실행 결과 수와 같음)
8. `MIG_IT_RO`(CREATE TABLE 권한 없음) 대상으로 AUTO → LOCAL 저장소로 실행되고 성공
9. 연결 끊김: 쓰기 중 대상 세션을 `ALTER SYSTEM KILL SESSION`(SYSTEM 계정으로)으로 죽이고 → 재시도로 완주(ORA-03113/00028 계열 — 어떤 코드가 왔는지 보고)

## 4. 완료 기준
```powershell
dotnet build MigrationStudio.sln -c Release --nologo --no-incremental     # 경고 0
dotnet test tests/MigrationStudio.Tests -c Release --nologo                # 실패 0 (골든 + 엔진 논리)
$env:ORACLE_IT_DSN = "localhost:1521/xe"; dotnet test tests/MigrationStudio.OracleIT -c Release --nologo
dotnet publish src/MigrationStudio -c Release --nologo                     # zip: MigrationStudio.Testing.dll 없음, agent\에 엔진이 든 Core.dll
```
보고서 `docs/dev/reports/P6a-report.md`: 실측 수치(속도·메모리 — `GC.GetTotalMemory`/프로세스 작업 집합 최대값), 시험 결과 표, 설계와 다르게 한 것·설계 공백(MERGE 거부 계산 가정, Updated 조회 비용 등), 알려진 한계.
