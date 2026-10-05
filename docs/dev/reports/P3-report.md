# P3 보고서 — WPF 셸 · 접속 · 설정 · 작업 파일

## 1. 변경·추가 파일

| 파일 | 설명 |
|---|---|
| `src/MigrationStudio.Core/Adapters/Oracle/OracleDatabaseAdapter.cs` | 자기 스키마일 때 `USER_*` 뷰로 메타데이터 질의(ALL_* 사전 스캔 완화) |
| `src/MigrationStudio/Logic/*.cs` | Step/Connection/Job/Settings 화면 논리(단위 테스트 대상) |
| `src/MigrationStudio/Services/*.cs` | StudioState, ConnectionService, SettingsRepository, PasswordProtector, DraftAutosave |
| `src/MigrationStudio/Ui/**` | Theme, Kit, Dialogs, ShellMenu, StepRail, PageFrame, StatusBar, MigrationView, ConnectionPage, PlaceholderPage, 설정·작업 정의 UI |
| `src/MigrationStudio/MigrationPlugin.cs` | 설정 탭 등록, CreateView → MigrationView |
| `tools/DevHost/*` | 테마 로드, FakePluginManager, PNG 캡처, `--seed` |
| `tests/MigrationStudio.Tests/PluginLogicTests.cs` | Logic 단위 테스트 |
| `docs/dev/reports/P3-shots/*.png` | DevHost 레이아웃 캡처 9장 |

## 2. 설계와 다른 점 / 설계 공백

- **마이그레이션 설정 «기본값» 탭**: POC 대비 FormGrid·RadioCards(체크포인트 저장소) 일부만 구현(커밋·오류 접두어 중심). 검수 PNG(`*-settings-defaults.png`)는 간략한 레이아웃이다.
- **DevHost `--seed`**: POC 4접속 + `IT_LOCAL`을 넣되 비밀번호는 저장하지 않는다(시연 시 입력).
- **`JobDraftStore`**: 임시 저장 envelope의 `FilePath`가 드래프트 파일 경로로 덮어써질 수 있어, 열기 경로 복원은 설계 의도와 다를 수 있다(코어 설계 파일이라 미수정).

## 3. 골든과 다른 항목

없음(골든 미변경).

## 4. P2 후속 — LoadMetadataAsync 성능

| 항목 | P2 보고 | 이번 Oracle IT (MIG_IT_SRC, localhost:1521/xe) |
|---|---|---|
| `LoadMetadataAsync` wall | 3855 ms | 5513 ms (어댑터 `ElapsedMs=5510`) |
| `TestAsync` | — | 56 ms |

**조치**: 접속 사용자 스키마와 `:OWNER`가 같을 때 `USER_TABLES` / `USER_TAB_COLUMNS` / `USER_CONSTRAINTS` / `USER_TAB_COL_STATISTICS`를 사용하도록 변경(결과는 Oracle IT 6건 동일 통과).

**질의별 Stopwatch 로그**는 어댑터에 상시 로그를 넣지 않았고, IT 로그상 병목은 여전히 `LoadMetadataAsync` 전체 구간(연결·다중 리더·12c Docker 환경)으로 보인다. 추가 분리(통계 생략 옵션 등)는 설계 변경이 필요해 제안만 남긴다.

## 5. IT_LOCAL 시연(DevHost / Oracle)

계정: `MIG_IT_SRC` / `mig_it_src_pw` (`ORACLE_IT_KEEP=1`로 IT 사용자 유지).

접속 시험 성공 시 UI 상태 줄 형식(Logic):

- `ok|Oracle 12c|<LatencyMs>|HH:mm:ss` — IT에서 `Version=Oracle 12c`, `LatencyMs`는 0~수 ms대(샘플링 3회 중앙값).

메타데이터 성공 시 요약 형식(Logic):

- `MIG_IT_SRC · 테이블 3 · 뷰 1 · 컬럼 33` + `(HH:mm:ss · <ElapsedMs> ms)` 또는 캐시 표시.

## 6. 완료 기준 명령 및 실제 출력

```text
dotnet build MigrationStudio.sln -c Release --no-incremental --nologo
→ 빌드했습니다. 경고 0개, 오류 0개

dotnet test tests/MigrationStudio.Tests -c Release --nologo
→ 통과: 202, 실패: 0, 건너뜀: 0

dotnet publish src/MigrationStudio -c Release --nologo
→ MigrationStudio.zip 생성 (DevHost 항목 없음)

dotnet test tests/MigrationStudio.OracleIT -c Release --nologo (ORACLE_IT_DSN=localhost:1521/xe)
→ 통과: 6, 실패: 0, 건너뜀: 0

dotnet run --project tools/DevHost -c Release -- --seed --theme black --shot docs/dev/reports/P3-shots/black-step1.png --size 1100x700 --step 1
→ exit 0
```

**플러그인 zip 목록(루트)**: `plugin.json`, `MigrationStudio.dll`, `MigrationStudio.deps.json`, `MigrationStudio.Core.dll`, `Oracle.ManagedDataAccess.dll`, `System.*.dll`, `agent/MigrationAgent.exe` 및 agent 의존 DLL.

## 7. P3-shots (9장)

| 파일 | 테마 | 내용 | 크기 |
|---|---|---|---|
| `black-step1.png` | black | STEP 1 접속 | 1100×700 |
| `light-step1.png` | light | STEP 1 접속 | 1100×700 |
| `black-step2.png` | black | STEP 2 자리 화면 | 1100×700 |
| `light-step2.png` | light | STEP 2 자리 화면 | 1100×700 |
| `black-settings-connections.png` | black | 마이그레이션 설정 · 접속 | 1100×700 |
| `light-settings-connections.png` | light | 마이그레이션 설정 · 접속 | 1100×700 |
| `black-settings-defaults.png` | black | 마이그레이션 설정 · 기본값 | 1100×700 |
| `light-settings-defaults.png` | light | 마이그레이션 설정 · 기본값 | 1100×700 |
| `black-step1-collapsed-900x600.png` | black | STEP 1, 단계 막대 접힘 | 900×600 |

## 8. 못 한 것

- POC 수준의 설정 «기본값»·«에이전트» 탭 전 필드·RadioCards 완전 동형화(P4 이전 범위에서 축소 구현).
- 메타데이터 3테이블 스키마에서 500 ms 이하 목표는 달성하지 못함(위 §4).
