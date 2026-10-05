# UI-MIG-007. 작업 정의 · 매핑 템플릿 (팝업)

## 1. 화면 내용 요약

- **목적**: 이관 설정을 하나의 작업(Job) 파일로 저장·열기·보기 하고, 매핑만 따로 템플릿(`customer_mapping.json`, `order_mapping.json` …)으로 내보내·가져와 다른 작업에서 다시 쓴다.
- **주요 기능**:
  - 작업 정의 보기 팝업: JSON / YAML 탭, 복사, JSON 저장, YAML 저장
  - 파일 메뉴: 새 작업 · 작업 열기 · 예제 작업 불러오기 · 작업 저장(`Ctrl+S`) · YAML로 저장(`Ctrl+Shift+S`)
  - 매핑 템플릿 내보내기 팝업(이름 입력) · 가져오기(파일 고르기 → 합치기)
  - 임시 저장: 바뀐 작업을 자동 저장하고 다음에 창을 열면 되살림(제목 뒤 `*`)

---

## 2. 화면 이미지 (와이어프레임)

**팝업 ① 작업 정의 — CUSTOMER_MIGRATION** (`파일 > 작업 정의 보기…`, 아이콘 `{}`, 단계 막대 아래 [작업 정의])

```
+--------------------------------------------------------------------------------+
| 작업 정의 — CUSTOMER_MIGRATION                                               [×]|
+--------------------------------------------------------------------------------+
| 접속·전략·매핑·체크포인트를 한 파일로 저장합니다. 비밀번호는 넣지 않습니다.        |
| +----------------------------------------------------------------------------+ |
| | [JSON] [YAML]                                                              | |
| |----------------------------------------------------------------------------| |
| | {                                                                          | |
| |   "format": "folderss-migration-job",                                      | |
| |   "version": 2,                                                            | |
| |   "jobName": "CUSTOMER_MIGRATION",                                         | |
| |   "source": { "profileId": "cn-legacy-prod", "schema": "LEGACY_APP", "name": "LEGACY_PROD", … }, |
| |   "strategy": { "mode": "FULL", "commitSize": 10000, … },                  | |
| |   "mappings": [ { "sourceType": "TABLE" … }, { "sourceType": "SQL" … } ],   | |
| |   "checkpoints": { … }                                                     | |
| | }                                                                          | |
| +----------------------------------------------------------------------------+ |
+--------------------------------------------------------------------------------+
|                                        [⧉ 복사] [YAML 저장] [JSON 저장]        |
+--------------------------------------------------------------------------------+
```

**팝업 ② 매핑 템플릿 내보내기** (`파일 > 매핑 템플릿 내보내기…`, 테이블 매핑 [JSON 내보내기])

```
+------------------------------------------------------+
| 매핑 템플릿 내보내기                               [×]|
+------------------------------------------------------+
|  템플릿 이름                                          |
|  [customer_mapping                                ]  |
|  파일 이름: <이름>.json — 매핑(테이블·SQL 원본)·컬럼 |
|  용어 사전을 담습니다(접속 정보 제외).                |
|  매핑 4개 (테이블 원본 3 · SQL 원본 1)               |
+------------------------------------------------------+
|                                  [취소] [↦ 내보내기] |
+------------------------------------------------------+
```

**확인 창 ③ 새 작업 / 예제 작업** (저장하지 않은 변경이 있을 때)

```
+------------------------------------------------------+
| 새 작업                                            [×]|
+------------------------------------------------------+
| 저장하지 않은 변경이 있습니다. 버리고 새 작업을       |
| 만들까요? (접속·전략은 그대로 둡니다)                 |
+------------------------------------------------------+
|                                  [취소] [새 작업]    |
+------------------------------------------------------+
```

---

## 3. 버튼 설명

