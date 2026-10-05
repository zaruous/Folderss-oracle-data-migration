# P7 보고서 — 배포 · 문서 · 마무리 (2026-10-05 갱신)

## 1. 자동 점검 결과 (최종)

| 항목 | 결과 |
|---|---|
| `dotnet build MigrationStudio.sln -c Release --no-incremental` | 경고 0 · 오류 0 |
| 단위 시험 `tests/MigrationStudio.Tests` | **330 통과** (실제 설치 검증에서 찾은 회귀 시험 35건 추가) |
| Oracle 통합 시험 `tests/MigrationStudio.OracleIT` (docker oracle-12c) | **25 통과** (열 형식 전부·오류 테이블 LOB 시험 추가) |
| 레이아웃 검사 `scripts/layout-check.ps1` | **108 / 108 조합** 문제 없음 (`step1 ▽ 펼침`, `step4 검증 통과` 시나리오 추가) |
| `scripts/pack.ps1 -Test` | `release\zaruous.folderss-oracle-migration-1.0.0.zip` 22개 파일, 에이전트 `--version` = 1.0.0 |
| 루트 README · `docs/images/` 4장 | 작성 |

## 2. 실제 Folderss 설치 검증 (2026-10-05 오전, UI 자동화로 수행)

Debug 빌드 Folderss를 별도 프로세스로 띄우고 UIAutomation 스크립트로 `⋯ > 플러그인 > Migration Studio`를 열어 다음을 확인했다
(docker `oracle-12c`, `MIG_IT_SRC` → `MIG_IT_TGT`). 그림: `P7-shots/01~19*.png`.

| 확인 | 결과 |
|---|---|
| zip 등록 → 플러그인 로드(계약 1.0.0) → 창 열림(블랙 테마, 상태 표시줄 `Oracle 어댑터 · v1.0.0`) | OK |
| 접속 선택(DPAPI 저장 비밀번호) → 메타데이터(원본 5테이블 946ms · 대상 1.1s) | OK |
| 이름으로 자동 매칭 → 4개(나중 5개) 매핑, 컬럼 자동 매핑 | OK |
| 실행 전 검증 — 처음엔 오탐 3건(아래 3장), 고친 뒤 **모두 통과**(1.3초) | OK |
| Dry Run 200,004행 | 5초 완료, 예상 Inserted 일치 |
| 이관 실행 200,004행(MERGE, 작업자 4) | **37초 완료**, 거부 0, 원본·대상 ORA_HASH 체크섬 일치(TZ 열 포함) |
| 대상 `MIG_RUN`·`MIG_RUN_TASK`·`MIG_CHECKPOINT` 기록, `ERR$_*` 오류 테이블 생성 | OK |
| 실행 후 검증(행 수 MATCH 200,000 · PK 누락 0 · 중복 키 0) | OK |
| 에이전트 명령줄에 비밀번호 없음(`--run --pipe --data --parent`만) | OK |
| `plugin-data` 28개 파일에 시험 비밀번호 평문 없음(Select-String) | OK |
| 실행 중 플러그인 창 닫기 → 에이전트 계속 → 36초 뒤 `done` | OK |
| DB Helper와 같은 Folderss에서 동시에 열기 | OK(둘 다 로드·창 표시, 충돌 없음) |
| 2,200,004행(BIG2 2,000,000 포함) 실행 | 시작 확인(40,000행/6초), 에이전트 작업 집합 최대 **261MB**(측정 중단 시점) — 완료 확인은 수동 항목 |

### 실측에서 찾아 고친 것 (모두 회귀 시험 추가)

