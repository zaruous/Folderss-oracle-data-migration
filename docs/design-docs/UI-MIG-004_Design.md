# UI-MIG-004. SQL 원본 편집기 (도구 창)

> **단계가 아니라 도구다.** 이관 단계(접속 → 테이블 매핑 → 컬럼 매핑 → 검증 → 실행)와 상관없이, 원본이 SQL인 매핑이 있을 때 꺼내 쓰는 별도 창이다. 단계 막대에는 두지 않는다.
> WPF 구현은 DB Helper의 테이블 정보 창처럼 **비모달 별도 창**(`SqlSourceEditorWindow`) — 컬럼 매핑 화면과 나란히 두고 쓸 수 있다. POC는 화면을 거의 채우는 대화상자로 흉내 낸다.

## 1. 화면 내용 요약

- **목적**: 원본이 SQL인 매핑(`sourceType: SQL`)의 SELECT 문을 쓰고 검증·미리보기 한다. 여러 원본 테이블 JOIN, CASE/DECODE, GROUP BY·집계, 서브쿼리, 코드 변환, 문자열 정제, 날짜 변환, N개 원본 → 1개 대상처럼 테이블 하나로 안 되는 원본을 SELECT로 만들고, **결과 별칭이 원본 컬럼**이 된다.
- **여는 곳**: 테이블 매핑의 SQL 원본 이름(링크) · [SQL 원본 추가] 직후 · 컬럼 매핑 머리의 SQL 원본 이름 · 검증 결과 [SQL 편집 ›] · `매핑 > SQL 원본 편집기…`(`Ctrl+Q`) · 아이콘 막대 `{}`.
- **주요 기능**:
  - SQL 원본 고르기·추가·삭제(창 위쪽)
  - SQL 편집기(줄 번호, Oracle 구문 강조, Tab = 공백 4칸, `Ctrl+Enter` = 검증)
  - SQL 검증: 구문 · 원본 객체·열 · 결과 열 수 · 별칭 · 바인드 변수 · 대상 컬럼 매핑 · 형식 호환성 · NULL 처리 · 병합 키 · 체크포인트
  - 100행 미리보기(대상에는 쓰지 않음)
  - Alias 매핑: 결과 별칭 → 대상 컬럼(이름이 같으면 자동). **컬럼 매핑(UI-MIG-003)과 같은 데이터** — 변환식·NULL 처리는 컬럼 매핑에서 더한다
  - 설정: 대상 테이블 · 쓰기 방식(MERGE 등) · 병합 키 · Fetch 크기 · 커밋 크기 · 체크포인트 컬럼 · 사용 · 바인드 변수(`:LAST_ID = 850000`, CP = 체크포인트 값 사용)
  - 생성 SQL: 엔진이 실행하는 원본 SELECT(사용자 SQL을 인라인 뷰로 감쌈)와 대상 쓰기 문

---

## 2. 화면 이미지 (와이어프레임)

