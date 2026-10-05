# 작업 지시서 P3 — WPF 셸 · 접속 화면 · 마이그레이션 설정 · 작업 파일 (작업id `P3`)

`docs/dev/00-common.md`를 먼저 읽어라. P1(Core)·P2(어댑터)가 끝난 저장소에서 시작한다.
기준 화면은 POC(`design/poc` — `python -m http.server 8765`로 열어 직접 눌러 봐라)와 설계서 README 2장·7장, `UI-MIG-001`, `UI-MIG-007`(작업 파일 부분), `UI-MIG-008`.
코드 모양의 본보기는 DB Helper(`D:\git\cshap\Folderss-oracle-db-helper\src\MyPlugin\Ui`): XAML 없이 코드로 만든 WPF, 테마 키는 `SetResourceReference`, 백그라운드 예외는 반드시 잡음(플러그인이 Folderss와 같은 프로세스).

## 0. 설계자가 미리 해 둔 것

- `plugin.json` `"hasSettings": true`(적용됨). `tools/DevHost/DevHost.csproj`는 빈 `Program.cs`로 만들어 두었다 — 6장대로 구현하라. 테스트 프로젝트는 `src/MigrationStudio/Logic/*.cs`를 이미 링크한다.
- `tests/MigrationStudio.Tests.csproj`가 `src/MigrationStudio/Logic/*.cs`를 링크해 컴파일한다(WPF 없는 화면 논리를 여기서 시험).
- 새 프로젝트 `tools/DevHost/DevHost.csproj`(net8.0-windows WPF exe, 플러그인·계약 참조, `Dirkster.AvalonDock 4.74.1` — Folderss `Themes/Controls.xaml`을 그대로 읽으려고). 솔루션 등록됨. **플러그인 zip에는 들어가지 않는다.**

- **P2 후속(먼저 처리)**: P2 보고서의 `LoadMetadataAsync` 3,852 ms는 테이블 3개짜리 스키마에는 너무 느리다. 질의별 시간을 재서(Stopwatch 로그) 느린 질의를 찾고(`ALL_TAB_COL_STATISTICS`·`ALL_CONSTRAINTS` 조인·`ALL_*` 사전 뷰의 `OWNER` 필터 형태가 의심스럽다), 고칠 수 있으면 고쳐라(힌트·질의 분리·불필요한 조인 제거 — **결과는 같아야 하고 P2 OracleIT 6개가 그대로 통과**). 고친 뒤 시간과 질의별 내역을 보고서에 적어라. 고칠 수 없으면 원인만 적는다. 이 일은 화면 작업 전에 끝내라(화면의 "메타데이터 불러오기"가 이 시간을 그대로 체감한다).

## 1. 범위

| 넣음 | 빼고 나중에 |
|---|---|
| 셸: 메뉴 막대·아이콘 막대·단계 막대·화면 틀·상태줄·단축키 | 테이블·컬럼 매핑 화면(P4), SQL 원본 편집기(P4), 검증(P5), 실행(P6) — 이번엔 자리 화면 |
| STEP 1 접속 화면 전부(UI-MIG-001) | 매핑 템플릿 가져오기·내보내기(P4) |
| 마이그레이션 설정: 대화상자 + Folderss 설정 탭(UI-MIG-008) | 실행 에이전트 탭의 "실행 중" 목록(P6 — 지금은 "없음") |
| 작업: 새 작업·열기·저장(JSON)·YAML로 저장·작업 정의 보기·임시 저장 | |
| DevHost(설계자 검수용 화면 캡처 도구) | |

아직 없는 기능의 메뉴·아이콘은 **보이되 꺼 둔다**(툴팁 "다음 단계에서 구현"). 단계 2~5를 누르면 자리 화면(아래 5.6)이 뜬다.

## 2. 파일 배치 (이름은 이대로)

```
src/MigrationStudio/
  MigrationPlugin.cs                IFolderssPlugin — Initialize에서 설정 탭 등록, CreateView마다 new MigrationView
  Logic/                            WPF 없는 화면 논리(시험 대상, net8.0에서도 컴파일되게 System.Windows 금지)
    StepLogic.cs  ConnectionLogic.cs  JobLogic.cs  SettingsLogic.cs  Labels.cs
  Services/
    PasswordProtector.cs            DPAPI(CurrentUser, 엔트로피 "Folderss.Migration.v1")
    SettingsRepository.cs           설정 읽기·저장·변경 알림(열린 창들)
    StudioState.cs                  창 하나의 상태: 작업·파일 경로·dirty·세션(접속 시험 결과·메타데이터)
    ConnectionService.cs            접속 테스트·메타데이터 읽기(어댑터 호출, 비밀번호 구하기, 캐시)
    DraftAutosave.cs                임시 저장(1초 디바운스, 창 하나만 소유)
  Ui/
    Theme.cs  Kit.cs  Dialogs.cs  ShellMenu.cs  StepRail.cs  StatusBar.cs  PageFrame.cs  MigrationView.cs
    JobDefinitionDialog.cs
    Pages/ConnectionPage.cs  Pages/PlaceholderPage.cs
    Settings/MigrationSettingsView.cs  Settings/MigrationSettingsDialog.cs  Settings/MigrationSettingsPage.cs
tools/DevHost/  Program.cs(또는 App.cs)  FakePluginManager.cs  ThemeLoader.cs  Shot.cs
```

