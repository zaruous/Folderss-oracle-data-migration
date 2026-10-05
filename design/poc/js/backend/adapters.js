/*
 * DatabaseAdapter — 화면·엔진과 실제 DB 드라이버 사이의 경계.
 * 엔진은 이 인터페이스만 부르고, DB 종류마다 구현을 바꾼다.
 *
 *   DatabaseAdapter (공통 인터페이스)
 *     ├─ OracleAdapter      ← WPF 구현: Oracle.ManagedDataAccess.Core (POC는 MockOracleAdapter)
 *     ├─ PostgreSqlAdapter  ← 예정(Npgsql)
 *     ├─ SqlServerAdapter   ← 예정(Microsoft.Data.SqlClient)
 *     └─ MySqlAdapter       ← 예정(MySqlConnector, MariaDB 겸용)
 *
 * 모든 메서드는 Promise를 돌려준다(C#에서는 Task + CancellationToken).
 */
(function () {
  'use strict';
  const MS = window.MS;
  const { sleep } = MS;

  class DatabaseAdapter {
    constructor(kind, title) {
      this.kind = kind;
      this.title = title;
    }
    /** 접속 시험 → {ok, version, banner, latencyMs, error} */
    async testConnection(profile) { throw notImplemented(this, 'testConnection'); }
    /** 스키마 메타데이터 → {schema, tables:[{name, kind, rows, columns:[{name,type,nullable,pk,defaultValue,stats}]}], loadedAt, elapsedMs} */
    async loadMetadata(profile) { throw notImplemented(this, 'loadMetadata'); }
    /** SELECT 결과 열(이름·형식)만 조회 — 행은 읽지 않는다(GetSchemaTable) */
    async describeQuery(profile, sql) { throw notImplemented(this, 'describeQuery'); }
    /** 원본을 Fetch 크기만큼씩 스트리밍(async iterator) — 전체를 메모리에 올리지 않는다 */
    async *openReader(profile, sql, binds, opts) { throw notImplemented(this, 'openReader'); }
    /** 배열 바인드로 배치 실행 → {inserted, updated, rejected} */
    async writeBatch(profile, sql, rows, opts) { throw notImplemented(this, 'writeBatch'); }
    async commit(profile) { throw notImplemented(this, 'commit'); }
    async rollback(profile) { throw notImplemented(this, 'rollback'); }
    /** DB별 SQL 방언 */
    get dialect() {
      return { quote: (n) => '"' + n + '"', merge: false, errorLogging: false, dual: '' };
    }
  }

  function notImplemented(adapter, method) {
    return new Error(adapter.title + ' 어댑터는 아직 ' + method + '를 지원하지 않습니다');
  }

  /** Mock Oracle: 약간 기다린 뒤 미리 정해 둔 결과를 돌려준다 */
  class MockOracleAdapter extends DatabaseAdapter {
    constructor() {
      super('oracle', 'Oracle');
    }

    get dialect() {
      return { quote: (n) => '"' + n + '"', merge: true, errorLogging: true, dual: 'FROM DUAL' };
    }

    async testConnection(p) {
      const latency = 18 + (hash(p.host + p.service) % 19);
      await sleep(420 + latency * 8);
      const fail = (code, msg) => ({ ok: false, error: code + ': ' + msg, latencyMs: null });
      if (!String(p.host || '').trim()) return fail('ORA-12545', '대상 호스트 또는 객체가 존재하지 않아 연결할 수 없습니다');
      if (!/^\d+$/.test(String(p.port || ''))) return fail('ORA-12541', 'TNS: 리스너가 없습니다(포트 ' + (p.port || '없음') + ')');
      if (!String(p.service || '').trim()) return fail('ORA-12514', 'TNS: 리스너가 현재 접속 기술자에서 요청한 서비스를 알지 못합니다');
      if (/\.99$/.test(p.host)) {
        await sleep(900);
        return fail('ORA-12170', 'TNS: 접속 시간 초과');
      }
      if (!String(p.password || '').trim()) return fail('ORA-01017', '사용자명/비밀번호가 부적합, 로그온할 수 없습니다');
      return {
        ok: true,
        version: 'Oracle 19c',
        banner: 'Oracle Database 19c Enterprise Edition Release 19.0.0.0.0 - Version 19.21.0.0.0',
        latencyMs: latency,
        nls: { characterSet: 'AL32UTF8', dateFormat: 'YYYY-MM-DD HH24:MI:SS' }
      };
    }

    async loadMetadata(p) {
      const started = performance.now();
      await sleep(650);
      const schema = String(p.schema || p.user || '').toUpperCase();
      const src = [MS.mockDb.SOURCE, MS.mockDb.TARGET].find((s) => s.schema === schema);
      const meta = src ? JSON.parse(JSON.stringify(src)) : { schema, tables: [] };
      meta.loadedAt = new Date().toISOString();
      meta.elapsedMs = Math.round(performance.now() - started);
      return meta;
    }

    async describeQuery(p, sql) {
      await sleep(250);
      const parsed = MS.sqlMapping.parseSelect(sql);
      return MS.sqlMapping.describe(parsed, MS.mockDb.SOURCE).columns.map((c) => ({ name: c.name, type: c.type }));
    }
  }

  /** 아직 구현하지 않은 DB — 선택지에 "예정"으로만 보인다 */
  class PlannedAdapter extends DatabaseAdapter {}

  const KINDS = [
    { value: 'oracle', label: 'Oracle', create: () => new MockOracleAdapter() },
    { value: 'postgresql', label: 'PostgreSQL (예정)', planned: true, create: () => new PlannedAdapter('postgresql', 'PostgreSQL') },
    { value: 'sqlserver', label: 'SQL Server (예정)', planned: true, create: () => new PlannedAdapter('sqlserver', 'SQL Server') },
    { value: 'mysql', label: 'MySQL (예정)', planned: true, create: () => new PlannedAdapter('mysql', 'MySQL') },
    { value: 'mariadb', label: 'MariaDB (예정)', planned: true, create: () => new PlannedAdapter('mariadb', 'MariaDB') }
  ];

  const cache = {};
  function adapterFor(kind) {
    if (!cache[kind]) {
      const k = KINDS.find((x) => x.value === kind) || KINDS[0];
      cache[kind] = k.create();
    }
    return cache[kind];
  }

  function hash(s) {
    let x = 7;
    for (const ch of String(s)) x = (Math.imul(x, 31) + ch.charCodeAt(0)) >>> 0;
    return x;
  }

  MS.adapters = { DatabaseAdapter, MockOracleAdapter, KINDS, adapterFor };
})();
