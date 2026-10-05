# UI-MIG-006. 실행

## 1. 화면 내용 요약

- **목적**: 실행할 작업을 고르고 Dry Run으로 확인한 뒤 이관한다. 진행률·처리 속도·엔진 파이프라인·작업별 결과·로그를 실시간으로 보이고, 일시정지·중지·체크포인트에서 재개를 제공한다.
- **주요 기능**:
  - 작업 선택: 매핑 목록 — 테이블 원본과 SQL 원본(`SQL` 꼬리표)(실행 순서 = 대상 FK 부모 → 자식), 행 수·이관 방식·체크포인트(재개 위치 %)·검증 ERROR 표시, 모든 작업(ALL SELECTED)
  - 실행 위치: 하위 프로세스 `MigrationAgent.exe`(PID 표시) — 창·Folderss를 닫아도 계속, 다시 열면 진행 화면에 다시 붙음(README 4.5)
  - 실행 모드: Dry Run · 이관 실행 · 체크포인트에서 재개
  - 제어: 시작 · 일시정지(커밋 경계) · 이어서 · 중지(진행 중 배치 롤백 + 체크포인트)
  - 진행: `850,000 / 1,240,325 행 · 68%`, 진행 막대, 처리 속도·경과·남은 시간·커밋·Inserted·Updated·Rejected
  - 엔진 파이프라인: 원본 읽기 → Transform → 컬럼 매핑 → 배치 쓰기 → 커밋 → 체크포인트(단계별 지표)
  - 작업별 진행 표, Migration Log(`[START]` `[INFO]` `[WARN]` `[ERROR]` `[DONE]`, 거르기·복사), 체크포인트 카드(`WHERE CUSTOMER_ID > 850000`)

---

## 2. 화면 이미지 (와이어프레임)