## 3. 시각 설계 (POC CSS → WPF)

### 3.1 공통 값
- 글꼴: 창 `AppFontFamily` 13px. 고정폭 `Theme.Mono`(Cascadia Mono, Consolas, D2Coding) 12px. 아이콘 `Segoe Fluent Icons, Segoe MDL2 Assets`.
- 모서리: 컨트롤 4, 카드 6. 간격 단위 4(대부분 8·12·14).
- 색: 테마 키(README 7.1) + `Theme`의 의미 색(Danger·Warning·Success)과 접속 색. **틴트**(배경 연한 색)는 의미 색의 알파 15%(경고 17%)로 `Theme`에 고정 붓으로: `SuccessTint`·`WarningTint`·`DangerTint`. 강조 틴트는 테마마다 강조색이 달라 고정할 수 없으니 `AccentBrush`를 `Opacity 0.15`로 겹친 Border를 쓴다(`Kit.AccentTint()`).
- 아이콘 글리프(POC `dom.js` ICON과 같음): newDoc E8A5 · open E838 · save E74E · saveAs E792 · importFile E8B5 · exportFile EDE1 · code E943 · link E71B · play E768 · pause E769 · stop E71A · resume E777 · check E73E · close E711 · add E710 · del E74D · arrow E72A · back E72B · info E946 · warn E7BA · error E783 · copy E8C8 · setting E713 · checklist E9D5 · view E890 · sync E895 · table E80A · page E7C3 · magic E8D4.

### 3.2 `Kit` — 화면 부품(정적 메서드, POC `components.js`와 1:1)

| 메서드 | 모양 |
|---|---|
| `Card(title, tools, body, foot)` | Border(PanelBackground, BorderBrush 1, 모서리 6). 머리: 최소 높이 40, 안쪽 6·12, 아래 테두리, 제목 SemiBold + 오른쪽 도구. 본문 안쪽 12. 바닥: 위 테두리, 안쪽 9·12, 가로 WrapPanel |
| `Field(label, control, required, hint)` | 세로: 라벨 11.5px SecondaryText(필수면 뒤에 빨간 `*`), 컨트롤, 힌트 11.5px DisabledTextBrush. 간격 3 |
| `FormGrid(columns, fields…)` | 2열(또는 3열) 균등, 간격 가로 12·세로 10. 너비 520 미만이면 1열 |
| `Notice(kind, inlines)` | info·warn·err·ok. 안쪽 8·10, 모서리 4, 틴트 배경 + 같은 색 45% 테두리, 왼쪽 아이콘(info E946·warn E7BA·err E783·ok E73E), 글자 12px, 줄바꿈. 안에 링크 버튼을 넣을 수 있게 `Inline` 목록을 받는다 |
| `Pill(kind, text, icon)` | 높이 20, 모서리 10, 안쪽 0·9, 11.5px — ok(성공)·warn·err·run(강조) |
| `Tag(text, kind)` | 높이 16, 모서리 3, 10px SemiBold, 테두리 — `ro`(성공색, "읽기 전용"·"쓰기 금지"), `pk`(경고색) |
| `RoleTag(isSource)` | `SOURCE`·`TARGET` 꼬리표: 10px Bold, 원본 = 강조색 글자 + 강조 틴트, 대상 = 성공색 글자 + 성공 틴트 |
| `Dot(state)` | 7×7 원 — unknown(DisabledText)·ok·warn·err·run(강조, 0.35↔1 깜빡임 1초) |
| `Spinner()` | 12×12 회전 호(강조색, 0.7초 1바퀴) |
| `Segmented(options, value, onChange)` | 테두리 1·모서리 4·ControlBackground 안에 버튼들(높이 22, 안쪽 0·10). 고른 것: 강조 틴트 + 강조색 SemiBold + 안쪽 강조 테두리. 키보드: 좌우 화살표 |
| `RadioCards(group, items, value, onChange)` | 세로 목록, 각 항목 테두리 1·모서리 4·안쪽 5·8, 제목 + 설명(11.5px SecondaryText), 오른쪽에 추가 요소(오류 테이블 접두어 칸). 고른 항목: 강조 테두리 + 강조 틴트 |
| `PrimaryButton(text, icon)` / `Button(text, icon)` / `GhostButton` / `IconButton(glyph, tooltip)` | DB Helper `ShellUi.PrimaryButton`·`SmallButton` 모양. 아이콘이 있으면 글리프 + 6 + 글자 |
| `LinkButton(text, onClick)` | 강조색 글자, 밑줄 없음, 마우스 올리면 밑줄 |
| `KeyValue(rows)` | 2열 Grid: 키 12px SecondaryText(줄바꿈 없음), 값 12px(줄바꿈 허용), 간격 4·12 |
| `SectionLabel(text)` | 11px SemiBold DisabledTextBrush, 아래 6 |
| `EmptyState(glyph, title, text)` | 가운데 정렬: 큰 아이콘 28px DisabledText, 제목 SemiBold, 설명 SecondaryText |
| `DbBadge(profile)` | DB Helper `Theme.DbBadge`와 같음(높이 18, 10.5px SemiBold). 접속 없음 → "접속 없음" 위험색 테두리. `WriteBlocked`는 색 표시가 없을 때만 회색 테두리 |
| `Toast(owner, text, kind)` | 창 오른쪽 아래 떠서 2.5초 뒤 사라지는 알림(Popup 또는 맨 위 Grid 층). 겹치면 위로 쌓음 |

