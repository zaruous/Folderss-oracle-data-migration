# P7 보고서 — 배포 · 문서 · 마무리

## 종단 점검 결과

| 항목 | 결과 |
|---|---|
| `scripts\pack.ps1 -Test` | 통과 — `release\zaruous.folderss-oracle-migration-1.0.0.zip`(6,471,311 바이트, 22개 파일), 에이전트 `--version` = 1.0.0 |
| 솔루션 빌드(Release) | 경고 0, 오류 0 |
| 단위 시험 | 295 통과 |
| Oracle IT(docker oracle-12c) | 24/24 통과(직전 통합 점검) |
| 레이아웃 검사 | 100/100 조합 문제 없음(직전 통합 점검) |
| About 버전 | 어셈블리 버전에서 읽도록 수정(0.1.0 하드코딩 제거) |
| 루트 README | 작성 완료(스크린샷 4장 `docs/images/`) |
| Placeholder·TODO | 없음 |

## 알려진 문제

| 심각도 | 내용 | 제안 |
|---|---|---|
| 중 | 메타데이터 읽기 약 2.4~2.8초(목표 1.5초 미달) | 딕셔너리 조회 병합·캐시 |
| 중 | 실행 전 검증 C06 NLS 바이트 길이 보정 미구현 | 후속 작업 |
| 중 | MIG_CHECKPOINT DB 병합(다른 PC 재개 시 상태 합치기) 미구현 | 후속 작업 |
| 낮 | 가짜 어댑터에서 C02(테이블 존재)가 ERROR로 표시(ValidationResponder에 ALL_TABLES 응답 없음) — DevHost 전용 | 응답 추가 |
| 낮 | P6b E2E(실제 에이전트로 200k 행, Job Object, 명령줄 비밀번호 검사)가 일부만 작성 | 시험 보강 |
| 낮 | 실제 Folderss 설치·다른 플러그인(DB Helper)과 동시 로드 확인은 수동 | `P7-manual-checklist.md` |

## 릴리스 전 남은 일
- 수동 체크리스트(`P7-manual-checklist.md`) 수행
- `git commit`·태그·push는 사용자가 결정(이 작업에서는 하지 않음)
