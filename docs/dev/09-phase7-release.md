# 작업 지시서 P7 — 배포 · 문서 · 마무리 (작업id `P7`)

`docs/dev/00-common.md`를 먼저 읽어라. P1~P6c가 끝난 저장소에서 시작한다. 기능을 더하는 작업이 아니라 **내보낼 수 있는 상태**로 만드는 작업이다.

## 1. 범위

| 넣음 | 내용 |
|---|---|
| 로컬 배포 스크립트 | `scripts/pack.ps1` — DB Helper(`D:\git\cshap\Folderss-oracle-db-helper\scripts\pack.ps1`, 읽기만)를 이 저장소에 맞게 옮김: `src\MigrationStudio\plugin.json`의 id·version 읽기, `-Test`(단위 시험), `dotnet publish`, `release\<id>-<version>.zip`, **zip 내용 검증**(아래 3장) |
| CI | `.github/workflows/build.yml`을 최종본으로: 시험(`MigrationStudio.Tests`) → publish → zip → 아티팩트 → 태그면 릴리스(DB Helper와 같은 흐름). Oracle 시험은 CI에서 돌리지 않는다(`ORACLE_IT_DSN` 없으면 Skip) |
| README | 저장소 루트 `README.md` — 설치·기능·단축키·접속·안전장치·체크포인트·에이전트·문제 해결·개발(빌드·시험·DevHost·위임 문서 링크). DB Helper README의 구성·말투를 따른다 |
| 버전 | `plugin.json` `version`을 `1.0.0`으로, 에이전트 `InformationalVersion`이 같은 값(+해시)인지 확인(`Directory.Build.props` 한 곳에서 `Version` 관리 권장 — 설계 소유 csproj를 건드리므로 **바꾸지 말고 제안**, 이미 한 곳이면 확인만) |
| 정리 | 사용하지 않는 코드·`Placeholder.cs`·남은 TODO·죽은 옵션 제거, 경고 0, 문서 링크 깨짐 점검, `design/poc`와 `docs/design-docs`의 "구현 상태" 표(README 4.2 대응표에 실제 파일 경로 채움) |
| 전체 점검 | 종단 시험(4장) |

## 2. README 내용 (루트 `README.md`)
1. 한 줄 소개 + 스크린샷 4장(`docs/dev/reports/*-shots`에서 black 테마 그림을 `docs/images/`로 **복사**: 접속·테이블 매핑·SQL 원본 편집기·실행)
2. **설치**: Folderss `설정 > 플러그인 > GitHub에서 설치…`에 `https://github.com/zaruous/Folderss-oracle-data-migration` 또는 Releases zip → `플러그인 찾기…`. 요구 사항(.NET 8 Desktop Runtime — Folderss가 이미 필요로 함, Windows 10+, Oracle 11g+ 접속 가능)
3. **빠른 시작**: ① `⋯ > 플러그인 > Migration Studio` ② 마이그레이션 설정에서 원본·대상 접속 추가 ③ 접속 → 메타데이터 ④ 테이블 매핑(자동 매칭) ⑤ 컬럼 매핑(변환식) ⑥ 검증(F6) ⑦ Dry Run → 이관 → 실행 후 검증
4. **기능 표**(DB Helper README처럼 영역 | 내용): 접속·이관 전략 / 테이블·SQL 원본 매핑 / 컬럼 매핑·변환식·NULL 처리 / SQL 원본 편집기 / 검증(실행 전 C01~C13·실행 후 P01~P06) / 실행(Dry Run·일시정지·중지·재개·병렬 작업자) / 체크포인트(저장소 두 가지) / 작업 파일(JSON·YAML·템플릿) / 하위 프로세스 에이전트
5. **단축키**(README 7.4 표) · **안전장치**(README 7.3) · **접속 정보 저장**(DPAPI, DB Helper와 공유 안 함, 비밀번호 파일·로그·명령줄에 없음)
6. **체크포인트 저장소 선택 가이드**(README 6.5 표를 요약: 자동/대상 DB/로컬 파일, `MIG_` 제어 테이블이 만들어지는 위치와 권한)
7. **에이전트**: 왜 하위 프로세스인가·창을 닫아도 계속·다시 붙기·"Folderss를 닫을 때" 설정·파일 위치(`%LOCALAPPDATA%\Folderss\plugin-data\zaruous.folderss-oracle-migration\{agent,runs,logs,checkpoints,metadata,jobs}`)·실행 로그
8. **문제 해결**: ORA-12514·12541·01017·12170 해석, 에이전트가 시작 안 됨(보안 소프트웨어가 `MigrationAgent.exe` 차단 → 예외 등록), 체크포인트 저장소를 못 만듦(권한), 지원하지 않는 열 형식(LONG·XMLTYPE 등), 메타데이터가 느림(통계·권한)
9. **한계**: Oracle만 실행 가능(어댑터 경계는 있음), CDC 미지원, 구문 강조 없는 편집기(TextBox), 개인 PC 사용자 단위 DPAPI
10. **개발**: 빌드·시험(단위·OracleIT·docker `oracle-12c`)·DevHost·`docs/design-docs`·`docs/dev` 링크, 라이선스는 DB Helper와 같은지 확인(없으면 비워 둠)