```
+-----------------------------------------------------------------------------------------------------------+
| SQL 원본 편집기 — SQLMAP_MEMBER                                                                        [×]|
+-----------------------------------------------------------------------------------------------------------+
| [필터 영역] SQL 원본 [SQLMAP_MEMBER → TB_MEMBER (사용 안 함) ▼] [+ SQL 원본 추가] [🗑]                       |
|             결과 별칭이 원본 컬럼이 됩니다 · 변환식·NULL 처리는 컬럼 매핑에서 · Ctrl+Enter 검증           |
+------------------------------------------------------------------------+----------------------------------+
| [본문 영역] {} [SQLMAP_MEMBER ]  [✓ SQL 검증][👁 100행 미리보기][✦ Alias 자동 매핑] | ⚙ SQL 원본 설정       |
| +----+------------------------------------------------------------+    |  *대상 테이블 (Target Table)      |
| |  1 | SELECT                                                     |    |  [TB_MEMBER                   ▼] |
| |  2 |     C.CUSTOMER_ID AS MEMBER_ID,                            |    |   NEXT_APP · 7열 · 기존 58,225행  |
| |  4 |     TRIM(C.CUSTOMER_NM) AS MEMBER_NAME,                    |    |  쓰기 방식 (Write Strategy)       |
| |  7 |     REGEXP_REPLACE(C.PHONE_NO, '[^0-9]', '') AS MOBILE_NO, |    |  [MERGE (INSERT + UPDATE)     ▼] |
| | 13 |     CASE WHEN C.STATUS_CD = 'A' THEN 'Y' ELSE 'N' END AS USE_YN, |  병합 키 (MEMBER_ID ×)[+ 키 추가▼]|
| | 19 |     G.GRADE_NM AS MEMBER_GRADE,                            |    |  Fetch [5000]   커밋 [10000]      |
| | 21 |     CAST(C.REG_DT AS TIMESTAMP) AS CREATED_AT,             |    |  체크포인트 컬럼                  |
| | 25 |     CAST(NVL(C.MOD_DT, C.REG_DT) AS TIMESTAMP) AS UPDATED_AT |  |  [MEMBER_ID  NUMBER(12)       ▼] |
| | 30 | FROM SRC_CUSTOMER C                                        |    |  ☐ 이번 작업에서 사용             |
| | 32 | LEFT JOIN SRC_CUSTOMER_GRADE G ON G.GRADE_CD = C.GRADE_CD  |    +----------------------------------+
| | 35 | WHERE C.CUSTOMER_ID > :LAST_ID                             |    | : 바인드 변수                     |
| +----+------------------------------------------------------------+    |  이름      형식     값       CP   |
| ⚠ SQL Valid  Result Columns : 7 · Target Mapping : 7 / 7 ·            |  :LAST_ID [NUMBER▼] [850000] ☑   |
|   Bind Parameter : :LAST_ID · 경고 2                                   |  체크포인트: CUSTOMER_ID = 850000 |
| 줄 35, 열 30 · 결과 열 7 · FROM SRC_CUSTOMER, SRC_CUSTOMER_GRADE · 약 390,325행 |                         |
+------------------------------------------------------------------------+----------------------------------+
| [검증 결과 10] [미리보기 100] [Alias 매핑 7/7] [생성 SQL]                                                  |
| +--------+----------------+----------------------------------------------------------------------+        |
| | 결과   | 검사           | 내용                                                                 |        |
| | PASS   | SQL 구문       | SELECT · 결과 열 7개 · FROM SRC_CUSTOMER C, SRC_CUSTOMER_GRADE G      |        |
| | PASS   | 대상 컬럼 매핑 | 7 / 7 매핑                                                           |        |
| | WARN   | 형식 호환성    | MOBILE_NO → MOBILE_NO: VARCHAR2(30) → VARCHAR2(20) 잘림 위험 · 실측 13자 |     |
| | WARN   | NULL 처리      | MEMBER_NAME: NULL 37행은 거부되어 오류 테이블로 감                    |        |
| | PASS   | 체크포인트     | SQL을 감싸 ORDER BY S.MEMBER_ID로 읽고, 재개할 때 :LAST_ID에 넣음      |        |
| +--------+----------------+----------------------------------------------------------------------+        |
+-----------------------------------------------------------------------------------------------------------+
|                                                       [컬럼 매핑에서 변환식 ›] [닫기]                     |
+-----------------------------------------------------------------------------------------------------------+
```

**Alias 매핑 탭** (컬럼 매핑과 같은 데이터)

```
+--------------+-----------------------------+--------------+---+------------------------------+-----------+--------------------------+
| SQL 결과 열   | SELECT 식                   | 추정 형식     |   | 대상 컬럼 (TB_MEMBER)          | 변환식    | 형식 검사                 |
+--------------+-----------------------------+--------------+---+------------------------------+-----------+--------------------------+
| MEMBER_ID    | C.CUSTOMER_ID               | NUMBER(12)   | → | [MEMBER_ID  NUMBER(18)     ▼] | —         | OK  호환(정밀도 넓어짐)    |
| MOBILE_NO    | REGEXP_REPLACE(C.PHONE_NO,…)| VARCHAR2(30) | → | [MOBILE_NO  VARCHAR2(20)   ▼] | —         | WARN 잘림 위험 · 실측 13자 |
| USE_YN       | CASE WHEN C.STATUS_CD = …   | CHAR(1)      | → | [USE_YN     CHAR(1)        ▼] | —         | OK                        |
+--------------+-----------------------------+--------------+---+------------------------------+-----------+--------------------------+
 값이 없는 대상 컬럼: (없음)   ← NOT NULL이고 기본값이 없으면 (NN) 강조
```

