# 작업 지시서 P2 — Oracle 어댑터 · 메타데이터 · 캐시 (작업id `P2`)

`docs/dev/00-common.md`를 먼저 읽어라. P1(Core 이식)이 끝난 저장소에서 시작한다. 이 작업도 **화면 없이** Core만 다룬다(화면은 P3).
화면 설계서: `UI-MIG-001`(접속 · FUNC02~07, SQL-0~3), `UI-MIG-008`(FUNC03 접속 테스트, FUNC05 제어 테이블 권한). README 4.3(서비스 표)·7.3(안전장치).

## 0. 설계자가 미리 해 둔 것

- `MigrationStudio.Core.csproj`에 `Oracle.ManagedDataAccess.Core 23.26.301`(DB Helper와 같은 판) 추가.
- 새 프로젝트 `tests/MigrationStudio.OracleIT`(xunit, net8.0, Core 참조) — 솔루션에 등록됨, `Placeholder.cs`는 지워라. 실제 Oracle에 붙는 시험만 둔다. Core에 `System.Security.Cryptography.ProtectedData`도 추가해 뒀다(P3 DPAPI용).
- `OracleType`이 `TIMESTAMP(6) WITH TIME ZONE`·`TIMESTAMP WITH LOCAL TIME ZONE`을 읽도록 고침(`TimeZone` 속성, Base = TIMESTAMP). `INTERVAL …`은 지금처럼 원문 그대로 Base에 둔다.

- 지금 `dotnet build --no-incremental`에 xUnit 분석기 경고 8개(xUnit2020 `Assert.True(false, …)` → `Assert.Fail`, xUnit2000 인수 순서)가 있다. **이 작업에서 0으로 만들어라**(테스트를 약하게 하지 말고 경고만 고침). 보고서의 경고 수는 반드시 `--no-incremental`로 잰 값.

## 1. 범위

| 넣음 | 빼고 나중에 |
|---|---|
| 어댑터 경계(`IDatabaseAdapter`)와 Oracle 구현: 접속 테스트, 메타데이터 읽기, 문자 집합, 제어 테이블 권한 확인 | SELECT 결과 열 조회(DESCRIBE)·미리보기(P4), 열 실측(P5), 스트리밍 읽기·배치 쓰기(P6) |
| Oracle 오류 문장 만들기(DB Helper `DbSession.DescribeError` 규칙) | |
| 메타데이터 캐시 파일, 작업 임시 저장 파일 | |
| 실제 Oracle 시험(`MigrationStudio.OracleIT`) | |

## 2. 공개 API — `MigrationStudio.Core.Adapters`

```csharp
/// 연결에 필요한 값. 비밀번호 평문은 메모리에만 — 파일·로그·예외 메시지에 넣지 않는다.
public sealed class ConnectionTarget
{
    public string Kind { get; set; } = "oracle";
    public string Host { get; set; }
    public int Port { get; set; } = 1521;
    public string Service { get; set; }
    public string User { get; set; }
    public string Password { get; set; }
    public static ConnectionTarget From(ConnectionProfile profile, string password);
    public string Describe();          // "LEGACY_APP@10.10.10.21:1521/LEGACY" (비밀번호 없음)
    public override string ToString(); // = Describe()
}

public sealed class NlsInfo { public string CharacterSet; public string NCharCharacterSet; public string LengthSemantics; }   // (속성으로)

public sealed class ConnectionTestResult
{
    public bool Ok;
    public string Version;        // "Oracle 19c" — OracleVersionText
    public string Banner;         // V$VERSION 첫 줄(권한 없으면 null)
    public string ServerVersion;  // "19.0.0.0.0"(OracleConnection.ServerVersion)
    public string DbName; public string ServiceName; public string CurrentSchema;
    public int? LatencyMs;        // SELECT 1 FROM DUAL 3번의 중앙값(반올림 ms)
    public NlsInfo Nls;
    public DateTime TestedAt;     // 현지 시각
    public string Error;          // 실패 문장(OracleErrors.Describe) — Ok면 null
    public string ErrorCode;      // "ORA-01017" 등 — 모르면 null
}

public sealed class ControlStoreCheck
{
    public bool TablesExist;      // <prefix>CHECKPOINT가 대상 스키마에 있음
    public bool CanCreate;        // 대상 스키마가 접속 사용자 자신이고 CREATE TABLE이 있음, 또는 CREATE ANY TABLE
    public string Resolved;       // CheckpointStores.Target 또는 Local (AUTO일 때 고른 값)
    public string Reason;         // 사람이 읽을 한 줄 — "MIG_CHECKPOINT가 있어 대상 DB에 저장합니다" 등
}

public sealed class AdapterKind { public string Value; public string Label; public bool Planned; }

public interface IDatabaseAdapter
{
    string Kind { get; }
    string Title { get; }
    /// 접속 시험. 연결·인증·권한 실패는 예외가 아니라 Ok=false로 돌려준다. 취소면 OperationCanceledException.
    Task<ConnectionTestResult> TestAsync(ConnectionTarget target, bool readOnly, CancellationToken cancellationToken);
    /// 스키마 메타데이터(테이블·뷰·컬럼·PK·FK·통계·테이블스페이스). schema가 비면 접속 사용자. 실패는 AdapterException.
    Task<SchemaMetadata> LoadMetadataAsync(ConnectionTarget target, string schema, CancellationToken cancellationToken);
    /// 체크포인트 저장소 결정(UI-MIG-008 FUNC05). store가 TARGET·LOCAL이면 확인만 하고 그대로 돌려준다.
    Task<ControlStoreCheck> CheckControlStoreAsync(ConnectionTarget target, string schema, string prefix, string store, CancellationToken cancellationToken);
}

public sealed class AdapterException : Exception
{
    public string Code { get; }       // "ORA-00942" 등, 모르면 null
    // Message = OracleErrors.Describe(inner) — 비밀번호가 들어가지 않게
}

public static class DatabaseAdapters
{
    public static IReadOnlyList<AdapterKind> Kinds { get; }    // oracle(Oracle) · postgresql · sqlserver · mysql · mariadb(라벨 뒤 " (예정)", Planned=true) — POC adapters.js KINDS와 같은 순서·라벨
    public static IDatabaseAdapter For(string kind);           // null·빈 값 = oracle. 예정 종류는 NotSupportedException("PostgreSQL 어댑터는 아직 지원하지 않습니다")
}
```

