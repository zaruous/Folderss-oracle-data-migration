# P2 보고서 — Oracle 어댑터 · 메타데이터 · 캐시

## 1. 바꾼·만든 파일

| 파일 | 설명 |
|---|---|
| `src/MigrationStudio.Core/Adapters/ConnectionTarget.cs` | 접속 대상·Describe·검증 |
| `src/MigrationStudio.Core/Adapters/AdapterModels.cs` | NlsInfo, ConnectionTestResult, ControlStoreCheck, AdapterKind |
| `src/MigrationStudio.Core/Adapters/IDatabaseAdapter.cs` | 어댑터 계약 |
| `src/MigrationStudio.Core/Adapters/AdapterException.cs` | OracleErrors 기반 예외 |
| `src/MigrationStudio.Core/Adapters/OracleErrors.cs` | Describe/CodeOf/취소·끊김 처리 |
| `src/MigrationStudio.Core/Adapters/OracleVersionText.cs` | ServerVersion → "Oracle 12c" 등 |
| `src/MigrationStudio.Core/Adapters/DatabaseAdapters.cs` | KINDS·For() |
| `src/MigrationStudio.Core/Adapters/Oracle/OracleConnectionHelper.cs` | 연결 문자열·DisableOOB·BindByName |
| `src/MigrationStudio.Core/Adapters/Oracle/OracleColumnTypeFormatter.cs` | ALL_TAB_COLUMNS → 표시 형식 |
| `src/MigrationStudio.Core/Adapters/Oracle/OracleDatabaseAdapter.cs` | Test/메타/제어테이블 Oracle 구현 |
| `src/MigrationStudio.Core/Metadata/MetadataJsonOptions.cs` | 메타데이터 JSON 옵션(P1 제안 반영) |
| `src/MigrationStudio.Core/Storage/MetadataCache.cs` | 메타데이터 캐시 원자적 저장 |
| `src/MigrationStudio.Core/Storage/JobDraft.cs` | 임시 작업 모델 |
| `src/MigrationStudio.Core/Storage/JobDraftStore.cs` | current.draft.json |
| `tests/MigrationStudio.Tests/AdapterUnitTests.cs` | P2 단위 시험 |
| `tests/MigrationStudio.OracleIT/OracleFixture.cs` | MIG_IT_* 스키마 준비·정리 |
| `tests/MigrationStudio.OracleIT/OracleAdapterTests.cs` | 실 Oracle 시험 |
| `tests/MigrationStudio.Tests/AssertEx.cs`, `Golden/JsonAssert.cs`, `CoreUnitTests.cs` | xUnit 분석기 경고 0 |
| `tests/MigrationStudio.OracleIT/Placeholder.cs` | 삭제 |

## 2. 설계서와 다르게 한 것

- **`JobDraftStore` 래퍼 JSON**: 지시서는 `JobFile`만 언급하지만 `SavedAt`·`Dirty`·`FilePath`를 함께 저장하려고 `{ savedAt, dirty, filePath, jobJson }` 봉투를 `JobJson.Options`로 직렬화했다. 작업 본문은 여전히 `JobFile.Parse`/`Serialize`이다.
- **`OracleErrors.CodeOf`**: `ConnectionTestResult.ErrorCode`는 ODP.NET 겉 ORA-50201보다 안쪽 ORA-12514·12541 등을 우선한다(Describe의 WithInnerCause와 같은 의도). UI-MIG-001 FUNC02의 ErrorCode 필드에 대한 세부 규칙은 설계서에 없었다.
- **IT 스키마 `MIG_IT_OTHER`**: 「다른 사용자 스키마 → 빈 목록」 시험에서 `SYS`는 ALL_*에 공개 딕셔너리가 많아, 권한 없는 전용 사용자(`MIG_IT_OTHER.SECRET`)를 추가했다(지시서의 MIG_IT_* 범위 안).

## 3. 골든과 다른 항목

없음(골든 파일 미변경).

## 4. 실행 명령과 실제 출력

```powershell
dotnet build MigrationStudio.sln -c Release --nologo
dotnet build MigrationStudio.sln -c Release --no-incremental -nologo
dotnet test tests/MigrationStudio.Tests -c Release --nologo
$env:ORACLE_IT_DSN = "localhost:1521/xe"; dotnet test tests/MigrationStudio.OracleIT -c Release --nologo
dotnet publish src/MigrationStudio -c Release --nologo
```

| 항목 | 결과 |
|---|---|
| 빌드(일반 Release) | 경고 **0**, 오류 **0** |
| 빌드 `--no-incremental` | 경고 **0**, 오류 **0** |
| MigrationStudio.Tests | 통과 **193**, 실패 0, 건너뜀 0 |
| MigrationStudio.OracleIT (`ORACLE_IT_DSN=localhost:1521/xe`) | 통과 **6**, 실패 0, 건너뜀 0 |
| publish 산출 zip | `src/MigrationStudio/bin/Release/net8.0-windows/win-x64/MigrationStudio.zip` — `Oracle.ManagedDataAccess.dll`, `agent/Oracle.ManagedDataAccess.dll` 포함 |

**Oracle IT 실측** (`tests/MigrationStudio.OracleIT/bin/Release/net8.0/oracle-it-report.txt`):

| 측정 | 값 |
|---|---|
| fixture 준비 | 750 ms |
| 접속 TestAsync (MIG_IT_SRC, readOnly) | 50 ms, `LatencyMs=0` (DUAL 3회 중앙값 반올림) |
| LoadMetadataAsync (MIG_IT_SRC) | 3855 ms (어댑터 `ElapsedMs=3852`) |
| 닫힌 포트(1) 실패 `ErrorCode` | ORA-12541 |

**시험 전용 계정 비밀번호**(docker oracle-12c localhost:1521/xe, SYSTEM/oracle):

| 사용자 | 비밀번호 | 용도 |
|---|---|---|
| MIG_IT_SRC | `mig_it_src_pw` | 원본 메타·접속 시험 |
| MIG_IT_TGT | `mig_it_tgt_pw` | CREATE TABLE → AUTO=TARGET |
| MIG_IT_RO | `mig_it_ro_pw` | CREATE TABLE 없음 → AUTO=LOCAL |
| MIG_IT_SPECIAL | `Pw;x=1'y` | 연결 문자열 특수문자 |
| MIG_IT_OTHER | `mig_it_other_pw` | SRC에 권한 없는 스키마(빈 메타) |

시험 종료 후 사용자는 삭제됨(`ORACLE_IT_KEEP=1`이면 유지).

## 5. 못 한 것·막힌 것

없음.
