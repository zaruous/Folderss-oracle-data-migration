# UI-MIG-001. 접속 · 이관 전략

## 1. 화면 내용 요약

- **목적**: 이 작업의 원본·대상 접속을 고르고(접속 자체는 마이그레이션 설정에 정의), 스키마를 정한 뒤 메타데이터(테이블·컬럼·제약·통계)를 불러온다. 이관 전체에 쓰는 전략(실행 방식·커밋 단위·Fetch 크기·오류 처리·병렬 작업자)을 정한다.
- **주요 기능**:
  - 원본·대상 접속 카드: 마이그레이션 설정의 접속 고르기 · 접속 요약(DB 종류·주소·사용자·비밀번호 저장 여부·색 표시) · 스키마(작업별)
  - [접속 관리…] → 마이그레이션 설정(UI-MIG-008)의 접속 탭. DB Helper 접속과는 공유하지 않음
  - 접속 테스트(`✔ Connected · Oracle 19c · 23 ms`)와 메타데이터 불러오기(캐시 표시)
  - 이관 전략: 전체/증분/CDC, 증분 기준(PK·Timestamp·Sequence·SCN), 커밋 단위, Fetch 크기, 병렬 작업자, 오류 정책, [설정 기본값으로]
  - 안전: 원본은 읽기 전용, "쓰기 금지" 접속은 대상으로 못 고름, 대상이 운영(빨강)이면 경고
  - 다른 PC에서 연 작업: 설정에 없는 접속이면 [접속 설정에 추가]

---

## 2. 화면 이미지 (와이어프레임)

```
+--------------------------------------------------------------------------------------------------+
| STEP 1  접속                                        [⚙ 마이그레이션 설정…] [🔗 두 접속 모두 테스트] |
| 마이그레이션 설정에 정의한 접속 중 원본·대상을 고르고 스키마를 정한 뒤 메타데이터를 불러옵니다.      |
+--------------------------------------------------------------------------------------------------+
| [필터 영역] 없음                                                                                  |
+-----------------------------------------------+     +--------------------------------------------+
| [SOURCE] 원본          [LEGACY_PROD] [읽기 전용]|     | [TARGET] 대상                  [NEXT_PROD] |
|  *접속 (마이그레이션 설정)                      |     |  *접속 (마이그레이션 설정)                  |
|  [LEGACY_PROD 10.10.10.21/LEGACY (쓰기 금지)▼][⚙ 접속 관리…]|[NEXT_PROD 10.20.10.35/NEXTDB ▼][⚙ 접속 관리…]|
|  +-------------------------------------------+ | (→) | ⚠ 운영 DB에 씁니다. TRUNCATE·DELETE 방식은  |
|  | DB 종류  Oracle                            | |     |   실행 직전에 확인 체크를 한 번 더 받습니다. |
|  | 주소     10.10.10.21:1521/LEGACY           | |     |  | DB 종류 Oracle                         | |
|  | 사용자   LEGACY_APP                        | |     |  | 주소    10.20.10.35:1521/NEXTDB        | |
|  | 비밀번호 저장됨(DPAPI)                      | |     |  | 사용자  NEXT_APP · 비밀번호 저장됨      | |
|  | 색 표시  [LEGACY_PROD] [쓰기 금지]          | |     |  | 색 표시 [NEXT_PROD]                    | |
|  +-------------------------------------------+ |     |  *스키마 [NEXT_APP                    ]   |
|  *스키마 [LEGACY_APP                       ]   |     |                                            |
|-----------------------------------------------|     |--------------------------------------------|
|  [접속 테스트] [메타데이터 다시 불러오기]       |     |  [접속 테스트] [메타데이터 다시 불러오기]   |
|  ✔ Connected  Oracle 19c  23 ms  14:20:31      |     |  ● 이번 창에서 아직 시험하지 않음           |
|  ▦ LEGACY_APP · 테이블 6 · 뷰 1 · 컬럼 33 (캐시)|     |  ▦ NEXT_APP · 테이블 6 · 컬럼 30 (캐시)     |
+-----------------------------------------------+     +--------------------------------------------+
+--------------------------------------------------------------------------------------------------+
| [본문 영역]  ⚙ 이관 전략   이 작업의 기본값                                     [설정 기본값으로] |
|  실행 방식(Execution Mode)                         체크포인트 / 증분 기준                          |
|  [ 전체 이관 | 증분 이관 | CDC 변경 동기화 ]        [ Primary Key | Timestamp | Sequence | SCN ]   |
|  트랜잭션 단위(Commit)                             Fetch 크기                                      |
|  [10,000 rows / commit        ▼]                   [5,000 rows / fetch            ▼]               |
|  병렬 작업자(Parallel Workers)                     오류 처리(Error Policy)                         |
|  [ 1 | 2 | 4 | 8 ]  (세션 8개)                      (●) 계속 + 오류 테이블          [ERR$_   ]     |
|                                                    ( ) 오류 시 중지                                 |
|                                                    ( ) 3회 재시도                                   |
+--------------------------------------------------------------------------------------------------+
| [‹ 이전]            접속은 이 플러그인의 설정에만 저장합니다(DB Helper와 공유하지 않음). [다음: 테이블 매핑] |
+--------------------------------------------------------------------------------------------------+
```

