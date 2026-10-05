# 작업 지시서 P4 — 테이블 매핑 · 컬럼 매핑 · SQL 원본 편집기 · 템플릿 (작업id `P4`)

`docs/dev/00-common.md`를 먼저 읽어라. P1(Core)·P2(어댑터)·P3(셸·접속 화면)이 끝난 저장소에서 시작한다.
기준: POC(`design/poc` — `pages/tables.js`·`columns.js`·`sql-editor.js`를 직접 눌러 보며 읽어라), 설계서 `UI-MIG-002`·`003`·`004`·`007`(템플릿), README 4.4(변환식은 원본 SELECT 안에서 Oracle이 계산).
P3의 `Kit`·`Theme`·`PageFrame`·`StudioState`·`Dialogs`·`DevHost`를 그대로 쓴다. **새 화면 부품이 필요하면 `Kit`에 더하고 보고서에 적어라.**

## 0a. 선행 보정 — P3b 검수에서 남은 결함 (**화면 작업보다 먼저** 고쳐라)
`docs/dev/reference/`의 기준 그림과 `docs/dev/reports/P3b-shots/`의 네 그림을 열어 비교했다. P3b는 대부분 고쳐졌으나 아래가 남았다. 고친 뒤 같은 이름으로 `docs/dev/reports/P4-shots/fix-*.png`에 다시 찍어라(black·light).
1. **접속 카드 바닥 버튼이 없다**: [접속 테스트] [메타데이터 (다시) 불러오기]가 카드에 안 보인다(기준 `poc-black-step1.png` 카드 아래쪽). 아이콘 + 글자, 줄바꿈 가능.
2. **상태 줄 포맷 버그**: `컬럼 33|cache|00:00:00`처럼 **포맷 문자열 조각이 그대로** 노출된다. 기준: `▦ LEGACY_APP · 테이블 6 · 뷰 1 · 컬럼 33 (캐시 · 09:12:44)`(table 아이콘, 괄호 안은 DisabledText). 시험 상태 줄(`● 이번 창에서 아직 시험하지 않음`)과 위아래 순서도 기준과 같게. `ConnectionLogic` 단위 시험을 이 문자열 그대로 비교하게 고쳐라.
3. **접속 요약 상자**: `색 표시` 행이 상자 **밖**으로 나와 `스키마` 라벨과 겹친다. 5행(DB 종류·주소·사용자·비밀번호·색 표시) 모두 테두리 상자 안에, 값(주소·사용자)은 고정폭 SemiBold(기준 그림).
4. **운영 DB 경고 알림 위치**: 대상 카드에서 접속 선택 줄 **아래**, 요약 상자 **위**(기준 그림) — 지금은 선택 줄에 붙어 간격이 없다. 알림과 앞뒤 요소 간격 12.
5. **설정 목록 도구 아이콘이 □**(+ ⧉ 🗑): P3b #22가 안 고쳐졌다. 메뉴 아이콘 막대의 `ShellIconButton`(글리프가 정상 표시됨)과 **같은 스타일·같은 글꼴 지정**을 쓰라(`FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"`를 컨트롤 템플릿 안 TextBlock에 직접). 원인을 보고서에 한 줄로.
6. **설정 화면 오른쪽이 잘린다**: 색 표시·기본 스키마·포트·사용자 입력이 창 오른쪽 끝에서 **잘려 나간다**(`black-settings-connections.png`). 폼 Grid 오른쪽 열이 뷰 폭을 넘는다 — 전체가 `ScrollViewer`(세로만) 안에서 뷰 폭에 맞게. 접속 목록 항목의 `쓰기 금지` 꼬리표도 잘림(항목 Grid `1* | Auto`, 이름·주소 말줄임). 목록 하단 가로 스크롤바 제거.
7. **접속 테스트 결과 줄**: 설정 접속 탭의 [접속 테스트] 옆/아래 결과(Pill `✓ Connected` + 버전 + ms / 오류 문장)가 안 보인다. 시험을 눌러 본 상태의 그림도 한 장(`fix-settings-test-ok`, `--fake-oracle`가 아직 없으니 P1~P3 어댑터를 가짜로 바꾸는 최소 `FakeAdapter`를 이 작업에서 먼저 만들어 DevHost `--fake-oracle`로).
8. **단계 막대 하단 링크**: `작업 정의`(page 아이콘)·`매핑 템플릿`(exportFile) 두 줄이 없다(P3 #7). 매핑 템플릿은 P4에서 켠다.
9. **DevHost 그림의 오른쪽 흰 띠(~16px)**: 창 내용(Content) 크기와 캡처 크기가 맞지 않아 생긴다. `--size 1100x700`이면 **콘텐츠 영역이 정확히 1100×700**(창 테두리 제외)으로 찍히게 `RenderTargetBitmap` 대상 크기를 맞춰라. 가로 스크롤 영역에 겹친 세로 스크롤바 때문에 본문이 좁아 보이지 않게(오버레이 스크롤바 아니면 폭에서 빼기).
10. 이관 전략 카드 이하(실행 방식 Segmented 등)가 첫 화면에서 잘려 보이는 것은 스크롤 때문이므로 문제 아님 — 다만 **1100×700 첫 화면**에서 접속 카드 두 개가 한눈에 들어오도록(기준 그림은 카드 높이 ≈ 500) 카드 안쪽 간격을 기준대로 맞춰라.
(메타데이터 로딩은 2.8s로 목표 1.5s 미달이나 보류 — 수용. 단 P4의 `QueryAsync` 등을 만들 때 같은 `Trace` 훅을 따르라.)

## 0. 설계자가 미리 해 둔 것
- (없음 — 이 작업이 시작될 때 P3 결과 위에 얹는다. 설계 소유 파일을 고쳐야 하면 보고서에 제안만 하라.)

## 1. 범위

| 넣음 | 빼고 나중에 |
|---|---|
| Core: 어댑터 확장(PARSE·DESCRIBE·질의·건수), `SqlProbe`(질의 문자열 만들기), `SqlSourceService` | 실측 프로파일(`profile-expression`)·검증 엔진·검증 화면(P5) |
| STEP 2 테이블 매핑 화면 + 팝업(매핑 추가 · 이름으로 자동 매칭 · 삭제 확인) | 실행·진행(P6) |
| STEP 3 컬럼 매핑 화면(설정 줄 · 컬럼 표 · 검사기 · 생성 SQL) | |
| SQL 원본 편집기(비모달 창) | |
| 매핑 템플릿 가져오기·내보내기(메뉴·단계 막대 링크·테이블 매핑 [JSON …]) | |
| 단계 막대의 테이블·컬럼 매핑 요약, 메뉴·아이콘 켜기(매핑 메뉴·`Ctrl+Q`) | |

## 2. Core 확장 (`MigrationStudio.Core.Adapters`, `MigrationStudio.Core.Sql`)

### 2.1 어댑터에 더할 것 (`IDatabaseAdapter`에 메서드 추가 — 기존 계약은 그대로)

```csharp
public sealed class QueryColumn { public string Name; public string Type; public bool Nullable; }      // Type = P2의 열 형식 문자열 규칙과 같은 표기
public sealed class SqlParseResult { public bool Ok; public string ErrorCode; public string Message; public int? Line; public int? Position; }
public sealed class QueryResult
{
    public List<QueryColumn> Columns; public List<string[]> Rows;   // null = NULL, 값은 문자열(날짜 'yyyy-MM-dd HH:mm:ss', 숫자 InvariantCulture)
    public long ElapsedMs; public bool HasMore;                     // maxRows+1행을 읽어 판단
}

Task<SqlParseResult> ParseSqlAsync(ConnectionTarget target, string schema, string sql, CancellationToken ct);
Task<List<QueryColumn>> DescribeSqlAsync(ConnectionTarget target, string schema, string sql, CancellationToken ct);
Task<QueryResult> QueryAsync(ConnectionTarget target, string schema, string sql, IList<SqlBind> binds, int maxRows, CancellationToken ct);
Task<long?> CountAsync(ConnectionTarget target, string schema, string sql, IList<SqlBind> binds, CancellationToken ct);   // 실패(권한·시간 초과)면 null이 아니라 AdapterException
```
`SqlBind { Name; Type("NUMBER"|"VARCHAR2"|"DATE"); Value }` — `BindParameter`에서 만든다(`SqlBind.From(BindParameter)`). 값이 비면 NULL, NUMBER는 InvariantCulture 파싱(실패 시 AdapterException "바인드 :LAST_ID 값이 숫자가 아닙니다"), DATE는 `yyyy-MM-dd[ HH:mm:ss]`.

**Oracle 구현 규칙**
- 원본 쪽 호출이다: 연결 직후 `ALTER SESSION SET CURRENT_SCHEMA = <schema>`(스키마는 `[A-Z0-9_$#]+`로 검증, 아니면 거부 — 문자열 이어 붙이기 방지)와 `SET TRANSACTION READ ONLY`. 두 문장은 P2의 연결 도우미에 한 곳으로 모아라.
- **SELECT 전용 보호**: 주석을 뺀 첫 단어가 `SELECT`·`WITH`가 아니면, 또는 문장 중간에 `;`가 있으면(끝의 `;`는 지움) **연결하기 전에** `AdapterException("SELECT 또는 WITH로 시작하는 문장만 쓸 수 있습니다")`. (`DBMS_SQL.PARSE`는 DDL을 바로 실행하므로 이 보호가 필수 — 시험으로 확인할 것.)
- `ParseSqlAsync`: UI-MIG-004 SQL-1의 익명 블록(`DBMS_SQL.OPEN_CURSOR`/`PARSE`/`CLOSE_CURSOR`), 오류 위치는 `DBMS_SQL.LAST_ERROR_POSITION` → 줄 번호(`SqlText.LineOf`). 사용자 SQL은 **바인드 변수 `:SQL_TEXT`** 로 넘긴다. 오류는 예외가 아니라 `Ok = false`(ErrorCode "ORA-00942", Message, Line). 연결 실패 등은 AdapterException.
- `DescribeSqlAsync`: UI-MIG-004 SQL-2 `SELECT * FROM (<사용자 SQL>) WHERE 1 = 0`를 `CommandBehavior.SchemaOnly`로 열어 `GetSchemaTable()`. **바인드 변수가 있어도 값 없이 동작해야 한다** — SchemaOnly는 실행하지 않으므로 `:이름`마다 `OracleParameter`를 NULL(VARCHAR2)로 달아 준다(`BindByName = true`). 형식 문자열은 P2의 열 형식 규칙(`internal static`)을 쓴다 — 중복 구현 금지. 이름이 없는 식 열(예: `COUNT(*)`)은 Oracle이 주는 이름(`COUNT(*)`)을 그대로 쓴다.
- `QueryAsync`: `maxRows`+1행까지 읽고 넘으면 `HasMore`. 값 문자열 변환은 P2의 값 형식 도우미가 없으면 새로 `Adapters/Oracle/ValueText.cs`(DATE·TIMESTAMP·NUMBER·CLOB 앞 4,000자·RAW 16진·NULL). 명령 `CommandTimeout`은 120초, 취소는 `Cancel()`. `InitialLOBFetchSize = 4000`.
- `CountAsync`: `SELECT COUNT(*) FROM (<사용자 SQL>)`(UI-MIG-004 SQL-4 — 안쪽 ORDER BY는 그대로 둬도 Oracle이 받는다). 시간 제한 60초.
- 모든 호출 `ConnectionTarget` + 비밀번호는 P2와 같은 규칙(로그·메시지에 연결 문자열 금지).

### 2.2 `SqlProbe` (정적, 질의 문자열만 만든다 — Oracle 없이 단위 시험)

UI-MIG-003 SQL-1·SQL-2와 UI-MIG-004 SQL-3의 문장을 만든다. 줄바꿈 `\n`, 들여쓰기 4칸.

```csharp
public static class SqlProbe
{
    /// 식 하나의 결과 형식·구문 검사(FUNC03): SELECT <식> AS V FROM <스키마>.<테이블> WHERE 1 = 0   (SQL 원본이면 FROM (<사용자 SQL>) S WHERE 1 = 0)
    public static string ExpressionCheck(Mapping mapping, string sourceSchema, string valueExpression);
    /// 샘플 미리보기(FUNC04): 원본 열 값과 결과를 함께 6행. 체크포인트 컬럼이 있고 :LAST_ID가 있으면 WHERE col > :LAST_ID ORDER BY col, 없으면 ORDER BY 없이.
    /// FETCH FIRST n ROWS ONLY (Oracle 12c+). 열 별칭 SRC_1.., RESULT.
    public static string SamplePreview(Mapping mapping, string sourceSchema, IList<string> sourceExpressions, string resultExpression, int rows);
    /// SQL 원본 100행 미리보기(UI-MIG-004 SQL-3): SELECT * FROM (<SQL>) S [ORDER BY S.<체크포인트>] FETCH FIRST n ROWS ONLY
    public static string SqlSourcePreview(Mapping sqlMapping, int rows);
    public static string SqlSourceCount(Mapping sqlMapping);
    /// 테이블 원본 정확한 행 수(UI-MIG-002 FUNC06): SELECT /*+ PARALLEL(4) */ COUNT(*) FROM <스키마>.<테이블>
    public static string TableCount(string schema, string table);
}
```
- 식별자는 `SqlGenerator`가 쓰는 규칙(따옴표 없이 대문자)과 같다. 스키마·테이블 이름에 `[A-Z0-9_$#]` 밖의 문자가 있으면 `ArgumentException`.
- 체크포인트 조건의 바인드 이름은 매핑의 `binds` 중 `FromCheckpoint = true`인 첫 번째(없으면 `LAST_ID`).
- 테이블 원본의 `WHERE`(사용자 조건)는 `SqlGenerator.BuildSourceSelect`와 같은 방식으로 AND로 붙인다(그 함수의 해당 부분을 `internal`로 떼어 재사용).

### 2.3 `SqlSourceService` (`Core.Sql`, 어댑터를 받아 오케스트레이션 — 화면이 이것만 부른다)

```csharp
public sealed class SqlSourceService
{
    public SqlSourceService(IDatabaseAdapter adapter);
    /// 구문 → DESCRIBE → P1 SqlSourceAnalyzer.Validate(실제 결과 열을 넣은 판) 순서. 구문 오류면 거기서 멈추고 결과 level ERROR(ORA 코드·줄 번호, 편집기가 줄을 칠함).
    public Task<SqlValidationResult> ValidateAsync(Mapping sqlMapping, ConnectionTarget sourceTarget, string sourceSchema, SchemaMetadata sourceMeta, SchemaMetadata targetMeta, CancellationToken ct);
    public Task<QueryResult> PreviewAsync(Mapping sqlMapping, ConnectionTarget sourceTarget, string sourceSchema, int rows, CancellationToken ct);
    public Task<long?> EstimateRowsAsync(...);   // 비싸면 생략: 주 테이블 통계 추정(P1 EstimateRows)을 먼저 보이고, 사용자가 [정확히 세기]를 누르면 CountAsync
    /// 결과 열을 가상 테이블로: P1 SqlSourceAnalyzer.Analyze(mapping, sourceMeta) 결과의 Columns를 DESCRIBE 결과(이름·형식·Nullable)로 바꾼 TableMetadata(Kind="SQL").
    public static TableMetadata VirtualTable(Mapping sqlMapping, IList<QueryColumn> described, SchemaMetadata sourceMeta);
}
```
- **P1의 `SqlSourceAnalyzer`에 오버로드 추가 허용**(기존 시그니처는 그대로, 골든 통과 유지): `Validate(mapping, source, target, IList<QueryColumn> described)`와 `Analyze(…, described)` — described가 null이면 기존 메타데이터 기반 추정. 실제 DESCRIBE가 있으면 열 형식·Nullable은 그 값을 쓰고 통계(`Stats`)는 메타데이터에서 찾은 같은 이름 열의 것을 그대로 잇는다(모르면 빈 통계).
- 실제 Oracle이 알려 주는 오류가 POC의 추정 오류보다 우선한다(ORA-00942 등은 Oracle 문구 그대로).

### 2.4 시험
- **단위(Oracle 없이)**: `SqlProbe`의 모든 문장 문자열(테이블·SQL 원본 × 체크포인트 유무 × 사용자 WHERE 유무), SELECT 전용 보호(`DROP TABLE x`, `SELECT 1; DROP …`, `-- 주석 뒤 DELETE`, 대소문자·앞 공백·줄바꿈 변형, 정상 `WITH`·끝의 `;`), 스키마 이름 검증, `SqlBind.From`, `SqlSourceService.VirtualTable`, 오버로드가 골든을 깨지 않음(기존 167개 통과).
- **실제 Oracle(OracleIT, P2 준비물 `MIG_IT_SRC` 재사용)**: Parse 성공/ORA-00942(없는 테이블)/ORA-00904(없는 열)/줄 번호가 둘째 줄 이상, **`DROP TABLE`을 넣어도 연결 전에 거부되고 테이블이 남아 있음**, Describe(열 이름·형식 문자열이 표대로·`COUNT(*)` 이름·바인드가 있는 SQL), Query(maxRows·HasMore·NULL·날짜 문자열), Count, 취소, `SET TRANSACTION READ ONLY` 아래에서 INSERT가 들어간 문장은 거부.

## 3. 상태 (`StudioState` 확장)
- `Ui`: `SelMapping`(id), `SelColumn`(대상 컬럼 이름), `ColFilter`("all"·"unmapped"·"issues"), `SqlTab`("check"·"preview"·"alias"·"merge"), `ColSqlTab`("select"·"write"), `MappingFilter`(문자열). 작업 파일에는 저장하지 않는다(창 안에서만, `StudioState`가 보관).
- `Session`: `SqlCheck[mappingId]` = { Result(SqlValidationResult), At, Sql }, `SqlPreview[mappingId]` = { Result(QueryResult), At, Sql, Error }, `SqlDescribe[mappingId]` = { Columns(List<QueryColumn>), Sql }.
- `SourceOf(mapping)`: TABLE → 원본 메타데이터의 테이블(없으면 null) / SQL → `SqlSourceAnalyzer.Analyze(mapping, sourceMeta, describe?.Columns)` — 결과가 열이 없으면 "SQL 검증 필요"로 취급. `TargetTable(name)`, `Mapping(id)`, `SqlMappings()`는 POC `store.js`와 같은 이름·동작.
- 서비스 호출은 P3 `ConnectionService`와 같은 방식(비밀번호 구하기·취소 토큰·창이 닫히면 취소). 같은 매핑의 같은 종류 호출이 진행 중이면 무시. 실패는 화면의 알림 줄에 문장으로(예외를 던지지 마라).

## 4. 화면 설계

공통: 모든 입력은 값이 바뀌어도 **그 입력 칸을 다시 만들지 않는다**(커서·포커스 유지). 표 행의 셀 편집은 `StudioState.MarkChanged()` 뒤 **표 전체를 다시 그리지 말고** 그 행·단계 막대·상태줄만 갱신한다(컬럼 수백 개에서도 입력이 끊기지 않게 — 갱신 비용이 큰 곳은 `Dispatcher.BeginInvoke(DispatcherPriority.Background)`로 미룸).

**표 구현 방식(필수)**: Folderss는 DataGrid 스타일이 없다. 셀 안에 ComboBox·CheckBox가 있는 표는 `ListView/GridView`의 CellTemplate 대신 **자체 행 컨트롤**로 만든다 — 머리 줄 `Grid` + 행마다 `Grid`(같은 `ColumnDefinition` 너비: 컨테이너에 `Grid.IsSharedSizeScope = true`, 열마다 `SharedSizeGroup`) 를 `ItemsControl`/`StackPanel`에 쌓는다. 행은 `Border`(아래 테두리 1, 높이 최소 32, 마우스 올리면 RowHoverBrush, 고른 행 SelectionBrush 연하게 + 왼쪽 2px 강조 막대). 행 수 ≤ 500이면 가상화 없이 둔다(테이블 매핑·컬럼 매핑 모두 해당). 이 표 부품을 `Ui/RowGrid.cs`로 만들어 두 화면과 편집기의 Alias 표가 같이 쓴다: `RowGrid(columns[])`, `AddHeader()`, `AddRow(FrameworkElement[] cells, object tag, Action onSelect, Action onDoubleClick)`, `SelectRow(tag)`, `Clear()`.

셀용 컴팩트 컨트롤: `Kit.CellComboBox(options, value, onChange)`(높이 24, 안쪽 4·2, 테두리 연하게 — 평소엔 테두리 없는 듯 보이다 마우스·포커스에서 BorderBrush), `Kit.CellTextBox`. 키보드: 행에서 ↑↓ 행 이동, Enter = (테이블 매핑) 컬럼 매핑으로, Tab으로 셀 이동.

### 4.1 STEP 2 테이블 매핑 (`Pages/TablesPage.cs`) — UI-MIG-002, POC `pages/tables.js`
- 머리: [✦ 이름으로 자동 매칭](magic). 바닥 안내 "고른 매핑: <라벨>". 메타데이터가 없으면(원본·대상 중 하나라도) 본문 대신 EmptyState(table, "메타데이터 없음", "접속 화면에서 원본·대상 메타데이터를 불러오세요.", [접속으로])와 메뉴·버튼 동작은 "메타데이터를 먼저 불러오세요" 경고 토스트.
- 본문: 너비 ≥ 1560 이면 `[매핑 그리드 카드 | 스키마 탐색 카드(너비 360)]` 2열, 미만이면 세로로(그리드 아래 탐색, 탐색 안은 원본·대상 목록 2열).
- **매핑 그리드 카드**: 제목 swap 아이콘 "테이블 매핑", 부제 `n개(SQL 원본 k) · 사용 중 원본 N행`, 도구 [+ 매핑 추가] [{} SQL 원본 추가] [🗑 삭제] | [JSON 가져오기] [JSON 내보내기](ghost) [찾기 입력 160px]. 열(POC와 같은 순서·너비): 사용(체크, 32) · 원본(테이블·SQL, 1.4*) · 행 수(숫자 우측, 96) · →(24) · 대상 테이블(셀 ComboBox, 150) · 이관 방식(셀 ComboBox, 150) · 병합 키(셀 ComboBox, 120) · 컬럼 매핑(`m/t` + 진행 막대 + [편집 ›], 130) · 상태(배지 + 아래 "오류 n"/"경고 n", 80).
  - 원본 셀: 테이블 = 이름(고정폭) + 아래 주석(11px DisabledText) / SQL = `SQL` 꼬리표(보라, `Theme.SqlTag` = 어두운·밝은 배경 모두에서 읽히는 고정 중간 톤 `#A67CD6`, 테두리·글자 같은 색) + 이름 링크(클릭 → SQL 원본 편집기에서 열기) + 아래 한 줄(`FROM … · 결과 열 n` 또는 오류 문장 위험색, 말줄임·툴팁 전체).
  - 행 수: SQL이면 `~` 접두 + 툴팁 "추정(주 테이블 통계 − 체크포인트 조건)", 테이블이면 통계 값, 없으면 `—`. 통계가 7일 넘으면 11px 경고색 "통계 n일 전"(메타데이터에 `LastAnalyzed`가 없으면 생략 — **P2 메타데이터에 `LastAnalyzed`가 없으니 이 표시는 이번엔 하지 않는다**).
  - 대상 셀 아래: `기존 n행`/`비어 있음`(11px). 방식 셀 아래: 파괴적 방식 + 대상 접속 색 빨강이면 경고색 "운영 DB · 되돌릴 수 없음". 병합 키 셀: 키가 필요한 방식일 때만 ComboBox(대상 열, PK 표시), 아래 `← 원본열`; 아니면 `—`.
  - 상태 판정: POC 그대로(원본 없음·SQL 오류 = ERROR, PASS·INFO = OK).
  - 행 클릭 = 고름, 더블클릭·Enter·[편집 ›] = `Ui.SelMapping` 설정 후 STEP 3. 사용 해제 행은 글자 Opacity 0.5.
  - 대상 바꾸기: 컬럼을 새 대상으로 `AutoMapColumns` 다시, 병합 키 = 대상 PK, 토스트 "대상을 바꿔 컬럼을 다시 자동 매핑했습니다". 방식 바꾸기: 키가 필요하고 비었으면 대상 PK로.
  - 빈 목록: EmptyState(swap, "매핑이 없습니다", "이름이 비슷한 테이블을 자동으로 맞추거나, 테이블 또는 SQL 원본을 직접 추가하세요.") + 버튼 3개([이름으로 자동 매칭] 주, [매핑 추가], [SQL 원본 추가]).
  - 바닥(카드 foot) 안내문: POC 문구.
- **스키마 탐색 카드**: 제목 list 아이콘, 부제 `매핑 안 된 원본 테이블 n개`/`원본 테이블을 모두 매핑함`, 도구 [{} SQL 원본 추가](ghost 작게). 본문: 원본 목록 | 대상 목록 — 각 줄 = 종류 꼬리표(`TBL`/`VIEW`, 10px) + 이름(고정폭 12px, 말줄임) + (원본) 매핑 있으면 링크 `→ 대상, SQL 이름…`(11.5px, 클릭 → 그 매핑 고르기) 없고 TABLE이면 [+ 매핑](ghost 작게, 그 원본으로 팝업) + 행 수 짧게(`1.24M`·`48K` — `Format.Short`, P1에 없으면 `Text/Format.cs`에 추가). 목록은 각각 최대 높이 360 스크롤. SQL 원본이 읽는 테이블도 `→ SQL 이름`으로 표시(P1 `SelectStatement.Tables`).
- **팝업 ① 매핑 추가**(`Dialogs/AddMappingDialog.cs`, 460폭): 원본 종류 Segmented(테이블 | SQL (SELECT 문)) → 테이블: 원본 ComboBox(그룹 "매핑 안 됨"·"이미 매핑함(다른 대상에 또)" — WPF `CollectionViewSource` 그룹 대신 비활성 머리 항목으로 흉내) / SQL: SQL 원본 이름(기본 `SQLMAP_n`, 고정폭) + 시작 SQL ComboBox(`빈 SELECT 문` + 각 테이블 `…의 열로 시작`) + info 알림. 공통: 대상 테이블 ComboBox(`이름  (n행)`), 추천 글(`추천: TB_PRODUCT — 이름 규칙`, 11.5px), 이관 방식(대상에 행이 있으면 기본 MERGE). 힌트 "대상에 행이 있으면 INSERT + UPDATE(MERGE)를 권합니다". [취소] [+ 추가]. 동작·검증 문구는 POC `addDialog`와 같게(같은 매핑이 있으면 "같은 매핑이 이미 있습니다", SQL 이름 겹침 "같은 이름의 SQL 원본이 있습니다"). SQL 추가 뒤 편집기를 연다.
  - 만들기는 Core로: `MappingFactory`(`Core/Mapping/MappingFactory.cs`, POC `createMapping`·`createSqlMapping`·`starterSql`을 옮김, **골든 밖 단위 시험 필수**): `CreateTableMapping(sourceMeta, targetMeta, source, target, mode)`, `CreateSqlMapping(name, target, mode, startTable, sourceMeta, targetMeta)`, `StarterSql(table)`(POC와 같은 문자열). `Mapping.NewId()` 사용, SQL 이름은 대문자·`[^A-Z0-9_$#]`→`_`.
- **팝업 ② 이름으로 자동 매칭**(`AutoMatchDialog`, 620폭): 안내 한 줄 + 표(체크 · 원본 → 대상 · 근거). [취소] [+ 추가] → `MappingFactory.CreateTableMapping` 여러 개, 토스트 "테이블 매핑 n개 추가 · 컬럼 자동 매핑함". 후보가 없으면 토스트 "더 맞출 테이블이 없습니다(…)".
- **삭제**: 확인 대화상자(위험 버튼) → 매핑·`Checkpoints[id]`·세션의 SqlCheck/SqlPreview 지움.
- 사용 합계 `사용 중 원본 N행`은 `SourceOf(m).Rows` 합.

### 4.2 STEP 3 컬럼 매핑 (`Pages/ColumnsPage.cs`) — UI-MIG-003, POC `pages/columns.js`
- 머리: 제목 아래 설명 줄이 매핑마다 다르다 — 테이블 원본: `스키마.테이블 (n행, m열)  →  스키마.대상 (k열, 기존 n행)` / SQL 원본: `[SQL] 이름(링크) (결과 n열, ~N행, FROM …)  →  …`. 오른쪽 동작: 매핑 고르기 ComboBox(최소 300, `라벨   m/t · 오류`).
- 매핑이 없으면 EmptyState(list, "매핑이 없습니다", "먼저 원본(테이블 또는 SQL)과 대상 테이블을 이어 주세요.", [테이블 매핑으로]). SQL 원본인데 결과 열을 모르면 EmptyState(code, "SQL 원본의 결과 열을 알 수 없습니다", Oracle 오류 문장 + " — SQL 원본 편집기에서 SELECT 문을 고치고 검증하세요.", [SQL 원본 편집기]). 메타데이터에 원본·대상 테이블이 없으면 EmptyState(warn …, [접속으로]).
- 본문 위→아래: ① **설정 카드**(`auto-fit` 4칸, 너비 180 미만이면 줄바꿈): 이관 방식 ComboBox / 병합 키(칩: 이름 + × — 키가 필요 없는 방식이면 "이 방식에는 필요 없음", 필요한데 비면 위험색 "표의 "키"에서 고르세요") / 체크포인트 컬럼 ComboBox(후보 = 원본 NUMBER·DATE·TIMESTAMP 열 + PK, 첫 항목 "(없음 — 재개 불가)") / 원본 조건(WHERE, 고정폭 TextBox, 자리 글자 POC 문구). ② **컬럼 표 카드 | 검사기 카드**(너비 ≥ 1100이면 `[1* | 320~360]`, 미만이면 세로) ③ **생성 SQL 카드**.
- **컬럼 표 카드**: 제목 list 아이콘 "컬럼", 부제 `m / t 매핑 · 오류 n · 경고 n`, 도구 Segmented(전체·매핑 안 됨·경고·오류) · [✦ 이름으로 자동 매핑] · [지우기](ghost). 열: 원본 컬럼(셀 ComboBox `(매핑 안 함)` + 아래 형식 11px 고정폭, 138) · →(24) · 대상 컬럼(고정폭 SemiBold + `PK`/`NN` 꼬리표 + 아래 `형식 DEFAULT x`, 150) · 변환식(고정폭 요약 한 줄 말줄임 최대 200, 없으면 `그대로`/`—`, 아래 `→ 결과형식`) · NULL 처리(셀 ComboBox + 기본값·사용자 식이면 아래 값 또는 경고색 "값 없음", 110) · 키(체크, 40, 키 불필요 방식이면 비활성 + 툴팁) · 검사(배지, 툴팁에 메시지 전부, 70).
  - 원본 컬럼 바꾸기 규칙은 POC 그대로(비우면 식도 비움 / 식이 옛 컬럼만 참조하면 새 이름으로 치환(**단어 경계**, 대소문자 구분 없이) / 처음 고르면 `SuggestExpression`).
  - 행을 고르면 검사기가 그 컬럼으로 바뀐다. 필터 결과가 없으면 EmptyState(check, "해당하는 컬럼이 없습니다").
  - [이름으로 자동 매핑]: 이미 매핑이 있으면 대화상자(표: 원본 → 대상 · 근거 · 추천식)와 [취소]·[모두 다시 매핑]·[빈 컬럼만 채우기](주), 없으면 바로. 토스트 `자동 매핑: n개 컬럼 (같은 이름 · 용어 사전 · 접미어 규칙)`. [지우기]는 확인 대화상자.
- **검사기 카드**(왼쪽 강조 막대 3px): 제목 `대상열 ← 원본열`(고정폭), 부제 `형식 · NOT NULL · 주석`. 본문: (a) 매핑 안 됨 안내 알림(info — 테이블 원본: "…에 맞는 값이 <원본>에 없습니다. 다른 테이블과 JOIN해야 하면 원본을 SQL로 만드세요." + 링크 [SQL 원본으로 만들기 ›](= 팝업 ①을 SQL로, 원본 시작 테이블·대상 미리 채움) / SQL 원본: "…SELECT 목록에 열을 더하세요." + [SQL 원본 편집기 ›]) (b) SectionLabel "변환식 (Transform Expression)" + 여러 줄 TextBox(고정폭, 5줄, Tab = 공백 4칸, 자리 글자 POC 문구) + 함수 조각 칩들(TRIM·NVL·REGEXP_REPLACE·CASE·CAST·TO_DATE·DECODE·SUBSTR·UPPER — POC `SNIPPETS`와 같은 문자열; 지금 식(없으면 원본 컬럼)을 감쌈) + "원본 컬럼" 칩들(클릭 = 커서 위치에 이름 넣기, 툴팁 = 형식) (c) NULL 처리 ComboBox + 힌트, 기본값·사용자 식 TextBox (d) **검사** 목록(배지 + 문장) (e) **샘플 미리보기** 6행(원본 값 → 결과, 바뀐 결과는 왼쪽 강조 막대, NULL은 흐린 기울임, 처리 꼬리표 `기본값`·`SYSDATE`·`사용자 식`·`거부 → 오류 테이블 (ORA-01400)`, 오류는 위험색).
  - **입력할 때마다**가 아니라 **0.3초 멈춘 뒤**(UI-MIG-003 5장) 검사·샘플을 갱신한다. 이전 요청은 취소(`CancellationTokenSource` 교체). 식 구문은 즉시 P1 `ExpressionAnalyzer`로 검사해 오류 배지를 먼저 보이고(Oracle 호출 전), 구문이 맞을 때만 Oracle에 묻는다.
  - **Oracle에 묻기**(FUNC03·04): 원본 접속이 시험 성공 상태(`Session.Conn[source].Status == Ok`)일 때만. (1) `SqlProbe.ExpressionCheck` → `QueryAsync(maxRows 0)`/`DescribeSqlAsync`로 결과 형식 → 검사 목록에 `→ 결과형식` 한 줄과 형식 호환성(P1 `MappingService.Compat`, 실측 통계는 메타데이터 `Stats`) (2) `SqlProbe.SamplePreview` → 6행. 접속이 안 되어 있으면 P1의 로컬 추정(`TypeInference`)으로 검사하고 샘플 칸에 안내 "원본에 연결하면 샘플이 보입니다"(접속 화면 링크). Oracle 오류(ORA-00904 등)는 검사 목록에 ERROR 배지 + Oracle 문구 그대로, 샘플 칸은 비움.
  - 칸을 떠나면(LostFocus) 값 확정: MarkChanged + 표의 그 행·요약 갱신.
- **생성 SQL 카드**: 제목 code 아이콘, 부제 "엔진이 실제로 실행하는 문장", 도구 [복사](클립보드 `Dialogs.TryCopy`). 탭 2개 `원본 SELECT` · `대상 쓰기 문 (방식 라벨)`, 본문 고정폭 읽기 전용 TextBox(가로 스크롤, 최대 높이 320). 문자열은 P1 `SqlGenerator`(Fetch 크기·작업자 수는 `Strategy`, 쓰기 문 위 주석 `-- 배치마다 {커밋크기:N0}행 배열 바인드 후 커밋`)와 같게.

### 4.3 SQL 원본 편집기 (`Ui/SqlSourceEditorWindow.cs`, 비모달 `Window`) — UI-MIG-004, POC `pages/sql-editor.js`
- DB Helper `TableInfoWindow`처럼: 소유 창 = 플러그인 창, 크기 1080×720(최소 760×520), `ShowInTaskbar = false`, `Theme.ApplyWindow`. **한 번에 하나** — 다시 열면 앞으로 가져오고 고른 매핑으로 바꾼다. 소유 창이 닫히면 같이 닫는다. 제목 `SQL 원본 편집기 — 이름`. Esc = 닫기(편집기 안 입력 중이면 무시).
- 위 줄: SQL 원본 ComboBox(`이름 → 대상 (사용 안 함)`) + [+ SQL 원본 추가](작게; 편집기를 숨기고 팝업 ① 연 뒤 추가되면 다시 보임) + [🗑] + 오른쪽 안내(12px SecondaryText "결과 별칭이 원본 컬럼이 됩니다 · 변환식·NULL 처리는 컬럼 매핑에서 · Ctrl+Enter 검증"). SQL 원본이 하나도 없으면 EmptyState(code, "SQL 원본 매핑이 없습니다", …) + [SQL 원본 추가](주) [예제 SQL로 시작](골든 `sampleJob`의 SQL 원본 사본 — 새 id, 사용 켬).
- 본문 2열(`1* | 300~340`) 위에 [편집기 카드 + 결과 탭 카드] 세로, 오른쪽 [설정 카드 + 바인드 카드] 세로. 창이 900 미만이면 오른쪽이 아래로.
- **편집기 카드**: 제목 `{}` + SQL 원본 이름 TextBox(고정폭, 170, 입력 즉시 대문자·`[^A-Z0-9_$#]`→`_`), 도구 [✓ SQL 검증](주, Ctrl+Enter) [👁 100행 미리보기] [✦ Alias 자동 매핑] — 진행 중이면 글자 `검증 중…`/`읽는 중…` + 버튼 끔. **편집기**: 줄 번호 열(오른쪽 정렬 고정폭, DisabledText, 스크롤 동기, 검증 오류 줄은 위험색 + 줄 배경 틴트) + `TextBox`(AcceptsReturn, AcceptsTab=false이고 Tab → 공백 4칸 직접 처리, 고정폭 12px, 줄바꿈 없음, 높이 330 또는 남는 공간, 선택 색 `SelectionBrush`). 구문 강조는 하지 않는다(README 결정 #6, TextBox로 시작). 입력 0.4초 뒤: 바인드 변수 동기화(`SqlSourceBinds.Sync`, 아래) + MarkChanged + "검증한 뒤 SQL이 바뀌었습니다" 표시. **배너**(검증 뒤): ERROR면 err 알림 `SQL 오류 ` + 첫 오류들, 아니면 ok/warn 알림 `SQL Valid   Result Columns : n   ·   Target Mapping : m / k   ·   Bind Parameter : :LAST_ID` (+ ` · 경고 n`). 바닥 줄: `줄 L, 열 C`(캐럿) · `결과 열 n · FROM A, B · 약 N행` · 오른쪽 `Oracle 문법 · 끝의 ; 생략 가능`.
  - 검증 동작: `SqlSourceService.ValidateAsync`. 처음 검증해 결과 열이 생겼고 컬럼 매핑이 비어 있으면 같은 이름 대상 컬럼에 자동 연결(`MappingService.AutoMapColumns`) 후 토스트 `SQL Valid · 별칭 n개를 대상 컬럼에 자동 연결`(오류면 `SQL 오류가 있습니다` 위험, 경고면 `SQL Valid · 경고 있음`). 오류 줄은 편집기에 칠하고 그 줄로 캐럿 이동. 원본 접속이 안 되어 있으면(시험 성공이 아니면) **먼저 시험**(비밀번호 구하기 포함), 실패하면 오류 알림.
  - 미리보기: `PreviewAsync(rows: 100)`. 결과 탭에 표(첫 열 `#`, 열 머리 = 이름 + 아래 형식 11px, NULL 흐린 기울임, 고정폭 12px, 가로·세로 스크롤, 최대 높이 340) + 위 줄 `n행 · x ms` `WHERE로 …`(없음) `SQL이 바뀜 — 다시 미리보세요`.
- **결과 탭 카드**(밑줄 탭, `WorkspaceUi.SelectorButton` 모양): `검증 결과 n`(표: 배지·검사·내용 — 내용은 줄바꿈 허용 12px) · `미리보기 n` · `Alias 매핑 m/k`(RowGrid: SQL 결과 열 · SELECT 식 · 추정 형식 · → · 대상 컬럼 셀 ComboBox · 변환식 · 형식 검사(배지 + 첫 메시지); 아래 줄 `값이 없는 대상 컬럼: …`(NOT NULL·기본값 없음은 `pk` 꼬리표 `(NN)`) — 데이터는 컬럼 매핑과 같은 `Mapping.Columns`, 별칭을 쓰던 대상 컬럼은 비우고 고른 것에 연결(POC `aliasTab` 로직) · `생성 SQL`(원본 SELECT + 쓰기 문, [복사]). 미검증이면 EmptyState(check, "아직 검증하지 않았습니다", …, [SQL 검증]).
- **설정 카드**: 제목 setting 아이콘 "SQL 원본 설정". 대상 테이블 ComboBox(필수; 바꾸면 병합 키 = 대상 PK, 컬럼 자동 매핑 다시) + 힌트 `스키마 · n열 · 기존 n행` / 쓰기 방식 ComboBox(`MERGE (INSERT + UPDATE)` 문구 포함) / 병합 키 칩 + `+ 키 추가` ComboBox / Fetch 크기·커밋 크기(숫자 TextBox, 자리 글자 `5,000 (작업 기본값)`, 비우면 null, 천 단위 쉼표 허용) / 체크포인트 컬럼 ComboBox(결과 열, 첫 항목 `(없음 — 재개 불가)`) + 힌트 / CheckBox "이번 작업에서 사용".
- **바인드 카드**: 제목 `:` + "바인드 변수 (Bind Parameters)". 표: 이름(`:LAST_ID` 고정폭 SemiBold, 바인드 색은 고정 중간 톤 `Theme.BindName` = `#C79A3A`) · 형식 셀 ComboBox(NUMBER·VARCHAR2·DATE) · 값 셀 TextBox · CP 체크. SQL에 없어진 변수는 Opacity 0.5 + 툴팁 "SQL에 없음". 없으면 글 "SQL에 :이름 형태의 바인드 변수가 없습니다." 바닥에 체크포인트가 있으면 `체크포인트: 열 = 값 (시각)`.
  - `SqlSourceBinds.Sync(mapping)`(Core, 단위 시험): SQL의 바인드 이름을 `SelectParser`로 찾아 새 이름은 더함(형식: `_ID|_NO|_SEQ|_CNT`로 끝나면 NUMBER, `_DT|_DATE|_AT`이면 DATE, 그 밖 VARCHAR2; `LAST_`로 시작하면 FromCheckpoint = true; 값 빈 문자열). 없어진 이름은 지우지 않는다(표시만 흐리게).
- 편집기 안의 모든 변경은 즉시 작업(`StudioState`)에 반영되고 뒤 화면(테이블·컬럼 매핑·단계 막대)이 함께 갱신된다. 닫기는 아무것도 묻지 않는다. [컬럼 매핑에서 변환식 ›](바닥 왼쪽) → `Ui.SelMapping` 설정 후 STEP 3, 창은 그대로(비모달).

### 4.4 템플릿 (UI-MIG-007 FUNC04·05, POC `actions.exportTemplate`·`importTemplate`)
- **내보내기**(파일 > 매핑 템플릿 내보내기 · 단계 막대 링크 · 테이블 매핑 [JSON 내보내기]): 대화상자(템플릿 이름 TextBox 기본 `<작업이름 소문자에서 _migration 뺀>_mapping`, 힌트) [취소] [내보내기](exportFile 아이콘) → `SaveFileDialog`(*.json) → `MappingTemplate.Export`. 매핑이 없으면 토스트 "내보낼 매핑이 없습니다".
- **가져오기**: `OpenFileDialog` → 파일 읽기 → `MappingTemplate.Apply` → 결과 토스트 `템플릿 적용: n개 추가 · m개 바꿈`, 파일이 템플릿이 아니면(`JobFileException`) 오류 대화상자. 적용 뒤 `ResolveConnections` 필요 없음(템플릿은 접속을 담지 않는다). 바뀐 매핑이 있으면 컬럼 상태가 달라질 수 있으니 단계 막대 갱신.
- 실행 중에는(P6 이후) 막는다 — 지금은 `StudioState.IsRunning` 속성만 만들어 두고 항상 false.

### 4.5 셸 연결
- `PlaceholderPage`를 STEP 2·3 실제 화면으로 바꾼다(4·5는 그대로).
- 메뉴·아이콘: 매핑 메뉴(`테이블 자동 매칭`·`컬럼 자동 매핑`(매핑 있을 때)·구분선·`SQL 원본 편집기…`(Ctrl+Q)·`SQL 원본 추가…`·`SQL 검증`(Ctrl+Enter, SQL 원본이 있을 때, 편집기 창이 있으면 그 창이 처리)·`SQL 100행 미리보기`) 켬. 아이콘 막대의 `{}`(SQL 원본 편집기) 켬. 파일 > 매핑 템플릿 가져오기/내보내기 켬. `Ctrl+Q`는 P3의 "다음 단계에서 구현" 토스트를 대체.
- 단계 막대 요약(`StepLogic`): 테이블 매핑 `n개 매핑(SQL k) · 사용 u`, 컬럼 매핑 `컬럼 m/t · 오류 n | 경고 n`(P3 5.5 규칙 그대로 — 메타가 있으면 이제 실제 값).

## 5. 시험 (`tests/MigrationStudio.Tests`, WPF 없는 논리만)
- Core: 2.4의 단위 시험 + `MappingFactory`(POC와 같은 결과 — `createMapping`/`createSqlMapping`/`starterSql`을 `make-golden.js`에 **더하지 말고**(골든은 설계 소유) 단위 시험에 기대값을 직접 적되, POC 문자열과 같음을 확인한 근거를 보고서에 적어라) + `SqlSourceBinds.Sync`.
- `Logic/` 새 파일: `TablesLogic`(그리드 행 모델: 상태 판정·라벨·`m/t`·"운영 DB · 되돌릴 수 없음" 판정·탐색 목록 `→`/`←` 링크 구성·팝업 추천), `ColumnsLogic`(필터·원본 컬럼 바꾸기 식 치환(단어 경계)·칩 문자열·체크포인트 후보·병합 키 칩), `SqlEditorLogic`(오류 줄 → 줄 번호 열 표시 계산·바인드 표·Alias 표 행·값 없는 대상 컬럼). 각 로직은 `StudioState`/WPF를 모른 채 입력 → 출력.

## 6. DevHost 그림 (`docs/dev/reports/P4-shots/`, black·light 각각, 1100×700 — 편집기는 1080×720)
`tables`(예제 작업 + 메타데이터 있는 상태 — DevHost `--seed`가 골든 `source`·`target` 메타데이터를 캐시로 넣게 확장, Oracle 없이 동작), `tables-add-dialog`, `columns`(SRC_CUSTOMER → TB_MEMBER, 행 `MOBILE_NO` 선택), `columns-sql-source`(SQLMAP_MEMBER), `sql-editor-check`(검증 결과 탭 — Oracle 없이 보이도록 DevHost `--fake-oracle` 옵션: 아래 `FakeAdapter`), `sql-editor-alias`, `sql-editor-error`(오류 줄이 칠해진 상태). DevHost `--shot`에 `--open sql-editor|add-mapping|auto-match|template-export` 옵션 추가.

### 6.1 `FakeAdapter` — 새 프로젝트 `src/MigrationStudio.Testing`(net8.0, Core 참조, 솔루션 `src` 폴더에 등록, **플러그인 zip·에이전트에는 넣지 않는다**)
`Adapters/FakeAdapter.cs`: `IDatabaseAdapter` 구현으로 Oracle 없이 화면·논리를 시험한다. `TestAsync`는 `Ok` + "Oracle 19c" + 지연 없음, `LoadMetadataAsync`는 생성자로 받은 `SchemaMetadata` 그대로, `ParseSqlAsync`는 P1 `SelectParser`의 오류를 그대로 변환(ORA 코드·줄), `DescribeSqlAsync`는 P1 `SqlSourceAnalyzer.Describe`의 열을 `QueryColumn`으로, `QueryAsync`는 **질의 문자열 → 준비된 `QueryResult`** 사전(없으면 열 없는 빈 결과)과 호출 기록(`Calls` 목록: 메서드·SQL), `CountAsync`는 사전 또는 null, `CheckControlStoreAsync`는 LOCAL. 지연 옵션(`Delay`)과 "다음 호출에서 던질 예외"(`FailNext`) 훅. DevHost는 `--fake-oracle`에서 골든 `source`·`target` 메타데이터와, 컬럼 매핑 샘플 6행·SQL 미리보기 100행을 그럴듯하게 만들어 주는 질의 응답기(`SampleResponder`: 질의에서 테이블·열 이름을 읽어 POC `mock-metadata.js`의 `sampleRows`와 같은 값)를 연결한다.

## 7. 완료 기준 (검수자가 그대로 다시 돌린다)
```powershell
dotnet build MigrationStudio.sln -c Release --nologo --no-incremental     # 경고 0
dotnet test tests/MigrationStudio.Tests -c Release --nologo                # 실패 0
$env:ORACLE_IT_DSN = "localhost:1521/xe"; dotnet test tests/MigrationStudio.OracleIT -c Release --nologo
dotnet publish src/MigrationStudio -c Release --nologo
dotnet run --project tools/DevHost -c Release -- --seed --fake-oracle --theme black --shot docs/dev/reports/P4-shots/black-tables.png --size 1100x700 --step 2
```
- 실제 Oracle(`IT_LOCAL` = `MIG_IT_SRC`)로 DevHost에서 SQL 원본 검증·미리보기·컬럼 매핑 샘플이 도는지 해 본 결과(성공 줄·ms)를 보고서에 적어라.
- 보고서 `docs/dev/reports/P4-report.md`: 새 화면 부품, 설계 공백, POC와 다른 점(+ 이유), 그림 목록.
