# 작업 지시서 P1 — 기반: 솔루션·패키징·Core 이식 (작업id `P1`)

`docs/dev/00-common.md`를 먼저 읽어라. 이 작업은 **화면 없이** Core 로직을 C#으로 옮기고, 빌드·테스트·zip 패키징이 끝까지 도는 기반을 만든다.

## 0. 지금 상태 (설계자가 미리 만든 것)

- 프로젝트 파일 4개(`src/MigrationStudio`, `src/MigrationStudio.Core`, `src/MigrationAgent`, `tests/MigrationStudio.Tests`) — 설계 소유. 플러그인 csproj의 `PackPlugin`은 publish 때 플러그인 zip을 만들고 `agent\`에 에이전트를 publish해 넣도록 짜여 있다(동작 확인은 네 몫, 안 되면 고치고 보고).
- `MigrationStudio.sln`은 이름만 바꿨다 — **아직 옛 `src\MyPlugin\MyPlugin.csproj`를 가리킨다.** 고쳐라(`dotnet sln` 명령으로 옛 항목 제거, 4개 프로젝트 + contract 등록, 폴더 `src`·`tests`·`contract`).
- Core 설계 파일: `Model/MigrationJob.cs`(작업·접속 참조·전략·매핑·컬럼 매핑·바인드·체크포인트), `Model/Codes.cs`(SourceTypes·ExecutionModes·ErrorPolicies·WriteModes·NullRules·CheckLevels), `Metadata/SchemaMetadata.cs`, `Types/OracleType.cs`.
- 플러그인·에이전트에는 소스 파일이 없다(아래 5장에서 만든다).
- 골든 `tests/MigrationStudio.Tests/Golden/poc-golden.json`: POC(`design/poc/js/backend`) 출력. 키 설명은 7장.

## 1. 솔루션·빌드

- `dotnet build MigrationStudio.sln -c Release` 가 오류 0으로 돈다(경고 0 목표, 남으면 보고).
- `.github/workflows/build.yml`을 publish 방식으로 바꾼다 — DB Helper의 `.github/workflows/build.yml`과 같은 흐름: 플러그인 찾기 → (태그면 버전 확인) → `dotnet test tests/MigrationStudio.Tests -c Release` → `dotnet publish <플러그인 폴더> -c Release` → zip 경로 `<플러그인 폴더>/bin/Release/net8.0-windows/win-x64/*.zip` → 아티팩트·릴리스. 러너는 ubuntu-latest 그대로(WPF는 `EnableWindowsTargeting`으로 컴파일만).
- `.gitignore`에 `release/` 추가.

## 2. 패키징 — zip 내용 (완료 기준)

`dotnet publish src/MigrationStudio -c Release` → `src/MigrationStudio/bin/Release/net8.0-windows/win-x64/MigrationStudio.zip`:

| 있어야 함 | 없어야 함 |
|---|---|
| `plugin.json`, `MigrationStudio.dll`, `MigrationStudio.deps.json`, `MigrationStudio.Core.dll` | `Folderss.PluginContract.dll`(본체 것을 씀) |
| `agent/MigrationAgent.exe`, `agent/MigrationAgent.dll`, `agent/MigrationAgent.runtimeconfig.json`, `agent/MigrationAgent.deps.json`, `agent/MigrationStudio.Core.dll` | `*.pdb` |

zip을 임시 폴더에 풀고 `agent\MigrationAgent.exe --version`이 종료 코드 0으로 버전을 찍는지 확인해 보고서에 출력을 붙여라.

## 3. Core 공개 API 설계

POC의 함수와 1:1로 대응한다. **동작·메시지 문구·SQL 문자열은 POC와 같아야 한다**(골든으로 검사). POC의 `MS.fmt.n`은 `N0`(en-US, 반올림) — C# 도우미 `Text.Format.Number(decimal)`로 둔다.

### 3.1 `MigrationStudio.Core.Expressions` ← `expression.js` (해석·형식 추정만, 평가 `evaluate`/`run`은 옮기지 않는다 — 미리보기는 Oracle이 한다)

```csharp
public sealed class SqlParseException : Exception      // Message = Code + ": " + 설명 (POC OraError와 같은 문구)
{ public string Code { get; } public int Position { get; } }   // Position: 원문 기준 0부터, 모르면 -1

public sealed class ExpressionNode                     // POC AST 노드(k)와 같은 종류: lit, bind, col, now, neg, not, isnull, like, in, between, case, cast, fn, bin
{
    public string Kind; public object Value; public bool IsString; public string Name; public string Op; public string TypeName;
    public bool Negated; public bool Timestamp; public int Position;
    public ExpressionNode Left, Right, Operand, Pattern, Low, High, Subject, Else;
    public List<ExpressionNode> Args;  public List<ExpressionNode> Items;  public List<CaseWhen> Whens;   // (필드든 속성이든 모양은 네가 정해도 된다)
}
public sealed class CaseWhen { public ExpressionNode Condition; public ExpressionNode Value; }

public sealed class ExpressionAnalysis
{ public ExpressionNode Node { get; } public IReadOnlyList<string> Refs { get; } public IReadOnlyList<string> Binds { get; } public SqlParseException Error { get; } }

public static class ExpressionAnalyzer
{
    public static ExpressionAnalysis Analyze(string expression);   // POC compile(): 결과 캐시(스레드 안전, 500개 넘으면 비움), 오류도 캐시
    public static IEnumerable<ExpressionNode> Walk(ExpressionNode node);
}
public static class TypeInference
{
    public static string Infer(ExpressionNode node, Func<string, string> columnType);   // POC inferType
    public static string Merge(IEnumerable<string> types);                              // POC mergeTypes
}
```

- 토큰 정규식은 POC와 같게 **ASCII 기준**(`\w`를 `[A-Za-z0-9_]`로; .NET의 `\w`는 유니코드라 결과가 달라진다). 식별자는 대문자로, 따옴표 식별자는 그대로.
- 알 수 없는 함수(`FOO(1)`)는 해석 오류가 아니다(POC와 같음).

### 3.2 `MigrationStudio.Core.Sql` ← `sql-mapping.js`

```csharp
public sealed class SqlError { public string Code; public string Message; public int? Line; }     // POC {code, msg, line}
public sealed class SelectItem { public string Text; public string Expr; public string Alias; public string Name; public bool Star;
                                 public int Position; public ExpressionNode Node; public IReadOnlyList<string> Refs; public SqlParseException Error; }
public sealed class TableRef { public string Schema; public string Name; public string Alias; public string Join; /* null·INNER·LEFT·CROSS */ public string On; public bool Subquery; }
public sealed class WhereClause { public string Text; public ExpressionNode Node; public SqlParseException Error; }
public sealed class SelectStatement { public List<SqlError> Errors; public List<string> Warnings; public List<SelectItem> Items; public List<TableRef> Tables;
                                      public List<string> Binds; public WhereClause Where; public string OrderBy; public bool GroupBy; }