### 2.1 Oracle 구현 (`Adapters/Oracle/*.cs` — 클래스 이름은 자유, 위 계약만 지킨다)

**연결**
- 연결 문자열은 `OracleConnectionStringBuilder`로만 만든다(`DataSource = host:port/service`, `Pooling = false`, `ConnectionTimeout = 15`). 문자열 이어 붙이기 금지 — 비밀번호의 `;`·`=`·`'`가 다른 키로 해석되지 않게.
- 첫 연결 전에 한 번 `OracleConfiguration.DisableOOB = true`(DB Helper `DbSession.UseInBandBreak`와 같은 이유·같은 방식, 주석도 옮겨라). 취소는 `cancellationToken.Register(command.Cancel)`.
- DB 호출은 `Task.Run` 안에서 동기 ADO.NET(공급자 async가 동기로 돌아도 호출 스레드를 막지 않게). 모든 명령 `BindByName = true`.
- `ConnectionTarget` 검사: 호스트·서비스·사용자 비었거나 포트가 1–65535 밖이면 연결하지 않고 `Ok=false`, Error는 `MigrationSettingsStore.ValidateProfile`과 같은 문구. 비밀번호가 비었으면 "비밀번호를 입력하세요".

**TestAsync** — UI-MIG-001 SQL-0 순서대로:
1. 연결 열기 → `ServerVersion`
2. `SELECT BANNER FROM V$VERSION WHERE ROWNUM = 1` — ORA-00942(권한 없음)면 Banner = null로 두고 계속
3. `SYS_CONTEXT('USERENV', 'DB_NAME' | 'SERVICE_NAME' | 'CURRENT_SCHEMA')`
4. `NLS_DATABASE_PARAMETERS`(UI-MIG-001 FUNC06 SQL)
5. `SELECT 1 FROM DUAL` 3번 → 중앙값
6. readOnly면 `SET TRANSACTION READ ONLY` 실행이 성공하는지 확인하고 롤백
- `Version` = `OracleVersionText.FromServerVersion(ServerVersion)` (공개 정적 도우미, 단위 시험 대상): 주 버전 11 → "Oracle 11g", 12 → "Oracle 12c", 18·19·21 → "Oracle 18c"·"19c"·"21c", 23 이상 → "Oracle 23ai", 해석 못 하면 "Oracle " + 원문.

**LoadMetadataAsync** — UI-MIG-001 SQL-1·2·3을 그대로 쓰고(바인드 `:OWNER`), 통계·테이블스페이스를 더한다:
- 스키마: Trim + 대문자. 비면 접속 사용자. 그 스키마의 객체가 하나도 안 보이면 예외가 아니라 빈 목록(권한 없는 스키마는 `ALL_*`에 안 보임 — 화면이 "테이블 0" 표시).
- 열 형식 문자열(`ColumnMetadata.Type`) — `OracleType.Parse`로 되읽을 수 있게:

