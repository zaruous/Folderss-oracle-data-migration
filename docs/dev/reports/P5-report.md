# P5 보고서 — 실행 전 검증 엔진 · 서비스 (Cursor)

## 1. 바꾼·만든 파일

| 파일 | 설명 |
|---|---|
| `src/MigrationStudio.Core/Validation/ValidationContext.cs` | 검증 입력(접속·메타·시험 결과·SQL DESCRIBE 캐시) |
| `src/MigrationStudio.Core/Validation/ValidationSql.cs` | C02·C06~C13용 SELECT 문자열 빌더 |
| `src/MigrationStudio.Core/Validation/ExpressionProfiler.cs` | UI-MIG-003 SQL-2 실측(8열 배치) |
| `src/MigrationStudio.Core/Validation/ValidationEngine.cs` | `RunPreAsync` C01~C13, POC `runPre` 흐름 |
| `src/MigrationStudio.Core/Mapping/MappingService.cs` | `Status`/`CheckColumn`/`SourceInfo`에 `measured` 오버로드 |
| `src/MigrationStudio/Services/ValidationService.cs` | `StudioState`+`ConnectionService` → `ValidationContext` → 엔진 |
| `src/MigrationStudio.Testing/Adapters/ValidationResponder.cs` | FakeAdapter용 검증 질의 응답(POC 통계) |
| `src/MigrationStudio.Testing/Adapters/FakeAdapter.cs` | `QueryAsync`에 SELECT/WITH 전용 가드 |
| `tests/MigrationStudio.Tests/ValidationEngineTests.cs` | 단위 시험(게이트·SQL·sampleJob·취소·SELECT 전용) |
| `tests/MigrationStudio.OracleIT/ValidationOracleTests.cs` | `MIG_V5_*` Oracle IT |

## 2. 설계와 다르게 한 것

- **C06 문자 집합 바이트 보정**: 설계서의 NLS 보정·`(추정)` Detail은 시간 관계상 `MappingService` 실측+`LENGTHB` 프로파일 수준까지만 반영. 전체 보정식은 P5 후속 또는 C06 전용 질의 확장 제안.
- **C02 DB 존재 확인**: 캐시와 DB 불일치 시 별도 ERROR 항목을 추가(기존 메타 기반 항목 유지). POC는 메타만; 설계서는 실제 `ALL_TABLES` 확인을 요구.
- **체크포인트 DB 병합(SQL-4)**: `MIG_CHECKPOINT` 테이블 읽기·작업 파일과 병합 INFO는 미구현(저장소 TARGET + 테이블 있을 때만). 작업 파일 `Checkpoints`만 POC처럼 INFO.
- **컬럼 매핑(C03) vs 실측(C05~)**: C03은 메타 `Status(null)`, 형식·NOT NULL 경고는 프로파일 후 `Status(measured)`로 `MergeMeasuredIssues` — POC와 같이 화면 문구는 유지하되 실측 반영 경로를 분리.

## 3. 골든과 다른 항목

- 없음(골든 167·`MappingStatus` 시그니처는 `measured=null` 위임으로 유지).

## 4. 실행 명령·실측

```text
dotnet build MigrationStudio.sln -c Release --nologo --no-incremental
  → 경고 0, 오류 0 (DevHost.exe 잠금 시 taskkill 후 재빌드)

dotnet test tests/MigrationStudio.Tests -c Release --nologo
  → 통과 290, 실패 1 (AgentIntegrationTests.Agent_memory_run_completes — 에이전트 파이프 60s 타임아웃, 로컬 환경)
  → ValidationEngineTests 8/8 통과, 골든 포함 나머지 282/283 통과

ORACLE_IT_DSN=localhost:1521/xe dotnet test tests/MigrationStudio.OracleIT -c Release --nologo --filter "FullyQualifiedName~Validation"
  → 통과 3, 실패 0, 건너뜀 0 (MIG_V5_SRC/TGT/RO, 종료 시 사용자 DROP)

dotnet test tests/MigrationStudio.Tests -c Release --nologo --filter "FullyQualifiedName!~AgentIntegrationTests"
  → 통과 290, 실패 0 (검증·골든만 보려면 위 필터 사용)
```

POC `sampleJob` + `poc-golden` 메타로 `RunPreAsync` 시 기대: CHANNEL_CD ERROR, MOBILE VARCHAR WARN(표본 5%), `tm-order` 컬럼 매핑 6/7 INFO, `tm-sql-member`(use=false) SQL 항목 없음 — DevHost `FakeValidationService` canned 목록과 항목 수는 다름(use=false SQL 제외).

## 5. 못 한 것·막힌 것

- **3장 상태·4장 WPF·DevHost 스크린**: Claude 담당(미구현).
- **P6 DevHost `--run-validation` 실 Oracle STEP 4 실측**: 본 작업 범위에서 DevHost 스크린 명령 미실행.
- **AgentIntegrationTests**: `MigrationAgent.exe` 경로 수정(테스트 프로젝트 5단계 상위) 후에도 파이프 연결 60s 실패 — 검증 작업과 무관, CI/에이전트 환경 이슈로 보고.

## 6. 설계 공백 제안

- C06 charset 보정: `NlsInfo` + `OracleCharset.MaxBytesPerChar` 헬퍼를 Core에 두고 Detail에 `(추정)` 접두.
- C13 `MIG_CHECKPOINT` 병합: `ValidationContext`에 `CheckpointStore` 해석(P3) 추가.
