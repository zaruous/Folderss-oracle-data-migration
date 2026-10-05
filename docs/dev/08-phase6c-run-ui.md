# 작업 지시서 P6c — 실행 화면 · 실행 후 검증 (작업id `P6c`)

`docs/dev/00-common.md`를 먼저 읽어라. P1~P5, P6a(엔진), P6b(에이전트·`RunLauncher`)가 끝난 저장소에서 시작한다.
기준: 설계서 `UI-MIG-006`(화면·팝업·FUNC·로그 형식), `UI-MIG-005` 4.2(실행 후 P01~P06), POC `pages/run.js`·`backend/validation.js`(`runPost`)·`pages/validation.js`(post 탭).
P3·P4·P5의 `Kit`·`RowGrid`·`Theme`·`StudioState`·`Dialogs`·`DevHost`를 그대로 쓴다.

## 1. 범위

| 넣음 | 빼고 나중에 |
|---|---|
| STEP 5 실행 화면 전부: 작업 선택 · 실행 제어 · 진행 · 파이프라인 · 작업별 표 · 로그 · 체크포인트 카드, 팝업 3종 | CDC |
| 시작 흐름(검증 먼저·검증 ERROR 팝업·검증 뒤 바뀜·파괴적 방식 확인) → `RunLauncher`(P6b) | |
| 창을 닫았다 열었을 때 **다시 붙기**, 살아 있는 에이전트 표시(설정 > 실행 에이전트 탭의 "실행 중") | |
| 실행 후 검증 엔진 P01~P06(`PostValidationEngine`)과 검증 화면 "실행 후" 탭 | |
| 오류 테이블 요약(FUNC03) — 실행 후 검증·완료 알림의 "거부 n행" 근거 | |
| 작업 파일의 `Checkpoints` 동기화, 체크포인트 지우기(FUNC09), F5·메뉴·아이콘 완성, 단계 막대 실행 요약 | |

## 2. 상태 (`StudioState` 확장)

- `Session.Run` = `RunView`: { Client(AgentClient), RunId, Mode, Snapshot(마지막 `RunSnapshot`), Log(List<LogEntry> 최근 2,000), State("idle|starting|running|pausing|paused|done|stopped|failed|detached"), AgentPid, Dry, StartedAt, Error(시작 실패 문장) }.
  - `detached`: 연결이 끊겼지만 에이전트는 살아 있음(화면에 "연결 끊김 — [다시 붙기]").
- `Ui`: `RunMode`("DRY"|"EXECUTE"|"RESUME", 기본 EXECUTE), `RunSelected`(HashSet<string> — 처음에는 매핑 `Use`), `LogFilter`("all"|"info"|"warn"|"error").
- 이벤트는 에이전트 클라이언트의 스레드 풀 스레드에서 오므로 **창 단위 `RunPresenter`**(UI 스레드, `Dispatcher`)가 받아 상태에 쌓고, **렌더 프레임에 한 번**(`CompositionTarget.Rendering` 또는 `DispatcherTimer` 100ms, 10Hz 초과 금지) 화면을 갱신한다. 로그는 `logShown` 인덱스 이후만 덧붙임(전체를 다시 만들지 않음).
- 체크포인트 동기화: `OnCheckpoint`/`Snapshot`의 체크포인트를 받으면 작업의 `Job.Checkpoints[mappingId]`(P1 `CheckpointInfo`)를 갱신(Column·Value·Rows·Total·At·RunId·Status)하고 `MarkChanged`(임시 저장) — 단 **0.5초마다 한 번**으로 묶는다.
- `StudioState.IsRunning`(P4가 자리를 만든 속성) = `State ∈ {starting, running, pausing, paused}`: 이때 새 작업·열기·템플릿 가져오기·접속 바꾸기·매핑 편집을 막는다(P3·P4 화면의 해당 동작에서 `IsRunning`이면 토스트 "실행 중에는 바꿀 수 없습니다"; 읽기 전용 입력은 `IsEnabled=false`).
- **다시 붙기**(창 열 때 `MigrationView.Loaded`): `AgentRuns.ListAlive(DataDirectory)`에서 **이 작업 이름**의 에이전트를 찾으면 `RunLauncher.AttachAsync` → `Session.Run` 복원(현재 스냅숏 + 최근 로그 2,000줄) → 상태가 `running`이면 STEP 5로 이동하고 토스트 "진행 중인 이관(R-…)에 다시 붙었습니다". 다른 작업 이름의 에이전트는 설정 탭 "실행 중"에만 표시. 끝난 실행의 마지막 상태(`ListRecent` 첫 항목이 이 작업이고 에이전트가 아직 `--idle-exit` 대기 중이면 `end`를 받아 결과 표시, 이미 종료됐으면 `runs\<RUN_ID>.json`+로그 파일 끝 2,000줄로 "지난 실행" 요약만 보임).

