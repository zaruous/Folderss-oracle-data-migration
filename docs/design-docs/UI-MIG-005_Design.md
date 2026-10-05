# UI-MIG-005. 검증 (실행 전 · 실행 후)

## 1. 화면 내용 요약

- **목적**: 실제 이관 전에 접속·객체·매핑·형식·제약·공간·실행 계획을 검사해 실패를 미리 막고(PASS · WARN · ERROR · INFO), 이관 뒤에는 원본과 대상을 비교해 결과를 확인한다.
- **주요 기능**:
  - 실행 전 검증: 검사 항목을 하나씩 끝나는 대로 표에 채움(진행 막대·회전 표시), 묶음별(접속 · 객체 · 매핑 · 형식 · 제약 · 공간·실행) 표시
  - 집계(PASS n · WARN n · ERROR n · INFO n), 마지막 검증 시각·소요 시간, "검증 뒤 작업이 바뀜" 표시, 전체/문제만 거르기
  - ERROR가 있으면 이관 실행 차단 안내(Dry Run은 가능), 각 줄의 [고치기 ›]로 해당 화면·컬럼에 바로 이동
  - 실행 후 검증: 작업별 행 수(MATCH) · PK 누락 · 중복 키 · 샘플 데이터 · 해시 · NULL 수

---

## 2. 화면 이미지 (와이어프레임)

```
+--------------------------------------------------------------------------------------------------+
| STEP 4  검증                                        [☰ 실행 전 검증 (F6)] [⟲ 실행 후 검증]       |
| 실제 이관 전에 접속·객체·매핑·형식·제약·공간을 검사하고(PASS · WARN · ERROR), 이관 뒤에는 …       |
+--------------------------------------------------------------------------------------------------+
| [필터 영역]  [실행 전 검증 23] [실행 후 검증]                                                      |
|  [PASS 14] [WARN 4] [ERROR 1] [INFO 4]          마지막 검증 14:47:13 · 3.1초   [전체|문제만]      |
+--------------------------------------------------------------------------------------------------+
| [본문 영역]                                                                                       |
| (!) ERROR 1건 — 이관을 실행할 수 없습니다. 고친 뒤 다시 검증하세요. Dry Run은 할 수 있습니다.        |
| +--------+------------------+--------------------------+-----------------------------------+-------+ |
| | 결과   | 검사 항목         | 대상                     | 내용                              | 조치  | |
| +--------+------------------+--------------------------+-----------------------------------+-------+ |
| | 접속 2                                                                                         | |
| | PASS   | 원본 접속        | LEGACY_PROD              | Oracle 19c · 31 ms · 10.10.10.21:1521/LEGACY · 읽기 전용 |  | |
| | PASS   | 대상 접속        | NEXT_PROD                | Oracle 19c · 18 ms · 10.20.10.35:1521/NEXTDB |       | |
| | 객체 2                                                                                         | |
| | PASS   | 원본 테이블 존재  | LEGACY_APP               | SRC_CUSTOMER, SRC_ORDER, SRC_CUSTOMER_GRADE (3개) |  | |
| | 매핑 3                                                                                         | |
| | INFO   | 컬럼 매핑        | SRC_ORDER → TB_SALES_ORDER | 6 / 7 컬럼 · 비워 둠: CHANNEL_CD(NOT NULL — 제약 참고) | 컬럼 › | |
| | 형식 3                                                                                         | |
| | WARN   | VARCHAR 길이     | TB_MEMBER                | PHONE_NO VARCHAR2(30) → MOBILE_NO VARCHAR2(20)    | 컬럼 › | |
| |        |                  |                          | 잘림 위험(Truncation Risk) · 실측 최대 13자       |       | |
| | WARN   | NUMBER 정밀도    | TB_SALES_ORDER           | ORDER_AMT NUMBER(15,2) → TOTAL_AMT NUMBER(13,2)   | 컬럼 › | |
| | 제약 9                                                                                         | |
| | ERROR  | NOT NULL         | TB_SALES_ORDER.CHANNEL_CD | NOT NULL 컬럼에 값이 없음 — 모든 행이 ORA-01400으로 거부됨 | 컬럼 › | |
| | WARN   | NOT NULL         | TB_MEMBER.MEMBER_NAME    | NULL 37행은 거부되어 오류 테이블로 감             | 컬럼 › | |
| | PASS   | 참조 무결성(FK)  | TB_SALES_ORDER.MEMBER_ID | FK_SALES_ORDER_MEMBER → TB_MEMBER · 부모 작업이 먼저 실행됨 |  | |
| | 공간·실행 4                                                                                    | |
| | PASS   | 대상 테이블스페이스 | NEXT_DATA             | 필요 약 1.12 GB / 여유 182.4 GB                   |       | |
| | WARN   | 운영 DB 쓰기     | NEXT_PROD                | TB_MEMBER_GRADE: TRUNCATE + INSERT — 되돌릴 수 없음 | 테이블 매핑 › | |
| | INFO   | 체크포인트       | SRC_CUSTOMER → TB_MEMBER | 지난 실행이 CUSTOMER_ID = 850,000에서 멈춤 — 재개 가능 | 실행 › | |
| +--------+------------------+--------------------------+-----------------------------------+-------+ |
+--------------------------------------------------------------------------------------------------+
| [‹ 컬럼 매핑]           검사에 쓰는 SQL은 기능 설계서 UI-MIG-005에 정리                [다음: 실행] |
+--------------------------------------------------------------------------------------------------+
```

