# P4 보고서 — 테이블·컬럼 매핑 · SQL 원본 · 템플릿

작업id: `P4` · 구현: Cursor · 검수: (대기)

## 1. 바꾼·만든 파일 (요약)

### P3b 선행 보정 (0a)
- `Logic/ConnectionLogic.cs` — 메타/시험 상태 문자열을 화면 문구와 동일하게 (`(캐시 · HH:mm:ss)` 등).
- `Ui/Pages/ConnectionPage.cs` — 카드 foot에 [접속 테스트][메타데이터…] + 상태 줄 순서, 요약 5행(색 표시 포함), Pill+버전+ms.
- `Ui/Kit.cs` — `ShellIconButton` 스타일을 Application 리소스에 등록(설정 탭 □ 아이콘).
- `Ui/Settings/MigrationSettingsView.cs` — 편집 폼 ScrollViewer, 목록 말줄임·가로 스크롤 제거, `--fake-oracle` 접속 테스트 결과.
- `Ui/StepRail.cs` — `SetFootTemplateExportAction` (매핑 템플릿 링크).
- `tools/DevHost/Shot.cs` — 콘텐츠 루트 1100×700 정확히 렌더(흰 띠 완화).

### Core · Testing
- `Core/Adapters/QueryModels.cs`, `Oracle/*` (SelectGuard, ValueText, Query partial), `Sql/SqlProbe.cs`, `SqlSourceService.cs`, `SqlSourceBinds.cs`, `Mapping/MappingFactory.cs`, `SqlSourceAnalyzer` overload, `SqlGenerator.BuildMappingWhereClauses`.
- `src/MigrationStudio.Testing/` — `FakeAdapter`, `SampleResponder`.
- Oracle IT: `tests/MigrationStudio.OracleIT/OracleSqlAdapterTests.cs`.

### WPF
- `Services/StudioState.cs`, `AppServices.cs`, `MappingOperations.cs`, `ConnectionService.cs` (가짜 어댑터).
- `Logic/TablesLogic.cs`, `ColumnsLogic.cs`, `SqlEditorLogic.cs`.
- `Ui/RowGrid.cs`, `Pages/TablesPage.cs`, `Pages/ColumnsPage.cs`, `Dialogs/AddMappingDialog.cs`, `AutoMatchDialog.cs`, `SqlSourceEditorWindow.cs`, `SqlEditorHost.cs`, `MappingUiHost.cs`, `MigrationView.cs`, `ShellMenu.cs`, `Theme.cs`, `Kit.cs` (CellComboBox, SqlTag 등).
- `tools/DevHost/Program.cs` — `--fake-oracle`, `--open`, DevHost 캡처 모드(모달 비블로킹).

### 시험
- `tests/MigrationStudio.Tests/` — SqlProbe, MappingFactory, SqlSourceBinds, OracleSelectGuard, SqlSourceService, MappingUiLogic 등.

## 2. P3b 결함(0a)별 고침

| # | 항목 | 상태 | 비고 |
|---|------|------|------|
| 1 | 접속 카드 바닥 버튼 | 고침 | 아이콘+글자 Button, foot 배치 |
| 2 | 상태 줄 포맷 | 고침 | `ConnectionLogic` + UI 아이콘/DisabledText |
| 3 | 접속 요약 색 표시 | 고침 | KeyValue 5행 안 |
| 4 | 운영 DB 경고 간격 | 고침 | notices margin 12 |
| 5 | 설정 □ 아이콘 | 고침 | `EnsureShellIconStyleInApp` — Segoe Fluent Icons 템플릿 미병합이 원인 |
| 6 | 설정 오른쪽 잘림 | 고침 | ScrollViewer + 목록 Grid |
| 7 | 설정 접속 테스트 결과 | 고침 | `AppServices.DatabaseAdapter` + Pill (DevHost `--fake-oracle`) |
| 8 | 단계 막대 링크 | 고침 | 작업 정의 + 매핑 템플릿(Export) |
| 9 | DevHost 흰 띠 | 완화 | Content 크기·Render 대상 조정 |
| 10 | 접속 카드 높이 | 부분 | 1100×700에서 두 카드 노출; 전략 카드는 스크롤(수용) |

