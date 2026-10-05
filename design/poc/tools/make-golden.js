// 골든 생성기: POC 백엔드(js/backend)를 Node에서 돌려 tests/MigrationStudio.Tests/Golden/poc-golden.json 을 만든다.
// 실행: node design/poc/tools/make-golden.js  (설계자 전용 — 결과 파일은 구현 쪽에서 고치지 않는다)
// POC 백엔드(JS)를 기준 구현으로 삼아 C# 이식의 기대값(골든)을 만든다.
const fs = require('fs'); const vm = require('vm');
const path = require('path');
const root = path.join(__dirname, '..', 'js') + '/';
const outDir = path.join(__dirname, '..', '..', '..', 'tests', 'MigrationStudio.Tests', 'Golden');
global.window = global; global.performance = require('perf_hooks').performance;
const files = ['backend/mock-metadata.js', 'backend/expression.js', 'backend/adapters.js', 'backend/mapping.js', 'backend/sqlgen.js', 'backend/sql-mapping.js', 'backend/job.js', 'backend/settings.js', 'backend/validation.js', 'backend/engine.js'];
global.MS = { fmt: { n: v => v == null ? '—' : Math.round(v).toLocaleString('en-US'), pct: (a, b) => b ? a / b * 100 : 0, dur: s => String(s), time: () => 't', stamp: () => 's', short: v => String(v) }, sleep: () => Promise.resolve() };
for (const f of files) vm.runInThisContext(fs.readFileSync(root + f, 'utf8'), { filename: f });
const X = MS.expr, M = MS.mapping, G = MS.sqlgen, SQ = MS.sqlMapping, J = MS.job;

// ---- 메타데이터를 C# SchemaMetadata 모양으로 ----
const csCol = c => ({ name: c.name, type: c.type, nullable: c.nullable, primaryKey: c.pk, defaultValue: c.defaultValue, comment: c.comment,
  stats: { nulls: c.stats.nulls ?? null, blanks: c.stats.blanks ?? null, maxLength: c.stats.maxLen ?? null, digitsMaxLength: c.stats.digitsMaxLen ?? null, distinct: c.stats.distinct ?? null, max: c.stats.max ?? null } });
const csTable = t => ({ name: t.name, kind: t.kind, rows: t.rows, avgRowLength: t.avgRowLen || 0, comment: t.comment || '', columns: t.columns.map(csCol),
  foreignKeys: (t.fks || []).map(f => ({ name: f.name, columns: f.columns, refTable: f.ref })) });
const csSchema = s => ({ schema: s.schema, tables: s.tables.map(csTable), tablespace: s.tablespace ? { name: s.tablespace.name, freeGb: s.tablespace.freeGb } : null });
const src = MS.mockDb.SOURCE, tgt = MS.mockDb.TARGET;
const T = n => tgt.tables.find(t => t.name === n), Sx = n => src.tables.find(t => t.name === n);

const out = { note: 'design/poc/js/backend 의 출력. C# Core 이식은 이 값과 같아야 한다(다르면 보고서에 이유를 적는다).', source: csSchema(src), target: csSchema(tgt) };

// ---- 1. 형식 ----
out.types = ['VARCHAR2(30)', 'NUMBER(15,2)', 'NUMBER(12)', 'NUMBER', 'CHAR(1)', 'DATE', 'TIMESTAMP', 'TIMESTAMP(3)', 'VARCHAR2(100 CHAR)', 'CLOB']
  .map(t => { const p = X.parseType(t); return { input: t, base: p.base, length: p.len ?? null, precision: p.prec ?? null, scale: p.scale ?? null, isChar: !!X.isChar(p), isDate: !!X.isDate(p) }; });

