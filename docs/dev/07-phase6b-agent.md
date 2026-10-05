# 작업 지시서 P6b — 에이전트 프로세스 · 파이프 · 다시 붙기 (작업id `P6b`)

`docs/dev/00-common.md`를 먼저 읽어라. P1~P5, P6a(엔진 코어)가 끝난 저장소에서 시작한다.
기준: README 4.5(실행 에이전트 — 배포·복사·실행·파이프·진행 보고·다시 붙기·중복 방지·Folderss 종료 시·비정상 종료), `UI-MIG-006` FUNC02·FUNC11·FUNC12, `UI-MIG-008` 실행 에이전트 탭.
**화면은 만들지 않는다**(P6c). 이 작업의 결과는 "플러그인 쪽 `AgentClient`와 `MigrationAgent.exe`가 실제로 서로 통신해 이관을 끝까지 돌린다"는 것이고, 시험은 **콘솔 시험 도구와 xunit 통합 시험**으로 한다.

## 1. 범위

| 넣음 | 빼고 나중에 |
|---|---|
| `MigrationAgent.exe` 본체: 인자 해석, 파이프 서버, `MigrationEngine`(P6a) 호스팅, 로그 파일, 실행 기록 파일, 종료 처리 | 실행 화면(P6c) |
| 플러그인 쪽 `AgentClient`(`Core.Hosting` — WPF 없음): 에이전트 복사·실행·파이프 연결·명령·이벤트·다시 붙기·살아 있는 에이전트 찾기·Job Object | 실행 후 검증(P6c) |
| 파이프 프로토콜(`AgentProtocol` v1 확장), 메시지 직렬화, ACL | |
| `DataDirectory` 파일 규약(`agent\`·`runs\`·`logs\`·`checkpoints\`) | |
| 에이전트 콘솔 도구 `tools/AgentCli`(시험·검수용: 작업 JSON과 접속을 받아 에이전트를 띄워 진행을 콘솔에 보임) | |

## 2. 프로토콜 (`Core.Engine.AgentProtocol` — 설계 소유 규약, 이대로 구현)

**전송**: 이름 있는 파이프 `folderss-migration-<RUN_ID>`(`NamedPipeServerStream`, `PipeDirection.InOut`, `PipeOptions.Asynchronous`, 바이트 모드). 에이전트가 **서버**, 플러그인이 **클라이언트**(여러 번 연결·끊기 가능, 한 번에 연결 1개). ACL: **현재 Windows 사용자만**(`PipeSecurity` — `WindowsIdentity.GetCurrent().User`에게 `ReadWrite`, 다른 모두 거부). 파이프 이름의 `RUN_ID`는 `[A-Za-z0-9-]`만.
**메시지**: UTF-8 JSON **한 줄 한 메시지**(`\n` 구분, 줄 안에 개행 없음 — `System.Text.Json`, camelCase, `JsonIgnoreCondition.WhenWritingNull`), 최대 길이 8MB. 모든 메시지 `{ "type": "...", ... }`.

| 방향 | type | 내용 |
|---|---|---|
| 클라이언트 → 에이전트 | `hello` | `{ protocol: 1, client: "MigrationStudio 1.0.0" }` — 첫 메시지. 에이전트가 프로토콜 불일치면 `error` + 끊음 |
| | `start` | `{ spec: RunSpec }`(비밀번호 포함, **한 번만**, 에이전트는 시작 뒤 메모리 외에 어디에도 쓰지 않음) |
| | `attach` | `{ logTail: 2000 }` — 이미 실행 중인 에이전트에 다시 붙음: 에이전트가 `snapshot`과 최근 로그를 다시 보냄 |
| | `pause` · `resume` · `stop` | 제어 |
| | `ping` | 연결 확인 |
| | `shutdown` | 실행이 끝난 뒤 에이전트를 끝내 달라는 요청(끝나지 않았으면 무시하고 `error`) |
| 에이전트 → 클라이언트 | `welcome` | `{ protocol: 1, agent: "1.0.0+<해시>", pid, state, runId, startedAt }` |
| | `snapshot` | `{ snapshot: RunSnapshot }` (0.25초마다·변화 있을 때) |
| | `log` | `{ entry: LogEntry }` |
| | `checkpoint` | `{ record: CheckpointRecord }` |
| | `end` | `{ snapshot: RunSnapshot, exitReason: "done"|"stopped"|"failed" }` |
| | `error` | `{ code, message }` (프로토콜 오류·시작 실패 — 예: `start-failed`, `protocol`, `busy`) |
| | `pong` | `{}` |

- `AgentProtocol.Version = 1`(P1 상수) 그대로. 메시지 클래스는 `Core.Engine.Messages`에 정의(`RunSpec`·`RunSnapshot`·`LogEntry`·`CheckpointRecord`는 P6a 것 그대로 직렬화 — **형식이 바뀌면 `Version`을 올린다**; `JsonSerializerOptions`는 `AgentProtocol.JsonOptions` 한 곳).
- 직렬화 시험(골든 아님 — 왕복): 모든 메시지 왕복 동일, 알 수 없는 `type`은 무시(앞으로 호환), 8MB 초과는 `error`.

## 3. 에이전트 (`src/MigrationAgent`)

### 3.1 명령줄
```
MigrationAgent.exe --run <RUN_ID> --pipe <파이프이름> --data <DataDirectory> [--parent <Folderss PID>] [--idle-exit <초>]
MigrationAgent.exe --version                                    (P1 그대로)
```
- 비밀번호·접속 문자열은 **명령줄에 절대 넣지 않는다**(시험: `Process` 명령줄 조회로 확인). `--parent`는 정보용(PID만), `--idle-exit`는 실행이 끝난 뒤 클라이언트가 없어도 기다리는 시간(기본 600초 — 그 안에 클라이언트가 안 붙으면 종료. 화면이 닫혀 있다가 다시 열려도 끝난 결과를 볼 수 있게).
- 잘못된 인자 → stderr 사용법 + 종료 코드 2. 파이프 이름이 `folderss-migration-<RUN_ID>`가 아니면 거부(종료 2).

### 3.2 수명 주기
1. 시작: `--data` 아래 폴더 만들기, **작업 이름 뮤텍스**는 에이전트가 아니라 플러그인이 잡는다(4.3), 에이전트는 `runs\<RUN_ID>.json`을 **자기가** 쓴다(`{ runId, pid, pipe, startedAt, agentVersion, state:"waiting", jobName, hostPid }`, 원자적 쓰기, 상태가 바뀔 때마다 갱신).
2. 파이프 서버 열기 → 클라이언트 연결 기다림(무제한 — 시작 후 60초 안에 `start`가 안 오면 종료: 화면이 죽은 채 에이전트만 남지 않게).
3. `hello` → `welcome` → `start`(한 번만, 두 번째는 `error busy`) → `RunSpec` 검증 → `MigrationEngine`(P6a)을 `Task.Run`으로 시작. 리스너는 메시지를 **채널**로 모아 파이프 쓰기 스레드가 보낸다(엔진을 막지 않게, 채널 용량 초과 시 `snapshot`은 최신 것만 남기고 `log`·`checkpoint`는 보존).
4. 클라이언트가 끊겨도 **엔진은 계속 돈다**(이것이 에이전트의 존재 이유). 다시 연결하면 `welcome` + 현재 `snapshot` + `attach`가 요청한 최근 로그를 보낸다. 끊긴 동안의 `log`는 메모리 링 버퍼(2,000줄) + 로그 파일에 남는다. `checkpoint` 이벤트는 쌓지 않는다(최신 `snapshot`에 지금 체크포인트가 있다).
5. 제어 메시지를 엔진 `Pause/Resume/Stop`으로 전달. `stop` 이후 종료까지 기다려 `end`.
6. 엔진이 끝나면(`done`·`stopped`·`failed`) `end` 전송, 실행 기록 파일에 최종 상태·시각, **`--idle-exit`초 동안 클라이언트를 기다리다** 종료(클라이언트가 `end`를 받은 뒤 `shutdown`을 보내면 즉시 종료). 종료 코드: done 0, stopped 10, failed 20, 프로토콜·시작 실패 30, 처리 못 한 예외 40(예외는 `logs\<RUN_ID>.log`와 stderr에).
7. **비정상 종료 대비**: 처리 못 한 예외·`ProcessExit`에서 가능한 만큼 `runs\<RUN_ID>.json`에 `state:"crashed"` + 메시지를 쓴다. 엔진 진행 중 **프로세스가 강제 종료**(Job Object·작업 관리자)되면 아무것도 못 쓴다 — 그래서 플러그인이 "PID가 죽었는데 파일이 `running`"이면 `crashed`로 해석한다(4.4).

### 3.3 로그 파일
`DataDirectory\logs\<RUN_ID>.log`(UTF-8, BOM 없음, 한 줄 = 한 로그 줄: `2026-10-03 14:48:05.123 [INFO] 본문`, 본문의 개행은 다음 줄에 4칸 들여써 이어 씀). 엔진 `OnLog`마다 즉시 쓰되 `FileShare.Read`로 열어 두고 `AutoFlush`(비정상 종료 때도 남게). 비밀번호·연결 문자열 금지(P6a 시험이 보장하지만 에이전트도 시작 메시지의 `spec` 전체를 로그에 찍지 않는다). 보관: 플러그인이 `logDays`(설정)보다 오래된 파일을 정리(4.5).

## 4. 플러그인 쪽 (`Core.Hosting` — WPF 없음, `src/MigrationStudio.Core`)

### 4.1 `AgentLocator` — 에이전트 복사본 준비 (README 4.5 "시작 전 복사")
```csharp
public static class AgentLocator
{
    /// 플러그인 폴더의 agent\ 를 DataDirectory\agent\<버전>\ 로 복사하고 exe 경로를 돌려준다. 이미 같은 버전이 있고 파일 해시가 같으면 복사하지 않는다.
    public static AgentInstall Prepare(string pluginDirectory, string dataDirectory);   // 버전 = MigrationAgent.dll의 InformationalVersion(+해시 포함)
    public static IReadOnlyList<string> InstalledVersions(string dataDirectory);
    public static void CleanOld(string dataDirectory, string keepVersion, ISet<int> livePids);   // 살아 있는 에이전트가 쓰는 폴더는 지우지 않는다
}
public sealed class AgentInstall { public string Version, Directory, ExePath; public bool Copied; }
```
- 복사는 임시 폴더(`agent\.tmp-<guid>`)에 한 뒤 폴더 이름 바꾸기(복사 도중에 죽어도 반쪽 폴더가 남지 않게). 두 창이 동시에 준비해도 안전(뮤텍스 `Local\folderss-migration-agent-install`).
- 플러그인 폴더에 `agent\MigrationAgent.exe`가 없으면 `AgentMissingException`("에이전트 파일이 없습니다 — 플러그인을 다시 설치하세요").

### 4.2 `AgentClient` — 실행·연결
```csharp
public sealed class AgentClient : IDisposable
{
    public static AgentClient Launch(AgentInstall install, string runId, string dataDirectory, int hostPid, bool killOnHostExit, ILaunchLog log);   // Process.Start
    public static AgentClient Attach(AgentRunRecord record);                                                // 살아 있는 에이전트에 다시 붙음
    public event Action<RunSnapshot> Snapshot; public event Action<LogEntry> Log; public event Action<CheckpointRecord> Checkpoint; public event Action<RunSnapshot, string> Ended; public event Action<string> Disconnected;   // 스레드 풀 스레드에서 호출 — 구독자가 UI 스레드로 마샬링
    public Task ConnectAsync(TimeSpan timeout, CancellationToken ct);          // 파이프 연결 + hello/welcome. 에이전트 프로세스가 이미 죽었으면 AgentExitedException(종료 코드·로그 마지막 줄)
    public Task StartAsync(RunSpec spec, CancellationToken ct);                // start 메시지 → 에이전트가 error를 보내면 AgentStartException(message)
    public Task AttachAsync(int logTail, CancellationToken ct);
    public void Pause(); public void Resume(); public void Stop();            // 보내고 잊음(응답은 이벤트)
    public Task ShutdownAsync();
    public int Pid { get; } public string RunId { get; } public string PipeName { get; }
}
public sealed class AgentRunRecord { RunId, Pid, Pipe, StartedAt, AgentVersion, State, JobName, HostPid, Message }   // runs\<RUN_ID>.json
public static class AgentRuns
{
    public static List<AgentRunRecord> ListAlive(string dataDirectory);       // runs\*.json 중 PID가 살아 있고 프로세스 이름이 MigrationAgent이며(PID 재사용 방지) 시작 시각이 기록과 맞는 것
    public static List<AgentRunRecord> ListRecent(string dataDirectory, int count);   // 끝난 것 포함, 최근 순. 죽은 PID인데 State가 waiting|running이면 State = "crashed"로 해석해 돌려줌(파일은 건드리지 않음)
    public static void PruneOld(string dataDirectory, int logDays);           // runs\·logs\ 정리(살아 있는 것·최근 체크포인트가 가리키는 RUN_ID는 보존)
}
```
- `Launch`: `ProcessStartInfo`(`UseShellExecute = false`, `CreateNoWindow = true`, 작업 폴더 = 에이전트 폴더, 환경 변수 상속 — **`DOTNET_`·`COMPlus_` 같은 호스트 변수가 에이전트 런타임 선택을 깨지 않는지 시험**), 표준 출력·오류는 비동기로 읽어 `ILaunchLog`(진단용 최근 50줄 보관 — 시작 실패 메시지에 붙임)에 쌓는다. `Process.Start` 실패(파일 막힘·바이러스 검사 차단 등)는 문구 있는 `AgentLaunchException`.
- **Job Object**: `killOnHostExit`이면 `CreateJobObject` + `JOBOBJECT_EXTENDED_LIMIT_INFORMATION.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, `AssignProcessToJobObject`(P/Invoke, `Core.Hosting.JobObject`). 핸들은 **프로세스(Folderss)가 끝나면 닫히게** 정적 필드로 보관(Folderss가 종료되면 OS가 에이전트도 종료). `false`면 `Process.Start`만 — 에이전트는 부모와 독립(`CREATE_BREAKAWAY_FROM_JOB`이 필요한 환경이면 시도하고 실패하면 로그만 남김). Folderss 자체가 Job 안에서 돌고 있어 `AssignProcessToJobObject`가 `ERROR_ACCESS_DENIED`를 주는 환경(중첩 Job 미지원 Windows 7 등)은 무시하고 경고만(Windows 8+는 중첩 지원).
- **연결**: 클라이언트는 `NamedPipeClientStream(".", name, InOut, Asynchronous)`, `ConnectAsync`를 에이전트가 파이프를 열 때까지 **재시도**(50ms 간격, 전체 timeout 기본 10초). 읽기 루프가 한 줄씩 파싱해 이벤트로. 연결이 끊기면 `Disconnected`(에이전트가 죽었는지 `Process.HasExited`/PID로 판별해 메시지에 `exitCode` 포함) — **자동 재연결은 하지 않는다**(화면이 결정).
- **스레드 안전**: 보내기는 락, 읽기는 단일 루프. `Dispose`는 파이프만 닫고 **에이전트를 죽이지 않는다**.

### 4.3 중복 실행 방지·동시 실행 한도 (`RunGuard`)
- 작업마다 이름 있는 뮤텍스 `Local\folderss-migration-<jobName>`(이름 정제: `[A-Za-z0-9_-]` 외 `_`, 128자 제한 + 해시 꼬리)를 **플러그인이** 시작 때 잡고 **에이전트 PID가 끝날 때까지** 유지한다. 단 뮤텍스는 프로세스 수명에 묶이므로(Folderss가 닫히면 풀림), 에이전트가 계속 도는 동안은 `AgentRuns.ListAlive`의 `JobName`으로 두 번째 방어선을 둔다: 같은 `JobName`의 살아 있는 에이전트가 있으면 시작을 막는다(`AlreadyRunningException(runId, pid)` — 문구 "이 작업은 이미 실행 중입니다(RUN_ID, PID). 진행 화면에 다시 붙으려면 실행 화면을 여세요").
- `MaxConcurrent`(설정): 살아 있는 에이전트 수 ≥ 한도면 `ConcurrencyLimitException`.
- 시험: 두 `RunGuard` 호출 경합, 죽은 PID 기록은 막지 않음, PID 재사용(다른 프로세스 이름) 오탐 없음.

### 4.4 `RunLauncher` — 시작부터 한 번에 (P6c 화면이 이것만 부른다)
```csharp
public sealed class RunLauncher
{
    public RunLauncher(IPluginDirs dirs, MigrationSettings settings, IDatabaseAdapter adapter);
    /// 계획 → 저장소 결정(AUTO) → 에이전트 준비·실행 → 연결 → 시작. 실패는 단계 이름이 든 예외(LaunchStage: Prepare·Plan·Launch·Connect·Start).
    public Task<AgentClient> StartAsync(MigrationJob job, SchemaMetadata sourceMeta, SchemaMetadata targetMeta, ISet<string> selectedIds, string runMode, PasswordResolver passwords, IProgress<string> stage, CancellationToken ct);
    public Task<AgentClient> AttachAsync(AgentRunRecord record, CancellationToken ct);
}
```
- 순서: `RunGuard` → `AgentLocator.Prepare` → `RunPlanner.BuildAsync`(P6a, 대상의 `CheckControlStoreAsync`로 AUTO 해결) → `AgentClient.Launch` → `ConnectAsync` → `StartAsync(RunSpec)` → 첫 `snapshot`을 받을 때까지(10초) 기다림. 어느 단계든 실패하면 **시작한 에이전트를 정리**(아직 `start`를 못 보냈으면 프로세스 종료, 보냈으면 `stop`+`shutdown`)하고 뮤텍스 해제.
- 비밀번호는 `PasswordResolver`(P3 `ConnectionService.PasswordFor` 호출을 감싼 델리게이트)로 구한다 — 없으면 시작하지 않는다.
- `RunMode = RESUME`이고 저장소가 LOCAL이면 `DataDirectory\checkpoints`를, TARGET이면 대상 `MIG_CHECKPOINT`를 `RunPlanner`가 읽는다(P6a).

## 5. 콘솔 도구 (`tools/AgentCli`, net8.0 콘솔, 솔루션 `tools` 폴더, 플러그인 zip 미포함)
```
AgentCli.exe run   --job <작업.json> --settings <settings.json|--seed-it> --mode DRY|EXECUTE|RESUME [--select id,id] [--data <폴더>] [--password-src <비밀번호> --password-tgt <비밀번호>]
AgentCli.exe attach --data <폴더> [--run <RUN_ID>]            (살아 있는 에이전트에 다시 붙어 진행을 콘솔에 보임)
AgentCli.exe list   --data <폴더>                              (살아 있는/최근 실행 목록)
AgentCli.exe pause|resume|stop --data <폴더> --run <RUN_ID>
```
- 진행은 한 줄 갱신(`850,000 / 1,240,325 행 · 68% · 38,138행/초 · 경과 00:00:29 · 남은 00:03:45`) + 로그 줄. `--seed-it`는 `localhost:1521/xe`의 `MIG_IT_SRC`→`MIG_IT_TGT` 시험 설정(P2 상수)을 쓴다. Ctrl+C = `stop`이 아니라 **연결만 끊고 에이전트는 계속**(안내 문구 출력) — 다시 `attach`.
- 검수자는 이 도구로 직접 돌려 본다: 작업을 띄우고 → 도구를 끄고 → 에이전트가 계속 도는지 → `attach`로 다시 붙는지.

## 6. 시험
- **단위**: 프로토콜 왕복·알 수 없는 type·길이 초과, `AgentLocator`(복사·해시 같으면 생략·동시 준비·반쪽 폴더 없음), `AgentRuns.ListAlive/ListRecent`(가짜 `runs\*.json` + 실제 PID: 현재 프로세스 PID로 "살아 있음", 죽은 PID, PID 재사용(이름 불일치)), `RunGuard`, 로그 파일 줄 형식.
- **통합(실제 에이전트 exe, Oracle 없이)**: 시험 프로젝트가 `MigrationAgent.exe`를 `Process`로 띄운다 — Oracle 없이 돌리려고 에이전트에 **숨은 시험 모드 환경 변수 `MIGRATION_AGENT_TEST_ADAPTER=memory`**(Release에서도 동작하되 문서화하지 않음, 켜져 있으면 `[WARN] 시험 어댑터로 실행 중` 로그)를 두어 `ISourceFactory`/`ITargetFactory`를 `MemoryTable` 기반(P6a Testing)으로 바꾼다(`RunSpec.Job`의 메모리 데이터는 환경 변수가 가리키는 JSON). 시험: ① 시작→진행→완료, `end`·종료 코드 0, 로그 파일·`runs\*.json` 내용 ② **클라이언트가 끊겨도 엔진 계속**: 연결 끊고 3초 뒤 다시 붙어 완료 상태 확인 ③ 일시정지·이어서·중지(종료 코드 10) ④ 두 번째 `start` → `error busy` ⑤ 60초 안에 `start`가 안 오면 에이전트 종료(시험은 환경 변수로 5초로 줄임) ⑥ 명령줄에 비밀번호 없음(`wmic`/`Process` 명령줄 조회 — .NET에서는 `Process.GetProcessById(...)`의 명령줄을 못 읽으니 `System.Management`(WMI)나 `NtQueryInformationProcess`가 필요하면 시험 프로젝트에만 쓰고 보고) ⑦ 다른 Windows 사용자 시나리오는 시험할 수 없으니 ACL 설정 코드(`PipeSecurity`가 현재 사용자만 허용)를 단위로 검사 ⑧ Job Object: `killOnHostExit=true`로 띄운 에이전트가, 시험이 띄운 **자식 시험 호스트 프로세스**(간단한 콘솔)를 강제 종료하면 같이 죽는지 ⑨ `killOnHostExit=false`면 호스트가 죽어도 에이전트가 산다 ⑩ 비정상 종료(에이전트를 `Process.Kill`) → `ListRecent`가 `crashed`로 해석, 같은 `JobName`으로 새로 시작 가능, 로그 마지막 줄이 남아 있음 ⑪ 에이전트 폴더 복사본이 실행 중이어도 플러그인 폴더 교체(`CleanOld`)가 실행 중 폴더를 지우지 않음.
- **실제 Oracle(OracleIT)**: `AgentCli`와 같은 경로로 `RunLauncher`를 써서 `BIG_SRC` 200,000행(P6a 준비물)을 실제 에이전트로 이관(속도·창 닫기 시나리오): 시작 → 연결 끊기 → 에이전트 계속 → `Attach` → 완료, 체크섬 동일. 중지 → `RunLauncher`로 RESUME → 완주. 에이전트를 `Kill` → 대상 행 수가 체크포인트와 일치 → RESUME로 완주(TARGET 저장소).

## 7. 완료 기준
```powershell
dotnet build MigrationStudio.sln -c Release --nologo --no-incremental     # 경고 0
dotnet test tests/MigrationStudio.Tests -c Release --nologo
$env:ORACLE_IT_DSN = "localhost:1521/xe"; dotnet test tests/MigrationStudio.OracleIT -c Release --nologo
dotnet publish src/MigrationStudio -c Release --nologo                     # zip: agent\MigrationAgent.exe 실행 가능, Testing·AgentCli 없음
dotnet run --project tools/AgentCli -c Release -- run --job <시험 작업.json> --seed-it --mode EXECUTE   # 직접 돌려 로그를 보고서에 붙일 것
```
보고서 `docs/dev/reports/P6b-report.md`: 프로토콜 변경이 있으면 이유와 `Version`, 시험 표, 에이전트 실측(시작 → 첫 snapshot ms, 메모리), 알려진 한계(예: Windows 7, 보안 소프트웨어 차단).