---

## 3. 버튼 설명

| 버튼명 | 기능 설명 | 주요 로직 |
|--------|----------|-----------|
| SQL 원본(선택) | 다른 SQL 원본으로 바꿈 | `이름 → 대상 (사용 안 함)`. 창 제목도 바뀜 |
| + SQL 원본 추가 | 새 SQL 원본 매핑 | 창을 닫고 UI-MIG-002 팝업 ①(원본 종류 SQL)을 연 뒤, 추가하면 이 창이 다시 열림 |
| 🗑 (삭제) | SQL 원본 매핑 삭제 | 확인 창 → 검증 결과·체크포인트도 삭제. 남은 SQL 원본이 없으면 창을 닫음 |
| SQL 검증 (`Ctrl+Enter`) | 문장 검사 | FUNC01·02·03. 처음 검증할 때 결과 열을 같은 이름의 대상 컬럼에 자동 연결. 결과 배너: `✔ SQL Valid · Result Columns : n · Target Mapping : m / k · Bind Parameter : :LAST_ID`, 오류면 첫 오류와 편집기 줄 번호 빨강. 검증 뒤 SQL을 바꾸면 "검증한 뒤 SQL이 바뀌었습니다" |
| 100행 미리보기 | 원본에서 결과 100행 | FUNC04. 대상에는 쓰지 않음. 소요 ms, 시작 위치(`WHERE로 850,001번부터`), 식 오류 칸 수 표시 |
| Alias 자동 매핑 | 별칭 = 대상 컬럼 이름이면 연결 | FUNC05. 알림 "별칭 7 / 7개를 같은 이름의 대상 컬럼에 이었습니다" |
| 대상 테이블 | 대상 고르기 | 병합 키 = 대상 PK, 컬럼 자동 매핑 다시 |
| 쓰기 방식 · 병합 키 | MERGE / INSERT ONLY / TRUNCATE + INSERT / DELETE + INSERT, 키 칩 추가·빼기 | 키가 필요 없는 방식이면 "필요 없음" |
| Fetch 크기 · 커밋 크기 | 이 SQL 원본만의 배치 크기 | 비우면 작업 기본값(UI-MIG-001) |
| 체크포인트 컬럼 | 결과 열 중 하나 | 감싼 SQL을 이 열 순서로 읽고 커밋마다 마지막 값 저장 |
| 바인드 변수 표 | 형식·값·CP(체크포인트 값 사용) | SQL에서 `:이름`을 찾아 자동으로 줄 추가(0.4초 멈춘 뒤, `LAST_`로 시작하면 CP 기본 체크). SQL에서 없어진 변수는 흐리게 |
| 탭: 검증 결과 · 미리보기 · Alias 매핑 · 생성 SQL | 결과 보기 | 생성 SQL 탭에 [복사] |
| 이번 작업에서 사용 | 실행 화면 기본 선택에 포함 | |
| 컬럼 매핑에서 변환식 › | 이 매핑의 컬럼 매핑 화면으로 | 창은 그대로(WPF는 비모달이라 나란히) |
| 닫기 / Esc | 창 닫기 | 바뀐 내용은 이미 작업에 반영됨 |

---

## 4. 서비스 처리

