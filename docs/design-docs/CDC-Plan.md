# CDC(변경 데이터 동기화) 구현 플랜

| 항목 | 내용 |
|---|---|
| 상태 | **초안 — 결정 대기**(6장의 질문에 답이 나와야 작업 지시서로 쪼갠다) |
| 작성일 | 2026-10-08 |
| 관련 | 기능 설계서 [README.md](README.md) 결정 사항 #7("CDC 1차 제외, 화면 자리만"), UI-MIG-001(이관 전략), UI-MIG-006(실행) |

---

## 1. 지금 상태 (코드 기준 사실)

| 사실 | 위치 |
|---|---|
| `Strategy.Mode`(`FULL`·`INCREMENTAL`·`CDC`)와 `IncrementalBy`는 작업 파일에 저장·화면에 표시만 되고 **엔진·플래너 어디서도 읽지 않는다** | `Model/MigrationJob.cs`, `Jobs/JobFile.cs`, `Ui/Pages/ConnectionPage.cs` `RebuildStrategy()` |
| 엔진이 아는 실행 모드는 `RunSpec.RunMode` = `DRY`·`EXECUTE`·`RESUME`뿐. "증분 이관"도 사실상 미구현이고, 있는 것은 **중단된 실행을 체크포인트 값부터 재개**하는 기능이다 | `Engine/RunPlanner.cs` `BuildAsync`, `Engine/MigrationEngine.cs` |
| 원본 읽기 SQL은 `체크포인트열 > :LAST_ID ORDER BY 체크포인트열` 한 번 흐르고 끝난다(1회성 파이프라인). 반복 주기·상한(watermark 상한) 개념이 없다 | `Adapters/Oracle/Engine/OracleSourceFactory.cs` `BuildSql`, `Sql/SqlGenerator.cs` `BuildSourceSelect` |
| 대상 쓰기는 INSERT·MERGE·TRUNCATE+INSERT·DELETE+INSERT. **키 하나를 지우는 경로(DELETE 전파)는 없다** | `Adapters/Oracle/Engine/OracleTargetFactory.cs` |
| 원본 세션은 `SET TRANSACTION READ ONLY`. 원본에 DDL·DML을 하지 않는 것이 안전장치 원칙 | `OracleConnectionHelper.ApplySourceSession`, 설계서 7.3 |
| 에이전트는 실행이 끝나면 `done`·`stopped`·`failed`로 종료 코드를 내고 `--idle-exit`초 뒤 나간다. "끝나지 않는 실행"을 전제하지 않는다 | `MigrationAgent/AgentHost.cs`, `Hosting/HostingContracts.cs` `AgentRunInfo.State` |
| 동시 실행은 기본 1개(`AgentSettings.MaxConcurrent = 1`). CDC가 상시 돌면 다른 이관을 못 돌린다 | `Settings/SettingsModels.cs`, `Hosting/RunGuard.cs` |
| `MIG_RUN.RUN_MODE VARCHAR2(10)` — 값 `CDC`는 들어가지만 상태 `running`이 몇 주 지속되는 행이 생긴다 | `OracleControlStore.cs` |
| 변환식은 **원본 SELECT 안에서 Oracle이 계산**한다(설계서 4.4). 행을 SELECT로 읽지 않는 방식(LogMiner 등)은 변환식을 따로 처리해야 한다 | `Sql/SqlGenerator.WriteColumns` |

결론: "CDC 모드 추가"는 플래그 하나를 켜는 일이 아니라, (1) 증분 이관 자체를 먼저 만들고, (2) 엔진을 1회성에서 반복 주기형으로 바꾸고, (3) 변경 원천을 하나 고르는 세 덩어리다.

---

## 2. 변경 원천 선택지와 허점

"CDC"라는 말이 가리킬 수 있는 방식은 다섯 가지이고, 서로 요구 권한·감지 범위·공수가 전혀 다르다. **어느 것을 뜻하는지 먼저 정해야 한다.**