// ---- 2. 형식 호환성 ----
const compatCases = [
  ['VARCHAR2(30)', 'VARCHAR2(20)', { maxLen: 13 }], ['VARCHAR2(30)', 'VARCHAR2(20)', { maxLen: 25 }], ['VARCHAR2(30)', 'VARCHAR2(20)', null],
  ['VARCHAR2(100)', 'VARCHAR2(150)', null], ['VARCHAR2(10)', 'CHAR(10)', null], ['NUMBER(12)', 'NUMBER(18)', null], ['NUMBER(12)', 'NUMBER(12)', null],
  ['NUMBER(15,2)', 'NUMBER(13,2)', { max: 48200000 }], ['NUMBER(15,2)', 'NUMBER(13,2)', null], ['NUMBER(15,4)', 'NUMBER(17,2)', null],
  ['NUMBER', 'NUMBER(10)', null], ['NUMBER(10)', 'NUMBER', null], ['DATE', 'TIMESTAMP', null], ['TIMESTAMP', 'DATE', null], ['DATE', 'DATE', null],
  ['NUMBER(12)', 'VARCHAR2(5)', null], ['NUMBER(12)', 'VARCHAR2(20)', null], ['VARCHAR2(10)', 'NUMBER(10)', null], ['VARCHAR2(10)', 'DATE', null],
  ['DATE', 'VARCHAR2(30)', null], ['DATE', 'NUMBER(10)', null], [null, 'VARCHAR2(10)', null], ['VARCHAR2(10)', null, null]];
out.compat = compatCases.map(([s, t, st]) => ({ source: s, target: t, stats: st ? { maxLength: st.maxLen ?? null, max: st.max ?? null } : null, result: M.compat(s, t, st) }));

// ---- 3. 식 분석(참조 열·바인드·형식 추정) ----
const custTypes = n => { const b = n.includes('.') ? n.slice(n.lastIndexOf('.') + 1) : n; const c = Sx('SRC_CUSTOMER').columns.concat(Sx('SRC_ORDER').columns).find(x => x.name === b); return c ? c.type : null; };
const exprs = ['CUSTOMER_ID', 'TRIM(CUSTOMER_NM)', "REGEXP_REPLACE(PHONE_NO, '[^0-9]', '')", "CASE\n    WHEN STATUS_CD = 'A' THEN 'Y'\n    ELSE 'N'\nEND",
  'CAST(REG_DT AS TIMESTAMP)', 'CAST(NVL(MOD_DT, REG_DT) AS TIMESTAMP)', 'NVL(MOD_DT, REG_DT)',
  "DECODE(ORDER_STAT_CD, '10', 'ORDERED', '20', 'PAID', '30', 'SHIPPED', '90', 'CANCELED', 'UNKNOWN')", 'SUBSTR(CUSTOMER_NM, 1, 20)',
  "CUSTOMER_NM || '-' || PHONE_NO", 'ORDER_AMT * 1.1', 'REG_DT + 1', 'SYSDATE', 'SYSTIMESTAMP', "TO_CHAR(REG_DT, 'YYYYMMDD')", "TO_DATE('20240101', 'YYYYMMDD')",
  "LPAD(CUSTOMER_ID, 10, '0')", 'UPPER(C.CUSTOMER_NM)', "C.CUSTOMER_ID > :LAST_ID AND C.STATUS_CD IN ('A', 'I')", "CASE STATUS_CD WHEN 'A' THEN 1 WHEN 'I' THEN 2 ELSE 0 END",
  'CUSTOMER_NM IS NOT NULL', "CUSTOMER_NM LIKE '김%'", 'CUSTOMER_ID BETWEEN 1 AND 10', 'NOT (CUSTOMER_ID = 1)', '-CUSTOMER_ID', 'COUNT(*)', 'MAX(REG_DT)',
  'TRIM(', 'FOO(1)', "'abc", 'CUSTOMER_ID +', '(CUSTOMER_ID', 'CAST(REG_DT TIMESTAMP)', 'CASE WHEN 1 = 1 THEN 1', 'SELECT', '', 'A B'];
out.expressions = exprs.map(e => { const c = X.compile(e); return { expr: e, error: c.error ? { code: c.error.code, message: c.error.message, position: c.error.at ?? null } : null, refs: c.refs, binds: c.binds, type: c.ast ? X.inferType(c.ast, custTypes) : null }; });

