# Oracle Migration Studio 기능 설계서

| 항목 | 내용 |
|---|---|
| 대상 | Folderss 플러그인 **Migration Studio** (예정 id `zaruous.folderss-oracle-migration`) |
| 문서 버전 | 0.3 — 화면 설계 POC 기준(원본 SQL 통합 · 자체 접속 설정 · 하위 프로세스 실행 반영) |
| 작성일 | 2026-10-03 |
| 화면 POC | [`design/poc/index.html`](../../design/poc/index.html) (Mock 데이터로 동작, 실행 방법은 [10장](#10-poc-실행)) |
| 참고 | DB Helper 플러그인(`Folderss-oracle-db-helper`)의 화면 틀·테마·안전장치 규칙을 따른다(접속 정보는 공유하지 않음) |

### 변경 이력

| 버전 | 내용 |
|---|---|
| 0.1 | 최초 작성 — 6단계 화면, 엔진·검증·작업 파일 |
| 0.3 | 구현 중 보강: `MIG_CHECKPOINT`에 `RANGE_FROM`·`RANGE_TO` 추가(병렬 작업자 범위별 재개), 구현 지시서 `docs/dev/` 추가 |
| 0.2 | ① 실행 엔진을 **하위 프로세스(MigrationAgent.exe)** 로 확정(4.5) ② 접속은 **플러그인 자체 "마이그레이션 설정"** 에 정의, DB Helper와 공유 안 함(UI-MIG-008) ③ 매핑의 원본을 **테이블 또는 SQL** 로 통합 — SQL 원본도 컬럼 매핑·변환식·체크포인트를 똑같이 씀(작업 파일 v2) ④ **SQL은 단계에서 빼고 별도 도구 "SQL 원본 편집기"** 로 — 단계는 접속 → 테이블 매핑 → 컬럼 매핑 → 검증 → 실행 5개 ⑤ 체크포인트 저장소 설명 보강(6.5) |

---

## 1. 목적과 범위

서로 다른 Oracle 접속·스키마·테이블·컬럼 구조 사이에서, 사용자가 매핑을 정의해 데이터를 옮기는 **가벼운 ETL / 이관 도구**를 Folderss 플러그인으로 만든다. 같은 테이블을 그대로 복사하는 도구가 아니다.

```text
원본 Oracle  LEGACY_APP.SRC_CUSTOMER          (또는 SELECT … FROM SRC_CUSTOMER C JOIN SRC_CUSTOMER_GRADE G …)
      ↓  매핑 / 변환(Transform)
대상 Oracle  NEXT_APP.TB_MEMBER
```

원본과 대상은 DB 서버·서비스명·스키마·테이블 이름·컬럼 이름·컬럼 형식·기본 키·데이터 구조가 모두 다를 수 있다.

핵심 기능 네 가지:

1. 서로 다른 DB 접속 간 이관
2. 서로 다른 테이블·컬럼 구조 매핑(이름 자동 매칭 + 변환식)
3. **원본을 테이블 대신 SQL(SELECT)로** — JOIN, CASE/DECODE, 집계, N개 원본 → 1개 대상. SQL 결과를 테이블처럼 써서 컬럼 매핑·변환식·체크포인트를 그대로 적용
4. 대용량 이관: Streaming + Batch + Checkpoint + Resume, 하위 프로세스에서 실행(창을 닫아도 계속)

범위 밖(1차): CDC(로그 기반 변경 동기화) 실행, Oracle 외 DB 실행(어댑터 자리만 둠), 스케줄 실행.

---

## 2. 화면 구성

### 2.1 공통 틀

Folderss `⋯ 메뉴 > 플러그인 > Migration Studio`로 여는 팝업(기본 900×600, 크기 조절 가능). DB Helper와 같은 두 줄 막대(메뉴 막대 + 아이콘 막대)를 쓰고, 왼쪽에 단계 막대를 둔다.

```text
+----------------------------------------------------------------------------------------------+
| 파일(F)  매핑(M)  실행(R)  보기(V)  도움말(H)                                     ← 메뉴 막대 |
| [새][열기][저장] | [접속시험][메타 새로고침] | [검증][DryRun] | [▶][⏸][⏹][↻] | [{} SQL][📄][⚙]  작업: CUSTOMER_MIGRATION * |
+------------------+---------------------------------------------------------------------------+
| 이관 작업         | STEP 3  컬럼 매핑                                        [동작 버튼 ...] |
| CUSTOMER_MIGRAT.. | 설명 한 줄                                                               |
| [LEGACY_PROD]→[NEXT_PROD]                                                                    |
|------------------| +-----------------------------------------------------------------------+ |
| (1) 접속          | |  카드 · 그리드 · 편집기 (화면별 본문)                                 | |
| (✓) 테이블 매핑   | |                                                                       | |
| (!) 컬럼 매핑     | |                                                                       | |
| (4) 검증          | +-----------------------------------------------------------------------+ |
| (5) 실행          |                                                                           |
|                   | [‹ 이전 단계]          안내 문구                       [다음: 검증]      ← 고정 |
| 📄 작업 정의      |                                                                           |
+------------------+---------------------------------------------------------------------------+
| ● [LEGACY_PROD] → ● [NEXT_PROD] | 실행 중 · 850,000 / 1,240,325 행 (68%)   Mock 어댑터 · POC |
+----------------------------------------------------------------------------------------------+
```

- **단계 막대**: 번호 원 + 이름 + 요약 한 줄. 상태 색 — 완료(초록 ✓), 경고(주황), 오류(빨강 !), 진행 중(회전), 지금 화면(강조색 바탕). 너비 1000px 이하에서는 번호만 보이게 접는다(팝업 기본 크기에서 표 공간 확보).
- **화면 머리**: `STEP n` 꼬리표 + 제목 + 설명, 오른쪽에 화면 동작 버튼.
- **화면 바닥(고정)**: 이전 단계 / 안내 문구 / `다음: …` 주 버튼.
- **상태줄**: 원본·대상 접속 점(시험 결과)과 DB 배지, 실행 상태·진행률, 어댑터 이름.
- **단계는 진행 순서만**: 접속 → 테이블 매핑 → 컬럼 매핑 → 검증 → 실행. 순서와 상관없이 꺼내 쓰는 기능은 단계 막대에 두지 않고 도구로 연다.
- **{} SQL 원본 편집기**(도구, `Ctrl+Q`): 원본이 SQL인 매핑의 SELECT 문을 쓰고 검증·미리보기 하는 별도 창(UI-MIG-004). 테이블 매핑의 SQL 원본 이름, 컬럼 매핑 머리, 검증 조치 링크, 매핑 메뉴, 아이콘 막대에서 연다.
- **⚙ 마이그레이션 설정**(도구, 아이콘 막대·파일 메뉴): 접속 목록·기본값·실행 에이전트(UI-MIG-008).
- 각 단계는 독립 화면이지만 한 작업(Job)을 공유하므로 이전 단계의 설정이 유지된다. 바뀐 작업은 제목 뒤 `*`로 표시하고 임시 저장한다.

### 2.2 작업 흐름

```text
단계:  ① 접속 ──▶ ② 테이블 매핑 ──▶ ③ 컬럼 매핑 ──▶ ④ 검증 ──▶ ⑤ 실행
          │              │                  │
도구:  ⚙ 마이그레이션 설정  {} SQL 원본 편집기 ◀─┘   (원본이 SQL인 매핑만, 필요할 때 연다)

① 설정의 접속 고르기 → 메타데이터  ② 원본(테이블 | SQL) → 대상 테이블  ③ 컬럼 자동 매핑 → 변환식·NULL 처리
④ 실행 전 검증  ⑤ Dry Run → 이관(하위 프로세스) → 실행 후 검증
```

---

## 3. 문서 목록

| 기능ID | 화면 | 문서 |
|---|---|---|
| UI-MIG-001 | 접속 · 이관 전략 | [UI-MIG-001_Design.md](UI-MIG-001_Design.md) |
| UI-MIG-002 | 테이블 매핑 — 원본(테이블·SQL) ↔ 대상 (+ 매핑 추가·자동 매칭 팝업) | [UI-MIG-002_Design.md](UI-MIG-002_Design.md) |
| UI-MIG-003 | 컬럼 매핑 · 변환식 · NULL 처리 | [UI-MIG-003_Design.md](UI-MIG-003_Design.md) |
| UI-MIG-004 | SQL 원본 편집기 (도구 창) — SELECT 작성 · 검증 · 미리보기 · Alias 매핑 | [UI-MIG-004_Design.md](UI-MIG-004_Design.md) |
| UI-MIG-005 | 검증 (실행 전 · 실행 후) | [UI-MIG-005_Design.md](UI-MIG-005_Design.md) |
| UI-MIG-006 | 실행 · 진행 · 로그 · 체크포인트 (하위 프로세스) | [UI-MIG-006_Design.md](UI-MIG-006_Design.md) |
| UI-MIG-007 | 작업 정의 · 매핑 템플릿 (팝업) | [UI-MIG-007_Design.md](UI-MIG-007_Design.md) |
| UI-MIG-008 | 마이그레이션 설정 — 접속 · 기본값 · 실행 에이전트 (팝업 + Folderss 설정 탭) | [UI-MIG-008_Design.md](UI-MIG-008_Design.md) |

> 각 문서의 **서비스 처리**에 적은 `서비스주소`(`/v1/migration/<그룹>/<서비스>`)는 HTTP 주소가 아니라 **논리 주소**다. 플러그인 안에서는 같은 이름의 C# 서비스 메서드를 직접 부르고, 실행(`run/*`)은 하위 프로세스와 이름 있는 파이프로 주고받는 메시지 이름이 된다(4.3·4.5).

---

## 4. 아키텍처

### 4.1 계층과 프로세스

```text
┌─ Folderss.exe (플러그인 DLL) ───────────────────────┐        ┌─ MigrationAgent.exe (하위 프로세스) ──────┐
│ Ui (WPF)   화면·메뉴·그리드·편집기                    │        │ 실행 사양을 받아 이관 실행                │
│   │                                                  │  이름  │   MigrationEngine (스트리밍·배치·체크포인트)│
│ Core       작업 모델·매핑·SQL 생성·검증·설정          │ ◀────▶ │   Core (같은 DLL)                          │
│ Adapters   DatabaseAdapter ─ OracleAdapter            │  있는  │   Adapters ─ OracleAdapter                 │
│            (메타데이터·검증·미리보기는 여기서)         │ 파이프 │ 진행·로그·체크포인트 이벤트를 보냄         │
└──────────────────────────────────────────────────────┘        └───────────────────────────────────────────┘
```

- **Core**(`net8.0`, UI 없음)는 두 프로세스가 같은 DLL을 쓴다 → 단위 테스트 대상(DB Helper처럼 Linux에서도 테스트).
- 메타데이터 조회·SQL 검증·미리보기처럼 짧은 일은 플러그인 안에서, **이관 실행만 하위 프로세스**에서 한다(4.5).
- DB 종류 확장: `DatabaseAdapter`(공통) ─ `OracleAdapter`(Oracle.ManagedDataAccess.Core) / `PostgreSqlAdapter`(Npgsql) / `SqlServerAdapter`(Microsoft.Data.SqlClient) / `MySqlAdapter`(MySqlConnector, MariaDB 겸용) — 나머지는 예정.

### 4.2 POC 파일 ↔ WPF 구현 대응

| POC (`design/poc/js`) | WPF 구현(예정, `src/MigrationStudio`) | 비고 |
|---|---|---|
| `app/app.js` 셸·메뉴·아이콘·단계 막대 | `Ui/MigrationView.cs`, `Ui/ShellMenu.cs`, `Ui/StepRail.cs` | DB Helper `DbHelperView`·`ShellMenu` 틀 재사용 |
| `ui/components.js` | `Ui/Theme.cs`, `Ui/ShellUi.cs` | `btn primary`→`ShellUi.PrimaryButton`, `dbBadge`→`Theme.DbBadge` |
| `pages/connection.js` … `pages/run.js` | `Ui/Pages/ConnectionPage.cs` … `RunPage.cs` | 단계 화면 하나 = UserControl 하나(5개) |
| `pages/sql-editor.js` | `Ui/SqlSourceEditorWindow.cs` | 도구 창(비모달 Window) — DB Helper `TableInfoWindow`처럼 따로 뜸 |
| `pages/settings.js` | `Ui/MigrationSettingsView.cs` + `MigrationSettingsPage`(IPluginSettingsPage) | 대화상자와 Folderss 설정 탭이 같은 View |
| `ui/components.js` `sqlEditor` | `Ui/SqlEditor.cs` | DB Helper처럼 TextBox(고정폭)로 시작, 강조는 AvalonEdit 검토 |
| `backend/settings.js` | `Core/MigrationSettings.cs`, `Core/ConnectionProfile.cs` | DPAPI 암호화(이 플러그인 전용 엔트로피) |
| `backend/adapters.js` | `Adapters/IDatabaseAdapter.cs`, `Adapters/OracleAdapter.cs` | |
| `backend/mapping.js` | `Core/MappingService.cs` | 자동 매칭·형식 호환성·컬럼 검사 |
| `backend/expression.js` | `Core/ExpressionInspector.cs` (미리보기는 Oracle에 위임) | 4.4 참고 |
| `backend/sqlgen.js` | `Core/SqlGenerator.cs` | 원본 SELECT(SQL 원본은 인라인 뷰로 감쌈), MERGE/INSERT/TRUNCATE/DELETE |
| `backend/sql-mapping.js` | `Core/SqlSourceService.cs` | SQL 원본 해석·DESCRIBE·검증·미리보기, 결과 열 → 가상 테이블 |
| `backend/validation.js` | `Core/ValidationEngine.cs` | 실행 전·후 검증 규칙 |
| `backend/engine.js` | `Agent/MigrationEngine.cs` + `Ui/AgentClient.cs` | 엔진은 하위 프로세스, UI는 파이프 클라이언트 |
| `backend/job.js` | `Core/MigrationJob.cs`, `Core/JobFile.cs` | JSON·YAML 저장(v2), 매핑 템플릿, v1 자동 변환 |
| `app/store.js` (localStorage 임시 저장) | `DataDirectory\jobs\*.draft.json` | DB Helper SQL 임시 저장과 같은 방식 |

### 4.3 서비스 주소 규칙

`/v1/migration/<그룹>/<서비스>` — 그룹: `settings` · `connection` · `metadata` · `mapping` · `sql` · `validation` · `run` · `checkpoint` · `job`.

| 그룹 | C# 서비스 | 주요 메서드 | 실행 위치 |
|---|---|---|---|
| settings | `MigrationSettingsStore` | `Load`, `Save`, `ProtectPassword` | 플러그인 |
| connection | `IConnectionService` | `TestAsync` | 플러그인 |
| metadata | `IMetadataService` | `LoadAsync`, `ProfileColumnAsync` | 플러그인 |
| mapping | `MappingService` | `AutoMatchTables`, `AutoMapColumns`, `CheckColumn`, `Compat` | 플러그인 |
| sql | `SqlSourceService` | `ParseAsync`, `DescribeAsync`, `ValidateAsync`, `PreviewAsync` | 플러그인 |
| validation | `ValidationEngine` | `RunPreAsync`, `RunPostAsync` | 플러그인 |
| run | `AgentClient` ↔ `MigrationEngine` | `Start`, `Attach`, `Pause`, `Resume`, `Stop`, `Snapshot`(이벤트) | **하위 프로세스** |
| checkpoint | `ICheckpointStore` | `Get`, `Save`(배치 트랜잭션 안), `Clear` | 하위 프로세스(조회는 플러그인도) |
| job | `JobFile` | `Load`, `Save`, `ExportTemplate`, `ApplyTemplate` | 플러그인 |

모든 DB 호출은 `Task` + `CancellationToken`, UI 스레드 밖에서 실행한다(DB Helper 규칙: 백그라운드 예외는 반드시 잡는다 — 플러그인이 Folderss와 같은 프로세스).

### 4.4 변환식(Transform)은 어디서 계산하나

변환식은 **원본 SELECT 목록에 그대로 넣어 원본 Oracle이 계산**한다(서버 쪽 변환). 엔진이 행마다 식을 해석하지 않으므로 Oracle 함수(`REGEXP_REPLACE`, `DECODE`, `CAST` …)를 그대로 쓸 수 있고 빠르다. SQL 원본이면 사용자 SQL을 인라인 뷰로 감싸고 같은 방식으로 붙인다.

```sql
-- 테이블 원본
SELECT CUSTOMER_ID AS MEMBER_ID,
       TRIM(CUSTOMER_NM) AS MEMBER_NAME,                          -- 변환식
       NVL(CAST(REG_DT AS TIMESTAMP), SYSTIMESTAMP) AS CREATED_AT -- NULL 처리(SYSDATE)
FROM LEGACY_APP.SRC_CUSTOMER
WHERE CUSTOMER_ID > :LAST_ID
ORDER BY CUSTOMER_ID;

-- SQL 원본: 결과 별칭이 원본 컬럼, 바깥에 변환식·체크포인트
SELECT MEMBER_ID AS MEMBER_ID, NVL(USE_YN, 'Y') AS USE_YN, MEMBER_GRADE AS MEMBER_GRADE
FROM (
    SELECT C.CUSTOMER_ID AS MEMBER_ID, CASE WHEN C.STATUS_CD = 'A' THEN 'Y' ELSE 'N' END AS USE_YN, G.GRADE_NM AS MEMBER_GRADE
    FROM SRC_CUSTOMER C LEFT JOIN SRC_CUSTOMER_GRADE G ON G.GRADE_CD = C.GRADE_CD
    WHERE C.CUSTOMER_ID > :LAST_ID
) S
ORDER BY S.MEMBER_ID;
```

POC의 자바스크립트 식 엔진은 화면 미리보기용 흉내다. 실제 구현의 샘플 미리보기는 같은 SELECT에 `FETCH FIRST 6 ROWS ONLY`를 붙여 원본에서 실행한다.

### 4.5 실행 에이전트(하위 프로세스)

**결정**: 이관 실행은 Folderss 프로세스가 아니라 플러그인이 띄우는 하위 프로세스 `MigrationAgent.exe`에서 한다. 팝업이나 Folderss를 닫아도 이관은 계속되고, 다시 열면 진행 화면에 다시 붙는다.

**가능 근거(Folderss 코드 확인)**

| 항목 | 확인 내용 |
|---|---|
| .NET 런타임 | Folderss는 `dotnet publish`(self-contained 아님)로 배포 → 실행 PC에 .NET 8 Desktop Runtime이 있음 → 프레임워크 의존 `net8.0` exe가 그대로 실행됨 |
| zip에 exe 포함 | `PluginPackage.ExtractSafely`는 경로 이탈(Zip Slip)만 막고 파일 종류는 거르지 않음 → `MigrationAgent.exe`·`.dll`을 plugin zip에 넣어 배포 |
| 프로세스 실행 | 플러그인은 사용자 권한으로 프로그램을 실행할 수 있음(등록 때 권한 경고). Folderss는 **자기 자신**을 끝내는 것만 기록·경고하고 하위 프로세스 실행은 막지 않음 |

**동작**

| 단계 | 처리 |
|---|---|
| 배포 | plugin zip: `MigrationStudio.dll`(플러그인) + `MigrationAgent.exe/.dll` + `MigrationStudio.Core.dll` + Oracle 드라이버 |
| 시작 전 복사 | 압축 폴더(`plugins\extracted\<id>-<해시>`)에서 바로 실행하지 않고 `DataDirectory\agent\<버전>\`으로 복사해 실행 — 실행 중에 플러그인을 교체해도 파일이 잠기지 않음 |
| 실행 | `Process.Start(MigrationAgent.exe --run <RUN_ID> --pipe folderss-migration-<RUN_ID>)`. 명령줄에는 비밀번호를 넣지 않는다 |
| 실행 사양 전달 | 플러그인이 이름 있는 파이프(현재 Windows 사용자만 접근하는 ACL)로 작업·실행 계획·접속 문자열(비밀번호 포함, 메모리로만)을 보냄 |
| 진행 보고 | 에이전트 → 플러그인: 스냅숏(0.25초마다)·로그·체크포인트·종료 이벤트. 로그는 `DataDirectory\logs\<RUN_ID>.log`에도 씀 |
| 다시 붙기 | 실행 정보 `DataDirectory\runs\<RUN_ID>.json`(PID·파이프·상태). 창을 열 때 살아 있는 에이전트가 있으면 진행 화면에 다시 연결 |
| 중복 방지 | 작업마다 이름 있는 뮤텍스 `Local\folderss-migration-<jobName>` — 같은 작업을 두 번 실행하지 않음 |
| Folderss 종료 시 | 설정 "계속 실행"(기본): 에이전트는 그대로. "함께 중지": 에이전트를 Job Object(`KILL_ON_JOB_CLOSE`)에 넣어 Folderss가 끝나면 함께 끝남 → 진행 중 배치는 DB가 롤백, 체크포인트에서 재개 |
| 에이전트 비정상 종료 | 파이프 끊김 + 프로세스 종료 코드로 감지 → 실행 화면에 "실패", 체크포인트에서 재개 안내 |

---

## 5. 데이터 모델 — 작업(Job) 파일 v2

작업 하나 = 접속 2개(마이그레이션 설정의 접속 참조) + 전략 + 매핑 N + 체크포인트. JSON으로 저장하고(기본), YAML로도 내보낸다. **비밀번호는 저장하지 않는다**(접속은 마이그레이션 설정에 DPAPI로 따로 보관).

```yaml
format: folderss-migration-job
version: 2
jobName: CUSTOMER_MIGRATION
# 접속: 설정의 접속 id + 이 작업의 스키마. 이름·주소는 다른 PC에서 열 때 이름으로 맞춰 보기 위한 사본
source: { profileId: cn-legacy-prod, schema: LEGACY_APP, name: LEGACY_PROD, host: 10.10.10.21, port: 1521, service: LEGACY, user: LEGACY_APP }
target: { profileId: cn-next-prod,   schema: NEXT_APP,   name: NEXT_PROD,   host: 10.20.10.35, port: 1521, service: NEXTDB, user: NEXT_APP }
strategy:
  mode: FULL                # FULL | INCREMENTAL | CDC
  incrementalBy: PK         # PK | TIMESTAMP | SEQUENCE | SCN
  commitSize: 10000
  fetchSize: 5000
  errorPolicy: CONTINUE     # CONTINUE | STOP | RETRY
  errorTable: ERR$_         # 오류 테이블 접두어
  workers: 4
mappings:
  - id: tm-customer
    use: true
    sourceType: TABLE       # TABLE | SQL
    source: SRC_CUSTOMER
    target: TB_MEMBER
    mode: MERGE             # INSERT_ONLY | MERGE | TRUNCATE_INSERT | DELETE_INSERT
    mergeKey: [MEMBER_ID]
    checkpointColumn: CUSTOMER_ID
    where: ''
    columns:
      - { target: MEMBER_ID,   source: CUSTOMER_ID, expr: '',                  nullRule: REJECT }
      - { target: MEMBER_NAME, source: CUSTOMER_NM, expr: 'TRIM(CUSTOMER_NM)', nullRule: REJECT }
      - { target: CREATED_AT,  source: REG_DT,      expr: 'CAST(REG_DT AS TIMESTAMP)', nullRule: SYSDATE }
  - id: tm-sql-member
    use: false
    sourceType: SQL
    source: SQLMAP_MEMBER   # SQL 원본 이름
    sql: |
      SELECT C.CUSTOMER_ID AS MEMBER_ID, TRIM(C.CUSTOMER_NM) AS MEMBER_NAME, G.GRADE_NM AS MEMBER_GRADE, ...
      FROM SRC_CUSTOMER C LEFT JOIN SRC_CUSTOMER_GRADE G ON G.GRADE_CD = C.GRADE_CD
      WHERE C.CUSTOMER_ID > :LAST_ID
    binds: [{ name: LAST_ID, type: NUMBER, value: '850000', fromCheckpoint: true }]
    target: TB_MEMBER
    mode: MERGE
    mergeKey: [MEMBER_ID]
    checkpointColumn: MEMBER_ID   # 결과 열
    fetchSize: 5000               # null이면 전략 기본값
    commitSize: 10000
    columns:                      # source = 결과 별칭
      - { target: MEMBER_ID,    source: MEMBER_ID,    expr: '', nullRule: REJECT }
      - { target: MEMBER_GRADE, source: MEMBER_GRADE, expr: '', nullRule: ALLOW }
checkpoints:
  tm-customer: { column: CUSTOMER_ID, value: 850000, rows: 850000, total: 1240325, status: stopped, runId: R-20261002-234107 }
```

| 값 | 뜻 |
|---|---|
| `sourceType` | `TABLE` 원본 테이블 하나 · `SQL` SELECT 문(결과를 인라인 뷰 S로 감싸 테이블처럼 씀) |
| `nullRule` | `ALLOW` NULL 허용 · `REJECT` 행 거부(오류 테이블) · `DEFAULT` 기본값 · `SYSDATE` · `EMPTY` 빈 문자열 · `CUSTOM` 사용자 식 |
| `mode` | `INSERT_ONLY` · `MERGE`(INSERT + UPDATE) · `TRUNCATE_INSERT` · `DELETE_INSERT` |
| `binds[].fromCheckpoint` | 재개할 때 이 바인드 변수에 체크포인트 값을 넣음 |
| `checkpoints` | 작업 파일에는 마지막 상태의 사본만. 기준 저장소는 6.5 |

**v1 → v2 자동 변환**: v1의 `tableMappings`는 `sourceType: TABLE`로, `sqlMappings`는 `sourceType: SQL`로(`aliasMap`은 `columns`로) 바꾸고, 접속은 이름으로 설정의 접속을 찾아 잇는다.

---

## 6. 이관 엔진

### 6.1 파이프라인

```text
원본 접속 ─ 원본 SELECT(변환식 포함, SQL 원본은 인라인 뷰) ─ ResultSet 스트리밍(Fetch 단위)
   → [버퍼: Channel<RowBatch>, 상한 = 작업자 × 2 배치]
   → 컬럼 매핑(별칭 → 대상 열, 배열 바인드 버퍼 채우기)
   → 배치 쓰기(MERGE/INSERT, ArrayBindCount = 커밋 크기) → COMMIT → 체크포인트
```

- 전체 결과를 메모리에 올리지 않는다. 메모리 상한 ≈ (Fetch 버퍼 + 작업자 수 × 커밋 크기) × 평균 행 크기.
- 원본은 `OracleDataReader.FetchSize = 평균 행 길이 × Fetch 행 수`, 대상은 `OracleCommand.ArrayBindCount`로 배치 실행.
- 오류 정책이 "계속"이면 쓰기 문에 `LOG ERRORS INTO <오류 테이블> ('<RUN_ID>') REJECT LIMIT UNLIMITED`를 붙여 문제 행만 남기고 배치는 성공시킨다.

### 6.2 병렬 작업자

체크포인트 컬럼(보통 PK) 범위를 작업자 수로 나눠 작업자마다 원본 세션·대상 세션을 하나씩 쓴다(접속 2 × 작업자 수).

```sql
-- 범위 나누기(작업자 4)
SELECT B, MIN(K) AS RANGE_FROM, MAX(K) AS RANGE_TO, COUNT(*) AS ROWS_CNT
FROM (SELECT CUSTOMER_ID AS K, NTILE(4) OVER (ORDER BY CUSTOMER_ID) AS B FROM LEGACY_APP.SRC_CUSTOMER)
GROUP BY B ORDER BY B
```

SQL 원본은 `SELECT * FROM (사용자 SQL) S`의 체크포인트 열로 같은 방식으로 나눈다. 작업자들이 같은 시점을 읽게 하려면 시작할 때 SCN을 하나 잡고 `AS OF SCN :START_SCN`으로 읽는다(UNDO 보존 시간 안에서만 — 결정 필요, 9장).

### 6.3 실행 순서

대상 외래 키(`ALL_CONSTRAINTS` R 타입)를 보고 부모 → 자식 순으로 작업을 정렬한다(예: `TB_MEMBER` → `TB_SALES_ORDER`). 같은 대상에 쓰는 작업이 둘이면 검증에서 경고한다.

### 6.4 상태와 제어

```text
        start            pause(커밋 경계)          resume
 idle ────────▶ running ───────────▶ pausing ──▶ paused ───────▶ running
                  │  │                                │
                  │  └── 모든 작업 끝 ──▶ done        │ stop
                  │                                   ▼
                  ├── stop: 진행 중 배치 ROLLBACK ──▶ stopped ── (체크포인트에서 재개) ──▶ running
                  └── 오류(정책: 중지)·에이전트 비정상 종료 ─▶ failed ── (체크포인트에서 재개) ──▶ running
```

| 동작 | 처리 |
|---|---|
| 일시정지 | 진행 중 배치를 커밋한 뒤 멈춘다(세션 유지). 이어서 실행하면 같은 커서에서 계속 |
| 중지 | 진행 중 배치를 롤백, 마지막 커밋 키를 체크포인트로 남기고 세션을 닫는다 |
| 체크포인트에서 재개 | `WHERE 키 > :LAST_ID ORDER BY 키`로 남은 범위만 다시 연다(SQL 원본은 CP 바인드 변수에 넣거나 바깥 `S.키 > :LAST_ID`) |
| 일시 오류(ORA-03113 등) | 진행 중 배치 롤백 → 다시 연결 → 체크포인트부터 이어 읽기, 3회까지 |
| 행 오류(ORA-01400·00001·12899 등) | 정책 "계속": 오류 테이블 · "중지"·"재시도": 멈춤(데이터 오류는 재시도로 안 풀림) |

### 6.5 체크포인트 저장소

**체크포인트란**: "여기까지는 대상 DB에 확실히 들어갔다"는 표시 — 마지막으로 **커밋된** 배치의 마지막 키 값(예: `CUSTOMER_ID = 850000`)이다. 중지·장애 뒤 재개하면 `WHERE CUSTOMER_ID > 850000`부터 다시 읽는다.

**왜 저장 위치가 문제인가**: 배치 하나를 끝낼 때 두 가지 일을 한다.

```text
① 대상 DB에 배치 커밋      (CUSTOMER_ID 840,001 ~ 850,000 이 들어감)
② 체크포인트 = 850000 저장
```

①과 ② 사이에서 에이전트·PC가 죽으면 두 기록이 어긋날 수 있다. 저장 위치에 따라 결과가 다르다.

| | A. 대상 DB 제어 테이블 `MIG_CHECKPOINT` | B. 로컬 파일 `DataDirectory\checkpoints\<작업>.json` |
|---|---|---|
| 저장 방법 | 배치 쓰기(MERGE/INSERT)와 체크포인트 UPDATE를 **같은 트랜잭션**에서 한 번에 COMMIT | 대상 DB에 COMMIT한 **뒤에** 파일에 씀 |
| ①과 ② 사이에 죽으면 | 둘 다 반영되거나 둘 다 안 됨 → 어긋날 수 없음 | DB에는 850,000까지 들어갔는데 파일에는 840,000 → 재개 때 840,001~850,000(한 배치)을 **다시 처리** |
| 다시 처리의 영향 | 없음 | MERGE: 같은 값으로 다시 갱신 → 결과 같음 · INSERT ONLY: 그 배치가 ORA-00001(중복 키)로 오류 테이블에 쌓이거나 "중지" 정책이면 멈춤 · TRUNCATE/DELETE + INSERT: 처음부터 다시 |
| 필요한 것 | 대상 스키마에 `MIG_RUN`·`MIG_RUN_TASK`·`MIG_CHECKPOINT` 테이블(CREATE TABLE 권한 또는 DBA가 미리 생성) | 없음(대상 DB를 건드리지 않음) |
| 다른 PC에서 재개 | 가능(체크포인트가 DB에 있음) | 그 PC에서만 |
| 실행 이력 조회 | DB에서 SQL로 조회 가능(UI-MIG-007 FUNC07) | 로컬 파일만 |

**설정**(UI-MIG-008 > 기본값 > 체크포인트 저장소): `자동`(기본) — 대상에 제어 테이블을 만들 수 있으면 A, 권한이 없으면 B. B로 돌 때 INSERT ONLY 작업은 **재개 직후 첫 배치만 MERGE로** 써서 다시 처리되는 한 배치의 중복 키 오류를 흡수한다.

### 6.6 제어 테이블(대상, 접두어 기본 `MIG_`)

```sql
CREATE TABLE MIG_RUN (
    RUN_ID      VARCHAR2(30)  PRIMARY KEY,
    JOB_NAME    VARCHAR2(100) NOT NULL,
    RUN_MODE    VARCHAR2(10)  NOT NULL,          -- DRY | EXECUTE | RESUME
    STATUS      VARCHAR2(10)  NOT NULL,          -- running | done | stopped | failed
    AGENT_PID   NUMBER,
    HOST_NAME   VARCHAR2(100),
    STARTED_AT  TIMESTAMP     DEFAULT SYSTIMESTAMP,
    ENDED_AT    TIMESTAMP,
    MESSAGE     VARCHAR2(4000)
);

CREATE TABLE MIG_RUN_TASK (
    RUN_ID      VARCHAR2(30)  NOT NULL,
    TASK_KEY    VARCHAR2(100) NOT NULL,          -- 매핑 id
    LABEL       VARCHAR2(400),
    STATUS      VARCHAR2(10),
    ROWS_READ   NUMBER DEFAULT 0,
    INSERTED    NUMBER DEFAULT 0,
    UPDATED     NUMBER DEFAULT 0,
    REJECTED    NUMBER DEFAULT 0,
    STARTED_AT  TIMESTAMP,
    ENDED_AT    TIMESTAMP,
    CONSTRAINT PK_MIG_RUN_TASK PRIMARY KEY (RUN_ID, TASK_KEY)
);

CREATE TABLE MIG_CHECKPOINT (
    JOB_NAME    VARCHAR2(100) NOT NULL,
    TASK_KEY    VARCHAR2(100) NOT NULL,
    CP_COLUMN   VARCHAR2(128) NOT NULL,
    CP_VALUE    VARCHAR2(4000),
    RANGE_FROM  VARCHAR2(4000),                      -- 작업자 범위(병렬일 때). 재개가 같은 범위로 이어 가려고 저장
    RANGE_TO    VARCHAR2(4000),
    ROWS_DONE   NUMBER,
    ROWS_TOTAL  NUMBER,
    STATUS      VARCHAR2(10),                    -- running | stopped | done
    RUN_ID      VARCHAR2(30),
    UPDATED_AT  TIMESTAMP DEFAULT SYSTIMESTAMP,
    CONSTRAINT PK_MIG_CHECKPOINT PRIMARY KEY (JOB_NAME, TASK_KEY)
);
```

---

## 7. 공통 규칙

### 7.1 테마 키 (POC CSS 변수 ↔ Folderss 리소스 키)

| CSS 변수 | Folderss 키 | Black | Light |
|---|---|---|---|
| `--win` | `WindowBackground` | #08090B | #EEF1F5 |
| `--panel` | `PanelBackground` | #0E1013 | #FFFFFF |
| `--surface` | `SurfaceBackground` | #14171B | #F7F8FA |
| `--control` | `ControlBackground` | #1B1F24 | #FFFFFF |
| `--hover` / `--pressed` | `ControlHoverBrush` / `ControlPressedBrush` | #252B32 / #303842 | #E8EDF5 / #DCE4EF |
| `--border` | `BorderBrush` | #2B3037 | #CDD4DE |
| `--text` / `--text2` / `--text3` | `PrimaryText` / `SecondaryText` / `DisabledTextBrush` | #F3F4F6 / #9CA3AF / #626A75 | #171A1F / #626B78 / #9AA2AD |
| `--accent` / `--accent-hover` | `AccentBrush` / `AccentHoverBrush` | #4F8CFF / #6CA0FF | #2563EB / #1D4ED8 |
| `--selection` / `--row-hover` | `SelectionBrush` / `RowHoverBrush` | #244A7C / #1D2733 | #CFE0FF / #EAF1FC |

의미 색(테마에 없음, `Theme.cs` 고정값): 성공 #3FB27F · 경고 #D19A66 · 위험 #E06C75. Light에서는 글자 대비를 위해 한 단계 어둡게(#1F8A5B · #B4691F · #C83A44). 접속 색 표시: 초록 #2EA05B(개발) · 노랑 #C9930A(검증) · 빨강 #D9363E(운영). 글꼴: `AppFontFamily`(Segoe UI) 13px, 고정폭 `Cascadia Mono, Consolas, D2Coding`, 아이콘 `Segoe Fluent Icons, Segoe MDL2 Assets`.

### 7.2 상태 표시

| 표시 | 쓰는 곳 |
|---|---|
| `PASS` `WARN` `ERROR` `INFO` 배지 | 검증 결과, 컬럼 검사(`OK`=PASS) |
| `MATCH` | 실행 후 검증 행 수 일치 |
| `SQL` 꼬리표(보라) | SQL 원본 매핑(테이블 매핑·실행 작업 목록·컬럼 매핑 머리) |
| 진행 막대: 강조색 줄무늬(실행) · 경고색(일시정지) · 성공색(완료) · 위험색(중지·실패) | 실행 화면, 테이블 매핑 컬럼 비율 |
| DB 배지(색 표시 바탕 흰 글자) | 단계 막대, 상태줄, 확인 창 — DB Helper `Theme.DbBadge`와 같음 |

### 7.3 안전장치 (DB Helper 규칙 승계)

- 원본 세션은 읽기 전용(`SET TRANSACTION READ ONLY` + SELECT만 실행). SQL 원본도 `SELECT`·`WITH`로 시작하는 문장만 받는다.
- 접속에 **쓰기 금지(원본 전용)** 를 켜면 그 접속은 대상으로 고를 수 없다(운영 원본 DB 보호).
- `TRUNCATE + INSERT`, `DELETE + INSERT`는 실행 직전 확인 창에서 체크해야 실행 버튼이 켜진다. 대상이 빨강(운영)이면 접속 화면·검증·테이블 매핑에서 미리 경고한다.
- 실행 전 검증 ERROR가 고른 작업에 있으면 이관 실행을 막는다(Dry Run은 허용). 검증 뒤 작업을 바꾸면 "검증 뒤 바뀜"으로 표시하고 실행 때 다시 묻는다.
- 실행 중에는 새 작업·열기·템플릿 가져오기를 막는다. 실행은 하위 프로세스라 창을 닫아도 계속된다(4.5).

### 7.4 단축키

| 키 | 동작 |
|---|---|
| `Ctrl+1` … `Ctrl+5` | 단계 이동 |
| `Ctrl+Q` | SQL 원본 편집기 |
| `Ctrl+O` / `Ctrl+S` / `Ctrl+Shift+S` | 작업 열기 / 저장(JSON) / YAML로 저장 |
| `F6` | 실행 전 검증 |
| `F5` | 이관 시작(지금 실행 모드) · 일시정지 중이면 이어서 |
| `Ctrl+Enter` | SQL 원본 편집기: SQL 검증 |
| `Alt+F·M·R·V·H` | 메뉴 열기(메뉴 안에서 밑줄 글자) |

---

## 8. Mock 데이터 (POC)

| 원본 `LEGACY_APP` | 행 | 대상 `NEXT_APP` | 행 |
|---|---|---|---|
| SRC_CUSTOMER (CUSTOMER_ID, CUSTOMER_NM, PHONE_NO, STATUS_CD, GRADE_CD, REG_DT, MOD_DT) | 1,240,325 | TB_MEMBER (MEMBER_ID, MEMBER_NAME, MOBILE_NO, USE_YN, MEMBER_GRADE, CREATED_AT, UPDATED_AT) | 58,225 |
| SRC_CUSTOMER_GRADE (GRADE_CD, GRADE_NM, SORT_SEQ) | 6 | TB_MEMBER_GRADE | 0 |
| SRC_ORDER | 8,420,117 | TB_SALES_ORDER (FK → TB_MEMBER, CHANNEL_CD NOT NULL 신규 컬럼) | 0 |
| SRC_ORDER_ITEM · SRC_PRODUCT · SRC_CODE_MST · V_CUSTOMER_SUMMARY | — | TB_SALES_ORDER_ITEM · TB_PRODUCT · TB_COMMON_CODE | 0 |

마이그레이션 설정의 예제 접속: `LEGACY_PROD`(빨강, 쓰기 금지) · `LEGACY_DEV`(초록) · `NEXT_PROD`(빨강) · `NEXT_STG`(노랑, 비밀번호 저장 안 함).

예제 작업은 일부러 문제를 품고 있다: `PHONE_NO VARCHAR2(30) → MOBILE_NO VARCHAR2(20)`(잘림 위험), 공백만 있는 고객명 37건(NOT NULL 거부), `CHANNEL_CD` 매핑 없음(ERROR), 지난 실행이 `CUSTOMER_ID = 850000`에서 멈춘 체크포인트, 같은 대상(TB_MEMBER)을 쓰는 테이블 원본과 SQL 원본(`SQLMAP_MEMBER`, 기본 사용 안 함).

---

## 9. 결정 사항

| # | 질문 | 상태 |
|---|---|---|
| 1 | 실행 엔진 위치 | **결정** — 하위 프로세스 `MigrationAgent.exe`(4.5). 창·Folderss를 닫아도 계속, 다시 열면 다시 붙음. Folderss 종료 시 동작은 설정(계속 실행·함께 중지) |
| 2 | 체크포인트 기준 저장소: 대상 제어 테이블 vs 로컬 파일 | **결정 필요** — 6.5 표 참고. 쟁점은 "운영 대상 DB에 `MIG_` 테이블 3개를 만들어도 되는가". POC 기본값은 `자동` |
| 3 | 접속 정보 | **결정** — 플러그인 자체 "마이그레이션 설정"에 정의(UI-MIG-008). DB Helper와 공유하지 않음 |
| 4 | 원본을 SQL로 | **결정** — 매핑의 원본 종류 `TABLE`·`SQL` 통합(작업 파일 v2). SQL 편집은 단계가 아닌 도구 창(UI-MIG-004) |
| 5 | 병렬 작업자 사이 일관성: `AS OF SCN` 사용 여부(ORA-01555 위험) | 결정 필요 — POC는 선택 옵션, 기본 끔 |
| 6 | SQL 편집기 구문 강조: TextBox(DB Helper와 동일) vs AvalonEdit(NuGet 추가) | 결정 필요 — POC는 강조 있음, 구현은 TextBox로 시작 |
| 7 | CDC(LogMiner·GoldenGate) 범위 | 1차 제외, 화면 자리만 — 구현 플랜·선택지·결정 질문은 [CDC-Plan.md](CDC-Plan.md) |

---

## 10. POC 실행

ES 모듈을 쓰지 않으므로 `design/poc/index.html`을 브라우저로 바로 열어도 된다(Edge·Chrome). 브라우저가 로컬 파일을 막으면 간단한 서버로 연다.

```powershell
cd design/poc
python -m http.server 8765    # http://127.0.0.1:8765
```

- 작업은 브라우저 `localStorage`에 임시 저장된다(v1 임시 저장은 자동으로 v2로 바뀜). 처음 상태로 돌리려면 `파일 > 예제 작업 불러오기`.
- 접속은 `파일 > 마이그레이션 설정…`(⚙)에서 고친다. POC는 브라우저에 저장한다.
- `보기` 메뉴에서 Black·Light 테마를 바꾼다. 아이콘은 Windows의 Segoe Fluent Icons 글꼴을 쓴다.
- 실행 시뮬레이션 속도는 실행 화면의 `POC 시뮬레이션 속도`(×1·×8·×32)로 바꾼다. 하위 프로세스는 흉내만 낸다(PID·파이프 이름 표시).
