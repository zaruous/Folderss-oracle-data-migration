# 위임 공통 규칙 — Migration Studio 구현 (모든 작업 지시서보다 먼저 읽어라)

저장소: `D:\git\cshap\Folderss-oracle-data-migration` (Folderss 플러그인 **Migration Studio** — Oracle → Oracle 데이터 이관).
역할: **Claude = 설계·검수**(아키텍처·API·화면 디자인·완료 기준을 정하고, 결과를 직접 다시 돌려 판정한다) / **Cursor = 기능 구현**(설계서대로 코드와 테스트를 쓴다).
설계서에 없는 결정이 필요하면 가장 보수적인 쪽을 고르고 보고서에 "설계 공백"으로 적어라. 설계를 바꾸고 싶으면 바꾸지 말고 보고서에 제안하라.

## 먼저 읽을 것

| 문서 | 내용 |
|---|---|
| `docs/design-docs/README.md` | 기능 설계 개요: 아키텍처(플러그인 · Core · 하위 프로세스 에이전트), 작업 파일 v2, 엔진, 체크포인트 |
| `docs/design-docs/UI-MIG-00x_Design.md` | 화면별 설계서(와이어프레임·버튼·서비스·SQL) |
| `design/poc/js/backend/*.js` | **기준 구현(POC)**. Core 이식은 이 동작과 같아야 한다 |
| `tests/MigrationStudio.Tests/Golden/poc-golden.json` | POC 출력으로 만든 기대값. 이식 결과는 이 값과 같아야 한다 |
| `D:\git\cshap\Folderss-oracle-db-helper` (읽기만) | 같은 저자의 Folderss 플러그인. 코드 스타일·WPF 도우미(Theme·ShellUi·ShellMenu)·Oracle 접속(Db/DbSession.cs)·테스트 구성의 본보기 |
| `contract/Folderss.PluginContract/PluginContracts.cs` | Folderss 플러그인 계약(IFolderssPlugin · IPluginManager · IPluginSettingsPage) |

## 솔루션 구성

```
MigrationStudio.sln
src/MigrationStudio/          플러그인(net8.0-windows, WPF) — plugin.json, 화면, 에이전트 클라이언트
src/MigrationStudio.Core/     화면과 무관한 로직(net8.0) — 모델·작업 파일·매핑·식/SQL 분석·SQL 생성·검증·엔진·Oracle 어댑터
src/MigrationAgent/           이관을 실행하는 하위 프로세스(net8.0 Exe, win-x64 apphost)
tests/MigrationStudio.Tests/  Core 단위 테스트(xunit, net8.0) — Linux CI에서도 돈다
contract/                     Folderss 계약 사본(바꾸지 마라)
```

## 코드 스타일 (DB Helper와 같게)

- 블록 네임스페이스(`namespace X { }`), 파일 범위 네임스페이스 금지. `record`·switch **식**·`init`·nullable 주석(`string?`) 쓰지 않는다(`<Nullable>disable</Nullable>`). `var`·람다·LINQ는 괜찮다.
- 중괄호는 다음 줄(Allman), 들여쓰기 4칸, private 필드 `_camelCase`, 상수 `PascalCase`.
- 주석·XML 문서·사용자에게 보이는 문자열은 **한국어**. 주석 어조는 DB Helper처럼 "무엇을 왜, 안 하면 무슨 일이" — 코드를 되풀이하는 주석은 쓰지 않는다.
- 문화권 영향을 받는 형식은 `CultureInfo.InvariantCulture`(숫자 천 단위는 `N0` + `en-US` = `48,200,000`). 문자열 비교는 서수(`StringComparison.Ordinal`) 또는 명시한 규칙.
- 새 NuGet 패키지는 작업 지시서에 적힌 것만. 그 밖이 필요하면 추가하지 말고 보고.
- 줄바꿈: 저장소 설정을 따른다(새 파일은 CRLF여도 LF여도 되지만 한 파일 안에서 섞지 마라). 파일 인코딩 UTF-8(BOM 없음).

## 설계 소유 파일 (공개 모양을 바꾸지 마라 — 멤버 추가는 되지만 보고서에 적는다)

- `src/MigrationStudio.Core/Model/*.cs`, `Metadata/*.cs`, `Types/OracleType.cs`
- 모든 `*.csproj`, `src/MigrationStudio/plugin.json`
- `tests/MigrationStudio.Tests/Golden/*` — **절대 고치지 마라.** 골든이 틀렸다고 보이면 그 항목과 이유를 보고서에 적어라(테스트는 그 항목만 `Skip = "<이유>"`로 두고 보고).
- `docs/**`, `design/**` — 읽기만(보고서 파일 하나만 쓴다).

## 금지

- git commit · push · 브랜치 조작 금지(작업 트리만 바꾼다).
- 실DB(oracle-12c 컨테이너 등)에 쓰는 동작은 작업 지시서가 허락할 때만.
- 테스트를 통과시키려고 테스트나 골든을 약하게 만들지 마라. 실패를 숨기지 말고 보고하라.

## 보고서 (필수)

`docs/dev/reports/<작업id>-report.md` 에:
1. 바꾼·만든 파일 목록(한 줄 설명)
2. 설계서와 다르게 한 것과 이유(설계 공백·제안 포함)
3. 골든과 다른 항목(있으면 각 항목 · 실제 값 · 이유)
4. 실행한 명령과 **실제 출력 수치**(빌드 경고·오류 수, 테스트 통과/실패/건너뜀 수, zip 파일 목록)
5. 못 한 것·막힌 것
검수자는 보고서의 명령을 그대로 다시 돌려 숫자를 맞춰 본다.