| 기능ID | 기능명 | 메소드 | 서비스주소 | 입력파라미터 | 기능설명 | 참조 테이블 | SQL |
|--------|--------|--------|-----------|-------------|----------|-------------|-----|
| FUNC01 | SQL 구문 검사 | POST | /v1/migration/sql/parse | sql | Oracle에 PARSE만 시킨다(실행 안 함). 오류는 ORA 코드·위치(줄) | 원본 | SQL-1 |
| FUNC02 | 결과 열 조회(DESCRIBE) | POST | /v1/migration/sql/describe | sql, binds | 행을 읽지 않고 결과 열 이름·형식·길이·NULL 허용. 구현: `OracleDataReader.GetSchemaTable()` (`CommandBehavior.SchemaOnly`). 결과 열 = 컬럼 매핑의 원본 컬럼(가상 테이블) | 원본 | SQL-2 |
| FUNC03 | SQL 원본 검증 | POST | /v1/migration/sql/validate | mappingId | FUNC01·02 결과로 결과 열 수·별칭(중복·없음)·바인드 값·체크포인트를 보고, 컬럼 검사(UI-MIG-003 규칙)로 대상 매핑·형식·NULL·병합 키 검사 | 대상 ALL_TAB_COLUMNS(캐시) | 해당 없음 — FUNC01·02 결과 사용 |
| FUNC04 | 100행 미리보기 | POST | /v1/migration/sql/preview | sql, binds, limit=100 | 원본에서 앞 100행(대상 쓰기 없음) | 원본 | SQL-3 |
| FUNC05 | Alias 자동 매핑 | POST | /v1/migration/sql/auto-map-alias | mappingId | 별칭과 대상 컬럼 이름이 같으면 그 대상 컬럼의 원본을 별칭으로 | (캐시) | 해당 없음 |
| FUNC06 | 생성 SQL | GET | /v1/migration/sql/generate | mappingId | 사용자 SQL을 인라인 뷰 S로 감싼 원본 SELECT + 대상 쓰기 문(UI-MIG-003 SQL-4와 같은 규칙) | — | SQL-5 |
| FUNC07 | 예상 행 수 | POST | /v1/migration/sql/estimate-rows | sql, binds | 테이블 매핑의 행 수·실행 진행률의 분모. 비싸면 생략하고 주 테이블 통계로 추정(`~390,325`) | 원본 | SQL-4 |
| FUNC08 | SQL 원본 저장 | PUT | /v1/migration/mapping/sql-source | mappingId, source, sql, target, mode, mergeKey, fetchSize, commitSize, checkpointColumn, binds, columns | 작업에 반영 | — | 해당 없음(작업 파일) |

### 4.1 SQL 상세

**SQL-1 구문 검사(실행하지 않고 PARSE만)**

```sql
DECLARE
    C INTEGER := DBMS_SQL.OPEN_CURSOR;
BEGIN
    DBMS_SQL.PARSE(C, :SQL_TEXT, DBMS_SQL.NATIVE);   -- 구문·객체·권한 오류는 여기서 ORA-xxxxx
    DBMS_SQL.CLOSE_CURSOR(C);
EXCEPTION
    WHEN OTHERS THEN
        IF DBMS_SQL.IS_OPEN(C) THEN DBMS_SQL.CLOSE_CURSOR(C); END IF;
        RAISE;
END;
```

오류 위치는 `DBMS_SQL.LAST_ERROR_POSITION`으로 받아 줄 번호로 바꾼다. (SELECT만 허용: 문장 앞 단어가 `SELECT`·`WITH`가 아니면 PARSE 전에 막는다 — DDL은 `DBMS_SQL.PARSE` 단계에서 바로 실행되기 때문이다.)

**SQL-2 결과 열 조회(행 없이)**

```sql
SELECT * FROM (
    /* 사용자 SQL */
    SELECT C.CUSTOMER_ID AS MEMBER_ID, TRIM(C.CUSTOMER_NM) AS MEMBER_NAME, ...
    FROM SRC_CUSTOMER C
    LEFT JOIN SRC_CUSTOMER_GRADE G ON G.GRADE_CD = C.GRADE_CD
    WHERE C.CUSTOMER_ID > :LAST_ID
) WHERE 1 = 0
```

**SQL-3 100행 미리보기**

```sql
SELECT * FROM (
    /* 사용자 SQL */
) S
ORDER BY S.MEMBER_ID            -- 체크포인트 컬럼
FETCH FIRST 100 ROWS ONLY
```

**SQL-4 예상 행 수**

```sql
SELECT COUNT(*) FROM (
    /* 사용자 SQL에서 ORDER BY를 뺀 것 */
)
```

