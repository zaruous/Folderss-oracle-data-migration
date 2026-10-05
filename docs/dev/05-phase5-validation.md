# 작업 지시서 P5 — 실행 전 검증 엔진 · 검증 화면 (작업id `P5`)

`docs/dev/00-common.md`를 먼저 읽어라. P1~P4가 끝난 저장소에서 시작한다.
기준: 설계서 `UI-MIG-005`(검사 C01~C13, SQL-1~4, 실행 후 P01~P06), POC `backend/validation.js`(`runPre`) · `pages/validation.js`.
**이 작업은 "실행 전 검증"만 구현한다.** 실행 후 검증(P01~P06)의 엔진은 실행 결과가 필요하므로 P6에서 한다 — 이번엔 그 탭의 화면 틀만 만든다(5.4).

## 1. 범위

| 넣음 | 빼고 나중에 |
|---|---|
| Core: `ValidationEngine.RunPreAsync`(C01~C13), `ValidationSql`(검사용 SQL 문자열), `ExpressionProfiler`(실측 프로파일), `ValidationGate`(실행 차단 판정) | 실행 후 검증 엔진(P01~P06) · 오류 테이블 요약(FUNC03) — P6 |
| P1 `MappingService`에 실측 통계를 받는 오버로드 | |
| STEP 4 검증 화면(실행 전 탭 전부, 실행 후 탭은 틀만), F6·메뉴·아이콘, 단계 막대 요약 | |
| 작업이 바뀌면 "검증 뒤 작업이 바뀜" 표시(작업 버전 카운터) | |

## 2. Core

### 2.1 결과 모양 (`MigrationStudio.Core.Validation`)

```csharp
public sealed class FixAction { public string Page; /* "connection"|"tables"|"columns"|"sql"|"run" */ public string MappingId; public string Column; }

public sealed class ValidationItem
{
    public string Group;      // 접속 · 객체 · 매핑 · 형식 · 제약 · 공간·실행
    public string Check;      // "원본 접속", "VARCHAR 길이", …  (POC 문구와 같게)
    public string Target;     // 대상 열의 "이름" 칸
    public string Level;      // CheckLevels.PASS|INFO|SKIP|WARN|ERROR
    public string Detail;
    public FixAction Fix;     // 없으면 null
    public string MappingId;  // 이 항목이 특정 매핑에 속하면 그 id, 아니면 null(전역 — 항상 실행을 막음)
    public string Sample;     // 표본으로 잰 값이면 "표본 5%" 같은 꼬리말(없으면 null)
}

public sealed class ValidationContext
{
    public MigrationJob Job; public MigrationSettings Settings;
    public ConnectionTarget Source; public ConnectionTarget Target;        // 비밀번호까지 채워서 받는다(창이 구한다). null이면 "접속을 고르세요" ERROR
    public ConnectionProfile SourceProfile; public ConnectionProfile TargetProfile;
    public SchemaMetadata SourceMeta; public SchemaMetadata TargetMeta;   // null이면 "메타데이터를 먼저 불러오세요" ERROR 후 멈춤
    public IDictionary<string, IList<QueryColumn>> SqlDescribe;           // mappingId → 마지막 DESCRIBE(P4). 없으면 이번 검증이 SqlSourceService로 새로 한다
    public ConnectionTestResult SourceTest, TargetTest;                  // 이미 시험한 결과가 있으면 재사용, 없으면 엔진이 TestAsync
}

public sealed class ValidationEngine
{
    public ValidationEngine(IDatabaseAdapter adapter);
    /// 검사를 하나씩 끝나는 대로 onItem으로 알리고(UI 스레드로 보내는 건 호출자 몫) 전체 목록을 돌려준다. 취소 가능.
    public Task<List<ValidationItem>> RunPreAsync(ValidationContext ctx, Action<ValidationItem> onItem, CancellationToken ct);
}

public static class ValidationGate
{
    /// 실행 차단 판정(UI-MIG-005 5장): 전역 ERROR는 항상 막고, 매핑 ERROR는 selectedMappingIds에 든 매핑일 때만 막는다.
    public static GateResult Evaluate(IEnumerable<ValidationItem> items, ISet<string> selectedMappingIds);
}
public sealed class GateResult { public bool Blocked; public int Errors; public int Warnings; public List<ValidationItem> Blocking; }
```

