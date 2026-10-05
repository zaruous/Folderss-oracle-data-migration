/*
 * SQL 원본 편집기 — 단계(Step)가 아니라 따로 여는 도구.
 * 매핑의 원본이 SQL일 때 SELECT 문을 쓰고 검증·미리보기 한다. JOIN·집계·코드 변환·N:1처럼 테이블 하나로 안 되는 원본을
 * SELECT로 만들고, 결과 별칭을 원본 컬럼처럼 쓴다.
 * 여는 곳: 테이블 매핑의 SQL 원본 이름·[SQL 원본 추가], 컬럼 매핑 머리, 검증 조치 링크, 매핑 메뉴, 아이콘 막대.
 * WPF 구현은 DB Helper의 테이블 정보 창처럼 별도 비모달 창(매핑 화면과 나란히 둘 수 있음). POC는 큰 대화상자로 흉내 낸다.
 * Alias 매핑 탭은 컬럼 매핑과 같은 데이터다 — 별칭 → 대상 컬럼, 변환식·NULL 처리는 컬럼 매핑에서 더한다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const { h, cx, icon, fmt } = MS;
  const UI = MS.ui;
  const S = MS.store;
  const M = MS.mapping;
  const X = MS.expr;
  const SM = MS.sqlMapping;

  let caretText = '줄 1, 열 1';
  let busy = null;
  let win = null; // {close, box, host, id}

  function current() {
    if (!win) return null;
    let m = S.sqlMap(win.id);
    if (!m && S.sqlMappings().length) {
      m = S.sqlMappings()[0];
      win.id = m.id;
    }
    return m;
  }

  /** 창 안을 다시 그린다(편집기 커서·스크롤은 새로 만듦) */
  function repaint() {
    if (!win || !win.box.isConnected) return;
    const m = current();
    const title = win.box.querySelector('.modal-title');
    if (title) title.textContent = 'SQL 원본 편집기' + (m ? ' — ' + m.source : '');
    const scroll = win.host.parentElement ? win.host.parentElement.scrollTop : 0;
    win.host.replaceChildren(m ? body(m) : emptyBody());
    if (win.host.parentElement) win.host.parentElement.scrollTop = scroll;
  }

  /** 작업이 바뀜: 뒤 화면(테이블·컬럼 매핑·단계 막대)과 창을 함께 갱신 */
  function changed(repaintWindow) {
    S.changed();
    MS.app.refresh();
    if (repaintWindow !== false) repaint();
  }

  /** 편집기 열기. id가 없으면 고른 매핑(SQL이면) → 첫 SQL 원본 */
  function open(id) {
    const sel = S.sqlMap(S.ui.selMapping);
    const target = id || (sel && sel.id) || (S.sqlMappings()[0] || {}).id || null;
    if (win && win.box.isConnected) {
      win.id = target || win.id;
      repaint();
      return;
    }
    const host = h('div');
    const handle = MS.modal({
      title: 'SQL 원본 편집기',
      cls: 'sheet',
      body: host,
      buttons: [
        { label: '컬럼 매핑에서 변환식 ›', onClick: () => { const m = current(); if (m) MS.app.go('columns', { selMapping: m.id, selColumn: null }); } },
        { label: '닫기' }
      ],
      onClose: () => { win = null; MS.app.refresh(); }
    });
    win = { close: handle.close, box: handle.box, host, id: target };
    repaint();
  }

  function addMapping() {
    if (win) win.close();
    MS.pages.tables.addDialog(null, 'SQL');
  }

  async function removeMapping() {
    const m = current();
    if (!m || !(await MS.confirmBox('SQL 원본 매핑 삭제', MS.validation.labelOf(m) + ' 매핑을 지울까요?', '삭제', 'danger'))) return;
    S.job.mappings = S.job.mappings.filter((x) => x !== m);
    delete S.session.sqlCheck[m.id];
    delete S.job.checkpoints[m.id];
    if (win) win.id = null;
    if (!S.sqlMappings().length && win) win.close();
    changed();
  }

  /** SQL에서 찾은 바인드 변수를 설정 목록에 맞춘다(새 것은 더하고, 없어진 것은 표시만) */
  function syncBinds(m) {
    const parsed = SM.parseSelect(m.sql);
    for (const b of parsed.binds) {
      if (!m.binds.some((x) => x.name === b)) m.binds.push({ name: b, type: /_(ID|NO|SEQ|CNT)$/.test(b) ? 'NUMBER' : /_(DT|DATE|AT)$/.test(b) ? 'DATE' : 'VARCHAR2', value: '', fromCheckpoint: /^LAST_/.test(b) });
    }
    return parsed.binds;
  }

  /** 결과 열이 처음 생기면 같은 이름의 대상 컬럼에 잇는다(이미 매핑이 있으면 건드리지 않음) */
  function autoMapIfEmpty(m) {
    const tgt = S.tgtTable(m.target);
    const v = S.sourceOf(m);
    if (!tgt || !v || !v.columns.length) return 0;
    if (m.columns.some((c) => M.valueSource(c))) return 0;
    m.columns = M.autoMapColumns(v.columns, tgt.columns);
    return m.columns.filter((c) => c.source).length;
  }

  async function validate(id) {
    if (id || !win) open(id);
    const m = current();
    if (!m || busy) return;
    busy = 'check';
    repaint();
    await MS.sleep(320); // 실제: 원본 Oracle에 PARSE·DESCRIBE 요청(UI-MIG-004)
    syncBinds(m);
    const mapped = autoMapIfEmpty(m);
    const r = SM.validate(m, S.meta('source'), S.meta('target'));
    S.session.sqlCheck[m.id] = Object.assign(r, { at: new Date(), sql: m.sql });
    busy = null;
    S.ui.sqlTab = 'check';
    if (mapped) S.changed();
    MS.app.refresh();
    repaint();
    MS.toast((r.level === 'ERROR' ? 'SQL 오류가 있습니다' : r.level === 'WARN' ? 'SQL Valid · 경고 있음' : 'SQL Valid') + (mapped ? ' · 별칭 ' + mapped + '개를 대상 컬럼에 자동 연결' : ''), r.level === 'ERROR' ? 'err' : r.level === 'WARN' ? 'warn' : 'ok');
  }

  async function preview(id) {
    if (id || !win) open(id);
    const m = current();
    if (!m || busy) return;
    busy = 'preview';
    repaint();
    await MS.sleep(450);
    const started = performance.now();
    const r = SM.preview(m, S.meta('source'), 100);
    S.session.sqlPreview[m.id] = Object.assign(r, { at: new Date(), sql: m.sql, ms: Math.round(performance.now() - started + 38) });
    busy = null;
    S.ui.sqlTab = 'preview';
    repaint();
  }

  function autoAlias() {
    const m = current();
    const tgt = S.tgtTable(m.target);
    const v = S.sourceOf(m);
    if (!tgt) { MS.toast('대상 테이블을 먼저 고르세요', 'warn'); return; }
    if (!v || !v.columns.length) { MS.toast('SQL을 먼저 검증하세요', 'warn'); return; }
    const fresh = M.autoMapColumns(v.columns, tgt.columns);
    let n = 0;
    for (const f of fresh) {
      if (!f.source) continue;
      let cm = m.columns.find((c) => c.target === f.target);
      if (!cm) m.columns.push((cm = { target: f.target, source: null, expr: '', nullRule: f.nullRule, defaultValue: f.defaultValue }));
      if (f.source === f.target) { cm.source = f.source; n++; }
    }
    S.ui.sqlTab = 'alias';
    changed();
    MS.toast('별칭 ' + n + ' / ' + v.columns.length + '개를 같은 이름의 대상 컬럼에 이었습니다', 'ok');
  }

  // ================= 결과 탭 =================
  function checkTab(m) {
    const r = S.session.sqlCheck[m.id];
    if (!r) return UI.empty('check', '아직 검증하지 않았습니다', 'SQL 구문·결과 열·별칭·바인드 변수·대상 컬럼 매핑·형식·체크포인트를 검사합니다.', UI.btn('SQL 검증', { icon: 'check', primary: true, onClick: () => validate() }));
    return h('div.grid-wrap', h('table.grid', h('thead', h('tr', h('th', { style: { width: '90px' } }, '결과'), h('th', { style: { width: '150px' } }, '검사'), h('th', '내용'))),
      h('tbody', r.items.map((i) => h('tr', h('td', UI.badge(i.level)), h('td', { style: { fontWeight: 600 } }, i.check), h('td', { style: { whiteSpace: 'pre-wrap', fontSize: '12px' } }, i.detail))))));
  }

  function previewTab(m) {
    const p = S.session.sqlPreview[m.id];
    if (!p) return UI.empty('view', '미리보기 없음', '원본에서 앞 100행을 읽어 결과 열을 보여 줍니다(대상에는 쓰지 않음).', UI.btn('100행 미리보기', { icon: 'view', primary: true, onClick: () => preview() }));
    if (p.error) return h('div', { style: { padding: '12px' } }, UI.notice('err', 'error', p.error));
    return h('div',
      h('div.row.wrap', { style: { padding: '8px 12px', fontSize: '12px', borderBottom: '1px solid var(--border)' } },
        h('span', h('b', p.rows.length + '행'), ' · ', p.ms + ' ms'),
        p.fromId > 1 && h('span.muted', 'WHERE로 ' + fmt.n(p.fromId) + '번부터'),
        p.errors ? h('span.err-text', '식 오류 ' + p.errors + '칸') : null,
        p.sql !== m.sql && h('span.warn-text', 'SQL이 바뀜 — 다시 미리보세요'),
        (p.warnings || []).map((w) => h('span.warn-text', w))),
      h('div.grid-wrap', { style: { maxHeight: '340px' } }, h('table.grid.compact.mono-cells',
        h('thead', h('tr', h('th.num', '#'), p.columns.map((c) => h('th', { title: c.type || '' }, c.name, h('div.type', { style: { fontWeight: 400 } }, c.type || '?'))))),
        h('tbody', p.rows.map((r, k) => h('tr', h('td.num.faint', k + 1), r.map((cell) => cell.error
          ? h('td.err-text', { title: cell.error }, '#오류')
          : h('td', { class: cx(cell.value == null && 'null') }, X.display(cell.value).text))))))));
  }

  /** 별칭 → 대상 컬럼. 데이터는 컬럼 매핑(m.columns)과 같다: 대상 컬럼의 원본이 그 별칭이면 연결 */
  function aliasTab(m) {
    const tgt = S.tgtTable(m.target);
    const v = S.sourceOf(m);
    if (!v || !v.columns.length) return UI.empty('list', '결과 열이 없습니다', 'SELECT 목록을 쓰고 검증하세요.');
    const tgtOpts = tgt ? [{ value: '', label: '(쓰지 않음)' }].concat(tgt.columns.map((t) => ({ value: t.name, label: t.name + '  ' + t.type }))) : [{ value: '', label: '대상 테이블을 고르세요' }];
    const used = new Set();
    const rows = v.columns.map((c) => {
      const cm = m.columns.find((x) => x.source === c.name);
      const t = tgt && cm && tgt.columns.find((x) => x.name === cm.target);
      if (t) used.add(t.name);
      const r = t ? M.checkColumn(cm, t, v.columns, { mode: m.mode }) : null;
      return h('tr',
        h('td', h('span.cell-main.mono', c.name)),
        h('td', h('span.expr', { title: c.comment }, c.comment)),
        h('td.type', c.type || '?'),
        h('td.arrow', icon('arrow')),
        h('td', { style: { minWidth: '170px' } }, UI.select(tgtOpts, t ? t.name : '', (val) => {
          // 이 별칭을 쓰던 대상 컬럼은 비우고, 고른 대상 컬럼의 원본을 이 별칭으로
          for (const x of m.columns) if (x.source === c.name) { x.source = null; x.expr = ''; }
          if (val) {
            let x = m.columns.find((y) => y.target === val);
            const tc = tgt.columns.find((y) => y.name === val);
            if (!x) m.columns.push((x = { target: val, source: null, expr: '', nullRule: M.defaultNullRule(tc), defaultValue: '' }));
            x.source = c.name;
            x.expr = '';
          }
          changed();
        }, { cell: true, label: c.name + ' 대상 컬럼' })),
        h('td', cm && cm.expr ? h('span.expr', { title: cm.expr }, cm.expr.replace(/\s+/g, ' ')) : h('span.faint', '—')),
        h('td', r ? h('div.row', UI.badge(r.level === 'INFO' ? 'PASS' : r.level, r.level === 'PASS' || r.level === 'INFO' ? 'OK' : r.level), h('span.muted', { style: { fontSize: '11.5px' } }, r.msgs[0] && r.msgs[0].msg)) : h('span.faint', '—')));
    });
    for (const x of m.columns) if (M.valueSource(x)) used.add(x.target);
    const missing = tgt ? tgt.columns.filter((t) => !used.has(t.name)) : [];
    return h('div',
      h('div.grid-wrap', h('table.grid', h('thead', h('tr', h('th', 'SQL 결과 열'), h('th', 'SELECT 식'), h('th', '추정 형식'), h('th.arrow', ''), h('th', '대상 컬럼 (' + (m.target || '—') + ')'), h('th', '변환식'), h('th', '형식 검사'))), h('tbody', rows))),
      h('div.row.wrap', { style: { padding: '8px 12px', borderTop: '1px solid var(--border)', fontSize: '12px' } },
        missing.length ? [h('span.muted', '값이 없는 대상 컬럼: '), missing.map((t) => h('span', { class: cx('tag', !t.nullable && !t.defaultValue && 'pk'), title: !t.nullable ? 'NOT NULL' : '' }, t.name + (!t.nullable && !t.defaultValue ? ' (NN)' : '')))] : h('span.muted', '모든 대상 컬럼에 값이 있음')));
  }

  function mergeTab(m) {
    const tgt = S.tgtTable(m.target);
    const v = S.sourceOf(m);
    if (!tgt) return UI.empty('code', '대상 테이블을 고르세요');
    if (!v || !v.columns.length) return UI.empty('code', 'SQL을 먼저 검증하세요');
    const st = S.job.strategy;
    const select = MS.sqlgen.buildSourceSelect(m, S.job.source.schema, v, tgt, { fetchSize: m.fetchSize || st.fetchSize, workers: st.workers });
    const cols = MS.sqlgen.writeColumns(m, tgt).map((c) => c.name);
    const write = MS.sqlgen.buildWriteSql(S.job.target.schema, tgt.name, cols, m.mode, m.mergeKey, MS.sqlgen.errorTableFor(st, tgt.name));
    const text = select + '\n\n-- 배치마다 ' + fmt.n(m.commitSize || st.commitSize) + '행 배열 바인드 → COMMIT → 체크포인트 ' + (m.checkpointColumn || '(없음)') + '\n' + write;
    return h('div', h('div.row', { style: { padding: '6px 12px', borderBottom: '1px solid var(--border)' } }, h('span.muted.grow', { style: { fontSize: '12px' } }, '엔진이 실행하는 원본 SELECT(사용자 SQL을 감쌈)와 대상 쓰기 문 — ' + M.modeOf(m.mode).label), UI.btn('복사', { icon: 'copy', sm: true, kind: 'ghost', onClick: () => MS.copyText(text) })),
      UI.code(text, 'flat'));
  }

  // ================= 설정 =================
  function settingsCard(m) {
    const tgtMeta = S.meta('target');
    const tgt = S.tgtTable(m.target);
    const v = S.sourceOf(m);
    const cols = v ? v.columns : [];
    const keyAdd = tgt ? UI.select([{ value: '', label: '+ 키 추가' }].concat(tgt.columns.filter((c) => !(m.mergeKey || []).includes(c.name)).map((c) => ({ value: c.name, label: c.name + (c.pk ? ' (PK)' : '') }))), '', (val) => {
      if (!val) return;
      m.mergeKey = (m.mergeKey || []).concat(val);
      changed();
    }, { label: '병합 키 추가' }) : null;
    const num = (key, fallback) => {
      const el = UI.input(m[key] == null ? '' : String(m[key]), null, { mono: true, label: key, placeholder: fmt.n(fallback) + ' (작업 기본값)', onChange: (val) => { const n = parseInt(String(val).replace(/,/g, ''), 10); m[key] = n > 0 ? n : null; changed(false); } });
      el.inputMode = 'numeric';
      return el;
    };
    return UI.card({
      title: [icon('setting'), 'SQL 원본 설정'],
      body: h('div', { style: { display: 'grid', gap: '10px' } },
        UI.field('대상 테이블 (Target Table)', UI.select([{ value: '', label: '— 고르세요' }].concat((tgtMeta ? tgtMeta.tables : []).map((t) => ({ value: t.name, label: t.name }))), m.target || '', (val) => {
          m.target = val;
          const t = S.tgtTable(val);
          m.mergeKey = t ? t.columns.filter((c) => c.pk).map((c) => c.name) : [];
          m.columns = t && cols.length ? M.autoMapColumns(cols, t.columns) : [];
          changed();
        }, { label: '대상 테이블' }), { required: true, hint: tgt ? S.job.target.schema + ' · ' + tgt.columns.length + '열 · 기존 ' + fmt.n(tgt.rows) + '행' : '' }),
        UI.field('쓰기 방식 (Write Strategy)', UI.select(M.MODES.map((x) => ({ value: x.value, label: x.value === 'MERGE' ? 'MERGE (INSERT + UPDATE)' : x.label })), m.mode, (val) => { m.mode = val; changed(); }, { label: '쓰기 방식' })),
        UI.field('병합 키 (Merge Key)', h('div.row.wrap',
          M.modeOf(m.mode).needsKey
            ? [(m.mergeKey || []).map((k) => h('span.key-chip', k, h('button', { type: 'button', 'aria-label': k + ' 빼기', onClick: () => { m.mergeKey = m.mergeKey.filter((x) => x !== k); changed(); } }, MS.ICON.close))), keyAdd]
            : h('span.faint', { style: { fontSize: '12px' } }, M.modeOf(m.mode).label + '에는 필요 없음'))),
        h('div.form',
          UI.field('Fetch 크기', num('fetchSize', S.job.strategy.fetchSize)),
          UI.field('커밋 크기', num('commitSize', S.job.strategy.commitSize))),
        UI.field('체크포인트 컬럼', UI.select([{ value: '', label: '(없음 — 재개 불가)' }].concat(cols.map((c) => ({ value: c.name, label: c.name + (c.type ? '  ' + c.type : '') }))), m.checkpointColumn || '', (val) => { m.checkpointColumn = val || null; changed(); }, { label: '체크포인트 컬럼' }),
          { hint: '감싼 SQL을 이 열 순서로 읽고(ORDER BY S.열), 커밋마다 마지막 값을 남김' }),
        UI.check('이번 작업에서 사용', m.use, (val) => { m.use = val; changed(); }))
    });
  }

  function bindCard(m) {
    const inSql = new Set(SM.parseSelect(m.sql).binds);
    const cp = S.job.checkpoints[m.id];
    return UI.card({
      title: [h('span.tok-bind', ':'), '바인드 변수 (Bind Parameters)'],
      flush: true,
      body: m.binds.length
        ? h('table.grid.compact', h('thead', h('tr', h('th', '이름'), h('th', '형식'), h('th', '값'), h('th', { title: '재개할 때 체크포인트 값으로 바꿈' }, 'CP'))),
          h('tbody', m.binds.map((b) => h('tr', { class: cx(!inSql.has(b.name) && 'dim'), title: inSql.has(b.name) ? '' : 'SQL에 없음' },
            h('td.mono', { style: { color: 'var(--syn-bind)', fontWeight: 600 } }, ':' + b.name),
            h('td', { style: { width: '92px' } }, UI.select(['NUMBER', 'VARCHAR2', 'DATE'], b.type, (val) => { b.type = val; changed(false); }, { cell: true, label: b.name + ' 형식' })),
            h('td', UI.input(b.value, (val) => { b.value = val; changed(false); }, { cell: true, mono: true, label: b.name + ' 값', placeholder: '값' })),
            h('td.w-check', h('input', { type: 'checkbox', checked: !!b.fromCheckpoint, 'aria-label': b.name + ' 체크포인트 값 사용', onChange: (e) => { b.fromCheckpoint = e.target.checked; changed(false); } }))))))
        : h('div.muted', { style: { padding: '10px 12px', fontSize: '12px' } }, 'SQL에 :이름 형태의 바인드 변수가 없습니다.'),
      foot: cp ? h('span.muted', { style: { fontSize: '12px' } }, '체크포인트: ' + cp.column + ' = ' + cp.value + ' (' + cp.at + ')') : null
    });
  }

  function emptyBody() {
    return UI.card({
      body: UI.empty('code', 'SQL 원본 매핑이 없습니다', 'N개 원본 → 1개 대상, 코드 변환, 집계가 필요할 때 원본을 SELECT 문으로 만듭니다.', h('div.row',
        UI.btn('SQL 원본 추가', { icon: 'add', primary: true, onClick: addMapping }),
        UI.btn('예제 SQL로 시작', {
          onClick: () => {
            const s = MS.job.sampleJob().mappings.find((x) => x.sourceType === 'SQL');
            s.id = MS.job.newId('tm-');
            s.use = true;
            S.job.mappings.push(s);
            win.id = s.id;
            changed();
          }
        })))
    });
  }

  function body(m) {
    const check = S.session.sqlCheck[m.id];
    const editor = UI.sqlEditor({
      value: m.sql, height: 330, label: m.source + ' SQL',
      onRun: () => validate(),
      onCaret: (c) => { caretText = '줄 ' + c.line + ', 열 ' + c.col; const el = document.getElementById('sql-caret'); if (el) el.textContent = caretText; },
      onChange: (val) => {
        m.sql = val;
        clearTimeout(win._t);
        win._t = setTimeout(() => {
          const before = m.binds.length;
          syncBinds(m);
          changed(false);
          const stale = document.getElementById('sql-stale');
          if (stale && check) stale.hidden = check.sql === m.sql;
          if (m.binds.length !== before) { const bh = document.getElementById('bind-host'); if (bh) bh.replaceChildren(bindCard(m)); }
        }, 400);
      }
    });
    if (check && check.sql === m.sql && check.errorLine) editor.markLine(check.errorLine);
    const name = UI.input(m.source, (val) => { m.source = val.toUpperCase().replace(/[^A-Z0-9_$#]/g, '_'); changed(false); }, { mono: true, label: 'SQL 원본 이름' });
    name.style.width = '170px';
    const banner = check
      ? h('div', { style: { padding: '8px 12px', borderBottom: '1px solid var(--border)' } },
        check.level === 'ERROR'
          ? UI.notice('err', 'error', h('b', 'SQL 오류 '), check.items.filter((i) => i.level === 'ERROR').map((i) => i.detail.split('\n')[0]).join(' · '))
          : UI.notice(check.level === 'WARN' ? 'warn' : 'ok', check.level === 'WARN' ? 'warn' : 'check',
            h('b', 'SQL Valid'), '   Result Columns : ' + check.columns.length + '   ·   Target Mapping : ' + check.mapped + ' / ' + check.total + '   ·   Bind Parameter : ' + (check.parsed.binds.map((b) => ':' + b).join(', ') || '없음') +
            (check.level === 'WARN' ? '   ·   경고 ' + check.items.filter((i) => i.level === 'WARN').length : '')),
        h('div#sql-stale.warn-text', { hidden: check.sql === m.sql, style: { fontSize: '12px', marginTop: '6px' } }, icon('warn'), ' 검증한 뒤 SQL이 바뀌었습니다'))
      : null;
    const v = S.sourceOf(m);
    const resultCols = v ? v.columns.length : 0;
    const mappedAlias = v ? v.columns.filter((c) => m.columns.some((x) => x.source === c.name)).length : 0;
    const pv = S.session.sqlPreview[m.id];
    const tabs = [
      { key: 'check', label: '검증 결과', count: check ? check.items.length : null },
      { key: 'preview', label: '미리보기', count: pv && pv.rows ? pv.rows.length : null },
      { key: 'alias', label: 'Alias 매핑', count: mappedAlias + '/' + resultCols },
      { key: 'merge', label: '생성 SQL' }
    ];
    const tabBody = { check: checkTab, preview: previewTab, alias: aliasTab, merge: mergeTab }[S.ui.sqlTab] || checkTab;
    const pick = UI.select(S.sqlMappings().map((x) => ({ value: x.id, label: x.source + ' → ' + (x.target || '?') + (x.use ? '' : '  (사용 안 함)') })), m.id, (val) => { win.id = val; S.ui.selMapping = val; repaint(); }, { label: 'SQL 원본 고르기' });
    pick.style.minWidth = '260px';
    pick.style.width = 'auto';
    const parsed = SM.parseSelect(m.sql);
    return h('div', { style: { display: 'grid', gap: '12px' } },
      h('div.row.wrap',
        h('span.muted', { style: { fontSize: '12px' } }, 'SQL 원본'), pick,
        UI.btn('SQL 원본 추가', { icon: 'add', sm: true, onClick: addMapping }),
        UI.btn('', { icon: 'del', kind: 'ghost', sm: true, title: 'SQL 원본 매핑 삭제', onClick: removeMapping }),
        h('span.grow'),
        h('span.muted', { style: { fontSize: '12px' } }, '결과 별칭이 원본 컬럼이 됩니다 · 변환식·NULL 처리는 컬럼 매핑에서 · Ctrl+Enter 검증')),
      h('div.split.wide-side',
        h('div.stack',
          UI.card({
            title: [icon('code'), name],
            tools: [
              UI.btn(busy === 'check' ? '검증 중…' : 'SQL 검증', { icon: 'check', primary: true, disabled: !!busy, title: 'Ctrl+Enter', onClick: () => validate() }),
              UI.btn(busy === 'preview' ? '읽는 중…' : '100행 미리보기', { icon: 'view', disabled: !!busy, onClick: () => preview() }),
              UI.btn('Alias 자동 매핑', { icon: 'magic', onClick: autoAlias })
            ],
            raw: [editor.el, banner],
            foot: [h('span#sql-caret.muted', { style: { fontSize: '12px' } }, caretText), h('span.faint', '·'), h('span.muted', { style: { fontSize: '12px' } }, '결과 열 ' + resultCols + ' · FROM ' + (parsed.tables.map((t) => t.name).filter(Boolean).join(', ') || '—') + (v && v.rows != null ? ' · 약 ' + fmt.n(v.rows) + '행' : '')), h('span.grow'), h('span.faint', { style: { fontSize: '11.5px' } }, 'Oracle 문법 · 끝의 ; 생략 가능')]
          }),
          UI.card({ raw: [UI.tabs(tabs, S.ui.sqlTab, (k) => { S.ui.sqlTab = k; repaint(); }), h('div', tabBody(m))] })),
        h('div.stack', settingsCard(m), h('div#bind-host', bindCard(m)))));
  }

  MS.sqlEditor = { open, validate, preview, addMapping, isOpen: () => !!(win && win.box.isConnected) };
})();
