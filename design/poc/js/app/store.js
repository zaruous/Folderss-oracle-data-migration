/*
 * 화면 상태 저장소.
 * - settings: 마이그레이션 설정(접속 목록·기본값·실행 에이전트). 플러그인 설정에 저장 — POC는 localStorage
 * - job: 저장 대상(작업 파일). 바뀌면 dirty 표시(제목 뒤 *)와 임시 저장(localStorage, DB Helper의 SQL 임시 저장과 같은 역할)
 * - session: 이번 창에서만 쓰는 상태(접속 시험 결과, 불러온 메타데이터, 검증 결과, 실행 상태)
 * - ui: 지금 화면·선택(테마·탭·고른 행)
 */
(function () {
  'use strict';
  const MS = window.MS;
  const DRAFT_KEY = 'folderss.migration.poc.job';
  const UI_KEY = 'folderss.migration.poc.ui';
  const SETTINGS_KEY = 'folderss.migration.poc.settings';

  function readStorage(key) {
    try {
      const raw = localStorage.getItem(key);
      return raw ? JSON.parse(raw) : null;
    } catch (e) {
      return null;
    }
  }
  function writeStorage(key, value) {
    try {
      localStorage.setItem(key, JSON.stringify(value));
    } catch (e) {
      // 개인 창·차단된 저장소: 임시 저장 없이 계속 쓴다
    }
  }

  function loadSettings() {
    const saved = readStorage(SETTINGS_KEY);
    const base = MS.settingsModel.defaults();
    if (!saved || saved.version !== base.version) return base;
    return {
      version: base.version,
      connections: Array.isArray(saved.connections) ? saved.connections : base.connections,
      defaults: Object.assign({}, base.defaults, saved.defaults),
      agent: Object.assign({}, base.agent, saved.agent)
    };
  }

  function loadJob() {
    const draft = readStorage(DRAFT_KEY);
    if (draft) {
      try {
        return { job: MS.job.parseJob(JSON.stringify(draft.job)), dirty: !!draft.dirty };
      } catch (e) {
        // 형식이 바뀐 옛 임시 저장은 버리고 예제로 시작
      }
    }
    return { job: MS.job.sampleJob(), dirty: false };
  }

  function emptySession() {
    return {
      conn: { source: { status: 'unknown' }, target: { status: 'unknown' } },
      meta: { source: null, target: null },
      pre: null,
      post: null,
      sqlCheck: {},
      sqlPreview: {},
      run: null
    };
  }

  /** 지난번에 불러 둔 메타데이터(캐시)가 있는 것처럼 시작한다 — 접속 시험 없이도 매핑 화면을 볼 수 있게 */
  function cachedMeta(schemaOf) {
    const pick = (schema) => [MS.mockDb.SOURCE, MS.mockDb.TARGET].find((s) => s.schema === String(schema || '').toUpperCase());
    const at = new Date();
    at.setHours(9, 12, 44, 0);
    const wrap = (s) => (s ? Object.assign(JSON.parse(JSON.stringify(s)), { loadedAt: at.toISOString(), cached: true }) : null);
    return { source: wrap(pick(schemaOf('source'))), target: wrap(pick(schemaOf('target'))) };
  }

  const settings = loadSettings();
  const loaded = loadJob();
  const ui = Object.assign({
    page: 'connection', theme: 'black', selMapping: null, selColumn: null, selSql: null,
    valTab: 'pre', sqlTab: 'check', colFilter: 'all', logFilter: 'all',
    runSelected: null, runMode: 'EXECUTE', speed: 8
  }, readStorage(UI_KEY) || {});

  const store = {
    settings,
    job: loaded.job,
    dirty: loaded.dirty,
    session: emptySession(),
    ui,
    /** 작업이 바뀜: dirty 표시, 검증 결과는 "검증 뒤 바뀜"으로 */
    changed() {
      store.dirty = true;
      if (store.session.pre && !store.session.pre.running) store.session.pre.stale = true;
      store.saveDraft();
    },
    saveDraft() {
      writeStorage(DRAFT_KEY, { job: store.job, dirty: store.dirty, at: new Date().toISOString() });
    },
    saveUi() {
      writeStorage(UI_KEY, Object.assign({}, store.ui));
    },
    saveSettings() {
      writeStorage(SETTINGS_KEY, store.settings);
    },
    replaceJob(job, dirty) {
      store.job = job;
      store.dirty = !!dirty;
      linkProfiles(job);
      store.session = emptySession();
      store.session.meta = cachedMeta((role) => store.job[role].schema);
      store.ui.selMapping = null;
      store.ui.selColumn = null;
      store.ui.selSql = null;
      store.ui.runSelected = null;
      store.saveDraft();
    },
    /** 작업이 가리키는 접속(설정의 접속 + 작업의 스키마). 설정에 없으면 null */
    conn(role) {
      const ref = store.job[role];
      const p = ref && MS.settingsModel.profile(store.settings, ref.profileId);
      return p ? Object.assign({}, p, { schema: ref.schema || p.defaultSchema, profileId: p.id }) : null;
    },
    /** 설정에 없는 접속의 작업 사본(다른 PC에서 연 작업) */
    missingConn(role) {
      const ref = store.job[role];
      return ref && !store.conn(role) && ref.name ? ref : null;
    },
    meta(role) {
      return store.session.meta[role];
    },
    srcTable(name) {
      const m = store.session.meta.source;
      return m ? m.tables.find((t) => t.name === name) : null;
    },
    tgtTable(name) {
      const m = store.session.meta.target;
      return m ? m.tables.find((t) => t.name === name) : null;
    },
    /** 매핑의 원본(테이블 또는 SQL 결과를 테이블처럼) */
    sourceOf(m) {
      return MS.validation.sourceOf(m, store.session.meta.source);
    },
    mapping(id) {
      return store.job.mappings.find((m) => m.id === id) || null;
    },
    sqlMap(id) {
      const m = store.mapping(id);
      return m && m.sourceType === 'SQL' ? m : null;
    },
    sqlMappings() {
      return store.job.mappings.filter((m) => m.sourceType === 'SQL');
    }
  };

  /** 옛 작업·다른 PC 작업: profileId가 없거나 맞지 않으면 접속 이름으로 설정의 접속을 찾는다 */
  function linkProfiles(job) {
    for (const role of ['source', 'target']) {
      const ref = job[role];
      if (MS.settingsModel.profile(store.settings, ref.profileId)) continue;
      const byName = ref.name && MS.settingsModel.profileByName(store.settings, ref.name);
      if (byName) ref.profileId = byName.id;
    }
  }

  linkProfiles(store.job);
  store.session.meta = cachedMeta((role) => store.job[role].schema);

  MS.store = store;
})();