## 3. `scripts/pack.ps1` 검증 항목 (실패하면 종료 코드 1)
zip 안에: `plugin.json`(id·version이 소스와 같음), `MigrationStudio.dll`, `MigrationStudio.Core.dll`, `MigrationStudio.deps.json`, `Oracle.ManagedDataAccess.dll`, `agent\MigrationAgent.exe`·`.dll`·`runtimeconfig.json`·`deps.json`·`MigrationStudio.Core.dll`·`Oracle.ManagedDataAccess.dll`, **없어야 함**: `*.pdb`, `Folderss.PluginContract.dll`, `MigrationStudio.Testing.dll`, `DevHost*`, `AgentCli*`, `xunit*`. 풀어서 `agent\MigrationAgent.exe --version` 실행(종료 0, 버전이 plugin.json과 같은 `1.0.0`으로 시작). zip 크기를 출력한다.

## 4. 종단 점검 (수동 + 스크립트, 보고서에 절차와 결과 기록)
1. 깨끗한 폴더에서 `scripts\pack.ps1 -Test` 성공
2. **실제 Folderss에 설치**(가능하면): `D:\git\cshap\Folderss`를 빌드·실행해(또는 설치된 Folderss) 플러그인 zip을 `플러그인 찾기…`로 등록 → 열기 → 설정 탭 → 접속 추가 → docker oracle-12c(`MIG_IT_SRC`→`MIG_IT_TGT`) 이관 → 창 닫기·다시 열기·다시 붙기 → 완료 → 실행 후 검증. 못 하면(UI 자동화 불가) 이유와 대신 한 DevHost 점검을 적는다. 이 단계는 **사람이 눈으로 확인할 항목 체크리스트**(`docs/dev/reports/P7-manual-checklist.md`)로 남겨라: 각 줄 `[ ] 동작 — 기대 결과`.
3. 로그·`runs\*.json`·`settings.json`·`jobs\current.draft.json`에 **비밀번호 평문이 없음**을 `Select-String`/`grep`으로 확인(시험용 비밀번호 문자열로 검색)
4. 에이전트 프로세스 명령줄에 비밀번호 없음(PowerShell `Get-CimInstance Win32_Process`)
5. 플러그인 DLL이 Folderss 프로세스에서 **다른 플러그인(DB Helper)과 같이 로드**돼도 충돌이 없는지(ODP.NET `OracleConfiguration` 전역 상태: DB Helper도 `DisableOOB = true`를 한다 — 두 플러그인이 각자 `PluginLoadContext`로 격리되니 문제없을 가능성이 높지만 **DB Helper와 Migration Studio를 같은 Folderss에서 동시에 열어 각자 접속**해 확인)
6. 큰 이관 한 번(`BIG_SRC` 200,000행 외에 가능하면 2,000,000행 — 시험 스키마에 임시로 만들고 지운다): 속도·메모리(에이전트 작업 집합 최대값)·UI 반응 기록

## 5. 완료 기준
```powershell
.\scripts\pack.ps1 -Test              # release\zaruous.folderss-oracle-migration-1.0.0.zip, 검증 통과
dotnet build MigrationStudio.sln -c Release --nologo --no-incremental     # 경고 0
dotnet test tests/MigrationStudio.Tests -c Release --nologo
$env:ORACLE_IT_DSN = "localhost:1521/xe"; dotnet test tests/MigrationStudio.OracleIT -c Release --nologo
```
보고서 `docs/dev/reports/P7-report.md`: 종단 점검 결과 표, 알려진 문제 목록(심각도·재현·제안), 릴리스 전 남은 일. **git commit·tag·push는 하지 않는다**(사용자가 정한다).