| | A. 쿼리 폴링(수정시각·시퀀스 열) | B. `ORA_ROWSCN` 폴링 | C. 플래시백 버전 조회 | D. LogMiner | E. 원본 트리거 + 변경 로그 테이블 |
|---|---|---|---|---|---|
| 감지 | INSERT·UPDATE | INSERT·UPDATE | INSERT·UPDATE·**DELETE** | INSERT·UPDATE·**DELETE** | INSERT·UPDATE·**DELETE** |
| 원본에 필요한 것 | 믿을 수 있는 수정시각/시퀀스 열 + 그 열의 인덱스 | 없음(단, 테이블이 `ROWDEPENDENCIES`가 아니면 블록 단위라 과다 감지) | 테이블 `FLASHBACK` 권한, 충분한 `UNDO_RETENTION` | ARCHIVELOG, 보조 로깅(최소 + 테이블별 PK), `EXECUTE ON DBMS_LOGMNR`·`LOGMINING`·`SELECT ANY TRANSACTION`·`V$` 뷰 권한. 멀티테넌트는 버전에 따라 CDB$ROOT 공용 사용자 필요 | **원본에 DDL(트리거·테이블)** — 읽기 전용 원칙 위반, 운영 DBA 승인 |
| SQL 원본(JOIN) 매핑 | 가능(결과 열에 수정시각이 있으면) | 불가 | 불가 | 불가(테이블 단위) | 불가 |
| 변환식 | 기존 SELECT 경로 그대로 | 그대로 | 그대로(`VERSIONS` 절만 추가) | 재조회 설계 필요(4장) | 재조회 설계 필요 |
| 치명적 허점 | ① 수정시각이 안 바뀌는 UPDATE·DELETE 놓침 ② **커밋 지연**: 수정시각은 과거인데 커밋이 늦은 행을 워터마크가 지나쳐 놓침(지연 창으로 완화, 완전 해결 아님) ③ 원본 시계 기준이어야 함(클라이언트 시계 쓰면 틀림) | `ORA_ROWSCN`에 인덱스를 못 걸어 **매 주기 풀 스캔**. 블록 단위 SCN이면 한 행 바꿔도 블록 전체가 다시 옴 | ① `UNDO_RETENTION`(보통 15분~수시간)보다 오래 멈추면 **ORA-01555 → 전체 재동기화**뿐 ② 매 주기 풀 스캔 수준 비용 ③ DDL 후 버전 조회 불가 | ① 19c부터 `CONTINUOUS_MINE` 지원 종료 → 로그 파일 목록을 직접 관리 ② RMAN이 아카이브를 지우면 **빈틈 → 전체 재동기화** ③ 운영 DB에서 이 권한을 받는 것 자체가 어려움 ④ LOB·LONG·XMLTYPE 제한, DDL 처리 ⑤ `V$LOGMNR_CONTENTS`는 SQL 문자열이라 파싱 필요 | 원본 성능 영향, 트리거 누락 시 조용히 어긋남, 원본 변경 승인 |
| 공수(상대) | 1 | 1 | 1.5 | 4~5 | 2 + 운영 협의 |

GoldenGate는 라이선스 제품이라 연동 대상으로만 둘 수 있고(이 플러그인이 캡처를 하지 않음), 이 플랜에서는 뺀다.

### 추천과 그 근거의 약점

- **추천: A(쿼리 폴링)를 "변경 동기화" 1단계로, C(플래시백)를 "삭제 감지" 옵션으로 얹는다.** 둘 다 원본에 손대지 않고(읽기 전용 원칙 유지), 기존 SELECT·MERGE·체크포인트 경로를 그대로 쓴다.
- 약점: A는 엄밀히 CDC가 아니라 "증분 반복"이다. 로그 기반 CDC(D)를 기대했다면 이 추천은 기대에 못 미친다. D는 운영 DB 권한·아카이브 운영 정책에 묶여 **플러그인 혼자 완결할 수 없고**, 개발 공수의 반 이상이 Oracle 운영 조건 처리에 들어간다. 그래도 D가 필요하면 4장처럼 "키 캡처 전용"으로 좁혀야 현실적이다.
- E(트리거)는 설계서 7.3 안전장치와 정면 충돌하므로, 사용자가 명시적으로 원하지 않는 한 선택지에서 제외할 것을 권한다.

---

## 3. 공통 구조 변경 (어느 원천을 고르든 필요)