| DATA_TYPE | 표시 |
|---|---|
| VARCHAR2·CHAR | `CHAR_USED = 'C'`면 `VARCHAR2(30 CHAR)`, 아니면 `VARCHAR2(30)` (길이 = CHAR_LENGTH) |
| NVARCHAR2·NCHAR | `NVARCHAR2(30)` (CHAR_LENGTH, 단위 생략) |
| NUMBER | 정밀도·스케일 없음 → `NUMBER` · 정밀도 없고 스케일 0 → `NUMBER(38)` · 스케일 0 → `NUMBER(p)` · 그 밖 → `NUMBER(p,s)` |
| FLOAT | `FLOAT(p)` |
| RAW | `RAW(DATA_LENGTH)` |
| 그 밖(DATE, TIMESTAMP(6), TIMESTAMP(6) WITH TIME ZONE, CLOB, BLOB, LONG, ROWID …) | DATA_TYPE 그대로 |

- `DATA_DEFAULT`는 LONG이다 → 명령에 `InitialLONGFetchSize = 4000`. 끝의 공백·줄바꿈을 Trim, 비면 null.
- PK 열은 `PrimaryKey = true`, FK는 `ForeignKeys`(제약 이름·열 순서대로·참조 테이블). UK는 지금 쓰지 않는다(읽기만 하고 버려도 됨).
- 통계: `SELECT TABLE_NAME, COLUMN_NAME, NUM_NULLS, NUM_DISTINCT FROM ALL_TAB_COL_STATISTICS WHERE OWNER = :OWNER` → `Stats.Nulls`·`Distinct`(통계가 없으면 null). MaxLength 등 실측 값은 P5.
- 테이블스페이스(`SchemaMetadata.Tablespace`) — 스키마가 접속 사용자일 때만: `USER_USERS.DEFAULT_TABLESPACE`, 여유 `USER_FREE_SPACE` 합(GB, 소수 1자리), 할당량 `USER_TS_QUOTAS`(MAX_BYTES = -1이면 무제한 → QuotaLeftGb = null). 권한·뷰 오류는 무시하고 null.
- `LoadedAt` = 현지 시각, `ElapsedMs` = 전체 걸린 시간, `Cached = false`. 테이블은 이름순, 열은 COLUMN_ID 순.
- 수천 개 테이블도 한 번에 읽는다(질의 4~5개, 테이블마다 질의하지 않는다).

**CheckControlStoreAsync** — UI-MIG-008 FUNC05 SQL. `prefix`는 대문자. 규칙: TARGET·LOCAL이면 확인 결과만 채우고 Resolved = 그 값. AUTO면 `TablesExist || CanCreate` → TARGET, 아니면 LOCAL. Reason 문구는 네가 정하되 한국어 한 줄.

### 2.2 오류 문장 — `OracleErrors`(공개 정적)

- `Describe(Exception)`: DB Helper `DbSession.DescribeError`·`IsCancellation`·`Unwrap`·`WithInnerCause`를 옮긴다(규칙·주석 그대로). 취소 → "실행을 취소했습니다.", 끊김 → "DB 연결이 끊겼습니다. 다시 연결하세요. (ORA-03113)", 그 밖 첫 줄. 안쪽 원인 우선(ORA-12514 … (ORA-50201)).
- `CodeOf(Exception)` → "ORA-01017" 등.
- 비밀번호가 메시지에 들어갈 길은 없지만, 연결 문자열을 메시지·로그에 넣지 마라.

## 3. 캐시·임시 저장 — `MigrationStudio.Core.Storage`

```csharp
public static class MetadataCache
{
    /// <dataDir>\metadata\<profileId>_<SCHEMA>.json  (이름에 쓸 수 없는 문자는 '_')
    public static string PathFor(string dataDirectory, string profileId, string schema);
    public static void Save(string dataDirectory, string profileId, SchemaMetadata metadata);   // 임시 파일에 쓰고 바꿔치기(쓰다 죽어도 이전 파일 유지)
    public static SchemaMetadata TryLoad(string dataDirectory, string profileId, string schema); // 없거나 깨졌으면 null, 읽으면 Cached = true
}

public sealed class JobDraft { public DateTime SavedAt; public string FilePath; public bool Dirty; public MigrationJob Job; }
public static class JobDraftStore
{
    /// <dataDir>\jobs\current.draft.json — 창을 닫았다 열어도 하던 작업이 그대로 열리게(DB Helper SQL 임시 저장과 같은 역할)
    public static string PathFor(string dataDirectory);
    public static void Save(string dataDirectory, JobDraft draft);       // 원자적 쓰기, JobJson.Options
    public static JobDraft TryLoad(string dataDirectory);                 // 없거나 깨졌으면 null(깨진 파일은 *.bad로 이름을 바꿔 남김)
}
```
작업 JSON은 P1의 `JobFile`(Parse·Serialize)로 쓰고 읽는다(v1 임시 저장도 v2로 올라오게).

