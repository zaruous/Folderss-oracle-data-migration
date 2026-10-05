/*
 * SQL 매핑 서비스: SELECT 문 해석(결과 별칭·FROM 테이블·바인드 변수), 검증, 미리보기.
 * 실제 구현에서는 구문 검사를 Oracle에 맡긴다:
 *   - 구문·객체·권한: DBMS_SQL.PARSE 또는 "SELECT * FROM (<sql>) WHERE 1=0" 실행
 *   - 결과 열 이름·형식: OracleDataReader.GetSchemaTable() (행을 읽지 않음)
 * POC는 같은 결과 모양을 화면에 보이려고 자바스크립트로 흉내 낸다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const X = MS.expr;
  const M = MS.mapping;

  // 주석은 같은 길이의 공백으로 바꿔 위치(줄 번호)를 지킨다
  function stripComments(sql) {
    return sql.replace(/('(?:[^']|'')*')|--[^\n]*|\/\*[\s\S]*?\*\//g, (m, str) => (str ? str : m.replace(/[^\n]/g, ' ')));
  }
  const lineOf = (sql, at) => sql.slice(0, Math.max(0, at)).split('\n').length;

  /** 괄호 밖(깊이 0)·문자열 밖에서 키워드 위치를 찾는다 */
  function topLevelWords(sql) {
    const out = [];
    let depth = 0;
    let i = 0;
    while (i < sql.length) {
      const ch = sql[i];
      if (ch === "'") {
        const end = sql.indexOf("'", i + 1);
        i = end < 0 ? sql.length : end + 1;
        continue;
      }
      if (ch === '(') depth++;
      else if (ch === ')') depth--;
      else if (/[A-Za-z_]/.test(ch) && (i === 0 || !/[\w$#.:]/.test(sql[i - 1]))) {
        const m = /^[A-Za-z_][\w$#]*/.exec(sql.slice(i));
        if (depth === 0) out.push({ word: m[0].toUpperCase(), at: i, end: i + m[0].length });
        i += m[0].length;
        continue;
      }
      i++;
    }
    return { words: out, balanced: depth };
  }

  function splitTopLevel(text, base) {
    const parts = [];
    let depth = 0;
    let start = 0;
    for (let i = 0; i < text.length; i++) {
      const ch = text[i];
      if (ch === "'") {
        const end = text.indexOf("'", i + 1);
        i = end < 0 ? text.length : end;
        continue;
      }
      if (ch === '(') depth++;
      else if (ch === ')') depth--;
      else if (ch === ',' && depth === 0) {
        parts.push({ text: text.slice(start, i), at: base + start });
        start = i + 1;
      }
    }
    parts.push({ text: text.slice(start), at: base + start });
    return parts;
  }

  /** 선택 목록 한 항목 → {expr, alias, name, ast, error} */
  function selectItem(raw, at) {
    const lead = raw.length - raw.trimStart().length;
    const text = raw.trim();
    const item = { text, at: at + lead, alias: null, expr: text };
    if (/^([A-Za-z_][\w$#]*\.)?\*$/.test(text)) return Object.assign(item, { star: true, name: text });
    let m = /^([\s\S]*?)\s+AS\s+("[^"]+"|[A-Za-z_][\w$#]*)$/i.exec(text);
    if (!m) {
      const t = /^([\s\S]*[^\s.])\s+("[^"]+"|[A-Za-z_][\w$#]*)$/.exec(text);
      if (t && !/^(END|NULL)$/i.test(t[2]) && !X.compile(t[1]).error && X.compile(text).error) m = t;
    }
    if (m) {
      item.expr = m[1].trim();
      item.alias = m[2].startsWith('"') ? m[2].slice(1, -1) : m[2].toUpperCase();
    }
    const c = X.compile(item.expr);
    item.ast = c.ast;
    item.refs = c.refs;
    item.error = c.error;
    if (item.error && item.error.at != null) item.errorAt = item.at + item.error.at;
    item.name = item.alias || (c.ast && c.ast.k === 'col' ? c.ast.name.replace(/^.*\./, '') : item.expr.replace(/\s+/g, '').toUpperCase().slice(0, 30));
    return item;
  }

  const JOIN_STOP = new Set(['ON', 'LEFT', 'RIGHT', 'INNER', 'FULL', 'CROSS', 'JOIN', 'WHERE', 'OUTER', 'GROUP', 'ORDER', 'USING']);

  function fromTables(text) {
    const tables = [];
    const rx = /(^|,|\bJOIN\b)\s*(\(|[A-Za-z_][\w$#]*(?:\.[A-Za-z_][\w$#]*)?)(?:\s+(?:AS\s+)?([A-Za-z_][\w$#]*))?/gi;
    let m;
    while ((m = rx.exec(text))) {
      if (m[2] === '(') { tables.push({ subquery: true }); continue; }
      const full = m[2].toUpperCase();
      let alias = m[3] && !JOIN_STOP.has(m[3].toUpperCase()) ? m[3].toUpperCase() : null;
      if (!alias) rx.lastIndex = m.index + m[0].length - (m[3] ? m[3].length : 0);
      const parts = full.split('.');
      const kind = m[1].toUpperCase();
      tables.push({
        schema: parts.length > 1 ? parts[0] : null,
        name: parts[parts.length - 1],
        alias,
        join: kind === 'JOIN' ? (/\bLEFT\s+(OUTER\s+)?$/i.test(text.slice(0, m.index)) ? 'LEFT' : 'INNER') : kind === ',' ? 'CROSS' : null
      });
    }
    const ons = [];
    const onRx = /\bON\s+([\s\S]+?)(?=\b(?:LEFT|RIGHT|INNER|FULL|CROSS|JOIN)\b|$)/gi;
    while ((m = onRx.exec(text))) ons.push(m[1].trim());
    let k = 0;
    for (const t of tables) if (t.join && t.join !== 'CROSS') t.on = ons[k++] || null;
    return tables;
  }

  /** SELECT 문을 해석한다. */
  function parseSelect(sqlRaw) {
    const res = { errors: [], warnings: [], items: [], tables: [], binds: [], where: null, groupBy: false };
    const sql = stripComments(sqlRaw).replace(/[;\s]+$/, '');
    const err = (code, msg, at) => res.errors.push({ code, msg, line: at == null ? null : lineOf(sqlRaw, at) });
    if (!sql.trim()) { err('ORA-00900', 'SQL 문이 없습니다', 0); return res; }
    const quotes = (sql.match(/'/g) || []).length;
    if (quotes % 2) err('ORA-01756', '인용부호가 올바르게 끝나지 않았습니다', sql.lastIndexOf("'"));
    const { words, balanced } = topLevelWords(sql);
    if (balanced > 0) err('ORA-00907', '오른쪽 괄호가 없습니다', sql.length);
    if (balanced < 0) err('ORA-00933', '괄호가 맞지 않습니다', sql.lastIndexOf(')'));
    const first = words[0];
    if (!first || (first.word !== 'SELECT' && first.word !== 'WITH')) { err('ORA-00900', 'SELECT 문만 매핑할 수 있습니다', first ? first.at : 0); return res; }
    if (first.word === 'WITH') res.warnings.push('WITH 절은 POC 미리보기에서 계산하지 않습니다(실제 구현은 Oracle이 실행)');
    const sel = words.find((w) => w.word === 'SELECT');
    const from = words.find((w) => w.word === 'FROM' && w.at > sel.at);
    if (!from) { err('ORA-00923', 'FROM 키워드가 필요합니다', sql.length); return res; }
    const after = (w) => words.find((x) => x.at > w.at && ['WHERE', 'GROUP', 'ORDER', 'HAVING', 'CONNECT', 'UNION', 'MINUS', 'INTERSECT', 'FETCH'].includes(x.word));
    let listStart = sel.end;
    const second = words[words.indexOf(sel) + 1];
    if (second && (second.word === 'DISTINCT' || second.word === 'UNIQUE') && !sql.slice(sel.end, second.at).trim()) listStart = second.end;
    const list = sql.slice(listStart, from.at);
    if (!list.trim()) err('ORA-00936', '선택 목록이 비어 있습니다', from.at);
    else res.items = splitTopLevel(list, listStart).map((p) => selectItem(p.text, p.at));
    const fromEnd = after(from);
    res.tables = fromTables(sql.slice(from.end, fromEnd ? fromEnd.at : sql.length));
    const where = words.find((w) => w.word === 'WHERE' && w.at > from.at);
    if (where) {
      const end = words.find((x) => x.at > where.at && ['GROUP', 'ORDER', 'HAVING', 'CONNECT', 'UNION', 'FETCH'].includes(x.word));
      const text = sql.slice(where.end, end ? end.at : sql.length);
      const c = X.compile(text);
      res.where = { text: text.trim(), ast: c.ast, error: c.error };
      if (c.error) err(c.error.code, 'WHERE: ' + c.error.message.replace(/^ORA-\d+: /, ''), where.end + (c.error.at || 0));
    }
    const order = words.find((w, k) => w.word === 'ORDER' && words[k + 1] && words[k + 1].word === 'BY' && w.at > from.at);
    res.orderBy = order ? sql.slice(words[words.indexOf(order) + 1].end).trim() : null;
    res.groupBy = words.some((w) => w.word === 'GROUP');
    if (res.groupBy) res.warnings.push('GROUP BY 결과는 POC 미리보기에서 집계하지 않습니다');
    if (words.some((w) => ['UNION', 'MINUS', 'INTERSECT'].includes(w.word))) res.warnings.push('집합 연산(UNION 등)은 첫 SELECT만 미리봅니다');
    for (const it of res.items) if (it.error) err(it.error.code, (it.alias || '항목') + ': ' + it.error.message.replace(/^ORA-\d+: /, ''), it.errorAt != null ? it.errorAt : it.at);
    const bindSet = new Set();
    sql.replace(/('(?:[^']|'')*')|:([A-Za-z_][\w$#]*)/g, (m, s, b) => { if (b) bindSet.add(b.toUpperCase()); return m; });
    res.binds = [...bindSet];
    return res;
  }

  /** 원본 메타데이터로 FROM 테이블·참조 열을 확인하고 결과 열(이름·형식)을 만든다. */
  function describe(parsed, srcMeta) {
    const errors = [];
    const tables = [];
    for (const t of parsed.tables) {
      if (t.subquery) continue;
      const meta = srcMeta.tables.find((x) => x.name === t.name);
      if (!meta) errors.push({ code: 'ORA-00942', msg: '테이블 또는 뷰가 존재하지 않습니다: ' + (t.schema ? t.schema + '.' : '') + t.name });
      else tables.push({ ref: t, meta });
    }
    const resolve = (name) => {
      const dot = name.lastIndexOf('.');
      if (dot > 0) {
        const q = name.slice(0, dot);
        const c = name.slice(dot + 1);
        const t = tables.find((x) => x.ref.alias === q || x.ref.name === q);
        if (!t) return { error: 'ORA-00904: "' + q + '": 부적합한 식별자' };
        const col = t.meta.columns.find((x) => x.name === c);
        return col ? { col, table: t } : { error: 'ORA-00904: "' + name + '": 부적합한 식별자' };
      }
      const hits = tables.filter((t) => t.meta.columns.some((x) => x.name === name));
      if (hits.length > 1) return { error: 'ORA-00918: 열의 정의가 애매합니다: ' + name };
      if (!hits.length) return { error: 'ORA-00904: "' + name + '": 부적합한 식별자' };
      return { col: hits[0].meta.columns.find((x) => x.name === name), table: hits[0] };
    };
    const columns = [];
    for (const it of parsed.items) {
      if (it.star) {
        const q = it.text.includes('.') ? it.text.split('.')[0].toUpperCase() : null;
        for (const t of tables) if (!q || t.ref.alias === q || t.ref.name === q) for (const c of t.meta.columns) columns.push({ name: c.name, type: c.type, expr: c.name, source: c });
        continue;
      }
      if (it.error) { columns.push({ name: it.name, type: null, expr: it.expr, error: it.error.message, alias: it.alias }); continue; }
      let bad = null;
      for (const r of it.refs) { const x = resolve(r); if (x.error) { bad = x.error; break; } }
      if (bad) { errors.push({ code: bad.slice(0, 9), msg: bad.slice(11) + ' (' + it.name + ')' }); columns.push({ name: it.name, type: null, expr: it.expr, error: bad, alias: it.alias }); continue; }
      const type = X.inferType(it.ast, (n) => { const x = resolve(n); return x.col ? x.col.type : null; });
      const single = it.refs.length === 1 ? resolve(it.refs[0]) : null;
      // 결과 열의 통계: 원본 열 통계에 SELECT 식의 효과를 반영(공백만 있는 값은 TRIM 뒤 NULL, NVL·CASE는 NULL 없음)
      const stats = single && single.col ? Object.assign({}, single.col.stats) : {};
      if (/REGEXP_REPLACE/i.test(it.expr) && stats.digitsMaxLen) stats.maxLen = stats.digitsMaxLen;
      if (/TRIM|REGEXP_REPLACE/i.test(it.expr)) { stats.nulls = (stats.nulls || 0) + (stats.blanks || 0); stats.blanks = 0; }
      if (/\bNVL\s*\(|COALESCE|\bCASE\b|DECODE/i.test(it.expr)) stats.nulls = 0;
      const outer = single && single.table && single.table.ref.join === 'LEFT';
      if (outer) delete stats.nulls;
      columns.push({ name: it.name, type, expr: it.expr, alias: it.alias, source: single && single.col, stats, nullable: outer || !single || !single.col ? true : single.col.nullable });
    }
    // WHERE 절 참조 열도 확인
    if (parsed.where && parsed.where.ast) {
      X.walk(parsed.where.ast, (n) => { if (n.k === 'col') { const x = resolve(n.name); if (x.error) errors.push({ code: x.error.slice(0, 9), msg: x.error.slice(11) + ' (WHERE)' }); } });
    }
    return { errors, columns, tables, resolve };
  }

  /** 체크포인트 값을 받는 바인드 변수(CP 표시)가 원본 SQL 안에 있나 */
  function checkpointBind(m, parsed) {
    const b = (m.binds || []).find((x) => x.fromCheckpoint);
    return b && parsed.binds.includes(b.name) ? b : null;
  }

  /**
   * SQL 원본 매핑 검증 — SQL 쪽 검사(구문·객체·별칭·바인드·체크포인트)와 컬럼 매핑 검사(대상 매핑·형식·NULL)를 함께 보인다.
   * 결과: {level, items:[{check, level, detail}], parsed, columns, mapped, total, errorLine}
   */
  function validate(m, srcMeta, tgtMeta) {
    const items = [];
    const add = (check, level, detail) => items.push({ check, level, detail });
    const parsed = parseSelect(m.sql);
    if (parsed.errors.length) {
      add('SQL 구문', 'ERROR', parsed.errors.map((e) => e.code + ': ' + e.msg + (e.line ? ' (' + e.line + '번 줄)' : '')).join('\n'));
      return { level: 'ERROR', items, parsed, columns: [], mapped: 0, total: 0, errorLine: parsed.errors[0].line };
    }
    add('SQL 구문', 'PASS', 'SELECT · 결과 열 ' + parsed.items.length + '개 · FROM ' + parsed.tables.filter((t) => !t.subquery).map((t) => t.name + (t.alias ? ' ' + t.alias : '')).join(', ') + (parsed.warnings.length ? '\n' + parsed.warnings.join('\n') : ''));
    const d = describe(parsed, srcMeta);
    add('원본 객체·열', d.errors.length ? 'ERROR' : 'PASS', d.errors.length ? d.errors.map((e) => e.code + ': ' + e.msg).join('\n') : '참조한 테이블 ' + d.tables.length + '개, 열이 모두 있음');
    const cols = d.columns;
    add('결과 열 수', cols.length ? 'PASS' : 'ERROR', cols.length + '개');
    const noAlias = parsed.items.filter((it) => !it.star && !it.alias && !(it.ast && it.ast.k === 'col'));
    const dup = cols.map((c) => c.name).filter((n, k, a) => a.indexOf(n) !== k);
    add('별칭', dup.length ? 'ERROR' : noAlias.length ? 'WARN' : 'PASS',
      dup.length ? '별칭이 겹칩니다: ' + [...new Set(dup)].join(', ') : noAlias.length ? '별칭 없는 식 ' + noAlias.length + '개: ' + noAlias.map((x) => x.expr.replace(/\s+/g, ' ').slice(0, 40)).join(', ') + ' — AS로 이름을 붙이세요' : '모든 결과 열에 이름이 있음');
    const binds = parsed.binds;
    const missing = binds.filter((b) => !(m.binds || []).some((x) => x.name === b && (String(x.value).trim() !== '' || x.fromCheckpoint)));
    add('바인드 변수', missing.length ? 'ERROR' : 'PASS', binds.length ? binds.map((b) => ':' + b).join(', ') + (missing.length ? ' — 값 없음: ' + missing.map((b) => ':' + b).join(', ') + ' (ORA-01008)' : '') : '없음');

    // 컬럼 매핑(결과 열 = 원본 열) 검사
    const target = tgtMeta.tables.find((t) => t.name === m.target);
    let mapped = 0;
    if (!target) add('대상 컬럼 매핑', 'ERROR', '대상 테이블을 고르세요');
    else {
      const virtual = { name: m.source, columns: cols.filter((c) => !c.error).map(toColumn) };
      const st = M.mappingStatus(m, virtual, target);
      mapped = st.mapped;
      const unused = cols.filter((c) => !m.columns.some((x) => x.source === c.name || X.compile(x.expr || '').refs.includes(c.name)));
      const nn = st.results.filter((r) => r.check.msgs.some((x) => x.level === 'ERROR'));
      add('대상 컬럼 매핑', nn.length ? 'ERROR' : unused.length ? 'WARN' : 'PASS',
        st.mapped + ' / ' + st.total + ' 매핑' +
        (nn.length ? '\n' + nn.map((r) => r.target.name + ': ' + r.check.msgs.find((x) => x.level === 'ERROR').msg).join('\n') : '') +
        (unused.length ? '\n대상에 쓰지 않는 결과 열: ' + unused.map((c) => c.name).join(', ') : ''));
      const typeIssues = [];
      const nullIssues = [];
      for (const r of st.results) {
        for (const x of r.check.msgs) {
          if (x.level === 'PASS' || x.level === 'INFO' || x.level === 'ERROR') continue;
          (/NULL|빈 문자열/.test(x.msg) ? nullIssues : typeIssues).push({ r, x });
        }
      }
      add('형식 호환성', M.worst(typeIssues.map((i) => i.x.level)), typeIssues.length ? typeIssues.map((i) => (i.r.cm.source || '식') + ' → ' + i.r.target.name + ': ' + i.x.msg).join('\n') : mapped + '개 열 모두 호환');
      if (nullIssues.length) add('NULL 처리', M.worst(nullIssues.map((i) => i.x.level)), nullIssues.map((i) => i.r.target.name + ': ' + i.x.msg).join('\n'));
      const needKey = M.modeOf(m.mode).needsKey;
      const covered = new Set(st.results.filter((r) => M.valueSource(r.cm)).map((r) => r.target.name));
      const keys = (m.mergeKey || []).filter((k) => covered.has(k));
      if (needKey) add('병합 키', keys.length ? 'PASS' : 'ERROR', keys.length ? keys.join(', ') + ' (대상 PK ' + target.columns.filter((t) => t.pk).map((t) => t.name).join(', ') + ')' : M.modeOf(m.mode).label + '에는 매핑된 병합 키가 필요합니다');
    }

    if (m.checkpointColumn) {
      const col = cols.find((c) => c.name === m.checkpointColumn);
      const cp = checkpointBind(m, parsed);
      if (!col) add('체크포인트', 'WARN', m.checkpointColumn + '을(를) 결과 열에서 찾지 못함 — 중단하면 처음부터 다시 해야 함');
      else if (cp) add('체크포인트', 'PASS', 'SQL을 감싸 ORDER BY S.' + col.name + '로 읽고, 재개할 때 마지막 커밋 값을 :' + cp.name + '에 넣음(= ' + col.expr.replace(/\s+/g, ' ') + ')');
      else add('체크포인트', 'INFO', 'SQL 안에 체크포인트 바인드 변수가 없어 바깥에서 S.' + col.name + ' > :LAST_ID로 거름.\n큰 원본이면 SQL의 WHERE에 ' + col.expr.replace(/\s+/g, ' ') + ' > :LAST_ID를 넣고 CP로 표시하면 인덱스를 탐');
    } else add('체크포인트', 'INFO', '체크포인트 열이 없음 — 중단하면 처음부터 다시 실행');
    const level = M.worst(items.map((i) => i.level));
    return { level, items, parsed, columns: cols, mapped, total: target ? target.columns.length : 0 };
  }

  /** 결과 열 → 컬럼 매핑이 쓰는 열 모양 */
  function toColumn(c) {
    return { name: c.name, type: c.type, nullable: c.nullable !== false, pk: false, defaultValue: null, comment: c.expr.replace(/\s+/g, ' '), stats: c.stats || {} };
  }

  const virtualCache = new Map();
  /**
   * SQL 원본을 테이블처럼 보이게: {name, kind:'SQL', columns, rows(추정), avgRowLen, comment, error}
   * 컬럼 매핑·검증·엔진이 테이블 원본과 같은 코드로 다룬다.
   */
  function virtualSource(m, srcMeta) {
    const bindKey = (m.binds || []).map((b) => b.name + '=' + b.value).join(',');
    const key = (srcMeta ? srcMeta.schema : '') + '\u0000' + m.source + '\u0000' + m.sql + '\u0000' + bindKey;
    if (virtualCache.has(key)) return virtualCache.get(key);
    const parsed = parseSelect(m.sql);
    let v;
    if (parsed.errors.length || !srcMeta) {
      v = { name: m.source, kind: 'SQL', virtual: true, columns: [], rows: null, avgRowLen: 100, comment: parsed.errors.length ? parsed.errors[0].code + ': ' + parsed.errors[0].msg : '', error: parsed.errors[0] || { msg: '메타데이터 없음' } };
    } else {
      const d = describe(parsed, srcMeta);
      const from = parsed.tables.filter((t) => !t.subquery).map((t) => t.name + (t.alias ? ' ' + t.alias : '')).join(' + ');
      v = {
        name: m.source, kind: 'SQL', virtual: true, columns: d.columns.filter((c) => !c.error).map(toColumn),
        rows: estimateRows(m, parsed, d), avgRowLen: 110, comment: 'FROM ' + from, error: d.errors[0] || null, parsed,
        baseRows: ((d.tables.find((t) => !t.ref.join) || d.tables[0] || {}).meta || {}).rows || null
      };
    }
    if (virtualCache.size > 100) virtualCache.clear();
    virtualCache.set(key, v);
    return v;
  }

  /** 결과 행 수 추정: 주 테이블 행 수(통계)에서 "PK > :바인드" 조건만큼 뺀다. 실제 구현은 COUNT(*) 또는 실행 계획의 CARDINALITY */
  function estimateRows(m, parsed, d) {
    const main = d.tables.find((t) => !t.ref.join) || d.tables[0];
    if (!main || main.meta.rows == null) return null;
    let rows = main.meta.rows;
    const pk = main.meta.columns.find((c) => c.pk);
    const hit = parsed.where && pk && new RegExp('(?:\\w+\\.)?' + pk.name + '\\s*>\\s*:(\\w+)', 'i').exec(parsed.where.text);
    if (hit) {
      const b = (m.binds || []).find((x) => x.name === hit[1].toUpperCase());
      if (b && /^\d+$/.test(String(b.value))) rows = Math.max(0, rows - Number(b.value));
    }
    if (parsed.groupBy) rows = Math.round(rows / 10);
    return rows;
  }

  /** 미리보기: Mock 샘플 행으로 FROM·JOIN·WHERE를 흉내 내고 선택 목록을 계산한다. */
  function preview(sm, srcMeta, limit) {
    const parsed = parseSelect(sm.sql);
    if (parsed.errors.length) return { error: parsed.errors[0].code + ': ' + parsed.errors[0].msg };
    const d = describe(parsed, srcMeta);
    if (d.errors.length) return { error: d.errors[0].code + ': ' + d.errors[0].msg };
    const binds = {};
    for (const b of sm.binds || []) binds[b.name] = b.type === 'NUMBER' && String(b.value).trim() !== '' ? Number(b.value) : b.value;
    const main = d.tables.find((t) => !t.ref.join) || d.tables[0];
    if (!main) return { error: '원본 테이블이 없습니다' };
    // WHERE에 "PK > :바인드"가 있으면 그 다음 값부터 샘플을 만든다(증분·체크포인트 미리보기)
    let fromId = 1;
    const pk = main.meta.columns.find((c) => c.pk);
    const m = parsed.where && new RegExp('(?:\\w+\\.)?' + (pk ? pk.name : '#') + '\\s*>\\s*:(\\w+)', 'i').exec(parsed.where.text);
    if (m && binds[m[1].toUpperCase()] != null) fromId = Number(binds[m[1].toUpperCase()]) + 1;
    const base = MS.mockDb.sampleRows(main.meta.name, Math.max(limit * 2, 40), { fromId });
    const joins = d.tables.filter((t) => t !== main).map((t) => ({ t, rows: MS.mockDb.sampleRows(t.meta.name, 50, { seed: 11 }), on: t.ref.on ? X.compile(t.ref.on).ast : null }));
    const put = (ctx, t, row) => {
      const q = t.ref.alias || t.ref.name;
      for (const c of t.meta.columns) {
        const v = row ? row[c.name] : null;
        ctx[q + '.' + c.name] = v === undefined ? null : v;
        if (!(c.name in ctx)) ctx[c.name] = v === undefined ? null : v;
      }
    };
    const rows = [];
    let errors = 0;
    for (const r of base) {
      if (rows.length >= limit) break;
      const ctx = { __strict: true };
      put(ctx, main, r);
      let skip = false;
      for (const j of joins) {
        const hit = j.on ? j.rows.find((jr) => { const c = Object.assign({}, ctx); put(c, j.t, jr); try { return X.evaluate(j.on, { row: c, binds }) === true; } catch (e) { return false; } }) : j.rows[0];
        if (!hit && j.t.ref.join === 'INNER') { skip = true; break; }
        put(ctx, j.t, hit || null);
      }
      if (skip) continue;
      if (parsed.where && parsed.where.ast) {
        try { if (X.evaluate(parsed.where.ast, { row: ctx, binds }) !== true) continue; } catch (e) { return { error: e.message }; }
      }
      const out = d.columns.map((c) => {
        const r1 = X.run(c.expr, ctx, binds);
        if (r1.error) errors++;
        return r1.error ? { error: r1.error.message } : { value: r1.value };
      });
      rows.push(out);
    }
    return { columns: d.columns, rows, errors, warnings: parsed.warnings, fromId };
  }

  MS.sqlMapping = { stripComments, parseSelect, describe, validate, preview, virtualSource, estimateRows, checkpointBind, toColumn };
})();