## 3. 화면 (`Pages/RunPage.cs`) — UI-MIG-006, POC `pages/run.js`

공통 틀·부품은 P3~P5와 같다. **실행 중에는 화면을 다시 만들지 않는다** — 칸을 한 번 만들어 참조를 쥐고 값만 바꾼다(POC `refs`/`update`).

- 머리: 제목 `실행`(STEP 5), 설명 POC 문구. 바닥: [‹ 검증] / `F5 시작 · 일시정지 중 F5 = 이어서` / (다음 없음).
- 맨 위 **결과 알림**(끝난 뒤): done → ok `이관 완료 · 00:00:33 · 처리 n행 · 거부 n행` + [실행 후 검증 ›](Dry가 아니면) / `Dry Run 완료 …`(Dry는 [실행 후 검증] 없음) / stopped → warn `중지됨 …` + [체크포인트에서 재개 ›] / failed → err `실패 …` + 실패 이유 첫 줄 + [체크포인트에서 재개 ›]. `detached`면 warn `에이전트와 연결이 끊겼습니다(에이전트는 계속 실행 중) — [다시 붙기]`.
- 본문 2열(너비 ≥ 1100이면 `1* | 1*`, 미만이면 세로):
  - **작업 선택 카드**: 제목 list 아이콘 "작업 선택", 부제 `n개 · N 행`, 도구 CheckBox "모든 작업 (ALL SELECTED)". 목록(최대 높이 340 스크롤): 한 항목 = CheckBox + 실행 순서 번호(선택된 것만 1부터, 아니면 `–`) + [제목: SQL 원본이면 `SQL` 꼬리표(`Theme.SqlTag`) + `원본 → 대상` / 보조: `~행 수 행`(SQL이면 `~`) · 방식 라벨 · 체크포인트 `⟲ 열 값 (68%)`(경고색, 툴팁 = 시각) 또는 `지난 실행 완료`(성공색)] + 오른쪽 `ERROR n` 배지(검증 결과에서). 순서는 FK 부모 → 자식(`RunPlanner.OrderByFk`, 모든 매핑 기준). 실행 중에는 체크 비활성. 바닥 안내 "실행 순서: 대상 외래 키 기준 부모 → 자식".
  - **실행 제어 카드**: 제목 play 아이콘 "실행 제어". Field "실행 모드 (Run Mode)": Segmented(Dry Run · 이관 실행 · 체크포인트에서 재개) + 모드 설명 힌트(POC `MODES` 문구). 버튼 줄: [▶ 이관 시작 | Dry Run 시작 | 재개 시작](주, F5) [⏸ 일시정지](경고색) [▷ 이어서](성공색) [□ 중지](위험색). 게이트 알림(없음·검증 중·ERROR n건·검증 통과·검증 뒤 바뀜 — POC `controlCard`와 같은 문구, ERROR 개수는 **고른 작업만**(`ValidationGate`)). KeyValue: 배치 / 오류 정책(`계속 + 오류 테이블 (ERR$_<대상>)`) / 대상(DB 배지 + 스키마) / 체크포인트 저장소(`자동 → 대상 DB(MIG_CHECKPOINT)` 또는 `로컬 파일` — 실행 전엔 설정값, 시작 후엔 결정값) / 실행 위치(`하위 프로세스 MigrationAgent.exe · 창·Folderss를 닫아도 계속` 또는 `Folderss를 닫으면 함께 중지`). **POC 전용 시뮬레이션 속도 줄은 없다.**