**실행 후 검증 탭**

```
+--------------------------------------------------------------------------------------------------+
|  [PASS 20] [WARN 0] [ERROR 0]                         실행 R-20261003-144805 · 14:49:00 [⟲ 다시 검증] |
| +--------+------------------+-------------+-----------------------+------------------------------+ |
| | 결과   | 검사             |        원본 |                  대상 | 내용                         | |
| | SRC_CUSTOMER → TB_MEMBER                                                                     | |
| | MATCH  | 행 수            |   1,240,325 |  1,240,288 + 거부 37  | MATCH (거부 37행은 ERR$_TB_MEMBER) | |
| | PASS   | PK 누락          |           0 |                     0 | 키 해시 버킷 1,024개 비교     | |
| | PASS   | 중복 키          |           0 |                     0 | 대상 키 중복 없음             | |
| | PASS   | 샘플 데이터      |       100행 |                 100행 | 원본에 변환식 적용 값 = 대상 값 | |
| | PASS   | 해시             |    01303D75 |              01303D75 | SUM(ORA_HASH(매핑 열 연결)) 일치 | |
| | PASS   | NULL 수 · MOBILE_NO |      1,204 |                 1,204 | 변환 후 NULL 수 일치          | |
| +--------+------------------+-------------+-----------------------+------------------------------+ |
+--------------------------------------------------------------------------------------------------+
```

---

## 3. 버튼 설명

| 버튼명 | 기능 설명 | 주요 로직 |
|--------|----------|-----------|
| 실행 전 검증 (F6) | 모든 "사용" 매핑을 검사 | FUNC01. 시험이 안 된 접속은 먼저 접속 테스트. 줄을 하나씩 추가, 끝나면 집계 알림 `검증 완료 · PASS n · WARN n · ERROR n` |
| 실행 후 검증 | 마지막 실행 결과를 원본과 비교 | FUNC02. 실행이 끝난(완료·중지·실패) 뒤에만 켜짐. Dry Run이면 행 수 비교를 SKIP |
| 실행 전 / 실행 후 탭 | 결과 전환 | 탭에 항목 수 |
| 전체 / 문제만 | WARN·ERROR만 보기 | |
| 조치 링크(컬럼 › · 테이블 매핑 › · SQL 편집 › · 접속 › · 실행 ›) | 원인 화면으로 이동 | 컬럼 문제는 해당 매핑·컬럼을 골라 둔 채로 UI-MIG-003, SQL 원본 문제는 SQL 원본 편집기(UI-MIG-004) |
| 실행 화면으로 › | 통과·경고만 있을 때 | UI-MIG-006 |
| 다시 검증(실행 후) | 실행 후 검증 다시 | FUNC02 |

---

## 4. 서비스 처리

| 기능ID | 기능명 | 메소드 | 서비스주소 | 입력파라미터 | 기능설명 | 참조 테이블 | SQL |
|--------|--------|--------|-----------|-------------|----------|-------------|-----|
| FUNC01 | 실행 전 검증 | POST | /v1/migration/validation/pre | jobId | 아래 검사 C01~C13을 차례로 실행하고 하나씩 결과를 흘려보냄(`IAsyncEnumerable<CheckItem>`). 구현: `ValidationEngine.RunPreAsync` | 4.1 표 | 4.1 표 |
| FUNC02 | 실행 후 검증 | POST | /v1/migration/validation/post | jobId, runId | 작업별 P01~P06 비교. 원본·대상이 다른 DB라 DB 링크 없이 양쪽에서 집계해 비교 | 4.2 표 | 4.2 표 |
| FUNC03 | 오류 테이블 요약 | GET | /v1/migration/validation/rejects | runId, target | 거부 행을 오류 번호별로 집계(실행 후 검증·실행 로그의 "거부 n행" 근거) | ERR$_<대상> | `SELECT ORA_ERR_NUMBER$, MIN(ORA_ERR_MESG$) AS MESSAGE, COUNT(*) AS ROWS_CNT FROM NEXT_APP.ERR$_TB_MEMBER WHERE ORA_ERR_TAG$ = :RUN_ID GROUP BY ORA_ERR_NUMBER$ ORDER BY ROWS_CNT DESC` |