설정에 없는 접속(다른 PC에서 연 작업)이면 카드 위에:

```
| ⚠ 이 PC의 마이그레이션 설정에 NEXT_PROD (10.20.10.35/NEXTDB) 접속이 없습니다. [접속 설정에 추가 ›] |
```

---

## 3. 버튼 설명

| 버튼명 | 기능 설명 | 주요 로직 |
|--------|----------|-----------|
| 마이그레이션 설정… | 설정 대화상자 | UI-MIG-008(접속 탭) |
| 두 접속 모두 테스트 | 원본·대상 접속을 동시에 시험 | FUNC02를 원본·대상 병렬 호출. 둘 다 성공이면 알림 "두 접속 모두 연결됨", 실패는 ORA 메시지 |
| 접속(선택) | 설정의 접속 중 하나를 이 작업에 씀 | 작업에는 `profileId` + 스키마(접속의 기본 스키마)만 저장. 대상 쪽은 "쓰기 금지" 접속이 꺼져 있음. 바꾸면 시험 결과·메타데이터를 비움 |
| 접속 관리… | 고른 접속을 설정에서 편집 | UI-MIG-008 접속 탭, 그 접속이 선택된 채로 |
| 스키마(입력) | 이 작업에서 읽고/쓸 스키마 | 비우면 접속의 기본 스키마. 바꾸고 칸을 떠나면 메타데이터를 다시 불러오게 함 |
| 접속 테스트 | 연결·DB 버전·왕복 시간 확인 | FUNC02. 성공: `✔ Connected · Oracle 19c · 23 ms`, 실패: `ORA-01017 …` 빨간 글자. 원본은 읽기 전용 트랜잭션으로 연다 |
| 메타데이터 불러오기 | 스키마의 테이블·뷰·컬럼·제약·통계를 읽어 캐시 | 시험이 안 됐으면 FUNC02 먼저 → FUNC03·04·05·06. 끝나면 `테이블 n · 뷰 n · 컬럼 n (시각 · ms)` 표시, `DataDirectory\metadata\<접속>_<스키마>.json`에 캐시 |
| 접속 설정에 추가 › | 작업 사본(이름·주소)으로 설정에 접속을 만듦 | 비밀번호는 비운 채 UI-MIG-008을 열어 입력받음 |
| 실행 방식(전체/증분/CDC) | 기본 실행 방식 선택 | 증분이면 "증분 기준" 선택지 표시. CDC는 1차 범위 밖 안내만 |
| 트랜잭션 단위 · Fetch 크기 | 배치 크기 선택 | 커밋 단위 = 배열 바인드 크기(ArrayBindCount). Fetch = 원본 한 번에 가져올 행 |
| 병렬 작업자 | 1·2·4·8 | 체크포인트 키 범위를 나눠 작업자별 세션(원본 1 + 대상 1) |
| 오류 처리 | 계속+오류 테이블 / 오류 시 중지 / 3회 재시도 | "계속"이면 오류 테이블 접두어 입력(기본 `ERR$_`) |
| 설정 기본값으로 | 설정의 기본값을 이 작업 전략에 덮어씀 | UI-MIG-008 기본값 탭의 값 |
| 다음: 테이블 매핑 | 다음 단계로 이동 | 검사 없이 이동(검사는 UI-MIG-005) |

---

## 4. 서비스 처리

