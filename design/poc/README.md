# Migration Studio 화면 POC

Folderss 플러그인 **Migration Studio**(Oracle → Oracle 이관)의 화면 설계 POC입니다. 실제 DB 없이 Mock 어댑터와 시뮬레이션 엔진으로 동작합니다. 기능 설계서는 [`docs/design-docs`](../../docs/design-docs/README.md).

## 열기

`index.html`을 Edge·Chrome으로 바로 엽니다. 로컬 파일이 막히면:

```powershell
cd design/poc
python -m http.server 8765    # http://127.0.0.1:8765
```

## 구성

- **단계(왼쪽 막대)**: ① 접속 → ② 테이블 매핑 → ③ 컬럼 매핑 → ④ 검증 → ⑤ 실행
- **도구(단계 아님)**: `{}` SQL 원본 편집기(`Ctrl+Q`) · ⚙ 마이그레이션 설정 · 작업 정의 · 매핑 템플릿

## 해 볼 것

1. **⚙ 마이그레이션 설정** — 접속 4개(LEGACY_PROD는 "쓰기 금지"), 기본값(체크포인트 저장소), 실행 에이전트
2. **접속** — 설정의 접속을 고르고 [두 접속 모두 테스트]. 대상 목록에서 LEGACY_PROD는 고를 수 없음
3. **테이블 매핑** — [이름으로 자동 매칭], [SQL 원본 추가](원본 종류 SQL · `SRC_PRODUCT의 열로 시작` → SQL 원본 편집기가 열림). 표의 `SQL SQLMAP_MEMBER` 이름을 누르면 편집기
4. **SQL 원본 편집기** — [SQL 검증](`Ctrl+Enter`), [100행 미리보기], Alias 매핑 탭(컬럼 매핑과 같은 데이터), 생성 SQL 탭(사용자 SQL을 인라인 뷰로 감싼 모양)
5. **컬럼 매핑** — `SQLMAP_MEMBER`를 고르면 원본 컬럼이 SQL 결과 별칭. MOBILE_NO 행에서 변환식을 고치면 샘플이 바로 바뀜
6. **검증** — `F6`, ERROR 줄의 [컬럼 ›] → CHANNEL_CD의 NULL 처리를 "기본값"(예: `ONLINE`)으로 → 다시 `F6`
7. **실행** — Dry Run → 이관 시작(TRUNCATE 확인 체크) → 일시정지·이어서·중지 → [체크포인트에서 재개] → [실행 후 검증]. 진행 머리에 하위 프로세스 PID(흉내)

`파일 > 예제 작업 불러오기`로 처음 상태로 돌아갑니다(작업·설정은 브라우저 localStorage에 임시 저장, 옛 v1 임시 저장은 자동으로 v2로 바뀜). `보기` 메뉴에서 Black·Light 테마를 바꿉니다.

## 구조

```
index.html
css/studio.css            Folderss Black·Light 테마 토큰과 공통 구성 요소
js/ui/                    화면 공용(요소 생성·대화상자·버튼·그리드·SQL 편집기)
js/backend/               실제 구현에서 C# Core·Adapters·Agent로 옮길 부분
  adapters.js             DatabaseAdapter 경계 + MockOracleAdapter
  mock-metadata.js        원본·대상 Mock 스키마와 샘플 행
  settings.js             마이그레이션 설정 모델(접속·기본값·에이전트)
  expression.js           변환식 해석·평가(미리보기용)·형식 추정
  mapping.js              자동 매칭·형식 호환성·컬럼 검사
  sqlgen.js               원본 SELECT(SQL 원본은 인라인 뷰) · MERGE/INSERT 생성
  sql-mapping.js          SQL 원본 해석·검증·미리보기, 결과 열 → 가상 테이블
  validation.js           실행 전·후 검증
  engine.js               스트리밍·배치·체크포인트 이관 엔진(하위 프로세스 흉내)
  job.js                  작업 파일 v2(JSON·YAML)·매핑 템플릿·v1 변환
js/app/                   상태 저장소 · 앱 셸(메뉴·아이콘·단계 막대·상태줄·단축키)
js/pages/                 단계 화면 5개 + 도구 창(sql-editor.js · settings.js)
```

WPF 구현과의 대응은 설계서 [README 4.2](../../docs/design-docs/README.md#42-poc-파일--wpf-구현-대응).