### 3.1 모델·작업 파일
- `MigrationStrategy`에 추가(모두 선택 필드, `JobFile` 읽기는 `TryGetProperty`라 v2 유지 가능):
  - `PollIntervalSeconds`(기본 60), `LagSeconds`(지연 창, 기본 300), `DeleteMode`(`NONE`·`SOFT_COLUMN`·`FLASHBACK`), `CdcSource`(`QUERY`·`FLASHBACK`·`LOGMINER`).
  - `IncrementalBy`의 뜻을 확정: 매핑의 `CheckpointColumn`이 곧 증분 기준이다. 전략 수준 `IncrementalBy`(PK·Timestamp·Sequence·SCN)는 **힌트/기본값**으로만 두거나 없앤다(둘 다 두면 어느 쪽이 맞는지 모호 — 현재 코드가 그 상태).
- 작업 파일 예제(`JobSamples`)·YAML 쓰기(`JobFile` 748행 근처)·골든 테스트 영향 확인. 골든은 바꾸지 않고 새 필드는 기본값일 때 쓰지 않는다.

### 3.2 엔진: 1회성 → 주기형
- `RunSpec.RunMode`에 `SYNC`(가칭) 추가. `MigrationEngine.RunAsync`는 `SYNC`일 때 **주기 루프**: `[계획 갱신 → 작업별 1회 증분 실행 → 워터마크 저장 → 대기(PollInterval)]`를 `Stop`까지 반복.
- 상태 추가: `waiting`(다음 주기 대기). `Pause`는 주기 경계에서 멈추고, `Stop`은 진행 중 배치를 커밋한 뒤 끝낸다(지금과 같은 경계 규칙).
- 스냅샷(`RunSnapshot`)에 `Cycle`(회차), `LastCycleAt`, `NextCycleAt`, `LagSeconds`(원본 현재 시각 − 워터마크), 회차별 `Inserted/Updated/Deleted`.
- 워터마크 = 기존 `CheckpointRecord`를 그대로 쓴다(`CP_COLUMN`·`CP_VALUE`, 상태 `syncing`). 대상 DB 저장소면 배치와 같은 트랜잭션이라 정확히 한 번, 로컬 파일이면 한 배치 재처리(MERGE라 결과 같음) — 지금 설계 6.5와 동일한 성질.
- 병렬 작업자: 주기당 변경량이 작으므로 **SYNC에서는 작업자 1로 고정**(범위 분할은 첫 적재에만).

### 3.3 쓰기 방식 제약
- SYNC는 **MERGE만 허용**. INSERT ONLY는 두 번째 주기부터 중복 키, TRUNCATE/DELETE+INSERT는 매 주기 대상을 비운다. 검증에서 ERROR.
- DELETE 전파용 `ITargetSession.DeleteKeysAsync(item, keys)` 추가(`OracleTargetFactory`에 `DELETE … WHERE 키 = :k` 배열 바인딩). `DeleteMode = SOFT_COLUMN`이면 삭제 대신 지정 열 UPDATE.

### 3.4 에이전트·호스트
- `AgentHost`: `SYNC`는 `done`이 없다. 종료는 `stopped`(사용자) 또는 `failed`만. `--idle-exit`는 적용하지 않는다.
- `AgentRunInfo.State`에 `syncing` 추가 → 다시 붙기 목록·`RunPage` 배지·`MIG_RUN.STATUS` 매핑.
- **위험**: `OnHostExit = CONTINUE`(기본)이면 Folderss를 닫아도 CDC 에이전트가 무기한 돈다. 사용자가 잊은 에이전트가 운영 대상에 계속 쓴다. 대책: SYNC 시작 확인 창에 "창을 닫아도 계속 동기화합니다" 명시 + 설정 `Agent.MaxConcurrent`를 SYNC 1 + 일반 1로 분리하거나 SYNC 중에는 다른 실행을 막는다는 안내.
- `RunGuard`: 같은 작업 이름의 SYNC가 돌면 같은 작업의 EXECUTE를 막는다(워터마크 충돌).

