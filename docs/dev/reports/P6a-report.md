# P6a 이관 엔진 코어 구현 보고서

## 1. 변경 파일

| 파일 | 내용 |
|---|---|
| `src/MigrationStudio.Core/Engine/EngineModels.cs` | 실행 사양·계획·체크포인트·로그·스냅숏 모델과 엔진 전용 인터페이스 |
| `src/MigrationStudio.Core/Engine/RunPlanner.cs` | FK 실행 순서, 범위/재개 계획, 병렬 가능 여부, 오류 테이블 이름 결정 |
| `src/MigrationStudio.Core/Engine/MigrationEngine.cs` | bounded Channel 파이프라인, 병렬 작업자, 상태 전이, Pause/Resume/Stop, Dry Run, 재시도, 스냅숏·로그 |
| `src/MigrationStudio.Core/Engine/LocalCheckpointStore.cs` | 작업별 JSON 체크포인트의 임시 파일 기반 원자적 저장 |
| `src/MigrationStudio.Core/Adapters/Oracle/Engine/OracleEngineSql.cs` | Oracle 식별자·접두어 검증과 리터럴 처리 |
| `src/MigrationStudio.Core/Adapters/Oracle/Engine/OracleSourceFactory.cs` | 읽기 전용 세션, 범위/재개 SELECT, 숨은 체크포인트 열, Oracle 형식별 읽기 |
| `src/MigrationStudio.Core/Adapters/Oracle/Engine/OracleSourceProbe.cs` | COUNT, NTILE 작업자 범위, 대상 기존 행 수 계획 조회 |
| `src/MigrationStudio.Core/Adapters/Oracle/Engine/OracleTargetFactory.cs` | 배열 DML, 트랜잭션, 쓰기 방식, 키 존재 조회, 체크포인트 동시 저장 |
| `src/MigrationStudio.Core/Adapters/Oracle/Engine/OracleCheckpointStore.cs` | 대상 `MIG_CHECKPOINT` 읽기·목록·삭제 |
| `src/MigrationStudio.Core/Adapters/Oracle/Engine/OracleControlStore.cs` | 제어 테이블과 오류 테이블 확인·생성 |
| `src/MigrationStudio.Core/Adapters/Oracle/Engine/OracleRunRecorder.cs` | `MIG_RUN`·`MIG_RUN_TASK` 실행 이력 기록 |
| `tests/MigrationStudio.Tests/Engine/Fakes/MemoryFakes.cs` | 결정적 메모리 원본·대상·체크포인트·시계·리스너 가짜 |
| `tests/MigrationStudio.Tests/Engine/MigrationEngineTests.cs` | 엔진 상태·쓰기 방식·오류·재시도·재개·병렬·보안 단위 시험 26개 |
| `tests/MigrationStudio.OracleIT/EngineOracleTests.cs` | Oracle 12c 대용량·재개·거부·SQL·Pause·Dry Run·권한·세션 종료 통합 시험 6개 |
| `docs/dev/reports/P6a-report.md` | 구현 차이, 검증 명령, 실측 성능·메모리, 알려진 한계 기록 |

## 2. 설계와 다르게 한 것·설계 공백

- 사용자 지시를 우선해 메모리 가짜를 `src/MigrationStudio.Testing`이 아니라 `tests/MigrationStudio.Tests/Engine/Fakes/`에 두었다.
- ODP.NET은 배열 DML과 `LOG ERRORS`를 함께 쓰면 ORA-38909가 발생하므로 `CONTINUE` 정책만 행 단위 실행으로 우회했다. STOP/RETRY와 일반 쓰기는 배열 바인드를 사용한다.
- CLOB/BLOB 포함 배열은 ODP.NET 네이티브 메모리 급증을 피하려고 같은 트랜잭션 안에서 2,000행씩 나눈다.
- `RunSpec` 등은 JSON 직렬화와 기존 Core 관례에 맞춰 공개 필드 대신 `get/set` 속성으로 구현했다.
- `AUTO` 판정은 P2의 `CheckControlStoreAsync`가 실행 전에 `TARGET`/`LOCAL`로 해소한다. 엔진은 지시서대로 해소된 값만 받는다. OracleIT에서는 기존 P2 AUTO→LOCAL 시험에 더해 `MIG_IT_RO` 로그인으로 LOCAL 엔진 실행 성공을 확인했다.
- MERGE의 `Updated`는 쓰기 전 키 존재 조회값이다. `LOG ERRORS`로 일부 행이 거부되면 `Math.Min(기존 키 수, 반영 수)`로 제한하므로 어떤 기존 행이 거부됐는지는 구분하지 못한다.
- `CountExistingKeysAsync`는 Oracle IN 목록 한계와 바인드 크기를 고려해 1,000행마다 조회한다. 정확하지만 MERGE·Dry Run에서 추가 왕복 비용이 든다.