| 버튼명 | 기능 설명 | 주요 로직 |
|--------|----------|-----------|
| 새 작업 | 빈 작업 시작 | 저장 안 한 변경이 있으면 확인 창 ③. 접속·전략은 유지, 매핑·체크포인트는 비움. 테이블 매핑 화면으로 |
| 작업 열기 (`Ctrl+O`) | `*.job.json` 열기 | FUNC02. 형식·버전 확인, 빠진 값은 기본값. 비밀번호는 다시 입력 안내. 실행 중이면 막음 |
| 예제 작업 불러오기 | POC 예제(CUSTOMER_MIGRATION) | 확인 창 후 교체 |
| 작업 저장 (`Ctrl+S`) | `<jobName>.job.json` 저장 | FUNC01. 비밀번호 제외. 저장하면 `*` 사라짐 |
| YAML로 저장 (`Ctrl+Shift+S`) | `<jobName>.job.yaml` 저장 | FUNC03 |
| 작업 정의 보기 | 팝업 ① | JSON·YAML 탭 전환, [복사]는 팝업을 닫지 않음 |
| 매핑 템플릿 내보내기 | 팝업 ② | FUNC04. 이름 기본값 `<작업 이름에서 _MIGRATION 뺀 것>_mapping` |
| 매핑 템플릿 가져오기 | 템플릿 파일을 지금 작업에 합침 | FUNC05. 결과 알림 `customer_mapping.json: 매핑 추가 n · 바꿈 m` → 테이블 매핑 화면으로 |

---

## 4. 서비스 처리

| 기능ID | 기능명 | 메소드 | 서비스주소 | 입력파라미터 | 기능설명 | 참조 테이블 | SQL |
|--------|--------|--------|-----------|-------------|----------|-------------|-----|
| FUNC01 | 작업 저장(JSON) | POST | /v1/migration/job/save | job, path | 비밀번호를 뺀 사본을 들여쓰기 2칸 UTF-8(BOM 없음)로 저장. 구현: `JobFile.Save` | (파일) `*.job.json` | 해당 없음(파일) |
| FUNC02 | 작업 열기 | POST | /v1/migration/job/open | path | `format = folderss-migration-job` 확인, `version`이 더 새것이면 거부, 빠진 값은 기본값, 매핑 id 없으면 새로 | (파일) | 해당 없음(파일) |
| FUNC03 | YAML 내보내기 | POST | /v1/migration/job/export-yaml | job, path | 여러 줄 SQL은 `\|` 블록 | (파일) `*.job.yaml` | 해당 없음(파일) |
| FUNC04 | 매핑 템플릿 내보내기 | POST | /v1/migration/job/template-export | job, name, path | 매핑(원본 종류·원본·SQL·바인드·대상·방식·키·체크포인트 컬럼·조건·컬럼)·용어 사전 + 원본/대상 스키마 이름. 접속·체크포인트·비밀번호 제외 | (파일) `<name>.json` | 해당 없음(파일) |
| FUNC05 | 매핑 템플릿 가져오기 | POST | /v1/migration/job/template-import | job, path | `format = folderss-migration-mapping` 확인. 같은 원본 종류·원본·대상 매핑은 바꾸고(id 유지) 나머지는 추가. v1 템플릿(tableMappings·sqlMappings)은 v2로 바꿔 읽음. 메타데이터에 없는 테이블·컬럼은 검증에서 걸러짐 | (파일) | 해당 없음(파일) |
| FUNC06 | 임시 저장 | PUT | /v1/migration/job/draft | job | 입력이 멈추고 1.5초 뒤, 창을 닫을 때 저장. 다음에 창을 열면 되살림(DB Helper SQL 임시 저장과 같은 방식) | (파일) `DataDirectory\jobs\<jobName>.draft.json` | 해당 없음(파일) |
| FUNC07 | 실행 기록 목록 | GET | /v1/migration/job/runs | jobName | 작업 정의 팝업 아래 "최근 실행" (구현 시) | MIG_RUN, MIG_RUN_TASK | `SELECT R.RUN_ID, R.RUN_MODE, R.STATUS, R.STARTED_AT, R.ENDED_AT, SUM(T.INSERTED) AS INSERTED, SUM(T.UPDATED) AS UPDATED, SUM(T.REJECTED) AS REJECTED FROM MIG_RUN R LEFT JOIN MIG_RUN_TASK T ON T.RUN_ID = R.RUN_ID WHERE R.JOB_NAME = :JOB_NAME GROUP BY R.RUN_ID, R.RUN_MODE, R.STATUS, R.STARTED_AT, R.ENDED_AT ORDER BY R.STARTED_AT DESC FETCH FIRST 20 ROWS ONLY` |

