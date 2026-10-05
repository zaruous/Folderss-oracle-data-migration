/*
 * 매핑 서비스: 이름 자동 매칭(테이블·컬럼), 형식 호환성, 컬럼 매핑 검사, NULL 처리 규칙.
 * WPF 구현에서는 MappingService(C#)로 옮긴다. 화면과 무관한 순수 함수만 둔다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const X = MS.expr;

  // 용어 사전: 이름이 다른 테이블·컬럼을 잇는 규칙. 작업 템플릿에 함께 저장해 다음 작업에서 다시 쓴다.
  const TABLE_DICT = { CUSTOMER: 'MEMBER', CUSTOMER_GRADE: 'MEMBER_GRADE', ORDER: 'SALES_ORDER', ORDER_ITEM: 'SALES_ORDER_ITEM', CODE_MST: 'COMMON_CODE' };
  const COLUMN_DICT = {
    CUSTOMER_ID: 'MEMBER_ID', CUSTOMER_NM: 'MEMBER_NAME', PHONE_NO: 'MOBILE_NO', STATUS_CD: 'USE_YN', REG_DT: 'CREATED_AT', MOD_DT: 'UPDATED_AT',
    GRADE_CD: 'GRADE_CODE', GRADE_NM: 'GRADE_NAME', SORT_SEQ: 'DISPLAY_ORDER', ORDER_DT: 'ORDERED_AT', ORDER_AMT: 'TOTAL_AMT',
    ORDER_STAT_CD: 'ORDER_STATUS', PRODUCT_CD: 'PRODUCT_ID', PRODUCT_NM: 'PRODUCT_NAME', CATEGORY_CD: 'CATEGORY_ID', PRICE: 'LIST_PRICE',
    USE_FLAG: 'USE_YN', ITEM_SEQ: 'LINE_NO', QTY: 'QUANTITY', CODE_GRP: 'GROUP_CODE', CODE_NM: 'CODE_NAME'
  };
  const SUFFIX_RULES = [['_NM', '_NAME'], ['_CD', '_CODE'], ['_DT', '_AT'], ['_DT', '_DATE'], ['_FLAG', '_YN']];

  const MODES = [
    { value: 'INSERT_ONLY', label: 'INSERT ONLY', needsKey: false },
    { value: 'MERGE', label: 'INSERT + UPDATE', needsKey: true },
    { value: 'TRUNCATE_INSERT', label: 'TRUNCATE + INSERT', needsKey: false, destructive: true },
    { value: 'DELETE_INSERT', label: 'DELETE + INSERT', needsKey: true, destructive: true }
  ];
  const modeOf = (v) => MODES.find((m) => m.value === v) || MODES[0];

  const NULL_RULES = [
    { value: 'ALLOW', label: 'NULL 허용' },
    { value: 'REJECT', label: '행 거부' },
    { value: 'DEFAULT', label: '기본값' },
    { value: 'SYSDATE', label: 'SYSDATE' },
    { value: 'EMPTY', label: '빈 문자열' },
    { value: 'CUSTOM', label: '사용자 식' }
  ];

  const LEVEL_RANK = { PASS: 0, INFO: 1, SKIP: 1, WARN: 2, ERROR: 3 };
  const worst = (levels) => levels.reduce((a, b) => (LEVEL_RANK[b] > LEVEL_RANK[a] ? b : a), 'PASS');

  function normTable(name) {
    return String(name).toUpperCase().replace(/^(SRC|TB|TBL|T|MST)_/, '').replace(/_(TB|T)$/, '');
  }

  /** 아직 매핑하지 않은 원본 테이블마다 대상 후보를 찾는다. [{source, target, reason}] */
  function autoMatchTables(srcTables, tgtTables, mappings) {
    const usedSrc = new Set(mappings.map((m) => m.source));
    const usedTgt = new Set(mappings.map((m) => m.target));
    const out = [];
    for (const s of srcTables) {
      if (s.kind !== 'TABLE' || usedSrc.has(s.name)) continue;
      const key = normTable(s.name);
      const free = tgtTables.filter((t) => !usedTgt.has(t.name));
      let hit = free.find((t) => normTable(t.name) === key);
      let reason = '이름 규칙(접두어 제외 같음)';
      if (!hit && TABLE_DICT[key]) {
        hit = free.find((t) => normTable(t.name) === TABLE_DICT[key]);
        reason = '용어 사전(' + key + ' → ' + TABLE_DICT[key] + ')';
      }
      if (hit) {
        out.push({ source: s.name, target: hit.name, reason });
        usedTgt.add(hit.name);
      }
    }
    return out;
  }

  function suggestTable(srcName, tgtTables, mappings) {
    const m = autoMatchTables([{ name: srcName, kind: 'TABLE' }], tgtTables, mappings.filter((x) => x.source !== srcName));
    return m[0] || null;
  }

  /** 대상 컬럼마다 원본 컬럼·추천 변환식·NULL 처리를 정한다. */
  function autoMapColumns(srcCols, tgtCols) {
    const used = new Set();
    return tgtCols.map((t) => {
      let s = srcCols.find((c) => c.name === t.name);
      let reason = s ? '같은 이름' : null;
      if (!s) {
        s = srcCols.find((c) => COLUMN_DICT[c.name] === t.name);
        if (s) reason = '용어 사전';
      }
      if (!s) {
        for (const [a, b] of SUFFIX_RULES) {
          s = srcCols.find((c) => (c.name.endsWith(a) && c.name.slice(0, -a.length) + b === t.name) || (c.name.endsWith(b) && c.name.slice(0, -b.length) + a === t.name));
          if (s) { reason = '접미어 규칙(' + a + ' ↔ ' + b + ')'; break; }
        }
      }
      if (s && used.has(s.name)) s = null;
      if (s) used.add(s.name);
      const cm = { target: t.name, source: s ? s.name : null, expr: s ? suggestExpr(s, t) : '', nullRule: defaultNullRule(t), defaultValue: t.defaultValue ? t.defaultValue.replace(/^'|'$/g, '') : '', key: t.pk, reason };
      return cm;
    });
  }

  function defaultNullRule(t) {
    if (t.nullable) return 'ALLOW';
    return t.defaultValue ? 'DEFAULT' : 'REJECT';
  }

  /** 형식 차이를 보고 변환식을 추천한다(사용자가 고칠 수 있음). */
  function suggestExpr(s, t) {
    const st = X.parseType(s.type);
    const tt = X.parseType(t.type);
    if (st.base === 'DATE' && tt.base === 'TIMESTAMP') return 'CAST(' + s.name + ' AS TIMESTAMP)';
    if (/_YN$/.test(t.name) && !/_YN$|_FLAG$/.test(s.name) && X.isChar(st)) return "CASE\n    WHEN " + s.name + " = 'A' THEN 'Y'\n    ELSE 'N'\nEND";
    if (/PHONE|MOBILE|TEL/.test(t.name) && X.isChar(st)) return 'REGEXP_REPLACE(' + s.name + ", '[^0-9]', '')";
    if (/(_NM|_NAME)$/.test(t.name) && X.isChar(st)) return 'TRIM(' + s.name + ')';
    return '';
  }

  /** 형식 호환성. {level, msg} */
  function compat(srcType, tgtType, stats) {
    const s = X.parseType(srcType);
    const t = X.parseType(tgtType);
    if (!s) return { level: 'INFO', msg: '결과 형식을 추정하지 못했습니다 — 실행 전 DESCRIBE로 다시 확인' };
    if (!t) return { level: 'ERROR', msg: '대상 형식을 알 수 없습니다' };
    if (X.isChar(s) && X.isChar(t)) {
      if (s.len != null && t.len != null && s.len > t.len) {
        const real = stats && stats.maxLen;
        if (real != null && real <= t.len) return { level: 'WARN', kind: 'truncate', msg: srcType + ' → ' + tgtType + ' 잘림 위험 · 실측 최대 ' + real + '자라 지금 데이터는 들어감' };
        return { level: 'WARN', kind: 'truncate', msg: srcType + ' → ' + tgtType + ' 잘림 위험' + (real != null ? ' · 실측 최대 ' + real + '자(ORA-12899)' : '') };
      }
      if (s.base === 'VARCHAR2' && t.base === 'CHAR') return { level: 'PASS', msg: 'VARCHAR2 → CHAR: 뒤를 공백으로 채움' };
      return { level: 'PASS', msg: '호환' };
    }
    if (s.base === 'NUMBER' && t.base === 'NUMBER') {
      if (t.prec == null) return { level: 'PASS', msg: '호환(대상 정밀도 제한 없음)' };
      if (s.prec == null) return { level: 'WARN', kind: 'precision', msg: '원본 NUMBER 정밀도 미지정 → ' + tgtType + ' 넘칠 수 있음(ORA-01438)' };
      const si = s.prec - (s.scale || 0);
      const ti = t.prec - (t.scale || 0);
      if (si > ti) {
        const real = stats && stats.max;
        const fits = real != null && String(Math.trunc(real)).length <= ti;
        return { level: 'WARN', kind: 'precision', msg: '정수부 ' + si + '자리 → ' + ti + '자리' + (fits ? ' · 실측 최대 ' + MS.fmt.n(real) + '이라 지금 데이터는 들어감' : ' · ORA-01438 위험') };
      }
      if ((s.scale || 0) > (t.scale || 0)) return { level: 'WARN', kind: 'precision', msg: '소수부 ' + s.scale + '자리 → ' + (t.scale || 0) + '자리로 반올림' };
      return { level: 'PASS', msg: s.prec === t.prec ? '호환' : '호환(정밀도 넓어짐)' };
    }
    if (X.isDate(s) && X.isDate(t)) {
      if (s.base === 'TIMESTAMP' && t.base === 'DATE') return { level: 'WARN', msg: 'TIMESTAMP → DATE: 소수 초가 사라짐' };
      return { level: 'PASS', msg: s.base === t.base ? '호환' : 'DATE → TIMESTAMP 암시 변환' };
    }
    if (s.base === 'NUMBER' && X.isChar(t)) {
      const need = (s.prec || 38) + ((s.scale || 0) > 0 ? 2 : 1);
      return t.len != null && t.len < need ? { level: 'WARN', msg: '숫자 → 문자: ' + need + '자 필요, 대상 ' + t.len + '자' } : { level: 'PASS', msg: '숫자 → 문자 암시 변환' };
    }
    if (X.isChar(s) && t.base === 'NUMBER') return { level: 'WARN', msg: '문자 → 숫자: 숫자가 아닌 값은 ORA-01722로 거부됨' };
    if (X.isChar(s) && X.isDate(t)) return { level: 'WARN', msg: '문자 → 날짜: TO_DATE(값, 형식)을 쓰세요(NLS에 따라 달라짐)' };
    if (X.isDate(s) && X.isChar(t)) return { level: 'WARN', msg: '날짜 → 문자: TO_CHAR(값, 형식)을 쓰세요(NLS에 따라 달라짐)' };
    return { level: 'ERROR', msg: srcType + ' → ' + tgtType + ' 형식이 호환되지 않음(ORA-00932)' };
  }

  function colTypeResolver(cols) {
    return (name) => {
      const bare = name.includes('.') ? name.slice(name.lastIndexOf('.') + 1) : name;
      const c = cols.find((x) => x.name === bare);
      return c ? c.type : null;
    };
  }

  /** 매핑 한 줄의 실제 값 식(식이 비면 원본 컬럼) */
  function valueSource(cm) {
    return cm.expr && cm.expr.trim() ? cm.expr.trim() : cm.source || '';
  }

  /** 원본 쪽에서 오는 값의 형식과 통계(검사·안내용) */
  function sourceInfo(cm, srcCols) {
    const src = valueSource(cm);
    if (!src) return null;
    const c = X.compile(src);
    if (c.error) return { error: c.error };
    const missing = c.refs.filter((r) => !colTypeResolver(srcCols)(r));
    if (missing.length) return { error: new X.OraError('ORA-00904', '"' + missing[0] + '": 부적합한 식별자') };
    const type = X.inferType(c.ast, colTypeResolver(srcCols));
    const ref = c.refs.length === 1 ? srcCols.find((x) => x.name === c.refs[0]) : null;
    let stats = ref ? Object.assign({}, ref.stats) : null;
    if (stats && /REGEXP_REPLACE/i.test(src) && stats.digitsMaxLen) stats.maxLen = stats.digitsMaxLen;
    // TRIM 등으로 공백만 있는 값이 NULL이 되는 경우
    let nulls = stats ? (stats.nulls || 0) : null;
    if (stats && /TRIM|REGEXP_REPLACE/i.test(src)) nulls += stats.blanks || 0;
    if (/\bNVL\s*\(|COALESCE|CASE/i.test(src)) nulls = 0;
    return { type, stats, nulls, refs: c.refs, nullable: ref ? ref.nullable : true };
  }

  /** 컬럼 매핑 한 줄 검사. {level, msgs:[{level,msg}], type} */
  function checkColumn(cm, tgtCol, srcCols, ctx) {
    ctx = ctx || {};
    const msgs = [];
    const add = (level, msg) => msgs.push({ level, msg });
    const src = valueSource(cm);
    let type = null;
    if (!src) {
      if (!tgtCol.nullable) {
        if (['DEFAULT', 'CUSTOM'].includes(cm.nullRule) && cm.defaultValue) add('PASS', '원본 없이 ' + (cm.nullRule === 'DEFAULT' ? "기본값 '" + cm.defaultValue + "'" : '식 ' + cm.defaultValue) + '로 채움');
        else if (cm.nullRule === 'SYSDATE') add('PASS', '원본 없이 SYSDATE로 채움');
        else if (tgtCol.defaultValue && ctx.mode !== 'MERGE') add('INFO', 'DB 기본값 DEFAULT ' + tgtCol.defaultValue + ' 사용');
        else add('ERROR', 'NOT NULL 컬럼에 값이 없음 — 모든 행이 ORA-01400으로 거부됨. 원본 컬럼이나 기본값을 정하세요');
      } else add('INFO', '매핑 안 함 — NULL로 둠');
      return { level: worst(msgs.map((m) => m.level)), msgs, type };
    }
    const info = sourceInfo(cm, srcCols);
    if (info.error) {
      add('ERROR', info.error.message);
      return { level: 'ERROR', msgs, type };
    }
    type = info.type;
    const c = compat(info.type, tgtCol.type, info.stats);
    add(c.level, c.msg);
    if (!tgtCol.nullable) {
      const n = info.nulls;
      if (cm.nullRule === 'EMPTY') add('WARN', "Oracle에서 빈 문자열('')은 NULL — NOT NULL 컬럼이라 행이 거부됨");
      else if (cm.nullRule === 'ALLOW' && (n == null || n > 0)) add('WARN', 'NULL이 오면 ORA-01400으로 실패' + (n ? ' (예상 ' + MS.fmt.n(n) + '행)' : '') + ' — NULL 처리를 정하세요');
      else if (cm.nullRule === 'REJECT' && n > 0) add('WARN', 'NULL ' + MS.fmt.n(n) + '행은 거부되어 오류 테이블로 감');
    }
    if (cm.nullRule === 'EMPTY' && tgtCol.nullable) add('INFO', "Oracle에서 빈 문자열('')은 NULL과 같음");
    return { level: worst(msgs.map((m) => m.level)), msgs, type };
  }

  /** 테이블 매핑 전체 상태: {mapped, total, level, errors, warns} */
  function mappingStatus(tm, srcTable, tgtTable) {
    if (!srcTable || !tgtTable) return { mapped: 0, total: 0, level: 'ERROR', errors: 1, warns: 0, results: [] };
    const results = tgtTable.columns.map((t) => {
      const cm = tm.columns.find((x) => x.target === t.name) || { target: t.name, source: null, expr: '', nullRule: defaultNullRule(t) };
      return { target: t, cm, check: checkColumn(cm, t, srcTable.columns, { mode: tm.mode }) };
    });
    const mapped = results.filter((r) => valueSource(r.cm) || (r.cm.nullRule !== 'ALLOW' && r.cm.defaultValue)).length;
    const levels = results.map((r) => r.check.level);
    if (modeOf(tm.mode).needsKey && !(tm.mergeKey || []).length) levels.push('ERROR');
    return {
      mapped, total: tgtTable.columns.length, level: worst(levels),
      errors: levels.filter((l) => l === 'ERROR').length, warns: levels.filter((l) => l === 'WARN').length, results
    };
  }

  MS.mapping = {
    TABLE_DICT, COLUMN_DICT, MODES, modeOf, NULL_RULES, LEVEL_RANK, worst,
    normTable, autoMatchTables, suggestTable, autoMapColumns, defaultNullRule, suggestExpr,
    compat, colTypeResolver, valueSource, sourceInfo, checkColumn, mappingStatus
  };
})();