| # | 증상(실제 Folderss) | 원인 | 고침 |
|---|---|---|---|
| 1 | "다음: 테이블 매핑" 버튼이 아무 일도 안 함 | PageFrame의 Prev/Next 이벤트에 아무도 구독 안 함(DevHost `--step`만 써서 못 잡음) | `MigrationView.WireFooterNav` |
| 2 | F5·F6·Ctrl+숫자 단축키 전부 먹통 | `IsDialogOpen()`이 Owner 있는 활성 창을 대화상자로 봄 → 플러그인 창 자체가 소유 창 | 자기 창 제외 |
| 3 | 검증이 **통과하면** 예외("이미 다른 요소의 논리 자식") | 통과 분기에서 요소를 두 패널에 붙임(가짜 서비스는 늘 ERROR라 분기 미실행) | 제거 + DevHost `--validation-pass` 시나리오 |
| 4 | 검증 오탐: `CLOB → CLOB 호환 안 됨` | Compat에 LOB 규칙 없음 | 같은 형식·LOB·RAW·LONG 규칙 |
| 5 | 검증 오탐: 빈 대상이 "이미 1행 있음" | 어댑터 CountAsync가 `SELECT COUNT(*)`를 한 번 더 감싸 결과 행 수(1) 반환 | 첫 셀 직접 읽기 |
| 6 | 검증 오탐: 테이블스페이스 "여유 0 GB" ERROR → 실행 차단 | 소수 첫째 자리 반올림 + 자동 확장 미반영 + 캐시 메타 사용 | 3자리·자동 확장(DBA_DATA_FILES, 권한 없으면 WARN)·검증 때 다시 조회 |
| 7 | 실행 시작 실패 `[Plan] PE image does not have metadata` | `AssemblyName.GetAssemblyName(MigrationAgent.exe)` — apphost는 관리 어셈블리가 아님 | 파일 버전 정보 → `.dll` 순으로 읽기 |
| 8 | Dry Run이 `Specified cast is not valid`로 실패 | ODP.NET `GetDataTypeName`이 "TimeStampTZ"를 줘 TZ 분기를 못 탐 | 공급자 형식(OracleTimeStampTZ 등)으로 분기, 열 이름 포함 오류 |
| 9 | 실패한 Dry Run 뒤 10분간 "동시 실행 한도(1)" | 끝난 에이전트(idle-exit 대기)를 동시 실행으로 셈 | 끝난 상태 제외 + 결과 받은 뒤 shutdown |
| 10 | 이관 실행 즉시 실패 "오류 테이블을 만들 권한이 없습니다" | CLOB 열 때문에 `DBMS_ERRLOG.CREATE_ERROR_LOG` ORA-20069(skip_unsupported 기본 FALSE) + 모든 오류를 권한으로 안내 | `skip_unsupported=>TRUE`, ORA 코드로 권한 여부 구분 |
| 11 | 이관 뒤 "검증 뒤 작업이 바뀜" 재확인 대화상자 | 체크포인트 동기화가 JobVersion을 올림 | `MarkChanged(affectsValidation:false)` |
| 12 | 창을 다시 열어도 진행 중 실행에 안 붙음 | UI에 다시 붙기 호출이 없었음 | `RunPage.CheckAliveRuns`(같은 작업이면 자동, 아니면 안내 상자) |
| 13 | 다시 붙기 `[Connect] 파이프 연결 시간 초과` | 창을 닫을 때 파이프를 안 끊음 + 에이전트가 끝난 뒤 새 접속을 안 받음 | Unloaded에서 `Detach()`, 에이전트 수락 루프 재구성 |
| 14 | 완료된 실행이 기록에 `crashed · process-exit` | ProcessExit 훅이 정상 종료도 사고로 기록 | End 뒤에는 기록 안 함 |
| 15 | 매핑 없이 실행 화면이 먼저 만들어지면 "실행할 작업을 고르세요"만 | 선택 집합을 한 번만 초기화 | `RunLogic.SyncSelection`(새 매핑 기본 선택) |
| 16 | 시험 어댑터 에이전트 실행이 사실은 실패(기록기 `Value cannot be null`) | 메모리 어댑터에서도 Oracle 기록기를 만듦 · 통합 시험이 "끝남"만 확인 | 기록기 없이 실행, 기록기 예외는 WARN 한 번(`BestEffortRecorder`), 시험은 `done` 확인 |
| 17 | 아이콘+글자 버튼의 UI 자동화 이름 비어 있음(접근성) | 내용이 패널이라 Name 없음 | `Kit.NameForAutomation` |
| 18 | "0 ms", 실행 순서 예시 문장, 토스트가 "다음" 버튼 가림, 메타 버튼 이름 미갱신 | 표시 | `<1 ms`, 실제 순서 표시, 토스트 위치, 이름 동기화 |