### 3.3 셸 배치 (`MigrationView` : Grid)

```
행0  메뉴 막대   높이 26  SurfaceBackground  아래 테두리     파일(F) 매핑(M) 실행(R) 보기(V) 도움말(H)
행1  아이콘 막대 높이 30  SurfaceBackground  아래 테두리     [아이콘들 …]                         작업: NAME *
행2  본문 Grid:  단계 막대(208, 창 너비 1000 미만이면 52로 접힘) | 화면(PageFrame)
행3  상태줄      높이 24  SurfaceBackground  위 테두리  11.5px SecondaryText
```

- **메뉴 막대·아이콘 막대**: DB Helper `ShellMenu`를 옮기고(MenuItem 템플릿 XAML·`ShellIconButton` 스타일 그대로) 항목만 바꾼다. 메뉴 항목·아이콘 목록은 POC `app.js`의 `MENUS`·`ICONS`와 같은 순서·글자·단축키. 단 **보기 메뉴의 테마(Black·Light) 항목은 뺀다**(테마는 Folderss가 정함). 도움말 > "이 POC에 대하여" 대신 "Migration Studio 정보(_A)…"(이름·버전·플러그인 id·에이전트 경로).
- 아이콘 색: 시작 = 성공색, 일시정지 = 경고색, 중지 = 위험색. 오른쪽 끝 `작업: ` + 작업 이름(PrimaryText SemiBold) + 바뀌었으면 ` *`.
- **단계 막대**(`StepRail`, PanelBackground, 오른쪽 테두리):
  - 위 작업 머리(안쪽 12·14·10, 아래 테두리): "이관 작업" 11px DisabledText / 작업 이름 13.5px SemiBold(말줄임, 툴팁 = 설명) + ` *` / DB 배지 → 화살표(10px) → DB 배지.
  - 단계 목록(안쪽 10·8): 한 줄 = 번호 원(22×22, 테두리 1) + 9 + [라벨 SemiBold / 요약 11.5px SecondaryText], 안쪽 7·8, 모서리 5, 단계 사이 세로선(1px BorderBrush). 마우스 올리면 RowHoverBrush, 지금 단계 = 강조 틴트 + 왼쪽 2px 강조 막대.
  - 번호 원 상태: 기본(ControlBackground·BorderBrush·SecondaryText 숫자) / done(성공색 테두리·글자 + 틴트, 글자 ✓ E73E 10px) / warn(경고색) / error(위험색, 글자 `!`) / busy(강조 테두리 + Spinner). 지금 단계면 원을 꽉 채움(기본 = 강조색, done = 성공색 …) + 글자 PanelBackground.
  - 아래(위 테두리, 안쪽 10·12·12): 링크 두 줄 "작업 정의"(page 아이콘) · "매핑 템플릿"(exportFile, P4까지 꺼짐).
  - 접힌 모양(52): 작업 머리·라벨·요약 숨김, 번호 원만 가운데, 툴팁 = "라벨 (Ctrl+n) — 요약".
  - 키보드: 단계에 포커스(Tab), Enter·Space로 이동.
- **화면 틀**(`PageFrame`): ScrollViewer(세로 자동) 안에 안쪽 16·20·0, 최대 너비 1480:
  - 머리: [`STEP n` 꼬리표(11px SemiBold 강조색 + 강조 틴트, 모서리 3, 안쪽 1·6) + 제목 18px SemiBold] / 설명 SecondaryText(최대 760, 줄바꿈), 오른쪽 동작 버튼들(WrapPanel). 머리와 본문 간격 14, 본문 요소 사이 14.
  - 바닥(스크롤 밖 고정, 위 테두리, 안쪽 10·20, WindowBackground): [‹ 이전 단계 라벨](ghost, back 아이콘) / 안내 문구(12px SecondaryText, 말줄임) / `다음: 라벨`(주 버튼). 첫 단계는 이전 없음, 마지막은 다음 없음.