골든 파일 변경 및 골든과 다른 항목은 없다.

## 3. 시험 결과

### 명령과 결과

```powershell
dotnet build src/MigrationStudio.Core -c Release --nologo --no-incremental
```

- 성공, 경고 0개, 오류 0개, 1.22초.

```powershell
dotnet test tests/MigrationStudio.Tests -c Release --nologo --filter "FullyQualifiedName~Engine"
```

- 통과 26, 실패 0, 건너뜀 0, 392ms.

```powershell
$env:ORACLE_IT_DSN="localhost:1521/xe"
dotnet test tests/MigrationStudio.OracleIT -c Release --nologo --filter "FullyQualifiedName~Engine"
```

- 통과 6, 실패 0, 건너뜀 0, 1분 22초.

### Oracle 12c 실측

| 시험 | 결과 |
|---|---|
| `BIG_SRC` → `BIG_TGT`, 200,000행, INSERT_ONLY, 작업자 4, Fetch 5,000, Commit 10,000 | 8.200초, 24,389행/초, 원본/대상 행 수·체크섬 일치 |
| 같은 200,000행 MERGE | 8.607초, Updated 200,000, 체크섬 일치 |
| 관리 힙 | 시작 대비 종료 +74,753,768바이트, 최대 증가 +77,899,992바이트 |
| 프로세스 작업 집합 | 시작 대비 +62,988,288바이트, 최대 증가 +63,275,008바이트 |
| TARGET 중지·재개 | 80,000행 커밋에서 중지, 대상 행 수=체크포인트, 재개 후 200,000행·체크섬 일치 |
| LOCAL 중지·재개 | 80,000행 커밋에서 중지, 재개 후 200,000행·체크섬 일치 |
| 엄격 대상 `TGT_STRICT` | 100행 중 Inserted 50, Rejected 50, `ERR$_TGT_STRICT`의 RUN_ID 건수와 일치 |
| SQL JOIN 원본 | 20,000행 성공 |
| Pause→Resume·Dry Run | 커밋 경계 일시정지 후 완주, Dry Run 대상 행 수 불변·Updated 200,000 |
| CREATE TABLE 권한 없는 `MIG_IT_RO` | LOCAL 저장소로 20,000행 성공 |
| 대상 세션 강제 종료 | ORA-03135, 진행 중 15,000행 롤백, 재시도 1/3 후 200,000행 완주 |
| DELETE_INSERT | 실제 Oracle 배열 키 삭제 후 1,000행 입력 성공 |

시험 표: `MIG_IT_SRC.BIG_SRC`, `MIG_IT_TGT.BIG_TGT`, `MIG_IT_SRC.STRICT_SRC`, `MIG_IT_TGT.TGT_STRICT`, `MIG_IT_TGT.ERR$_TGT_STRICT`, 제어 표 `MIG_RUN`·`MIG_RUN_TASK`·`MIG_CHECKPOINT`. 기본 실행은 fixture 종료 때 `MIG_IT_*` 사용자를 제거한다. `ORACLE_IT_KEEP=1`이면 유지한다.

## 4. 알려진 한계·못 한 것

- 단위 시험은 Channel의 관측 최대 버퍼가 2 이하임을 검증하지만 1,000,000행을 별도 메모리 가짜로 반복하지는 않았다. 실제 메모리 실측은 더 복합적인 CLOB 포함 200,000행 Oracle 시험 수치다.
- 동일 체크포인트 값이 여러 행에 반복되는 열을 NTILE 경계로 쓰면 범위가 겹칠 수 있다. 병렬 체크포인트 열은 사실상 단조 고유 키여야 하며 검증 규칙 보강이 필요하다.
- 병렬 작업자의 동일 시점 일관성을 위한 `AS OF SCN`은 설계 문서 9장의 미결정 사항이라 구현하지 않았다.
- 전체 솔루션 빌드·publish·zip 검사는 동시 작업 중인 P4 파일과 프로젝트를 건드리지 않기 위해 수행하지 않았다. 허용된 Core 빌드와 Engine 필터 시험은 모두 통과했다.
