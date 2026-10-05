/*
 * ⑥ 실행: 작업 선택 · 실행 모드(Dry Run · 이관 실행 · 체크포인트에서 재개) · 시작/일시정지/중지/재개,
 * 진행 막대·처리 속도·엔진 파이프라인·작업별 상태·실행 로그·체크포인트.
 * 실행 중에는 화면을 다시 그리지 않고 update(snapshot)로 필요한 칸만 바꾼다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const { h, cx, icon, fmt } = MS;
  const UI = MS.ui;
  const S = MS.store;
  const M = MS.mapping;

  let refs = null;
  let logShown = 0;
  let logCleared = 0;

  const STATE_TEXT = { idle: '대기', running: '실행 중', pausing: '일시 정지 요청…', paused: '일시 정지', done: '완료', stopped: '중지됨', failed: '실패' };
  const STATE_PILL = { idle: '', running: 'run', pausing: 'warn', paused: 'warn', done: 'ok', stopped: 'warn', failed: 'err' };
  const JOB_STATUS = { wait: ['대기', 'muted'], run: ['실행 중', 'info'], paused: ['일시 정지', 'warn'], done: ['완료', 'pass'], stopped: ['중지', 'warn'], failed: ['실패', 'error'], skipped: ['건너뜀', 'muted'] };
  const MODES = [
    { value: 'DRY', label: 'Dry Run', desc: '원본을 읽고 변환·매핑까지만 합니다. 대상에 쓰지 않고 예상 입력·갱신·거부 수를 보여 줍니다.' },
    { value: 'EXECUTE', label: '이관 실행', desc: '처음부터 실행합니다. MERGE는 이미 있는 행을 갱신하므로 다시 실행해도 중복이 생기지 않습니다.' },
    { value: 'RESUME', label: '체크포인트에서 재개', desc: '체크포인트가 있는 작업은 마지막 커밋 키 다음부터(WHERE 키 > :LAST_ID) 이어서 합니다.' }
  ];

  function logClass(tag) {
    if (['WARN', 'PAUSE'].includes(tag)) return 'warn';
    if (['ERROR', 'STOP'].includes(tag)) return 'error';
    return 'info';
  }

  function logLine(e) {
    return h('span', { class: cx('ln', 't-' + e.cls) }, h('span.ts', fmt.time(e.at)), h('span.tg', '[' + e.tag + ']'), h('span.tx', e.text));
  }

  /** 지금 고른 작업으로 만든 계획(실행 전 미리보기) 또는 실행 중 스냅숏 */
  function view() {
    const snap = S.session.run;
    if (snap && (MS.runner.active || snap.state !== 'idle')) return { snap, live: true };
    return { snap: null, live: false };
  }

  function idlePlan() {
    const sel = MS.actions.runSelection();
    if (!S.meta('source') || !S.meta('target')) return [];
    return MS.engine.buildPlan(S.job, S.session.meta, sel, S.ui.runMode).map((p) => Object.assign(p, { status: 'wait', written: 0, inserted: 0, updated: 0, rejected: 0, commits: 0, checkpoint: p.base ? p.keyAt(p.base) : null }));
  }

  // ================= 작업 선택 =================
  function jobsCard(running) {
    const sel = MS.actions.runSelection();
    const all = MS.validation.plannedMappings(Object.assign({}, S.job, {
      mappings: S.job.mappings.map((m) => Object.assign({}, m, { use: true }))
    }), S.meta('target'));
    const errorsOf = (id) => (S.session.pre ? S.session.pre.items.filter((i) => i.level === 'ERROR' && i.fix && (i.fix.mapping === id || i.fix.sql === id)).length : 0);
    let order = 0;
    let total = 0;
    const items = all.map((p) => {
      const on = sel.has(p.m.id);
      const srcInfo = S.sourceOf(p.m);
      const rows = srcInfo && srcInfo.rows != null ? srcInfo.rows : null;
      if (on && rows) total += rows;
      const cp = S.job.checkpoints[p.m.id];
      const errs = errorsOf(p.m.id);
      const box = h('input', {
        type: 'checkbox', checked: on, disabled: running, 'aria-label': p.label + ' 선택',
        onChange: (e) => {
          const set = MS.actions.runSelection();
          if (e.target.checked) set.add(p.m.id); else set.delete(p.m.id);
          S.ui.runSelected = [...set];
          S.saveUi();
          MS.app.refresh();
        }
      });
      return h('label', { class: cx('job-item', on && 'on') },
        box,
        h('span.job-order', on ? String(++order) : '–'),
        h('div', { style: { minWidth: 0 } },
          h('div.job-title', p.kind === 'sql' ? h('span.tag', { style: { marginRight: '6px', color: 'var(--syn-fn)', borderColor: 'currentColor' } }, 'SQL') : null, p.m.source + ' → ' + (p.m.target || '?')),
          h('div.job-meta',
            h('span', rows != null ? (p.kind === 'sql' ? '~' : '') + fmt.n(rows) + ' 행' : '행 수 모름'),
            h('span', M.modeOf(p.m.mode).label),
            cp && cp.status !== 'done' && h('span.warn-text', { title: '체크포인트 ' + cp.at }, icon('history'), ' ' + cp.column + ' ' + fmt.n(cp.value) + ' (' + Math.floor(fmt.pct(cp.rows, cp.total)) + '%)'),
            cp && cp.status === 'done' && h('span.ok-text', '지난 실행 완료'))),
        errs ? UI.badge('ERROR', 'ERROR ' + errs) : h('span'));
    });
    const allOn = all.length && all.every((p) => sel.has(p.m.id));
    return UI.card({
      title: [icon('list'), '작업 선택'],
      sub: sel.size + '개 · ' + fmt.n(total) + ' 행' + (sel.size ? '' : ''),
      tools: [UI.check('모든 작업 (ALL SELECTED)', allOn, (v) => {
        S.ui.runSelected = v ? all.map((p) => p.m.id) : [];
        S.saveUi();
        MS.app.refresh();
      })],
      body: all.length ? h('div.job-list', items) : UI.empty('list', '실행할 매핑이 없습니다', null, UI.btn('테이블 매핑으로', { onClick: () => MS.app.go('tables') })),
      foot: h('span.muted', { style: { fontSize: '12px' } }, '실행 순서: 대상 외래 키 기준 부모 → 자식(예: TB_MEMBER 다음 TB_SALES_ORDER)')
    });
  }

  // ================= 실행 제어 =================
  function controlCard() {
    const st = S.job.strategy;
    const mode = MODES.find((m) => m.value === S.ui.runMode) || MODES[1];
    const pre = S.session.pre;
    const sel = MS.actions.runSelection();
    const errs = pre && !pre.running ? pre.items.filter((i) => i.level === 'ERROR' && (!i.fix || !(i.fix.mapping || i.fix.sql) || sel.has(i.fix.mapping || i.fix.sql))).length : 0;
    const gate = !pre ? UI.notice('warn', 'warn', '실행 전 검증을 아직 하지 않았습니다. ', h('button.btn.link', { type: 'button', onClick: MS.actions.runValidation }, '검증 실행 (F6)'))
      : pre.running ? UI.notice('info', 'info', '검증 중…')
        : errs ? UI.notice('err', 'error', h('b', '고른 작업에 검증 ERROR ' + errs + '건 '), '— 이관 실행은 막힘, Dry Run은 가능. ', h('button.btn.link', { type: 'button', onClick: () => MS.app.go('validation') }, '검증 결과 ›'))
          : UI.notice(pre.stale ? 'warn' : 'ok', pre.stale ? 'warn' : 'check', pre.stale ? '검증한 뒤 작업이 바뀌었습니다 — 다시 검증을 권합니다' : '검증 통과 · ' + fmt.time(pre.at));
    refs.start = UI.btn({ DRY: 'Dry Run 시작', EXECUTE: '이관 시작', RESUME: '재개 시작' }[mode.value], { icon: mode.value === 'DRY' ? 'view' : mode.value === 'RESUME' ? 'resume' : 'play', primary: true, title: 'F5', onClick: () => MS.actions.startRun(S.ui.runMode) });
    refs.pause = UI.btn('일시정지', { icon: 'pause', kind: 'warning', onClick: () => MS.runner.pause() });
    refs.cont = UI.btn('이어서', { icon: 'play', kind: 'success', onClick: () => MS.runner.resume() });
    refs.stop = UI.btn('중지', { icon: 'stop', kind: 'danger', onClick: () => MS.runner.stop() });
    return UI.card({
      title: [icon('play'), '실행 제어'],
      body: h('div', { style: { display: 'grid', gap: '12px' } },
        UI.field('실행 모드 (Run Mode)', h('div', { style: { display: 'grid', gap: '6px' } },
          refs.modeSeg = UI.seg(MODES.map((m) => ({ value: m.value, label: m.label })), S.ui.runMode, (v) => { S.ui.runMode = v; S.saveUi(); MS.app.refresh(); }, '실행 모드'),
          h('span.field-hint', mode.desc))),
        h('div.row.wrap', refs.start, refs.pause, refs.cont, refs.stop),
        gate,
        h('dl.kv',
          h('dt', '배치'), h('dd', '커밋 ' + fmt.n(st.commitSize) + '행 · Fetch ' + fmt.n(st.fetchSize) + '행 · 작업자 ' + st.workers),
          h('dt', '오류 정책'), h('dd', MS.engine.policyText(st.errorPolicy) + (st.errorPolicy === 'CONTINUE' ? ' (' + st.errorTable + '<대상>)' : '')),
          h('dt', '대상'), h('dd', UI.dbBadge(S.conn('target')), ' ', S.job.target.schema),
          h('dt', '실행 위치'), h('dd', '하위 프로세스 ', h('span.mono', 'MigrationAgent.exe'), ' · ' + (S.settings.agent.onHostExit === 'CONTINUE' ? '창·Folderss를 닫아도 계속' : 'Folderss를 닫으면 함께 중지'))),
        h('div.row', { style: { borderTop: '1px solid var(--border)', paddingTop: '10px' } },
          h('span.muted', { style: { fontSize: '12px' } }, 'POC 시뮬레이션 속도'),
          UI.seg([1, 8, 32].map((n) => ({ value: n, label: '×' + n })), S.ui.speed, (v) => { S.ui.speed = v; S.saveUi(); if (MS.runner.opts) MS.runner.opts.speed = v; MS.app.refresh(); }, '시뮬레이션 속도')))
    });
  }

  // ================= 진행 =================
  function stage(name, glyph) {
    const metric = h('div.stage-metric', '—');
    const sub = h('div.stage-metric', '');
    const b = UI.bar(0, '');
    const el = h('div.stage', h('div.stage-name', icon(glyph), name), metric, sub, b);
    return { el, metric, sub, bar: b };
  }

  function progressCard() {
    refs.pill = h('span.pill', '대기');
    refs.runId = h('span.faint', { style: { fontSize: '12px' } });
    refs.rows = h('span.rows', '0');
    refs.of = h('span.of', '/ 0 행');
    refs.pct = h('span.pct', '0%');
    refs.currentJob = h('div.muted', { style: { fontSize: '12px' } });
    refs.bar = UI.bar(0, '', true);
    const stat = (label) => {
      const v = h('div.stat-value', '—');
      return { el: h('div.stat', h('div.stat-label', label), v), v };
    };
    refs.stats = { rate: stat('처리 속도'), elapsed: stat('경과'), eta: stat('남은 시간'), commits: stat('커밋'), ins: stat('Inserted'), upd: stat('Updated'), rej: stat('Rejected') };
    refs.stages = {
      read: stage('원본 읽기', 'importFile'), transform: stage('Transform', 'flash'), map: stage('컬럼 매핑', 'swap'),
      write: stage('배치 쓰기', 'exportFile'), commit: stage('커밋', 'check'), cp: stage('체크포인트', 'history')
    };
    const order = ['read', 'transform', 'map', 'write', 'commit', 'cp'];
    const pipe = h('div.pipeline', { 'aria-label': '엔진 파이프라인' });
    order.forEach((k, i) => {
      if (i) pipe.appendChild(h('span.pipe-arrow', MS.ICON.chevR));
      pipe.appendChild(refs.stages[k].el);
    });
    refs.jobBody = h('tbody');
    return UI.card({
      title: [icon('sync'), '진행'],
      tools: [refs.runId, refs.pill],
      body: h('div', { style: { display: 'grid', gap: '12px' } },
        h('div', h('div.big-progress', refs.rows, refs.of, refs.pct), refs.currentJob),
        refs.bar,
        h('div.stats', Object.values(refs.stats).map((s) => s.el)),
        h('div', h('div.section-label', '엔진 파이프라인 — Streaming · Batch · Checkpoint (전체를 메모리에 올리지 않음)'), pipe),
        h('div.grid-wrap', h('table.grid.compact', { 'aria-label': '작업별 진행' },
          h('thead', h('tr', h('th.num', '#'), h('th', '작업'), h('th', '상태'), h('th', { style: { minWidth: '120px' } }, '진행'), h('th.num', '행'), h('th.num', 'Inserted'), h('th.num', 'Updated'), h('th.num', 'Rejected'), h('th', '체크포인트'))),
          refs.jobBody)))
    });
  }

  function logCard() {
    refs.console = h('div.console', { role: 'log', 'aria-live': 'off', tabIndex: 0 });
    const filter = S.ui.logFilter;
    return UI.card({
      title: [icon('list'), 'Migration Log'],
      tools: [
        UI.seg([{ value: 'all', label: '전체' }, { value: 'info', label: 'INFO' }, { value: 'warn', label: 'WARN' }, { value: 'error', label: 'ERROR' }], filter, (v) => { S.ui.logFilter = v; logShown = 0; MS.app.refresh(); }, '로그 거르기'),
        UI.btn('', { icon: 'copy', kind: 'ghost', sm: true, title: '로그 복사', onClick: () => { const s = S.session.run; if (s) MS.copyText(s.log.map((e) => fmt.time(e.at) + ' [' + e.tag + '] ' + e.text).join('\n')); } }),
        UI.btn('', { icon: 'clear', kind: 'ghost', sm: true, title: '화면에서 지우기', onClick: () => { const s = S.session.run; logCleared = s ? s.log.length : 0; logShown = 0; refs.console.replaceChildren(); paintLog(); } })
      ],
      raw: refs.console
    });
  }

  function paintLog() {
    const s = S.session.run;
    const c = refs.console;
    if (!c) return;
    if (!s) {
      c.replaceChildren(h('span.empty-log', '실행하면 [START] · [INFO] Committed … · [DONE] Inserted / Updated / Rejected 로그가 여기에 나옵니다.'));
      logShown = 0;
      return;
    }
    const filter = S.ui.logFilter;
    const stick = c.scrollTop + c.clientHeight >= c.scrollHeight - 30;
    if (logShown === 0) c.replaceChildren();
    const list = s.log;
    const from = Math.max(logShown, logCleared);
    for (let i = from; i < list.length; i++) {
      const e = list[i];
      if (filter === 'all' || logClass(e.tag) === filter) c.appendChild(logLine(e));
    }
    logShown = list.length;
    if (stick) c.scrollTop = c.scrollHeight;
  }

  function checkpointCard() {
    const entries = Object.entries(S.job.checkpoints || {});
    const labelOf = (key) => {
      const m = S.mapping(key);
      return m ? MS.validation.labelOf(m) : key;
    };
    return UI.card({
      title: [icon('history'), '체크포인트'],
      sub: '커밋마다 저장 · 중지·장애 뒤 여기부터 재개',
      body: entries.length ? h('div', { style: { display: 'grid', gap: '8px' } }, entries.map(([key, cp]) => h('div', { style: { border: '1px solid var(--border)', borderRadius: '4px', padding: '8px 10px', display: 'grid', gap: '4px' } },
        h('div.row', h('b.grow.ellipsis', labelOf(key)), h('span', { class: cx('pill', cp.status === 'done' ? 'ok' : cp.status === 'running' ? 'run' : 'warn') }, cp.status === 'done' ? '완료' : cp.status === 'running' ? '진행 중' : '중단됨'),
          UI.btn('', { icon: 'del', kind: 'ghost', sm: true, title: '체크포인트 지우기(다음 실행은 처음부터)', disabled: MS.runner.active, onClick: async () => { if (await MS.confirmBox('체크포인트 지우기', labelOf(key) + '의 체크포인트를 지울까요? 다음 실행은 처음부터 합니다.', '지우기', 'danger')) { delete S.job.checkpoints[key]; MS.app.changed({}); } } })),
        h('dl.kv',
          h('dt', 'Last Successful'), h('dd.mono', cp.column + ' = ' + cp.value),
          h('dt', '진행'), h('dd', fmt.n(cp.rows) + ' / ' + fmt.n(cp.total) + ' (' + Math.floor(fmt.pct(cp.rows, cp.total)) + '%)'),
          h('dt', '시각'), h('dd', cp.at + ' · ' + (cp.runId || ''))),
        cp.status !== 'done' ? UI.code('WHERE ' + cp.column + " > " + (typeof cp.value === 'number' ? cp.value : "'" + cp.value + "'") + '\nORDER BY ' + cp.column) : h('div.muted', { style: { fontSize: '12px' } }, '증분 이관을 하면 ' + cp.column + ' > ' + cp.value + '부터 읽습니다'))))
        : UI.empty('history', '체크포인트 없음', '체크포인트 컬럼이 있는 작업은 커밋마다 마지막 키를 남깁니다.')
    });
  }

  // ================= 갱신 =================
  function update() {
    if (!refs || !refs.rows.isConnected) return;
    const v = view();
    const s = v.snap;
    const jobs = s ? s.jobs : idlePlan();
    const state = s ? s.state : 'idle';
    const totalRows = jobs.reduce((a, j) => a + j.scopeTotal, 0);
    const doneRows = jobs.reduce((a, j) => a + j.base + (j.written || 0), 0);
    const pct = fmt.pct(doneRows, totalRows);
    const cur = s ? s.jobs[Math.min(s.current, s.jobs.length - 1)] : null;
    refs.rows.textContent = fmt.n(doneRows);
    refs.of.textContent = '/ ' + fmt.n(totalRows) + ' 행';
    refs.pct.textContent = Math.floor(pct) + '%';
    refs.pill.className = cx('pill', STATE_PILL[state]);
    refs.pill.replaceChildren(...(state === 'running' ? [h('span.dot.run')] : []), (s && s.dry ? 'Dry Run · ' : '') + STATE_TEXT[state]);
    refs.runId.textContent = s ? s.runId + (s.agent ? ' · ' + s.agent.exe + ' PID ' + s.agent.pid : '') : '';
    refs.currentJob.textContent = s ? (cur ? '지금: ' + cur.label + ' (' + (Math.min(s.current, s.jobs.length - 1) + 1) + '/' + s.jobs.length + ')' : '') : jobs.length ? '시작하면 ' + jobs.length + '개 작업을 차례로 실행합니다' + (jobs.some((j) => j.base) ? ' · 체크포인트 반영' : '') : '고른 작업이 없습니다';
    refs.bar.set(pct, state === 'idle' ? '' : state === 'running' || state === 'pausing' ? 'running' : state);
    const t = MS.runner.totals();
    const sum = (k) => jobs.reduce((a, j) => a + (j[k] || 0), 0);
    refs.stats.rate.v.replaceChildren(s && s.rate ? fmt.n(s.rate) : '—', h('small', '행/초'));
    refs.stats.elapsed.v.textContent = s ? fmt.dur(s.elapsed) : '—';
    refs.stats.eta.v.textContent = s && state === 'running' && t.eta != null ? fmt.dur(t.eta) : state === 'done' ? '00:00:00' : '—';
    refs.stats.commits.v.textContent = s ? fmt.n(sum('commits')) : '—';
    refs.stats.ins.v.textContent = s ? fmt.n(sum('inserted')) : '—';
    refs.stats.upd.v.textContent = s ? fmt.n(sum('updated')) : '—';
    refs.stats.rej.v.replaceChildren(s ? h('span', { class: sum('rejected') ? 'warn-text' : '' }, fmt.n(sum('rejected'))) : '—');

    // 파이프라인
    const live = s && cur && cur.status === 'run';
    const st = S.job.strategy;
    const set = (k, metric, sub, pctv, active) => {
      const x = refs.stages[k];
      x.metric.textContent = metric;
      x.sub.textContent = sub;
      x.bar.set(pctv, active ? 'running' : '');
      x.el.classList.toggle('active', !!active);
    };
    const rate = live ? fmt.n(cur.rateNow) + ' 행/초' : '대기';
    const fetches = cur ? Math.ceil((cur.read || 0) / st.fetchSize) : 0;
    set('read', rate, cur ? 'Fetch ' + fmt.n(st.fetchSize) + ' × ' + fmt.n(fetches) + '회' : 'Fetch ' + fmt.n(st.fetchSize), live ? s.pipeline.buffer : 0, live);
    set('transform', live ? rate : '대기', 'Oracle SELECT 안에서', live ? 60 + Math.random() * 30 : 0, live);
    set('map', live ? (cur.columns || '?') + '열 → ' + cur.target : '대기', '별칭 → 대상 열', live ? 70 : 0, live);
    set('write', live ? (s.dry ? '쓰기 없음(Dry)' : cur.modeLabel) : '대기', '배열 ' + fmt.n(st.commitSize) + ' × ' + (live ? s.pipeline.inflight : st.workers), live ? Math.min(100, (cur.pending / st.commitSize) * 100) : 0, live && !s.dry);
    set('commit', s ? fmt.n(sum('commits')) + '회' : '—', s && s.dry ? '롤백(Dry)' : 'COMMIT', 0, false);
    set('cp', cur && cur.checkpoint != null ? cur.checkpointColumn + ' = ' + cur.checkpoint : '—', cur && !cur.checkpointColumn ? '체크포인트 없음' : s && s.dry ? '저장 안 함(Dry)' : '커밋마다 저장', 0, false);

    // 작업별
    refs.jobBody.replaceChildren(...jobs.map((j, i) => {
      const p = fmt.pct(j.base + (j.written || 0), j.scopeTotal);
      const [text, cls] = JOB_STATUS[j.status] || JOB_STATUS.wait;
      return h('tr', { class: cx(s && i === s.current && j.status === 'run' && 'sel') },
        h('td.num.faint', i + 1),
        h('td', h('div.cell-main', { style: { whiteSpace: 'nowrap' } }, j.label), h('div.cell-sub', j.modeLabel + (j.base ? ' · 재개: ' + j.checkpointColumn + ' > ' + j.resumeFrom.value : ''))),
        h('td', h('span', { class: 'badge ' + cls }, j.status === 'run' ? h('span.spin', { style: { width: '9px', height: '9px', borderWidth: '1.5px' } }) : null, text)),
        h('td', h('div.row', { style: { gap: '6px' } }, UI.bar(p, j.status === 'run' ? 'running' : j.status === 'done' ? 'done' : j.status === 'failed' || j.status === 'stopped' ? 'stopped' : j.status === 'paused' ? 'paused' : ''), h('span.num', { style: { width: '38px' } }, Math.floor(p) + '%'))),
        h('td.num', fmt.n(j.base + (j.written || 0)) + ' / ' + fmt.short(j.scopeTotal)),
        h('td.num', s ? fmt.n(j.inserted) : '—'),
        h('td.num', s ? fmt.n(j.updated) : j.existing ? '~' + fmt.n(j.existing) : '—'),
        h('td.num', { class: j.rejected ? 'warn-text' : '' }, s ? fmt.n(j.rejected) : j.rejectTotal ? '~' + fmt.n(j.rejectTotal) : '—'),
        h('td.mono', { style: { fontSize: '11.5px', whiteSpace: 'nowrap' } }, j.checkpoint != null ? j.checkpointColumn + ' = ' + j.checkpoint : j.checkpointColumn ? '—' : '없음'));
    }));

    // 버튼
    const rs = MS.runner.state;
    const active = MS.runner.active;
    refs.start.disabled = active;
    refs.pause.disabled = rs !== 'running';
    refs.cont.disabled = rs !== 'paused';
    refs.stop.disabled = !active;
    refs.modeSeg.querySelectorAll('button').forEach((b) => { b.disabled = active; });
    paintLog();
    // 체크포인트 카드는 0.5초마다(커밋마다 바뀌지만 자주 그릴 필요는 없음)
    const now = performance.now();
    if (refs.cpHost && (!refs.cpAt || now - refs.cpAt > 500 || !active)) {
      refs.cpAt = now;
      refs.cpHost.replaceChildren(checkpointCard());
    }
  }

  MS.pages.run = {
    update,
    onShow() {
      logShown = 0;
      update();
    },
    render() {
      refs = {};
      logShown = 0;
      const running = MS.runner.active;
      const s = S.session.run;
      const doneCard = s && !running && ['done', 'stopped', 'failed'].includes(s.state)
        ? UI.notice(s.state === 'done' ? 'ok' : s.state === 'stopped' ? 'warn' : 'err', s.state === 'done' ? 'check' : s.state === 'stopped' ? 'warn' : 'error',
          h('b', { done: s.dry ? 'Dry Run 완료' : '이관 완료', stopped: '중지됨', failed: '실패' }[s.state] + ' · '),
          fmt.dur(s.elapsed) + ' · 처리 ' + fmt.n(s.jobs.reduce((a, j) => a + j.written, 0)) + '행 · 거부 ' + fmt.n(s.jobs.reduce((a, j) => a + j.rejected, 0)) + '행   ',
          s.state === 'done' && !s.dry ? h('button.btn.link', { type: 'button', onClick: MS.actions.runPostValidation }, '실행 후 검증 ›') : null,
          s.state !== 'done' ? h('button.btn.link', { type: 'button', onClick: () => MS.actions.startRun('RESUME') }, '체크포인트에서 재개 ›') : null)
        : null;
      const page = MS.app.frame({
        key: 'run',
        title: '실행',
        desc: '작업을 골라 Dry Run으로 확인한 뒤 이관합니다. 일시정지는 커밋 경계에서 멈추고, 중지하면 진행 중 배치를 롤백한 뒤 마지막 커밋 키를 체크포인트로 남깁니다.',
        body: [
          doneCard,
          h('div.two', jobsCard(running), controlCard()),
          progressCard(),
          h('div.split.wide-side', logCard(), refs.cpHost = h('div', checkpointCard()))
        ],
        hint: 'F5 시작 · 일시정지 중 F5 = 이어서'
      });
      requestAnimationFrame(update);
      return page;
    }
  };
})();