```
+--------------------------------------------------------------------------------------------------+
| STEP 5  실행                                                                                      |
| 작업을 골라 Dry Run으로 확인한 뒤 이관합니다. 일시정지는 커밋 경계에서 멈추고, 중지하면 …          |
+--------------------------------------------------------------------------------------------------+
| [필터 영역] (완료·중지 뒤) ⚠ 중지됨 · 00:00:14 · 처리 530,000행 · 거부 16행  [체크포인트에서 재개 ›] |
+------------------------------------------------+-------------------------------------------------+
| ≡ 작업 선택  3개 · 9,660,448 행  ☐ 모든 작업    | ▷ 실행 제어                                      |
| +--------------------------------------------+ |  실행 모드 (Run Mode)                            |
| |☑ 1 SRC_CUSTOMER → TB_MEMBER                | |  [ Dry Run | 이관 실행 | 체크포인트에서 재개 ]   |
| |     1,240,325 행 · INSERT + UPDATE          | |  처음부터 실행합니다. MERGE는 다시 실행해도 …    |
| |     ⟲ CUSTOMER_ID 850,000 (68%)             | |  [▶ 이관 시작] [⏸ 일시정지] [▷ 이어서] [□ 중지] |
| |☑ 2 SRC_ORDER → TB_SALES_ORDER     [ERROR 1]| |  ✓ 검증 통과 · 14:49:45                          |
| |☑ 3 SRC_CUSTOMER_GRADE → TB_MEMBER_GRADE     | |  배치    커밋 10,000행 · Fetch 5,000행 · 작업자 4 |
| |☐ – [SQL] SQLMAP_MEMBER → TB_MEMBER          | |  오류 정책 계속 + 오류 테이블 (ERR$_<대상>)      |
| +--------------------------------------------+ |  대상    [NEXT_PROD] NEXT_APP                    |
|                                                |  실행 위치 하위 프로세스 MigrationAgent.exe ·    |
|                                                |           창·Folderss를 닫아도 계속              |
| 실행 순서: 대상 외래 키 기준 부모 → 자식        |  POC 시뮬레이션 속도 [×1|×8|×32]                 |
+------------------------------------------------+-------------------------------------------------+
| [본문 영역] ⟳ 진행                    R-20261003-144805 · MigrationAgent.exe PID 18244  (● 실행 중) |
|  850,000 / 1,240,325 행                                                                    68%   |
|  지금: SRC_CUSTOMER → TB_MEMBER (1/3)                                                            |
|  [██████████████████████████████████████████░░░░░░░░░░░░░░░░░░░░]   ← 실행 중 줄무늬               |
|  +-----------+----------+----------+--------+-----------+----------+----------+                   |
|  | 처리 속도  | 경과     | 남은 시간 | 커밋   | Inserted  | Updated  | Rejected |                   |
|  | 38,138행/초| 00:00:29 | 00:03:45 | 108    | 1,029,269 | 50,699   | 32       |                   |
|  +-----------+----------+----------+--------+-----------+----------+----------+                   |
|  엔진 파이프라인 — Streaming · Batch · Checkpoint (전체를 메모리에 올리지 않음)                    |
|  [원본 읽기]  › [Transform]   › [컬럼 매핑]   › [배치 쓰기]      › [커밋]  › [체크포인트]          |
|   37,457행/초    Oracle SELECT   6열→TB_MEMBER   INSERT + UPDATE      108회     CUSTOMER_ID       |
|   Fetch 5,000×218 안에서         별칭→대상 열    배열 10,000 × 3      COMMIT    = 1080000          |
|   ▬▬▬(버퍼)      ▬▬▬             ▬▬▬            ▬▬▬(배치 채움)                                      |
|  +---+-----------------------------------+-------+------------+------------+---------+-----+----+ |
|  | # | 작업                               | 상태   | 진행        | 행          | Inserted|Upd. |Rej.| |
|  | 1 | SRC_CUSTOMER → TB_MEMBER           |◌실행 중| ▬▬▬▬▬ 87%  | 1,080,000/1.24M | 1,029,269 | 50,699 | 32 | |
|  |   |  INSERT + UPDATE · 재개: CUSTOMER_ID > 850000 |   |            |             |         |     |    | |
|  | 2 | SRC_ORDER → TB_SALES_ORDER         | 대기   |        0%  | 0 / 8.42M   |       0 |   0 |  0 | |
|  +---+-----------------------------------+-------+------------+------------+---------+-----+----+ |
+--------------------------------------------------------+-----------------------------------------+
| ≡ Migration Log         [전체|INFO|WARN|ERROR] [⧉][⌫]   | ⟲ 체크포인트  커밋마다 저장             |
| 14:48:05 [START] 실행 R-… · 이관 실행 · 작업 3개        | SRC_CUSTOMER → TB_MEMBER   (진행 중) [🗑]|
|                  Batch size=10,000 · Workers=4 · …      |  Last Successful  CUSTOMER_ID = 810000  |
| 14:48:05 [START] SRC_CUSTOMER → TB_MEMBER (INSERT+UPDATE)|  진행  810,000 / 1,240,325 (65%)        |
| 14:48:05 [INFO]  MERGE enabled · 대상 기존 58,225행 갱신 |  WHERE CUSTOMER_ID > 810000             |
| 14:48:05 [WARN]  거부 1행 → NEXT_APP.ERR$_TB_MEMBER     |  ORDER BY CUSTOMER_ID                   |
| 14:48:06 [INFO]  Committed 200,000 rows · 체크포인트 …  | SRC_ORDER → TB_SALES_ORDER   (완료)     |
| 14:48:38 [DONE]  SRC_CUSTOMER → TB_MEMBER (00:00:33)    |  증분 이관을 하면 ORDER_NO > … 부터     |
|                  Inserted : 1,182,063                   |                                         |
|                  Updated  : 58,225                      |                                         |
|                  Rejected : 37 → NEXT_APP.ERR$_TB_MEMBER |                                        |
+--------------------------------------------------------+-----------------------------------------+
| [‹ 검증]                F5 시작 · 일시정지 중 F5 = 이어서                                          |
+--------------------------------------------------------------------------------------------------+
```

**팝업 ① 되돌릴 수 없는 이관 방식(파괴적 방식 확인)**