- **상태줄**: Dot(원본 시험 상태) + 원본 DB 배지 + 화살표 + Dot(대상) + 대상 DB 배지 | 실행 상태 글자("대기" — P6에서 채움) | (오른쪽) "Oracle 어댑터" | 플러그인 버전.

### 3.4 단축키 (창 전체, `PreviewKeyDown`)
`Ctrl+1…5` 단계 · `Ctrl+O` 열기 · `Ctrl+S` 저장 · `Ctrl+Shift+S` YAML로 저장 · `Alt+F/M/R/V/H` 메뉴(WPF 기본 액세스 키) · `Ctrl+Q`·`F5`·`F6`은 이번엔 "다음 단계에서 구현" 토스트. 대화상자가 떠 있으면 창 단축키는 쉰다.

## 4. 상태와 서비스

### 4.1 `StudioState` (창마다 하나, UI 스레드에서만 만짐)
- `Job`(MigrationJob), `FilePath`(열거나 저장한 파일, 없으면 null), `Dirty`, `Settings`(MigrationSettings — `SettingsRepository.Changed`를 받으면 다시 읽음), `Session.Conn[role]` = { Status: Unknown·Testing·Ok·Error, Result(ConnectionTestResult), MetaLoading, Metadata(SchemaMetadata), MetaError }.
- `event Changed(ChangeScope scope)` — scope: `Job`(작업 내용, dirty 켬), `Session`, `Settings`. 셸은 받으면 단계 막대·상태줄·아이콘·제목을 다시 그리고, 화면은 자기에게 필요한 것만 다시 그린다(입력 중인 TextBox를 다시 만들지 마라 — 포커스·커서가 날아간다).
- `MarkChanged()`: Dirty = true, Changed(Job), `DraftAutosave.Schedule()`.
- 역할(role)은 문자열 상수 `Roles.Source`·`Roles.Target`("source"·"target")으로.

### 4.2 `SettingsRepository` (정적)
- `Load(manager)` → `MigrationSettings`(P1 `MigrationSettingsStore.Deserialize`). 읽은 원문(JSON)을 `Revision`으로 함께 돌려준다(`LoadedSettings { Settings, Revision, Error }`). 깨졌으면 Error 문장 + 기본값(저장하면 덮어쓴다고 안내).
- `Save(manager, settings, revision)`: 지금 저장된 원문이 revision과 다르면 저장하지 않고 `SettingsConflictException("다른 창에서 설정을 먼저 저장했습니다. 이 창의 변경은 저장하지 않았습니다 — 설정을 다시 열어 고치세요.")`. 같으면 `SetSetting("migration-settings", json)` → `Changed`(UI 스레드, 처리기 예외 격리 — DB Helper `ConnectionRepository.NotifySubscribers`와 같게).
- 비밀번호: 화면에서 입력받은 평문은 저장 직전에 `PasswordProtector.Protect`로 `ProtectedPassword`에 넣는다. "저장" 끔이면 `ProtectedPassword = null`.

### 4.3 비밀번호 구하기 (`ConnectionService.PasswordFor(profile, owner)`)
1. 이번 창에서 이미 입력받은 값(창 메모리 사전, 프로필 id 기준) 2. 저장된 값(`Unprotect`, 실패하면 "저장된 비밀번호를 풀 수 없습니다(다른 PC·다른 Windows 사용자). 다시 입력하세요."를 보이며 3으로) 3. 입력 대화상자(DB Helper `PasswordPrompt`와 같은 모양: DB 배지·접속 주소·이유·[이번 창에서만 기억] 체크 기본 켬). 취소면 테스트·불러오기 취소(조용히).

### 4.4 `ConnectionService`
- `TestAsync(role)`: Status = Testing → 어댑터 `TestAsync(target, readOnly: role == Source)` → Ok/Error. 결과는 `Session.Conn[role]`에. 같은 역할 진행 중이면 무시.
- `LoadMetadataAsync(role)`: 시험이 Ok가 아니면 먼저 TestAsync. 성공하면 `MetadataCache.Save(DataDirectory, profileId, meta)`. 실패 문장은 `MetaError`(빨간 줄).
- `TestAllAsync()`: 두 역할 동시에. 둘 다 성공 → 토스트 "두 접속 모두 연결됨".
- 창을 열 때·접속/스키마를 고를 때: `MetadataCache.TryLoad`가 있으면 그것을 넣는다(Cached 표시).
- 창이 닫히면 진행 중인 작업을 취소(CancellationTokenSource)하고, 끝난 Task의 예외는 관찰만 한다(Folderss 멈춤 방지).