**SQL-5 엔진이 실행하는 원본 SELECT(감싼 모양)**

```sql
-- 원본 SQL을 인라인 뷰 S로 감싸 변환식·NULL 처리·체크포인트를 붙임
SELECT
    MEMBER_ID AS MEMBER_ID,
    MEMBER_NAME AS MEMBER_NAME,
    NVL(USE_YN, 'Y') AS USE_YN,          -- 컬럼 매핑의 NULL 처리(기본값)
    MEMBER_GRADE AS MEMBER_GRADE
FROM (
    SELECT C.CUSTOMER_ID AS MEMBER_ID, ... FROM SRC_CUSTOMER C LEFT JOIN SRC_CUSTOMER_GRADE G ON G.GRADE_CD = C.GRADE_CD
    WHERE C.CUSTOMER_ID > :LAST_ID      -- CP 바인드가 SQL 안에 있으면 그대로 씀
) S
WHERE S.MEMBER_ID <= :RANGE_TO          -- 작업자 2개 이상일 때
ORDER BY S.MEMBER_ID
```

SQL 안에 CP 바인드 변수가 없으면 바깥에 `WHERE S.MEMBER_ID > :LAST_ID`를 붙인다(검증에서 INFO: 큰 원본이면 SQL 안 WHERE에 넣어야 인덱스를 탐).

---

## 5. 기타 특이사항

- **유효성 검사**(검증 결과 항목):

  | 검사 | ERROR | WARN · INFO |
  |---|---|---|
  | SQL 구문 | ORA 오류(줄 번호), SELECT·WITH 외 문장 | WITH·GROUP BY·UNION은 POC 미리보기 한계 안내 |
  | 원본 객체·열 | ORA-00942 테이블 없음, ORA-00904 열 없음, ORA-00918 애매한 열 | |
  | 결과 열 수 | 0개 | |
  | 별칭 | 별칭 중복 | 식에 별칭 없음 → `AS 이름` 권장 |
  | 바인드 변수 | 값 없는 변수(ORA-01008) | |
  | 대상 컬럼 매핑 | NOT NULL(기본값 없음) 대상 컬럼에 값 없음 | 대상에 쓰지 않는 결과 열 |
  | 형식 호환성 | 날짜↔숫자 등 | 잘림·정밀도·암시 변환 |
  | NULL 처리 | | NULL 행 거부 예상, NULL 허용인데 NOT NULL 대상 |
  | 병합 키 | 키가 필요한 방식인데 매핑된 키 없음 | |
  | 체크포인트 | | 체크포인트 열이 결과에 없음(WARN) / SQL 안에 CP 바인드 없음 → 바깥에서 거름(INFO) |

- **권한 처리**: 원본 계정으로 실행(읽기 전용 세션). `DBMS_SQL`은 PUBLIC 실행 권한이 기본이다. 사용자가 SELECT 외 문장을 넣어도 원본 세션은 읽기 전용이라 바뀌지 않지만, 창에서 먼저 막는다.
- **팝업**: SQL 원본 매핑 삭제 확인. (창 자체는 WPF에서 비모달 창 — 한 번에 하나, 다시 열면 앞으로 가져와 고른 SQL 원본으로 바꿈)
- **기타**:
  - 단계 막대에 두지 않는 이유: SQL 원본은 매핑의 한 종류일 뿐 이관 순서의 한 단계가 아니다. 테이블 원본만 쓰는 작업에서는 열 일이 없고, SQL 원본이 있는 작업에서는 테이블 매핑·컬럼 매핑·검증 어디서든 바로 연다.
  - 체크포인트 바인드 변수(CP 체크)는 재개할 때 체크포인트 값으로 바뀐다. 처음 실행 때는 입력한 값(예: 850000)을 쓴다.
  - 같은 대상(예: TB_MEMBER)에 테이블 원본 매핑과 SQL 원본 매핑을 모두 사용하면 실행 전 검증에서 경고한다(나중 작업이 앞 작업 값을 덮어씀).
  - 편집기 구문 강조는 POC 기능이다. WPF 구현은 DB Helper처럼 TextBox로 시작하고 AvalonEdit 도입은 결정 필요(README 9장 #6).