// ---- 4·5. 자동 매칭·자동 매핑 ----
const job = J.sampleJob();
out.autoMatchTables = M.autoMatchTables(src.tables, tgt.tables, job.mappings.filter(m => m.sourceType !== 'SQL'));
out.autoMapColumns = [['SRC_CUSTOMER', 'TB_MEMBER'], ['SRC_ORDER', 'TB_SALES_ORDER'], ['SRC_CUSTOMER_GRADE', 'TB_MEMBER_GRADE'], ['SRC_PRODUCT', 'TB_PRODUCT'], ['SRC_ORDER_ITEM', 'TB_SALES_ORDER_ITEM']]
  .map(([s, t]) => ({ source: s, target: t, columns: M.autoMapColumns(Sx(s).columns, T(t).columns).map(c => ({ target: c.target, source: c.source, expr: c.expr, nullRule: c.nullRule, defaultValue: c.defaultValue, reason: c.reason })) }));

// ---- 6. 매핑 상태(컬럼 검사) — 예제 작업의 테이블 원본 매핑 ----
out.sampleJob = J.toPlain(job, MS.settingsModel.defaults());
out.mappingStatus = job.mappings.filter(m => m.sourceType !== 'SQL').map(m => {
  const st = M.mappingStatus(m, Sx(m.source), T(m.target));
  return { mapping: m.id, mapped: st.mapped, total: st.total, level: st.level, errors: st.errors, warns: st.warns,
    columns: st.results.map(r => ({ target: r.target.name, level: r.check.level, type: r.check.type, msgs: r.check.msgs })) };
});

// ---- 7. SELECT 해석 ----
const sqls = {
  sample: J.SAMPLE_SQL,
  sampleOrdered: J.SAMPLE_SQL + '\nORDER BY C.CUSTOMER_ID',
  noFromWhere: 'SELECT C.CUSTOMER_ID AS MEMBER_ID, TRIM(C.NOPE) AS MEMBER_NAME FROM SRC_CUSTOMER C WHERE',
  missingParen: 'SELECT C.CUSTOMER_ID AS MEMBER_ID,\n  TRIM(C.CUSTOMER_NM AS MEMBER_NAME\nFROM SRC_CUSTOMER C',
  notSelect: "UPDATE SRC_CUSTOMER SET CUSTOMER_NM = 'x'",
  withComments: '-- 머리 주석\nSELECT /* 별칭 */ G.GRADE_CD AS GRADE_CODE, UPPER(G.GRADE_NM) GRADE_NAME, G.SORT_SEQ\nFROM SRC_CUSTOMER_GRADE G;',
  distinctGroup: 'SELECT DISTINCT O.CUSTOMER_ID AS MEMBER_ID, COUNT(*) AS ORDER_CNT FROM SRC_ORDER O GROUP BY O.CUSTOMER_ID',
  star: 'SELECT G.* FROM SRC_CUSTOMER_GRADE G',
  unknownTable: 'SELECT X.A AS A FROM NO_SUCH_TABLE X',
  ambiguous: 'SELECT CUSTOMER_ID AS MEMBER_ID FROM SRC_CUSTOMER C JOIN SRC_ORDER O ON O.CUSTOMER_ID = C.CUSTOMER_ID',
  noAlias: 'SELECT TRIM(CUSTOMER_NM), CUSTOMER_ID FROM SRC_CUSTOMER',
  dupAlias: 'SELECT CUSTOMER_ID AS A, CUSTOMER_NM AS A FROM SRC_CUSTOMER',
  stringLiteral: "SELECT 'a:b' AS LIT, :P1 AS P FROM SRC_CUSTOMER WHERE CUSTOMER_NM = ':NOT_BIND'",
  unterminated: "SELECT 'abc AS X FROM SRC_CUSTOMER"
};
out.parseSelect = Object.entries(sqls).map(([key, sql]) => {
  const p = SQ.parseSelect(sql);
  return { key, sql, errors: p.errors, warnings: p.warnings,
    items: p.items.map(i => ({ name: i.name, alias: i.alias, expr: i.expr, star: !!i.star, error: i.error ? i.error.code : null })),
    tables: p.tables.map(t => t.subquery ? { subquery: true } : { schema: t.schema, name: t.name, alias: t.alias, join: t.join, on: t.on || null }),
    binds: p.binds, where: p.where ? p.where.text : null, orderBy: p.orderBy === undefined ? null : p.orderBy, groupBy: p.groupBy };
});