### 3.5 검증(실행 전 C14~)
| 코드(가칭) | 내용 | 수준 |
|---|---|---|
| C14 | SYNC인데 쓰기 방식이 MERGE가 아님 / 병합 키 없음 | ERROR |
| C15 | 증분 기준 열 없음·형식이 DATE/TIMESTAMP/NUMBER 아님·NULL 허용 열 | ERROR / WARN(NULL 허용) |
| C16 | 증분 기준 열에 인덱스 없음(매 주기 풀 스캔) | WARN |
| C17 | 원본·클라이언트 시계 차(원본 `SYSTIMESTAMP`와 비교, 정보성) | INFO |
| C18 | `DeleteMode = FLASHBACK`: `FLASHBACK` 권한, `UNDO_RETENTION` 값, `PollInterval < UNDO_RETENTION/2` | ERROR / WARN |
| C19 | LogMiner 선택 시: ARCHIVELOG, 보조 로깅, 권한, 컨테이너 종류 | ERROR |

### 3.6 화면
- `ConnectionPage.RebuildStrategy()`: `CDC` 세그먼트 선택 시 안내문 대신 폴링 주기·지연 창·삭제 처리·변경 원천 필드. 라벨은 "변경 동기화"로 바꾸는 편이 정직하다(A 방식은 로그 기반 CDC가 아니므로).
- `RunPage`: 회차·지연·다음 주기 카운트다운, "동기화 중지" 버튼(Stop과 같음), 체크포인트 카드에 워터마크 표시.
- `README.md` 한계 항목("CDC 미지원") 갱신, 설계서 결정 사항 #7 갱신.

---

## 4. 원천별 상세

### 4.1 A. 쿼리 폴링 (1단계)
주기 1회 알고리즘(작업 하나 기준):
1. 원본에서 `SELECT SYSTIMESTAMP FROM DUAL` → `Upper = 원본현재시각 − LagSeconds`(클라이언트 시계는 쓰지 않는다).
2. `WHERE 열 > :LOWER AND 열 <= :UPPER ORDER BY 열` — `LOWER = 워터마크 − LagSeconds`(겹쳐 읽기). 겹친 행은 MERGE라 결과가 같다.
3. 배치마다 MERGE + 체크포인트(워터마크 = 읽은 마지막 값, 단 `Upper`를 넘지 않음).
4. 변경 0건이면 워터마크를 `Upper`로 올린다(그래야 지연 계산이 맞는다).

허점을 그대로 적어 둔다:
- 수정시각을 안 바꾸는 UPDATE, 물리 DELETE는 **영원히 못 본다**. 삭제가 필요하면 `SOFT_COLUMN`(원본에 삭제 플래그가 있을 때) 또는 4.2.
- 지연 창은 "보통의 커밋 지연"만 덮는다. 긴 트랜잭션(배치 작업이 30분 열어 두는 경우)은 창을 그만큼 키워야 하고, 그만큼 매 주기 재읽기 비용이 는다. 창 크기는 설정이지 해법이 아니다.
- `DATE` 열은 초 단위라 같은 초에 들어온 행을 경계에서 나눌 수 없다 → `>` 대신 `>=`로 겹쳐 읽는 것이 필수(2번의 LOWER가 그 역할).
- SQL 원본(JOIN)은 "조인된 어느 쪽이 바뀌어도 결과 행이 바뀐다"를 수정시각 하나로 표현할 수 없다. SQL 원본은 `GREATEST(a.UPD, b.UPD) AS UPD_AT` 같은 결과 열을 사용자가 만들었을 때만 허용하고 WARN을 낸다.

### 4.2 C. 플래시백 버전 조회 (삭제 감지 옵션)
- 주기마다 `SELECT 키열, VERSIONS_OPERATION, VERSIONS_ENDSCN FROM 원본 VERSIONS BETWEEN SCN :LAST AND :NOW WHERE VERSIONS_OPERATION = 'D'`로 삭제 키만 뽑아 대상에서 지운다. 워터마크는 SCN(`DBMS_FLASHBACK.GET_SYSTEM_CHANGE_NUMBER` 또는 `V$DATABASE.CURRENT_SCN`, 권한 없으면 `TIMESTAMP_TO_SCN`).
- INSERT·UPDATE까지 이 경로로 가져올 수도 있으나(수정시각 열이 없는 테이블에 유효), 매 주기 비용이 풀 스캔급이라 **삭제 감지 전용**으로 쓰는 것을 전제한다.
- 멈춘 시간이 `UNDO_RETENTION`을 넘으면 ORA-01555. 이때 할 수 있는 것은 "전체 재적재(TRUNCATE+INSERT 또는 MERGE 전체)"뿐이고, 사용자에게 선택 창을 띄운다(자동 재적재 금지 — 운영 대상이면 위험).