public static class SqlText
{
    public static string StripComments(string sql);          // 주석을 같은 길이 공백으로(줄 번호 유지)
    public static int LineOf(string sql, int position);
}
public static class SelectParser { public static SelectStatement Parse(string sql); }            // POC parseSelect

public sealed class ResultColumn { public string Name; public string Type; public string Expr; public string Alias; public bool Nullable;
                                   public ColumnStats Stats; public ColumnMetadata Source; public string Error; }
public sealed class DescribeResult { public List<SqlError> Errors; public List<ResultColumn> Columns; }

public sealed class SqlSourceInfo                           // POC virtualSource 결과
{ public TableMetadata Table; /* Kind="SQL", Rows=추정, AvgRowLength=110, Comment="FROM …" */ public long? BaseRows; public SqlError Error; public SelectStatement Statement; }

public sealed class CheckItem { public string Check; public string Level; public string Detail; }   // 검증 결과 한 줄(검증 화면·SQL 편집기 공용)
public sealed class SqlValidationResult { public string Level; public List<CheckItem> Items; public SelectStatement Statement;
                                          public List<ResultColumn> Columns; public int Mapped; public int Total; public int? ErrorLine; }

public static class SqlSourceAnalyzer
{
    public static DescribeResult Describe(SelectStatement statement, SchemaMetadata source);       // POC describe(메타데이터 기반 DESCRIBE)
    public static SqlSourceInfo Analyze(Mapping sqlMapping, SchemaMetadata source);               // POC virtualSource (캐시는 해도 되고 안 해도 된다)
    public static long? EstimateRows(Mapping sqlMapping, SelectStatement statement, DescribeResult describe);
    public static BindParameter CheckpointBind(Mapping mapping, SelectStatement statement);
    public static SqlValidationResult Validate(Mapping sqlMapping, SchemaMetadata source, SchemaMetadata target);   // POC validate
}
```

### 3.3 `MigrationStudio.Core.Mapping` ← `mapping.js`

```csharp
public sealed class TableMatch { public string Source; public string Target; public string Reason; }
public sealed class AutoMappedColumn { public ColumnMapping Mapping; public string Reason; }
public sealed class CompatResult { public string Level; public string Kind; /* truncate·precision·null */ public string Message; }
public sealed class CheckMessage { public string Level; public string Message; }
public sealed class ColumnCheck { public string Level; public List<CheckMessage> Messages; public string Type; }
public sealed class SourceValueInfo { public string Type; public ColumnStats Stats; public long? Nulls; public IReadOnlyList<string> Refs; public bool Nullable; public SqlParseException Error; }
public sealed class ColumnResult { public ColumnMetadata Target; public ColumnMapping Mapping; public ColumnCheck Check; }
public sealed class MappingStatus { public int Mapped; public int Total; public string Level; public int Errors; public int Warns; public List<ColumnResult> Results; }