### 4.1 실행 전 검사 항목

| ID | 묶음 · 검사 | 결과 규칙 | 참조 | SQL |
|---|---|---|---|---|
| C01 | 접속 · 원본/대상 접속 | 실패 ERROR(ORA 메시지) | V$VERSION | UI-MIG-001 SQL-0 |
| C02 | 객체 · 원본/대상 테이블 존재 | 없으면 ERROR(ORA-00942) | ALL_TABLES, ALL_VIEWS | `SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = :OWNER AND TABLE_NAME IN ('SRC_CUSTOMER', 'SRC_ORDER') UNION ALL SELECT VIEW_NAME FROM ALL_VIEWS WHERE OWNER = :OWNER AND VIEW_NAME IN ('SRC_CUSTOMER', 'SRC_ORDER')` |
| C03 | 매핑 · 컬럼 매핑 | 매핑 수/전체, 비운 컬럼 INFO, 키 필요한데 없음 ERROR | (캐시) | 해당 없음 |
| C04 | 매핑 · 원본 SQL | SQL 원본 매핑마다 구문·객체·별칭·바인드·체크포인트(UI-MIG-004 검증 중 SQL 쪽 항목)의 최악 수준. 컬럼 쪽 검사는 C03·C05~C11에서 테이블 원본과 같이 | 원본 | UI-MIG-004 SQL-1·2 |
| C05 | 형식 · 데이터 형식 호환성 | UI-MIG-003 5장 규칙 | ALL_TAB_COLUMNS | UI-MIG-001 SQL-2 |
| C06 | 형식 · VARCHAR 길이 | 결과 최대 길이 > 대상 길이면 WARN(Truncation Risk). 대상 `CHAR_USED = 'B'`면 바이트로 비교 | 원본 | UI-MIG-003 SQL-2 (`MAX(LENGTHB(…))`) |
| C07 | 형식 · NUMBER 정밀도 | 정수부·소수부가 줄면 WARN, 실측 최대값 함께 | 원본 | `SELECT MAX(ABS(ORDER_AMT)) AS MAX_ABS, MAX(LENGTH(TO_CHAR(TRUNC(ABS(ORDER_AMT))))) AS INT_DIGITS FROM LEGACY_APP.SRC_ORDER` |
| C08 | 제약 · NOT NULL | 값 없음 ERROR, NULL 예상 행 WARN | 원본 | `SELECT COUNT(*) FROM LEGACY_APP.SRC_CUSTOMER WHERE TRIM(CUSTOMER_NM) IS NULL` |
| C09 | 제약 · PK / Unique Key | 대상 PK가 매핑 안 됨 ERROR, 병합 키가 PK·UK 아님 WARN(ORA-30926 위험) | ALL_CONSTRAINTS | UI-MIG-001 SQL-3 |
| C10 | 제약 · 중복 키 | 원본 병합 키 중복 ERROR, INSERT ONLY인데 대상에 행 있음 WARN(ORA-00001) | 원본, 대상 | SQL-1 |
| C11 | 제약 · 참조 무결성(FK) | 부모 작업이 먼저 실행되면 PASS, 원본에 부모 없는 행(고아) WARN | ALL_CONSTRAINTS, 원본 | SQL-2 |
| C12 | 공간·실행 · 대상 테이블스페이스 | 필요(행 × 평균 행 길이 × 1.35) > 여유면 ERROR, 70% 넘으면 WARN | USER_USERS, USER_FREE_SPACE, USER_TS_QUOTAS | SQL-3 |
| C13 | 공간·실행 · 같은 대상 작업 · 운영 DB 쓰기 · 오류 테이블 · 체크포인트 | 같은 대상 둘 이상 WARN, 빨강 대상 + 파괴적 방식 WARN, 오류 테이블 없음 INFO(실행 전 생성), 미완료 체크포인트 INFO | ALL_TABLES, MIG_CHECKPOINT | SQL-4 |

**SQL-1 중복 키**

