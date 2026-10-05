/*
 * ⑤ 검증: 실행 전 검증(PASS·WARN·ERROR)과 실행 후 검증(원본 ↔ 대상 비교).
 * 검사는 하나씩 끝나는 대로 표에 채운다. ERROR 줄의 [고치기 ›]는 해당 화면·컬럼으로 바로 간다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const { h, cx, icon, fmt } = MS;
  const UI = MS.ui;
  const S = MS.store;

  const ORDER = { ERROR: 0, WARN: 1, INFO: 2, SKIP: 3, PASS: 4 };

  function fixButton(fix) {
    if (!fix) return null;
    const label = { connection: '접속', tables: '테이블 매핑', columns: '컬럼', sql: 'SQL 편집', run: '실행' }[fix.page];
    return h('button.btn.link', {
      type: 'button',
      onClick: () => {
        if (fix.page === 'sql') { MS.sqlEditor.open(fix.mapping); return; }
        const params = {};
        if (fix.mapping) params.selMapping = fix.mapping;
        if (fix.column) params.selColumn = fix.column;
        if (fix.sql) params.selSql = fix.sql;
        MS.app.go(fix.page, params);
      }
    }, label + ' ›');
  }

  function summary(items, extra) {
    const c = MS.app.levelCount(items);
    const chip = (lvl) => h('span.sum', UI.badge(lvl), c[lvl]);
    return h('div.summary', chip('PASS'), chip('WARN'), chip('ERROR'), c.INFO ? chip('INFO') : null, extra);
  }

  function preTab() {
    const pre = S.session.pre;
    if (!pre) {
      return UI.empty('checklist', '아직 검증하지 않았습니다',
        '접속 · 원본/대상 테이블 · 컬럼 매핑 · 형식 호환성 · NOT NULL · PK/Unique · 중복 키 · VARCHAR 길이 · NUMBER 정밀도 · 테이블스페이스를 검사합니다.',
        UI.btn('검증 실행 (F6)', { icon: 'play', primary: true, onClick: MS.actions.runValidation }));
    }
    const onlyIssues = S.ui.valFilter === 'issues';
    const items = pre.items.filter((i) => !onlyIssues || ['ERROR', 'WARN'].includes(i.level));
    const groups = [];
    for (const i of items) {
      let g = groups.find((x) => x.name === i.group);
      if (!g) groups.push((g = { name: i.group, items: [] }));
      g.items.push(i);
    }
    const c = MS.app.levelCount(pre.items);
    const gate = pre.running ? null
      : c.ERROR ? UI.notice('err', 'error', h('b', 'ERROR ' + c.ERROR + '건 — '), '이관을 실행할 수 없습니다. 고친 뒤 다시 검증하세요. Dry Run은 할 수 있습니다.')
        : UI.notice(c.WARN ? 'warn' : 'ok', c.WARN ? 'warn' : 'check', h('b', c.WARN ? '경고를 확인했다면 실행할 수 있습니다. ' : '모두 통과했습니다. '), h('button.btn.link', { type: 'button', onClick: () => MS.app.go('run') }, '실행 화면으로 ›'));
    return h('div',
      h('div.row.wrap', { style: { padding: '10px 12px', borderBottom: '1px solid var(--border)' } },
        summary(pre.items),
        h('span.grow'),
        pre.running ? h('span.row.muted', h('span.spin'), '검사 중… ' + pre.items.length) : h('span.muted', { style: { fontSize: '12px' } }, '마지막 검증 ' + fmt.time(pre.at) + ' · ' + (pre.ms / 1000).toFixed(1) + '초'),
        pre.stale && h('span.pill.warn', icon('warn'), '검증 뒤 작업이 바뀜'),
        UI.seg([{ value: 'all', label: '전체' }, { value: 'issues', label: '문제만' }], onlyIssues ? 'issues' : 'all', (v) => { S.ui.valFilter = v; MS.app.refresh(); }, '보기')),
      pre.running && UI.bar(Math.min(96, pre.items.length / 34 * 100), 'running'),
      gate && h('div', { style: { padding: '10px 12px 0' } }, gate),
      h('div.grid-wrap', { style: { padding: '0 0 4px' } }, h('table.grid', { 'aria-label': '실행 전 검증 결과' },
        h('thead', h('tr', h('th', { style: { width: '78px' } }, '결과'), h('th', { style: { width: '150px' } }, '검사 항목'), h('th', { style: { width: '200px' } }, '대상'), h('th', '내용'), h('th', { style: { width: '90px' } }, '조치'))),
        h('tbody', groups.map((g) => [
          h('tr.group', h('td', { colspan: 5 }, g.name, h('span.faint', '  ' + g.items.length))),
          g.items.slice().sort((a, b) => ORDER[a.level] - ORDER[b.level]).map((i) => h('tr',
            h('td', UI.badge(i.level)),
            h('td', { style: { fontWeight: 600 } }, i.check),
            h('td.mono', { style: { fontSize: '11.5px', wordBreak: 'break-all' } }, i.target || ''),
            h('td', { style: { whiteSpace: 'pre-wrap', fontSize: '12px' } }, i.detail),
            h('td', i.level === 'PASS' ? null : fixButton(i.fix))))
        ]), pre.running && h('tr', h('td', { colspan: 5 }, h('span.row.muted', h('span.spin'), '다음 검사…')))))));
  }

  function postTab() {
    const run = S.session.run;
    const post = S.session.post;
    if (!run || ['running', 'pausing', 'paused'].includes(run.state)) {
      return UI.empty('history', '실행을 마친 뒤에 확인합니다', '행 수 · PK 누락 · 중복 키 · 샘플 데이터 · 해시 · NULL 수를 원본과 대상에서 비교합니다.',
        UI.btn('실행 화면으로', { onClick: () => MS.app.go('run') }));
    }
    if (!post || post.runId !== run.runId) {
      return UI.empty('history', '실행 ' + run.runId + ' 결과를 검증할 수 있습니다', (run.dry ? 'Dry Run은 대상에 쓰지 않아 행 수 비교를 건너뜁니다.' : '대상에서 COUNT·MINUS·ORA_HASH 집계를 실행합니다(대상 부하가 적은 시간에 권장).'),
        UI.btn('실행 후 검증', { icon: 'play', primary: true, onClick: MS.actions.runPostValidation }));
    }
    const groups = [];
    for (const i of post.items) {
      let g = groups.find((x) => x.name === i.group);
      if (!g) groups.push((g = { name: i.group, items: [] }));
      g.items.push(i);
    }
    return h('div',
      h('div.row.wrap', { style: { padding: '10px 12px', borderBottom: '1px solid var(--border)' } },
        summary(post.items), h('span.grow'),
        post.running ? h('span.row.muted', h('span.spin'), '비교 중…') : h('span.muted', { style: { fontSize: '12px' } }, '실행 ' + run.runId + ' · ' + fmt.time(post.at)),
        UI.btn('다시 검증', { icon: 'refresh', sm: true, disabled: post.running, onClick: MS.actions.runPostValidation })),
      h('div.grid-wrap', h('table.grid', { 'aria-label': '실행 후 검증 결과' },
        h('thead', h('tr', h('th', { style: { width: '78px' } }, '결과'), h('th', { style: { width: '150px' } }, '검사'), h('th.num', '원본'), h('th.num', '대상'), h('th', '내용'))),
        h('tbody', groups.map((g) => [
          h('tr.group', h('td', { colspan: 5 }, g.name)),
          g.items.map((i) => h('tr',
            h('td', UI.badge(i.level === 'SKIP' ? 'SKIP' : i.level, i.level === 'PASS' && i.check === '행 수' ? 'MATCH' : null)),
            h('td', { style: { fontWeight: 600 } }, i.check),
            h('td.num.mono', i.source),
            h('td.num.mono', i.target),
            h('td', { style: { fontSize: '12px', whiteSpace: 'pre-wrap' } }, i.detail)))
        ])))));
  }

  MS.pages.validation = {
    update() {
      MS.app.refresh();
    },
    render() {
      const pre = S.session.pre;
      const tab = S.ui.valTab;
      const run = S.session.run;
      return MS.app.frame({
        key: 'validation',
        title: '검증',
        desc: '실제 이관 전에 접속·객체·매핑·형식·제약·공간을 검사하고(PASS · WARN · ERROR), 이관 뒤에는 원본과 대상을 비교합니다.',
        actions: [
          UI.btn(pre && pre.running ? '검증 중…' : '실행 전 검증 (F6)', { icon: 'checklist', primary: true, disabled: pre && pre.running, onClick: MS.actions.runValidation }),
          UI.btn('실행 후 검증', { icon: 'history', disabled: !run || !['done', 'stopped', 'failed'].includes(run.state), onClick: MS.actions.runPostValidation })
        ],
        body: UI.card({
          raw: [
            UI.tabs([
              { key: 'pre', label: '실행 전 검증', count: pre ? pre.items.length : null },
              { key: 'post', label: '실행 후 검증', count: S.session.post ? S.session.post.items.length : null }
            ], tab, (k) => { S.ui.valTab = k; MS.app.refresh(); }),
            tab === 'post' ? postTab() : preTab()
          ]
        }),
        hint: '검사에 쓰는 SQL은 기능 설계서 UI-MIG-005에 정리'
      });
    }
  };
})();