### 4.1 매핑 템플릿 형식

```json
{
  "format": "folderss-migration-mapping",
  "version": 2,
  "name": "customer_mapping",
  "sourceSchema": "LEGACY_APP",
  "targetSchema": "NEXT_APP",
  "dictionary": {
    "tables": { "CUSTOMER": "MEMBER", "ORDER": "SALES_ORDER", "CODE_MST": "COMMON_CODE" },
    "columns": { "CUSTOMER_ID": "MEMBER_ID", "CUSTOMER_NM": "MEMBER_NAME", "PHONE_NO": "MOBILE_NO" }
  },
  "mappings": [
    {
      "sourceType": "TABLE", "source": "SRC_CUSTOMER", "target": "TB_MEMBER", "mode": "MERGE", "mergeKey": ["MEMBER_ID"],
      "checkpointColumn": "CUSTOMER_ID", "where": "",
      "columns": [
        { "target": "MEMBER_NAME", "source": "CUSTOMER_NM", "expr": "TRIM(CUSTOMER_NM)", "nullRule": "REJECT", "defaultValue": "" }
      ]
    },
    {
      "sourceType": "SQL", "source": "SQLMAP_MEMBER", "sql": "SELECT C.CUSTOMER_ID AS MEMBER_ID, … FROM SRC_CUSTOMER C LEFT JOIN SRC_CUSTOMER_GRADE G …",
      "binds": [{ "name": "LAST_ID", "type": "NUMBER", "value": "850000", "fromCheckpoint": true }],
      "target": "TB_MEMBER", "mode": "MERGE", "mergeKey": ["MEMBER_ID"], "checkpointColumn": "MEMBER_ID",
      "columns": [
        { "target": "MEMBER_ID", "source": "MEMBER_ID", "expr": "", "nullRule": "REJECT", "defaultValue": "" }
      ]
    }
  ]
}
```

작업 파일 형식은 [README 5장](README.md#5-데이터-모델--작업job-파일-v2).

---

## 5. 기타 특이사항

- **유효성 검사**: 형식(`format`)이 다르면 "작업 파일이 아닙니다(format: …)", 버전이 더 높으면 "더 새 버전(n)의 작업 파일입니다". JSON 구문 오류는 위치와 함께 알림. 열기·가져오기 실패는 지금 작업을 바꾸지 않는다.
- **권한 처리**: 파일은 사용자 권한으로 읽고 쓴다. 저장 위치 기본값은 마지막으로 쓴 폴더(없으면 문서 폴더). 플러그인 압축 폴더(`PluginDirectory`)에는 쓰지 않는다.
- **팝업**: ① 작업 정의(너비 820), ② 매핑 템플릿 내보내기, ③ 새 작업·예제 작업 확인. 모두 위에 제목, 아래 오른쪽 버튼, Esc 닫기.
- **기타**:
  - 비밀번호는 작업 파일·템플릿·YAML 어디에도 넣지 않는다. 작업 파일의 접속은 마이그레이션 설정의 접속 참조(`profileId`)와 비밀번호 없는 사본(이름·주소)이다. 다른 PC에서 열면 이름으로 그 PC의 설정 접속을 찾고, 없으면 접속 화면에서 [접속 설정에 추가](UI-MIG-001).
  - 버전 1 작업 파일(`tableMappings`·`sqlMappings`)은 열 때 버전 2(`mappings` + `sourceType`)로 바꾼다(README 5장).
  - 체크포인트는 작업 파일에 마지막 상태 사본만 담는다. 기준은 대상 `MIG_CHECKPOINT`(README 6.5)라, 다른 PC에서 같은 작업을 열어도 재개 위치가 맞는다.
  - 템플릿의 용어 사전은 가져올 때 지금 작업의 사전에 합친다(같은 키는 템플릿 값이 이김).
  - 실행 중에는 새 작업·열기·예제·템플릿 가져오기를 막는다("실행 중에는 작업을 바꿀 수 없습니다").
