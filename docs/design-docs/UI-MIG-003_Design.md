# UI-MIG-003. 컬럼 매핑

## 1. 화면 내용 요약

- **목적**: 고른 매핑에서 대상 컬럼마다 값을 어디서 가져올지(원본 컬럼 또는 변환식)와 NULL을 어떻게 처리할지 정한다. 변환식 결과를 원본 샘플로 바로 확인하고, 엔진이 실행할 SQL을 보여 준다.
- **원본이 SQL인 매핑**: 원본 컬럼 = SQL 결과 별칭(DESCRIBE 결과). 머리에 `[SQL] SQLMAP_MEMBER (결과 7열, ~390,325행, FROM …)`와 SQL 원본 편집기 링크를 보이고, 샘플은 SQL 미리보기 결과로 계산한다. 원본 SELECT는 사용자 SQL을 인라인 뷰 S로 감싼다(UI-MIG-004 SQL-5).
- **주요 기능**:
  - 테이블 설정 줄: 이관 방식 · 병합 키 · 체크포인트 컬럼 · 원본 조건(WHERE)
  - 컬럼 그리드(대상 컬럼 기준 한 줄씩): 원본 컬럼 · 대상 컬럼(형식·PK·NN) · 변환식 · NULL 처리 · 키 · 검사
  - 이름으로 자동 매핑(같은 이름 → 용어 사전 → 접미어 규칙 `_NM↔_NAME`, `_CD↔_CODE`, `_DT↔_AT`), 추천 변환식
  - 검사기(오른쪽, 좁으면 아래): 변환식 편집 · 함수 조각(TRIM·NVL·REGEXP_REPLACE·CASE·CAST·TO_DATE·DECODE·SUBSTR·UPPER) · 원본 컬럼 넣기 · NULL 처리 · 검사 결과 · 샘플 6행 미리보기
  - 생성 SQL: 원본 SELECT(변환식·NULL 처리·체크포인트 조건 포함) / 대상 쓰기 문(MERGE 등)

---

## 2. 화면 이미지 (와이어프레임)