```
+--------------------------------------------------------+
| 되돌릴 수 없는 이관 방식                             [×]|
+--------------------------------------------------------+
| ⚠ 대상 [NEXT_PROD]에서 아래 테이블의 기존 행을 지웁니다. |
|   • TRUNCATE + INSERT  NEXT_APP.TB_MEMBER_GRADE         |
| ☐ 지워도 되는 것을 확인했습니다                          |
+--------------------------------------------------------+
|                               [취소] [실행](체크 전 꺼짐)|
+--------------------------------------------------------+
```

**팝업 ② 실행할 수 없음(검증 ERROR)**

```
+--------------------------------------------------------+
| 실행할 수 없음                                       [×]|
+--------------------------------------------------------+
| ⓧ 고른 작업에 검증 ERROR가 1건 있습니다. 고친 뒤 다시   |
|   검증하세요. Dry Run은 할 수 있습니다.                  |
|   • NOT NULL · TB_SALES_ORDER.CHANNEL_CD                |
|     NOT NULL 컬럼에 값이 없음 — 모든 행이 ORA-01400 …   |
+--------------------------------------------------------+
|                               [닫기] [검증 결과 보기]  |
+--------------------------------------------------------+
```

---

## 3. 버튼 설명

| 버튼명 | 기능 설명 | 주요 로직 |
|--------|----------|-----------|
| 작업 체크 · 모든 작업 | 실행 대상 고르기 | 처음에는 매핑의 "사용" 값. 실행 중에는 바꿀 수 없음. 번호 = 실행 순서 |
| 실행 모드 | Dry Run / 이관 실행 / 체크포인트에서 재개 | 실행 중에는 꺼짐. 모드 설명 한 줄 표시 |
| 시작 (F5) — "Dry Run 시작" / "이관 시작" / "재개 시작" | 실행 시작 | Dry Run 외에는: 검증 안 했으면 "검증 먼저" 확인 창 → 고른 작업에 ERROR면 팝업 ② → 검증 뒤 바뀌었으면 확인 → 파괴적 방식이면 팝업 ①. 통과하면 FUNC01 계획 → FUNC02 시작 |
| 일시정지 | 커밋 경계에서 멈춤 | FUNC04. 진행 중 배치를 커밋한 뒤 `[PAUSE]` 로그, 세션 유지, 진행 막대 경고색 |
| 이어서 (일시정지 중 F5) | 같은 커서에서 계속 | FUNC05. `[RESUME]` 로그 |
| 중지 | 실행 끝내기 | FUNC06. 진행 중 배치 ROLLBACK, 마지막 커밋 키를 체크포인트로, `[STOP] … 체크포인트 저장: CUSTOMER_ID = 530000` |
| 체크포인트에서 재개 › / 아이콘 ↻ | 남은 범위만 실행 | FUNC01(mode=RESUME) → 체크포인트가 있는 작업은 `WHERE 키 > :LAST_ID`, 없으면 처음부터(알림) |
| 실행 후 검증 › | 완료 뒤 검증 화면으로 | UI-MIG-005 FUNC02 |
| 로그 거르기 · 복사 · 지우기 | 로그 보기 | 지우기는 화면에서만(파일 로그는 유지) |
| 체크포인트 🗑 | 체크포인트 지우기 | 확인 창 → FUNC09. 다음 실행은 처음부터 |
| POC 시뮬레이션 속도 | POC 전용 | 실제 구현에는 없음 |

---

## 4. 서비스 처리