### 4.5 작업 파일·임시 저장 (UI-MIG-007 FUNC01~03)
- **새 작업**: dirty면 "저장하지 않은 변경이 있습니다" [저장]·[저장 안 함]·[취소](DB Helper `Dialogs.AskSaveChanges` 모양). 새 작업 = `JobLogic.NewJob(settings)`(전략은 설정 기본값, 접속 없음, 매핑 없음, 이름 `NEW_MIGRATION`).
- **열기**(Ctrl+O): `OpenFileDialog`(필터 "작업 파일 (*.json;*.yaml;*.yml)|…|모든 파일|*.*" — YAML 읽기는 아직 없음: .yaml을 고르면 "YAML 열기는 아직 지원하지 않습니다. JSON으로 저장한 작업을 여세요."). `JobFile.Parse` 실패 문장은 대화상자로. 연 뒤 `JobLogic.ResolveConnections`(4.6), 메타데이터 캐시 넣기.
- **저장**(Ctrl+S): FilePath 없으면 다른 이름으로. `JobFile.Serialize(job, settings)` UTF-8(BOM 없음), 임시 파일에 쓰고 바꿔치기. 성공 → Dirty = false, 토스트 "저장했습니다: 파일명".
- **YAML로 저장**(Ctrl+Shift+S): `JobFile.ToYaml`로 다른 이름 저장(작업의 FilePath는 바꾸지 않는다 — YAML은 내보내기).
- **작업 정의 보기**: 대화상자 900×600, 탭 JSON·YAML(고정폭 읽기 전용 TextBox), 버튼 [복사]·[YAML 저장]·[JSON 저장]·[닫기].
- **임시 저장**(`DraftAutosave`): 작업이 바뀌면 1초 뒤 `JobDraftStore.Save`(Job·FilePath·Dirty). 창이 열릴 때 `TryLoad`로 복원(없으면 새 작업). **프로세스에서 먼저 연 창 하나만 임시 저장을 소유**한다(정적 플래그). 두 번째 창은 새 작업으로 시작하고 임시 저장하지 않으며, 닫을 때 dirty면 저장할지 묻는다. 소유 창이 닫히면 소유권을 놓는다.
- 창 닫기(`Window.Closing`): 소유 창은 임시 저장을 즉시 한 번 쓰고 닫는다(묻지 않음). DB Helper `DbHelperView.View_Loaded`처럼 Loaded에서 창을 찾아 Closing을 구독하고 Unloaded에서 푼다.

### 4.6 접속 참조 맞추기 (`JobLogic`)
- `ResolveConnections(job, settings)`: 역할마다 `profileId`가 설정에 있으면 그대로. 없으면 **접속 이름**(사본 `name`, 대소문자 무시)으로 찾아 profileId를 바꿔 넣는다(작업 dirty). 그래도 없으면 "없는 접속"(사본을 그대로 둠).
- `Missing(job, settings, role)` → 없는 접속의 사본(ConnectionRef) 또는 null.
- `ProfileFromMissing(ref, settings)` → 새 ConnectionProfile(사본의 이름·종류·색·호스트·포트·서비스·사용자, 기본 스키마 = ref.Schema, SavePassword = false). 이름이 이미 있으면 `_2`…
- 접속을 고르면 `job.Source = { ProfileId, Schema = profile.DefaultSchema ?? profile.User }`(사본 필드는 저장할 때 `JobFile.Serialize`가 채움).

## 5. 화면

### 5.1 STEP 1 접속 (`ConnectionPage`) — UI-MIG-001 와이어프레임·버튼 표 그대로, POC `pages/connection.js`와 같은 동작

- 머리 동작: [⚙ 마이그레이션 설정…] [🔗 두 접속 모두 테스트]. 바닥 안내: "접속은 이 플러그인의 설정에만 저장합니다(DB Helper와 공유하지 않음)."
- 접속 카드 두 개 + 가운데 화살표 원(30×30, 강조 틴트, 강조색 화살표) — 3열(1*·40·1*). 화면 너비 820 미만이면 세로로 쌓고 화살표는 아래 방향(E74B).
- **카드 머리**: RoleTag + "원본"/"대상", 오른쪽 DB 배지 + (원본만) Tag ro "읽기 전용"(툴팁 "원본 세션은 SET TRANSACTION READ ONLY로 엽니다").
- **카드 본문**(세로 간격 12):
  1. Field "접속 (마이그레이션 설정)" 필수: ComboBox(항목 = `— 접속을 고르세요` + 설정 접속들. 표시: 이름(SemiBold) + 3칸 + `host/service`(SecondaryText) + 쓰기 금지면 ` (쓰기 금지)`. **대상 쪽은 쓰기 금지 접속을 IsEnabled=false**) + [접속 관리…](setting 아이콘) — 고른 접속이 선택된 채로 설정 대화상자.
  2. 알림(있을 때): 없는 접속 → warn "이 PC의 마이그레이션 설정에 **NAME** (host/service) 접속이 없습니다. [접속 설정에 추가 ›]"(링크: `ProfileFromMissing`으로 만들어 설정에 추가·저장 → 작업이 그 접속을 가리킴 → 설정 대화상자를 그 접속으로 열어 비밀번호 입력받음). 대상이 쓰기 금지 → err "**NAME**은(는) "쓰기 금지" 접속이라 대상으로 쓸 수 없습니다." 대상 색 표시 빨강 → warn "**운영 DB에 씁니다.** TRUNCATE·DELETE 방식은 실행 직전에 확인 체크를 한 번 더 받습니다."
  3. 접속 요약(고른 경우): SurfaceBackground·테두리·모서리 4·안쪽 8·10 안에 KeyValue — DB 종류 / 주소(고정폭 `host:port/service`) / 사용자(고정폭) / 비밀번호("저장됨(DPAPI)" 또는 경고색 "연결할 때 입력") / 색 표시(DB 배지 + 쓰기 금지 Tag).
  4. Field "스키마" 필수: 고정폭 TextBox(입력하면 대문자로, 자리 글자 = 접속 기본 스키마 또는 사용자), 힌트 "이 작업에서 읽고/쓸 스키마. 비우면 접속의 기본 스키마". **칸을 떠날 때** 값이 바뀌었으면 시험 결과·메타데이터를 비우고(캐시가 있으면 캐시로) MarkChanged.