```
+--------------------------------------------------------------------------------------------------+
| STEP 3  컬럼 매핑                                        [SRC_CUSTOMER → TB_MEMBER  6/7       ▼] |
| LEGACY_APP.SRC_CUSTOMER (1,240,325행, 7열) → NEXT_APP.TB_MEMBER (7열, 기존 58,225행)              |
+--------------------------------------------------------------------------------------------------+
| [필터 영역]                                                                                       |
|  이관 방식: [INSERT + UPDATE ▼]  병합 키: (MEMBER_ID ×)  체크포인트: [CUSTOMER_ID NUMBER(12)·PK ▼] |
|  원본 조건(WHERE): [예: STATUS_CD <> 'D'                    ]  ← 체크포인트 조건과 AND            |
+--------------------------------------------------------------------------------------------------+
| [본문 영역] ≡ 컬럼 6/7 매핑 · 경고 2     [전체|매핑 안 됨|경고·오류] [✦ 이름으로 자동 매핑] [지우기] |
| +-------------------+---+-------------------+-------------------------------+-----------+--+-----+ |
| | 원본 컬럼          |   | 대상 컬럼          | 변환식(Transform)              | NULL 처리  |키| 검사 | |
| +-------------------+---+-------------------+-------------------------------+-----------+--+-----+ |
| | [CUSTOMER_ID   ▼] | → | MEMBER_ID [PK][NN]| 그대로                         | [행 거부▼]|☑ | OK  | |
| |  NUMBER(12)       |   |  NUMBER(18)       |                               |           |  |     | |
| | [CUSTOMER_NM   ▼] | → | MEMBER_NAME  [NN] | TRIM(CUSTOMER_NM) → VARCHAR2(100)| [행 거부▼]|☐ | WARN| |
| | [PHONE_NO      ▼] | → | MOBILE_NO         | REGEXP_REPLACE(PHONE_NO,'[^0-9]','')| [NULL 허용▼]|☐| WARN| |
| | [STATUS_CD     ▼] | → | USE_YN       [NN] | CASE WHEN STATUS_CD='A' THEN 'Y'… | [기본값 ▼]|☐ | OK  | |
| |                   |   |  CHAR(1) DEFAULT 'Y'|                             |  'Y'      |  |     | |
| | [(매핑 안 함)  ▼] | → | MEMBER_GRADE      | —                             | [NULL 허용▼]|☐| INFO| |
| | [REG_DT        ▼] | → | CREATED_AT   [NN] | CAST(REG_DT AS TIMESTAMP)     | [SYSDATE ▼]|☐ | OK  | |
| | [MOD_DT        ▼] | → | UPDATED_AT        | CAST(NVL(MOD_DT, REG_DT) AS TIMESTAMP)| [NULL 허용▼]|☐| OK| |
| +-------------------+---+-------------------+-------------------------------+-----------+--+-----+ |
|  변환식은 원본 SELECT 안에서 Oracle이 계산합니다. 키는 MERGE ON·DELETE 조건에 씁니다.              |
+--------------------------------------------------------------------------------------------------+
| [상세/입력 영역] ▌MOBILE_NO ← PHONE_NO     VARCHAR2(20) · 휴대폰 번호(숫자만)                     |
|  변환식 (Transform Expression)                                                                    |
|  +--------------------------------------------------------------+                                 |
|  | REGEXP_REPLACE(PHONE_NO, '[^0-9]', '')                        |                                 |
|  +--------------------------------------------------------------+                                 |
|  (TRIM)(NVL)(REGEXP_REPLACE)(CASE)(CAST)(TO_DATE)(DECODE)(SUBSTR)(UPPER)   ← 지금 식을 감쌈       |
|  원본 컬럼: (CUSTOMER_ID)(CUSTOMER_NM)(PHONE_NO)(STATUS_CD)(GRADE_CD)(REG_DT)(MOD_DT) ← 커서에 넣기 |
|  NULL 처리: [NULL 허용 ▼]   기본값: [______]                                                      |
|  검사:  [WARN] VARCHAR2(30) → VARCHAR2(20) 잘림 위험 · 실측 최대 13자라 지금 데이터는 들어감       |
|  샘플 미리보기 (원본 6행)                                                                          |
|   원본 값              →  결과 → MOBILE_NO                                                         |
|   010-1234-5678        →  01012345678                                                             |
|   (02)555-0101         →  025550101                                                               |
|   NULL                 →  NULL                                                                    |
|   +82-10-9876-5432     →  821098765432                                                            |
+--------------------------------------------------------------------------------------------------+
| {} 생성 SQL   [원본 SELECT | 대상 쓰기 문 (INSERT + UPDATE)]                               [복사] |
|  -- 원본 읽기: Fetch 5,000행씩 스트리밍                                                            |
|  SELECT CUSTOMER_ID AS MEMBER_ID, TRIM(CUSTOMER_NM) AS MEMBER_NAME, ...                           |
|  FROM LEGACY_APP.SRC_CUSTOMER WHERE CUSTOMER_ID > :LAST_ID ORDER BY CUSTOMER_ID                   |
+--------------------------------------------------------------------------------------------------+
| [‹ 테이블 매핑]                                                              [다음: 검증]          |
+--------------------------------------------------------------------------------------------------+
```

---

## 3. 버튼 설명

