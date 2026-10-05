/*
 * ② 테이블 매핑: 원본(테이블 또는 SQL) ↔ 대상 테이블, 이관 방식·병합 키, 스키마 탐색.
 * 원본이 SQL이면 SELECT 결과를 테이블처럼 쓴다(결과 별칭 = 원본 컬럼) — SQL 문은 따로 여는 SQL 원본 편집기(pages/sql-editor.js)에서 쓴다.
 * 행을 두 번 누르거나 [편집 ›]을 누르면 컬럼 매핑으로 간다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const { h, cx, icon, fmt } = MS;
  const UI = MS.ui;
  const S = MS.store;
  const M = MS.mapping;

  let filter = '';

  function createMapping(srcName, tgtName, mode) {
    const src = S.srcTable(srcName);
    const tgt = S.tgtTable(tgtName);
    const srcPk = src.columns.find((c) => c.pk);
    const m = MS.job.tableMapping({
      source: srcName, target: tgtName,
      mode: mode || (tgt.rows ? 'MERGE' : 'INSERT_ONLY'),
      mergeKey: tgt.columns.filter((c) => c.pk).map((c) => c.name),
      checkpointColumn: srcPk ? srcPk.name : null,
      columns: M.autoMapColumns(src.columns, tgt.columns)
    });
    S.job.mappings.push(m);
    return m;
  }

  /** 원본 테이블에서 시작하는 SELECT 문(열 목록 + 체크포인트 조건) */
  function starterSql(table) {
    const t = S.srcTable(table);
    if (!t) return { sql: 'SELECT\n    \nFROM \nWHERE ', binds: [], cp: null };
    const pk = t.columns.find((c) => c.pk);
    const sql = 'SELECT\n' + t.columns.map((c) => '    T.' + c.name + ' AS ' + c.name).join(',\n') + '\nFROM ' + t.name + ' T' +
      (pk ? '\nWHERE T.' + pk.name + ' > :LAST_ID' : '');
    return { sql, binds: pk ? [{ name: 'LAST_ID', type: /^NUMBER/.test(pk.type) ? 'NUMBER' : 'VARCHAR2', value: /^NUMBER/.test(pk.type) ? '0' : ' ', fromCheckpoint: true }] : [], cp: pk ? pk.name : null };
  }

  function createSqlMapping(name, tgtName, mode, fromTable) {
    const tgt = S.tgtTable(tgtName);
    const start = fromTable ? starterSql(fromTable) : { sql: 'SELECT\n    \nFROM \nWHERE ', binds: [], cp: null };
    const m = MS.job.sqlMapping({
      source: name, target: tgtName, mode, sql: start.sql, binds: start.binds, checkpointColumn: start.cp,
      mergeKey: tgt ? tgt.columns.filter((c) => c.pk).map((c) => c.name) : []
    });
    const v = S.sourceOf(m);
    if (tgt && v && v.columns.length) m.columns = M.autoMapColumns(v.columns, tgt.columns);
    S.job.mappings.push(m);
    return m;
  }

  function autoMatch() {
    const src = S.meta('source');
    const tgt = S.meta('target');
    if (!src || !tgt) {
      MS.toast('메타데이터를 먼저 불러오세요', 'warn');
      return;
    }
    const matches = M.autoMatchTables(src.tables, tgt.tables, S.job.mappings.filter((m) => m.sourceType !== 'SQL'));
    if (!matches.length) {
      MS.toast('더 맞출 테이블이 없습니다(매핑 안 된 원본 테이블과 이름이 맞는 대상이 없음)', 'warn');
      return;
    }
    const checks = matches.map(() => true);
    const body = h('div', { style: { display: 'grid', gap: '10px' } },
      h('div.muted', { style: { fontSize: '12px' } }, '접두어(SRC_·TB_)를 뺀 이름과 용어 사전으로 찾았습니다. 추가하면 컬럼도 자동 매핑합니다.'),
      h('table.grid.compact', h('thead', h('tr', h('th.w-check', ''), h('th', '원본'), h('th.arrow', ''), h('th', '대상'), h('th', '근거'))),
        h('tbody', matches.map((m, k) => h('tr',
          h('td.w-check', h('input', { type: 'checkbox', checked: true, 'aria-label': m.source + ' 추가', onChange: (e) => { checks[k] = e.target.checked; } })),
          h('td.mono', m.source), h('td.arrow', icon('arrow')), h('td.mono', m.target), h('td.muted', m.reason))))));
    MS.modal({
      title: '이름으로 자동 매칭',
      width: 620,
      body,
      buttons: [{ label: '취소' }, {
        label: '추가', primary: true, icon: 'add',
        onClick: () => {
          const added = matches.filter((m, k) => checks[k]).map((m) => createMapping(m.source, m.target));
          if (!added.length) return;
          S.ui.selMapping = added[added.length - 1].id;
          MS.app.changed({});
          MS.toast('테이블 매핑 ' + added.length + '개 추가 · 컬럼 자동 매핑함', 'ok');
        }
      }]
    });
  }

  /** 매핑 추가: 원본 종류(테이블 | SQL)를 고른다 */
  function addDialog(presetSource, presetType, presetTarget) {
    const src = S.meta('source');
    const tgt = S.meta('target');
    if (!src || !tgt) {
      MS.toast('메타데이터를 먼저 불러오세요', 'warn');
      return;
    }
    let type = presetType || 'TABLE';
    const used = new Set(S.job.mappings.filter((m) => m.sourceType !== 'SQL').map((m) => m.source));
    const srcOpts = [
      { group: '매핑 안 됨', options: src.tables.filter((t) => !used.has(t.name)).map((t) => ({ value: t.name, label: t.name + (t.rows != null ? '  (' + fmt.n(t.rows) + '행)' : '  (뷰)') })) },
      { group: '이미 매핑함(다른 대상에 또)', options: src.tables.filter((t) => used.has(t.name)).map((t) => ({ value: t.name, label: t.name })) }
    ];
    let source = presetSource || (srcOpts[0].options[0] || srcOpts[1].options[0] || {}).value;
    const hint = h('div.field-hint');
    const tgtSel = UI.select(tgt.tables.map((t) => ({ value: t.name, label: t.name + '  (' + fmt.n(t.rows) + '행)' })), '', null, { label: '대상 테이블' });
    const modeSel = UI.select(M.MODES.map((m) => ({ value: m.value, label: m.label })), 'MERGE', null, { label: '이관 방식' });
    const suggest = () => {
      const s = type === 'TABLE' && M.suggestTable(source, tgt.tables, S.job.mappings.filter((m) => m.sourceType !== 'SQL'));
      if (s) {
        tgtSel.value = s.target;
        hint.textContent = '추천: ' + s.target + ' — ' + s.reason;
      } else hint.textContent = type === 'TABLE' ? '이름이 맞는 대상이 없습니다. 직접 고르세요.' : '';
      const t = tgt.tables.find((x) => x.name === tgtSel.value);
      modeSel.value = t && t.rows ? 'MERGE' : 'INSERT_ONLY';
    };
    tgtSel.addEventListener('change', () => { const t = tgt.tables.find((x) => x.name === tgtSel.value); modeSel.value = t && t.rows ? 'MERGE' : 'INSERT_ONLY'; });
    const srcSel = UI.select(srcOpts, source, (v) => { source = v; suggest(); }, { label: '원본 테이블' });
    const sqlName = UI.input('SQLMAP_' + (S.sqlMappings().length + 1), null, { mono: true, label: 'SQL 원본 이름' });
    const startSel = UI.select([{ value: '', label: '빈 SELECT 문' }].concat(src.tables.filter((t) => t.kind === 'TABLE').map((t) => ({ value: t.name, label: t.name + '의 열로 시작' }))), source || '', null, { label: '시작 SQL' });
    const typeHost = h('div');
    const paintType = () => {
      typeHost.replaceChildren(type === 'TABLE'
        ? h('div.form', UI.field('원본 테이블 (' + src.schema + ')', srcSel, { required: true, wide: true }))
        : h('div.form',
          UI.field('SQL 원본 이름', sqlName, { required: true, hint: '목록·로그·체크포인트에 쓰는 이름' }),
          UI.field('시작 SQL', startSel, { hint: '고른 테이블의 열 목록과 PK 체크포인트 조건으로 시작' }),
          h('div.field.wide', UI.notice('info', 'info', '추가하면 SQL 원본 편집기가 열립니다. JOIN·집계·CASE/DECODE·서브쿼리를 쓸 수 있고, 결과 별칭이 원본 컬럼이 됩니다.'))));
      suggest();
    };
    const typeSeg = h('div');
    const paintSeg = () => typeSeg.replaceChildren(UI.seg([{ value: 'TABLE', label: '테이블' }, { value: 'SQL', label: 'SQL (SELECT 문)' }], type, (v) => { type = v; paintSeg(); paintType(); }, '원본 종류'));
    paintSeg();
    paintType();
    if (presetTarget) {
      tgtSel.value = presetTarget;
      tgtSel.dispatchEvent(new Event('change'));
    }
    MS.modal({
      title: '매핑 추가',
      body: h('div', { style: { display: 'grid', gap: '12px' } },
        UI.field('원본 종류', typeSeg),
        typeHost,
        h('div.form',
          UI.field('대상 테이블 (' + tgt.schema + ')', tgtSel, { required: true, wide: true }),
          h('div.field.wide', hint),
          UI.field('이관 방식', modeSel, { wide: true, hint: '대상에 행이 있으면 INSERT + UPDATE(MERGE)를 권합니다' }))),
      buttons: [{ label: '취소' }, {
        label: '추가', primary: true, icon: 'add',
        onClick: () => {
          if (!tgtSel.value) return false;
          if (type === 'SQL') {
            const name = sqlName.value.trim().toUpperCase().replace(/[^A-Z0-9_$#]/g, '_');
            if (!name) return false;
            if (S.sqlMappings().some((m) => m.source === name)) { MS.toast('같은 이름의 SQL 원본이 있습니다', 'warn'); return false; }
            const m = createSqlMapping(name, tgtSel.value, modeSel.value, startSel.value || null);
            S.ui.selMapping = m.id;
            S.ui.sqlTab = 'check';
            MS.app.changed({});
            setTimeout(() => MS.sqlEditor.open(m.id), 0);
            MS.toast('SQL 원본 ' + name + ' → ' + m.target + ' 추가 · SELECT 문을 쓰고 검증하세요', 'ok');
            return;
          }
          if (!srcSel.value) return false;
          if (S.job.mappings.some((m) => m.sourceType !== 'SQL' && m.source === srcSel.value && m.target === tgtSel.value)) {
            MS.toast('같은 매핑이 이미 있습니다', 'warn');
            return false;
          }
          const m = createMapping(srcSel.value, tgtSel.value, modeSel.value);
          S.ui.selMapping = m.id;
          const st = M.mappingStatus(m, S.sourceOf(m), S.tgtTable(m.target));
          MS.app.changed({});
          MS.toast(m.source + ' → ' + m.target + ' 추가 · 컬럼 ' + st.mapped + '/' + st.total + ' 자동 매핑', 'ok');
        }
      }]
    });
  }

  async function removeSelected() {
    const m = S.mapping(S.ui.selMapping);
    if (!m) {
      MS.toast('지울 매핑을 고르세요', 'warn');
      return;
    }
    if (!(await MS.confirmBox('매핑 삭제', MS.validation.labelOf(m) + ' 매핑과 컬럼 매핑 ' + m.columns.length + '개를 지울까요?', '삭제', 'danger'))) return;
    S.job.mappings = S.job.mappings.filter((x) => x !== m);
    delete S.job.checkpoints[m.id];
    delete S.session.sqlCheck[m.id];
    S.ui.selMapping = null;
    MS.app.changed({});
  }

  function sourceCell(m, src) {
    if (m.sourceType !== 'SQL') return h('td', h('div.cell-main.mono', m.source), h('div.cell-sub', src ? src.comment : '원본에 없음'));
    const bad = !src || (src.error && !src.columns.length);
    return h('td',
      h('div.row', { style: { gap: '6px' } }, h('span.tag', { style: { color: 'var(--syn-fn)', borderColor: 'currentColor' } }, 'SQL'),
        h('button.btn.link.mono', { type: 'button', style: { fontWeight: 600, color: 'var(--text)' }, title: 'SQL 원본 편집기에서 SELECT 문 편집', onClick: (e) => { e.stopPropagation(); MS.sqlEditor.open(m.id); } }, m.source)),
      h('div', { class: cx('cell-sub', bad && 'err-text'), style: { maxWidth: '260px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }, title: src ? src.comment : '' },
        bad ? (src && src.error ? src.error.msg || src.comment : 'SQL을 검증하세요') : src.comment + ' · 결과 열 ' + src.columns.length));
  }

  function row(m) {
    const src = S.sourceOf(m);
    const tgt = S.tgtTable(m.target);
    const st = M.mappingStatus(m, src && src.columns.length ? src : null, tgt);
    const mode = M.modeOf(m.mode);
    const tgtTables = (S.meta('target') || { tables: [] }).tables;
    const stop = (e) => e.stopPropagation();
    const tgtSel = UI.select([{ value: '', label: '— 고르세요' }].concat(tgtTables.map((t) => ({ value: t.name, label: t.name }))), m.target || '', (v) => {
      m.target = v;
      const t = S.tgtTable(v);
      if (t && src) m.columns = M.autoMapColumns(src.columns, t.columns);
      m.mergeKey = t ? t.columns.filter((c) => c.pk).map((c) => c.name) : [];
      MS.app.changed({});
      MS.toast('대상을 바꿔 컬럼을 다시 자동 매핑했습니다');
    }, { cell: true, label: '대상 테이블' });
    const modeSel = UI.select(M.MODES.map((x) => ({ value: x.value, label: x.label })), m.mode, (v) => {
      m.mode = v;
      if (M.modeOf(v).needsKey && !m.mergeKey.length && tgt) m.mergeKey = tgt.columns.filter((c) => c.pk).map((c) => c.name);
      MS.app.changed({});
    }, { cell: true, label: '이관 방식' });
    const keyOpts = tgt ? tgt.columns.map((c) => ({ value: c.name, label: c.name + (c.pk ? ' (PK)' : '') })) : [];
    const keySel = mode.needsKey
      ? UI.select([{ value: '', label: '— 고르세요' }].concat(keyOpts), (m.mergeKey || [])[0] || '', (v) => { m.mergeKey = v ? [v] : []; MS.app.changed({}); }, { cell: true, label: '병합 키' })
      : h('span.faint', { title: mode.label + '에는 키가 필요 없음' }, '—');
    for (const el of [tgtSel, modeSel, keySel]) el.addEventListener('click', stop);
    const use = h('input', { type: 'checkbox', checked: m.use, 'aria-label': '사용', onClick: stop, onChange: (e) => { m.use = e.target.checked; MS.app.changed({}); } });
    const level = !src || (m.sourceType === 'SQL' && src.error) ? 'ERROR' : st.level === 'PASS' || st.level === 'INFO' ? 'PASS' : st.level;
    const target = S.conn('target');
    return h('tr', {
      class: cx('clickable', S.ui.selMapping === m.id && 'sel', !m.use && 'dim'),
      onClick: () => { S.ui.selMapping = m.id; MS.app.refresh(); },
      onDblclick: () => MS.app.go('columns', { selMapping: m.id, selColumn: null })
    },
    h('td.w-check', use),
    sourceCell(m, src),
    h('td.num', { title: m.sourceType === 'SQL' ? '추정(주 테이블 통계 − 체크포인트 조건)' : '통계(NUM_ROWS)' }, src && src.rows != null ? (m.sourceType === 'SQL' ? '~' : '') + fmt.n(src.rows) : '—'),
    h('td.arrow', icon('arrow')),
    h('td', { style: { minWidth: '132px' } }, tgtSel, tgt && h('div.cell-sub', { style: { paddingLeft: '5px' } }, tgt.rows ? '기존 ' + fmt.n(tgt.rows) + '행' : '비어 있음')),
    h('td', { style: { minWidth: '132px' } }, modeSel, mode.destructive && target && target.color === 'red' && h('div.cell-sub.warn-text', { style: { paddingLeft: '5px' } }, '운영 DB · 되돌릴 수 없음')),
    h('td', { style: { minWidth: '110px' } }, keySel, mode.needsKey && m.mergeKey.length && h('div.cell-sub', { style: { paddingLeft: '5px' } }, '← ' + m.mergeKey.map((k) => { const c = m.columns.find((x) => x.target === k); return c && c.source ? c.source : '?'; }).join(', '))),
    h('td', { style: { minWidth: '96px' } },
      h('div.row', { style: { gap: '6px' } }, h('span.num', { style: { fontWeight: 600 } }, st.mapped + '/' + st.total), UI.bar(st.total ? st.mapped / st.total * 100 : 0, st.errors ? 'failed' : st.mapped === st.total ? 'done' : '')),
      h('button.btn.link', { type: 'button', onClick: (e) => { e.stopPropagation(); MS.app.go('columns', { selMapping: m.id, selColumn: null }); } }, '편집 ›')),
    h('td', UI.badge(level, level === 'PASS' ? 'OK' : level), h('div.cell-sub', st.errors ? '오류 ' + st.errors : st.warns ? '경고 ' + st.warns : '')));
  }

  function explorer() {
    const src = S.meta('source');
    const tgt = S.meta('target');
    if (!src || !tgt) return UI.card({ title: '스키마 탐색', body: UI.empty('table', '메타데이터 없음', '접속 화면에서 원본·대상 메타데이터를 불러오세요.', UI.btn('접속으로', { onClick: () => MS.app.go('connection') })) });
    const bySrc = {};
    const byTgt = {};
    for (const m of S.job.mappings) {
      if (m.sourceType !== 'SQL') (bySrc[m.source] = bySrc[m.source] || []).push(m);
      else {
        // SQL 원본이 읽는 테이블도 "쓰임"으로 표시
        const v = S.sourceOf(m);
        for (const t of (v && v.parsed ? v.parsed.tables : [])) if (t.name) (bySrc[t.name] = bySrc[t.name] || []).push(m);
      }
      if (m.target) (byTgt[m.target] = byTgt[m.target] || []).push(m);
    }
    const name = (x, side) => (side === 'src' ? (x.sourceType === 'SQL' ? 'SQL ' + x.source : x.target) : (x.sourceType === 'SQL' ? 'SQL ' : '') + x.source);
    const list = (meta, map, side) => h('ul.obj-list', meta.tables.map((t) => {
      const ms = map[t.name] || [];
      return h('li', { title: t.comment },
        h('span.kind', t.kind === 'VIEW' ? 'VIEW' : 'TBL'),
        h('span.mono.ellipsis', { style: { fontSize: '12px' } }, t.name),
        ms.length
          ? h('button.btn.link.ellipsis', { type: 'button', style: { fontSize: '11.5px', minWidth: 0 }, title: ms.map((x) => MS.validation.labelOf(x)).join('\n'), onClick: () => { S.ui.selMapping = ms[0].id; MS.app.refresh(); } }, (side === 'src' ? '→ ' : '← ') + ms.map((x) => name(x, side)).join(', '))
          : side === 'src' && t.kind === 'TABLE' ? UI.btn('매핑', { sm: true, icon: 'add', kind: 'ghost', title: t.name + ' 매핑 추가', onClick: () => addDialog(t.name) }) : h('span.faint', { style: { fontSize: '11px' } }, side === 'tgt' ? '매핑 없음' : ''),
        h('span.rows', t.rows != null ? fmt.short(t.rows) : ''));
    }));
    const unmapped = src.tables.filter((t) => t.kind === 'TABLE' && !bySrc[t.name]).length;
    return UI.card({
      title: [icon('list'), '스키마 탐색'],
      sub: unmapped ? '매핑 안 된 원본 테이블 ' + unmapped + '개' : '원본 테이블을 모두 매핑함',
      tools: [UI.btn('SQL 원본 추가', { icon: 'code', sm: true, kind: 'ghost', onClick: () => addDialog(null, 'SQL') })],
      body: h('div.explorer-lists',
        h('div', h('div.section-label', '원본 · ' + src.schema + ' (' + src.tables.length + ')'), list(src, bySrc, 'src')),
        h('div', h('div.section-label', '대상 · ' + tgt.schema + ' (' + tgt.tables.length + ')'), list(tgt, byTgt, 'tgt')))
    });
  }

  function gridCard() {
    const match = (m) => !filter || (m.source + ' ' + m.target).toLowerCase().includes(filter.toLowerCase());
    const search = UI.input(filter, (v) => {
      filter = v;
      const tbody = document.getElementById('tm-body');
      if (tbody) tbody.replaceChildren(...S.job.mappings.filter(match).map(row));
    }, { placeholder: '원본·대상 이름 찾기', label: '매핑 찾기' });
    search.style.width = '160px';
    const total = S.job.mappings.reduce((a, m) => a + (m.use ? ((S.sourceOf(m) || {}).rows || 0) : 0), 0);
    const sqlCount = S.sqlMappings().length;
    return UI.card({
      title: [icon('swap'), '테이블 매핑'],
      sub: S.job.mappings.length + '개' + (sqlCount ? '(SQL 원본 ' + sqlCount + ')' : '') + ' · 사용 중 원본 ' + fmt.n(total) + '행',
      tools: [
        UI.btn('매핑 추가', { icon: 'add', onClick: () => addDialog() }),
        UI.btn('SQL 원본 추가', { icon: 'code', onClick: () => addDialog(null, 'SQL') }),
        UI.btn('삭제', { icon: 'del', disabled: !S.mapping(S.ui.selMapping), onClick: removeSelected }),
        h('span.ib-sep'),
        UI.btn('JSON 가져오기', { icon: 'importFile', kind: 'ghost', onClick: MS.actions.importTemplate, title: '매핑 템플릿(JSON) 가져오기' }),
        UI.btn('JSON 내보내기', { icon: 'exportFile', kind: 'ghost', onClick: MS.actions.exportTemplate, title: '매핑 템플릿(JSON) 내보내기' }),
        search
      ],
      body: S.job.mappings.length
        ? h('div.grid-wrap', h('table.grid', { 'aria-label': '테이블 매핑' },
          h('thead', h('tr',
            h('th.w-check', { title: '이번 작업에서 사용' }, '사용'), h('th', '원본 (테이블 · SQL)'), h('th.num', '행 수'), h('th.arrow', ''),
            h('th', '대상 테이블'), h('th', '이관 방식'), h('th', '병합 키'), h('th', '컬럼 매핑'), h('th', '상태'))),
          h('tbody#tm-body', S.job.mappings.filter(match).map(row))))
        : UI.empty('swap', '매핑이 없습니다', '이름이 비슷한 테이블을 자동으로 맞추거나, 테이블 또는 SQL 원본을 직접 추가하세요.',
          h('div.row', UI.btn('이름으로 자동 매칭', { icon: 'magic', primary: true, onClick: autoMatch }), UI.btn('매핑 추가', { icon: 'add', onClick: () => addDialog() }), UI.btn('SQL 원본 추가', { icon: 'code', onClick: () => addDialog(null, 'SQL') }))),
      flush: true,
      foot: h('span.muted', { style: { fontSize: '12px' } }, '원본은 테이블 또는 SQL(SELECT 결과를 테이블처럼) · INSERT ONLY · INSERT + UPDATE(MERGE) · TRUNCATE + INSERT · DELETE + INSERT — 행을 두 번 누르면 컬럼 매핑')
    });
  }

  MS.pages.tables = {
    autoMatch,
    addDialog,
    render() {
      const sel = S.mapping(S.ui.selMapping);
      return MS.app.frame({
        key: 'tables',
        title: '테이블 매핑',
        desc: '원본(테이블 또는 SQL)과 대상 테이블을 잇습니다. 이름·구조가 달라도 되고, SQL 원본이면 JOIN·집계 결과를 테이블처럼 옮깁니다.',
        actions: [UI.btn('이름으로 자동 매칭', { icon: 'magic', onClick: autoMatch })],
        body: h('div.split.explorer-side', gridCard(), explorer()),
        hint: sel ? '고른 매핑: ' + MS.validation.labelOf(sel) : ''
      });
    }
  };
})();