## 3. DevHost 그림 (`docs/dev/reports/P4-shots/`)

| 파일 | 내용 |
|------|------|
| `black-tables.png` / `light-tables.png` | STEP 2 (seed + fake oracle) |
| `tables-add-dialog.png` | 매핑 추가 팝업 |
| `black-columns.png` | SRC_CUSTOMER · MOBILE_NO |
| `columns-sql-source.png` | SQLMAP_MEMBER |
| `sql-editor-check.png` / `sql-editor-error.png` | SQL 편집기 (1080×720, fake 검증 상태) |
| `fix-black-step1.png` / `fix-light-step1.png` | STEP 1 보정 확인 |
| `fix-settings-test-ok.png` | 설정 접속 탭 (fake oracle) |

**기준 대비 남은 차이 (시각)**
- SQL 편집기·컬럼 매핑: POC 대비 탭·Alias·미리보기·검사기 Oracle 샘플 UI는 1차 축소 구현(동작은 Core·FakeAdapter로 연결).
- 테이블 그리드: 일부 열(병합 키 하위 문구 등) POC와 픽셀 단위 차이 가능.
- STEP 1 1100×700: 카드 foot(테스트/메타)이 스크롤 아래에 있을 수 있음 — POC는 카드 높이 ~500 기준.

## 4. 설계 공백·POC와 다른 점

- SQL 편집기: UI-MIG-004의 줄 번호·Alias 탭·100행 미리보기 표는 최소 구현; 검증 결과는 `CheckItem` 목록 + DevHost fake 상태.
- 컬럼 매핑 검사기: 0.3s 디바운스·Oracle FUNC03/04는 `MappingOperations` + FakeAdapter/실Oracle 경로 있으나 UI 칩·샘플 6행 표는 단순화.
- `Engine/EngineModels.cs` · `RunPlanner.cs`: `Mapping` 타입 이름 충돌 해소용 alias (동작 동일).

## 5. 골든

- `tests/MigrationStudio.Tests/Golden/*` **수정 없음**. Golden 필터 시험 포함 전체 단위 시험 통과.

## 6. 실행 명령·수치 (검수 재현)

```text
dotnet build MigrationStudio.sln -c Release --nologo --no-incremental
→ 경고 0, 오류 0 (testhost/DevHost 잠금 시 MSB3021 — 프로세스 종료 후 재실행)

dotnet test tests/MigrationStudio.Tests -c Release --nologo
→ 통과 244, 실패 0, 건너뜀 0

$env:ORACLE_IT_DSN = "localhost:1521/xe"; dotnet test tests/MigrationStudio.OracleIT -c Release --nologo
→ 통과 0, 실패 12 (전체 12) — ORA-01017: MIG_IT_SRC 로그인 거부. docker oracle-12c는 기동 중이나 IT 사용자/비밀번호 미일치(ORACLE_IT_KEEP=1 유지 전제하에 계정 재적재 필요).

dotnet publish src/MigrationStudio -c Release --nologo
→ MigrationStudio.zip 생성

dotnet run --project tools/DevHost -c Release -- --seed --fake-oracle --theme black --shot docs/dev/reports/P4-shots/black-tables.png --size 1100x700 --step 2
→ exit 0
```

### 실 Oracle DevHost (IT_LOCAL)
- 현재 PC: `MIG_IT_SRC` 접속 불가로 **미실행**. 컨테이너 복구 후 `IT_LOCAL` + `--seed`로 SQL 검증·컬럼 샘플 재시험 필요.

## 7. 못 한 것·막힌 것

- Oracle IT 12건: DB 계정 문제(환경). 코드 경로는 `OracleSqlAdapterTests` 추가됨.
- `sql-editor-alias.png`, `template-export` 전용 캡처: Alias 탭 UI 축소·Export SaveFileDialog 블로킹으로 생략(보고서 그림 목록 참고).

## 8. 새 Kit/화면 부품

- `RowGrid`, `CellComboBox`, `CellTextBox`, `Theme.SqlTag`, `Theme.BindName`, `Format.Short`.
