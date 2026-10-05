/*
 * ① 접속: 원본·대상 접속(카드 두 개)과 이관 전략.
 * 접속 자체(호스트·포트·서비스·사용자·비밀번호)는 플러그인의 "마이그레이션 설정"에 정의하고, 작업은 그 접속을 고른 뒤 스키마만 정한다.
 * DB Helper의 접속과는 공유하지 않는다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const { h, cx, icon, fmt } = MS;
  const UI = MS.ui;
  const S = MS.store;
  const SM = MS.settingsModel;

  function statusLine(role) {
    const st = S.session.conn[role];
    const meta = S.session.meta[role];
    const lines = [];
    if (st.status === 'testing') lines.push(h('div.line', h('span.spin'), '연결하는 중…'));
    else if (st.status === 'ok') lines.push(h('div.line', h('span.pill.ok', icon('check'), 'Connected'), h('b', st.version), h('span.muted', st.latency + ' ms'), h('span.faint', fmt.time(st.testedAt))));
    else if (st.status === 'error') lines.push(h('div.line.err-text', icon('error'), st.error));
    else lines.push(h('div.line.muted', h('span.dot'), '이번 창에서 아직 시험하지 않음'));
    if (st.metaLoading) lines.push(h('div.line', h('span.spin'), '메타데이터 불러오는 중…'));
    else if (meta) {
      const tables = meta.tables.filter((t) => t.kind === 'TABLE').length;
      const views = meta.tables.length - tables;
      const cols = meta.tables.reduce((a, t) => a + t.columns.length, 0);
      lines.push(h('div.line.muted', icon('table'), meta.schema + ' · 테이블 ' + tables + (views ? ' · 뷰 ' + views : '') + ' · 컬럼 ' + cols,
        h('span.faint', meta.cached ? '(캐시 · ' + fmt.time(new Date(meta.loadedAt)) + ')' : '(' + fmt.time(new Date(meta.loadedAt)) + ' · ' + meta.elapsedMs + ' ms)')));
    } else lines.push(h('div.line.warn-text', icon('warn'), '메타데이터를 불러오지 않았습니다'));
    return h('div.conn-result', { 'aria-live': 'polite' }, lines);
  }

  /** 접속이나 스키마가 바뀌면 시험 결과·메타데이터를 다시 받게 한다 */
  function resetRole(role) {
    Object.assign(S.session.conn[role], { status: 'unknown', error: null });
    S.session.meta[role] = null;
  }

  function connCard(role) {
    const isSrc = role === 'source';
    const ref = S.job[role];
    const c = S.conn(role);
    const missing = S.missingConn(role);
    const options = [{ value: '', label: '— 접속을 고르세요' }].concat(S.settings.connections.map((p) => ({
      value: p.id,
      label: p.name + '   ' + p.host + '/' + p.service + (p.writeBlocked ? '   (쓰기 금지)' : ''),
      disabled: !isSrc && p.writeBlocked
    })));
    const pick = UI.select(options, c ? c.id : '', (v) => {
      const p = SM.profile(S.settings, v);
      S.job[role] = p ? { profileId: p.id, schema: p.defaultSchema || p.user } : { profileId: null, schema: '' };
      resetRole(role);
      MS.app.changed({});
      if (p) MS.toast(p.name + ' 접속을 골랐습니다 · 메타데이터를 불러오세요');
    }, { label: (isSrc ? '원본' : '대상') + ' 접속' });
    const schema = UI.input(ref.schema, (v) => { ref.schema = v.toUpperCase(); }, {
      mono: true, label: '스키마', placeholder: c ? c.defaultSchema || c.user : '',
      onChange: () => { resetRole(role); MS.app.changed({}); }
    });
    const summary = c
      ? h('dl.kv', { style: { padding: '8px 10px', border: '1px solid var(--border)', borderRadius: '4px', background: 'var(--surface)' } },
        h('dt', 'DB 종류'), h('dd', (MS.adapters.KINDS.find((k) => k.value === c.kind) || {}).label || c.kind),
        h('dt', '주소'), h('dd.mono', { style: { fontSize: '12px' } }, c.host + ':' + c.port + '/' + c.service),
        h('dt', '사용자'), h('dd.mono', { style: { fontSize: '12px' } }, c.user),
        h('dt', '비밀번호'), h('dd', c.savePassword && c.password ? '저장됨(DPAPI)' : h('span.warn-text', '연결할 때 입력')),
        h('dt', '색 표시'), h('dd', UI.dbBadge(c), c.writeBlocked && h('span.tag.ro', { style: { marginLeft: '6px' } }, '쓰기 금지')))
      : null;
    const notices = [];
    if (missing) {
      notices.push(UI.notice('warn', 'warn', '이 PC의 마이그레이션 설정에 ', h('b', missing.name), ' (' + (missing.host || '?') + '/' + (missing.service || '?') + ') 접속이 없습니다. ',
        h('button.btn.link', {
          type: 'button',
          onClick: () => {
            const p = Object.assign(SM.newProfile(S.settings), { name: missing.name, kind: missing.kind || 'oracle', color: missing.color || '', host: missing.host || '', port: String(missing.port || '1521'), service: missing.service || '', user: missing.user || '', defaultSchema: missing.schema || '', savePassword: false });
            S.settings.connections.push(p);
            S.saveSettings();
            ref.profileId = p.id;
            MS.app.changed({});
            MS.settingsDialog.open('connections', p.id);
          }
        }, '접속 설정에 추가 ›')));
    }
    if (!isSrc && c && c.writeBlocked) notices.push(UI.notice('err', 'error', h('b', c.name), '은(는) "쓰기 금지" 접속이라 대상으로 쓸 수 없습니다.'));
    else if (!isSrc && c && c.color === 'red') notices.push(UI.notice('warn', 'warn', h('b', '운영 DB에 씁니다. '), 'TRUNCATE·DELETE 방식은 실행 직전에 확인 체크를 한 번 더 받습니다.'));
    return UI.card({
      cls: 'conn-card',
      title: [h('span', { class: cx('role', isSrc ? 'src' : 'tgt') }, isSrc ? 'SOURCE' : 'TARGET'), isSrc ? '원본' : '대상'],
      tools: [UI.dbBadge(c), isSrc && h('span.tag.ro', { title: '원본 세션은 SET TRANSACTION READ ONLY로 엽니다' }, '읽기 전용')],
      body: h('div', { style: { display: 'grid', gap: '12px' } },
        UI.field('접속 (마이그레이션 설정)', h('div.row', h('div.grow', pick), UI.btn('접속 관리…', { icon: 'setting', onClick: () => MS.settingsDialog.open('connections', c ? c.id : null) })), { required: true }),
        notices,
        summary,
        UI.field('스키마', schema, { required: true, hint: '이 작업에서 읽고/쓸 스키마. 비우면 접속의 기본 스키마' })),
      foot: [
        UI.btn('접속 테스트', { icon: 'link', disabled: !c || S.session.conn[role].status === 'testing', onClick: () => MS.actions.testConnection(role) }),
        UI.btn(S.session.meta[role] ? '메타데이터 다시 불러오기' : '메타데이터 불러오기', { icon: 'sync', disabled: !c || S.session.conn[role].metaLoading, onClick: () => MS.actions.loadMetadata(role) }),
        h('div.grow', { style: { flexBasis: '100%' } }, statusLine(role))
      ]
    });
  }

  function strategyCard() {
    const st = S.job.strategy;
    const set = (key, rerender) => (v) => {
      st[key] = v;
      MS.app.changed(rerender ? {} : { page: false });
    };
    const modeDesc = {
      FULL: '원본 범위 전체를 읽어 씁니다. 중단하면 체크포인트부터 이어서 할 수 있습니다.',
      INCREMENTAL: '지난 실행의 마지막 키 다음부터 읽습니다(WHERE 키 > :LAST_ID). 대용량 테이블을 나눠 옮길 때.',
      CDC: '변경분만 따라 옮깁니다(SCN·로그 기반). POC에서는 설정 자리만 보입니다.'
    }[st.mode];
    const radio = (value, title, desc, extra) => h('label', { class: cx('radio-item', st.errorPolicy === value && 'on') },
      h('input', { type: 'radio', name: 'errorPolicy', checked: st.errorPolicy === value, onChange: () => set('errorPolicy', true)(value) }),
      h('div.grow', h('div', title), h('div.muted', desc)), extra);
    const errTable = st.errorPolicy === 'CONTINUE' ? UI.input(st.errorTable, set('errorTable'), { mono: true, label: '오류 테이블 접두어', title: '대상마다 <접두어><테이블> 오류 테이블을 씁니다' }) : null;
    if (errTable) errTable.style.width = '92px';
    return UI.card({
      title: [icon('setting'), '이관 전략'],
      sub: '이 작업의 기본값(새 작업은 마이그레이션 설정의 기본값으로 시작) — SQL 원본은 자기 Fetch·커밋 크기를 따로 가질 수 있음',
      tools: [UI.btn('설정 기본값으로', {
        kind: 'ghost', sm: true, title: '마이그레이션 설정 > 기본값을 이 작업에 적용',
        onClick: () => {
          const d = S.settings.defaults;
          Object.assign(st, { commitSize: d.commitSize, fetchSize: d.fetchSize, workers: d.workers, errorPolicy: d.errorPolicy, errorTable: d.errorTable });
          MS.app.changed({});
        }
      })],
      body: h('div', { style: { display: 'grid', gap: '14px' } },
        h('div.form',
          UI.field('실행 방식(Execution Mode)', h('div', { style: { display: 'grid', gap: '6px' } },
            UI.seg([{ value: 'FULL', label: '전체 이관' }, { value: 'INCREMENTAL', label: '증분 이관' }, { value: 'CDC', label: 'CDC 변경 동기화' }], st.mode, set('mode', true), '실행 방식'),
            h('span.field-hint', modeDesc))),
          st.mode === 'INCREMENTAL'
            ? UI.field('증분 기준', h('div', { style: { display: 'grid', gap: '6px' } },
              UI.seg([{ value: 'PK', label: 'Primary Key' }, { value: 'TIMESTAMP', label: 'Timestamp' }, { value: 'SEQUENCE', label: 'Sequence' }, { value: 'SCN', label: 'SCN' }], st.incrementalBy, set('incrementalBy', true), '증분 기준'),
              h('span.field-hint', '매핑마다 기준 컬럼은 컬럼 매핑 화면의 "체크포인트 컬럼"에서 정합니다')))
            : st.mode === 'CDC'
              ? UI.field('CDC 원천', UI.notice('info', 'info', 'LogMiner·GoldenGate 연동은 다음 단계 범위입니다. 지금은 전체·증분만 실행됩니다.'))
              : UI.field('체크포인트', h('div.muted', { style: { fontSize: '12px', paddingTop: '4px' } }, '커밋할 때마다 마지막 키를 남깁니다 — 중지·장애 뒤 그 다음부터 재개 (저장소: 마이그레이션 설정 > 기본값)')),
          UI.field('트랜잭션 단위(Commit)', UI.select([1000, 10000, 50000].map((n) => ({ value: String(n), label: fmt.n(n) + ' rows / commit' })), String(st.commitSize), (v) => set('commitSize')(Number(v)), { label: '커밋 단위' }),
            { hint: '배열 바인드 크기와 같음. 클수록 빠르지만 롤백 범위가 커짐' }),
          UI.field('Fetch 크기', UI.select([1000, 5000, 10000].map((n) => ({ value: String(n), label: fmt.n(n) + ' rows / fetch' })), String(st.fetchSize), (v) => set('fetchSize')(Number(v)), { label: 'Fetch 크기' }),
            { hint: '원본에서 한 번에 가져오는 행 — 메모리에는 이만큼만 올라감' }),
          UI.field('병렬 작업자(Parallel Workers)', h('div', { style: { display: 'grid', gap: '6px' } },
            UI.seg([1, 2, 4, 8].map((n) => ({ value: n, label: String(n) })), st.workers, set('workers', true), '병렬 작업자'),
            h('span.field-hint', '체크포인트 키 범위를 나눠 작업자마다 따로 읽고 씁니다(세션 ' + st.workers * 2 + '개)'))),
          UI.field('오류 처리(Error Policy)', h('div.radio-list',
            radio('CONTINUE', '계속 + 오류 테이블', '문제 행만 오류 테이블에 남기고 계속(DML 오류 로깅)', errTable),
            radio('STOP', '오류 시 중지', '첫 오류에서 멈춤 · 진행 중 배치 롤백'),
            radio('RETRY', '3회 재시도', '일시 오류(연결 끊김 등)는 3번까지 다시 시도한 뒤 멈춤')))))
    });
  }

  MS.pages.connection = {
    render() {
      return MS.app.frame({
        key: 'connection',
        title: '접속',
        desc: '마이그레이션 설정에 정의한 접속 중 원본·대상을 고르고 스키마를 정한 뒤 메타데이터를 불러옵니다. 서버·서비스·스키마·테이블·컬럼이 모두 달라도 됩니다.',
        actions: [
          UI.btn('마이그레이션 설정…', { icon: 'setting', onClick: () => MS.settingsDialog.open('connections') }),
          UI.btn('두 접속 모두 테스트', { icon: 'link', onClick: () => MS.actions.testAll() })
        ],
        body: [
          h('div.conn-pair', connCard('source'), h('div.conn-arrow', { 'aria-hidden': 'true' }, h('span', MS.ICON.arrow)), connCard('target')),
          strategyCard()
        ],
        hint: '접속은 이 플러그인의 설정에만 저장합니다(DB Helper와 공유하지 않음).'
      });
    }
  };
})();