// ---- 8. DESCRIBE(메타데이터 기반) → 가상 원본 ----
out.describe = ['sample', 'withComments', 'star', 'unknownTable', 'ambiguous', 'distinctGroup'].map(key => {
  const d = SQ.describe(SQ.parseSelect(sqls[key]), src);
  return { key, errors: d.errors, columns: d.columns.map(c => ({ name: c.name, type: c.type, expr: c.expr, nullable: c.nullable === undefined ? null : c.nullable, error: c.error || null,
    stats: c.stats ? { nulls: c.stats.nulls ?? null, blanks: c.stats.blanks ?? null, maxLength: c.stats.maxLen ?? null } : null })) };
});
const sm = job.mappings.find(m => m.sourceType === 'SQL');
const v = SQ.virtualSource(sm, src);
out.virtualSource = { mapping: sm.id, name: v.name, rows: v.rows, baseRows: v.baseRows, comment: v.comment, columns: v.columns.map(c => ({ name: c.name, type: c.type, nullable: c.nullable, comment: c.comment })) };

// ---- 9. SQL 원본 검증 ----
const sqlCases = {
  sample: sm,
  ordered: Object.assign({}, sm, { sql: sqls.sampleOrdered }),
  noCheckpointBind: Object.assign({}, sm, { binds: [], sql: sqls.sample.replace('WHERE C.CUSTOMER_ID > :LAST_ID', '') }),
  missingBindValue: Object.assign({}, sm, { binds: [{ name: 'LAST_ID', type: 'NUMBER', value: '', fromCheckpoint: false }] }),
  broken: Object.assign({}, sm, { sql: sqls.missingParen }),
  noTarget: Object.assign({}, sm, { target: '' })
};
out.sqlValidate = Object.entries(sqlCases).map(([key, m]) => { const r = SQ.validate(m, src, tgt); return { key, level: r.level, mapped: r.mapped || 0, total: r.total || 0, errorLine: r.errorLine || null, items: r.items }; });

// ---- 10. SQL 생성 ----
const tmC = job.mappings.find(m => m.id === 'tm-customer'), tmO = job.mappings.find(m => m.id === 'tm-order');
out.sqlgen = {
  sourceSelectTableWorkers4: G.buildSourceSelect(tmC, 'LEGACY_APP', Sx('SRC_CUSTOMER'), T('TB_MEMBER'), { fetchSize: 5000, workers: 4 }),
  sourceSelectTableWorkers1Where: G.buildSourceSelect(tmO, 'LEGACY_APP', Sx('SRC_ORDER'), T('TB_SALES_ORDER'), { fetchSize: 5000, workers: 1 }),
  sourceSelectSql: G.buildSourceSelect(sm, 'LEGACY_APP', v, T('TB_MEMBER'), { fetchSize: 5000, workers: 4 }),
  writeColumnsCustomer: G.writeColumns(tmC, T('TB_MEMBER')).map(c => ({ name: c.name, expr: c.expr })),
  write: {}
};
const cols = G.writeColumns(tmC, T('TB_MEMBER')).map(c => c.name);
for (const mode of ['INSERT_ONLY', 'MERGE', 'TRUNCATE_INSERT', 'DELETE_INSERT']) {
  out.sqlgen.write[mode] = G.buildWriteSql('NEXT_APP', 'TB_MEMBER', cols, mode, ['MEMBER_ID'], 'ERR$_TB_MEMBER');
  out.sqlgen.write[mode + '_noErrorTable_noKey'] = G.buildWriteSql('NEXT_APP', 'TB_MEMBER', cols, mode, [], null);
}
out.sqlgen.literal = [['Y', 'CHAR(1)'], ['10', 'NUMBER(5)'], ['10', 'VARCHAR2(5)'], ["O'Brien", 'VARCHAR2(20)'], ["'Y'", 'CHAR(1)'], ['SYSDATE', 'DATE'], ['', 'VARCHAR2(5)'], [null, 'NUMBER']]
  .map(([val, t]) => ({ value: val, type: t, result: G.literal(val, t) }));