public static class NameDictionary   // POC TABLE_DICT · COLUMN_DICT · SUFFIX_RULES (읽기 전용 사전)
{ public static IReadOnlyDictionary<string, string> Tables { get; } public static IReadOnlyDictionary<string, string> Columns { get; }
  public static IReadOnlyList<KeyValuePair<string, string>> SuffixRules { get; } public static string NormalizeTable(string name); }

public static class MappingService
{
    public static List<TableMatch> AutoMatchTables(IEnumerable<TableMetadata> source, IEnumerable<TableMetadata> target, IEnumerable<Mapping> existing);
    public static TableMatch SuggestTable(string sourceName, IEnumerable<TableMetadata> target, IEnumerable<Mapping> existing);
    public static List<AutoMappedColumn> AutoMapColumns(IList<ColumnMetadata> source, IList<ColumnMetadata> target);
    public static string DefaultNullRule(ColumnMetadata target);
    public static string SuggestExpression(ColumnMetadata source, ColumnMetadata target);
    public static CompatResult Compat(string sourceType, string targetType, ColumnStats stats);
    public static string ValueSource(ColumnMapping mapping);                                    // 식이 있으면 식, 없으면 원본 컬럼
    public static SourceValueInfo SourceInfo(ColumnMapping mapping, IList<ColumnMetadata> sourceColumns);
    public static ColumnCheck CheckColumn(ColumnMapping mapping, ColumnMetadata target, IList<ColumnMetadata> sourceColumns, string mode);
    public static MappingStatus Status(Mapping mapping, TableMetadata source, TableMetadata target);   // POC mappingStatus — source가 null이면 ERROR
}
```

### 3.4 `MigrationStudio.Core.Sql.SqlGenerator` ← `sqlgen.js`

```csharp
public sealed class WriteColumn { public string Name; public string Expr; public ColumnMetadata Column; }
public sealed class SourceSelectOptions { public int FetchSize = 5000; public int Workers = 1; }

public static class SqlGenerator
{
    public static string Literal(string value, string type);
    public static string ValueExpression(ColumnMapping mapping, ColumnMetadata target);           // NULL 처리 반영, 값이 없으면 null
    public static List<WriteColumn> WriteColumns(Mapping mapping, TableMetadata target);
    public static string BuildSourceSelect(Mapping mapping, string sourceSchema, TableMetadata source, TableMetadata target, SourceSelectOptions options);  // SQL 원본은 인라인 뷰 S
    public static string BuildWriteSql(string targetSchema, string targetTable, IList<string> columns, string mode, IList<string> keys, string errorTable);
    public static string ErrorTableFor(MigrationStrategy strategy, string targetTable);
}
```
줄바꿈은 `\n`(POC와 같게). 화면·엔진에서 Oracle에 보낼 때 그대로 쓴다.

### 3.5 `MigrationStudio.Core.Jobs` ← `job.js`

```csharp
public static class JobJson { public static JsonSerializerOptions Options { get; } }   // camelCase, 들여쓰기 2칸, null은 쓰지 않음, 한글 그대로(UnsafeRelaxedJsonEscaping)

public sealed class JobFileException : Exception { }      // "작업 파일이 아닙니다(format: …)" · "더 새 버전(n)의 작업 파일입니다" · JSON 오류(위치 포함)