### 2.2 검사 구현 (POC `runPre`를 옮기되, **DB로 잴 수 있는 것은 실제로 잰다**)
검사 순서·묶음·`Check` 문구·`Detail` 문구·`Fix` 대상·수준 규칙은 POC `runPre`와 같다(이 파일을 한 줄씩 대조하라). 달라지는 곳만 적는다:

| ID | 이번 구현 |
|---|---|
| C01 | 시험 결과가 없으면 `adapter.TestAsync`(원본 readOnly). 대상이 `WriteBlocked`면 ERROR(Fix connection). Detail = `Oracle 19c · 31 ms · host:port/service` + 원본은 ` · 읽기 전용(SELECT만)` |
| C02 | 메타데이터(캐시일 수 있음)와 별도로, 접속이 있으면 SQL(UI-MIG-005 C02, 최대 1,000개씩 `IN` 분할)로 **실제 존재**를 확인(캐시가 오래됐을 수 있으므로). 캐시에 있는데 DB에 없으면 ERROR `ORA-00942: 없음 — …` + "메타데이터를 다시 불러오세요" |
| C03 | 메타데이터 기반(POC와 같음) |
| C04 | SQL 원본: `SqlSourceService.ValidateAsync`(P4)로 **실제 PARSE·DESCRIBE** 후 SQL 쪽 항목(`SQL 구문`·`원본 객체·열`·`결과 열 수`·`별칭`·`바인드 변수`·`체크포인트`)의 최악 수준. 접속이 안 되면 ERROR |
| C05 | `MappingService.Status`(P1) + 실측 통계 오버로드(아래 2.3) |
| C06 | 실측 최대 길이 vs 대상 길이. 대상 열이 `BYTE` 단위(`OracleType.LengthUnit == "BYTE"` 또는 단위 없음+DB 기본 BYTE)면 **바이트**(`MAX(LENGTHB(식))`)로 비교, 원본·대상 문자 집합이 다르면(P2 `NlsInfo`) 대상 집합 기준으로 바이트 길이를 보정해 Detail에 `(문자 집합 KO16MSWIN949 → AL32UTF8)`를 적는다. 보정은 한글 등 비ASCII가 있는 값만 늘리므로, 실측은 `SUM(LENGTHB(CONVERT(식, '대상집합')))` 대신 **`MAX(LENGTHB(식))`를 원본 집합으로 재서 ×(대상 최대 바이트/문자 ÷ 원본 최대 바이트/문자) 올림**하되 이 근사를 Detail에 "추정"으로 표기 |
| C07 | `SELECT MAX(ABS(식)) AS MAX_ABS, MAX(LENGTH(TO_CHAR(TRUNC(ABS(식))))) AS INT_DIGITS FROM …` (UI-MIG-005 C07). NUMBER 정밀도 감소 매핑에만 |
| C08 | NOT NULL 대상 열 중 원본이 NULL 가능한 열만: `COUNT(*) … WHERE TRIM(식) IS NULL`(문자) / `WHERE 식 IS NULL`. 결과가 0이면 PASS |
| C09 | 메타데이터 기반(POC와 같음) |
| C10 | 원본 병합 키 중복(UI-MIG-005 SQL-1 첫 문장, 표본 5%이면 `SAMPLE BLOCK (5)` — 표본으로는 중복 **없음을 보증할 수 없으니** 표본일 때 PASS 문구에 "표본 5% 기준"을 붙이고 `Sample` 꼬리말 채움), INSERT ONLY + 대상 기존 행 WARN은 메타데이터의 `Rows`, 없으면 `SELECT COUNT(*)`(대상) |
| C11 | FK 부모가 이번 작업에 없고 부모에 행도 없으면 WARN(POC). 부모가 있으면(작업 포함 또는 행 있음) 원본 고아 행 수(UI-MIG-005 SQL-2, 같은 원본 DB의 부모 원본 테이블을 찾을 수 있을 때만 — 매핑 사전으로 부모 대상 테이블 ← 원본 테이블을 찾음) 0이면 PASS, 아니면 WARN `고아 n행` |
| C12 | `TablespaceInfo`(P2 메타데이터)가 있으면 그것, 없으면 UI-MIG-005 SQL-3을 대상에서 실행. 필요 크기 계산은 POC와 같음. 정보를 못 구하면 INFO `테이블스페이스 정보를 읽지 못했습니다 — 대상 계정 권한(USER_FREE_SPACE) 확인` |
| C13 | 같은 대상 둘 이상 / 운영 DB 쓰기 / 오류 테이블(**실제 존재 확인** `ALL_TABLES`, 없으면 INFO `없으면 실행 전에 만듭니다`) / 끝나지 않은 체크포인트(작업 파일의 `Checkpoints`에 더해, 저장소가 TARGET이고 `MIG_CHECKPOINT`가 있으면 UI-MIG-005 SQL-4 마지막 문장도 읽어 병합 — 불일치하면 INFO로 두 값을 보여 줌) |

