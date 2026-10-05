/*
 * Transform 식 엔진(미리보기용). Oracle 식의 일부를 해석·평가하고 결과 형식을 추정한다.
 * - 실제 이관은 이 엔진을 쓰지 않는다: 변환식은 원본 SELECT 목록에 그대로 넣어 Oracle이 계산한다(서버 쪽 변환).
 *   이 엔진은 화면의 샘플 미리보기·별칭 형식 추정·참조 열 검사에만 쓴다.
 * - Oracle 규칙 일부를 따른다: 빈 문자열('')은 NULL, || 연결은 NULL을 빈 문자열로 본다, 비교에 NULL이 있으면 알 수 없음.
 */
(function () {
  'use strict';
  const MS = window.MS;

  class OraError extends Error {
    constructor(code, message, at) {
      super(code + ': ' + message);
      this.code = code;
      this.at = at;
    }
  }

  // ================= 형식 =================
  /** 'NUMBER(15,2)' → {base:'NUMBER', len:null, prec:15, scale:2} */
  function parseType(type) {
    if (!type) return null;
    const m = /^([A-Z0-9_ ]+?)\s*(?:\((\d+)(?:\s*,\s*(\d+))?\))?$/i.exec(String(type).trim());
    if (!m) return { base: String(type).toUpperCase() };
    const base = m[1].toUpperCase();
    const a = m[2] == null ? null : Number(m[2]);
    const b = m[3] == null ? null : Number(m[3]);
    if (base === 'NUMBER') return { base, prec: a, scale: b == null ? (a == null ? null : 0) : b };
    if (base === 'TIMESTAMP') return { base, frac: a == null ? 6 : a };
    return { base, len: a };
  }
  const isChar = (t) => t && (t.base === 'VARCHAR2' || t.base === 'CHAR' || t.base === 'NVARCHAR2' || t.base === 'VARCHAR');
  const isDate = (t) => t && (t.base === 'DATE' || t.base === 'TIMESTAMP');

  // ================= 토큰 =================
  const TOKEN_RX = /\s+|--[^\n]*|\/\*[\s\S]*?\*\/|('(?:[^']|'')*')|(\d+(?:\.\d+)?)|(:[A-Za-z_][\w$#]*)|("[^"]+"|[A-Za-z_][\w$#]*)(\s*\.\s*(?:"[^"]+"|[A-Za-z_][\w$#]*|\*))?|(\|\||<>|!=|>=|<=|[-+*\/(),=<>])/y;

  function tokenize(src) {
    const out = [];
    let pos = 0;
    while (pos < src.length) {
      TOKEN_RX.lastIndex = pos;
      const m = TOKEN_RX.exec(src);
      if (!m) {
        if (src[pos] === "'") throw new OraError('ORA-01756', '인용부호가 올바르게 끝나지 않았습니다', pos);
        throw new OraError('ORA-00911', "문자가 부적합합니다: '" + src[pos] + "'", pos);
      }
      const at = pos;
      pos = TOKEN_RX.lastIndex;
      if (m[1]) out.push({ t: 'str', v: m[1].slice(1, -1).replace(/''/g, "'"), at, raw: m[1] });
      else if (m[2]) out.push({ t: 'num', v: Number(m[2]), at, raw: m[2] });
      else if (m[3]) out.push({ t: 'bind', v: m[3].slice(1).toUpperCase(), at, raw: m[3] });
      else if (m[4]) {
        const head = m[4].startsWith('"') ? m[4].slice(1, -1) : m[4].toUpperCase();
        const tail = m[5] ? m[5].replace(/[\s.]/g, '').replace(/"/g, '') : null;
        const name = tail ? head + '.' + (tail === '*' ? '*' : m[5].includes('"') ? tail : tail.toUpperCase()) : head;
        out.push({ t: 'id', v: name, at, raw: m[0], quoted: m[4].startsWith('"') });
      } else if (m[6]) out.push({ t: 'op', v: m[6], at, raw: m[6] });
    }
    return out;
  }

  // ================= 해석 =================
  const RESERVED = new Set(['SELECT', 'FROM', 'WHERE', 'AS', 'THEN', 'ELSE', 'END', 'WHEN', 'AND', 'OR', 'ON', 'JOIN', 'GROUP', 'ORDER', 'BY', 'HAVING', 'IS', 'IN', 'LIKE', 'BETWEEN']);

  function parse(src) {
    const toks = tokenize(src);
    let i = 0;
    const peek = (o) => toks[i + (o || 0)];
    const isKw = (tok, w) => tok && tok.t === 'id' && !tok.quoted && tok.v === w;
    const isOp = (tok, o) => tok && tok.t === 'op' && tok.v === o;
    const next = () => toks[i++];
    const fail = (code, msg) => {
      const tok = peek();
      return new OraError(code, msg + (tok ? " ('" + tok.raw + "' 근처)" : ' (식 끝)'), tok ? tok.at : src.length);
    };
    const expectOp = (o) => {
      if (!isOp(peek(), o)) throw fail(o === ')' ? 'ORA-00907' : 'ORA-00936', o === ')' ? '오른쪽 괄호가 없습니다' : "'" + o + "'가 필요합니다");
      return next();
    };
    const expectKw = (w) => {
      if (!isKw(peek(), w)) throw fail(w === 'END' ? 'ORA-00905' : 'ORA-00905', '키워드가 없습니다: ' + w);
      next();
    };

    function expr(minPrec) {
      let left;
      if (isKw(peek(), 'NOT')) {
        next();
        left = { k: 'not', e: expr(4) };
      } else left = unary();
      for (;;) {
        const tok = peek();
        if (!tok) break;
        let prec = 0;
        const notNext = isKw(tok, 'NOT') && (isKw(peek(1), 'LIKE') || isKw(peek(1), 'IN') || isKw(peek(1), 'BETWEEN'));
        if (isKw(tok, 'OR')) prec = 1;
        else if (isKw(tok, 'AND')) prec = 2;
        else if (tok.t === 'op' && ['=', '<>', '!=', '<', '>', '<=', '>='].includes(tok.v)) prec = 4;
        else if (isKw(tok, 'IS') || isKw(tok, 'LIKE') || isKw(tok, 'IN') || isKw(tok, 'BETWEEN') || notNext) prec = 4;
        else if (tok.t === 'op' && ['||', '+', '-'].includes(tok.v)) prec = 5;
        else if (tok.t === 'op' && ['*', '/'].includes(tok.v)) prec = 6;
        if (!prec || prec < minPrec) break;
        next();
        if (isKw(tok, 'IS')) {
          const neg = isKw(peek(), 'NOT');
          if (neg) next();
          expectKw('NULL');
          left = { k: 'isnull', e: left, neg };
          continue;
        }
        let neg = false;
        let word = tok;
        if (notNext) {
          neg = true;
          word = next();
        }
        if (isKw(word, 'LIKE')) left = { k: 'like', e: left, p: expr(5), neg };
        else if (isKw(word, 'IN')) {
          expectOp('(');
          const list = [expr(1)];
          while (isOp(peek(), ',')) { next(); list.push(expr(1)); }
          expectOp(')');
          left = { k: 'in', e: left, list, neg };
        } else if (isKw(word, 'BETWEEN')) {
          const lo = expr(5);
          expectKw('AND');
          left = { k: 'between', e: left, lo, hi: expr(5), neg };
        } else left = { k: 'bin', op: isKw(tok, 'AND') ? 'AND' : isKw(tok, 'OR') ? 'OR' : tok.v, l: left, r: expr(prec + 1) };
      }
      return left;
    }

    function unary() {
      if (isOp(peek(), '-')) { next(); return { k: 'neg', e: unary() }; }
      if (isOp(peek(), '+')) { next(); return unary(); }
      return primary();
    }

    function typeName() {
      const t = next();
      if (!t || t.t !== 'id') throw fail('ORA-00902', '데이터 형식이 부적합합니다');
      let s = t.v;
      if (isOp(peek(), '(')) {
        next();
        const a = next();
        if (!a || a.t !== 'num') throw fail('ORA-00902', '데이터 형식이 부적합합니다');
        s += '(' + a.v;
        if (isOp(peek(), ',')) { next(); s += ',' + next().v; }
        expectOp(')');
        s += ')';
      }
      return s;
    }

    function caseExpr() {
      let subject = null;
      if (!isKw(peek(), 'WHEN')) subject = expr(1);
      const whens = [];
      while (isKw(peek(), 'WHEN')) {
        next();
        const cond = expr(1);
        expectKw('THEN');
        whens.push({ cond, val: expr(1) });
      }
      if (!whens.length) throw fail('ORA-00905', '키워드가 없습니다: WHEN');
      let els = null;
      if (isKw(peek(), 'ELSE')) { next(); els = expr(1); }
      expectKw('END');
      return { k: 'case', subject, whens, els };
    }

    function primary() {
      const tok = next();
      if (!tok) throw fail('ORA-00936', '식이 없습니다');
      if (tok.t === 'num') return { k: 'lit', v: tok.v };
      if (tok.t === 'str') return { k: 'lit', v: tok.v === '' ? null : tok.v, str: true };
      if (tok.t === 'bind') return { k: 'bind', name: tok.v };
      if (isOp(tok, '(')) {
        const e = expr(1);
        expectOp(')');
        return e;
      }
      if (tok.t !== 'id') { i--; throw fail('ORA-00936', '식이 없습니다'); }
      const w = tok.quoted ? null : tok.v;
      if (w === 'NULL') return { k: 'lit', v: null };
      if (w === 'CASE') return caseExpr();
      if (w === 'SYSDATE' || w === 'CURRENT_DATE') return { k: 'now', ts: false };
      if (w === 'SYSTIMESTAMP' || w === 'CURRENT_TIMESTAMP') return { k: 'now', ts: true };
      if (w === 'CAST') {
        expectOp('(');
        const e = expr(1);
        expectKw('AS');
        const type = typeName();
        expectOp(')');
        return { k: 'cast', e, type };
      }
      if (w && RESERVED.has(w)) { i--; throw fail('ORA-00936', '식이 없습니다'); }
      if (isOp(peek(), '(')) {
        next();
        const args = [];
        if (isOp(peek(), '*')) { next(); args.push({ k: 'lit', v: 1 }); }
        else if (!isOp(peek(), ')')) {
          if (isKw(peek(), 'DISTINCT')) next();
          args.push(expr(1));
          while (isOp(peek(), ',')) { next(); args.push(expr(1)); }
        }
        expectOp(')');
        return { k: 'fn', name: tok.v, args, at: tok.at };
      }
      return { k: 'col', name: tok.v, at: tok.at };
    }

    if (!toks.length) throw new OraError('ORA-00936', '식이 없습니다', 0);
    const ast = expr(1);
    if (i < toks.length) throw fail('ORA-00933', 'SQL 명령어가 올바르게 종료되지 않았습니다');
    return ast;
  }

  const cache = new Map();
  /** 식을 해석한다. {ast, refs:[열 이름], binds:[바인드 이름], error} — 오류도 캐시한다. */
  function compile(src) {
    const key = String(src || '');
    if (cache.has(key)) return cache.get(key);
    let result;
    try {
      const ast = parse(key);
      const refs = new Set();
      const binds = new Set();
      walk(ast, (n) => {
        if (n.k === 'col') refs.add(n.name);
        if (n.k === 'bind') binds.add(n.name);
      });
      result = { ast, refs: [...refs], binds: [...binds], error: null };
    } catch (e) {
      result = { ast: null, refs: [], binds: [], error: e };
    }
    if (cache.size > 500) cache.clear();
    cache.set(key, result);
    return result;
  }

  function walk(n, fn) {
    if (!n || typeof n !== 'object') return;
    fn(n);
    for (const key of ['e', 'l', 'r', 'p', 'lo', 'hi', 'subject', 'els']) if (n[key]) walk(n[key], fn);
    for (const a of n.args || []) walk(a, fn);
    for (const a of n.list || []) walk(a, fn);
    for (const w of n.whens || []) { walk(w.cond, fn); walk(w.val, fn); }
  }

  // ================= 평가 =================
  const nz = (s) => (s === '' ? null : s);
  function tsDate(d, ts) {
    const c = new Date(d.getTime());
    if (ts) Object.defineProperty(c, 'isTimestamp', { value: true });
    return c;
  }
  const pad = (n, w) => String(n).padStart(w || 2, '0');
  function fmtDate(d, f) {
    return f.replace(/YYYY|HH24|HH|MI|MM|DD|SS|FF\d?|YY/g, (t) => {
      switch (t) {
        case 'YYYY': return d.getFullYear();
        case 'YY': return pad(d.getFullYear() % 100);
        case 'MM': return pad(d.getMonth() + 1);
        case 'DD': return pad(d.getDate());
        case 'HH24': return pad(d.getHours());
        case 'HH': return pad(d.getHours() % 12 || 12);
        case 'MI': return pad(d.getMinutes());
        case 'SS': return pad(d.getSeconds());
        default: return pad(d.getMilliseconds() * 1000, 6).slice(0, Number(t.slice(2)) || 6);
      }
    });
  }
  function str(v) {
    if (v instanceof Date) return fmtDate(v, v.isTimestamp ? 'YYYY-MM-DD HH24:MI:SS.FF6' : 'YYYY-MM-DD HH24:MI:SS');
    return String(v);
  }
  function parseDate(s, f) {
    if (s == null) return null;
    if (s instanceof Date) return s;
    const t = String(s).trim();
    let m = /^(\d{4})-?(\d{2})-?(\d{2})(?:[ T](\d{2}):?(\d{2}):?(\d{2})?)?/.exec(t);
    if (!m) throw new OraError('ORA-01861', '리터럴이 형식 문자열과 일치하지 않습니다' + (f ? ' (' + f + ')' : ''));
    return new Date(+m[1], +m[2] - 1, +m[3], +(m[4] || 0), +(m[5] || 0), +(m[6] || 0));
  }
  function toNum(v) {
    if (v == null) return null;
    if (typeof v === 'number') return v;
    const n = Number(String(v).trim());
    if (String(v).trim() === '' || Number.isNaN(n)) throw new OraError('ORA-01722', '수치가 부적합합니다: ' + JSON.stringify(v));
    return n;
  }
  function cmp(a, b) {
    if (a instanceof Date || b instanceof Date) {
      a = a instanceof Date ? a.getTime() : parseDate(a).getTime();
      b = b instanceof Date ? b.getTime() : parseDate(b).getTime();
    } else if (typeof a === 'number' || typeof b === 'number') {
      a = toNum(a);
      b = toNum(b);
    } else {
      a = String(a);
      b = String(b);
    }
    return a < b ? -1 : a > b ? 1 : 0;
  }
  function oraRegex(p) {
    const fixed = String(p)
      .replace(/\[:digit:\]/g, '0-9').replace(/\[:alpha:\]/g, 'A-Za-z').replace(/\[:alnum:\]/g, 'A-Za-z0-9')
      .replace(/\[:space:\]/g, '\\s').replace(/\[:upper:\]/g, 'A-Z').replace(/\[:lower:\]/g, 'a-z').replace(/\[:punct:\]/g, '!-\\/:-@\\[-`{-~');
    try {
      return new RegExp(fixed, 'g');
    } catch (e) {
      throw new OraError('ORA-12725', '정규식의 괄호가 맞지 않습니다');
    }
  }
  function substr(s, p, n) {
    if (s == null || p == null) return null;
    s = str(s);
    p = Math.trunc(toNum(p));
    let start = p > 0 ? p - 1 : p < 0 ? s.length + p : 0;
    if (start < 0) return null;
    const out = n == null ? s.slice(start) : toNum(n) < 1 ? '' : s.substr(start, Math.trunc(toNum(n)));
    return nz(out);
  }

  const FUNCS = {
    TRIM: (s) => (s == null ? null : nz(str(s).replace(/^ +| +$/g, ''))),
    LTRIM: (s, set) => (s == null ? null : nz(str(s).replace(new RegExp('^[' + escapeClass(set == null ? ' ' : set) + ']+'), ''))),
    RTRIM: (s, set) => (s == null ? null : nz(str(s).replace(new RegExp('[' + escapeClass(set == null ? ' ' : set) + ']+$'), ''))),
    UPPER: (s) => (s == null ? null : str(s).toUpperCase()),
    LOWER: (s) => (s == null ? null : str(s).toLowerCase()),
    INITCAP: (s) => (s == null ? null : str(s).toLowerCase().replace(/(^|[^a-z0-9])([a-z])/g, (m, a, b) => a + b.toUpperCase())),
    NVL: (a, b) => (a == null ? b : a),
    NVL2: (a, b, c) => (a != null ? b : c),
    COALESCE: (...a) => { for (const x of a) if (x != null) return x; return null; },
    DECODE: (e, ...rest) => {
      for (let k = 0; k + 1 < rest.length; k += 2) {
        if (e == null ? rest[k] == null : rest[k] != null && cmp(e, rest[k]) === 0) return rest[k + 1];
      }
      return rest.length % 2 ? rest[rest.length - 1] : null;
    },
    REGEXP_REPLACE: (s, p, r) => (s == null ? null : p == null ? s : nz(str(s).replace(oraRegex(p), r == null ? '' : str(r).replace(/\\(\d)/g, '$$$1')))),
    REGEXP_SUBSTR: (s, p) => { if (s == null) return null; const m = str(s).match(oraRegex(p)); return m ? nz(m[0]) : null; },
    REPLACE: (s, a, b) => (s == null ? null : a == null ? s : nz(str(s).split(str(a)).join(b == null ? '' : str(b)))),
    SUBSTR: substr,
    LENGTH: (s) => (s == null ? null : str(s).length),
    LPAD: (s, n, p) => (s == null ? null : nz(str(s).padStart(toNum(n), p == null ? ' ' : str(p)).slice(0, toNum(n)))),
    RPAD: (s, n, p) => (s == null ? null : nz(str(s).padEnd(toNum(n), p == null ? ' ' : str(p)).slice(0, toNum(n)))),
    TO_CHAR: (v, f) => (v == null ? null : v instanceof Date ? fmtDate(v, f || 'YYYY-MM-DD HH24:MI:SS') : String(v)),
    TO_DATE: (s, f) => (s == null ? null : tsDate(parseDate(s, f), false)),
    TO_TIMESTAMP: (s, f) => (s == null ? null : tsDate(parseDate(s, f), true)),
    TO_NUMBER: toNum,
    ROUND: (v, n) => (v == null ? null : Math.round(toNum(v) * Math.pow(10, n || 0)) / Math.pow(10, n || 0)),
    TRUNC: (v, n) => {
      if (v == null) return null;
      if (v instanceof Date) return tsDate(new Date(v.getFullYear(), v.getMonth(), v.getDate()), false);
      const f = Math.pow(10, n || 0);
      return Math.trunc(toNum(v) * f) / f;
    },
    ABS: (v) => (v == null ? null : Math.abs(toNum(v))),
    MOD: (a, b) => (a == null || b == null ? null : toNum(a) % toNum(b)),
    ORA_HASH: (v) => {
      if (v == null) return null;
      let x = 0;
      for (const ch of str(v)) x = (Math.imul(x, 31) + ch.charCodeAt(0)) >>> 0;
      return x % 4294967295;
    }
  };
  function escapeClass(s) {
    return String(s).replace(/[\]\\^-]/g, '\\$&');
  }

  function castTo(v, type) {
    if (v == null) return null;
    const t = parseType(type);
    if (t.base === 'TIMESTAMP' || t.base === 'DATE') {
      if (typeof v === 'number') throw new OraError('ORA-00932', '일관성 없는 데이터 유형: DATE가 필요하나 NUMBER임');
      return tsDate(parseDate(v), t.base === 'TIMESTAMP');
    }
    if (t.base === 'NUMBER') {
      if (v instanceof Date) throw new OraError('ORA-00932', '일관성 없는 데이터 유형: NUMBER가 필요하나 DATE임');
      const n = toNum(v);
      return t.scale != null ? Number(n.toFixed(t.scale)) : n;
    }
    if (isChar(t)) {
      const s = str(v);
      if (t.len != null && s.length > t.len) throw new OraError('ORA-25137', '데이터 값이 ' + type + '에 맞지 않습니다(' + s.length + '자)');
      return t.base === 'CHAR' && t.len ? s.padEnd(t.len) : nz(s);
    }
    return v;
  }

  function truth(v) {
    return v == null ? null : !!v;
  }

  function lookup(row, name) {
    if (Object.prototype.hasOwnProperty.call(row, name)) return row[name];
    const dot = name.lastIndexOf('.');
    if (dot > 0) {
      const bare = name.slice(dot + 1);
      if (!row.__strict && Object.prototype.hasOwnProperty.call(row, bare)) return row[bare];
    }
    throw new OraError('ORA-00904', '"' + name + '": 부적합한 식별자');
  }

  function evaluate(n, ctx) {
    const ev = (x) => evaluate(x, ctx);
    switch (n.k) {
      case 'lit': return n.v;
      case 'bind':
        if (!ctx.binds || !(n.name in ctx.binds)) throw new OraError('ORA-01008', '바인드 변수 :' + n.name + '의 값이 없습니다');
        return ctx.binds[n.name];
      case 'col': return lookup(ctx.row, n.name);
      case 'now': return tsDate(ctx.now || new Date(), n.ts);
      case 'neg': { const v = ev(n.e); return v == null ? null : -toNum(v); }
      case 'not': { const t = truth(ev(n.e)); return t == null ? null : !t; }
      case 'isnull': { const v = ev(n.e); return n.neg ? v != null : v == null; }
      case 'like': {
        const v = ev(n.e); const p = ev(n.p);
        if (v == null || p == null) return null;
        const rx = new RegExp('^' + String(p).replace(/[.+?^${}()|[\]\\*]/g, '\\$&').replace(/%/g, '.*').replace(/_/g, '.') + '$', 's');
        return rx.test(str(v)) !== n.neg;
      }
      case 'in': {
        const v = ev(n.e);
        if (v == null) return null;
        const hit = n.list.some((x) => { const y = ev(x); return y != null && cmp(v, y) === 0; });
        return hit !== n.neg;
      }
      case 'between': {
        const v = ev(n.e); const lo = ev(n.lo); const hi = ev(n.hi);
        if (v == null || lo == null || hi == null) return null;
        return (cmp(v, lo) >= 0 && cmp(v, hi) <= 0) !== n.neg;
      }
      case 'case': {
        if (n.subject) {
          const s = ev(n.subject);
          for (const w of n.whens) { const c = ev(w.cond); if (s != null && c != null && cmp(s, c) === 0) return ev(w.val); }
        } else {
          for (const w of n.whens) if (truth(ev(w.cond)) === true) return ev(w.val);
        }
        return n.els ? ev(n.els) : null;
      }
      case 'cast': return castTo(ev(n.e), n.type);
      case 'fn': {
        const f = FUNCS[n.name];
        if (!f) throw new OraError('ORA-00904', '"' + n.name + '": 부적합한 식별자');
        return f(...n.args.map(ev));
      }
      case 'bin': {
        if (n.op === 'AND') {
          const a = truth(ev(n.l)); if (a === false) return false;
          const b = truth(ev(n.r)); if (b === false) return false;
          return a == null || b == null ? null : true;
        }
        if (n.op === 'OR') {
          const a = truth(ev(n.l)); if (a === true) return true;
          const b = truth(ev(n.r)); if (b === true) return true;
          return a == null || b == null ? null : false;
        }
        const a = ev(n.l); const b = ev(n.r);
        if (n.op === '||') return nz((a == null ? '' : str(a)) + (b == null ? '' : str(b)));
        if (a == null || b == null) return null;
        switch (n.op) {
          case '=': return cmp(a, b) === 0;
          case '<>': case '!=': return cmp(a, b) !== 0;
          case '<': return cmp(a, b) < 0;
          case '>': return cmp(a, b) > 0;
          case '<=': return cmp(a, b) <= 0;
          case '>=': return cmp(a, b) >= 0;
          case '+': case '-':
            if (a instanceof Date) return tsDate(new Date(a.getTime() + (n.op === '+' ? 1 : -1) * toNum(b) * 864e5), a.isTimestamp);
            return n.op === '+' ? toNum(a) + toNum(b) : toNum(a) - toNum(b);
          case '*': return toNum(a) * toNum(b);
          case '/':
            if (toNum(b) === 0) throw new OraError('ORA-01476', '제수가 0입니다');
            return toNum(a) / toNum(b);
        }
      }
    }
    throw new OraError('ORA-00936', '식이 없습니다');
  }

  /** 식을 한 행에 대해 계산한다. {value} 또는 {error} */
  function run(src, row, binds) {
    const c = compile(src);
    if (c.error) return { error: c.error };
    try {
      return { value: evaluate(c.ast, { row, binds: binds || {}, now: new Date(2026, 9, 3, 14, 0, 0) }) };
    } catch (e) {
      return { error: e };
    }
  }

  // ================= 형식 추정 =================
  /** 식 결과의 Oracle 형식을 추정한다. colType(name) → 'VARCHAR2(30)' 또는 null */
  function inferType(n, colType) {
    const it = (x) => inferType(x, colType);
    const len = (t) => { const p = parseType(t); return p && p.len != null ? p.len : p && p.base === 'NUMBER' ? (p.prec || 38) + 2 : p && isDate(p) ? 26 : 4000; };
    switch (n.k) {
      case 'lit':
        if (n.v == null) return null;
        return typeof n.v === 'number' ? 'NUMBER' : 'CHAR(' + String(n.v).length + ')';
      case 'col': return colType(n.name);
      case 'bind': return null;
      case 'now': return n.ts ? 'TIMESTAMP' : 'DATE';
      case 'cast': return n.type;
      case 'neg': return 'NUMBER';
      case 'case': {
        const types = n.whens.map((w) => it(w.val)).concat(n.els ? [it(n.els)] : []).filter(Boolean);
        return mergeTypes(types);
      }
      case 'bin':
        if (n.op === '||') return 'VARCHAR2(' + Math.min(4000, len(it(n.l)) + len(it(n.r))) + ')';
        if (['+', '-'].includes(n.op)) { const l = it(n.l); return l && isDate(parseType(l)) ? l : 'NUMBER'; }
        if (['*', '/'].includes(n.op)) return 'NUMBER';
        return null;
      case 'fn': {
        const a0 = n.args[0] ? it(n.args[0]) : null;
        switch (n.name) {
          case 'TRIM': case 'LTRIM': case 'RTRIM': case 'UPPER': case 'LOWER': case 'INITCAP': case 'REGEXP_REPLACE': case 'REPLACE': case 'REGEXP_SUBSTR':
            return 'VARCHAR2(' + len(a0) + ')';
          case 'SUBSTR': {
            const l = n.args[2] && n.args[2].k === 'lit' ? n.args[2].v : len(a0);
            return 'VARCHAR2(' + Math.min(l, len(a0)) + ')';
          }
          case 'LPAD': case 'RPAD': return 'VARCHAR2(' + (n.args[1] && n.args[1].k === 'lit' ? n.args[1].v : 4000) + ')';
          case 'NVL': case 'COALESCE': case 'NVL2': return mergeTypes(n.args.slice(n.name === 'NVL2' ? 1 : 0).map(it).filter(Boolean));
          case 'DECODE': return mergeTypes(n.args.filter((x, k) => k > 1 && k % 2 === 0).concat(n.args.length % 2 === 0 ? [n.args[n.args.length - 1]] : []).map(it).filter(Boolean));
          case 'TO_CHAR': return 'VARCHAR2(' + (a0 && isDate(parseType(a0)) ? 26 : len(a0)) + ')';
          case 'TO_DATE': case 'TRUNC': return n.name === 'TRUNC' && a0 && !isDate(parseType(a0)) ? 'NUMBER' : 'DATE';
          case 'TO_TIMESTAMP': return 'TIMESTAMP';
          case 'MAX': case 'MIN': return a0;
          default: return 'NUMBER';
        }
      }
    }
    return null;
  }

  function mergeTypes(types) {
    if (!types.length) return null;
    const parsed = types.map(parseType);
    if (parsed.every(isChar)) {
      const max = Math.max(...parsed.map((p) => p.len || 0));
      const allChar = parsed.every((p) => p.base === 'CHAR') && parsed.every((p) => p.len === max);
      return (allChar ? 'CHAR(' : 'VARCHAR2(') + (max || 4000) + ')';
    }
    if (parsed.every((p) => p.base === 'NUMBER')) {
      const precs = parsed.map((p) => p.prec);
      if (precs.some((p) => p == null)) return 'NUMBER';
      return 'NUMBER(' + Math.max(...precs) + (parsed.some((p) => p.scale) ? ',' + Math.max(...parsed.map((p) => p.scale || 0)) : '') + ')';
    }
    if (parsed.every(isDate)) return parsed.some((p) => p.base === 'TIMESTAMP') ? 'TIMESTAMP' : 'DATE';
    return types[0];
  }

  /** 화면 표시용 값 */
  function display(v) {
    if (v == null) return { text: 'NULL', isNull: true };
    if (v instanceof Date) return { text: str(v) };
    if (typeof v === 'boolean') return { text: v ? 'TRUE' : 'FALSE' };
    return { text: String(v) };
  }

  MS.expr = { OraError, parseType, isChar, isDate, tokenize, parse, compile, evaluate, run, inferType, mergeTypes, display, str, walk };
})();
