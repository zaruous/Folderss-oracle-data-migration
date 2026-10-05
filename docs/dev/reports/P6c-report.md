# P6c 보고서 — 실행 후 검증 서비스 로직 (Cursor)

## 1. 바꾼·만든 파일

| 파일 | 설명 |
|---|---|
| `src/MigrationStudio.Core/Validation/PostValidationContext.cs` | 실행 후 검증 컨텍스트(접속·메타·Plan·BaseRows) |
| `src/MigrationStudio.Core/Validation/PostValidationSql.cs` | P01~P06·FUNC03 SQL 생성 |
| `src/MigrationStudio.Core/Validation/PostValidationEngine.cs` | P01~P06 엔진, `IPostValidationRunner` 구현 |
| `src/MigrationStudio.Core/Validation/RejectSummary.cs` | 오류 테이블 ORA 번호별 요약(FUNC03) |
| `src/MigrationStudio/Services/PostValidationService.cs` | 스텁 제거, 컨텍스트 조립 후 엔진 호출 |
| `src/MigrationStudio.Testing/Adapters/PostValidationResponder.cs` | 단위 시험용 가짜 질의 응답 |
| `src/MigrationStudio.Testing/Adapters/FakeAdapter.cs` | `CountAsync`가 `QueryResponder` CNT를 읽도록 보강 |
| `tests/MigrationStudio.Tests/PostValidationEngineTests.cs` | Dry·중지·PASS·행 수 ERROR 단위 시험 |
| `tests/MigrationStudio.OracleIT/V7OracleUsers.cs` | P6c 전용 `MIG_V7_SRC`/`MIG_V7_TGT` |
| `tests/MigrationStudio.OracleIT/PostValidationOracleTests.cs` | 이관 후 PASS·P02/P03/P04 tamper Oracle IT |

## 2. 설계서와 다르게 한 것

- **`RunSummary`/`TaskResult`**: 계약대로 `PostValidationRequest.Final`(`RunSnapshot`/`TaskSnapshot`)만 사용. `TargetRowsBefore`는 실행 스냅숏에 없어 **이관 후 대상 행 수 − Inserted**로 추정(캐시). `TRUNCATE_INSERT`·BaseRows=0이면 0으로 본다.
- **`PostValidationEngine` 생성자**: `IDatabaseAdapter` + `PostValidationContext` — UI는 `PostValidationService`가 컨텍스트를 채운 뒤 엔진을 생성한다.
- **집계 질의**: `IDatabaseAdapter.CountAsync`는 `SELECT COUNT(*) FROM ( … )` 래핑이라 `SELECT COUNT(*) AS CNT …`와 맞지 않는다. 실행 후 검증은 **QueryAsync로 CNT를 읽는다**(설계 공백·POC와 동일 결과를 위해).
- **P04 샘플**: 행 수 1만 미만 테이블은 `SAMPLE (0.05)` 생략, `FETCH FIRST 100` + `DBMS_RANDOM` 사용(Oracle 12c IT에서 빈 샘플 방지).

## 3. 골든과 다른 항목

없음(골든 미변경).

## 4. 실행 명령과 실측

```text
dotnet build MigrationStudio.sln -c Release --nologo --no-incremental
→ 경고 0, 오류 0

dotnet test tests/MigrationStudio.Tests -c Release --nologo
→ 통과 295, 실패 0, 건너뜀 0

ORACLE_IT_DSN=localhost:1521/xe dotnet test tests/MigrationStudio.OracleIT -c Release --nologo --filter "FullyQualifiedName~PostValidation"
→ 통과 4, 실패 0, 건너뜀 0 (약 43s)
```

## 5. 못 한 것

- **2·3장 WPF/`RunPresenter`/DevHost 스크린**: Claude 담당 범위로 구현하지 않음.
- **`RunView`에 `TargetRowsBefore` 저장**: 상태 확장 없이 추정으로 처리 — 실행 직전 값이 필요하면 `RunView` 또는 run spec 저장을 후속 제안.
- **FUNC03 UI 지연 조회**: `RejectSummary.FormatAsync`만 Core에 추가; 실행 화면 작업 행 클릭 연동은 UI 쪽.

## 6. 설계 공백 제안

- P02/P05 **(a) 전체 버킷 vs (b) 존재 확인**: `TargetRowsBefore` 추정 + `INSERT_ONLY`/`TRUNCATE_INSERT`일 때 (a), 그 외 (b). MERGE 후에도 Inserted=0·Updated>0이면 추정 `TargetRowsBefore>0`로 (b)로 넘어가는 것이 자연스럽다.
- **체크포인트 제외 원본 범위**: P01 원본 COUNT는 `mapping.Where`만 반영(체크포인트 조건 없음). 재개분 `BaseRows`는 `RunView.BaseRows`에서 전달.
