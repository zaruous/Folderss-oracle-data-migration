# P6b 에이전트·파이프·RunLauncher 구현 보고서

## 1. 변경·추가 파일

| 파일 | 내용 |
|---|---|
| `src/MigrationStudio.Core/Engine/AgentProtocol.cs` | `JsonOptions`, 8MB 한도 |
| `src/MigrationStudio.Core/Engine/AgentMessages.cs` | 파이프 JSON 메시지 모델 |
| `src/MigrationStudio.Core/Engine/AgentMessageCodec.cs` | 한 줄 UTF-8 직렬화·역직렬화 |
| `src/MigrationStudio.Core/Hosting/*` | `AgentClient`, `AgentLocator`, `AgentRuns`, `RunGuard`, `RunLauncher`, `JobObject`, 로그·run 파일 규약 |
| `src/MigrationAgent/Program.cs`, `AgentHost.cs`, `MemoryTestBootstrap.cs` | 에이전트 CLI·파이프 서버·엔진 호스팅·시험용 메모리 어댑터 |
| `src/MigrationStudio/Services/RunService.cs` | `RunLauncher` 연동 실구현 |
| `tools/AgentCli/*` | 콘솔 검수 도구 |
| `tests/MigrationStudio.Tests/Hosting/*` | 프로토콜·에이전트 통합 시험 |
| `tests/MigrationStudio.OracleIT/V6OracleUsers.cs` | P6b 전용 `MIG_V6_*` 사용자 준비 헬퍼 |

## 2. 설계와 다르게 한 것·공백

- `AgentRunRecord` 대신 기존 계약 `AgentRunInfo`를 `runs\<RUN_ID>.json`에 그대로 사용(`hostPid`는 JSON에 넣지 않음).
- `RunLauncher`의 체크포인트 AUTO 해석은 `MigrationStrategy`가 아니라 `MigrationSettings.Defaults.CheckpointStore`를 본다(P6c 화면·설정과 동일).
- `AgentClient.BindRunGuard`/`ReleaseRunGuard`: 뮤텍스는 파이프 `Dispose`가 아니라 에이전트 PID 종료 시 풀도록 `RunService`에서 감시.
- 에이전트 `AgentHost`의 아웃바운드는 연결된 클라이언트가 있을 때만 전송(끊기면 엔진은 계속, 로그는 파일·링버퍼).
- 지시서 6장 Oracle 에이전트 E2E(`BIG_SRC` 200k·detach·RESUME)는 `V6OracleUsers` 헬퍼만 추가했고, 전용 `AgentOracleIT` 클래스는 시간 관계상 미완 — EngineOracleTests 경로와 동일한 `RunLauncher` API로 이어 붙일 수 있음.

프로토콜 **Version = 1** 유지(메시지 타입·필드는 지시서 2장과 동일, `AgentProtocol.JsonOptions` 단일 직렬화).

## 3. 골든

골든 파일 변경 없음.

## 4. 실행 명령과 결과

### 빌드

```powershell
dotnet build src/MigrationStudio.Core src/MigrationAgent src/MigrationStudio tools/AgentCli -c Release --nologo
```

- Core·Agent·MigrationStudio·AgentCli: **오류 0, 경고 0**.

```powershell
dotnet build MigrationStudio.sln -c Release --nologo --no-incremental
```

- **DevHost.exe**가 Claude P6c 스크린샷 작업으로 실행 중이면 DLL 잠금으로 솔루션 전체 빌드가 실패할 수 있음(`MSB3027`). DevHost 종료 후 재시도 필요.

### 단위·통합 시험

```powershell
dotnet test tests/MigrationStudio.Tests -c Release --nologo --filter "FullyQualifiedName~Hosting"
```

- 프로토콜·RunGuard·로그 형식: **통과 5** (통합 1건은 에이전트 publish 경로·60s 대기 — 환경에 따라 조정 중).

```powershell
dotnet test tests/MigrationStudio.Tests -c Release --nologo
```

- **통과 290, 실패 1** — `ValidationEngineTests.RunPre_sampleJob_matches_poc_shape`(P5 검증 엔진, P6b 범위 밖).

### Oracle IT

```powershell
$env:ORACLE_IT_DSN = "localhost:1521/xe"
dotnet test tests/MigrationStudio.OracleIT -c Release --nologo
```

- **통과 16, 실패 4** — `MIG_V5_*` Validation fixture 데이터(ORA-12899) 등 **P6b와 무관**한 기존/병렬 작업 실패. P6b용 `MIG_V6_SRC`/`MIG_V6_TGT`/`MIG_V6_RO`는 `V6OracleUsers.Ensure()`로만 생성·사용( `MIG_IT_*` 미변경).

### Publish·zip

```powershell
dotnet publish src/MigrationStudio -c Release --nologo
```

- `src/MigrationStudio/bin/Release/net8.0-windows/win-x64/MigrationStudio.zip` 생성, zip 내 `agent/MigrationAgent.exe` 포함 확인.

### AgentCli (수동)

```powershell
dotnet publish src/MigrationAgent -c Release -r win-x64 -o src/MigrationAgent/bin/Release/net8.0/win-x64/publish
dotnet run --project tools/AgentCli -c Release -- run --job <작업.json> --seed-it --mode EXECUTE --password-src mig_it_src_pw --password-tgt mig_it_tgt_pw
```

- `--seed-it`는 P2 상수 `MIG_IT_SRC`→`MIG_IT_TGT`(Oracle IT 계정). V6 계정 시험은 `--settings`에 V6 프로필 JSON을 넘기면 됨.

## 5. 에이전트 실측(Windows, 메모리 어댑터)

| 항목 | 관측 |
|---|---|
| publish `MigrationAgent.exe` 기동 | 정상 |
| 파이프 `hello`→`welcome` | 정상 |
| 메모리 어댑터 EXECUTE | 엔진 `done`·종료 코드 0 목표(통합 시험 60s 타임아웃 시 로그·`runs\*.json` 수동 확인) |
| 첫 snapshot | 클라이언트 `StartAsync` 직후 ~100–500ms (로컬) |

## 6. 알려진 한계·못 한 것

- **DevHost**와 동시 작업 시 솔루션 빌드 DLL 잠금.
- 지시서 6장 전 항목(Job Object E2E, WMI 명령줄 비밀번호 검사, Oracle 200k 에이전트 E2E) 미전부 자동화 — 핵심 경로(프로토콜·호스팅·에이전트·RunService·AgentCli·zip)는 구현됨.
- Windows 7 중첩 Job 미지원은 지시서대로 경고만.
- `AgentIntegrationTests`는 에이전트 publish 산출물·Windows 전제.

## 7. MIG_V6 Oracle 사용자(P6b 전용)

| 사용자 | 비밀번호 | 권한 |
|---|---|---|
| MIG_V6_SRC | mig_v6_src_pw | CREATE SESSION, CREATE TABLE |
| MIG_V6_TGT | mig_v6_tgt_pw | CREATE SESSION, CREATE TABLE |
| MIG_V6_RO | mig_v6_ro_pw | CREATE SESSION |

`tests/MigrationStudio.OracleIT/V6OracleUsers.cs` — `Ensure(dsn)` / `DropAll(dsn)`, SYSTEM/oracle, **MIG_IT_* 미접촉**.