| 기능ID | 기능명 | 메소드 | 서비스주소 | 입력파라미터 | 기능설명 | 참조 테이블 | SQL |
|--------|--------|--------|-----------|-------------|----------|-------------|-----|
| FUNC01 | 접속 목록 | GET | /v1/migration/settings/connections | 없음 | 이 플러그인 설정의 접속 목록(비밀번호는 DPAPI로 풀어 메모리에만). 구현: `MigrationSettingsStore.Load().Connections` | (파일) `%LOCALAPPDATA%\Folderss\plugin-data\zaruous.folderss-oracle-migration\settings.json` 키 `migration-settings` | 해당 없음(설정 파일) |
| FUNC02 | 접속 테스트 | POST | /v1/migration/connection/test | profileId, schema, role | 연결 → 버전·현재 스키마·왕복 시간. 원본은 `SET TRANSACTION READ ONLY`. 구현: `IConnectionService.TestAsync` | V$VERSION, DUAL | `SELECT BANNER FROM V$VERSION WHERE ROWNUM = 1` → SQL-0 |
| FUNC03 | 테이블·뷰 목록 | GET | /v1/migration/metadata/tables | owner | 테이블·뷰·행 수(통계)·평균 행 길이·주석. 구현: `IMetadataService.LoadAsync` | ALL_TABLES, ALL_VIEWS, ALL_TAB_COMMENTS | SQL-1 |
| FUNC04 | 컬럼 목록 | GET | /v1/migration/metadata/columns | owner | 형식·길이(BYTE/CHAR)·정밀도·NULL 허용·기본값·주석 | ALL_TAB_COLUMNS, ALL_COL_COMMENTS | SQL-2 |
| FUNC05 | 제약 조건 | GET | /v1/migration/metadata/constraints | owner | PK·UK·FK(참조 테이블) — 병합 키 추천·실행 순서·검증에 씀 | ALL_CONSTRAINTS, ALL_CONS_COLUMNS | SQL-3 |
| FUNC06 | 문자 집합 | GET | /v1/migration/connection/nls | role | DB 문자 집합(원본·대상이 다르면 문자 열 길이 검사에 반영) | NLS_DATABASE_PARAMETERS | `SELECT PARAMETER, VALUE FROM NLS_DATABASE_PARAMETERS WHERE PARAMETER IN ('NLS_CHARACTERSET', 'NLS_NCHAR_CHARACTERSET', 'NLS_LENGTH_SEMANTICS')` |
| FUNC07 | 열 통계 | GET | /v1/migration/metadata/column-stats | owner, table | NULL 수·평균 길이(통계) — 검증 화면의 실측 표시 기초 | ALL_TAB_COL_STATISTICS | `SELECT COLUMN_NAME, NUM_NULLS, NUM_DISTINCT, AVG_COL_LEN, LAST_ANALYZED FROM ALL_TAB_COL_STATISTICS WHERE OWNER = :OWNER AND TABLE_NAME = :TABLE_NAME` |
| FUNC08 | 작업 접속 저장 | PUT | /v1/migration/job/connection | role, profileId, schema | 작업에 접속 참조와 스키마 저장(작업 파일에는 이름·주소 사본도) | — | 해당 없음(작업 파일) |

### 4.1 SQL 상세

**SQL-0 접속 테스트**

```sql
-- 1) 버전
SELECT BANNER FROM V$VERSION WHERE ROWNUM = 1;
-- 2) 접속 정보
SELECT SYS_CONTEXT('USERENV', 'DB_NAME')        AS DB_NAME,
       SYS_CONTEXT('USERENV', 'SERVICE_NAME')   AS SERVICE_NAME,
       SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') AS CURRENT_SCHEMA
FROM DUAL;
-- 3) 왕복 시간: 아래 문장을 3번 실행한 중앙값(ms)
SELECT 1 FROM DUAL;
-- 4) 원본 세션만
SET TRANSACTION READ ONLY;
```

**SQL-1 테이블·뷰 목록**

```sql
SELECT T.TABLE_NAME AS OBJECT_NAME, 'TABLE' AS KIND, T.NUM_ROWS, T.AVG_ROW_LEN, T.LAST_ANALYZED,
       T.TABLESPACE_NAME, C.COMMENTS
FROM ALL_TABLES T
LEFT JOIN ALL_TAB_COMMENTS C
       ON C.OWNER = T.OWNER AND C.TABLE_NAME = T.TABLE_NAME
WHERE T.OWNER = :OWNER
  AND T.NESTED = 'NO'
  AND T.SECONDARY = 'N'
  AND T.TABLE_NAME NOT LIKE 'BIN$%'
UNION ALL
SELECT V.VIEW_NAME, 'VIEW', NULL, NULL, NULL, NULL, C.COMMENTS
FROM ALL_VIEWS V
LEFT JOIN ALL_TAB_COMMENTS C
       ON C.OWNER = V.OWNER AND C.TABLE_NAME = V.VIEW_NAME
WHERE V.OWNER = :OWNER
ORDER BY 1
```

