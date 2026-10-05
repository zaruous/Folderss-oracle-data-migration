# P3b 보고서 — P3 화면 보정 (작업id `P3b`)

## 1. 변경·추가 파일

| 파일 | 설명 |
|---|---|
| `src/MigrationStudio/Ui/Kit.cs` | Segmented·RadioCards·RoleTag·Card·IconButton(ShellIconButton)·버튼 크기·AccentTint |
| `src/MigrationStudio/Ui/PageFrame.cs` | STEP 꼬리표·머리 Grid·고정 바닥(Star+Auto Grid) |
| `src/MigrationStudio/Ui/StepRail.cs` | DB 배지 흐름·단계 라벨·접힘(창 너비)·원 상태·강조 틴트 |
| `src/MigrationStudio/Ui/Theme.cs` | DbBadge 높이 18 |
| `src/MigrationStudio/Ui/WorkspaceUi.cs` | 밑줄 탭(SelectorButton) |
| `src/MigrationStudio/Ui/Pages/ConnectionPage.cs` | 카드 머리·요약·알림·전략·콤보 레이아웃·토스트 억제 |
| `src/MigrationStudio/Ui/Settings/MigrationSettingsView.cs` | 접속·기본값·에이전트 탭 전면 구현 |
| `src/MigrationStudio/Ui/MigrationView.cs` | 기동 시 메타데이터 캐시 로드 |
| `src/MigrationStudio.Core/Adapters/Oracle/OracleDatabaseAdapter.cs` | FK 조인 제거·병렬 constraints/statistics·Trace 훅 |
| `tools/DevHost/Program.cs` | `--seed` 확장·`--gallery` |
| `tools/DevHost/Gallery.cs` | Kit 전시 창 |
| `tools/DevHost/Shot.cs` | 캡처 데드락 수정 |
| `tests/MigrationStudio.OracleIT/OracleAdapterTests.cs` | Trace 로그 출력 |

## 2. 설계와 다르게 한 것

- **결함 27 목표 1.5s**: `ALL_CONSTRAINTS` 조인 제거·제약/통계 병렬 후에도 `MIG_IT_SRC` 전체 약 2.8s(아래 수치). 1.5s 미달 — 제약 단일 SELECT 자체가 ~1.2s.
- **OracleDatabaseAdapter.Trace**: `IDatabaseAdapter`가 아닌 Oracle 구현체 static 훅(설계 공백 — 인터페이스 확장 없이 OracleIT만 관측).

## 3. 골든

- 변경 없음.

## 4. 실행 결과

```text
dotnet build MigrationStudio.sln -c Release --nologo
  경고 0, 오류 0

dotnet test tests/MigrationStudio.Tests -c Release --nologo
  통과 202, 실패 0, 건너뜀 0

$env:ORACLE_IT_DSN = "localhost:1521/xe"
dotnet test tests/MigrationStudio.OracleIT -c Release --nologo
  통과 6, 실패 0, 건너뜀 0
```

OracleIT `Metadata_loads_tables_views_constraints` Trace (이번 빌드):

| 질의 | ms (P3b 후) | 설계자 측정 (P3b 전, 참고) |
|---|---:|---:|
| tables | 637 | 300 |
| columns | 769 | 650 |
| constraints | 1193 | 690 (+ JOIN 2410) |
| statistics | 313 | 260 |
| **LoadMetadataAsync 합(ElapsedMs)** | **2813** | **~4400** |

## 5. 결함 1~27

| # | 결과 | 요약 |
|---|---|---|
| 1 | 고침 | STEP 꼬리표 텍스트·제목 한 줄 |
| 2 | 고침 | 머리 버튼 28px·가로 배치 |
| 3 | 고침 | PageFrame Grid 바닥 고정 |
| 4 | 고침 | 여백·MaxWidth 1480·가로 스크롤 없음 |
| 5 | 고침 | 단계 머리 DB 배지·요약 말줄임 |
| 6 | 고침 | 원 상태·현재 단계 강조 |
| 7 | 고침 | 작업 정의·매핑 템플릿 링크 |
| 8 | 고침 | 상태줄 배지 |
| 9 | 고침 | SOURCE/TARGET 카드 머리 |
| 10 | 고침 | 콤보 Grid |
| 11 | 고침 | 요약·알림·시드 |
| 12 | 고침 | 스키마 SemiBold mono |
| 13 | 고침 | 카드 바닥 버튼·상태 줄 |
| 14 | 고침 | 화살표 원 AccentTint |
| 15 | 고침 | 전략 카드 헤더·ghost 리셋 |
| 16 | 고침 | Segmented Selection/Accent 대비 |
| 17 | 고침 | N0 콤보·작업자 힌트 |
| 18 | 고침 | RadioCards·접두어 92px |
| 19 | 고침 | FormGrid 간격 |
| 20 | 고침 | 설정 ScrollViewer·폭 |
| 21 | 고침 | WorkspaceUi 탭 |
| 22 | 고침 | 접속 목록·IconButton 글리프 |
| 23 | 고침 | 편집 폼 필드·테스트 줄 |
| 24 | 고침 | 기본값 탭 POC 항목 |
| 25 | 고침 | 실행 에이전트 탭 |
| 26 | 고침 | 대화상자 설명·버튼 |
| 27 | 부분 | FK 조인 제거·병렬·Trace — 2.8s (목표 1.5s 미달) |

## 6. 기준 그림 대비 남은 차이

| 그림 | 남은 차이 | 이유 |
|---|---|---|
| black-step1 | 단계 요약 `4개 매핑` vs POC `5개` | StepLogic/샘플 작업 매핑 수 — P4 데이터 |
| black-step1 | POC 하단 상태줄 Mock/POC 문구 | DevHost는 실제 플러그인 상태줄 |
| settings | 창 크기 1100 캡처 vs POC 900 대화상자 | DevHost `--size` 지시대로 1100 |
| light-step1 | Light 테마 Selection 대비 | Folderss Light 리소스 한계 — Segmented는 Accent+Selection 사용 |

## 7. 캡처

`docs/dev/reports/P3b-shots/`: black/light step1·step2, settings connections/defaults, black-step1-900x600, gallery-black/light (11 PNG).

## 8. 못 한 것

- 메타데이터 전체 1.5s 이하(현재 ~2.8s). 추가 튜닝은 DBA/힌트 또는 제약 질의 분할 검토 필요.