- **진행 카드**: 제목 sync 아이콘 "진행", 도구 `R-… · MigrationAgent.exe PID n`(DisabledText 12px) + 상태 Pill(`실행 중`(run, 점 깜빡임)·`일시 정지 요청…`·`일시 정지`·`완료`·`중지됨`·`실패`·`연결 끊김`·`대기`, Dry면 앞에 `Dry Run · `). 큰 숫자 줄: `850,000`(24px SemiBold) `/ 1,240,325 행`(SecondaryText) 오른쪽 `68%`. 아래 `지금: 라벨 (1/3)`(12px). 진행 막대(높이 8, 실행 중 줄무늬 애니메이션 — `LinearGradient` 오프셋 이동, 일시정지 경고색, 완료 성공색, 중지·실패 위험색). 통계 7칸(처리 속도 `38,138 행/초` · 경과 `00:00:29` · 남은 시간 · 커밋 · Inserted · Updated · Rejected — 거부가 있으면 경고색). **엔진 파이프라인**: 6단계 카드(원본 읽기 · Transform · 컬럼 매핑 · 배치 쓰기 · 커밋 · 체크포인트) 사이에 `›`; 각 카드 = 이름(+아이콘) / 지표 한 줄 / 보조 한 줄 / 얇은 막대 — 문구·계산은 POC `update()`의 `set(...)` 호출 그대로(`읽기 행/초`·`Fetch n × m회`·`Oracle SELECT 안에서`·`열 → 대상`·`배열 커밋크기 × 쓰기 중 작업자`·`커밋 n회`·`열 = 값`). 값은 스냅숏의 `Pipeline`에서(P6a가 채움). **작업별 표**(RowGrid): # · 작업(라벨 + 아래 `방식 · 재개: 열 > 값`) · 상태 배지(대기·실행 중(Spinner)·일시 정지·완료·중지·실패·건너뜀) · 진행(막대 + `%`) · 행(`읽음 / 총`) · Inserted · Updated · Rejected · 체크포인트(고정폭 `열 = 값`/`—`/`없음`). 실행 전에는 계획 미리보기(`RunPlanner` 로컬 계산: 작업 수·재개 위치, 예상 갱신 `~n`, 예상 거부 `~n` — POC `idlePlan` 흉내를 **메타데이터 기반 추정**으로).
- 아래 2열(`1* | 360`): **Migration Log 카드** + **체크포인트 카드**.
  - 로그: 도구 Segmented(전체·INFO·WARN·ERROR) + [복사] [지우기](화면에서만). 본문 = 고정폭 12px 읽기 전용 영역(`ListBox`/`ItemsControl` + 가상화, 최대 2,000줄, 맨 아래에 붙어 있을 때만 자동 스크롤 — 위로 올리면 멈춤). 줄: `14:48:05`(DisabledText) `[START]`(꼬리표 색: START 강조 · INFO 보조 · WARN 경고 · PAUSE 경고 · RESUME 강조 · STOP/ERROR 위험 · DONE 성공 · DRY 보라 `Theme.SqlTag`) 본문(여러 줄이면 들여써 이음). 거르기 기준은 POC `logClass`. 빈 상태 글 POC 문구.
  - 체크포인트 카드: 항목 = [라벨(SemiBold, 말줄임) + Pill(진행 중·중단됨·완료) + [🗑]] / KeyValue(`Last Successful 열 = 값` · `진행 n / m (68%)` · `시각 · RUN_ID`) / 미완료면 코드 상자 `WHERE 열 > 값\nORDER BY 열`(문자 키는 따옴표), 완료면 "증분 이관을 하면 열 > 값부터 읽습니다". [🗑] = 확인 대화상자(위험) 후 `Job.Checkpoints` 삭제 + 저장소가 TARGET이면 `ICheckpointStore.DeleteAsync`(대상 `MIG_CHECKPOINT`) / LOCAL이면 파일 항목 삭제. 실행 중엔 비활성. 비면 EmptyState(history, "체크포인트 없음", …). 이 카드는 0.5초마다만 다시 그림.
