/*
 * 마이그레이션 설정 대화상자: 접속 · 기본값 · 실행 에이전트.
 * 플러그인 자체 설정이다(DB Helper 접속과 공유하지 않음). WPF 구현에서는 같은 내용을
 * Folderss 설정 창의 플러그인 탭(IPluginSettingsPage)과 플러그인 안의 [마이그레이션 설정…] 두 곳에서 연다.
 * 고친 내용은 사본에 모았다가 [저장]할 때 한 번에 반영한다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const { h, cx, icon } = MS;
  const UI = MS.ui;
  const S = MS.store;
  const SM = MS.settingsModel;

  const COLOR_OPTIONS = [
    { value: '', label: '없음' },
    { value: 'green', label: '초록 — 개발' },
    { value: 'yellow', label: '노랑 — 검증' },
    { value: 'red', label: '빨강 — 운영' }
  ];

  function open(tab, profileId) {
    const draft = JSON.parse(JSON.stringify(S.settings));
    let current = tab || 'connections';
    let sel = profileId || (S.conn('source') || {}).id || (draft.connections[0] || {}).id;
    const tests = {};
    const host = h('div');

    const usedBy = (id) => ['source', 'target'].filter((r) => S.job[r].profileId === id).map((r) => (r === 'source' ? '원본' : '대상'));

    function connectionsTab() {
      const p = draft.connections.find((c) => c.id === sel) || draft.connections[0];
      if (p) sel = p.id;
      const list = h('ul.obj-list', { style: { maxHeight: '340px' }, role: 'listbox', 'aria-label': '접속 목록' }, draft.connections.map((c) => h('li', {
        role: 'option', 'aria-selected': String(c.id === sel), class: cx(c.id === sel && 'sel'), style: { cursor: 'pointer', background: c.id === sel ? 'var(--selection)' : null },
        onClick: () => { sel = c.id; paint(); }
      },
      h('span', { class: cx('dot'), style: { background: c.color ? 'var(--c-' + c.color + ')' : 'var(--text3)' } }),
      h('div', { style: { minWidth: 0 } }, h('div.mono.ellipsis', { style: { fontSize: '12px', fontWeight: 600 } }, c.name), h('div.faint.ellipsis', { style: { fontSize: '11px' } }, c.host + ':' + c.port + '/' + c.service)),
      h('span.rows', usedBy(c.id).join('·'), c.writeBlocked && h('span.tag.ro', { style: { marginLeft: '4px' } }, '쓰기 금지')))));
      const add = (copy) => {
        const n = copy && p ? Object.assign({}, p, { id: MS.job.newId('cn-'), name: p.name + '_COPY' }) : SM.newProfile(draft);
        draft.connections.push(n);
        sel = n.id;
        paint();
      };
      const remove = () => {
        if (!p) return;
        const used = usedBy(p.id);
        if (used.length) { MS.toast('지금 작업의 ' + used.join('·') + ' 접속이라 지울 수 없습니다', 'warn'); return; }
        draft.connections = draft.connections.filter((c) => c !== p);
        sel = (draft.connections[0] || {}).id;
        paint();
      };
      let form = UI.empty('link', '접속이 없습니다', '[추가]로 원본·대상 접속을 만드세요.');
      if (p) {
        const set = (k) => (v) => { p[k] = v; delete tests[p.id]; };
        const errors = SM.validateProfile(p, draft.connections);
        const test = tests[p.id];
        form = h('div', { style: { display: 'grid', gap: '10px' } },
          h('div.form',
            UI.field('접속 이름', UI.input(p.name, (v) => { p.name = v.trim(); }, { mono: true, label: '접속 이름', onChange: paint }), { required: true }),
            UI.field('색 표시', UI.select(COLOR_OPTIONS, p.color || '', (v) => { p.color = v; paint(); }, { label: '색 표시' })),
            UI.field('DB 종류', UI.select(MS.adapters.KINDS.map((k) => ({ value: k.value, label: k.label, disabled: k.planned })), p.kind, set('kind'), { label: 'DB 종류' })),
            UI.field('기본 스키마', UI.input(p.defaultSchema, (v) => { p.defaultSchema = v.toUpperCase(); }, { mono: true, label: '기본 스키마', placeholder: '비우면 사용자 이름' }), { hint: '작업마다 바꿀 수 있음' }),
            UI.field('호스트', UI.input(p.host, set('host'), { mono: true, label: '호스트' }), { required: true }),
            UI.field('포트', UI.input(p.port, set('port'), { mono: true, label: '포트' }), { required: true }),
            UI.field('서비스명', UI.input(p.service, set('service'), { mono: true, label: '서비스명' }), { required: true, hint: 'EZConnect: 호스트:포트/서비스명' }),
            UI.field('사용자', UI.input(p.user, set('user'), { mono: true, label: '사용자' }), { required: true }),
            UI.field('비밀번호', UI.input(p.password, set('password'), { type: 'password', label: '비밀번호', placeholder: p.savePassword ? '' : '연결할 때마다 입력' }),
              { extra: UI.check('저장', p.savePassword, (v) => { p.savePassword = v; }, 'DPAPI로 암호화해 이 PC의 이 Windows 사용자만 풀 수 있게 저장') }),
            UI.field('안전', UI.check('쓰기 금지 (원본 전용)', p.writeBlocked, (v) => { p.writeBlocked = v; paint(); }, '이 접속은 대상으로 고를 수 없고, 원본 세션은 언제나 읽기 전용'), { hint: '운영 원본 DB에 실수로 쓰지 않게' })),
          errors.length ? UI.notice('err', 'error', errors.join(' · ')) : null,
          h('div.row.wrap',
            UI.btn('접속 테스트', {
              icon: 'link', disabled: test && test.running,
              onClick: async () => {
                tests[p.id] = { running: true };
                paint();
                const r = await MS.adapters.adapterFor(p.kind).testConnection(p);
                tests[p.id] = r;
                paint();
              }
            }),
            test && test.running ? h('span.row.muted', h('span.spin'), '연결하는 중…')
              : test && test.ok ? h('span.row', h('span.pill.ok', icon('check'), 'Connected'), h('b', test.version), h('span.muted', test.latencyMs + ' ms'))
                : test ? h('span.err-text', { style: { fontSize: '12px' } }, test.error) : null));
      }
      return h('div', { style: { display: 'grid', gridTemplateColumns: 'minmax(200px, 250px) minmax(0, 1fr)', gap: '14px', alignItems: 'start' } },
        h('div.card', h('div.card-head', h('span.card-title', '접속 ' + draft.connections.length), h('div.card-tools',
          UI.btn('', { icon: 'add', sm: true, kind: 'ghost', title: '추가', onClick: () => add(false) }),
          UI.btn('', { icon: 'copy', sm: true, kind: 'ghost', title: '복제', disabled: !p, onClick: () => add(true) }),
          UI.btn('', { icon: 'del', sm: true, kind: 'ghost', title: '삭제', disabled: !p, onClick: remove }))), list),
        form);
    }

    function defaultsTab() {
      const d = draft.defaults;
      const radio = (value, title, desc) => h('label', { class: cx('radio-item', d.checkpointStore === value && 'on') },
        h('input', { type: 'radio', name: 'cpstore', checked: d.checkpointStore === value, onChange: () => { d.checkpointStore = value; paint(); } }),
        h('div.grow', h('div', title), h('div.muted', desc)));
      return h('div', { style: { display: 'grid', gap: '14px' } },
        h('div.section-label', '새 작업의 이관 전략 기본값'),
        h('div.form.cols-3',
          UI.field('커밋 단위', UI.select([1000, 10000, 50000].map((n) => ({ value: String(n), label: MS.fmt.n(n) + ' rows / commit' })), String(d.commitSize), (v) => { d.commitSize = Number(v); }, { label: '커밋 단위' })),
          UI.field('Fetch 크기', UI.select([1000, 5000, 10000].map((n) => ({ value: String(n), label: MS.fmt.n(n) + ' rows / fetch' })), String(d.fetchSize), (v) => { d.fetchSize = Number(v); }, { label: 'Fetch 크기' })),
          UI.field('병렬 작업자', UI.seg([1, 2, 4, 8].map((n) => ({ value: n, label: String(n) })), d.workers, (v) => { d.workers = v; paint(); }, '병렬 작업자')),
          UI.field('오류 처리', UI.select([{ value: 'CONTINUE', label: '계속 + 오류 테이블' }, { value: 'STOP', label: '오류 시 중지' }, { value: 'RETRY', label: '3회 재시도' }], d.errorPolicy, (v) => { d.errorPolicy = v; }, { label: '오류 처리' })),
          UI.field('오류 테이블 접두어', UI.input(d.errorTable, (v) => { d.errorTable = v.toUpperCase(); }, { mono: true, label: '오류 테이블 접두어' }), { hint: 'ERR$_ → ERR$_TB_MEMBER' })),
        h('div.section-label', { style: { marginTop: '4px' } }, '체크포인트 저장소'),
        h('div.radio-list', SM.CHECKPOINT_STORES.map((x) => radio(x.value, x.label, x.desc))),
        h('div.form', UI.field('제어 테이블 접두어', UI.input(d.controlPrefix, (v) => { d.controlPrefix = v.toUpperCase(); }, { mono: true, label: '제어 테이블 접두어' }), { hint: d.controlPrefix + 'RUN · ' + d.controlPrefix + 'RUN_TASK · ' + d.controlPrefix + 'CHECKPOINT (대상 스키마)' })));
    }

    function agentTab() {
      const a = draft.agent;
      const snap = S.session.run;
      const live = MS.runner.active && snap ? snap.agent : null;
      const radio = (value, title, desc) => h('label', { class: cx('radio-item', a.onHostExit === value && 'on') },
        h('input', { type: 'radio', name: 'hostexit', checked: a.onHostExit === value, onChange: () => { a.onHostExit = value; paint(); } }),
        h('div.grow', h('div', title), h('div.muted', desc)));
      return h('div', { style: { display: 'grid', gap: '14px' } },
        UI.notice('info', 'info', '이관은 Folderss 안이 아니라 플러그인이 띄우는 ', h('b.mono', 'MigrationAgent.exe'), ' 하위 프로세스에서 실행합니다. 팝업을 닫아도 계속되고, 다시 열면 진행 화면에 다시 붙습니다.'),
        h('div.section-label', 'Folderss를 닫을 때'),
        h('div.radio-list',
          radio('CONTINUE', '계속 실행 (권장)', '에이전트는 그대로 돌고, Folderss를 다시 열어 Migration Studio를 열면 진행 화면에 다시 붙음'),
          radio('STOP', '함께 중지', 'Folderss가 끝나면 에이전트도 끝냄(Job Object) — 진행 중 배치는 롤백, 체크포인트에서 재개')),
        h('div.form.cols-3',
          UI.field('동시에 실행할 작업', UI.seg([1, 2, 4].map((n) => ({ value: n, label: String(n) })), a.maxConcurrent, (v) => { a.maxConcurrent = v; paint(); }, '동시 실행'), { hint: '작업 하나 = 에이전트 하나' }),
          UI.field('실행 로그 보관', UI.select([7, 30, 90].map((n) => ({ value: String(n), label: n + '일' })), String(a.logDays), (v) => { a.logDays = Number(v); }, { label: '로그 보관' }))),
        h('dl.kv',
          h('dt', '실행 파일'), h('dd.mono', { style: { fontSize: '11.5px' } }, '%LOCALAPPDATA%\\Folderss\\plugin-data\\zaruous.folderss-oracle-migration\\agent\\1.0.0\\MigrationAgent.exe'),
          h('dt', '통신'), h('dd', '이름 있는 파이프 ', h('span.mono', 'folderss-migration-<RUN_ID>'), ' (현재 Windows 사용자만 접근)'),
          h('dt', '실행 중'), h('dd', live ? h('span', h('span.pill.run', h('span.dot.run'), 'PID ' + live.pid), ' ', snap.runId) : '없음')));
    }

    function paint() {
      host.replaceChildren(
        UI.tabs([{ key: 'connections', label: '접속', count: draft.connections.length }, { key: 'defaults', label: '기본값' }, { key: 'agent', label: '실행 에이전트' }], current, (k) => { current = k; paint(); }),
        h('div', { style: { paddingTop: '14px' } }, current === 'defaults' ? defaultsTab() : current === 'agent' ? agentTab() : connectionsTab()));
    }

    paint();
    MS.modal({
      title: '마이그레이션 설정',
      width: 900,
      body: h('div', h('div.muted', { style: { fontSize: '12px', marginBottom: '6px' } }, 'Migration Studio 플러그인의 설정입니다. Folderss 설정 > 플러그인 > Migration Studio에서도 같은 내용을 고칩니다.'), host),
      buttons: [
        { label: '취소' },
        {
          label: '저장', primary: true,
          onClick: () => {
            for (const c of draft.connections) {
              const errors = SM.validateProfile(c, draft.connections);
              if (errors.length) {
                current = 'connections';
                sel = c.id;
                paint();
                MS.toast(c.name + ': ' + errors[0], 'err');
                return false;
              }
            }
            S.settings = draft;
            S.saveSettings();
            // 쓰는 접속의 주소가 바뀌었을 수 있으니 시험 결과를 다시 받게 한다
            for (const role of ['source', 'target']) Object.assign(S.session.conn[role], { status: 'unknown', error: null });
            MS.app.refresh();
            MS.toast('마이그레이션 설정을 저장했습니다', 'ok');
          }
        }
      ]
    });
  }

  MS.settingsDialog = { open };
})();