| 버튼명 | 기능 설명 | 주요 로직 |
|--------|----------|-----------|
| 테이블 매핑 선택(머리 오른쪽) | 다른 테이블 매핑으로 바꿈 | 목록에 `원본 → 대상  매핑/전체 · 오류` 표시 |
| 이관 방식 · 병합 키 · 체크포인트 · 원본 조건 | 테이블 단위 설정 | 방식이 키를 요구하면 병합 키 칩(×로 빼기), 아니면 "필요 없음". 체크포인트 후보: 원본 NUMBER·DATE·TIMESTAMP·PK 컬럼. 조건은 체크포인트 조건과 AND로 붙음 |
| 전체 / 매핑 안 됨 / 경고·오류 | 그리드 거르기 | 화면 보기만 바꿈 |
| 이름으로 자동 매핑 | 대상 컬럼마다 원본 컬럼·추천 변환식·NULL 처리 채움 | FUNC02. 이미 매핑이 있으면 팝업: [빈 컬럼만 채우기](기본) / [모두 다시 매핑] / [취소] |
| 지우기 | 이 테이블의 컬럼 매핑을 모두 비움 | 확인 창 후 원본·변환식 비움 |
| 원본 컬럼(셀) | 값 원본 고르기 | 비우면 변환식도 비움. 다른 컬럼으로 바꾸면 식 안의 옛 컬럼 이름을 새 이름으로 바꿈. 처음 고르면 형식 차이로 식 추천(DATE→TIMESTAMP면 `CAST(… AS TIMESTAMP)`) |
| NULL 처리(셀) | NULL 허용 · 행 거부 · 기본값 · SYSDATE · 빈 문자열 · 사용자 식 | 기본값·사용자 식이면 셀 아래 값 표시, 값이 없으면 "값 없음" 경고 |
| 키(체크) | 병합 키 포함 | 키가 필요 없는 방식이면 꺼짐 |
| 행 누르기 | 검사기에 그 컬럼 표시 | |
| 함수 조각(TRIM 등) | 지금 식을 그 함수로 감쌈 | 식이 비면 원본 컬럼을 감쌈. 예: `PHONE_NO` → `REGEXP_REPLACE(PHONE_NO, '[^0-9]', '')` |
| 원본 컬럼 조각 | 커서 위치에 컬럼 이름 넣기 | |
| 변환식 입력 | 식 편집 | 입력할 때마다 검사·샘플 갱신(FUNC03·04), 칸을 떠나면 그리드 갱신·작업 저장 |
| 생성 SQL 탭 · 복사 | 원본 SELECT / 대상 쓰기 문 보기 | FUNC06 |
| SQL 원본으로 만들기 › | 테이블 원본에서 매핑 안 한 nullable 컬럼에 표시 | 다른 테이블 JOIN이 필요하면 같은 대상으로 SQL 원본 매핑을 만듦(UI-MIG-002 팝업 ①, 시작 SQL = 이 테이블의 열) |
| SQL 원본 이름(머리) · SQL 원본 편집기 › | 원본이 SQL인 매핑 | SQL 원본 편집기(UI-MIG-004) 열기 |

---

## 4. 서비스 처리

| 기능ID | 기능명 | 메소드 | 서비스주소 | 입력파라미터 | 기능설명 | 참조 테이블 | SQL |
|--------|--------|--------|-----------|-------------|----------|-------------|-----|
| FUNC01 | 컬럼 매핑 조회 | GET | /v1/migration/mapping/columns | mappingId | 대상 컬럼 순서(COLUMN_ID)로 매핑 줄을 만든다. 없는 줄은 기본 NULL 처리(NOT NULL이면 행 거부, DB 기본값 있으면 기본값) | ALL_TAB_COLUMNS(캐시) | 해당 없음 — 캐시 사용(UI-MIG-001 SQL-2) |
| FUNC02 | 이름으로 자동 매핑 | POST | /v1/migration/mapping/auto-map-columns | mappingId, mode(빈 컬럼만/모두) | 같은 이름 → 용어 사전 → 접미어 규칙 순. 원본 한 컬럼은 한 번만. 형식 차이로 변환식 추천. 구현: `MappingService.AutoMapColumns` | (캐시) | 해당 없음 |
| FUNC03 | 변환식 검사 | POST | /v1/migration/mapping/check-column | mappingId, target, source, expr, nullRule, defaultValue | 구문·참조 열 확인과 결과 형식(DESCRIBE)을 Oracle에 맡기고, 형식 호환성·NOT NULL·NULL 처리 규칙을 검사 | 원본 테이블 | `SELECT REGEXP_REPLACE(PHONE_NO, '[^0-9]', '') AS V FROM LEGACY_APP.SRC_CUSTOMER WHERE 1 = 0` (실행 후 GetSchemaTable로 형식·길이) |
| FUNC04 | 샘플 미리보기 | POST | /v1/migration/mapping/preview-column | mappingId, target, expr, nullRule | 원본 6행의 원본 값과 결과(NULL 처리 반영) | 원본 테이블 | SQL-1 |
| FUNC05 | 실측 프로파일 | POST | /v1/migration/metadata/profile-expression | mappingId, target, expr | 결과의 최대 길이(문자·바이트)·NULL 수 — 잘림·NOT NULL 경고의 "실측" 값 | 원본 테이블 | SQL-2 |
| FUNC06 | 생성 SQL | GET | /v1/migration/sql/generate | mappingId | 원본 SELECT(Fetch·작업자 범위 주석 포함)와 대상 쓰기 문. 구현: `SqlGenerator.BuildSourceSelect`, `BuildWriteSql` | — | SQL-3 · SQL-4 |
| FUNC07 | 매핑 저장 | PUT | /v1/migration/mapping/columns | mappingId, columns[], mergeKey, checkpointColumn, where | 작업에 반영(dirty, 임시 저장) | — | 해당 없음(작업 파일) |

