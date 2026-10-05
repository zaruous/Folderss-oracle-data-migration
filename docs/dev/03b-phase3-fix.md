# 작업 지시서 P3b — P3 화면 보정 (작업id `P3b`)

`docs/dev/00-common.md`와 `docs/dev/03-phase3-shell.md`를 다시 읽어라. P3 결과는 빌드·시험은 통과했지만 **화면이 설계(3장 시각 설계·5장 화면)와 POC에서 크게 벗어나 있다.** 이 작업은 새 기능이 아니라 **P3 지시서대로 보이게 고치는 것**이다. "빌드 통과"가 완료가 아니다 — **그림이 기준 그림과 같아 보여야** 완료다.

## 기준 그림 (반드시 눈으로 비교하라 — 이미지를 열어 볼 수 있다)
`docs/dev/reference/`(설계자가 POC를 같은 1100×700에서 찍은 것):
`poc-black-step1.png` · `poc-light-step1.png` · `poc-black-step2.png`(테이블 매핑 — P4 참고용) · `poc-black-settings-connections.png` · `poc-black-settings-defaults.png`.
네 현재 결과: `docs/dev/reports/P3-shots/*.png`. **고친 뒤 같은 이름·같은 크기로 다시 찍어 `docs/dev/reports/P3b-shots/`에 두고, 기준 그림과 한 장씩 나란히 보며 차이를 목록으로 적어라**(보고서 표: 그림 / 남은 차이 / 이유). 설계자가 같은 방식으로 검수한다.

## 0. DevHost 시드 (그림 비교의 전제)
POC 그림은 **예제 작업이 로드된 상태**(작업 `CUSTOMER_MIGRATION *`, 원본 LEGACY_PROD → 대상 NEXT_PROD, 메타데이터 캐시 있음, 원본·대상 시험 안 함)다. 지금 DevHost `--seed`는 빈 작업(`NEW_MIGRATION`, 접속 없음)이라 비교가 안 된다.
- `--seed`가 (a) 설정에 POC 예제 접속 4개 + `IT_LOCAL`, **POC와 같은 id**(`cn-legacy-prod`·`cn-legacy-dev`·`cn-next-prod`·`cn-next-stg`)로 (b) 임시 저장으로 **골든 `sampleJob`**(`JobSamples`)을 넣고 (c) 골든 `source`·`target` 메타데이터를 `MetadataCache`로 저장하도록 확장. 그러면 시작 상태 = POC와 같다(단계 막대: 테이블 매핑 `5개 매핑(SQL 2) · 사용 4` 등은 P4 전이라 자리 화면이어도 요약은 `StepLogic` 규칙대로).
- 접속 비밀번호는 시드에 넣지 않는다(저장됨(DPAPI) 표시를 보이려면 `--seed`에서 `PasswordProtector`로 더미 값을 암호화해 `ProtectedPassword`만 채워도 됨 — 실제 접속은 안 쓰는 값).

## 1. 결함 목록 (P3 그림에서 확인한 것 — 전부 고쳐라, 번호로 보고서에 대응)

**화면 틀·머리·바닥**
1. `STEP 1` 꼬리표가 **글자 없이 빈 상자**로 나온다. 꼬리표(11px SemiBold 강조색 글자 + 강조 틴트 배경, 모서리 3, 안쪽 1·6)와 제목 `접속`이 같은 줄에.
2. 머리 오른쪽 버튼이 너무 크고(높이 ~60) **두 번째 버튼 글자가 잘린다**("두 접속 모두 테스트"). 버튼은 높이 28, 글자 13px, 둘이 가로로 나란히 + 간격 6, 폭은 내용만큼. 설명 글이 오른쪽 버튼과 겹치지 않게 머리 Grid(`1* | Auto`, 좁으면 아래로 줄바꿈).
3. **화면 바닥 줄이 없다**: `[‹ 이전 단계] … 안내 문구 … [다음: 테이블 매핑](주 버튼)`. 스크롤 영역 밖에 고정(P3 지시서 3.3).
4. 화면 안쪽 여백(16·20)과 최대 너비(1480) 적용, 가로 스크롤바 없음.

**단계 막대**
5. 위 "이관 작업" 머리: 작업 이름 + ` *` + 줄 아래 **DB 배지 → 화살표 → DB 배지**(지금은 글자 "접속 없음 → 접속 없음"). 단계 줄 요약도 `LEGACY_PROD → NEXT_P…`(말줄임). 라벨은 번호 없이 `접속`(번호는 원 안에).
6. 단계 번호 원 상태 모양(done ✓·error `!`·warn 숫자 + 주황·busy)이 기준 그림과 같게. 지금 단계는 원을 꽉 채우고 줄 배경 강조 틴트 + 왼쪽 2px 막대.
7. 아래 **링크 두 줄**(`작업 정의` · `매핑 템플릿`)이 없다.
8. 상태줄: 점 + DB 배지(색 칠한 배지: LEGACY_PROD 빨강 등) 모양을 기준 그림과 같게. 지금은 "접속 없음"만(시드로 해결되지만 배지 모양·간격도 확인).

