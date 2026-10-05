/*
 * SQL 생성기: 테이블 매핑 → 원본 SELECT(변환식은 Oracle이 계산), 대상 쓰기 문(MERGE·INSERT·TRUNCATE·DELETE).
 * 쓰기 문은 행마다 실행하지 않고 배열 바인드(ArrayBindCount = 커밋 크기)로 배치 실행한다.
 * 오류 정책이 "계속 + 오류 테이블"이면 DML 오류 로깅(LOG ERRORS INTO ERR$_…)을 붙여 문제 행만 따로 남긴다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const X = MS.expr;
  const M = MS.mapping;

  const indent = (s, n) => String(s).replace(/\n/g, '\n' + ' '.repeat(n));

  function literal(v, type) {
    if (v == null || v === '') return 'NULL';
    const s = String(v).trim();
    if (/^'.*'$/s.test(s) || /^(SYSDATE|SYSTIMESTAMP|NULL)$/i.test(s)) return s;
    const t = X.parseType(type);
    if (t && t.base === 'NUMBER' && /^-?\d+(\.\d+)?$/.test(s)) return s;
    return "'" + s.replace(/'/g, "''") + "'";
  }

  /** NULL 처리 규칙을 반영한 값 식. 원본도 기본값도 없으면 null */
  function valueExpr(cm, tgtCol) {
    const e = M.valueSource(cm);
    const isTs = tgtCol && X.parseType(tgtCol.type).base === 'TIMESTAMP';
    const now = isTs ? 'SYSTIMESTAMP' : 'SYSDATE';
    if (!e) {
      if (cm.nullRule === 'DEFAULT' && cm.defaultValue) return literal(cm.defaultValue, tgtCol && tgtCol.type);
      if (cm.nullRule === 'CUSTOM' && cm.defaultValue) return cm.defaultValue;
      if (cm.nullRule === 'SYSDATE') return now;
      return null;
    }
    switch (cm.nullRule) {
      case 'DEFAULT': return cm.defaultValue ? 'NVL(' + e + ', ' + literal(cm.defaultValue, tgtCol && tgtCol.type) + ')' : e;
      case 'SYSDATE': return 'NVL(' + e + ', ' + now + ')';
      case 'EMPTY': return 'NVL(' + e + ", '')";
      case 'CUSTOM': return cm.defaultValue ? 'NVL(' + e + ', ' + cm.defaultValue + ')' : e;
      default: return e;
    }
  }

  /** 쓰기에 들어가는 열: 값 식이 있는 대상 컬럼(값이 없으면 빼서 DB 기본값이 들어가게 함) */
  function writeColumns(tm, tgtTable) {
    return tgtTable.columns
      .map((t) => ({ t, cm: tm.columns.find((c) => c.target === t.name) }))
      .filter((x) => x.cm && valueExpr(x.cm, x.t) != null)
      .map((x) => ({ name: x.t.name, expr: valueExpr(x.cm, x.t), col: x.t }));
  }

  /**
   * 원본 SELECT. 원본이 테이블이면 FROM 스키마.테이블, SQL이면 사용자 SQL을 인라인 뷰(S)로 감싼다 —
   * 변환식·NULL 처리·체크포인트·작업자 범위를 테이블 원본과 같은 방식으로 붙이기 위해서다.
   */
  function buildSourceSelect(tm, srcSchema, srcTable, tgtTable, opts) {
    opts = opts || {};
    const isSql = tm.sourceType === 'SQL';
    const q = isSql ? 'S.' : '';
    const cols = writeColumns(tm, tgtTable);
    const lines = cols.map((c) => '    ' + indent(c.expr, 4) + ' AS ' + c.name);
    const cp = tm.checkpointColumn;
    const cpBind = isSql ? MS.sqlMapping.checkpointBind(tm, MS.sqlMapping.parseSelect(tm.sql)) : null;
    const where = [];
    if (cp && !cpBind) where.push(q + cp + ' > :LAST_ID');
    if (tm.where && tm.where.trim()) where.push('(' + tm.where.trim() + ')');
    if (opts.workers > 1 && cp) where.push(q + cp + ' <= :RANGE_TO');
    const from = isSql
      ? '(\n    ' + indent(MS.sqlMapping.stripComments(tm.sql).replace(/[;\s]+$/, ''), 4) + '\n) S'
      : srcSchema + '.' + srcTable.name;
    let sql = '-- 원본 읽기: Fetch ' + MS.fmt.n(tm.fetchSize || opts.fetchSize || 5000) + '행씩 스트리밍\n' +
      (isSql ? '-- 원본 SQL을 인라인 뷰 S로 감싸 변환식·NULL 처리·체크포인트를 붙임\n' : '') +
      (cp ? (cpBind ? '-- :' + cpBind.name + ' = 체크포인트(SQL 안 조건, 없으면 입력한 값)\n' : '-- :LAST_ID = 체크포인트(없으면 처음부터)\n') : '') +
      (opts.workers > 1 && cp ? '-- 작업자 ' + opts.workers + '개: ' + cp + ' 범위를 나눠 :RANGE_TO까지씩 읽음\n' : '') +
      'SELECT\n' + lines.join(',\n') + '\nFROM ' + from;
    if (where.length) sql += '\nWHERE ' + where.join('\n  AND ');
    if (cp) sql += '\nORDER BY ' + q + cp;
    return sql;
  }

  /**
   * 대상 쓰기 문. cols: 대상 컬럼 이름 목록, keys: 병합 키.
   * errorTable이 있으면 LOG ERRORS 절을 붙인다.
   */
  function buildWriteSql(tgtSchema, tgtName, cols, mode, keys, errorTable) {
    const T = tgtSchema + '.' + tgtName;
    const log = errorTable ? '\nLOG ERRORS INTO ' + tgtSchema + '.' + errorTable + " ('RUN_ID') REJECT LIMIT UNLIMITED" : '';
    const insertCols = cols.map((c) => '    ' + c).join(',\n');
    const insertVals = cols.map((c) => '    :' + c).join(',\n');
    const insert = 'INSERT INTO ' + T + ' (\n' + insertCols + '\n)\nVALUES (\n' + insertVals + '\n)' + log;
    keys = (keys || []).filter((k) => cols.includes(k));
    switch (mode) {
      case 'MERGE': {
        if (!keys.length) return '-- 병합 키를 정하세요(MERGE는 키로 대상 행을 찾습니다)';
        const others = cols.filter((c) => !keys.includes(c));
        return 'MERGE INTO ' + T + ' T\nUSING (\n    SELECT\n' +
          cols.map((c) => '        :' + c + ' AS ' + c).join(',\n') +
          '\n    FROM DUAL\n) S\nON (\n' + keys.map((k) => '    T.' + k + ' = S.' + k).join('\n    AND ') + '\n)\n' +
          (others.length ? 'WHEN MATCHED THEN\n    UPDATE SET\n' + others.map((c) => '        T.' + c + ' = S.' + c).join(',\n') + '\n' : '') +
          'WHEN NOT MATCHED THEN\n    INSERT (\n' + cols.map((c) => '        ' + c).join(',\n') + '\n    )\n    VALUES (\n' +
          cols.map((c) => '        S.' + c).join(',\n') + '\n    )' + log;
      }
      case 'TRUNCATE_INSERT':
        return '-- 시작할 때 한 번(되돌릴 수 없음)\nTRUNCATE TABLE ' + T + ';\n\n-- 배치마다\n' + insert;
      case 'DELETE_INSERT':
        if (!keys.length) return '-- 삭제 키를 정하세요';
        return '-- 배치마다: 같은 키의 대상 행을 지우고 넣음\nDELETE FROM ' + T + '\nWHERE ' + keys.map((k) => k + ' = :' + k).join('\n  AND ') + ';\n\n' + insert;
      default:
        return insert;
    }
  }

  function errorTableFor(strategy, tgtName) {
    if (strategy.errorPolicy !== 'CONTINUE') return null;
    const base = (strategy.errorTable || 'ERR$_').trim();
    return base.endsWith('_') ? (base + tgtName).slice(0, 128) : base;
  }

  MS.sqlgen = { literal, valueExpr, writeColumns, buildSourceSelect, buildWriteSql, errorTableFor };
})();