### 4.1 SQL 상세

**SQL-1 샘플 미리보기(체크포인트 위치부터 6행)**

```sql
SELECT PHONE_NO                                   AS SRC_1,
       REGEXP_REPLACE(PHONE_NO, '[^0-9]', '')     AS RESULT
FROM LEGACY_APP.SRC_CUSTOMER
WHERE CUSTOMER_ID > :LAST_ID
ORDER BY CUSTOMER_ID
FETCH FIRST 6 ROWS ONLY
```

**SQL-2 실측 프로파일(표본 5%)**

```sql
SELECT COUNT(*)                                                                   AS SAMPLE_ROWS,
       SUM(CASE WHEN REGEXP_REPLACE(PHONE_NO, '[^0-9]', '') IS NULL THEN 1 ELSE 0 END) AS NULL_ROWS,
       MAX(LENGTH(REGEXP_REPLACE(PHONE_NO, '[^0-9]', '')))                         AS MAX_CHARS,
       MAX(LENGTHB(REGEXP_REPLACE(PHONE_NO, '[^0-9]', '')))                        AS MAX_BYTES
FROM LEGACY_APP.SRC_CUSTOMER SAMPLE BLOCK (5)
```

**SQL-3 원본 SELECT(생성 예: SRC_CUSTOMER → TB_MEMBER, 작업자 4)**

```sql
-- 원본 읽기: Fetch 5,000행씩 스트리밍
-- :LAST_ID = 체크포인트(없으면 처음부터)
SELECT
    CUSTOMER_ID AS MEMBER_ID,
    TRIM(CUSTOMER_NM) AS MEMBER_NAME,
    REGEXP_REPLACE(PHONE_NO, '[^0-9]', '') AS MOBILE_NO,
    NVL(CASE
        WHEN STATUS_CD = 'A' THEN 'Y'
        ELSE 'N'
    END, 'Y') AS USE_YN,
    NVL(CAST(REG_DT AS TIMESTAMP), SYSTIMESTAMP) AS CREATED_AT,
    CAST(NVL(MOD_DT, REG_DT) AS TIMESTAMP) AS UPDATED_AT
FROM LEGACY_APP.SRC_CUSTOMER
WHERE CUSTOMER_ID > :LAST_ID
  AND CUSTOMER_ID <= :RANGE_TO
ORDER BY CUSTOMER_ID
```

**SQL-4 대상 쓰기 문(INSERT + UPDATE, 오류 정책 "계속")**

```sql
MERGE INTO NEXT_APP.TB_MEMBER T
USING (
    SELECT
        :MEMBER_ID AS MEMBER_ID,
        :MEMBER_NAME AS MEMBER_NAME,
        :MOBILE_NO AS MOBILE_NO,
        :USE_YN AS USE_YN,
        :CREATED_AT AS CREATED_AT,
        :UPDATED_AT AS UPDATED_AT
    FROM DUAL
) S
ON (
    T.MEMBER_ID = S.MEMBER_ID
)
WHEN MATCHED THEN
    UPDATE SET
        T.MEMBER_NAME = S.MEMBER_NAME,
        T.MOBILE_NO = S.MOBILE_NO,
        T.USE_YN = S.USE_YN,
        T.CREATED_AT = S.CREATED_AT,
        T.UPDATED_AT = S.UPDATED_AT
WHEN NOT MATCHED THEN
    INSERT (MEMBER_ID, MEMBER_NAME, MOBILE_NO, USE_YN, CREATED_AT, UPDATED_AT)
    VALUES (S.MEMBER_ID, S.MEMBER_NAME, S.MOBILE_NO, S.USE_YN, S.CREATED_AT, S.UPDATED_AT)
LOG ERRORS INTO NEXT_APP.ERR$_TB_MEMBER ('RUN_ID') REJECT LIMIT UNLIMITED
```

