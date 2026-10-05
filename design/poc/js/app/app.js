/*
 * 앱 셸: 메뉴 막대·아이콘 막대(DB Helper ShellMenu와 같은 두 줄), 왼쪽 단계 막대, 상태줄, 화면 전환, 단축키, 공용 동작.
 * 화면(pages)은 MS.pages[key].render()로 만들고, 실행 중 갱신은 MS.pages.run.update(snapshot)로 받는다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const { h, cx, icon, fmt } = MS;
  const UI = MS.ui;
  const S = MS.store;
  const M = MS.mapping;

  const PAGES = [
    { key: 'connection', label: '접속' },
    { key: 'tables', label: '테이블 매핑' },
    { key: 'columns', label: '컬럼 매핑' },
    { key: 'validation', label: '검증' },
    { key: 'run', label: '실행' }
  ];
  MS.pages = MS.pages || {};

  const $ = (sel) => document.querySelector(sel);

  // ================= 공용 동작 =================
  const actions = {};

  actions.testConnection = async function (role) {
    const c = S.conn(role);
    const st = S.session.conn[role];
    if (!c) {
      Object.assign(st, { status: 'error', error: '마이그레이션 설정에서 접속을 고르세요', testedAt: new Date() });
      app.refresh();
      return { ok: false, error: st.error };
    }
    Object.assign(st, { status: 'testing', error: null });
    app.refresh();
    const r = await MS.adapters.adapterFor(c.kind).testConnection(c);
    if (r.ok) Object.assign(st, { status: 'ok', version: r.version, banner: r.banner, latency: r.latencyMs, nls: r.nls, testedAt: new Date(), error: null });
    else Object.assign(st, { status: 'error', error: r.error, testedAt: new Date() });
    app.refresh();
    return r;
  };

  actions.loadMetadata = async function (role) {
    const st = S.session.conn[role];
    st.metaLoading = true;
    app.refresh();
    try {
      if (st.status !== 'ok') {
        const r = await actions.testConnection(role);
        if (!r.ok) {
          MS.toast((role === 'source' ? '원본' : '대상') + ' 접속 실패: ' + r.error, 'err');
          return false;
        }
        st.metaLoading = true;
      }
      const c = S.conn(role);
      const meta = await MS.adapters.adapterFor(c.kind).loadMetadata(c);
      S.session.meta[role] = meta;
      const n = meta.tables.length;
      MS.toast((role === 'source' ? '원본 ' : '대상 ') + meta.schema + ' 메타데이터: 테이블·뷰 ' + n + '개', n ? 'ok' : 'warn');
      return true;
    } finally {
      st.metaLoading = false;
      app.refresh();
    }
  };

  actions.testAll = async function () {
    const [a, b] = await Promise.all([actions.testConnection('source'), actions.testConnection('target')]);
    if (a.ok && b.ok) MS.toast('두 접속 모두 연결됨', 'ok');
    else MS.toast('접속 실패: ' + [a, b].filter((x) => !x.ok).map((x) => x.error).join(' · '), 'err');
  };

  actions.runValidation = async function () {
    if (S.session.pre && S.session.pre.running) return;
    const pre = { running: true, items: [], startedAt: performance.now(), stale: false };
    S.session.pre = pre;
    S.ui.valTab = 'pre';
    app.go('validation');
    const test = async (role) => {
      const st = S.session.conn[role];
      if (st.status === 'ok') return { ok: true, version: st.version, latencyMs: st.latency };
      return actions.testConnection(role);
    };
    try {
      await MS.validation.runPre({ job: S.job, conn: S.conn, meta: S.session.meta, test }, (item) => {
        pre.items.push(item);
        if (S.ui.page === 'validation') MS.pages.validation.update();
      });
    } catch (e) {
      pre.items.push({ group: '검증', check: '내부 오류', level: 'ERROR', detail: e.message });
      console.error(e);
    }
    pre.running = false;
    pre.at = new Date();
    pre.ms = performance.now() - pre.startedAt;
    const count = levelCount(pre.items);
    MS.toast('검증 완료 · PASS ' + count.PASS + ' · WARN ' + count.WARN + ' · ERROR ' + count.ERROR, count.ERROR ? 'err' : count.WARN ? 'warn' : 'ok');
    app.refresh();
  };

  actions.runPostValidation = async function () {
    const run = S.session.run;
    if (!run || ['running', 'pausing', 'paused'].includes(run.state)) return;
    const post = { running: true, items: [], runId: run.runId };
    S.session.post = post;
    S.ui.valTab = 'post';
    app.go('validation');
    await MS.validation.runPost({ job: S.job }, run, (item) => {
      post.items.push(item);
      if (S.ui.page === 'validation') MS.pages.validation.update();
    });
    post.running = false;
    post.at = new Date();
    app.refresh();
  };

  function levelCount(items) {
    const c = { PASS: 0, WARN: 0, ERROR: 0, INFO: 0, SKIP: 0 };
    for (const i of items || []) c[i.level] = (c[i.level] || 0) + 1;
    return c;
  }

  // 실행 대상 목록(실행 화면): 테이블 매핑 + SQL 매핑. 처음에는 "사용" 표시한 것 전부
  actions.runSelection = function () {
    const all = S.job.mappings.map((m) => m.id);
    if (!S.ui.runSelected) S.ui.runSelected = S.job.mappings.filter((m) => m.use).map((m) => m.id);
    S.ui.runSelected = S.ui.runSelected.filter((id) => all.includes(id));
    return new Set(S.ui.runSelected);
  };

  /** 실행 시작(모드: DRY · EXECUTE · RESUME). 검증 ERROR·위험 방식은 여기서 막거나 묻는다. */
  actions.startRun = async function (mode) {
    if (runner.active) return;
    mode = mode || S.ui.runMode;
    const selected = actions.runSelection();
    if (!selected.size) {
      MS.toast('실행할 작업을 고르세요', 'warn');
      app.go('run');
      return;
    }
    if (!S.session.meta.source || !S.session.meta.target) {
      MS.toast('메타데이터를 먼저 불러오세요', 'warn');
      app.go('connection');
      return;
    }
    const target = S.conn('target');
    if (!S.conn('source') || !target) {
      MS.toast('접속 화면에서 원본·대상 접속을 고르세요', 'warn');
      app.go('connection');
      return;
    }
    if (mode !== 'DRY' && target.writeBlocked) {
      MS.toast(target.name + ' 접속은 "쓰기 금지"라 대상으로 쓸 수 없습니다(마이그레이션 설정)', 'err');
      return;
    }
    if (mode !== 'DRY') {
      const pre = S.session.pre;
      if (!pre || pre.running) {
        const go = await MS.confirmBox('검증 먼저', '실행 전 검증을 아직 하지 않았습니다. 검증을 실행할까요?', '검증 실행');
        if (go) actions.runValidation();
        return;
      }
      const blocking = pre.items.filter((i) => i.level === 'ERROR' && (!i.fix || !(i.fix.mapping || i.fix.sql) || selected.has(i.fix.mapping || i.fix.sql)));
      if (blocking.length) {
        MS.modal({
          title: '실행할 수 없음',
          body: h('div', { style: { display: 'grid', gap: '8px' } },
            UI.notice('err', 'error', '고른 작업에 검증 ERROR가 ' + blocking.length + '건 있습니다. 고친 뒤 다시 검증하세요. Dry Run은 할 수 있습니다.'),
            h('ul', { style: { margin: 0, paddingLeft: '18px' } }, blocking.slice(0, 5).map((i) => h('li', h('b', i.check), ' · ', i.target || '', h('div.muted', i.detail.split('\n')[0]))))),
          buttons: [{ label: '닫기' }, { label: '검증 결과 보기', primary: true, onClick: () => { S.ui.valTab = 'pre'; app.go('validation'); } }]
        });
        return;
      }
      if (pre.stale) {
        const ok = await MS.confirmBox('검증 뒤 바뀜', '검증한 뒤 작업이 바뀌었습니다. 그래도 실행할까요? (다시 검증을 권합니다)', '그래도 실행', 'warning');
        if (!ok) return;
      }
      const risky = S.job.mappings.filter((m) => selected.has(m.id) && M.modeOf(m.mode).destructive);
      if (risky.length && !(await confirmDestructive(risky))) return;
    }
    const plan = MS.engine.buildPlan(S.job, S.session.meta, selected, mode);
    if (mode === 'RESUME' && !plan.some((p) => p.base)) MS.toast('재개할 체크포인트가 없는 작업은 처음부터 실행합니다', 'warn');
    S.session.post = null;
    const store = MS.settingsModel.CHECKPOINT_STORES.find((x) => x.value === S.settings.defaults.checkpointStore) || MS.settingsModel.CHECKPOINT_STORES[0];
    runner.start(plan, {
      runMode: mode, commitSize: S.job.strategy.commitSize, fetchSize: S.job.strategy.fetchSize, workers: S.job.strategy.workers,
      errorPolicy: S.job.strategy.errorPolicy, speed: S.ui.speed, onHostExit: S.settings.agent.onHostExit,
      checkpointStore: store.value === 'LOCAL' ? '로컬 파일' : store.value === 'TARGET' ? '대상 DB(' + S.settings.defaults.controlPrefix + 'CHECKPOINT)' : '자동 → 대상 DB(' + S.settings.defaults.controlPrefix + 'CHECKPOINT)'
    });
    app.go('run');
  };

  /** DB Helper 안전장치와 같은 방식: 위험한 문장은 확인 체크 후 실행 */
  function confirmDestructive(list) {
    return new Promise((resolve) => {
      let ok = false;
      const box = h('input', { type: 'checkbox' });
      const m = MS.modal({
        title: '되돌릴 수 없는 이관 방식',
        width: 520,
        body: h('div', { style: { display: 'grid', gap: '10px' } },
          UI.notice('warn', 'warn', '대상 ', UI.dbBadge(S.conn('target')), '에서 아래 테이블의 기존 행을 지웁니다.'),
          h('ul.mono', { style: { margin: 0, paddingLeft: '18px' } }, list.map((x) => h('li', M.modeOf(x.mode).label + '  ' + S.job.target.schema + '.' + x.target))),
          h('label.check', box, '지워도 되는 것을 확인했습니다')),
        buttons: [
          { label: '취소' },
          { label: '실행', primary: true, kind: 'danger', disabled: true, onClick: () => { ok = true; } }
        ]
      });
      const run = m.box.querySelector('.modal-foot .primary');
      box.addEventListener('change', () => { run.disabled = !box.checked; });
      const obs = new MutationObserver(() => { if (!m.box.isConnected) { obs.disconnect(); resolve(ok); } });
      obs.observe($('#overlay'), { childList: true });
    });
  }

  // ================= 작업 파일 =================
  function guardRunning() {
    if (runner.active) {
      MS.toast('실행 중에는 작업을 바꿀 수 없습니다', 'warn');
      return true;
    }
    return false;
  }

  actions.newJob = async function () {
    if (guardRunning()) return;
    if (S.dirty && !(await MS.confirmBox('새 작업', '저장하지 않은 변경이 있습니다. 버리고 새 작업을 만들까요? (접속·전략은 그대로 둡니다)', '새 작업'))) return;
    const job = MS.job.blankJob(S.job);
    const d = S.settings.defaults;
    Object.assign(job.strategy, { commitSize: d.commitSize, fetchSize: d.fetchSize, workers: d.workers, errorPolicy: d.errorPolicy, errorTable: d.errorTable });
    S.replaceJob(job, false);
    MS.toast('새 작업을 만들었습니다(접속은 그대로, 전략은 마이그레이션 설정의 기본값)', 'ok');
    app.go('tables');
  };

  actions.loadSample = async function () {
    if (guardRunning()) return;
    if (S.dirty && !(await MS.confirmBox('예제 작업', '저장하지 않은 변경을 버리고 예제 작업(CUSTOMER_MIGRATION)을 불러올까요?', '불러오기'))) return;
    S.replaceJob(MS.job.sampleJob(), false);
    MS.toast('예제 작업을 불러왔습니다', 'ok');
    app.go('connection');
  };

  actions.openJob = async function () {
    if (guardRunning()) return;
    const f = await MS.pickFile('.json');
    if (!f) return;
    try {
      S.replaceJob(MS.job.parseJob(f.text), false);
      MS.toast(f.name + ' 열기 · 접속은 이 PC의 마이그레이션 설정에서 이름으로 찾습니다', 'ok');
      app.go('connection');
    } catch (e) {
      MS.toast('작업 파일을 열지 못했습니다: ' + e.message, 'err');
    }
  };

  actions.saveJob = function (yaml) {
    const name = S.job.jobName + (yaml ? '.job.yaml' : '.job.json');
    MS.download(name, yaml ? MS.job.yamlOf(S.job, S.settings) : MS.job.toJson(S.job, S.settings), yaml ? 'text/yaml' : 'application/json');
    S.dirty = false;
    S.saveDraft();
    MS.toast(name + ' 저장 (비밀번호 제외)', 'ok');
    app.refresh({ page: false });
  };

  actions.showJobDefinition = function () {
    let tab = 'json';
    const host = h('div');
    const paint = () => {
      host.innerHTML = '';
      host.appendChild(UI.tabs([{ key: 'json', label: 'JSON' }, { key: 'yaml', label: 'YAML' }], tab, (k) => { tab = k; paint(); }));
      host.appendChild(UI.code(tab === 'json' ? MS.job.toJson(S.job, S.settings) : MS.job.yamlOf(S.job, S.settings), 'flat'));
    };
    paint();
    MS.modal({
      title: '작업 정의 — ' + S.job.jobName,
      width: 820,
      body: h('div', { style: { display: 'grid', gap: '8px' } },
        h('div.muted', { style: { fontSize: '12px' } }, '접속·전략·매핑·체크포인트를 한 파일로 저장합니다. 비밀번호는 넣지 않습니다.'),
        h('div.card', { style: { overflow: 'hidden' } }, host)),
      buttons: [
        { label: '복사', icon: 'copy', onClick: () => { MS.copyText(tab === 'json' ? MS.job.toJson(S.job, S.settings) : MS.job.yamlOf(S.job, S.settings)); return false; } },
        { label: 'YAML 저장', onClick: () => actions.saveJob(true) },
        { label: 'JSON 저장', primary: true, onClick: () => actions.saveJob(false) }
      ]
    });
  };

  actions.exportTemplate = function () {
    const name = UI.input(S.job.jobName.toLowerCase().replace(/_migration$/, '') + '_mapping', null, { mono: true, label: '템플릿 이름' });
    MS.modal({
      title: '매핑 템플릿 내보내기',
      body: h('div', { style: { display: 'grid', gap: '10px' } },
        UI.field('템플릿 이름', name, { hint: '파일 이름: <이름>.json — 테이블·컬럼·SQL 매핑과 용어 사전을 담습니다(접속 정보 제외).' }),
        h('div.muted', { style: { fontSize: '12px' } }, '매핑 ' + S.job.mappings.length + '개 (테이블 원본 ' + S.job.mappings.filter((m) => m.sourceType !== 'SQL').length + ' · SQL 원본 ' + S.sqlMappings().length + ')')),
      buttons: [{ label: '취소' }, {
        label: '내보내기', primary: true, icon: 'exportFile',
        onClick: () => {
          const n = name.value.trim() || 'mapping';
          MS.download(n + '.json', MS.job.mappingTemplate(S.job, n));
          MS.toast(n + '.json 내보냄', 'ok');
        }
      }]
    });
  };

  actions.importTemplate = async function () {
    if (guardRunning()) return;
    const f = await MS.pickFile('.json');
    if (!f) return;
    try {
      const r = MS.job.applyTemplate(S.job, f.text);
      S.changed();
      MS.toast(f.name + ': 매핑 추가 ' + r.added + ' · 바꿈 ' + r.replaced, 'ok');
      app.go('tables');
    } catch (e) {
      MS.toast('템플릿을 읽지 못했습니다: ' + e.message, 'err');
    }
  };

  actions.showShortcuts = function () {
    const rows = [
      ['Ctrl+1 … Ctrl+5', '단계 이동(접속 … 실행)'], ['Ctrl+Q', 'SQL 원본 편집기'], ['Ctrl+O', '작업 열기'], ['Ctrl+S', '작업 저장(JSON)'],
      ['F6', '실행 전 검증'], ['F5', '이관 시작(지금 실행 모드)'], ['Ctrl+Enter', 'SQL 편집기: SQL 검증'], ['Tab', 'SQL 편집기: 공백 4칸'],
      ['Alt+F · M · R · V · H', '메뉴 열기'], ['Esc', '메뉴·대화상자 닫기']
    ];
    MS.modal({
      title: '단축키',
      body: h('table.grid.compact', h('tbody', rows.map((r) => h('tr', h('td.mono', { style: { width: '170px' } }, r[0]), h('td', r[1]))))),
      buttons: [{ label: '닫기', primary: true }]
    });
  };

  actions.about = function () {
    MS.modal({
      title: 'Migration Studio POC',
      body: h('div', { style: { display: 'grid', gap: '8px' } },
        h('p', { style: { margin: 0 } }, 'Folderss 플러그인 "Oracle Migration Studio"의 화면 설계 POC입니다. 실제 DB 대신 Mock 어댑터와 시뮬레이션 엔진으로 동작합니다.'),
        h('dl.kv',
          h('dt', '화면'), h('dd', '접속 → 테이블 매핑 → 컬럼 매핑 → 검증 → 실행 (SQL 원본 편집기는 따로 여는 도구)'),
          h('dt', '계층'), h('dd', 'ui(화면) · app(셸·상태) · backend(어댑터·매핑·검증·엔진) — WPF 구현에서 같은 경계로 나눕니다'),
          h('dt', '테마'), h('dd', 'Folderss Black·Light 테마 값과 같음(보기 메뉴)'),
          h('dt', '실행'), h('dd', '실제 구현은 하위 프로세스 MigrationAgent.exe가 실행 — 창을 닫아도 계속'),
          h('dt', '설계서'), h('dd', 'docs/design-docs/'))),
      buttons: [{ label: '닫기', primary: true }]
    });
  };

  actions.setTheme = function (t) {
    S.ui.theme = t;
    document.documentElement.dataset.theme = t;
    S.saveUi();
    renderMenubarLabels();
  };

  // ================= 실행 엔진 =================
  let liveQueued = false;
  const runner = new MS.engine.MigrationEngine((type, snap, payload) => {
    S.session.run = snap;
    if (type === 'checkpoint') {
      S.job.checkpoints[payload.key] = Object.assign({}, payload, { at: fmt.stamp() });
      S.saveDraft();
      return;
    }
    if (type === 'end') {
      const msg = { done: snap.dry ? 'Dry Run 완료' : '이관 완료', stopped: '중지 · 체크포인트 저장', failed: '실패 · 로그를 확인하세요' }[snap.state];
      MS.toast(msg, snap.state === 'done' ? 'ok' : snap.state === 'stopped' ? 'warn' : 'err');
      app.refresh();
      return;
    }
    if (!liveQueued) {
      liveQueued = true;
      requestAnimationFrame(() => {
        liveQueued = false;
        if (S.ui.page === 'run' && MS.pages.run.update) MS.pages.run.update(S.session.run);
        renderSidebar();
        renderStatus();
        refreshIcons();
      });
    }
  });
  MS.runner = runner;

  // ================= 메뉴 =================
  function mnLabel(text) {
    const at = text.indexOf('_');
    if (at < 0) return [text];
    return [text.slice(0, at), h('span.mn', text[at + 1]), text.slice(at + 2)];
  }

  const can = {
    start: () => !runner.active,
    pause: () => runner.state === 'running',
    stop: () => runner.active,
    resume: () => runner.state === 'paused' || (!runner.active && Object.values(S.job.checkpoints).some((c) => c.status !== 'done')),
    columnsPage: () => S.ui.page === 'columns'
  };

  const MENUS = [
    {
      label: '파일(_F)', key: 'F', items: () => [
        { label: '새 작업(_N)', icon: 'newDoc', run: actions.newJob },
        { label: '작업 열기(_O)…', key: 'Ctrl+O', icon: 'open', run: actions.openJob },
        { label: '예제 작업 불러오기(_X)', run: actions.loadSample },
        'sep',
        { label: '작업 저장(_S)', key: 'Ctrl+S', icon: 'save', run: () => actions.saveJob(false) },
        { label: 'YAML로 저장(_Y)', icon: 'saveAs', run: () => actions.saveJob(true) },
        { label: '작업 정의 보기(_J)…', icon: 'page', run: actions.showJobDefinition },
        'sep',
        { label: '매핑 템플릿 가져오기(_I)…', icon: 'importFile', run: actions.importTemplate },
        { label: '매핑 템플릿 내보내기(_E)…', icon: 'exportFile', run: actions.exportTemplate },
        'sep',
        { label: '마이그레이션 설정(_G)…', icon: 'setting', run: () => MS.settingsDialog.open('connections') }
      ]
    },
    {
      label: '매핑(_M)', key: 'M', items: () => [
        { label: '테이블 자동 매칭(_A)', icon: 'magic', run: () => { app.go('tables'); MS.pages.tables.autoMatch(); } },
        { label: '컬럼 자동 매핑(_C)', run: () => { app.go('columns'); MS.pages.columns.autoMap(); }, enabled: () => S.job.mappings.length > 0 },
        'sep',
        { label: 'SQL 원본 편집기(_Q)…', key: 'Ctrl+Q', icon: 'code', run: () => MS.sqlEditor.open() },
        { label: 'SQL 원본 추가(_N)…', icon: 'add', run: () => { app.go('tables'); MS.pages.tables.addDialog(null, 'SQL'); } },
        { label: 'SQL 검증(_V)', key: 'Ctrl+Enter', icon: 'check', run: () => MS.sqlEditor.validate(), enabled: () => S.sqlMappings().length > 0 },
        { label: 'SQL 100행 미리보기(_P)', icon: 'view', run: () => MS.sqlEditor.preview(), enabled: () => S.sqlMappings().length > 0 }
      ]
    },
    {
      label: '실행(_R)', key: 'R', items: () => [
        { label: '실행 전 검증(_V)', key: 'F6', icon: 'checklist', run: actions.runValidation, enabled: () => !(S.session.pre && S.session.pre.running) },
        { label: '실행 후 검증(_A)', run: actions.runPostValidation, enabled: () => S.session.run && ['done', 'stopped', 'failed'].includes(S.session.run.state) },
        'sep',
        { label: 'Dry Run(_D)', icon: 'view', run: () => actions.startRun('DRY'), enabled: can.start },
        { label: '이관 시작(_S)', key: 'F5', icon: 'play', run: () => actions.startRun('EXECUTE'), enabled: can.start },
        { label: '일시정지(_P)', icon: 'pause', run: () => runner.pause(), enabled: can.pause },
        { label: '이어서 실행(_C)', icon: 'play', run: () => runner.resume(), enabled: () => runner.state === 'paused' },
        { label: '중지(_T)', icon: 'stop', run: () => runner.stop(), enabled: can.stop },
        { label: '체크포인트에서 재개(_R)', icon: 'resume', run: () => actions.startRun('RESUME'), enabled: () => !runner.active && Object.values(S.job.checkpoints).some((c) => c.status !== 'done') }
      ]
    },
    {
      label: '보기(_V)', key: 'V', items: () => PAGES.map((p, i) => ({ label: (i + 1) + ' ' + p.label, key: 'Ctrl+' + (i + 1), run: () => app.go(p.key), checked: () => S.ui.page === p.key }))
        .concat(['sep', { note: '테마' },
          { label: 'Black(_B)', icon: 'check', run: () => actions.setTheme('black'), checked: () => S.ui.theme === 'black', iconOnlyWhenChecked: true },
          { label: 'Light(_L)', icon: 'check', run: () => actions.setTheme('light'), checked: () => S.ui.theme === 'light', iconOnlyWhenChecked: true }])
    },
    {
      label: '도움말(_H)', key: 'H', items: () => [
        { label: '단축키(_K)…', run: actions.showShortcuts },
        { label: '이 POC에 대하여(_A)…', icon: 'info', run: actions.about }
      ]
    }
  ];

  let openMenuState = null;

  function renderMenubar() {
    const bar = $('#menubar');
    bar.innerHTML = '';
    MENUS.forEach((m) => {
      const b = h('button.menu-top', {
        type: 'button', 'aria-haspopup': 'true', dataset: { key: m.key },
        onClick: (e) => (openMenuState && openMenuState.menu === m ? closeMenu() : openMenu(m, e.currentTarget)),
        onMouseenter: (e) => { if (openMenuState && openMenuState.menu !== m) openMenu(m, e.currentTarget); }
      }, mnLabel(m.label));
      bar.appendChild(b);
    });
  }
  function renderMenubarLabels() {
    if (openMenuState) {
      const { menu, anchor } = openMenuState;
      openMenu(menu, anchor);
    }
  }

  function openMenu(menu, anchor) {
    closeMenu();
    const items = menu.items();
    const pop = h('div.menu-pop', { role: 'menu', style: { left: anchor.offsetLeft + 'px' } });
    for (const it of items) {
      if (it === 'sep') { pop.appendChild(h('div.menu-sep')); continue; }
      if (it.note) { pop.appendChild(h('div.menu-note', it.note)); continue; }
      const enabled = it.enabled ? it.enabled() : true;
      const checked = it.checked ? it.checked() : false;
      const glyph = it.iconOnlyWhenChecked ? (checked ? it.icon : null) : it.icon || (checked ? 'check' : null);
      pop.appendChild(h('button', {
        type: 'button', role: 'menuitem', class: cx('menu-item', checked && 'checked'), disabled: !enabled,
        onClick: () => { closeMenu(); it.run(); }
      }, glyph ? icon(glyph) : h('span'), h('span', mnLabel(it.label)), h('span.kbd', it.key || '')));
    }
    $('#menubar').appendChild(pop);
    anchor.classList.add('open');
    openMenuState = { menu, anchor, pop };
    const first = pop.querySelector('.menu-item:not(:disabled)');
    if (first) first.focus();
    setTimeout(() => document.addEventListener('mousedown', outside), 0);
  }

  function outside(e) {
    if (openMenuState && !$('#menubar').contains(e.target)) closeMenu();
  }

  function closeMenu() {
    if (!openMenuState) return;
    openMenuState.pop.remove();
    openMenuState.anchor.classList.remove('open');
    openMenuState = null;
    document.removeEventListener('mousedown', outside);
  }

  function menuKeys(e) {
    if (!openMenuState) return false;
    const items = [...openMenuState.pop.querySelectorAll('.menu-item:not(:disabled)')];
    const at = items.indexOf(document.activeElement);
    if (e.key === 'Escape') { closeMenu(); return true; }
    if (e.key === 'ArrowDown') { (items[at + 1] || items[0]).focus(); return true; }
    if (e.key === 'ArrowUp') { (items[at - 1] || items[items.length - 1]).focus(); return true; }
    if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
      const k = MENUS.indexOf(openMenuState.menu) + (e.key === 'ArrowRight' ? 1 : -1);
      const m = MENUS[(k + MENUS.length) % MENUS.length];
      openMenu(m, $('#menubar').querySelector('[data-key="' + m.key + '"]'));
      return true;
    }
    // 메뉴 안에서 밑줄 글자
    const hit = [...openMenuState.pop.querySelectorAll('.menu-item:not(:disabled)')].find((b) => {
      const mn = b.querySelector('.mn');
      return mn && mn.textContent.toUpperCase() === e.key.toUpperCase();
    });
    if (hit) { hit.click(); return true; }
    return false;
  }

  // ================= 아이콘 막대 =================
  const ICONS = [
    { glyph: 'newDoc', title: '새 작업', run: () => actions.newJob() },
    { glyph: 'open', title: '작업 열기 (Ctrl+O)', run: () => actions.openJob() },
    { glyph: 'save', title: '작업 저장 (Ctrl+S)', run: () => actions.saveJob(false) },
    'sep',
    { glyph: 'link', title: '두 접속 시험', run: () => actions.testAll(), enabled: () => !['testing'].includes(S.session.conn.source.status) },
    { glyph: 'sync', title: '메타데이터 다시 불러오기(원본·대상)', run: () => Promise.all([actions.loadMetadata('source'), actions.loadMetadata('target')]) },
    'sep',
    { glyph: 'checklist', title: '실행 전 검증 (F6)', run: () => actions.runValidation(), enabled: () => !(S.session.pre && S.session.pre.running) },
    { glyph: 'view', title: 'Dry Run (쓰기 없이 시험 실행)', run: () => actions.startRun('DRY'), enabled: can.start },
    'sep',
    { glyph: 'play', title: '이관 시작 (F5) · 일시정지 중이면 이어서 실행', color: 'green', run: () => (runner.state === 'paused' ? runner.resume() : actions.startRun('EXECUTE')), enabled: () => !runner.active || runner.state === 'paused' },
    { glyph: 'pause', title: '일시정지 (커밋 경계에서 멈춤)', color: 'amber', run: () => runner.pause(), enabled: can.pause },
    { glyph: 'stop', title: '중지 (진행 중 배치 롤백 · 체크포인트 저장)', color: 'red', run: () => runner.stop(), enabled: can.stop },
    { glyph: 'resume', title: '체크포인트에서 재개', run: () => actions.startRun('RESUME'), enabled: () => !runner.active && Object.values(S.job.checkpoints).some((c) => c.status !== 'done') },
    'sep',
    { glyph: 'code', title: 'SQL 원본 편집기 (Ctrl+Q) — 원본을 SELECT 문으로 쓰는 매핑', run: () => MS.sqlEditor.open() },
    { glyph: 'page', title: '작업 정의 보기 (JSON·YAML)', run: () => actions.showJobDefinition() },
    { glyph: 'setting', title: '마이그레이션 설정 (접속·기본값·실행 에이전트)', run: () => MS.settingsDialog.open('connections') }
  ];
  let iconButtons = [];
  let jobLabel = null;

  function renderIconbar() {
    const bar = $('#iconbar');
    bar.innerHTML = '';
    iconButtons = [];
    for (const it of ICONS) {
      if (it === 'sep') { bar.appendChild(h('span.ib-sep')); continue; }
      const b = h('button', { type: 'button', class: cx('ib', it.color), title: it.title, 'aria-label': it.title, onClick: () => it.run() }, MS.ICON[it.glyph]);
      iconButtons.push([b, it]);
      bar.appendChild(b);
    }
    jobLabel = h('span.ib-label');
    bar.appendChild(jobLabel);
    refreshIcons();
  }

  function refreshIcons() {
    for (const [b, it] of iconButtons) b.disabled = it.enabled ? !it.enabled() : false;
    if (jobLabel) {
      jobLabel.textContent = '';
      jobLabel.append('작업: ', h('b', { style: { color: 'var(--text)' } }, S.job.jobName + (S.dirty ? ' *' : '')));
    }
  }

  // ================= 단계 막대 =================
  function stepInfo(key) {
    const job = S.job;
    const sess = S.session;
    switch (key) {
      case 'connection': {
        const a = sess.conn.source.status;
        const b = sess.conn.target.status;
        const state = a === 'error' || b === 'error' ? 'error' : a === 'ok' && b === 'ok' && sess.meta.source && sess.meta.target ? 'done' : a === 'testing' || b === 'testing' ? 'busy' : '';
        const name = (role) => (S.conn(role) || {}).name || '접속 없음';
        return { state: !S.conn('source') || !S.conn('target') ? 'error' : state, sub: name('source') + ' → ' + name('target') };
      }
      case 'tables': {
        const n = job.mappings.length;
        const used = job.mappings.filter((m) => m.use).length;
        const sql = S.sqlMappings().length;
        return { state: n ? 'done' : '', sub: n ? n + '개 매핑' + (sql ? '(SQL ' + sql + ')' : '') + ' · 사용 ' + used : '매핑 없음' };
      }
      case 'columns': {
        let mapped = 0, total = 0, err = 0, warn = 0;
        for (const m of job.mappings) {
          const st = M.mappingStatus(m, S.sourceOf(m), S.tgtTable(m.target));
          mapped += st.mapped; total += st.total; err += st.errors; warn += st.warns;
        }
        if (!total) return { state: '', sub: '—' };
        return { state: err ? 'error' : warn ? 'warn' : 'done', sub: '컬럼 ' + mapped + '/' + total + (err ? ' · 오류 ' + err : warn ? ' · 경고 ' + warn : '') };
      }
      case 'validation': {
        const pre = sess.pre;
        if (!pre) return { state: '', sub: '아직 안 함' };
        if (pre.running) return { state: 'busy', sub: '검사 중… ' + pre.items.length };
        const c = levelCount(pre.items);
        return { state: c.ERROR ? 'error' : pre.stale || c.WARN ? 'warn' : 'done', sub: pre.stale ? '검증 뒤 바뀜' : 'PASS ' + c.PASS + ' · WARN ' + c.WARN + ' · ERROR ' + c.ERROR };
      }
      case 'run': {
        const r = sess.run;
        const cp = Object.values(job.checkpoints).find((c) => c.status !== 'done');
        if (!r) return { state: cp ? 'warn' : '', sub: cp ? '재개 가능 · ' + Math.floor(fmt.pct(cp.rows, cp.total)) + '%' : '대기' };
        const t = runner.totals();
        const pct = Math.floor(t.pct) + '%';
        switch (r.state) {
          case 'running': case 'pausing': return { state: 'busy', sub: (r.dry ? 'Dry Run ' : '실행 중 ') + pct };
          case 'paused': return { state: 'warn', sub: '일시 정지 ' + pct };
          case 'done': return { state: 'done', sub: (r.dry ? 'Dry Run 완료' : '완료') + ' · ' + fmt.dur(r.elapsed) };
          case 'stopped': return { state: 'warn', sub: '중지 · 재개 가능 ' + pct };
          default: return { state: 'error', sub: '실패 · ' + pct };
        }
      }
    }
    return { state: '', sub: '' };
  }

  function renderSidebar() {
    const side = $('#sidebar');
    const scroll = side.scrollTop;
    side.innerHTML = '';
    const job = S.job;
    side.appendChild(h('div.job-head',
      h('div.job-kicker', '이관 작업'),
      h('div.job-name', { title: job.description || job.jobName }, job.jobName, S.dirty && h('span.faint', ' *')),
      h('div.job-route', UI.dbBadge(S.conn('source')), icon('arrow'), UI.dbBadge(S.conn('target')))));
    const list = h('ol.steps', { 'aria-label': '단계' });
    PAGES.forEach((p, i) => {
      const info = stepInfo(p.key);
      const glyph = info.state === 'done' ? MS.ICON.check : info.state === 'error' ? '!' : info.state === 'busy' ? null : null;
      list.appendChild(h('li', {
        class: cx('step', S.ui.page === p.key && 'on', info.state), tabIndex: 0, role: 'link', 'aria-current': S.ui.page === p.key ? 'step' : null,
        title: p.label + ' (Ctrl+' + (i + 1) + ')',
        onClick: () => app.go(p.key),
        onKeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); app.go(p.key); } }
      },
      h('span.step-num', info.state === 'busy' ? h('span.spin') : glyph ? h('span', { class: glyph === '!' ? '' : 'ico', style: { fontSize: glyph === '!' ? '12px' : '10px' } }, glyph) : String(i + 1)),
      h('span.step-label', p.label),
      h('span.step-sub', info.sub)));
    });
    side.appendChild(list);
    side.appendChild(h('div.side-foot',
      h('button.side-link', { type: 'button', onClick: actions.showJobDefinition, title: '작업 정의 보기 (JSON·YAML)' }, icon('page'), h('span.label', '작업 정의')),
      h('button.side-link', { type: 'button', onClick: actions.exportTemplate, title: '매핑 템플릿 내보내기' }, icon('exportFile'), h('span.label', '매핑 템플릿'))));
    side.scrollTop = scroll;
  }

  // ================= 상태줄 =================
  function renderStatus() {
    const bar = $('#statusbar');
    bar.innerHTML = '';
    const dot = (st) => h('span', { class: cx('dot', st === 'ok' && 'ok', st === 'error' && 'err', st === 'testing' && 'run') });
    const c = S.session.conn;
    const r = S.session.run;
    const t = runner.totals();
    let runText = '대기';
    if (r) {
      const stateText = { running: '실행 중', pausing: '일시 정지 요청', paused: '일시 정지', done: '완료', stopped: '중지됨', failed: '실패' }[r.state];
      runText = (r.dry ? 'Dry Run ' : '') + stateText + ' · ' + fmt.n(t.done) + ' / ' + fmt.n(t.total) + ' 행 (' + Math.floor(t.pct) + '%)';
    }
    bar.append(
      dot(c.source.status), UI.dbBadge(S.conn('source')), icon('arrow', 'faint'), dot(c.target.status), UI.dbBadge(S.conn('target')),
      h('span.sep'),
      h('span', runText),
      h('span.grow'),
      h('span.opt', 'Mock Oracle 어댑터 · 시뮬레이션 ×' + S.ui.speed),
      h('span.sep.opt'),
      h('span.opt', 'POC'));
  }

  // ================= 화면 =================
  function renderPage(keepScroll) {
    const host = $('#page');
    const scroll = keepScroll ? host.scrollTop : 0;
    const page = MS.pages[S.ui.page];
    host.innerHTML = '';
    try {
      host.appendChild(page.render());
    } catch (e) {
      console.error(e);
      host.appendChild(h('div.page-inner', UI.notice('err', 'error', '화면을 그리지 못했습니다: ' + e.message)));
    }
    host.scrollTop = scroll;
  }

  const app = {
    PAGES,
    go(key, params) {
      if (!MS.pages[key]) return;
      const same = S.ui.page === key;
      S.ui.page = key;
      if (params) Object.assign(S.ui, params);
      S.saveUi();
      closeMenu();
      renderPage(same);
      renderSidebar();
      refreshIcons();
      if (!same) $('#page').focus({ preventScroll: true });
      if (MS.pages[key].onShow) MS.pages[key].onShow();
    },
    /** 상태가 바뀜: 단계 막대·상태줄·아이콘, 그리고 지금 화면(page:false면 빼고) */
    refresh(opts) {
      renderSidebar();
      renderStatus();
      refreshIcons();
      if (!opts || opts.page !== false) renderPage(true);
    },
    /** 작업 내용이 바뀜(dirty) */
    changed(opts) {
      S.changed();
      app.refresh(opts || { page: false });
    },
    /** 화면 틀: 제목·설명·동작 + 본문 + 아래 이전/다음 */
    frame(o) {
      const idx = PAGES.findIndex((p) => p.key === o.key);
      const prev = PAGES[idx - 1];
      const next = PAGES[idx + 1];
      return h('div', { style: { display: 'contents' } },
        h('div.page-inner',
          h('header.page-head',
            h('div.titles',
              h('h1.page-title', h('span.step-tag', 'STEP ' + (idx + 1)), o.title),
              o.desc && h('p.page-desc', o.desc)),
            o.actions && h('div.page-actions', o.actions)),
          o.body),
        h('footer.page-foot',
          prev ? UI.btn(prev.label, { icon: 'back', kind: 'ghost', onClick: () => app.go(prev.key) }) : h('span'),
          h('span.grow.hint', o.hint || ''),
          o.footExtra,
          next && UI.btn('다음: ' + next.label, { primary: true, onClick: () => app.go(next.key) }, null)));
    },
    levelCount
  };
  MS.app = app;
  MS.actions = actions;

  // ================= 단축키 =================
  document.addEventListener('keydown', (e) => {
    if (menuKeys(e)) { e.preventDefault(); return; }
    if (document.querySelector('.modal-back')) return;
    if (e.altKey && !e.ctrlKey && /^[a-z]$/i.test(e.key)) {
      const m = MENUS.find((x) => x.key === e.key.toUpperCase());
      if (m) { e.preventDefault(); openMenu(m, $('#menubar').querySelector('[data-key="' + m.key + '"]')); }
      return;
    }
    if (e.ctrlKey && !e.shiftKey && /^[1-9]$/.test(e.key) && PAGES[Number(e.key) - 1]) { e.preventDefault(); app.go(PAGES[Number(e.key) - 1].key); return; }
    if (e.ctrlKey && (e.key === 'q' || e.key === 'Q')) { e.preventDefault(); MS.sqlEditor.open(); return; }
    if (e.ctrlKey && (e.key === 's' || e.key === 'S')) { e.preventDefault(); actions.saveJob(e.shiftKey); return; }
    if (e.ctrlKey && (e.key === 'o' || e.key === 'O')) { e.preventDefault(); actions.openJob(); return; }
    if (e.key === 'F5') { e.preventDefault(); if (runner.state === 'paused') runner.resume(); else actions.startRun(S.ui.runMode); return; }
    if (e.key === 'F6') { e.preventDefault(); actions.runValidation(); }
  });
  window.addEventListener('beforeunload', (e) => {
    if (runner.active) { e.preventDefault(); e.returnValue = ''; }
  });

  // ================= 시작 =================
  if (!PAGES.some((p) => p.key === S.ui.page)) S.ui.page = 'tables';
  document.documentElement.dataset.theme = S.ui.theme;
  renderMenubar();
  renderIconbar();
  app.refresh();
  if (MS.pages[S.ui.page] && MS.pages[S.ui.page].onShow) MS.pages[S.ui.page].onShow();
})();