public static class JobFile
{
    public static MigrationJob Parse(string json);                                    // POC parseJob: 형식·버전 확인, v1 → v2 변환, 빠진 값 기본값, 비밀번호 필드는 버림
    public static string Serialize(MigrationJob job, MigrationSettings settings);     // POC toJson: 접속 사본을 settings의 접속에서 채움(비밀번호 없음). settings가 null이면 job의 값 그대로
    public static string ToYaml(MigrationJob job, MigrationSettings settings);        // POC yamlOf (첫 줄 주석 포함, 문자열 따옴표 규칙 같게)
}
public sealed class TemplateApplyResult { public int Added; public int Replaced; }
public static class MappingTemplate
{
    public const string Format = "folderss-migration-mapping";
    public static string Export(MigrationJob job, string name);
    public static TemplateApplyResult Apply(MigrationJob job, string json);           // 같은 원본 종류·원본·대상이면 바꿈(id 유지), 아니면 추가. v1 템플릿도 읽음
}
```

- 체크포인트 `value`는 JSON에서 숫자(850000)나 문자열('O2025…') 둘 다 읽어 `string`으로 둔다(쓰기는 문자열). 접속 사본의 `port`도 숫자·문자열 둘 다 읽는다.
- 매핑 `id`가 없으면 `Mapping.NewId()`.

### 3.6 `MigrationStudio.Core.Settings` ← `settings.js`

```csharp
public sealed class ConnectionProfile
{ public string Id; public string Name; public string Kind = "oracle"; public string Color = ""; public string Host; public int Port = 1521; public string Service;
  public string User; public string ProtectedPassword; /* DPAPI Base64 — 암호화는 플러그인(Windows)이 한다 */ public bool SavePassword = true;
  public string DefaultSchema; public bool WriteBlocked; }
public sealed class MigrationDefaults { public int CommitSize = 10000; public int FetchSize = 5000; public int Workers = 4; public string ErrorPolicy = "CONTINUE";
                                        public string ErrorTable = "ERR$_"; public string CheckpointStore = "AUTO"; public string ControlPrefix = "MIG_"; }
public sealed class AgentSettings { public string OnHostExit = "CONTINUE"; public int MaxConcurrent = 1; public int LogDays = 30; }
public sealed class MigrationSettings { public int Version = 1; public List<ConnectionProfile> Connections; public MigrationDefaults Defaults; public AgentSettings Agent; }