방식별 쓰기 문: `INSERT ONLY` = `INSERT INTO … VALUES (:…)` · `TRUNCATE + INSERT` = 시작 때 한 번 `TRUNCATE TABLE` 후 INSERT · `DELETE + INSERT` = 배치마다 `DELETE … WHERE 키 = :키` 후 INSERT. 매핑 값이 없는 대상 컬럼은 INSERT 목록에서 빼서 DB 기본값이 들어가게 한다.

### 4.2 NULL 처리 규칙 → SQL

| NULL 처리 | 원본 SELECT에 들어가는 식 | 비고 |
|---|---|---|
| NULL 허용 | `식` | 대상이 NOT NULL이면 NULL 행은 ORA-01400 → 오류 정책 |
| 행 거부 | `식` | NULL 행은 오류 테이블로(LOG ERRORS). 예상 거부 수를 검사에 표시 |
| 기본값 | `NVL(식, '값')` | 숫자 형식이면 따옴표 없이. 원본이 없으면 `'값'`만 |
| SYSDATE | `NVL(식, SYSDATE)` | 대상이 TIMESTAMP면 `SYSTIMESTAMP` |
| 빈 문자열 | `NVL(식, '')` | **Oracle에서 `''`는 NULL** — NOT NULL 컬럼이면 경고 |
| 사용자 식 | `NVL(식, 사용자 식)` | 예: `NVL(MOD_DT, REG_DT)` |

---

## 5. 기타 특이사항

- **유효성 검사**(검사 열·검사기):
  - 원본·식이 없고 대상이 NOT NULL: 기본값·SYSDATE·사용자 식이 있으면 PASS, DB 기본값이 있고 MERGE가 아니면 INFO, 그 밖은 ERROR("모든 행이 ORA-01400으로 거부").
  - 식 구문 오류·없는 컬럼: ERROR(ORA-00936·ORA-00904 등 Oracle 메시지 그대로).
  - 형식 호환성(README 7.2 배지): 문자 길이 줄어듦 → WARN 잘림 위험(실측 최대 길이 함께), 숫자 정수부·소수부 줄어듦 → WARN(ORA-01438·반올림), TIMESTAMP→DATE → WARN 소수 초 손실, 문자↔숫자·날짜 → WARN(변환 실패 가능, TO_DATE·TO_CHAR 권장), 날짜↔숫자 → ERROR(ORA-00932).
  - NOT NULL + NULL 가능 원본: 행 거부면 "NULL n행은 거부되어 오류 테이블로"(WARN), NULL 허용이면 "ORA-01400으로 실패 — NULL 처리를 정하세요"(WARN). `TRIM`·`REGEXP_REPLACE`는 공백만 있는 값도 NULL이 되므로 공백 행 수를 더한다.
- **권한 처리**: FUNC03·04·05는 원본 SELECT 권한. 원본 세션은 읽기 전용.
- **팝업**: 자동 매핑 방식 선택(빈 컬럼만/모두 다시), 매핑 지우기 확인.
- **기타**:
  - 샘플 미리보기는 체크포인트 위치(`:LAST_ID`) 다음 6행을 읽는다(재개 범위 데이터를 미리 봄). 원본 값과 결과가 다르면 결과 칸 왼쪽에 강조색 막대, NULL은 흐린 기울임, 결과에 붙은 처리(기본값·SYSDATE·거부)는 회색 꼬리표.
  - 변환식 입력은 0.3초 멈춘 뒤 FUNC03·04를 부른다(입력 중 연속 호출 방지, 이전 요청은 취소).
  - 체크포인트 컬럼은 단조 증가하고 중복이 없어야 재개가 정확하다(PK 권장). 중복 가능한 컬럼(날짜 등)을 고르면 검증에서 경고.
