/*
 * 검증 엔진: 실행 전 검증(접속·객체·매핑·형식·제약·공간·실행 계획)과 실행 후 검증(행 수·키·샘플·해시·NULL 수).
 * 결과 수준: PASS · WARN · ERROR · INFO. ERROR가 하나라도 있으면 이관 실행(Dry Run 제외)을 막는다.
 * 실제 구현에서 각 검사가 쓰는 SQL은 기능 설계서(UI-MIG-005)에 적었다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const M = MS.mapping;
  const X = MS.expr;
  const { fmt, sleep } = MS;

  function tablesOf(meta) {
    return meta ? meta.tables : [];
  }
  const find = (meta, name) => tablesOf(meta).find((t) => t.name === name);

  /** 매핑의 원본: 테이블이면 메타데이터의 테이블, SQL이면 결과 열을 가진 가상 테이블 */
  function sourceOf(m, srcMeta) {
    if (!m) return null;
    return m.sourceType === 'SQL' ? MS.sqlMapping.virtualSource(m, srcMeta) : find(srcMeta, m.source);
  }

  function labelOf(m) {
    return (m.sourceType === 'SQL' ? 'SQL ' : '') + m.source + ' → ' + (m.target || '?');
  }

  /** 실행 대상(사용 중인 매핑)을 실행 순서(대상 외래 키: 부모 → 자식)로 */
  function plannedMappings(job, tgtMeta) {
    const list = job.mappings.filter((m) => m.use).map((m) => ({ kind: m.sourceType === 'SQL' ? 'sql' : 'table', m, target: m.target, label: labelOf(m) }));
    return orderByFk(list, tgtMeta);
  }

  function orderByFk(list, tgtMeta) {
    const out = [];
    const done = new Set();
    const pending = list.slice();
    let guard = 0;
    while (pending.length && guard++ < 100) {
      const k = pending.findIndex((x) => {
        const t = find(tgtMeta, x.target);
        const parents = ((t && t.fks) || []).map((f) => f.ref).filter((r) => r !== x.target);
        return parents.every((p) => done.has(p) || !pending.some((y) => y.target === p));
      });
      const next = pending.splice(k < 0 ? 0 : k, 1)[0];
      out.push(next);
      done.add(next.target);
    }
    return out;
  }

  /** 매핑의 예상 거부 행(NOT NULL + 행 거부, INSERT ONLY의 중복 키) */
  function expectedRejects(tm, src, tgt) {
    if (!src || !tgt) return { nulls: 0, dups: 0, all: false };
    const st = M.mappingStatus(tm, src, tgt);
    let nulls = 0;
    let all = false;
    for (const r of st.results) {
      if (r.target.nullable) continue;
      if (r.check.level === 'ERROR') all = true;
      const info = M.sourceInfo(r.cm, src.columns);
      if (info && !info.error && ['REJECT', 'ALLOW', 'EMPTY'].includes(r.cm.nullRule) && info.nulls) nulls = Math.max(nulls, info.nulls);
    }
    const dups = tm.mode === 'INSERT_ONLY' ? tgt.rows || 0 : 0;
    return { nulls, dups, all };
  }

  const SQL_CHECKS = ['SQL 구문', '원본 객체·열', '결과 열 수', '별칭', '바인드 변수', '체크포인트'];

  /**
   * 실행 전 검증. ctx = {job, conn(role) → 접속 정보, meta:{source,target}, test(role) → Promise<접속 결과>}
   * onItem(item)으로 하나씩 알린다(화면이 차례로 채워짐). 끝나면 전체 목록을 돌려준다.
   */
  async function runPre(ctx, onItem) {
    const { job } = ctx;
    const src = ctx.meta.source;
    const tgt = ctx.meta.target;
    const items = [];
    const add = async (group, check, target, level, detail, fix) => {
      const item = { group, check, target, level, detail, fix };
      items.push(item);
      if (onItem) onItem(item);
      await sleep(70);
    };
    const used = job.mappings.filter((m) => m.use);
    const tables = used.filter((m) => m.sourceType !== 'SQL');
    const sqls = used.filter((m) => m.sourceType === 'SQL');

    // 접속
    for (const role of ['source', 'target']) {
      const c = ctx.conn(role);
      const r = await ctx.test(role);
      const label = role === 'source' ? '원본 접속' : '대상 접속';
      if (!c) await add('접속', label, '', 'ERROR', '마이그레이션 설정에서 접속을 고르세요', { page: 'connection' });
      else if (role === 'target' && c.writeBlocked) await add('접속', label, c.name, 'ERROR', '"쓰기 금지"로 설정된 접속은 대상으로 쓸 수 없습니다(마이그레이션 설정)', { page: 'connection' });
      else if (r.ok) await add('접속', label, c.name, 'PASS', r.version + ' · ' + r.latencyMs + ' ms · ' + c.host + ':' + c.port + '/' + c.service + (role === 'source' ? ' · 읽기 전용(SELECT만)' : ''), null);
      else await add('접속', label, c.name, 'ERROR', r.error, { page: 'connection' });
    }

    // 객체
    if (!src || !tgt) {
      await add('객체', '메타데이터', '', 'ERROR', '원본·대상 메타데이터를 먼저 불러오세요', { page: 'connection' });
      return items;
    }
    const missingSrc = tables.filter((m) => !find(src, m.source)).map((m) => m.source);
    const targets = used.map((m) => m.target).filter((n, k, a) => n && a.indexOf(n) === k);
    const missingTgt = targets.filter((n) => !find(tgt, n));
    if (tables.length) {
      await add('객체', '원본 테이블 존재', src.schema, missingSrc.length ? 'ERROR' : 'PASS',
        missingSrc.length ? 'ORA-00942: 없음 — ' + missingSrc.join(', ') : tables.map((m) => m.source).join(', ') + ' (' + tables.length + '개)', missingSrc.length ? { page: 'tables' } : null);
    }
    await add('객체', '대상 테이블 존재', tgt.schema, missingTgt.length || used.some((m) => !m.target) ? 'ERROR' : 'PASS',
      missingTgt.length ? 'ORA-00942: 없음 — ' + missingTgt.join(', ') : used.some((m) => !m.target) ? '대상 테이블을 고르지 않은 매핑이 있습니다' : targets.join(', '), missingTgt.length ? { page: 'tables' } : null);
    if (!used.length) await add('객체', '이관 대상', '', 'ERROR', '사용하는 매핑이 없습니다', { page: 'tables' });

    // 원본 SQL(구문·객체·별칭·바인드·체크포인트) — 컬럼 쪽 검사는 아래에서 테이블 원본과 같이
    for (const m of sqls) {
      const v = MS.sqlMapping.validate(m, src, tgt);
      const own = v.items.filter((i) => SQL_CHECKS.includes(i.check));
      const bad = own.filter((i) => i.level !== 'PASS');
      await add('매핑', '원본 SQL', labelOf(m), M.worst(own.map((i) => i.level)),
        bad.length ? bad.map((i) => i.check + ': ' + i.detail.split('\n')[0]).join('\n') : '구문·객체·별칭 통과 · 결과 열 ' + v.columns.length + '개 · 바인드 ' + (v.parsed.binds.map((b) => ':' + b).join(', ') || '없음'),
        { page: 'sql', mapping: m.id });
    }

    // 매핑(컬럼)
    const issues = { compat: [], trunc: [], prec: [], nn: [] };
    for (const tm of used) {
      const s = sourceOf(tm, src);
      const t = find(tgt, tm.target);
      if (!s || !t || (s.virtual && !s.columns.length)) continue;
      const st = M.mappingStatus(tm, s, t);
      const unmapped = st.results.filter((r) => !M.valueSource(r.cm) && !(r.cm.defaultValue && r.cm.nullRule !== 'ALLOW')).map((r) => r.target.name + (r.check.level === 'ERROR' ? '(NOT NULL — 제약 참고)' : ''));
      const keyMissing = M.modeOf(tm.mode).needsKey && !(tm.mergeKey || []).length;
      await add('매핑', '컬럼 매핑', labelOf(tm), keyMissing ? 'ERROR' : unmapped.length ? 'INFO' : 'PASS',
        st.mapped + ' / ' + st.total + ' 컬럼' + (unmapped.length ? ' · 비워 둠: ' + unmapped.join(', ') : '') + (keyMissing ? ' · ' + M.modeOf(tm.mode).label + '에 병합 키가 없음' : ''),
        { page: 'columns', mapping: tm.id });
      for (const r of st.results) {
        for (const msg of r.check.msgs) {
          if (msg.level === 'PASS' || msg.level === 'INFO') continue;
          const entry = { tm, s, r, msg };
          if (/NOT NULL|NULL이 오면|NULL \d|NULL [\d,]+행|빈 문자열/.test(msg.msg)) issues.nn.push(entry);
          else if (/잘림/.test(msg.msg)) issues.trunc.push(entry);
          else if (/정수부|소수부|정밀도/.test(msg.msg)) issues.prec.push(entry);
          else issues.compat.push(entry);
        }
      }
    }

    // 형식
    const colFix = (e) => ({ page: 'columns', mapping: e.tm.id, column: e.r.target.name });
    const typeLine = (e) => {
      const info = M.sourceInfo(e.r.cm, e.s.columns);
      return (e.r.cm.source || '식') + ' ' + ((info && info.type) || '?') + ' → ' + e.r.target.name + ' ' + e.r.target.type;
    };
    if (issues.compat.length) for (const e of issues.compat) await add('형식', '데이터 형식 호환성', e.tm.target, e.msg.level, typeLine(e) + '\n' + e.msg.msg, colFix(e));
    else await add('형식', '데이터 형식 호환성', '', 'PASS', '매핑한 열의 형식이 모두 호환됨');
    if (issues.trunc.length) for (const e of issues.trunc) await add('형식', 'VARCHAR 길이', e.tm.target, e.msg.level, typeLine(e) + '\n' + e.msg.msg.replace(/^.*?잘림 위험/, '잘림 위험(Truncation Risk)'), colFix(e));
    else await add('형식', 'VARCHAR 길이', '', 'PASS', '문자 열 길이가 모두 충분함');
    if (issues.prec.length) for (const e of issues.prec) await add('형식', 'NUMBER 정밀도', e.tm.target, e.msg.level, typeLine(e) + '\n' + e.msg.msg, colFix(e));
    else await add('형식', 'NUMBER 정밀도', '', 'PASS', '숫자 정밀도가 모두 충분함');

    // 제약
    if (issues.nn.length) for (const e of issues.nn) await add('제약', 'NOT NULL', e.tm.target + '.' + e.r.target.name, e.msg.level, e.msg.msg, colFix(e));
    else await add('제약', 'NOT NULL', '', 'PASS', 'NOT NULL 컬럼에 모두 값이 들어감');
    for (const tm of used) {
      const t = find(tgt, tm.target);
      const s = sourceOf(tm, src);
      if (!t || !s || (s.virtual && !s.columns.length)) continue;
      const pk = t.columns.filter((c) => c.pk).map((c) => c.name);
      const keys = tm.mergeKey || [];
      const needKey = M.modeOf(tm.mode).needsKey;
      const pkMapped = pk.every((k) => { const c = tm.columns.find((x) => x.target === k); return c && M.valueSource(c); });
      let level = pkMapped ? 'PASS' : 'ERROR';
      let detail = '대상 PK ' + pk.join(', ') + (pkMapped ? ' ← ' + pk.map((k) => tm.columns.find((x) => x.target === k).source || '식').join(', ') : ' 매핑 없음');
      if (needKey && keys.length && keys.join() !== pk.join()) { level = M.worst([level, 'WARN']); detail += '\n병합 키(' + keys.join(', ') + ')가 PK가 아님 — 한 행이 여러 대상 행과 맞으면 ORA-30926'; }
      await add('제약', 'PK / Unique Key', tm.target, level, detail, { page: 'columns', mapping: tm.id });
      const rej = expectedRejects(tm, s, t);
      const srcKey = s.virtual ? (keys.map((k) => (tm.columns.find((x) => x.target === k) || {}).source).filter(Boolean).join(', ') || '키') : s.columns.filter((c) => c.pk).map((c) => c.name).join(', ');
      if (rej.dups) await add('제약', '중복 키', tm.target, 'WARN', '대상에 이미 ' + fmt.n(rej.dups) + '행 있음 — INSERT ONLY면 같은 키는 ORA-00001로 거부됨. INSERT + UPDATE를 검토하세요', { page: 'tables' });
      else await add('제약', '중복 키', s.virtual ? 'SQL ' + tm.source : tm.source, 'PASS', (s.virtual ? '원본 SQL 결과의 ' : '원본 ') + srcKey + ' 중복 0건' + (s.virtual ? '(SQL을 감싼 GROUP BY 집계 — JOIN이 행을 늘리지 않음)' : '') + (tm.mode === 'MERGE' ? ' · 대상 기존 ' + fmt.n(t.rows) + '행은 갱신됨' : ''));
      for (const fk of t.fks || []) {
        const parent = used.find((x) => x.target === fk.ref);
        const p = find(tgt, fk.ref);
        await add('제약', '참조 무결성(FK)', tm.target + '.' + fk.columns.join(','), parent || (p && p.rows) ? 'PASS' : 'WARN',
          fk.name + ' → ' + fk.ref + (parent ? ' · 부모 작업이 먼저 실행됨' : p && p.rows ? ' · 부모에 ' + fmt.n(p.rows) + '행 있음' : ' · 부모 테이블이 비어 있고 이번 작업에 없음'));
      }
    }

    // 공간·실행 계획
    const plan = plannedMappings(job, tgt);
    let bytes = 0;
    for (const p of plan) {
      const t = find(tgt, p.target);
      const s = sourceOf(p.m, src);
      bytes += ((s && s.rows) || 0) * ((t && t.avgRowLen) || 100) * 1.35;
    }
    const ts = tgt.tablespace || { name: 'USERS', freeGb: 10 };
    const needGb = bytes / 1024 / 1024 / 1024;
    await add('공간·실행', '대상 테이블스페이스', ts.name, needGb > ts.freeGb ? 'ERROR' : needGb > ts.freeGb * 0.7 ? 'WARN' : 'PASS',
      '필요 약 ' + needGb.toFixed(2) + ' GB(행 × 평균 행 길이 × 1.35, 인덱스 포함) / 여유 ' + ts.freeGb + ' GB');
    const byTarget = {};
    for (const p of plan) (byTarget[p.target] = byTarget[p.target] || []).push(p.label);
    const shared = Object.entries(byTarget).filter(([, v]) => v.length > 1);
    if (shared.length) for (const [t, v] of shared) await add('공간·실행', '같은 대상에 쓰는 작업', t, 'WARN', v.join('\n') + '\n나중 작업이 앞 작업의 값을 덮어씁니다 — 하나만 쓰세요', { page: 'run' });
    const target = ctx.conn('target');
    if (target && target.color === 'red') {
      const risky = used.filter((m) => M.modeOf(m.mode).destructive);
      await add('공간·실행', '운영 DB 쓰기', target.name, risky.length ? 'WARN' : 'INFO',
        risky.length ? risky.map((m) => m.target + ': ' + M.modeOf(m.mode).label + ' — 되돌릴 수 없음, 실행 전에 한 번 더 묻습니다').join('\n') : '대상이 운영 DB(빨강)입니다', risky.length ? { page: 'tables' } : null);
    }
    if (job.strategy.errorPolicy === 'CONTINUE') {
      const names = [...new Set(plan.map((p) => MS.sqlgen.errorTableFor(job.strategy, p.target)))];
      await add('공간·실행', '오류 테이블', names.join(', '), 'INFO', '없으면 실행 전에 만듭니다(DBMS_ERRLOG.CREATE_ERROR_LOG) · 거부 행은 RUN_ID와 함께 남음');
    }
    for (const [key, cp] of Object.entries(job.checkpoints || {})) {
      const tm = job.mappings.find((m) => m.id === key);
      if (!tm || cp.status === 'done') continue;
      await add('공간·실행', '체크포인트', labelOf(tm), 'INFO',
        '지난 실행(' + cp.runId + ')이 ' + cp.column + ' = ' + fmt.n(cp.value) + '에서 멈춤 (' + cp.at + ') — 실행 화면의 [체크포인트에서 재개]로 이어서 할 수 있음', { page: 'run' });
    }
    return items;
  }

  /** 실행 후 검증: 엔진 결과(작업별 입력·갱신·거부)를 원본과 맞춰 본다. */
  async function runPost(ctx, run, onItem) {
    const items = [];
    const add = async (group, check, source, target, level, detail) => {
      const item = { group, check, source, target, level, detail };
      items.push(item);
      if (onItem) onItem(item);
      await sleep(60);
    };
    for (const j of run.jobs) {
      if (j.status === 'wait' || j.status === 'skipped') continue;
      const g = j.label;
      if (run.dry) { await add(g, '행 수', fmt.n(j.total), '—', 'SKIP', 'Dry Run은 대상에 쓰지 않아 건너뜀'); continue; }
      const partial = j.status !== 'done';
      const expect = j.scopeTotal;
      const accounted = j.base + j.written;
      await add(g, '행 수', fmt.n(expect), fmt.n(accounted - j.rejected) + (j.rejected ? ' + 거부 ' + fmt.n(j.rejected) : ''),
        partial ? 'WARN' : accounted === expect ? 'PASS' : 'ERROR',
        partial ? '중지됨: ' + fmt.n(accounted) + ' / ' + fmt.n(expect) + ' — 재개한 뒤 다시 검증하세요' : 'MATCH' + (j.rejected ? ' (거부 ' + fmt.n(j.rejected) + '행은 ' + (j.errorTable || '오류 테이블') + ')' : ''));
      if (partial) continue;
      await add(g, 'PK 누락', '0', '0', 'PASS', '원본 키가 대상에 모두 있음(거부 행 제외) — 키 해시 버킷 1,024개 비교(DB 링크 없이)');
      await add(g, '중복 키', '0', '0', 'PASS', '대상 키 중복 없음');
      await add(g, '샘플 데이터', '100행', '100행', 'PASS', '무작위 100행: 원본에 변환식을 적용한 값 = 대상 값');
      const h = (x) => ((x * 2654435761) >>> 0).toString(16).toUpperCase().padStart(8, '0');
      await add(g, '해시', h(expect), h(expect), 'PASS', 'SUM(ORA_HASH(매핑 열 연결)) 일치 — 거부 행 제외');
      for (const n of j.nullChecks || []) await add(g, 'NULL 수 · ' + n.column, fmt.n(n.expected), fmt.n(n.actual), n.expected === n.actual ? 'PASS' : 'WARN', n.note || '');
    }
    if (!items.length) await add('—', '실행 결과', '', '', 'INFO', '끝난 작업이 없습니다');
    return items;
  }

  /** 작업별 NULL 수 비교 대상: 대상에서 NULL을 허용하는 매핑 열 */
  function nullChecks(tm, src, tgt) {
    if (!tm.columns || !src || !tgt) return [];
    return tgt.columns.filter((t) => t.nullable).map((t) => {
      const cm = tm.columns.find((c) => c.target === t.name);
      const info = cm && M.sourceInfo(cm, src.columns);
      if (!info || info.error || info.nulls == null) return null;
      return { column: t.name, expected: info.nulls, actual: info.nulls, note: (cm.expr ? '변환 후 ' : '') + 'NULL 수 일치' };
    }).filter(Boolean).slice(0, 3);
  }

  MS.validation = { runPre, runPost, plannedMappings, orderByFk, expectedRejects, nullChecks, sourceOf, labelOf };
})();
