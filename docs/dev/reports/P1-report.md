# P1 보고서 — 기반: 솔루션·패키징·Core 이식

## 1. 바꾼·만든 파일

| 경로 | 설명 |
|------|------|
| `MigrationStudio.sln` | 옛 MyPlugin 제거, MigrationStudio·Core·Agent·Tests·Contract 등록 |
| `.github/workflows/build.yml` | DB Helper와 동일 흐름: test → publish → win-x64 zip 아티팩트 |
| `.gitignore` | `release/` 추가 |
| `src/MigrationStudio.Core/Text/Format.cs` | POC `MS.fmt.n` → `Format.Number` |
| `src/MigrationStudio.Core/Engine/AgentProtocol.cs` | 프로토콜 버전 상수 |
| `src/MigrationStudio.Core/Expressions/*` | expression.js 이식(해석·형식 추정·캐시) |
| `src/MigrationStudio.Core/Mapping/*` | mapping.js 이식 |
| `src/MigrationStudio.Core/Settings/*` | settings.js 이식(제품 기본값에 예제 접속 없음) |
| `src/MigrationStudio.Core/Sql/*` | sql-mapping.js · sqlgen.js 이식 |
| `src/MigrationStudio.Core/Jobs/*` | job.js 이식(작업 파일·YAML·템플릿·SampleJob) |
| `src/MigrationStudio/MigrationPlugin.cs` | IFolderssPlugin 최소 WPF 진입점 |
| `src/MigrationAgent/Program.cs` | `--version` / 사용법 stderr |
| `tests/MigrationStudio.Tests/MigrationStudio.Tests.csproj` | Golden 복사 `CopyToOutputDirectory` |
| `tests/MigrationStudio.Tests/Golden/GoldenFixture.cs` 등 | 골든·단위 테스트 |

## 2. 설계서와 다르게 한 것

| 항목 | 이유 |
|------|------|
| `MigrationSettingsStore.Deserialize` 기본 접속 목록 비움 | §3.6·골든 `settingsDefaults` — 제품 기본 설정에 POC 예제 접속 4개를 넣지 않음 |
| `FindByName` 대소문자 무시 | §3.6 명시(POC는 이름 완전 일치) |
| `ExpressionTypeParse` | POC `expression.js`의 `parseType`/`isChar`/`isDate`(골든 `types[]`는 설계 `OracleType.Parse` 사용) |
| `ExpressionTypeParse`를 public | 골든 `types[]`가 POC `X.parseType` 기준이라 테스트에서 동일 API 필요 |
| `ResultColumn.Nullable`을 `bool?` | describe 골든의 `nullable: null` 표현 |
| `JobFile.Serialize` / `JobYaml` | YAML·null 소스·숫자 체크포인트가 골든 `yamlSample`/`sampleJob`과 맞도록 POC `toYaml`/`toPlain` 동작 보정 |

**설계 공백 제안:** `SchemaMetadata` 역직렬화 시 camelCase JSON 속성명을 테스트에서만 `JsonNamingPolicy.CamelCase`로 처리함. 런타임 Oracle 어댑터는 C# 속성 직접 설정 — 작업 파일·골든 JSON용 옵션을 Core에 `MetadataJsonOptions`로 두면 2단계 UI·픽스처가 단순해짐.

## 3. 골든과 다른 항목

없음 — `poc-golden.json` 전 키(§7) 검사 통과.

## 4. 실행한 명령과 실제 출력

### `dotnet build MigrationStudio.sln -c Release --nologo`

```
빌드했습니다.
    경고 0개
    오류 0개
경과 시간: 00:00:01.33
```

### `dotnet test tests/MigrationStudio.Tests -c Release --nologo`

```
통과!  - 실패:     0, 통과:   167, 건너뜀:     0, 전체:   167, 기간: 102 ms
```

### `dotnet publish src/MigrationStudio -c Release --nologo`

zip: `src/MigrationStudio/bin/Release/net8.0-windows/win-x64/MigrationStudio.zip`

`unzip -l` (9 entries, `*.pdb`·`Folderss.PluginContract.dll` 없음):

```
  Length      Date    Time    Name
---------  ---------- -----   ----
   136192  ...   MigrationStudio.Core.dll
      954  ...   MigrationStudio.deps.json
     4608  ...   MigrationStudio.dll
      327  ...   plugin.json
      904  ...   agent/MigrationAgent.deps.json
     4608  ...   agent/MigrationAgent.dll
   151552  ...   agent/MigrationAgent.exe
      340  ...   agent/MigrationAgent.runtimeconfig.json
   136192  ...   agent/MigrationStudio.Core.dll
---------                     -------
   435677                     9 files
```

zip 풀고 `agent\MigrationAgent.exe --version` (종료 코드 0):

```
MigrationAgent 1.0.0+f094280a15de6d3cb0daa7b1780ff77a30fe1bf5 (protocol 1)
```

## 5. 못 한 것·막힌 것

없음. P1 완료 기준(빌드·167 테스트·zip·에이전트 `--version`) 충족.