### 화면 변경(사용자 요청)
- 단계 막대 머리의 "작업 정의 · 매핑 템플릿 내보내기"는 오른쪽으로 밀지 않고 **▽ 버튼으로 펼침**.
- **왼쪽 단계 막대 ↔ 본문 분할선**(사용자가 뜻한 split 패널): 세로 분할선을 끌어 막대 너비를 160~480px로 조절(`StudioUiState.RailWidth`에 유지). 좁은 창(1000px 미만)에서 막대가 52px로 접히면 분할선은 숨긴다.
- **위·아래 분할 패널**(`Ui/SplitPane.cs`, `PageFrame.SetBodyFill`): 본문이 창 높이를 채우고 가로 분할선을 끌어 위·아래 비율을 바꾼다 — 실행 화면(위: 작업 선택·실행 제어·진행 ↔ 아래: 로그·체크포인트), 테이블 매핑(매핑 목록 ↔ 스키마 탐색), SQL 편집기(편집기 ↔ 결과). 바뀐 비율은 `StudioUiState.RunSplit/TablesSplit/SqlSplit`에 남아 다시 그려도 유지. 각 패널은 내용이 길면 혼자 스크롤한다.

## 3. 알려진 문제

| 심각도 | 내용 | 제안 |
|---|---|---|
| 중 | 메타데이터 읽기 약 1.0~2.8초(목표 1.5초 경계) | 딕셔너리 조회 병합·캐시 |
| 중 | 실행 전 검증 C06 NLS 바이트 길이 보정 미구현 | 후속 |
| 중 | `MIG_CHECKPOINT` 다른 PC 재개 병합 미구현 | 후속 |
| 낮 | 에이전트가 끝난 뒤 클라이언트가 떠나면 `--idle-exit`(10분)까지 살아 있음(결과 재접속용) — 자원은 작음 | 설정 노출 검토 |
| 낮 | 가짜 어댑터(DevHost)에서 C02가 ERROR(ValidationResponder에 ALL_TABLES 응답 없음) | 응답 추가 |
| 낮 | 같은 플러그인 창을 두 개 열 수 있음(본체 동작) | 기존 창 포커스 검토 |
| 낮 | 비밀번호가 아주 짧으면(1~2자) 로그 가림이 다른 글자까지 가림 | 3자 미만은 가리지 않기 |

## 4. 사람이 확인할 항목
`P7-manual-checklist.md` 참고 — 데스크톱 실측은 사용자 지시로 중단했고, 남은 항목은 나중에 일괄 확인한다.

## 5. 시험 환경 재현
docker `oracle-12c`(system/oracle, `localhost:1521/xe`). 사용자 `MIG_IT_SRC`/`MIG_IT_TGT`(비밀번호 `mig_it_src_pw`/`mig_it_tgt_pw`)는 OracleIT 픽스처가 만들고 지운다(`ORACLE_IT_KEEP=1`이면 유지).
실제 설치 검증용 대상 테이블(원본과 같은 이름의 빈 사본 `BIG_SRC`·`SRC_*`, 2,000,000행 `BIG2_SRC`)은 아래 SQL로 만든다.

```sql
CREATE TABLE MIG_IT_TGT.BIG_SRC AS SELECT * FROM MIG_IT_SRC.BIG_SRC WHERE 1=0;  -- 열·PK는 원본 DDL과 같게
GRANT SELECT_CATALOG_ROLE TO MIG_IT_TGT;  -- 자동 확장 여유(DBA_DATA_FILES)를 보려면
```
플러그인 접속 설정은 `%LOCALAPPDATA%\Folderss\plugin-data\zaruous.folderss-oracle-migration\settings.json`(DPAPI 암호화, 키 `migration-settings`).