- **시작 흐름**(`F5`·메뉴 `실행 > 이관 시작`·버튼; UI-MIG-006 3장 "시작" 행 그대로):
  1. 메타데이터·접속이 없으면 토스트로 막음. 고른 작업 0개면 막음("실행할 작업을 고르세요").
  2. Dry Run이 아니면: 검증 안 했으면 확인 대화상자 "실행 전 검증을 아직 하지 않았습니다. 지금 검증할까요?"([검증 먼저](= STEP 4로 가서 F6 동작)·[그냥 실행](위험 아님, 경고 문구)·[취소]) → 고른 작업에 검증 ERROR가 있으면 **팝업 ② 실행할 수 없음**(ERROR 목록 5개까지: `검사 · 대상` + 첫 줄, 위험 아이콘, [닫기] [검증 결과 보기]) → 검증 뒤 작업이 바뀌었으면(`Stale`) 확인 "검증한 뒤 작업이 바뀌었습니다. 다시 검증하지 않고 실행할까요?" → 파괴적 방식(`TRUNCATE_INSERT`·`DELETE_INSERT`)이 있으면 **팝업 ① 되돌릴 수 없는 이관 방식**(대상 DB 배지 + `• TRUNCATE + INSERT  NEXT_APP.TB_X` 목록 + 체크 "지워도 되는 것을 확인했습니다", [취소] [실행](체크 전 꺼짐, 위험색)). 대상이 빨강(운영)이면 팝업 ① 문구에 "운영 DB" 강조.
  3. `RunMode = RESUME`: 체크포인트가 없는 선택 작업은 처음부터 한다고 토스트 안내(`체크포인트가 없는 작업 n개는 처음부터 실행합니다`). 선택 작업 전부에 체크포인트가 없으면 "재개할 체크포인트가 없습니다 — 이관 실행으로 시작하세요"로 막음.
  4. `RunLauncher.StartAsync`(진행 단계 문구를 진행 카드 Pill 옆에 `계획 중…`·`에이전트 시작 중…`·`연결 중…` — `IProgress<string>`). 실패하면 단계 이름이 든 문장으로 err 알림(`AlreadyRunningException`·`ConcurrencyLimitException`·`AgentMissingException`·계획 오류(`UnsupportedColumnTypeException` 등)는 문구 그대로 + 해당 화면 링크).
  5. 성공: `Session.Run` 설정, 토스트 `실행 R-… 시작`, 진행 카드로 스크롤.
- 제어: [일시정지](running일 때만) → `client.Pause()`, [이어서](paused) → `Resume()`, [중지] → 확인 대화상자 없이 `Stop()`(진행 중 배치 롤백 안내 토스트). 모두 UI를 막지 않는다.
- 창 닫기: 실행 중이어도 **묻지 않는다**(이관은 에이전트에서 계속). 단 설정 `OnHostExit = STOP`이면 닫을 때 한 번 안내(토스트 불가 — 창이 닫히니 `Closing`에서 `MessageBox` 한 번 "Folderss를 닫으면 이관이 중지됩니다 — 설정 > 실행 에이전트에서 바꿀 수 있습니다": [그래도 닫기]·[취소]). Folderss 자체 종료는 플러그인이 막을 수 없으니 이 안내는 **플러그인 창 닫기**에만.
- 메뉴·아이콘: `실행` 메뉴(실행 전 검증 · 실행 후 검증 · Dry Run · 이관 시작 · 일시정지 · 이어서 · 중지 · 체크포인트에서 재개)와 아이콘 막대(play·pause·stop·resume·view)를 POC `can.*` 조건 그대로 켜고 끈다. `F5`는 일시정지 중이면 이어서. 아이콘 `↻`(체크포인트에서 재개)는 미완료 체크포인트가 있고 실행 중이 아닐 때.
- 단계 막대·상태줄 요약(`StepLogic`·`StatusBar`): 실행 상태 문구(POC `stepInfo('run')`·`renderStatus` 그대로: `실행 중 68%`·`일시 정지 68%`·`완료 · 00:00:33`·`중지 · 재개 가능 68%`·`실패 · 68%`·`재개 가능 · 68%`·`대기`), 상태줄 `실행 중 · 850,000 / 1,240,325 행 (68%)`.
- 설정 > 실행 에이전트 탭(P3): "실행 중" 값 = `AgentRuns.ListAlive`(`● PID n  RUN_ID  작업`)/`없음`. 설정 화면이 열려 있는 동안 2초마다 갱신. "실행 파일" 경로 = 실제 `AgentLocator` 경로.

