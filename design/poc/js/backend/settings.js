/*
 * 마이그레이션 설정(플러그인 설정) 모델. 다른 플러그인(DB Helper)과 공유하지 않는다.
 * WPF 구현: IPluginManager.SetSetting("migration-settings", json) — %LOCALAPPDATA%\Folderss\plugin-data\<id>\settings.json
 *   - connections: 접속 목록. 비밀번호는 DPAPI(CurrentUser, 이 플러그인 전용 엔트로피)로 암호화해 저장
 *   - defaults:    새 작업의 이관 전략 기본값, 체크포인트 저장소
 *   - agent:       실행 에이전트(하위 프로세스) 동작
 * Folderss 설정 창의 플러그인 탭(IPluginSettingsPage)과 플러그인 안의 [마이그레이션 설정…]이 같은 내용을 편집한다.
 */
(function () {
  'use strict';
  const MS = window.MS;

  function defaults() {
    return {
      version: 1,
      connections: [
        { id: 'cn-legacy-prod', name: 'LEGACY_PROD', kind: 'oracle', color: 'red', host: '10.10.10.21', port: '1521', service: 'LEGACY', user: 'LEGACY_APP', password: 'legacy#2026', savePassword: true, defaultSchema: 'LEGACY_APP', writeBlocked: true },
        { id: 'cn-legacy-dev', name: 'LEGACY_DEV', kind: 'oracle', color: 'green', host: '10.10.30.11', port: '1521', service: 'LEGACYDEV', user: 'LEGACY_APP', password: 'dev', savePassword: true, defaultSchema: 'LEGACY_APP', writeBlocked: false },
        { id: 'cn-next-prod', name: 'NEXT_PROD', kind: 'oracle', color: 'red', host: '10.20.10.35', port: '1521', service: 'NEXTDB', user: 'NEXT_APP', password: 'next#2026', savePassword: true, defaultSchema: 'NEXT_APP', writeBlocked: false },
        { id: 'cn-next-stg', name: 'NEXT_STG', kind: 'oracle', color: 'yellow', host: '10.20.20.12', port: '1521', service: 'NEXTSTG', user: 'NEXT_APP', password: '', savePassword: false, defaultSchema: 'NEXT_APP', writeBlocked: false }
      ],
      defaults: { commitSize: 10000, fetchSize: 5000, workers: 4, errorPolicy: 'CONTINUE', errorTable: 'ERR$_', checkpointStore: 'AUTO', controlPrefix: 'MIG_' },
      agent: { onHostExit: 'CONTINUE', maxConcurrent: 1, logDays: 30 }
    };
  }

  const CHECKPOINT_STORES = [
    { value: 'AUTO', label: '자동 (권장)', desc: '대상에 MIG_CHECKPOINT를 만들 권한이 있으면 대상 DB, 없으면 로컬 파일' },
    { value: 'TARGET', label: '대상 DB 제어 테이블', desc: '데이터 배치와 같은 트랜잭션으로 저장 — 커밋과 체크포인트가 절대 어긋나지 않음' },
    { value: 'LOCAL', label: '로컬 파일', desc: '대상 DB를 건드리지 않음 — 커밋 직후 끊기면 마지막 배치를 한 번 더 처리' }
  ];

  function profile(settings, id) {
    return (settings.connections || []).find((c) => c.id === id) || null;
  }

  function profileByName(settings, name) {
    return (settings.connections || []).find((c) => c.name === name) || null;
  }

  function newProfile(settings) {
    let n = 1;
    while (profileByName(settings, 'NEW_CONNECTION_' + n)) n++;
    return { id: MS.job.newId('cn-'), name: 'NEW_CONNECTION_' + n, kind: 'oracle', color: '', host: '', port: '1521', service: '', user: '', password: '', savePassword: true, defaultSchema: '', writeBlocked: false };
  }

  /** 접속 정보 검사 → 오류 문장 목록 */
  function validateProfile(p, all) {
    const errors = [];
    if (!String(p.name || '').trim()) errors.push('접속 이름을 입력하세요');
    else if (all.some((x) => x !== p && x.id !== p.id && x.name === p.name)) errors.push('같은 이름의 접속이 있습니다: ' + p.name);
    if (!String(p.host || '').trim()) errors.push('호스트를 입력하세요');
    if (!/^\d+$/.test(String(p.port || '')) || Number(p.port) < 1 || Number(p.port) > 65535) errors.push('포트는 1–65535 숫자');
    if (!String(p.service || '').trim()) errors.push('서비스명을 입력하세요');
    if (!String(p.user || '').trim()) errors.push('사용자를 입력하세요');
    return errors;
  }

  MS.settingsModel = { defaults, CHECKPOINT_STORES, profile, profileByName, newProfile, validateProfile };
})();