### 3.1 `MetadataJsonOptions`
`MigrationStudio.Core.Metadata.MetadataJsonOptions.Options`(camelCase, 들여쓰기 2칸, 한글 그대로) — `MetadataCache`와 테스트 픽스처(골든 `source`·`target`)가 같이 쓴다. P1 테스트 안에서 따로 만든 옵션이 있으면 이것으로 바꿔라.

## 4. 시험

**단위(`tests/MigrationStudio.Tests`, Oracle 없이)**: `OracleVersionText`, 열 형식 표시 규칙(표의 모든 줄 — 형식을 만드는 함수를 `internal static`으로 떼어 시험), `ConnectionTarget.Describe`·검사 문구, `OracleErrors.Describe`(OracleException은 만들 수 없으니 일반 예외·AggregateException·OperationCanceledException·메시지에 ORA-01013만 있는 예외), `MetadataCache`·`JobDraftStore` 왕복·깨진 파일·원자적 쓰기, `DatabaseAdapters.For`.

**실제 Oracle(`tests/MigrationStudio.OracleIT`)** — DB Helper `tests/MyPlugin.OracleIT/OracleFixture.cs` 방식 그대로:
- `ORACLE_IT_DSN`(예: `localhost:1521/xe`)이 없으면 전부 Skip. `ORACLE_IT_SYS_PW`(기본 `oracle`, SYSTEM 계정)로 준비.
- 이 PC에는 docker `oracle-12c`(localhost:1521/xe, SYSTEM/oracle)가 떠 있다. **이 작업에서는 이 컨테이너에 쓰기를 허락한다** — 단 시험 사용자 `MIG_IT_SRC`·`MIG_IT_TGT`·`MIG_IT_RO`(CREATE TABLE 없음)만 만들고 지운다. 다른 스키마는 건드리지 마라.
- 준비물(ODP.NET 직접 호출): `MIG_IT_SRC`에 POC 원본과 비슷한 `SRC_CUSTOMER`(PK, VARCHAR2(30)·CHAR(1)·DATE·NUMBER(12,2)·`VARCHAR2(20 CHAR)`·DEFAULT 'Y'·주석), `SRC_CUSTOMER_GRADE`, FK가 있는 `SRC_ORDER`, 뷰 하나, `TIMESTAMP(6) WITH TIME ZONE` 열 하나. 행 몇 개 넣고 `DBMS_STATS.GATHER_SCHEMA_STATS`.
- 시험:
  1. 접속 테스트 성공(Version "Oracle 12c", LatencyMs 값 있음, CurrentSchema, Nls.CharacterSet), readOnly 성공
  2. 실패 문장: 틀린 비밀번호 → ErrorCode ORA-01017 · 없는 서비스 → ORA-12514 · 닫힌 포트(예: 1) → 실패(코드는 보고만) · 비밀번호에 `;`·`=`·`'`가 든 사용자 접속 성공
  3. 메타데이터: 테이블 3·뷰 1, 열 형식 문자열이 표대로, PK·FK·NULL 허용·기본값·주석·통계 행 수
  4. 다른 사용자의 스키마(권한 없음) → 빈 목록
  5. 제어 테이블: `MIG_IT_TGT`(CREATE TABLE 있음) AUTO → TARGET, `MIG_IT_RO` AUTO → LOCAL
  6. 취소: 메타데이터 읽기 중 토큰 취소 → OperationCanceledException(또는 취소 문장)
- 끝나면 시험 사용자를 지운다. 단 `ORACLE_IT_KEEP=1`이면 남긴다(P3 화면 시연에서 `MIG_IT_SRC`·`MIG_IT_TGT`를 씀). 비밀번호는 상수로 두고 보고서에 적어라(시험 전용 계정).
- 실측 수치(접속 ms, 메타데이터 ms)는 보고서에 적어라.

## 5. 완료 기준

```powershell
dotnet build MigrationStudio.sln -c Release --nologo
dotnet test tests/MigrationStudio.Tests -c Release --nologo
$env:ORACLE_IT_DSN = "localhost:1521/xe"; dotnet test tests/MigrationStudio.OracleIT -c Release --nologo
dotnet publish src/MigrationStudio -c Release --nologo     # zip에 Oracle.ManagedDataAccess.dll(플러그인 쪽·agent 쪽 모두) 들어감
```
보고서: `docs/dev/reports/P2-report.md`.