- **카드 바닥**: [접속 테스트](link) [메타데이터 불러오기 | 메타데이터 다시 불러오기](sync), 진행 중이면 해당 버튼 끔. 그 아래 줄 전체 폭 상태(UI 자동 알림 `AutomationProperties.LiveSetting = Polite`):
  - 시험: Testing → Spinner "연결하는 중…" / Ok → Pill ok "✓ Connected" + **버전**(SemiBold) + "23 ms"(SecondaryText) + "14:20:31"(DisabledText) / Error → 위험색 error 아이콘 + 오류 문장(줄바꿈) / Unknown → Dot + "이번 창에서 아직 시험하지 않음"(SecondaryText).
  - 메타: 불러오는 중 → Spinner "메타데이터 불러오는 중…" / 있음 → table 아이콘 "LEGACY_APP · 테이블 6 · 뷰 1 · 컬럼 33" + 캐시면 "(캐시 · 14:20:31)" 아니면 "(14:20:31 · 650 ms)"(DisabledText) / 오류 → 위험색 문장 / 없음 → 경고색 warn 아이콘 "메타데이터를 불러오지 않았습니다".
- **이관 전략 카드**: 제목 setting 아이콘 + "이관 전략", 부제 "이 작업의 기본값(새 작업은 마이그레이션 설정의 기본값으로 시작) — SQL 원본은 자기 Fetch·커밋 크기를 따로 가질 수 있음", 도구 [설정 기본값으로](ghost, 작음 — `MigrationSettingsStore.ApplyDefaults`).
  - FormGrid 2열: 실행 방식 Segmented(전체 이관·증분 이관·CDC 변경 동기화) + 설명 힌트(POC modeDesc 문구) / 증분이면 "증분 기준" Segmented(Primary Key·Timestamp·Sequence·SCN) + 힌트, CDC면 info 알림 "LogMiner·GoldenGate 연동은 다음 단계 범위입니다. 지금은 전체·증분만 실행됩니다.", 전체면 "체크포인트" 설명 글 / 트랜잭션 단위 ComboBox(1,000·10,000·50,000 "rows / commit") / Fetch 크기 ComboBox(1,000·5,000·10,000 "rows / fetch") / 병렬 작업자 Segmented(1·2·4·8) + 힌트 "…(세션 n개)" / 오류 처리 RadioCards(계속 + 오류 테이블[접두어 칸 92px 고정폭, 이 항목일 때만] · 오류 시 중지 · 3회 재시도 — POC 문구).
  - 값이 바뀌면 MarkChanged(입력 칸은 다시 만들지 않는다).
- 접속을 바꾸면: 그 역할 세션 비움 → 캐시 있으면 넣음 → 토스트 "NAME 접속을 골랐습니다 · 메타데이터를 불러오세요".