- **표본**: 큰 테이블(통계 행 수 > 1,000,000)의 집계(C06·C07·C08·C10)는 `SAMPLE BLOCK (5)` + 항목 `Sample = "표본 5%"`. 작은 테이블은 전체. SQL 원본(인라인 뷰)은 `SAMPLE`을 못 쓰므로 전체를 재되, `FETCH FIRST 1000000 ROWS ONLY` 한도를 두고 한도를 넘으면 `Sample = "앞 100만 행"`.
- **시간 제한·취소**: 질의마다 60초. 시간 초과·권한 부족(ORA-00942 on `ALL_*`·`USER_*`)은 그 검사만 INFO `확인하지 못했습니다 — <사유>`로 남기고 검증 전체는 계속한다(예외로 멈추지 않는다). 취소 토큰이 서면 `OperationCanceledException`.
- **읽기 전용 보호**: 모든 검사 질의는 P4 `QueryAsync`(READ ONLY 트랜잭션 + SELECT 전용 보호)로만 실행한다. 어떤 검증 질의도 쓰기를 하지 않는다(오류 테이블 생성은 P6의 실행 직전).
- 질의 문자열은 `ValidationSql`(정적, 단위 시험)에서 만든다: `ExistingTables(owner, names)`, `LengthProfile(...)`, `NumberProfile(...)`, `NullCount(...)`, `DuplicateKeys(...)`, `OrphanRows(...)`, `TablespaceFree()`, `ErrorTableExists(owner, name)`, `PendingCheckpoints()`. 식별자는 P4 `SqlProbe`와 같은 검증(`[A-Z0-9_$#]`).
- 진행: 항목마다 `onItem`. 각 항목 사이에 인위 지연을 두지 마라(POC의 `sleep`은 흉내).

### 2.3 `MappingService` 오버로드 (P1 골든 불변)
`Status(mapping, source, target, IReadOnlyDictionary<string, ColumnStats> measured)`, `CheckColumn(…, measured)` — `measured`의 키는 **대상 열 이름**, 값은 그 열로 들어가는 값(식 적용 후)의 실측(`MaxLength`·`Nulls`·`Blanks`·`DigitsMaxLength`·`Max`). 있으면 `SourceInfo`가 추정한 통계를 덮어쓴다. 기존 시그니처는 `measured = null`로 위임(골든 167개 그대로 통과해야 한다).
`ExpressionProfiler`(Core.Validation): 매핑·대상 열마다 한 질의로 `SAMPLE_ROWS`, `NULL_ROWS`, `MAX_CHARS`, `MAX_BYTES`, `BLANK_ROWS`(문자), `MAX_ABS`(숫자)를 재서 `ColumnStats`로(UI-MIG-003 SQL-2). 열 수가 많으면 **한 질의에 열 8개씩** 묶는다(전체 스캔 횟수 줄이기). 결과는 `Dictionary<mappingId, Dictionary<targetColumn, ColumnStats>>`로 보관해 같은 검증 안에서 C05~C08이 같이 쓴다.