```sql
-- 원본 병합 키 중복(있으면 MERGE가 ORA-30926)
SELECT CUSTOMER_ID, COUNT(*) AS CNT
FROM LEGACY_APP.SRC_CUSTOMER
GROUP BY CUSTOMER_ID
HAVING COUNT(*) > 1
FETCH FIRST 10 ROWS ONLY;

-- INSERT ONLY 대상의 기존 행
SELECT COUNT(*) FROM NEXT_APP.TB_SALES_ORDER;
```

**SQL-2 고아 행(원본 기준, 같은 원본 DB 안에서)**

```sql
SELECT COUNT(*) AS ORPHAN_ROWS
FROM LEGACY_APP.SRC_ORDER O
WHERE NOT EXISTS (
    SELECT 1 FROM LEGACY_APP.SRC_CUSTOMER C WHERE C.CUSTOMER_ID = O.CUSTOMER_ID
)
```

**SQL-3 테이블스페이스 여유**

```sql
SELECT U.DEFAULT_TABLESPACE,
       (SELECT ROUND(SUM(F.BYTES) / 1024 / 1024 / 1024, 1)
          FROM USER_FREE_SPACE F
         WHERE F.TABLESPACE_NAME = U.DEFAULT_TABLESPACE)              AS FREE_GB,
       (SELECT DECODE(Q.MAX_BYTES, -1, NULL, ROUND((Q.MAX_BYTES - Q.BYTES) / 1024 / 1024 / 1024, 1))
          FROM USER_TS_QUOTAS Q
         WHERE Q.TABLESPACE_NAME = U.DEFAULT_TABLESPACE)              AS QUOTA_LEFT_GB
FROM USER_USERS U
```

**SQL-4 오류 테이블·체크포인트**

```sql
-- 오류 테이블이 있나
SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = :OWNER AND TABLE_NAME = 'ERR$_TB_MEMBER';

-- 없으면 실행 직전에 만든다
BEGIN
    DBMS_ERRLOG.CREATE_ERROR_LOG(dml_table_name => 'TB_MEMBER', err_log_table_name => 'ERR$_TB_MEMBER');
END;

-- 끝나지 않은 체크포인트
SELECT TASK_KEY, CP_COLUMN, CP_VALUE, ROWS_DONE, ROWS_TOTAL, RUN_ID, UPDATED_AT
FROM MIG_CHECKPOINT
WHERE JOB_NAME = :JOB_NAME AND STATUS <> 'done'
```

### 4.2 실행 후 비교 항목

원본·대상은 서로 다른 DB(DB 링크 없음)이므로 같은 집계를 양쪽에서 따로 실행해 비교한다. 원본 쪽은 **변환식을 적용한 값**(원본 SELECT)을 집계한다.

| ID | 검사 | 결과 규칙 | SQL |
|---|---|---|---|
| P01 | 행 수 | 원본 범위 행 수 = 대상 반영 행 + 거부 행이면 `MATCH`, 다르면 ERROR, 중지된 작업은 WARN | SQL-5 |
| P02 | PK 누락 | 키 해시 버킷(1,024개)별 개수·합이 다르면 그 버킷만 키를 내려받아 비교 | SQL-6 |
| P03 | 중복 키 | 대상 키 중복 0건 | `SELECT COUNT(*) FROM (SELECT MEMBER_ID FROM NEXT_APP.TB_MEMBER GROUP BY MEMBER_ID HAVING COUNT(*) > 1)` |
| P04 | 샘플 데이터 | 원본 무작위 100행의 변환 값 = 대상 값 | SQL-7 |
| P05 | 해시 | 매핑 열을 정해진 형식으로 이은 문자열의 `ORA_HASH` 합이 같음(거부 행 제외) | SQL-8 |
| P06 | NULL 수 | NULL 허용 대상 컬럼마다 변환 후 NULL 수 = 대상 NULL 수 | `SELECT COUNT(*) - COUNT(MOBILE_NO) FROM NEXT_APP.TB_MEMBER` |

**SQL-5 행 수**

```sql
-- 원본(범위: 원본 조건만, 체크포인트 조건 없이)
SELECT COUNT(*) FROM LEGACY_APP.SRC_CUSTOMER;
-- 대상
SELECT COUNT(*) FROM NEXT_APP.TB_MEMBER;
-- 거부 행(이번 실행)
SELECT COUNT(*) FROM NEXT_APP.ERR$_TB_MEMBER WHERE ORA_ERR_TAG$ = :RUN_ID;
```

**SQL-6 키 해시 버킷(양쪽에서 같은 식)**