### 5.2 마이그레이션 설정 (`MigrationSettingsView`) — UI-MIG-008, POC `pages/settings.js`
- **한 View를 두 곳에서**: `MigrationSettingsDialog`(플러그인 안, 900×620, 제목 "마이그레이션 설정", 아래 오른쪽 [취소][저장], Esc = 취소)와 `MigrationSettingsPage`(Folderss 설정 탭, 제목 "Migration Studio" — 설정 창 [저장]이 `Save()`를 부름. 검사 실패면 예외로 던져 설정 창이 실패 메시지를 모은다).
- View는 열 때 설정의 **사본**을 만들어 고치고, `Commit()`에서 검사(모든 접속 `ValidateProfile`, 실패면 접속 탭으로 가서 그 접속을 고르고 첫 오류를 위에 빨갛게 보이고 false) → 비밀번호 암호화 → `SettingsRepository.Save(…, revision)`.
- 위 설명 한 줄(12px SecondaryText) + 탭 3개(DB Helper `WorkspaceUi.SelectorButton` 밑줄 탭 모양): `접속 n` · `기본값` · `실행 에이전트`.
- **접속 탭**: 2열(220~250 고정 | 나머지).
  - 왼쪽 카드 "접속 n" + 도구 [+][⧉][🗑](IconButton). ListBox: 색 점(접속 색, 없으면 DisabledText) + [이름 고정폭 12px SemiBold / host:port/service 11px DisabledText] + 오른쪽 "원본·대상"(지금 작업이 쓰는 역할, 11px) + 쓰기 금지 Tag. 추가 = `NewProfile` 후 선택, 복제 = 새 id + 이름 `_COPY`, 삭제 = 지금 작업이 쓰면 막고 토스트 "지금 작업의 원본·대상 접속이라 지울 수 없습니다"(Folderss 설정 탭에서 열 때는 열린 창들의 작업을 알 수 없으니 막지 않는다).
  - 오른쪽 편집 FormGrid 2열: 접속 이름(필수, 고정폭) · 색 표시(ComboBox: 없음·초록 — 개발·노랑 — 검증·빨강 — 운영) · DB 종류(Kinds, 예정 항목은 꺼짐) · 기본 스키마(대문자, 자리 글자 "비우면 사용자 이름", 힌트 "작업마다 바꿀 수 있음") · 호스트(필수) · 포트(필수, 숫자만) · 서비스명(필수, 힌트 "EZConnect: 호스트:포트/서비스명") · 사용자(필수) · 비밀번호(PasswordBox, 테마 연결 — DB Helper `DialogKit.PasswordInput`) + 체크 "저장"(툴팁 "DPAPI로 암호화해 이 PC의 이 Windows 사용자만 풀 수 있게 저장") · 안전: 체크 "쓰기 금지 (원본 전용)" + 힌트 "운영 원본 DB에 실수로 쓰지 않게".
  - 비밀번호 칸: 저장된 비밀번호가 있으면 빈 칸 + 자리 글자 "저장됨 — 바꿀 때만 입력", 풀 수 없으면 경고 알림 "저장된 비밀번호를 풀 수 없습니다 … 다시 입력하세요". 입력하면 그 값이 새 비밀번호.
  - 검사 오류(이 접속)는 폼 아래 err 알림 한 줄(" · "로 잇기) — 입력할 때마다 갱신.
  - [🔗 접속 테스트] + 결과(POC와 같은 모양): **고친 값 그대로** 시험(저장된 비밀번호를 쓰거나 입력한 값). 결과는 이 탭 안에만.
  - 접속이 없으면 EmptyState(link, "접속이 없습니다", "[추가]로 원본·대상 접속을 만드세요.").
- **기본값 탭**: SectionLabel "새 작업의 이관 전략 기본값" + FormGrid 3열(커밋 단위·Fetch 크기·병렬 작업자 / 오류 처리 ComboBox·오류 테이블 접두어(힌트 "ERR$_ → ERR$_TB_MEMBER")) + SectionLabel "체크포인트 저장소" + RadioCards 3개(POC `CHECKPOINT_STORES` 문구) + 제어 테이블 접두어(힌트 "MIG_RUN · MIG_RUN_TASK · MIG_CHECKPOINT (대상 스키마)" — 입력에 따라 접두어 바뀜).
- **실행 에이전트 탭**: info 알림(POC 문구) + SectionLabel "Folderss를 닫을 때" + RadioCards(계속 실행 (권장)·함께 중지) + FormGrid 3열(동시에 실행할 작업 Segmented 1·2·4 힌트 "작업 하나 = 에이전트 하나" / 실행 로그 보관 ComboBox 7·30·90일) + KeyValue(실행 파일 = `DataDirectory\agent\<버전>\MigrationAgent.exe` 실제 경로, 통신 = "이름 있는 파이프 folderss-migration-<RUN_ID> (현재 Windows 사용자만 접근)", 실행 중 = "없음").
- 저장 성공 → 토스트 "마이그레이션 설정을 저장했습니다", 열린 Migration Studio 창들은 `Changed`로 설정을 다시 읽고 두 역할의 시험 결과를 Unknown으로(주소가 바뀌었을 수 있음).

### 5.3 대화상자 공통 (`Dialogs`)
DB Helper `Dialogs.cs`의 `DialogKit`(Create·Body·Text·Hint·Warning·Buttons·PrimaryButton·CancelButton·PasswordInput·FocusOnLoad·TryCopy)과 `Show`·`AskSaveChanges`·`PasswordPrompt`를 옮긴다(OracleConnectionProfile → ConnectionProfile). 소유 창은 지금 View의 Window.

### 5.4 정보 대화상자
"Migration Studio 정보": 이름·버전(plugin.json)·플러그인 id·에이전트 경로·데이터 폴더(열기 링크 — `explorer.exe`). 