### 2.4 시험
- **단위(Oracle 없이)**: 가짜 어댑터(`FakeAdapter`, 테스트 프로젝트 안, `IDatabaseAdapter` 구현 — 질의 문자열을 키로 준비된 `QueryResult`를 돌려주고 호출 기록을 남김)로 `RunPreAsync` 전체를 돌린다: ① **골든 `sampleJob` + `source`/`target` 메타데이터**에서 POC와 같은 항목 목록(그룹·Check·Level·Fix; 실측 의존 Detail은 가짜 어댑터가 POC 통계와 같은 값을 주면 같아야 함)이 나오는지 — POC `runPre` 결과를 **새 골든 파일을 만들지 말고**(골든은 설계 소유) 이 시험 안에 기대 목록으로 적고, POC 출력과 대조한 근거를 보고서에 적어라 ② 접속 없음·쓰기 금지 대상·메타데이터 없음 ③ 질의 시간 초과 → INFO 후 계속 ④ 취소 ⑤ 모든 질의가 SELECT/WITH로만 시작함(가짜 어댑터가 검사) ⑥ `ValidationGate`(전역 ERROR 항상 차단, 선택 밖 매핑 ERROR는 통과, WARN만이면 통과) ⑦ 표본 꼬리말 ⑧ `ValidationSql` 문자열.
- **실제 Oracle(OracleIT)**: `MIG_IT_SRC`·`MIG_IT_TGT`에 일부러 문제를 심고(잘리는 길이, NOT NULL에 NULL, 중복 키, FK 부모 없음, 없는 대상 테이블) `RunPreAsync`가 기대한 수준을 내는지, 시험 사용자 `MIG_IT_RO`로 `USER_FREE_SPACE` 권한이 없어도 INFO로 이어지는지, **검증이 어떤 쓰기도 하지 않음**(검증 전후 대상 스키마 객체·행 수 동일), 취소.

## 3. 상태 (`StudioState` 확장)
- `JobVersion`(int): `MarkChanged()`마다 +1. 검증 결과가 만들어진 시점의 값을 `Session.Pre.JobVersion`에 저장 → 다르면 `Stale`.
- `Session.Pre` = { Items(List<ValidationItem>), Running, At, ElapsedMs, JobVersion, Error }, `Session.Post`(P6용 자리, 지금은 null 고정).
- `Ui.ValTab`("pre"|"post"), `Ui.ValFilter`("all"|"issues").
- 서비스: `ValidationRunner`(Services/): 비밀번호 구하기(P3 규칙) → `ValidationContext` 만들기 → `RunPreAsync` → 항목이 올 때마다 UI 스레드로 `Session.Pre.Items.Add` + 화면 갱신(**항목마다 표 전체를 다시 그리지 말고 행만 덧붙임**), 끝나면 토스트 `검증 완료 · PASS n · WARN n · ERROR n`. 진행 중 재실행은 무시, 창이 닫히면 취소. 이미 시험한 접속 결과는 재사용.

## 4. 화면 (`Pages/ValidationPage.cs`) — POC `pages/validation.js`
공통 부품은 P3·P4의 `Kit`·`RowGrid`·`Theme`을 쓴다.
- 머리: 제목 `검증`(STEP 4), 설명 POC 문구, 오른쪽 동작 [☰ 실행 전 검증 (F6)](주, 진행 중 `검증 중…` + 끔) [⟲ 실행 후 검증](지금은 항상 끔, 툴팁 "실행을 마친 뒤에 켜집니다 — 다음 단계에서 구현"). 바닥: [‹ 컬럼 매핑] / 안내 "검사에 쓰는 SQL은 기능 설계서 UI-MIG-005에 정리" / [다음: 실행].
- 본문 카드 하나(밑줄 탭 `실행 전 검증 n` · `실행 후 검증`):
  - **미검증**: EmptyState(checklist, "아직 검증하지 않았습니다", POC 문구 + 표본·실측 안내 한 줄, [검증 실행 (F6)]).
  - **실행 전 탭**: 위 줄 — 집계 칩(`PASS n` `WARN n` `ERROR n` + INFO가 있으면 `INFO n`, 칩 = 배지 + 숫자) · (오른쪽) 진행 중 Spinner `검사 중… n` / `마지막 검증 14:47:13 · 3.1초` · `검증 뒤 작업이 바뀜` Pill(warn) · Segmented(전체 · 문제만). 진행 중이면 얇은 진행 막대(항목 수/34 기준 최대 96%, 강조색). 그 아래 **차단 안내**(진행 끝난 뒤): ERROR 있음 → err 알림 `ERROR n건 — 이관을 실행할 수 없습니다. 고친 뒤 다시 검증하세요. Dry Run은 할 수 있습니다.` / WARN만 → warn 알림 `경고를 확인했다면 실행할 수 있습니다.` + 링크 [실행 화면으로 ›] / 통과 → ok 알림 `모두 통과했습니다.` + 링크.
  - 표(RowGrid): 열 결과(배지, 78) · 검사 항목(SemiBold, 150) · 대상(고정폭 11.5px, 200, 줄바꿈 허용) · 내용(12px, 줄바꿈 허용 `\n` 유지) · 조치(링크 버튼, 90). 묶음 머리 줄(`접속 2` — 그룹 이름 + 개수 흐리게, SurfaceBackground, 접기 없음). 그룹 안은 ERROR → WARN → INFO → SKIP → PASS 순(안정 정렬). 진행 중 마지막 줄에 `다음 검사…`(Spinner). `Sample` 꼬리말이 있으면 내용 끝에 `  · 표본 5%`(DisabledText).
  - 조치 링크 `label ›`: connection=`접속`, tables=`테이블 매핑`, columns=`컬럼`, sql=`SQL 편집`, run=`실행`. 동작: `Ui.SelMapping`·`SelColumn` 설정 후 단계 이동, sql은 SQL 원본 편집기 열기. PASS 항목은 링크 숨김.