```sql
SELECT MOD(ORA_HASH(CUSTOMER_ID), 1024) AS BUCKET, COUNT(*) AS CNT, SUM(ORA_HASH(CUSTOMER_ID)) AS HSUM
FROM LEGACY_APP.SRC_CUSTOMER
GROUP BY MOD(ORA_HASH(CUSTOMER_ID), 1024);

SELECT MOD(ORA_HASH(MEMBER_ID), 1024) AS BUCKET, COUNT(*) AS CNT, SUM(ORA_HASH(MEMBER_ID)) AS HSUM
FROM NEXT_APP.TB_MEMBER
GROUP BY MOD(ORA_HASH(MEMBER_ID), 1024);
```

**SQL-7 샘플 100행**

```sql
-- 원본: 변환 SELECT에서 무작위 100행
SELECT * FROM (
    SELECT CUSTOMER_ID AS MEMBER_ID, TRIM(CUSTOMER_NM) AS MEMBER_NAME, REGEXP_REPLACE(PHONE_NO, '[^0-9]', '') AS MOBILE_NO
    FROM LEGACY_APP.SRC_CUSTOMER SAMPLE (0.05)
    ORDER BY DBMS_RANDOM.VALUE
) WHERE ROWNUM <= 100;

-- 대상: 같은 키
SELECT MEMBER_ID, MEMBER_NAME, MOBILE_NO
FROM NEXT_APP.TB_MEMBER
WHERE MEMBER_ID IN (:K1, :K2, /* … */ :K100)
```

**SQL-8 해시(형식을 고정해 이어 붙임)**

```sql
-- 1) 거부 행 키(대상 오류 테이블 — 보통 몇 건). 오류 테이블의 열은 VARCHAR2(4000)
SELECT MEMBER_ID FROM NEXT_APP.ERR$_TB_MEMBER WHERE ORA_ERR_TAG$ = :RUN_ID;

-- 2) 원본: 변환 SELECT를 감싸고 거부 키는 뺀다(1의 키를 바인드 목록으로)
SELECT SUM(ORA_HASH(MEMBER_ID || '|' || MEMBER_NAME || '|' || MOBILE_NO || '|' || TO_CHAR(CREATED_AT, 'YYYYMMDDHH24MISSFF6')))
FROM ( /* UI-MIG-003 SQL-3 원본 SELECT (체크포인트·작업자 범위 조건 없이) */ )
WHERE TO_CHAR(MEMBER_ID) NOT IN (:R1, :R2 /* … */);

-- 3) 대상
SELECT SUM(ORA_HASH(MEMBER_ID || '|' || MEMBER_NAME || '|' || MOBILE_NO || '|' || TO_CHAR(CREATED_AT, 'YYYYMMDDHH24MISSFF6')))
FROM NEXT_APP.TB_MEMBER;
```

대상에 원본 밖의 행(이관 전부터 있던 다른 데이터)이 있으면 3)에 원본 범위와 같은 조건을 붙인다.

---

## 5. 기타 특이사항

- **유효성 검사**:
  - 실행 차단은 **고른 작업**의 ERROR만 따진다(실행 화면에서 그 작업을 빼면 실행 가능). 매핑과 무관한 ERROR(접속·메타데이터)는 항상 차단.
  - 작업을 바꾸면 결과를 지우지 않고 "검증 뒤 작업이 바뀜"으로 표시한다(단계 막대도 경고색). 실행할 때 다시 묻는다.
  - 원본·대상 문자 집합이 다르면 C06은 대상 문자 집합 기준 바이트 길이로 계산한다(예: 한글 VARCHAR2(100 BYTE)).
- **권한 처리**: 원본 SELECT, 대상 SELECT(실행 후 비교), `USER_FREE_SPACE`·`USER_TS_QUOTAS`(본인 것), 오류 테이블 생성 시 CREATE TABLE.
- **팝업**: 없음(결과는 표에, 실행 차단 안내는 실행 시도 때 UI-MIG-006 팝업).
- **기타**:
  - 큰 테이블의 집계(C06·C08·C10, P02·P05)는 오래 걸릴 수 있다. 실행 전 검사는 표본(`SAMPLE BLOCK (5)`)으로 하고 결과에 "표본 5%"를 적는다. 실행 후 검사는 전체를 보되 취소할 수 있게 한다.
  - `ORA_HASH`는 같은 문자열이면 DB가 달라도 같은 값이다. 숫자·날짜는 반드시 `TO_CHAR`로 형식을 고정해 이어 붙인다(NLS 차이 방지).
  - 확장(구현 시): 대상 트리거·인덱스 수 안내(INFO — 대량 쓰기 성능), 통계가 오래된 원본(행 수 추정 부정확) 안내.
