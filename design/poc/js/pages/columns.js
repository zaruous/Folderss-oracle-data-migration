/*
 * ③ 컬럼 매핑: 대상 컬럼마다 원본 컬럼·변환식(Transform)·NULL 처리·키를 정한다.
 * 오른쪽(좁으면 아래) 검사기에서 변환식을 고치면 샘플 6행으로 결과를 바로 보여 준다.
 * 아래 "생성 SQL"은 이 매핑으로 엔진이 실제로 쓰는 원본 SELECT와 대상 쓰기 문이다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const { h, cx, icon, fmt } = MS;
  const UI = MS.ui;
  const S = MS.store;
  const M = MS.mapping;
  const X = MS.expr;

  let sqlTab = 'select';

  function current() {
    let m = S.mapping(S.ui.selMapping);
    if (!m && S.job.mappings.length) {
      m = S.job.mappings[0];
      S.ui.selMapping = m.id;
    }
    return m;
  }

  function cmOf(m, target) {
    let cm = m.columns.find((c) => c.target === target.name);
    if (!cm) {
      cm = { target: target.name, source: null, expr: '', nullRule: M.defaultNullRule(target), defaultValue: '', key: false };
      m.columns.push(cm);
    }
    return cm;
  }

  function autoMap() {
    const m = current();
    if (!m) return;
    const src = S.sourceOf(m);
    const tgt = S.tgtTable(m.target);
    if (!src || !tgt) return;
    const fresh = M.autoMapColumns(src.columns, tgt.columns);
    const apply = (all) => {
      let n = 0;
      for (const f of fresh) {
        const cm = cmOf(m, tgt.columns.find((t) => t.name === f.target));
        if (!all && M.valueSource(cm)) continue;
        if (!f.source) continue;
        Object.assign(cm, { source: f.source, expr: f.expr, nullRule: f.nullRule, defaultValue: f.defaultValue });
        n++;
      }
      MS.app.changed({});
      MS.toast('자동 매핑: ' + n + '개 컬럼 (같은 이름 · 용어 사전 · 접미어 규칙)', n ? 'ok' : 'warn');
    };
    const has = m.columns.some((c) => M.valueSource(c));
    if (!has) return apply(true);
    MS.modal({
      title: '이름으로 자동 매핑',
      body: h('div', { style: { display: 'grid', gap: '8px' } },
        h('p', { style: { margin: 0 } }, '이미 매핑한 컬럼이 있습니다. 어떻게 할까요?'),
        h('table.grid.compact', h('tbody', fresh.filter((f) => f.source).map((f) => h('tr', h('td.mono', f.source), h('td.arrow', icon('arrow')), h('td.mono', f.target), h('td.muted', f.reason), h('td', f.expr ? h('span.expr', f.expr.split('\n')[0]) : '')))))),
      buttons: [{ label: '취소' }, { label: '모두 다시 매핑', onClick: () => apply(true) }, { label: '빈 컬럼만 채우기', primary: true, onClick: () => apply(false) }]
    });
  }

  function clearAll() {
    const m = current();
    MS.confirmBox('매핑 지우기', m.source + ' → ' + m.target + '의 컬럼 매핑을 모두 지울까요?', '지우기', 'danger').then((ok) => {
      if (!ok) return;
      for (const c of m.columns) Object.assign(c, { source: null, expr: '' });
      MS.app.changed({});
    });
  }

  // ================= 표 =================
  function grid(m, src, tgt) {
    const st = M.mappingStatus(m, src, tgt);
    const filt = S.ui.colFilter;
    const visible = st.results.filter((r) => filt === 'all' || (filt === 'unmapped' ? !M.valueSource(r.cm) : ['WARN', 'ERROR'].includes(r.check.level)));
    const keys = new Set(m.mergeKey || []);
    const needKey = M.modeOf(m.mode).needsKey;
    const rows = visible.map((r) => {
      const cm = r.cm;
      const t = r.target;
      const info = M.sourceInfo(cm, src.columns);
      const stop = (e) => e.stopPropagation();
      const srcSel = UI.select([{ value: '', label: '(매핑 안 함)' }].concat(src.columns.map((c) => ({ value: c.name, label: c.name }))), cm.source || '', (v) => {
        const prev = cm.source;
        cm.source = v || null;
        // 식이 옛 컬럼만 참조하면 새 컬럼으로 바꾸고, 비우면 식도 비운다
        if (!v) cm.expr = '';
        else if (prev && cm.expr && X.compile(cm.expr).refs.every((x) => x === prev)) cm.expr = cm.expr.replace(new RegExp('\\b' + prev + '\\b', 'g'), v);
        else if (!prev) cm.expr = M.suggestExpr(src.columns.find((c) => c.name === v), t);
        S.ui.selColumn = t.name;
        MS.app.changed({});
      }, { cell: true, label: t.name + ' 원본 컬럼' });
      const ruleSel = UI.select(M.NULL_RULES, cm.nullRule, (v) => { cm.nullRule = v; S.ui.selColumn = t.name; MS.app.changed({}); }, { cell: true, label: t.name + ' NULL 처리' });
      const key = h('input', {
        type: 'checkbox', checked: keys.has(t.name), disabled: !needKey, 'aria-label': t.name + ' 병합 키',
        title: needKey ? '병합 키(MERGE ON / DELETE 조건)' : M.modeOf(m.mode).label + '에는 키가 필요 없음',
        onChange: (e) => {
          m.mergeKey = e.target.checked ? (m.mergeKey || []).concat(t.name) : (m.mergeKey || []).filter((k) => k !== t.name);
          MS.app.changed({});
        }
      });
      for (const el of [srcSel, ruleSel, key]) el.addEventListener('click', stop);
      const expr = cm.expr && cm.expr.trim();
      const level = r.check.level === 'INFO' ? 'INFO' : r.check.level;
      return h('tr', {
        class: cx('clickable', S.ui.selColumn === t.name && 'sel'),
        onClick: () => { S.ui.selColumn = t.name; MS.app.refresh(); }
      },
      h('td', { style: { minWidth: '128px' } }, srcSel, h('div.type', { style: { paddingLeft: '5px' } }, cm.source ? (src.columns.find((c) => c.name === cm.source) || {}).type || '' : '')),
      h('td.arrow', icon('arrow')),
      h('td', { style: { minWidth: '136px' } },
        h('div.row', { style: { gap: '5px' } }, h('span.cell-main.mono', t.name), t.pk && h('span.tag.pk', 'PK'), !t.nullable && h('span.tag.nn', 'NN')),
        h('div.type', t.type, t.defaultValue && h('span.faint', ' DEFAULT ' + t.defaultValue))),
      h('td', { style: { maxWidth: '200px' } }, expr ? h('span.expr', { title: expr }, expr.replace(/\s+/g, ' ')) : h('span.expr.none', cm.source ? '그대로' : '—'),
        info && info.type && expr && h('div.type', '→ ' + info.type)),
      h('td', { style: { minWidth: '96px' } }, ruleSel, ['DEFAULT', 'CUSTOM'].includes(cm.nullRule) && h('div.type', { style: { paddingLeft: '5px' } }, cm.defaultValue ? (cm.nullRule === 'DEFAULT' ? "'" + cm.defaultValue + "'" : cm.defaultValue) : h('span.warn-text', '값 없음'))),
      h('td.w-check', key),
      h('td', { title: r.check.msgs.map((x) => x.level + ' ' + x.msg).join('\n') }, UI.badge(level, level === 'PASS' ? 'OK' : level)));
    });
    return UI.card({
      title: [icon('list'), '컬럼'],
      sub: st.mapped + ' / ' + st.total + ' 매핑' + (st.errors ? ' · 오류 ' + st.errors : '') + (st.warns ? ' · 경고 ' + st.warns : ''),
      tools: [
        UI.seg([{ value: 'all', label: '전체' }, { value: 'unmapped', label: '매핑 안 됨' }, { value: 'issues', label: '경고·오류' }], filt, (v) => { S.ui.colFilter = v; MS.app.refresh(); }, '보기'),
        UI.btn('이름으로 자동 매핑', { icon: 'magic', onClick: autoMap }),
        UI.btn('지우기', { icon: 'clear', kind: 'ghost', onClick: clearAll })
      ],
      flush: true,
      body: h('div.grid-wrap', h('table.grid', { 'aria-label': '컬럼 매핑' },
        h('thead', h('tr', h('th', (src.virtual ? 'SQL 결과 열 (' : '원본 컬럼 (') + src.name + ')'), h('th.arrow', ''), h('th', '대상 컬럼 (' + tgt.name + ')'), h('th', '변환식(Transform)'), h('th', 'NULL 처리'), h('th.w-check', '키'), h('th', '검사'))),
        h('tbody', rows.length ? rows : h('tr', h('td', { colspan: 7 }, UI.empty('check', '해당하는 컬럼이 없습니다')))))),
      foot: h('span.muted', { style: { fontSize: '12px' } }, '변환식은 원본 SELECT 안에서 Oracle이 계산합니다(서버 쪽 변환). 키는 MERGE ON·DELETE 조건에 씁니다.')
    });
  }

  // ================= 검사기 =================
  const SNIPPETS = [
    ['TRIM', (b) => 'TRIM(' + b + ')'],
    ['NVL', (b) => 'NVL(' + b + ", '')"],
    ['REGEXP_REPLACE', (b) => 'REGEXP_REPLACE(' + b + ", '[^0-9]', '')"],
    ['CASE', (b) => 'CASE\n    WHEN ' + b + " = 'A' THEN 'Y'\n    ELSE 'N'\nEND"],
    ['CAST', (b) => 'CAST(' + b + ' AS TIMESTAMP)'],
    ['TO_DATE', (b) => 'TO_DATE(' + b + ", 'YYYYMMDD')"],
    ['DECODE', (b) => 'DECODE(' + b + ", 'A', 'Y', 'N')"],
    ['SUBSTR', (b) => 'SUBSTR(' + b + ', 1, 20)'],
    ['UPPER', (b) => 'UPPER(' + b + ')']
  ];

  /** 샘플 6행: 테이블 원본은 Mock 샘플, SQL 원본은 SQL 미리보기 결과(별칭 = 열 이름) */
  function sourceRows(m) {
    if (m.sourceType !== 'SQL') return MS.mockDb.sampleRows(m.source, 6, { fromId: 850001 });
    const p = MS.sqlMapping.preview(m, S.meta('source'), 6);
    if (p.error) return [];
    return p.rows.map((r) => {
      const o = {};
      p.columns.forEach((c, k) => { o[c.name] = r[k].error ? null : r[k].value; });
      return o;
    });
  }

  function samples(m, cm, tgtCol) {
    const rows = sourceRows(m);
    const srcExpr = M.valueSource(cm);
    const comp = srcExpr ? X.compile(srcExpr) : null;
    const refs = comp && !comp.error ? comp.refs : cm.source ? [cm.source] : [];
    const tt = X.parseType(tgtCol.type);
    return rows.map((r) => {
      const input = refs.map((n) => { const v = r[n.replace(/^.*\./, '')]; return refs.length > 1 ? n + '=' + X.display(v).text : X.display(v).text; }).join(', ');
      const inNull = refs.length === 1 && r[refs[0]] == null;
      let out = null;
      let error = null;
      let note = null;
      if (srcExpr) {
        const res = X.run(srcExpr, r);
        if (res.error) error = res.error.message;
        else out = res.value;
      }
      if (!error && out == null) {
        switch (cm.nullRule) {
          case 'DEFAULT': if (cm.defaultValue) { out = cm.defaultValue; note = '기본값'; } break;
          case 'SYSDATE': out = new Date(2026, 9, 3, 14, 0, 0); note = 'SYSDATE'; break;
          case 'CUSTOM': if (cm.defaultValue) { const c = X.run(cm.defaultValue, r); out = c.error ? null : c.value; note = '사용자 식'; } break;
          default: break;
        }
        if (out == null && !tgtCol.nullable) note = '거부 → 오류 테이블 (ORA-01400)';
      }
      if (!error && out != null && X.isChar(tt) && tt.len && X.str(out).length > tt.len) error = 'ORA-12899: 값이 너무 큼(실제 ' + X.str(out).length + ', 최대 ' + tt.len + ')';
      if (!error && out instanceof Date && tt.base === 'TIMESTAMP' && !out.isTimestamp) out = Object.defineProperty(new Date(out.getTime()), 'isTimestamp', { value: true });
      return { input: refs.length ? input : '—', inNull, out, error, note, changed: !error && X.display(out).text !== input };
    });
  }

  function inspector(m, src, tgt) {
    const t = tgt.columns.find((c) => c.name === S.ui.selColumn) || tgt.columns[0];
    S.ui.selColumn = t.name;
    const cm = cmOf(m, t);
    const area = h('textarea.input.mono', { rows: 5, spellcheck: false, value: cm.expr || '', placeholder: cm.source ? '비우면 ' + cm.source + ' 값을 그대로 씁니다' : '원본 컬럼을 고르거나 식을 쓰세요 (예: TRIM(CUSTOMER_NM))', 'aria-label': t.name + ' 변환식' });
    const live = h('div', { style: { display: 'grid', gap: '12px' } });
    const paintLive = () => live.replaceChildren(checkBlock(m, cm, t, src), sampleBlock(m, cm, t));
    area.addEventListener('input', () => {
      cm.expr = area.value;
      paintLive();
    });
    area.addEventListener('change', () => MS.app.changed({}));
    area.addEventListener('keydown', (e) => {
      if (e.key === 'Tab' && !e.ctrlKey) { e.preventDefault(); document.execCommand('insertText', false, '    '); }
    });
    const base = () => (area.value.trim() || cm.source || 'COL').replace(/\n/g, '\n    ');
    const chips = h('div.chips', SNIPPETS.map(([name, f]) => h('button.chip', {
      type: 'button', title: '지금 식을 ' + name + '(…)로 감쌉니다',
      onClick: () => { area.value = f(base()); cm.expr = area.value; paintLive(); MS.app.changed({}); }
    }, name)));
    const cols = h('div.chips', src.columns.map((c) => h('button.chip', {
      type: 'button', style: { color: 'var(--text)' }, title: c.type + ' — 커서 위치에 넣기',
      onClick: () => { area.focus(); document.execCommand('insertText', false, c.name); }
    }, c.name)));
    const defInput = ['DEFAULT', 'CUSTOM'].includes(cm.nullRule)
      ? UI.field(cm.nullRule === 'DEFAULT' ? '기본값' : '사용자 식', UI.input(cm.defaultValue, (v) => { cm.defaultValue = v; paintLive(); }, { mono: true, label: '기본값', onChange: () => MS.app.changed({}) }), { hint: cm.nullRule === 'DEFAULT' ? "문자는 따옴표 없이 쓰면 '…'로 감쌉니다" : '예: TO_CHAR(SYSDATE, \'YYYY\')' })
      : null;
    const unmappedHint = !M.valueSource(cm) && t.nullable
      ? (m.sourceType === 'SQL'
        ? UI.notice('info', 'info', h('div', t.name + '에 맞는 결과 열이 SQL에 없습니다. SELECT 목록에 열을 더하세요.'),
          h('button.btn.link', { type: 'button', onClick: () => MS.sqlEditor.open(m.id) }, 'SQL 원본 편집기 ›'))
        : UI.notice('info', 'info', h('div', t.name + '에 맞는 값이 ' + m.source + '에 없습니다. 다른 테이블과 JOIN해야 하면 원본을 SQL로 만드세요.'),
          h('button.btn.link', { type: 'button', onClick: () => MS.pages.tables.addDialog(m.source, 'SQL', m.target) }, 'SQL 원본으로 만들기 ›')))
      : null;
    paintLive();
    return UI.card({
      cls: 'accent-left',
      title: [h('span.mono', t.name), h('span.faint', '←'), h('span.mono.muted', cm.source || '(없음)')],
      sub: t.type + (t.nullable ? '' : ' · NOT NULL') + (t.comment ? ' · ' + t.comment : ''),
      body: h('div', { style: { display: 'grid', gap: '12px' } },
        unmappedHint,
        h('div', h('div.section-label', '변환식 (Transform Expression)'), area, h('div', { style: { marginTop: '6px', display: 'grid', gap: '6px' } }, chips, h('div.field-hint', '원본 컬럼'), cols)),
        h('div.form',
          UI.field('NULL 처리', UI.select(M.NULL_RULES, cm.nullRule, (v) => { cm.nullRule = v; MS.app.changed({}); }, { label: 'NULL 처리' }), { hint: { ALLOW: 'NULL 그대로', REJECT: '행을 오류 테이블로', DEFAULT: 'NVL(식, 기본값)', SYSDATE: 'NVL(식, SYSDATE)', EMPTY: "Oracle에선 ''도 NULL", CUSTOM: 'NVL(식, 사용자 식)' }[cm.nullRule] }),
          defInput || h('div')),
        live)
    });
  }

  function checkBlock(m, cm, t, src) {
    const r = M.checkColumn(cm, t, src.columns, { mode: m.mode });
    return h('div', h('div.section-label', '검사'),
      h('div', { style: { display: 'grid', gap: '4px' } }, r.msgs.map((x) => h('div.row', { style: { alignItems: 'flex-start' } }, UI.badge(x.level), h('span', { style: { fontSize: '12px' } }, x.msg)))));
  }

  function sampleBlock(m, cm, t) {
    const list = samples(m, cm, t);
    return h('div', h('div.section-label', '샘플 미리보기 (원본 6행)'),
      h('div.sample',
        h('div.head', '원본 값'), h('div'), h('div.head', '결과 → ' + t.name),
        list.map((s) => [
          h('div', { class: cx('v', s.inNull && 'null') }, s.input),
          h('div.arr', MS.ICON.arrow),
          s.error ? h('div.v.bad', s.error)
            : h('div', { class: cx('v', s.out == null && 'null', s.changed && 'changed'), title: s.note || '' }, X.display(s.out).text, s.note && h('span.faint', { style: { fontFamily: 'var(--font)' } }, '  · ' + s.note))
        ])));
  }

  // ================= 생성 SQL =================
  function sqlCard(m, src, tgt) {
    const st = S.job.strategy;
    const select = MS.sqlgen.buildSourceSelect(m, S.job.source.schema, src, tgt, { fetchSize: st.fetchSize, workers: st.workers });
    const cols = MS.sqlgen.writeColumns(m, tgt).map((c) => c.name);
    const write = '-- 배치마다 ' + fmt.n(st.commitSize) + '행 배열 바인드 후 커밋\n' + MS.sqlgen.buildWriteSql(S.job.target.schema, tgt.name, cols, m.mode, m.mergeKey, MS.sqlgen.errorTableFor(st, tgt.name));
    const text = sqlTab === 'select' ? select : write;
    return UI.card({
      title: [icon('code'), '생성 SQL'],
      sub: '엔진이 실제로 실행하는 문장',
      tools: [UI.btn('복사', { icon: 'copy', sm: true, kind: 'ghost', onClick: () => MS.copyText(text) })],
      raw: [
        UI.tabs([{ key: 'select', label: '원본 SELECT' }, { key: 'write', label: '대상 쓰기 문 (' + M.modeOf(m.mode).label + ')' }], sqlTab, (k) => { sqlTab = k; MS.app.refresh(); }),
        UI.code(text, 'flat')
      ]
    });
  }

  function settings(m, src, tgt) {
    const numCols = src.columns.filter((c) => /^(NUMBER|DATE|TIMESTAMP)/.test(c.type || '') || c.pk);
    const where = UI.input(m.where, (v) => { m.where = v; }, { mono: true, placeholder: src.virtual ? "예: S.USE_YN = 'Y'" : "예: STATUS_CD <> 'D'", label: '원본 조건', onChange: () => MS.app.changed({}) });
    return UI.card({
      body: h('div.form.cols-3', { style: { gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))' } },
        UI.field('이관 방식', UI.select(M.MODES.map((x) => ({ value: x.value, label: x.label })), m.mode, (v) => {
          m.mode = v;
          if (M.modeOf(v).needsKey && !(m.mergeKey || []).length) m.mergeKey = tgt.columns.filter((c) => c.pk).map((c) => c.name);
          MS.app.changed({});
        }, { label: '이관 방식' })),
        UI.field('병합 키', h('div.row.wrap', { style: { minHeight: '26px' } },
          M.modeOf(m.mode).needsKey
            ? (m.mergeKey || []).length ? m.mergeKey.map((k) => h('span.key-chip', k, h('button', { type: 'button', 'aria-label': k + ' 빼기', onClick: () => { m.mergeKey = m.mergeKey.filter((x) => x !== k); MS.app.changed({}); } }, MS.ICON.close))) : h('span.err-text', { style: { fontSize: '12px' } }, '표의 "키"에서 고르세요')
            : h('span.faint', { style: { fontSize: '12px' } }, '이 방식에는 필요 없음')), { hint: M.modeOf(m.mode).needsKey ? 'MERGE ON (T.키 = S.키)' : '' }),
        UI.field('체크포인트 컬럼', UI.select([{ value: '', label: '(없음 — 재개 불가)' }].concat(numCols.map((c) => ({ value: c.name, label: c.name + '  ' + c.type + (c.pk ? ' · PK' : '') }))), m.checkpointColumn || '', (v) => { m.checkpointColumn = v || null; MS.app.changed({}); }, { label: '체크포인트 컬럼' }),
          { hint: '이 순서로 읽고 커밋마다 마지막 값을 남김' }),
        UI.field('원본 조건 (WHERE)', where, { hint: src.virtual ? 'SQL 결과(S)에 거는 조건 — SQL 안에 넣어도 됨' : '체크포인트 조건과 AND로 붙음' }))
    });
  }

  MS.pages.columns = {
    autoMap,
    render() {
      const m = current();
      const frame = (body, desc, actions) => MS.app.frame({ key: 'columns', title: '컬럼 매핑', desc, actions, body });
      if (!m) return frame(UI.card({ body: UI.empty('list', '매핑이 없습니다', '먼저 원본(테이블 또는 SQL)과 대상 테이블을 이어 주세요.', UI.btn('테이블 매핑으로', { primary: true, onClick: () => MS.app.go('tables') })) }), '대상 컬럼마다 원본 컬럼과 변환식을 정합니다.');
      const src = S.sourceOf(m);
      const tgt = S.tgtTable(m.target);
      const pick = UI.select(S.job.mappings.map((x) => {
        const s = M.mappingStatus(x, S.sourceOf(x), S.tgtTable(x.target));
        return { value: x.id, label: MS.validation.labelOf(x) + '   ' + s.mapped + '/' + s.total + (s.errors ? '  · 오류' : '') };
      }), m.id, (v) => MS.app.go('columns', { selMapping: v, selColumn: null }), { label: '매핑 고르기' });
      pick.style.minWidth = '300px';
      if (m.sourceType === 'SQL' && (!src || !src.columns.length)) return frame(UI.card({ body: UI.empty('code', 'SQL 원본의 결과 열을 알 수 없습니다', (src && src.error ? (src.error.code ? src.error.code + ': ' : '') + src.error.msg + ' — ' : '') + 'SQL 원본 편집기에서 SELECT 문을 고치고 검증하세요.', UI.btn('SQL 원본 편집기', { icon: 'code', primary: true, onClick: () => MS.sqlEditor.open(m.id) })) }), '', [pick]);
      if (!src || !tgt) return frame(UI.card({ body: UI.empty('warn', '메타데이터에 테이블이 없습니다', (src ? '' : m.source + ' ') + (tgt ? '' : (m.target || '대상 테이블 없음')) + ' — 접속 화면에서 메타데이터를 다시 불러오거나 대상을 고르세요.', UI.btn('접속으로', { onClick: () => MS.app.go('connection') })) }), '', [pick]);
      return frame([
        settings(m, src, tgt),
        h('div.split.inspector-side', grid(m, src, tgt), inspector(m, src, tgt)),
        sqlCard(m, src, tgt)
      ], h('span',
        src.virtual
          ? [h('span.tag', { style: { color: 'var(--syn-fn)', borderColor: 'currentColor', marginRight: '6px' } }, 'SQL'), h('button.btn.link.mono', { type: 'button', title: 'SQL 원본 편집기 열기', onClick: () => MS.sqlEditor.open(m.id) }, m.source), ' (결과 ' + src.columns.length + '열, ~' + fmt.n(src.rows) + '행, ' + src.comment + ')  →  ']
          : [h('span.mono', S.job.source.schema + '.' + src.name), ' (' + fmt.n(src.rows) + '행, ' + src.columns.length + '열)  →  '],
        h('span.mono', S.job.target.schema + '.' + tgt.name), ' (' + tgt.columns.length + '열' + (tgt.rows ? ', 기존 ' + fmt.n(tgt.rows) + '행' : '') + ')'), [pick]);
    }
  };
})();