- **실행 후 탭(틀만)**: 실행이 없으면 EmptyState(history, "실행을 마친 뒤에 확인합니다", POC 문구, [실행 화면으로]) — 결과 표·`다시 검증`은 P6.
- `F6`·메뉴 `실행 > 실행 전 검증(V)`·아이콘 checklist 켬(진행 중엔 끔). 어느 단계에서 F6을 눌러도 STEP 4로 이동해 검증 시작.
- 단계 막대 요약(`StepLogic`): 검증 없음 "아직 안 함" / 진행 중 busy `검사 중… n` / 결과 error(ERROR 있음) · warn(`Stale` 또는 WARN) · done, 요약 `PASS n · WARN n · ERROR n` 또는 `검증 뒤 바뀜`.

## 5. 시험·그림
- 논리 시험(`Logic/ValidationLogic.cs` — WPF 없는 순수 논리): 그룹 묶기·정렬, 집계, 차단 안내 종류 판정, 필터, 조치 링크 라벨, 단계 요약.
- DevHost 그림(`docs/dev/reports/P5-shots/`, black·light, 1100×700): `validation-empty`, `validation-pre`(예제 작업 + `--fake-oracle`로 만든 결과: ERROR 1·WARN 4 포함, 묶음 6개), `validation-running`(진행 중 상태 — DevHost `--freeze-validation 12`로 항목 12개에서 멈춘 상태), `validation-post-empty`. 가짜 어댑터는 P4가 만든 `src/MigrationStudio.Testing`의 `FakeAdapter`를 쓴다(검증 질의 응답기 `ValidationResponder`를 같은 프로젝트에 더한다: 질의 문자열 패턴 → POC `runPre`가 쓰던 통계와 같은 값).

## 6. 완료 기준
```powershell
dotnet build MigrationStudio.sln -c Release --nologo --no-incremental     # 경고 0
dotnet test tests/MigrationStudio.Tests -c Release --nologo                # 실패 0 (골든 167 포함)
$env:ORACLE_IT_DSN = "localhost:1521/xe"; dotnet test tests/MigrationStudio.OracleIT -c Release --nologo
dotnet publish src/MigrationStudio -c Release --nologo                     # zip에 MigrationStudio.Testing.dll 없음
dotnet run --project tools/DevHost -c Release -- --seed --fake-oracle --theme black --shot docs/dev/reports/P5-shots/black-validation-pre.png --size 1100x700 --step 4 --run-validation
```
- 실제 Oracle로 DevHost에서 STEP 4 검증을 돌린 결과(항목 수·PASS/WARN/ERROR 수·총 시간)를 보고서에 적어라.
- 보고서 `docs/dev/reports/P5-report.md`: POC와 다른 점, 설계 공백(문자 집합 보정 근사 등), 그림 목록.