**접속 카드 (가장 심각)**
9. 카드 머리가 `원본 DB SOURCE 원본`처럼 **글자가 뒤섞이고** DB 배지가 없다. 기준: 왼쪽 `SOURCE` 꼬리표 + `원본`, 오른쪽 **DB 배지 + `읽기 전용` 꼬리표**(원본만).
10. **카드 안쪽 폭 문제**: 오른쪽이 잘려 `접속 [↻]`처럼 버튼이 카드 밖으로 밀린다. 접속 ComboBox + [접속 관리…] 한 줄이 카드 폭 안에서 `1* | Auto`로.
11. **접속 요약 상자**(DB 종류·주소·사용자·비밀번호·색 표시 KeyValue, 테두리 상자)와 **알림**(운영 DB 경고·쓰기 금지·없는 접속)이 안 보인다 — 접속을 고르면 나와야 한다. 콤보 항목 글자 `LEGACY_PROD 10.10.10.21/LEGACY (쓰기 금지)`.
12. 스키마 입력: 값이 대문자 고정폭 SemiBold로(기준 그림), 필수 `*`와 힌트.
13. **카드 바닥**: [접속 테스트] [메타데이터 (다시) 불러오기] 버튼이 아이콘 + 글자, 줄바꿈 가능, 잘리지 않음. 그 아래 상태 줄 두 개(시험 상태 · `스키마 · 테이블 n · 뷰 n · 컬럼 n (캐시 · 시각)`).
14. 두 카드 사이 **화살표 원**(30×30, 강조 틴트, 강조색 화살표)이 카드 가운데 높이에. 지금은 흐릿하게 낮게 보임.

**이관 전략 카드**
15. 제목 `⚙ 이관 전략` + 부제 + 도구 [설정 기본값으로](ghost 작게, **카드 폭 전체로 늘어나지 않게**) — 지금은 헤더 아래에 가로로 꽉 찬 큰 버튼.
16. **Segmented 컨트롤이 읽히지 않는다**: 고른 항목은 `강조 틴트 배경 + 강조색 SemiBold 글자 + 안쪽 강조 테두리`, 나머지는 SecondaryText. 지금은 어두운 배경에 어두운 글자(black), 연한 배경에 연한 글자(light) — 두 테마 모두에서 대비를 확인하라(AccentBrush는 테마마다 다르므로 틴트는 Opacity 0.15 겹침, 글자는 AccentBrush).
17. 숫자 ComboBox 표기 `10,000 rows / commit`·`5,000 rows / fetch`(천 단위 쉼표 — `Format.Number`). `병렬 작업자` 힌트 `체크포인트 키 범위를 나눠 작업자마다 따로 읽고 씁니다(세션 8개)` — 지금 "(세션 n개)"로 **자리표시자가 그대로** 나온다.
18. **오류 처리 RadioCards** 3개가 보이지 않는다(테두리 없는 한 줄만). 기준: 각 항목 테두리 1·모서리 4·안쪽 5·8, 제목 + 설명(11.5px), 고른 항목은 강조 테두리 + 강조 틴트, "계속" 항목 오른쪽에 접두어 입력 칸(92px 고정폭).
19. FormGrid 2열 간격(가로 12·세로 10), 라벨 11.5px, 입력 높이 맞춤. 실행 방식 줄의 설명 힌트.

**마이그레이션 설정 (대화상자·Folderss 설정 탭 공통 View) — 거의 미구현 수준**
20. 레이아웃이 좌상단에 쪼그라들고 **오른쪽이 창 밖으로 잘린다**(폼 입력이 화면 폭을 넘음). 전체가 창 폭에 맞고(`ScrollViewer` 세로만), 입력은 칸 폭 안.
21. **탭**이 일반 버튼 3개다. 기준: 밑줄 탭(DB Helper `WorkspaceUi.SelectorButton`) `접속 5` · `기본값` · `실행 에이전트`, 고른 탭 아래 강조 밑줄 2px.
22. **접속 목록 카드**: 제목 `접속 5` + 도구 IconButton [+][⧉][🗑] — 지금 [+]만 있고 **글리프가 □로** 나온다(아이콘 글꼴 `Segoe Fluent Icons, Segoe MDL2 Assets`가 IconButton에 적용되지 않음 — 이 버그는 다른 IconButton(메뉴 아이콘은 정상)과 비교해 원인을 찾아 고쳐라). 목록 항목: 색 점 + 이름(고정폭 SemiBold) + 주소(11px) + 오른쪽 원본·대상 표시 + 쓰기 금지 꼬리표. 지금은 한 줄 글자가 잘리고 가로 스크롤바.
23. **접속 편집 폼 필드 누락**: 색 표시(ComboBox) · DB 종류(ComboBox) · 기본 스키마 · 비밀번호 칸 위치(필드 라벨 아래) · `저장` 체크가 비밀번호 칸 옆에 · `쓰기 금지 (원본 전용)` 체크를 `안전` 필드로 + 힌트. 접속 테스트 버튼은 폭 내용만큼(지금 전체 폭), 결과 줄(Pill ok `✓ Connected` + 버전 + ms / 오류 문장).
24. **기본값 탭이 2개 필드뿐**: 커밋 단위·Fetch 크기·병렬 작업자·오류 처리·오류 테이블 접두어 + **체크포인트 저장소 RadioCards 3개** + 제어 테이블 접두어(힌트 `MIG_RUN · MIG_RUN_TASK · MIG_CHECKPOINT (대상 스키마)`)를 `poc-black-settings-defaults.png`와 같게 전부.
25. **실행 에이전트 탭**: info 알림 + `Folderss를 닫을 때` RadioCards 2개 + 동시에 실행할 작업(Segmented)·로그 보관 + KeyValue(실행 파일·통신·실행 중) — 전부 구현(P3 지시서 5.2). 지금 미구현.
26. 대화상자 바닥 [취소] [저장](주 버튼) 오른쪽 정렬, 위에 설명 한 줄.