## 4. 실행 후 검증 (`Core.Validation.PostValidationEngine`, 화면은 검증 화면 "실행 후" 탭) — UI-MIG-005 4.2

```csharp
public sealed class PostValidationContext { MigrationJob Job; RunSummary Run; ConnectionTarget Source, Target; SchemaMetadata SourceMeta, TargetMeta; ... }
public sealed class RunSummary { RunId; bool Dry; List<TaskResult> Tasks; }                // 엔진 `end` 스냅숏에서 만듦 (`RunSummary.From(RunSnapshot, RunSpec)`)
public sealed class TaskResult { Key; Label; Status; Mapping(사본); ScopeTotal; BaseRows; Written; Inserted; Updated; Rejected; ErrorTable; Checkpoint; }
public sealed class PostItem { Group(작업 라벨); Check; Source; Target; Level(PASS|WARN|ERROR|SKIP|INFO); Detail; MappingId; }
public sealed class PostValidationEngine { public PostValidationEngine(IDatabaseAdapter adapter);
    public Task<List<PostItem>> RunAsync(PostValidationContext ctx, Action<PostItem> onItem, CancellationToken ct); }
```
- **작업마다** P01~P06(UI-MIG-005 4.2 · SQL-5~8)을 **양쪽 DB에서 따로 집계해 비교**한다(DB 링크 없음). 작업 상태가 `wait`·`skipped`면 건너뜀, `stopped`·`failed`는 P01만 WARN `중지됨: n / m — 재개한 뒤 다시 검증하세요`로 하고 나머지 생략. Dry는 P01을 SKIP `Dry Run은 대상에 쓰지 않아 건너뜀`만.
- **"이관 밖 행" 문제**: 대상에는 이관 전부터 있던 행이 있을 수 있다(MERGE·INSERT ONLY로 기존 테이블에 얹는 경우). 그래서 대상 쪽 집계는 **원본 키 범위 안의 행만** 비교한다. 이를 위해 엔진 계획(`PlanItem`, P6a)에 `TargetRowsBefore`(실행 시작 전 대상 행 수 — 계획 단계에서 `COUNT`)를 더하고 `TaskResult`로 넘긴다.
- **P01 행 수**: 엔진이 보고한 값으로 `Written + Rejected == (원본 범위 행 − BaseRows 제외 전 전체)`, 즉 `BaseRows + Written + Rejected == ScopeTotal`이면 `MATCH`(PASS), 아니면 ERROR. 원본 범위 행 수는 이번 검증에서 **다시 센다**(원본 조건만 적용·체크포인트 조건 없이; SQL 원본은 `SqlSourceCount`). 또한 오류 테이블의 실제 거부 수(`ORA_ERR_TAG$ = RUN_ID`)가 엔진 보고 `Rejected`와 다르면 WARN `오류 테이블 n행 ≠ 엔진 보고 m행`. 대상이 이관 전에 비어 있었고(`TargetRowsBefore == 0`) 방식이 INSERT_ONLY·TRUNCATE_INSERT이면 추가로 **대상 실제 행 수 == Written(+BaseRows)** 를 확인한다.
- **P02 PK 누락**: SQL-6 해시 버킷(1,024)으로 양쪽을 비교한다. 비교 방식은 두 가지이고 Detail에 어느 쪽인지 적는다. (a) **전체 버킷 비교** — `TargetRowsBefore == 0`이고 방식이 INSERT_ONLY·TRUNCATE_INSERT(대상에 원본 밖 행이 없음): 양쪽 버킷별 `COUNT`·`SUM(ORA_HASH(키))`가 다르면 그 버킷만 키를 내려받아 비교(최대 10,000키, 넘으면 WARN `불일치 버킷 n개 — 키 비교 생략`). 거부된 키(오류 테이블 키 열, VARCHAR2)는 원본 쪽에서 뺀다. (b) **원본 키의 대상 존재 확인** — 그 밖(MERGE·DELETE_INSERT 등): 원본 키를 10,000개씩 읽어 대상에서 `WHERE 키 IN (…)`(1,000개 단위 분할)로 존재 확인, 키가 1,000,000개를 넘으면 앞쪽 100,000키 표본(`Sample` 꼬리말 `표본 100,000키`).
- P03 중복 키: 대상 키 `GROUP BY HAVING COUNT(*) > 1` 건수(UI-MIG-005 P03 SQL). P04 샘플 100행: 원본 변환 SELECT에서 무작위 100행(`SAMPLE`+`DBMS_RANDOM`, SQL 원본이면 `ORDER BY DBMS_RANDOM.VALUE FETCH FIRST 100`) → 대상에서 같은 키로 조회해 열별 비교(문자열화 규칙: 숫자 `TO_CHAR`와 동일 형식, 날짜 `YYYYMMDDHH24MISSFF6`; 공백·NULL 의미 차이는 Oracle `''`=NULL 규칙 반영), 불일치가 있으면 WARN/ERROR + 첫 3건을 Detail에. P05 해시: SQL-8(키 + 매핑 열을 `||'|'||`로 이은 값의 `SUM(ORA_HASH)`; 거부 키 제외). 대상 쪽은 (a)의 경우 테이블 전체, (b)의 경우 키가 연속 범위 열(숫자·날짜)이면 원본 키의 `BETWEEN 최소 AND 최대`(원본 밖 행이 그 범위에 섞여 있을 수 있으니 이 경우 Detail에 `범위 안에 이관 밖 행이 있으면 불일치할 수 있음` 안내하고 불일치는 WARN), 키가 연속 범위가 아니면 SKIP `대상에 이관 밖 행이 있어 해시 비교를 건너뜀`. P06 NULL 수: 대상이 NULL 허용인 매핑 열(최대 3개 — 변환식이 있는 열 우선; (b)의 경우 원본 키 범위 안의 행만 세는 것이 불가능하면 SKIP)의 `COUNT(*) - COUNT(열)` 양쪽 비교.
- **큰 표**: 표본 5%를 쓰지 않는다(실행 후 검증은 전체) — 대신 질의마다 **취소 가능**, 질의 시간 제한 10분, 진행 항목 알림, 오래 걸리는 검사 앞에 INFO `큰 테이블이라 오래 걸릴 수 있습니다(취소 가능)`. 읽기 전용 세션만 사용(모든 질의 P4 `QueryAsync`).
- 시험: **가짜 어댑터**(P4 `FakeAdapter`)로 각 검사의 PASS·불일치 경로, `ValidationResponder`에 실행 후 질의 응답 추가. **실제 Oracle**: P6a 복사 결과(`BIG_SRC` → `TGT_BIG`)에 실행 후 검증 → 모두 PASS(해시·키·샘플), 일부러 대상 행 1개 값 수정/삭제/중복 삽입 후 → 각각 P04/P02/P03이 잡는지.
- 화면(검증 화면 "실행 후" 탭, POC `postTab`): 위 줄 `실행 RUN_ID · 14:49:00`, 집계 칩, [⟲ 다시 검증]; 표(RowGrid): 결과(MATCH는 P01 PASS의 라벨) · 검사 · 원본(우측 정렬 고정폭) · 대상 · 내용, 작업별 묶음 머리. 진행 중 `비교 중…`. 실행이 없으면 EmptyState(history, "실행을 마친 뒤에 확인합니다"), 실행이 끝났으나 아직 검증 안 했으면 EmptyState("실행 R-… 결과를 검증할 수 있습니다", Dry 안내 문구, [실행 후 검증]). 실행 화면 결과 알림·메뉴 `실행 > 실행 후 검증`·아이콘이 STEP 4 "실행 후" 탭으로 이동해 시작. 실행이 끝난 `Done|Stopped|Failed` 상태에서만 켬. 다른 RUN_ID의 이전 결과는 `Session.Post.RunId`가 다르면 숨김.