| 기능ID | 기능명 | 메소드 | 서비스주소 | 입력파라미터 | 기능설명 | 참조 테이블 | SQL |
|--------|--------|--------|-----------|-------------|----------|-------------|-----|
| FUNC01 | 실행 계획 | POST | /v1/migration/run/plan | jobId, selected[], runMode | 작업을 대상 FK 순으로 정렬, 원본 범위 행 수, 재개 위치(체크포인트), 작업자별 키 범위, 예상 거부 수 | ALL_CONSTRAINTS, MIG_CHECKPOINT, 원본 | SQL-1 |
| FUNC02 | 실행 시작 | POST | /v1/migration/run/start | jobId, plan, runMode, commitSize, fetchSize, workers, errorPolicy | 에이전트 실행(FUNC11) 뒤 파이프로 실행 사양을 보내고, 에이전트가 실행 기록 생성·오류 테이블 준비·TRUNCATE(해당 방식)·작업별 파이프라인 시작. 구현: `AgentClient.StartAsync` → `MigrationEngine.Start` | MIG_RUN, MIG_RUN_TASK | SQL-2 |
| FUNC03 | 배치 쓰기 · 커밋 · 체크포인트 | (엔진 내부) | /v1/migration/run/batch | runId, taskKey, rows[] | 배열 바인드로 쓰기 → 체크포인트 갱신 → COMMIT을 **한 트랜잭션**으로 | 대상 테이블, MIG_CHECKPOINT | SQL-3 |
| FUNC04 | 일시정지 | POST | /v1/migration/run/pause | runId | 진행 중 배치를 커밋한 뒤 읽기 중단(커서·세션 유지) | — | 해당 없음(엔진 상태) |
| FUNC05 | 이어서 실행 | POST | /v1/migration/run/resume | runId | 같은 커서에서 Fetch 재개 | — | 해당 없음 |
| FUNC06 | 중지 | POST | /v1/migration/run/stop | runId | 진행 중 배치 ROLLBACK, 커서·세션 닫기, 실행·작업 상태 기록 | MIG_RUN, MIG_RUN_TASK | SQL-4 |
| FUNC07 | 진행 스냅숏 | GET | /v1/migration/run/snapshot | runId | 상태·작업별 읽음/씀/입력/갱신/거부/커밋·속도·체크포인트·파이프라인 지표·새 로그. 에이전트가 파이프로 0.25초마다 보내고, 화면은 렌더 프레임에 한 번 그린다 | — | 해당 없음(파이프 메시지) |
| FUNC08 | 원본 읽기 | (엔진 내부) | /v1/migration/run/read | taskKey, LAST_ID, RANGE_TO | 원본 SELECT를 Fetch 단위로 스트리밍(UI-MIG-003 SQL-3 / UI-MIG-004 사용자 SQL) | 원본 테이블 | UI-MIG-003 SQL-3 |
| FUNC09 | 체크포인트 지우기 | DELETE | /v1/migration/checkpoint | jobName, taskKey | 다음 실행을 처음부터 | MIG_CHECKPOINT | `DELETE FROM MIG_CHECKPOINT WHERE JOB_NAME = :JOB_NAME AND TASK_KEY = :TASK_KEY` |
| FUNC10 | 작업 결과 기록 | PUT | /v1/migration/run/task | runId, taskKey, counts, status | 작업이 끝나거나 멈출 때 결과 저장 | MIG_RUN_TASK | SQL-5 |
| FUNC11 | 에이전트 실행 | POST | /v1/migration/run/agent-start | runId | `DataDirectory\agent\<버전>\`로 복사한 `MigrationAgent.exe --run <RUN_ID> --pipe folderss-migration-<RUN_ID>` 실행, 이름 있는 파이프(현재 사용자 ACL) 연결, `runs\<RUN_ID>.json`(PID·파이프·상태) 기록, 설정이 "함께 중지"면 Job Object에 넣음. 명령줄에 비밀번호 없음 | — | 해당 없음(프로세스) |
| FUNC12 | 진행 화면 다시 붙기 | POST | /v1/migration/run/attach | runId | 창을 열 때 `runs\*.json`에서 살아 있는 에이전트를 찾아 파이프로 다시 연결하고 지금 스냅숏·로그(파일 끝 2,000줄)를 받음 | — | 해당 없음(프로세스) |

### 4.1 SQL 상세

**SQL-1 계획: 체크포인트와 작업자 범위**

```sql
-- 재개 위치
SELECT CP_VALUE, ROWS_DONE, ROWS_TOTAL, STATUS
FROM MIG_CHECKPOINT
WHERE JOB_NAME = :JOB_NAME AND TASK_KEY = :TASK_KEY;