### 5.5 단계 요약 (`StepLogic`) — POC `stepInfo`
- 접속: 접속이 둘 다 없거나 하나라도 없으면 error. 둘 중 Error → error / 둘 다 Ok + 메타 둘 다 있음 → done / Testing → busy / 그 밖 "". 요약 `원본이름 → 대상이름`(없으면 "접속 없음").
- 테이블 매핑: 매핑 n개면 done, 요약 `n개 매핑(SQL k) · 사용 u` / "매핑 없음".
- 컬럼 매핑: 메타데이터(원본·대상)가 있을 때 `MappingService.Status` 합계로 POC와 같게, 메타가 없으면 state "" 요약 "—".
- 검증: "" · "아직 안 함". 실행: 끝나지 않은 체크포인트가 있으면 warn "재개 가능 · 68%", 없으면 "" "대기".

### 5.6 자리 화면 (`PlaceholderPage`)
PageFrame(STEP n, 제목, 설명 = 설계서 요약 한 줄) + EmptyState(해당 아이콘, "다음 단계에서 구현됩니다", "설계: docs/design-docs/UI-MIG-00x_Design.md") + 바닥 이전/다음.

## 6. DevHost — 설계자 검수 도구 (`tools/DevHost`)

```
DevHost.exe [--theme black|light] [--themes <Folderss\Themes 폴더>] [--data <임시 데이터 폴더>] [--seed]
            [--shot <out.png> --size 1100x700 --step 1..5 | --settings connections|defaults|agent]
```
- Folderss와 같은 팝업 창(PluginManager.ShowPluginWindow: 900×600 기본, 배경·글자·글꼴 키)을 만들고 플러그인 `CreateView()`를 넣는다. 테마는 `<themes>\Black.xaml`(또는 Light) + `Controls.xaml`을 `XamlReader`로 읽어 `Application.Resources`에 병합(기본 경로 `D:\git\cshap\Folderss\Folderss\Themes`).
- `FakePluginManager`: 설정은 `<data>\settings.json`(사전 JSON), DataDirectory = `<data>`, PluginDirectory = 플러그인 출력 폴더, `AddSettingsPage`는 목록에 보관.
- `--seed`: 설정에 예제 접속을 넣는다 — POC 예제 4개(비밀번호 없이) + `IT_LOCAL`(localhost:1521/xe, 사용자 `MIG_IT_SRC`, 초록) — 실제 Oracle 시연용. 임시 저장이 없으면 골든 `sampleJob`을 임시 저장으로 넣어도 된다.
- `--shot`: 창을 띄워 레이아웃이 끝난 뒤(`Dispatcher` Background 우선순위 두 번 + `UpdateLayout`) 창 내용(`Content`)을 `RenderTargetBitmap`(96 DPI)으로 PNG 저장하고 종료(코드 0). `--step n`이면 그 단계로 이동한 뒤, `--settings 탭`이면 설정 대화상자를 그 탭으로 띄워 그 창을 찍는다. 실패하면 stderr + 코드 1.
- 이 도구로 찍은 그림 8장을 `docs/dev/reports/P3-shots/`에 남겨라: black·light × (step1, step2 자리 화면, settings-connections, settings-defaults). 크기 1100x700, 그리고 접힌 단계 막대 1장(900x600, black step1).

## 7. 시험 (`tests/MigrationStudio.Tests`, `Logic/*` 링크)
- `StepLogic`: 각 단계 상태·요약(POC 문구), 접속 없음·오류·busy·done.
- `ConnectionLogic`: 콤보 항목 글자, 상태 줄 글자(시각·ms 형식), 메타 줄 글자(캐시 여부), 알림 종류 판정(없는 접속·쓰기 금지 대상·빨강 대상).
- `JobLogic`: NewJob 기본값, ResolveConnections(id 있음·이름으로 찾음(대소문자)·없음), ProfileFromMissing(이름 겹침 `_2`), 접속 고르기 → 스키마 기본값.
- `SettingsLogic`: 추가·복제(이름 `_COPY`, 새 id)·삭제 막기(작업이 쓰는 접속), 검사 실패 시 고를 접속.

## 8. 완료 기준

```powershell
dotnet build MigrationStudio.sln -c Release --nologo
dotnet test tests/MigrationStudio.Tests -c Release --nologo
dotnet publish src/MigrationStudio -c Release --nologo      # zip에 DevHost 관련 파일이 없어야 함
dotnet run --project tools/DevHost -c Release -- --seed --theme black --shot docs/dev/reports/P3-shots/black-step1.png --size 1100x700 --step 1
```
- 6장의 그림 9장(`docs/dev/reports/P3-shots/`)이 있어야 한다. 설계자는 그림과 POC를 나란히 놓고 검수한다.
- 실제 Oracle(`IT_LOCAL`)로 DevHost에서 접속 테스트·메타데이터 불러오기를 해 본 결과(성공 줄 글자)를 보고서에 적어라(시험 계정·비밀번호는 P2 준비물 상수. 계정이 없으면 `$env:ORACLE_IT_KEEP=1`로 OracleIT를 한 번 돌려 남겨 둔다).
- 보고서: `docs/dev/reports/P3-report.md`.
