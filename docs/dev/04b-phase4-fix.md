# 작업 지시서 P4b — P4 화면 보정 + 자동 레이아웃 검사기 (작업id `P4b`)

`docs/dev/00-common.md`, `docs/dev/03-phase3-shell.md`(시각 설계 3장), `docs/dev/04-phase4-mapping.md`(4장 화면 설계)를 다시 읽어라.
P4 결과는 빌드·시험(244개)을 통과했지만 **화면이 설계와 크게 다르고 일부 그림은 엉뚱한 화면이다.** 이 작업은 새 기능이 아니라 **P4 지시서대로 보이게 고치는 것**이고, 이번에는 그림을 눈으로 못 보더라도 결함을 기계적으로 잡을 수 있게 **자동 레이아웃 검사기**를 먼저 만든다. 검사기가 0건이어야 완료다.

## 1. 자동 레이아웃 검사기 (먼저 만들어라 — 이것이 완료 기준의 중심)
DevHost에 `--layout-check` 옵션을 만든다(`--shot`과 같은 `--step`/`--open`/`--settings`/`--theme`/`--size`를 받되 PNG 대신 **검사 결과**를 stdout에 JSON 줄로, 문제가 하나라도 있으면 종료 코드 1). 레이아웃이 끝난 뒤(`UpdateLayout`) 대상 창(들)의 **시각 트리를 순회**해 다음을 찾는다:

| 코드 | 판정 |
|---|---|
| `ClippedRight` / `ClippedBottom` | 보이는 요소(`IsVisible`, 크기 > 0)의 경계 사각형이 **가장 가까운 자르는 조상**(`ScrollViewer`의 뷰포트, `ClipToBounds=true`인 요소, 창 콘텐츠)의 경계를 1px 넘어 벗어남. 단 그 조상이 해당 방향으로 스크롤 가능(`ScrollViewer`의 `HorizontalScrollBarVisibility`가 Disabled가 아님/세로는 Auto)이면 **세로 스크롤은 허용, 가로는 허용하지 않는다**(가로 스크롤바가 필요한 화면은 설계에 없다. 단 표의 `RowGrid` 내부 가로 스크롤은 `AllowHScroll` 표시된 것만 허용) |
| `TextClipped` | `TextBlock`/`ContentPresenter` 안 텍스트가 `TextTrimming = None`이고 `TextWrapping = NoWrap`인데 `ActualWidth < DesiredSize.Width`(글자가 잘림). `CharacterEllipsis` 말줄임은 허용 |
| `IconBox` | 글자가 Private Use 영역(U+E000–U+F8FF)을 포함하는 `TextBlock`의 실제 폰트(`FontFamily`)가 `Segoe Fluent Icons`/`Segoe MDL2 Assets`가 아님 — **□로 나오는 아이콘**(현재 카드 제목·표의 `→`·설정 목록 도구 등) |
| `EmptyChoice` | `ComboBox`(편집 불가)가 보이는데 `Items.Count == 0`이고 `Tag`가 `optional`이 아님(필수 선택칸이 비어 있음 — 매핑 추가 팝업) |
| `Overlap` | 같은 부모 `Grid`/`StackPanel`/`WrapPanel` 안의 형제 요소 둘의 경계가 **서로 5px 넘게 겹침**(제목과 Segmented가 겹치는 현재 증상). 의도된 겹침(`Panel.ZIndex`로 쌓는 배지·화살표 원)은 `Tag="overlay"`로 예외 |
| `ZeroSized` | 보이는 필수 컨테이너(`Card`·`RowGrid`·`PageFrame` 본문)가 높이 또는 너비 0 |
| `Unthemed` | 대화상자·창의 `Background`가 설정되지 않음(투명/기본 흰색) 또는 `Foreground`가 테마 키가 아님(`SetResourceReference` 아닌 고정 색은 `Theme`의 의미 색만 허용) |

- 검사는 **black·light × 1100×700·900×600** 모두에서, 화면별로: STEP 1~5(자리 화면 포함), 설정 대화상자 3탭, 매핑 추가 팝업(테이블·SQL), 이름으로 자동 매칭, 자동 매핑 확인, 삭제 확인, 템플릿 내보내기, SQL 원본 편집기(창) 4개 탭 + 빈 상태 + 오류 상태. 이것을 한 번에 도는 `scripts/layout-check.ps1`(`DevHost.exe`를 화면×테마×크기마다 실행, 요약 표 + 실패 목록, 종료 코드)를 만든다. **이 스크립트가 종료 코드 0이어야 완료.**
- 예외 목록(`Tag`)은 최소로, 보고서에 개수와 이유를 적어라. 검사기를 느슨하게 만들어 통과시키지 마라(설계자가 검사기 코드를 읽는다).
- 이 검사기 자체의 시험: 일부러 깨진 가짜 화면(`ClippedRight`·`IconBox` 등 하나씩)을 DevHost `--layout-selftest`로 만들어 검사기가 각각 잡는지.