**메타데이터 속도 (P3 지시서 0장 "P2 후속"이 해결되지 않았다)**
27. 설계자가 sqlplus로 질의별로 쟀다(`MIG_IT_SRC`, oracle-12c): `USER_TABLES`+주석 0.30s · `USER_TAB_COLUMNS`+주석 0.65s · 제약+열 0.69s · **`+ LEFT JOIN ALL_CONSTRAINTS R`(FK 참조 테이블 구하기) 2.41s** · 통계 0.26s · 뷰 0.12s. 합계의 대부분이 `ALL_CONSTRAINTS` 조인이다. 해결: ① FK의 참조 테이블은 **조인 대신** 별도로 구한다 — 같은 스키마 안이면 `USER_CONSTRAINTS`에서 `R_CONSTRAINT_NAME`으로 메모리 매핑(이미 읽은 PK/UK 제약 이름 → 테이블), 다른 스키마를 가리키는 FK만 `ALL_CONSTRAINTS`에서 `(OWNER, CONSTRAINT_NAME) IN (…)` 소량 조회 ② 서로 독립인 질의(테이블·열·제약·통계·테이블스페이스)는 **연결 여러 개로 병렬** 실행(최대 4, 연결 풀링 끔 유지) 또는 최소한 한 연결에서 순차 유지하되 ①만이라도 ③ 질의별 소요를 `Stopwatch`로 재서 **어댑터에 `Action<string,long> Trace` 훅**(기본 null)을 두고 OracleIT가 출력(보고서에 질의별 ms 표). 목표: `MIG_IT_SRC` 전체 **1.5초 이하**(전체 합 4.4s → 병렬·조인 제거 후). 결과는 같아야 하고 P2 OracleIT 6개 통과.

## 2. 구현 지침 (이번에 반복하지 말 것)
- **먼저 `Kit`을 고쳐라.** 위 결함 대부분(꼬리표·버튼·Segmented·RadioCards·Notice·Card 머리/바닥·IconButton)은 `Kit` 부품이 설계(P3 지시서 3.2 표)대로 안 만들어진 것이다. 부품마다 DevHost에 **부품 전시 화면**(`--gallery` 옵션: Kit의 모든 부품을 한 창에 black/light로)을 만들어 찍고 확인한 뒤 화면에 쓴다.
- WPF 레이아웃: `StackPanel` 안에서 `WrapPanel`/가로 정렬 요소가 폭을 못 받으면 잘린다 — 카드 본문은 `Grid`(`1*`/`Auto`)로 폭을 제한하고 `TextWrapping`·`TextTrimming`을 쓰라. 입력 컨트롤은 `HorizontalAlignment=Stretch`지만 부모가 무한 폭이면 안 줄어든다는 점 주의.
- 두 테마 모두에서 찍어라(black·light). 글자 대비가 낮은 곳이 있으면 고쳐라(Folderss 테마 키만으로 안 되면 `Theme`의 틴트·의미 색).
- 크기 900×600(최소)과 1100×700에서 모두: 900×600에서는 단계 막대가 접히고(52) 접속 카드 두 개가 세로로 쌓이거나 폭이 줄어도 **잘리지 않아야** 한다.

## 3. 완료 기준
```powershell
dotnet build MigrationStudio.sln -c Release --nologo --no-incremental     # 경고 0
dotnet test tests/MigrationStudio.Tests -c Release --nologo                # 실패 0
$env:ORACLE_IT_DSN = "localhost:1521/xe"; dotnet test tests/MigrationStudio.OracleIT -c Release --nologo
dotnet run --project tools/DevHost -c Release -- --seed --theme black --shot docs/dev/reports/P3b-shots/black-step1.png --size 1100x700 --step 1
```
- `docs/dev/reports/P3b-shots/`: P3 지시서 6장의 그림 9장(black·light) + `black-step1-900x600`(접힌 막대) + Kit 전시 2장(`gallery-black`·`gallery-light`).
- 보고서 `docs/dev/reports/P3b-report.md`: 결함 번호 1~27 각각 `고침/부분/못 함` + 그림 대응, 기준 그림 대비 남은 차이 표, 메타데이터 질의별 ms(전·후).
- **스스로 눈으로 검수하라**: 그림을 열어 위 결함이 남아 있으면 끝내지 마라. 불가능한 항목만 이유를 적고 넘어간다.