-- 남은 범위를 작업자 수로 나누기(재개면 :LAST_ID 다음부터)
SELECT B, MIN(K) AS RANGE_FROM, MAX(K) AS RANGE_TO, COUNT(*) AS ROWS_CNT
FROM (
    SELECT CUSTOMER_ID AS K, NTILE(:WORKERS) OVER (ORDER BY CUSTOMER_ID) AS B
    FROM LEGACY_APP.SRC_CUSTOMER
    WHERE CUSTOMER_ID > :LAST_ID
)
GROUP BY B
ORDER BY B
```

**SQL-2 실행 시작**

```sql
INSERT INTO MIG_RUN (RUN_ID, JOB_NAME, RUN_MODE, STATUS) VALUES (:RUN_ID, :JOB_NAME, :RUN_MODE, 'running');

INSERT INTO MIG_RUN_TASK (RUN_ID, TASK_KEY, LABEL, STATUS, STARTED_AT)
VALUES (:RUN_ID, :TASK_KEY, :LABEL, 'run', SYSTIMESTAMP);

-- TRUNCATE + INSERT 작업만(확인 창 통과 뒤)
TRUNCATE TABLE NEXT_APP.TB_MEMBER_GRADE;
```

**SQL-3 배치 하나(작업자마다, 커밋 크기만큼 배열 바인드)**

```sql
-- 1) 쓰기: UI-MIG-003 SQL-4 (ArrayBindCount = 10,000)
MERGE INTO NEXT_APP.TB_MEMBER T USING (SELECT :MEMBER_ID AS MEMBER_ID, ... FROM DUAL) S
ON (T.MEMBER_ID = S.MEMBER_ID)
WHEN MATCHED THEN UPDATE SET ...
WHEN NOT MATCHED THEN INSERT (...) VALUES (...)
LOG ERRORS INTO NEXT_APP.ERR$_TB_MEMBER (:RUN_ID) REJECT LIMIT UNLIMITED;

-- 2) 체크포인트(같은 트랜잭션)
MERGE INTO MIG_CHECKPOINT C
USING (SELECT :JOB_NAME AS JOB_NAME, :TASK_KEY AS TASK_KEY FROM DUAL) S
ON (C.JOB_NAME = S.JOB_NAME AND C.TASK_KEY = S.TASK_KEY)
WHEN MATCHED THEN UPDATE SET C.CP_VALUE = :CP_VALUE, C.ROWS_DONE = :ROWS_DONE, C.STATUS = 'running',
                             C.RUN_ID = :RUN_ID, C.UPDATED_AT = SYSTIMESTAMP
WHEN NOT MATCHED THEN INSERT (JOB_NAME, TASK_KEY, CP_COLUMN, CP_VALUE, ROWS_DONE, ROWS_TOTAL, STATUS, RUN_ID)
                      VALUES (:JOB_NAME, :TASK_KEY, :CP_COLUMN, :CP_VALUE, :ROWS_DONE, :ROWS_TOTAL, 'running', :RUN_ID);

-- 3)
COMMIT;
```

작업자가 여럿이면 작업자마다 자기 범위의 체크포인트(`TASK_KEY = 매핑id#작업자번호`)를 두고, 재개 때 범위별로 이어 간다. 화면의 체크포인트 값은 범위 전체에서 끊김 없이 끝난 마지막 키(최소값)다.

**SQL-4 중지·실패**

```sql
ROLLBACK;   -- 진행 중 배치(체크포인트도 함께 되돌아감 → 마지막 커밋 위치 유지)

UPDATE MIG_CHECKPOINT SET STATUS = 'stopped', UPDATED_AT = SYSTIMESTAMP
WHERE JOB_NAME = :JOB_NAME AND TASK_KEY = :TASK_KEY;

UPDATE MIG_RUN SET STATUS = :STATUS, ENDED_AT = SYSTIMESTAMP, MESSAGE = :MESSAGE WHERE RUN_ID = :RUN_ID;
COMMIT;
```

**SQL-5 작업 결과**

```sql
UPDATE MIG_RUN_TASK
SET STATUS = :STATUS, ROWS_READ = :ROWS_READ, INSERTED = :INSERTED, UPDATED = :UPDATED,
    REJECTED = :REJECTED, ENDED_AT = SYSTIMESTAMP
WHERE RUN_ID = :RUN_ID AND TASK_KEY = :TASK_KEY
```

### 4.2 로그 형식