### 4.3 D. LogMiner (요구가 분명할 때만, 별도 단계)
현실적인 범위는 **"어느 키가 바뀌었나"만 LogMiner로 캡처하고, 값은 기존 SELECT 경로로 재조회**하는 하이브리드다. 이유: 변환식·형식 변환·NULL 처리를 모두 Oracle SELECT에 맡긴 현재 구조를 유지할 수 있고, `SQL_REDO` 문자열에서 전체 값을 파싱하지 않아도 된다.

주기 1회:
1. `V$DATABASE.CURRENT_SCN`으로 상한. `V$ARCHIVED_LOG`·`V$LOG`에서 `[워터마크 SCN, 상한]`을 덮는 로그 파일 목록을 구해 `DBMS_LOGMNR.ADD_LOGFILE`(19c는 `CONTINUOUS_MINE`이 없으므로 필수).
2. `START_LOGMNR(STARTSCN, ENDSCN, OPTIONS = DICT_FROM_ONLINE_CATALOG + COMMITTED_DATA_ONLY)`.
3. `V$LOGMNR_CONTENTS`에서 `SEG_OWNER`·`TABLE_NAME`이 매핑 대상인 행의 `OPERATION`·`ROW_ID`·`SQL_REDO`를 읽는다. INSERT·UPDATE는 `ROW_ID`로 원본을 재조회(`WHERE ROWID IN (...)`), DELETE는 `SQL_REDO`의 WHERE절에서 PK 값만 파싱(테이블별 PK 보조 로깅이 켜져 있어야 PK가 WHERE절에 나온다).
4. `END_LOGMNR`, 워터마크 = 상한 SCN.

이 길의 허점:
- 운영 DB에서 `LOGMINING`·`SELECT ANY TRANSACTION`·`V$` 권한과 보조 로깅 DDL을 받아야 한다. 플러그인은 요구 사항을 **검사(C19)하고 DDL 문을 보여 줄 뿐 실행하지 않는다**(원본 읽기 전용 원칙).
- 아카이브 로그가 지워지면 빈틈. 감지는 되지만(필요 로그가 목록에 없음) 복구는 전체 재적재뿐.
- ROWID 재조회는 "지금 값"을 가져오므로 한 주기 안의 중간 상태는 잃는다(동기화 목적엔 괜찮지만 감사 로그 목적이면 부적합). 테이블 `MOVE`·파티션 이동으로 ROWID가 바뀌면 재조회가 빈다 → PK 파싱으로 대체해야 하고, 그러면 결국 `SQL_REDO` 파서가 필요하다.
- 멀티테넌트에서 PDB 접속만으로 마이닝이 안 되는 버전이 있다(CDB$ROOT 공용 사용자 필요). 접속 프로필 하나로 끝나지 않는다.
- `V$LOGMNR_CONTENTS` 조회는 매 주기 지정 구간의 redo를 전부 읽는다. 변경이 많은 DB에서 주기를 짧게 두면 원본에 부담.
- 통합 시험: 현재 `ORACLE_IT_DSN` 컨테이너(gvenzl 류 XE)는 NOARCHIVELOG가 기본이라 **시험 환경부터 만들어야** 한다(SYSDBA로 ARCHIVELOG 전환·보조 로깅).

---

## 5. 단계별 작업 분해와 완료 조건

각 단계는 독립 PR 하나로, 완료 조건은 자동 시험으로 판정한다.

### P8-a 증분 이관을 진짜로 만들기 (선행, 원천 무관)
- `RunMode = INCREMENTAL`(1회성): 워터마크 이후만 읽고 끝에 워터마크 저장. 지금의 `RESUME`과 다른 점은 "완료된 작업도 다시 돌릴 수 있고, 체크포인트가 `done`이어도 그 값부터 읽는다".
- `Strategy.Mode`가 엔진에 실제로 반영되게 연결(`RunLauncher`/`RunPlanner`), 모드 `INCREMENTAL`인데 `CheckpointColumn` 없는 매핑은 검증 ERROR.
- 완료 조건: `MigrationEngineTests`에 "1차 실행 후 원본에 3행 추가·2행 수정 → 증분 실행이 그 5행만 MERGE하고 워터마크가 최댓값으로 바뀐다" (`MemoryFakes` 사용). 기존 테스트 전부 통과.