public static class CheckpointStores { public const string Auto = "AUTO", Target = "TARGET", Local = "LOCAL"; }
public static class MigrationSettingsStore
{
    public const string SettingKey = "migration-settings";
    public static MigrationSettings Deserialize(string json);          // null·빈 문자열 → 기본값(접속 없음). 버전이 다르면 기본값 + 접속만 살림
    public static string Serialize(MigrationSettings settings);
    public static ConnectionProfile Find(MigrationSettings s, string id);
    public static ConnectionProfile FindByName(MigrationSettings s, string name);      // 대소문자 무시
    public static ConnectionProfile NewProfile(MigrationSettings s);                   // NEW_CONNECTION_n
    public static List<string> ValidateProfile(ConnectionProfile p, IEnumerable<ConnectionProfile> all);   // POC validateProfile 문구
    public static void ApplyDefaults(MigrationStrategy strategy, MigrationDefaults defaults);
}
```
(위 필드 표기는 모양을 보이려는 것 — 실제로는 `{ get; set; }` 속성으로.) 제품 기본 설정에는 **예제 접속을 넣지 않는다**(골든 `settingsDefaults`의 접속 4개는 POC 예제다).

### 3.7 `MigrationStudio.Core.Engine.AgentProtocol`
`public static class AgentProtocol { public const int Version = 1; }` — 5단계에서 채운다(지금은 상수만).

## 4. 이식 시 주의 (POC → C#)

- POC 정규식 중 `\w`, `\s`, `\d`는 ASCII 의미다. .NET에서 같게 하려면 문자 클래스를 직접 쓰거나 `RegexOptions.ECMAScript`를 쓴다(단 ECMAScript 옵션은 일부 구문 제한이 있다).
- POC의 `toLocaleString('en-US')` 숫자 → `N0` en-US. `Math.round` → `MidpointRounding.AwayFromZero`.
- POC가 `undefined`/`null`을 섞어 쓰는 곳은 C#에서 `null`로 통일. 골든 JSON에서 없는 키 = null.
- POC `compat`의 `kind`가 없으면 `null`. `checkColumn`의 `type`은 원본 값 형식(없으면 null).

## 5. 플러그인·에이전트 최소 진입점

- `src/MigrationStudio/MigrationPlugin.cs`: `public sealed class MigrationPlugin : IFolderssPlugin` — `Initialize`에서 manager 저장, `CreateView`는 매번 새 요소: 가운데에 "Migration Studio" 제목과 "구현 중 — 다음 단계에서 화면이 들어갑니다" 안내(글자색은 테마 키 `PrimaryText`·`SecondaryText`를 `SetResourceReference`로). 화면 설계는 2단계에서 준다.
- `src/MigrationAgent/Program.cs`: `--version` → `MigrationAgent <InformationalVersion> (protocol <AgentProtocol.Version>)` 출력, 종료 0 / 인수 없음·모르는 인수 → 사용법을 stderr로, 종료 2. 예외는 잡아서 stderr + 종료 1.

## 6. 테스트

`tests/MigrationStudio.Tests`:
- `Golden/poc-golden.json`을 출력 폴더로 복사(`<None Update="Golden\**" CopyToOutputDirectory="PreserveNewest" />` — csproj는 설계 소유지만 이 한 줄은 추가해도 된다).
- **골든 테스트**(`GoldenTests.cs`): 7장의 모든 키를 검사한다. 항목마다 `[Theory]`/`MemberData`로 케이스 단위 실패가 보이게.
- 골든 밖 단위 테스트: OracleType 경계, ExpressionAnalyzer 캐시·스레드 안전(병렬 호출), JobFile 왕복(Serialize → Parse 같음), JSON 숫자·문자열 체크포인트 값, MappingTemplate.Apply 바꿈/추가, Settings 역직렬화(빈 문자열·버전 다름), SqlText.StripComments 줄 번호 유지.

## 7. 골든 키 → 검사할 C# API

| 키 | 입력 → 기대 |
|---|---|
| `source`·`target` | 테스트 픽스처로 쓰는 `SchemaMetadata` 두 개(그대로 역직렬화) |
| `types[]` | `OracleType.Parse(input)` → base·length·precision·scale·isChar·isDate |
| `compat[]` | `MappingService.Compat(source, target, stats)` → result.level·kind·msg |
| `expressions[]` | `ExpressionAnalyzer.Analyze(expr)` → error.code·message·position, refs, binds / `TypeInference.Infer(node, t)` → type. t = SRC_CUSTOMER·SRC_ORDER 열 형식(점 앞 별칭은 떼고 찾음) |
| `autoMatchTables` | 예제 작업(`sampleJob`)의 테이블 원본 매핑을 existing으로 `AutoMatchTables(source.tables, target.tables, …)` |
| `autoMapColumns[]` | `AutoMapColumns(원본 테이블 열, 대상 테이블 열)` → target·source·expr·nullRule·defaultValue·reason |
| `sampleJob` | `JobFile.Parse`로 읽히는 v2 작업(테스트 픽스처) |
| `mappingStatus[]` | 예제 작업의 테이블 원본 매핑마다 `MappingService.Status` → mapped·total·level·errors·warns·columns[].level·type·msgs |
| `parseSelect[]` | `SelectParser.Parse(sql)` → errors(code·msg·line)·warnings·items(name·alias·expr·star·error 코드)·tables·binds·where·orderBy·groupBy |
| `describe[]` | `SqlSourceAnalyzer.Describe(Parse(sql), source)` (sql은 parseSelect의 같은 key) → errors·columns |
| `virtualSource` | 예제 작업의 SQL 원본 매핑 `Analyze` → name·rows·baseRows·comment·columns |
| `sqlValidate[]` | `Validate`(예제 SQL 원본을 key대로 바꾼 매핑 — 생성 방법은 `design/poc/tools/make-golden.js`와 같게: ordered=ORDER BY 추가, noCheckpointBind=binds 비우고 WHERE 제거, missingBindValue, broken=missingParen SQL, noTarget) → level·mapped·total·errorLine·items |
| `sqlgen.*` | `BuildSourceSelect`·`WriteColumns`·`BuildWriteSql`(4방식 × 키·오류 테이블 유무)·`Literal`·`ValueExpression`·`ErrorTableFor` 문자열이 **완전히 같음** |
| `jobV1` → `jobV1Upgraded` | `JobFile.Parse(jobV1)` → 버전 2, 접속·매핑·체크포인트 |
| `yamlSample` | `JobFile.ToYaml(sampleJob, 골든 settingsDefaults로 만든 설정)` 텍스트가 같음 |
| `settingsDefaults` | 모양 참고용(기본값 defaults·agent 값만 비교, 접속 목록은 비교하지 않음) |

골든 생성기는 `design/poc/tools/make-golden.js`(읽기만 — 각 키의 입력을 어떻게 만들었는지 여기서 확인하라. `node design/poc/tools/make-golden.js`로 다시 만들면 같은 파일이 나온다). 테스트 입력은 이 스크립트가 만든 방식과 똑같이 C#에서 만든다.

## 8. 완료 기준 (검수자가 그대로 다시 돌린다)

```powershell
dotnet build MigrationStudio.sln -c Release --nologo          # 오류 0 (경고 수 보고)
dotnet test tests/MigrationStudio.Tests -c Release --nologo   # 실패 0, 골든 7장 키 전부 검사
dotnet publish src/MigrationStudio -c Release --nologo        # 2장 zip 내용
```
보고서: `docs/dev/reports/P1-report.md` (00-common의 양식).