## 2. 결함 목록 (설계자가 `P4-shots`와 기준 그림을 비교해 확인한 것 — 번호로 보고)
기준 그림(`docs/dev/reference/`): `poc-black-step2.png`(테이블 매핑) · `poc-black-columns.png`(컬럼 매핑, 전체 페이지) · `poc-black-sql-editor.png`(SQL 원본 편집기 — 검증 결과 탭) · `poc-black-add-mapping.png`(매핑 추가 팝업). 네 그림은 `docs/dev/reports/P4-shots/`.

**공통**
1. **아이콘이 □로 나온다**: 카드 제목 앞 아이콘(`스키마 탐색`·`설정`·`컬럼`·`생성 SQL`·`테이블 매핑`), 컬럼 표의 `→`, 매핑 그리드의 `→`, 단계 막대 `!`가 아닌 곳 전부. `Kit.Icon(glyph)` 한 곳에서 `Segoe Fluent Icons, Segoe MDL2 Assets`를 지정하고 모든 글리프 사용이 그것을 거치게 해라(P3b #22/P4 0a-5가 다시 새고 있다 — 근본 수정, 위 `IconBox` 검사 0).
2. **Segmented 선택 표시**: 고른 항목이 밝은 하늘색 배경 + 어두운 글자(black 테마에서 이질적)다. 설계: **강조색 15% 틴트 배경 + `AccentBrush` SemiBold 글자 + 안쪽 강조 테두리**. 두 테마 확인.
3. **카드 머리 겹침**: `컬럼 · 6/7 · 오류 0`과 Segmented, `생성 SQL`과 탭이 겹친다(제목 `1*`, 도구 `Auto` Grid로 — 폭이 모자라면 도구가 아래 줄로 감김).

**STEP 2 테이블 매핑**
4. **그리드가 잘려 있다**: `사용·원본·행 수`까지만 보이고 **대상 테이블·이관 방식·병합 키·컬럼 매핑·상태 열이 없다**. 설계 4.1의 9열 전부. 카드 폭이 모자라면 화면 폭 < 1560에서 **탐색 카드를 그리드 아래로** 내려 그리드가 전체 폭을 쓰게(설계 4.1 첫 문단, 지금은 두 카드가 옆에 놓여 둘 다 좁다).
5. 도구 버튼 줄이 잘린다(`+ 매핑 추가` 이후 [SQL 원본 추가][삭제][JSON …][찾기] 안 보임) — 도구는 `WrapPanel`로 줄바꿈.
6. 탐색 카드: `→ TB_M…` 링크 글자가 잘리고(`TextClipped`), `+매핑` 버튼이 작은 글자 링크처럼 보임 — 설계 4.1 모양(ghost 작은 버튼 `+ 매핑`).
7. **행 모양**: 기준 그림처럼 원본 칸 아래 보조 줄(주석/SQL이면 `FROM … · 결과 열 7`), 행 높이 32+, 체크 사용 열, `SQL` 꼬리표(보라) 정렬.

**STEP 3 컬럼 매핑**
8. **오른쪽 검사기 카드가 없다**(변환식 편집·함수 칩·원본 컬럼 칩·NULL 처리·검사 목록·샘플 6행)와 **생성 SQL 카드 본문이 없다**(제목만 겹쳐 보임). 설계 4.2 ①②③ 전부. 너비 ≥ 1100이면 `[표 1* | 검사기 320~360]`, 미만이면 세로.
9. 설정 카드: 이관 방식·**병합 키 칩**·체크포인트 컬럼·원본 조건 4칸이 한 줄 `auto-fit`(기준 그림). 지금 병합 키 칩이 없고 `원본 조건`의 힌트가 칸 폭을 못 넘어 줄바꿈이 깨짐. 카드 제목 없이 본문만(기준 그림).
10. 컬럼 표: 열 순서·폭 설계대로(원본 컬럼 셀 아래에 형식 11px, 대상 컬럼 아래 `형식 DEFAULT x`, `PK`/`NN` 꼬리표, 키 체크 열). 지금 **키 열·형식 줄·꼬리표가 없다**. 변환식 요약은 **연속 공백·줄바꿈을 한 칸으로**(`CASE     WHEN` 모양 수정).
11. 머리 오른쪽 매핑 고르기 콤보: `라벨   m/t · 오류` 형식과 최소 너비 300(기준).

**SQL 원본 편집기**
12. **그림이 엉뚱하다**: `sql-editor-check.png`·`sql-editor-error.png`가 **STEP 1 접속 화면**이다(편집기 창이 찍히지 않음). DevHost `--open sql-editor`는 **그 창(`Window`) 자체**를 `RenderTargetBitmap`으로 찍어야 한다(소유 창이 아님). 요구 그림: `sql-editor-check`·`sql-editor-preview`·`sql-editor-alias`·`sql-editor-merge`(생성 SQL)·`sql-editor-error`(오류 줄 칠함)·`sql-editor-empty`, black·light.
13. 설계 4.3 전부 구현 확인: 위 줄(SQL 원본 콤보·추가·삭제·안내), 편집기 카드(줄 번호 열·캐럿 줄/열·배너·[SQL 검증][100행 미리보기][Alias 자동 매핑]), 결과 탭 4개, 오른쪽 설정 카드·바인드 카드. P4 보고서가 "1차 축소 UI"라 밝힌 것(Alias 탭·100행 표·6행 샘플)을 **전부 완성**하라.

**팝업**
14. **매핑 추가 팝업이 미완성**: 창 배경·제목·테두리가 없고(투명한 흰 바탕), 원본·대상 ComboBox가 **비어 있고**, 폭이 창을 넘어 오른쪽이 잘리며 버튼(`+ 추가`)이 잘린다. `DialogKit.Create`(P3 `Dialogs`)로 만든 진짜 `Window`(제목줄 없는 모달, `Theme.ApplyWindow`, 폭 460, 안쪽 16, 아래 오른쪽 [취소][+ 추가])로. 기준 `poc-black-add-mapping.png`. 같은 방식으로 **이름으로 자동 매칭·자동 매핑 확인·삭제 확인·템플릿 내보내기** 팝업도 모두.
15. 팝업 그림 6장(black) + 2장(light)을 `P4b-shots/`에: `add-table`·`add-sql`·`auto-match`·`auto-map-confirm`·`delete-confirm`·`template-export`.

## 3. 실제 Oracle
P4 보고서에 OracleIT 12건 `ORA-01017`이 있다 — 다른 에이전트(Codex, P6a)가 같은 시간에 시험 사용자 `MIG_IT_*`를 만들고 지우는 동안 돌려서 생긴 충돌이다. **다시 돌려라**: 1분 기다린 뒤 `ORACLE_IT_KEEP=1`로(다른 쪽이 사용자를 지우지 않게), 그래도 `ORA-01017`이면 5분 간격으로 3번 재시도하고, 계속 실패하면 보고서에 적어라(픽스처 동작은 바꾸지 마라). 통과해야 완료. P4 어댑터 확장(Parse·Describe·Query·Count)이 실제 Oracle에서 설계(2.1)대로 도는지 **보고서에 시험별 결과 표**를 적어라.

## 4. 완료 기준
```powershell
dotnet build MigrationStudio.sln -c Release --nologo --no-incremental     # 경고 0
dotnet test tests/MigrationStudio.Tests -c Release --nologo                # 실패 0
$env:ORACLE_IT_DSN = "localhost:1521/xe"; $env:ORACLE_IT_KEEP = "1"; dotnet test tests/MigrationStudio.OracleIT -c Release --nologo   # 실패 0
.\scripts\layout-check.ps1                                                 # 종료 코드 0 (black·light × 1100×700·900×600 × 모든 화면)
```
- 그림: P4 지시서 6장 목록 + 위 12·15번 요구 전부 `docs/dev/reports/P4b-shots/`(black·light).
- 보고서 `docs/dev/reports/P4b-report.md`: 결함 번호 1~15 각각 `고침/부분/못 함` + 그림, 검사기 판정 표(화면×테마×크기), `Tag` 예외 목록, 기준 그림 대비 남은 차이.
- **자기 검수**: 그림 파일을 가능하면 직접 열어(이미지 보기 도구가 있으면) 확인하고, 없다면 검사기 JSON과 시각 트리 덤프(`--layout-check --dump`로 요소·경계·텍스트 출력)로 확인하라. 검사기를 통과해도 위 결함 번호의 설명과 다르면 고쳐라.