out.sqlgen.valueExpr = [];
{
  const created = T('TB_MEMBER').columns.find(c => c.name === 'CREATED_AT');
  const useYn = T('TB_MEMBER').columns.find(c => c.name === 'USE_YN');
  for (const rule of ['ALLOW', 'REJECT', 'DEFAULT', 'SYSDATE', 'EMPTY', 'CUSTOM']) {
    for (const [srcName, expr, dv, col] of [['REG_DT', 'CAST(REG_DT AS TIMESTAMP)', 'SYSTIMESTAMP', created], [null, '', 'Y', useYn], ['STATUS_CD', '', 'Y', useYn]]) {
      out.sqlgen.valueExpr.push({ rule, source: srcName, expr, defaultValue: dv, targetType: col.type, result: G.valueExpr({ source: srcName, expr, nullRule: rule, defaultValue: dv }, col) });
    }
  }
}
out.sqlgen.errorTableFor = [[{ errorPolicy: 'CONTINUE', errorTable: 'ERR$_' }, 'TB_MEMBER'], [{ errorPolicy: 'CONTINUE', errorTable: 'MIG_ERRORS' }, 'TB_MEMBER'], [{ errorPolicy: 'STOP', errorTable: 'ERR$_' }, 'TB_MEMBER']]
  .map(([st, t]) => ({ strategy: st, target: t, result: G.errorTableFor(st, t) }));

// ---- 11. 작업 파일 ----
const v1 = {
  format: 'folderss-migration-job', version: 1, jobName: 'OLD_JOB',
  source: { kind: 'oracle', name: 'LEGACY_PROD', color: 'red', host: '10.10.10.21', port: '1521', service: 'LEGACY', schema: 'LEGACY_APP', user: 'LEGACY_APP', password: 'should-be-dropped' },
  target: { name: 'NEXT_PROD', schema: 'NEXT_APP' },
  strategy: { mode: 'FULL', commitSize: 10000 },
  tableMappings: [{ id: 'tm-a', use: true, source: 'SRC_CUSTOMER', target: 'TB_MEMBER', mode: 'MERGE', mergeKey: ['MEMBER_ID'], checkpointColumn: 'CUSTOMER_ID', columns: [{ target: 'MEMBER_ID', source: 'CUSTOMER_ID', expr: '', nullRule: 'REJECT', defaultValue: '' }] }],
  sqlMappings: [{ id: 'sm-x', use: false, name: 'SQLMAP_X', sql: 'SELECT 1 AS A FROM DUAL', targetTable: 'TB_MEMBER', writeStrategy: 'MERGE', mergeKey: ['MEMBER_ID'], fetchSize: 5000, commitSize: 10000, checkpointColumn: 'A', binds: [], aliasMap: { A: 'MEMBER_ID' } }],
  checkpoints: { 'tm-a': { column: 'CUSTOMER_ID', value: 850000, rows: 850000, total: 1240325, at: '2026-10-02 23:41:07', runId: 'R-1', status: 'stopped' } }
};
out.jobV1 = v1;
const up = J.parseJob(JSON.stringify(v1));
out.jobV1Upgraded = { version: up.version, source: up.source, target: up.target,
  mappings: up.mappings.map(m => ({ id: m.id, use: m.use, sourceType: m.sourceType, source: m.source, sql: m.sql || null, target: m.target, mode: m.mode, mergeKey: m.mergeKey, checkpointColumn: m.checkpointColumn, columns: m.columns })),
  checkpoints: up.checkpoints };
out.yamlSample = J.yamlOf(job, MS.settingsModel.defaults());
out.settingsDefaults = MS.settingsModel.defaults();
fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(outDir + '/poc-golden.json', JSON.stringify(out, null, 2), 'utf8');
console.log('ok', Object.keys(out).join(', '), 'bytes', JSON.stringify(out).length);