## 5. 오류 테이블 요약 (FUNC03)
`RejectSummary`(Core.Validation): `SELECT ORA_ERR_NUMBER$, MIN(ORA_ERR_MESG$), COUNT(*) FROM <스키마>.ERR$_… WHERE ORA_ERR_TAG$ = :RUN_ID GROUP BY ORA_ERR_NUMBER$ ORDER BY 3 DESC`(P4 `QueryAsync`로 대상에서). 실행 화면 로그의 `거부 n행 → ERR$_…` 줄 옆이 아니라, **작업별 표 행을 클릭하면 아래 보조 줄에 오류 번호별 요약**(`ORA-12899 ×30 · ORA-01400 ×7`)을 지연 조회해 보임(Rejected > 0이고 실행이 끝난 작업만, 한 번 읽으면 캐시, 읽기 실패는 조용히 무시).

## 6. 시험·그림
- 논리(`Logic/RunLogic.cs`, WPF 없는 순수 함수): 상태 → 버튼 켬·끔 표, 통계 문자열(속도·경과·ETA·`%`), 파이프라인 카드 문구, 작업별 표 행 모델, 로그 거르기·꼬리표 분류, 시작 흐름 판정(어느 팝업이 먼저 뜨나 — 검증 없음/ERROR/stale/파괴적/RESUME 체크포인트 없음 조합), 단계·상태줄 요약, 체크포인트 카드 `WHERE` 문자열.
- **`RunPresenter` 통합 시험**: `FakeAgentClient`(P6b `AgentClient`의 인터페이스 `IAgentClient`를 뽑아 — 이 작업에서 추출 허용, 기존 시험 불변)로 이벤트 폭주(초당 5,000 로그·1,000 snapshot)를 보내도 UI 갱신이 100ms 틱으로 묶이고 로그 버퍼가 2,000줄을 안 넘으며 UI 스레드 점유가 한 틱 10ms 미만(Stopwatch로 측정해 보고).
- **실제 end-to-end**(OracleIT 아님, 수동 + DevHost): DevHost에 `--real-run`(실제 Oracle `IT_LOCAL`, 실제 에이전트) 모드. `BIG_SRC`를 DevHost 화면에서 이관하고, 창을 닫고 다시 열어 다시 붙는 것까지 보고서에 단계별 기록·로그·PNG(진행 중 3장: 시작 직후·50%·완료).
- DevHost 그림(`docs/dev/reports/P6c-shots/`, black·light, 1100×700): `run-idle`(예제 작업, 계획 미리보기), `run-running`(`--fake-oracle`+가짜 에이전트 스냅숏 68%), `run-paused`, `run-done`, `run-failed`, `run-popup-destructive`, `run-popup-blocked`, `validation-post`(MATCH·PASS 목록), `settings-agent-live`. `--fake-run <state>` 옵션으로 `FakeAgentClient`가 해당 상태의 스냅숏을 고정해서 보냄.

## 7. 완료 기준
```powershell
dotnet build MigrationStudio.sln -c Release --nologo --no-incremental     # 경고 0
dotnet test tests/MigrationStudio.Tests -c Release --nologo
$env:ORACLE_IT_DSN = "localhost:1521/xe"; dotnet test tests/MigrationStudio.OracleIT -c Release --nologo
dotnet publish src/MigrationStudio -c Release --nologo
dotnet run --project tools/DevHost -c Release -- --seed --fake-oracle --fake-run running --theme black --shot docs/dev/reports/P6c-shots/black-run-running.png --size 1100x700 --step 5
```
보고서 `docs/dev/reports/P6c-report.md`: 실측(UI 틱 비용·로그 폭주), end-to-end 기록, POC와 다른 점, 설계 공백(P02·P05의 "이관 밖 행" 처리 선택 근거), 그림 목록.