**SQL-2 컬럼 목록**

```sql
SELECT C.TABLE_NAME, C.COLUMN_ID, C.COLUMN_NAME, C.DATA_TYPE, C.DATA_LENGTH, C.CHAR_LENGTH, C.CHAR_USED,
       C.DATA_PRECISION, C.DATA_SCALE, C.NULLABLE, C.DATA_DEFAULT, CC.COMMENTS
FROM ALL_TAB_COLUMNS C
LEFT JOIN ALL_COL_COMMENTS CC
       ON CC.OWNER = C.OWNER AND CC.TABLE_NAME = C.TABLE_NAME AND CC.COLUMN_NAME = C.COLUMN_NAME
WHERE C.OWNER = :OWNER
ORDER BY C.TABLE_NAME, C.COLUMN_ID
```

**SQL-3 제약 조건(PK·UK·FK)**

```sql
SELECT K.TABLE_NAME, K.CONSTRAINT_NAME, K.CONSTRAINT_TYPE, KC.COLUMN_NAME, KC.POSITION,
       R.TABLE_NAME AS REF_TABLE, R.CONSTRAINT_NAME AS REF_CONSTRAINT
FROM ALL_CONSTRAINTS K
JOIN ALL_CONS_COLUMNS KC
  ON KC.OWNER = K.OWNER AND KC.CONSTRAINT_NAME = K.CONSTRAINT_NAME
LEFT JOIN ALL_CONSTRAINTS R
  ON R.OWNER = K.R_OWNER AND R.CONSTRAINT_NAME = K.R_CONSTRAINT_NAME
WHERE K.OWNER = :OWNER
  AND K.CONSTRAINT_TYPE IN ('P', 'U', 'R')
  AND K.STATUS = 'ENABLED'
ORDER BY K.TABLE_NAME, K.CONSTRAINT_NAME, KC.POSITION
```

---

## 5. 기타 특이사항

- **유효성 검사**:
  - 원본·대상 접속과 스키마는 필수. 접속을 고르지 않으면 단계 막대가 오류(!)이고 실행·검증이 "마이그레이션 설정에서 접속을 고르세요"로 멈춘다.
  - "쓰기 금지" 접속은 대상 목록에서 꺼져 있고, 다른 경로로 들어와도 검증 C01이 ERROR로 막는다.
  - 접속이나 스키마를 바꾸면 시험 결과를 "이번 창에서 아직 시험하지 않음"으로 되돌리고 메타데이터를 비운다(다시 불러오기).
  - 시험 실패 메시지는 Oracle 오류를 그대로 보인다: ORA-12545(호스트), ORA-12541(리스너·포트), ORA-12514(서비스명), ORA-01017(사용자·비밀번호), ORA-12170(시간 초과).
- **권한 처리**:
  - 원본 계정: 대상 테이블 SELECT. `ALL_*` 사전 뷰는 권한 있는 객체만 보인다.
  - 대상 계정: INSERT·UPDATE·DELETE, 오류 테이블·제어 테이블용 CREATE TABLE(없으면 체크포인트는 로컬 파일, README 6.5). `TRUNCATE + INSERT`는 테이블 소유자이거나 DROP ANY TABLE 권한 필요.
- **팝업**: 마이그레이션 설정(UI-MIG-008).
- **기타**:
  - 접속 정보(호스트·포트·서비스·사용자·비밀번호)는 플러그인 설정에만 있고 작업 파일에는 참조(`profileId`)와 비밀번호 없는 사본만 들어간다. DB Helper 접속 목록과는 공유하지 않는다.
  - 작업을 다른 PC에서 열면 `profileId`가 없으므로 **접속 이름**으로 설정의 접속을 찾는다. 없으면 [접속 설정에 추가]로 사본에서 만든다(비밀번호는 다시 입력).
  - 원본·대상 문자 집합이 다르면(예: `KO16MSWIN949` → `AL32UTF8`) 한글은 1자당 2→3바이트가 된다. 대상 열이 `BYTE` 길이(`CHAR_USED = 'B'`)면 검증(UI-MIG-005)에서 바이트 기준으로 잘림을 검사한다.
  - 메타데이터는 접속·스키마별로 캐시하고, 화면에 `(캐시 · 시각)`으로 표시한다. 테이블이 수천 개인 스키마는 DB Helper 트리처럼 처음 1,000개 + 검색으로 보인다.