### P8-b 주기 루프 + 상태 + 에이전트
- 3.2·3.4의 엔진·에이전트 변경. UI는 최소(실행 방식 세그먼트에 "변경 동기화", RunPage 회차·지연 표시).
- 완료 조건: `MigrationEngineTests` — 가짜 시계로 2주기 돌려 각 주기가 그 사이 변경만 쓰는지, `Stop`이 배치 경계에서 멈추고 워터마크가 보존되는지. `AgentIntegrationTests` — SYNC 상태로 다시 붙기, 호스트 종료 후에도 살아 있음.

### P8-c 쿼리 폴링 완성(지연 창·원본 시계·검증 C14~C17)
- 4.1 전부, `OracleSourceFactory`에 `:UPPER` 바인딩, SQL 원본 WARN.
- 완료 조건: `SqlGenerator` 골든 외 신규 테스트로 생성 SQL 확인(`> :LOWER AND <= :UPPER`), 커밋 지연 시나리오(가짜 원본에서 "수정시각은 과거·가시화는 다음 주기"인 행이 지연 창 안에서 잡힘), `ValidationEngineTests`에 C14~C17. Oracle IT: `DATE` 경계 같은 초 행 누락 없음.

### P8-d 삭제 전파(소프트 열 → 플래시백)
- `DeleteKeysAsync`, `SOFT_COLUMN`, 그 다음 `FLASHBACK`(C18, ORA-01555 → 선택 창).
- 완료 조건: 가짜 대상에서 삭제 키가 지워지는 테스트, Oracle IT에서 `VERSIONS BETWEEN SCN`으로 삭제 1건 감지.

### P8-e LogMiner (선택, 6장 답에 따라)
- 4.3. 시험 환경 구축이 선행 작업.

### 손대는 파일 (P8-a~d)
`Model/Codes.cs`, `Model/MigrationJob.cs`, `Jobs/JobFile.cs`, `Jobs/JobSamples.cs`, `Engine/EngineModels.cs`, `Engine/RunPlanner.cs`, `Engine/MigrationEngine.cs`, `Hosting/RunLauncher.cs`, `Hosting/HostingContracts.cs`, `Hosting/RunGuard.cs`, `Adapters/Oracle/Engine/OracleSourceFactory.cs`, `Adapters/Oracle/Engine/OracleTargetFactory.cs`, `Sql/SqlGenerator.cs`, `Validation/ValidationEngine.cs`, `MigrationAgent/AgentHost.cs`, `Ui/Pages/ConnectionPage.cs`, `Ui/Pages/RunPage.cs`, `Logic/RunLogic.cs`, `tests/.../Engine/Fakes/MemoryFakes.cs`(시계·변경 주입), `README.md`, `docs/design-docs/README.md`.

---

## 6. 결정이 필요한 질문

플랜의 절반은 이 답에 달려 있다. 답 없이 코드를 시작하면 추측으로 간다.

1. **"CDC"가 뜻하는 것**: 수정시각 기반 주기 동기화(A)로 충분한가, 로그 기반(D)이 요구인가? 요구가 D라면 원본 운영 DBA에게 ARCHIVELOG·보조 로깅·`LOGMINING` 권한을 받을 수 있는가?
2. **삭제 전파가 필요한가?** 필요하면 원본에 삭제 플래그 열이 있는가(SOFT), 없으면 플래시백(C)으로 가도 되는가(UNDO_RETENTION 값 확인 필요).
3. **원본에 어떤 변경도 못 하는가?** (트리거·보조 로깅 DDL 모두 불가인지) — 불가면 E와 D는 사실상 제외.
4. **대상 Oracle 버전·멀티테넌트 여부**(원본 쪽): 19c PDB면 LogMiner 접속 방식이 달라진다.
5. **운영 방식**: 동기화를 몇 분 주기로, 얼마나 오래(마이그레이션 전환 기간 며칠 vs 상시 복제) 돌릴 것인가? 상시 복제면 Folderss 플러그인(사용자 PC의 하위 프로세스)이 맞는 자리인지부터 의문이다 — PC가 꺼지면 멈춘다.
6. **라벨**: A로 간다면 화면 라벨을 "CDC" 대신 "변경 동기화"로 바꾸는 데 동의하는가(기대치 관리).