| 꼬리표 | 색 | 예 |
|---|---|---|
| `[START]` | 강조 | `실행 R-20261003-144805 · 이관 실행 · 작업 3개` / `Batch size=10,000 · Fetch=5,000 · Workers=4 · 오류 정책=계속 + 오류 테이블` |
| `[INFO]` | 보조 | `MERGE enabled · 대상 기존 58,225행은 갱신 예상` · `Committed 500,000 rows · 체크포인트 CUSTOMER_ID = 500000` (원본 범위의 약 1/8마다) |
| `[WARN]` | 경고 | `거부 1행 → NEXT_APP.ERR$_TB_MEMBER` / `ORA-01400 …` · `ORA-03113 … 진행 중 배치 54,016행 롤백 · 다시 연결해 재시도 1/3` |
| `[PAUSE]` `[RESUME]` | 경고 / 강조 | `커밋 경계에서 일시 정지 · 마지막 커밋 CUSTOMER_ID = 612000` |
| `[STOP]` `[ERROR]` | 위험 | `사용자가 중지 · 진행 중 배치 9,812행 롤백` / `체크포인트 저장: CUSTOMER_ID = 620000` |
| `[DONE]` | 성공 | `SRC_CUSTOMER → TB_MEMBER (00:00:33)` / `Inserted : 1,182,063` / `Updated  : 58,225` / `Rejected : 37 → NEXT_APP.ERR$_TB_MEMBER` |
| `[DRY]` | 보라 | `쓰기 없음 — 원본 읽기·변환·매핑만 하고 쓰기 문은 만들기만 함` |

로그는 화면(최근 2,000줄)과 `DataDirectory\logs\<RUN_ID>.log`(전체)에 남긴다.

---

## 5. 기타 특이사항

- **유효성 검사**: 시작 조건 — 고른 작업 1개 이상, 메타데이터 있음, (Dry Run 외) 실행 전 검증 완료·고른 작업 ERROR 없음, 파괴적 방식 확인 체크.
- **권한 처리**: 대상 쓰기 권한, `MIG_*` 제어 테이블(없으면 첫 실행 때 만들지 묻고, 권한이 없으면 로컬 파일 체크포인트 — README 6.5).
- **팝업**: ① 되돌릴 수 없는 이관 방식(확인 체크해야 [실행] 켜짐, 위험색), ② 실행할 수 없음(검증 ERROR 목록 5개까지), 검증 먼저/검증 뒤 바뀜 확인, 체크포인트 지우기 확인.
- **기타**:
  - **Dry Run**: 원본 읽기·변환·매핑까지 실제로 하고, 쓰기 문은 만들기만 한다. 배치마다 대상에서 키 존재만 조회(`SELECT 키 FROM 대상 WHERE 키 IN (…)`)해 예상 입력/갱신 수를 세고, NOT NULL·길이·형식 거부는 엔진이 세서 "예상 Inserted / Updated / Rejected"로 보인다. 체크포인트는 남기지 않는다.
  - **일시 오류 재시도**: ORA-03113·03114·12170·12571 등 연결 오류는 진행 중 배치 롤백 → 다시 연결 → 체크포인트부터 다시 읽기, 3회까지(정책 "오류 시 중지"면 바로 멈춤).
  - **행 오류**: 정책 "계속"은 `LOG ERRORS`로 남기고 계속, "중지"·"3회 재시도"는 그 배치를 롤백하고 멈춤(데이터 오류는 재시도로 풀리지 않음).
  - **창을 닫을 때**: 이관은 하위 프로세스에서 계속된다(묻지 않음). 다시 열면 FUNC12로 진행 화면에 다시 붙는다. Folderss를 닫을 때는 마이그레이션 설정의 "Folderss를 닫을 때"(계속 실행 · 함께 중지)를 따른다(README 4.5).
  - **진행률**: 분모는 원본 범위 행 수(통계 또는 FUNC07 of UI-MIG-004), 재개면 이미 끝난 행을 포함해 68%부터 시작. 남은 시간 = 남은 행 ÷ 최근 처리 속도(지수 평활).
  - **UI 갱신**: 엔진 이벤트를 렌더 프레임마다 한 번 모아 그린다(10Hz 이상 다시 그리지 않음). 체크포인트 카드는 0.5초마다.
