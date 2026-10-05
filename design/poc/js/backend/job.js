/*
 * 이관 작업(Job) 모델과 저장 형식.
 * 작업 하나 = 접속 2개(마이그레이션 설정의 접속 참조) + 전략 + 매핑 N + 체크포인트.
 * 매핑의 원본은 두 종류다:
 *   - TABLE: 원본 테이블 하나(컬럼 = 테이블 컬럼)
 *   - SQL:   원본 SELECT 문(컬럼 = 결과 별칭). JOIN·집계·코드 변환 등 — 결과를 인라인 뷰로 감싸 테이블처럼 쓴다
 * 어느 쪽이든 그 뒤의 컬럼 매핑(변환식·NULL 처리)·쓰기 방식·체크포인트는 같다.
 * 저장: JSON(기본) 또는 YAML. 비밀번호는 저장하지 않는다(접속은 마이그레이션 설정에 DPAPI로 따로 보관).
 * 매핑 템플릿: 매핑과 용어 사전만 담은 파일(customer_mapping.json 등) — 다른 작업에서 가져다 쓴다.
 */
(function () {
  'use strict';
  const MS = window.MS;

  const JOB_FORMAT = 'folderss-migration-job';
  const MAPPING_FORMAT = 'folderss-migration-mapping';
  const VERSION = 2;

  let seq = 0;
  const newId = (p) => p + Date.now().toString(36).slice(-4) + (++seq).toString(36);

  const SAMPLE_SQL = [
    'SELECT',
    '    C.CUSTOMER_ID AS MEMBER_ID,',
    '',
    '    TRIM(C.CUSTOMER_NM)',
    '        AS MEMBER_NAME,',
    '',
    '    REGEXP_REPLACE(',
    '        C.PHONE_NO,',
    "        '[^0-9]',",
    "        ''",
    '    ) AS MOBILE_NO,',
    '',
    '    CASE',
    "        WHEN C.STATUS_CD = 'A'",
    "        THEN 'Y'",
    "        ELSE 'N'",
    '    END AS USE_YN,',
    '',
    '    G.GRADE_NM AS MEMBER_GRADE,',
    '',
    '    CAST(',
    '        C.REG_DT AS TIMESTAMP',
    '    ) AS CREATED_AT,',
    '',
    '    CAST(',
    '        NVL(C.MOD_DT, C.REG_DT)',
    '        AS TIMESTAMP',
    '    ) AS UPDATED_AT',
    '',
    'FROM SRC_CUSTOMER C',
    '',
    'LEFT JOIN SRC_CUSTOMER_GRADE G',
    '       ON G.GRADE_CD = C.GRADE_CD',
    '',
    'WHERE C.CUSTOMER_ID > :LAST_ID'
  ].join('\n');

  const cm = (target, source, expr, nullRule, defaultValue) => ({ target, source, expr: expr || '', nullRule, defaultValue: defaultValue || '' });

  function tableMapping(o) {
    return Object.assign({ id: newId('tm-'), use: true, sourceType: 'TABLE', source: '', target: '', mode: 'INSERT_ONLY', mergeKey: [], checkpointColumn: null, where: '', columns: [] }, o);
  }

  /** SQL 원본 매핑. fetchSize·commitSize가 null이면 전략 기본값 */
  function sqlMapping(o) {
    return Object.assign({
      id: newId('tm-'), use: true, sourceType: 'SQL', source: 'SQLMAP_1', sql: 'SELECT\n    \nFROM \nWHERE ', binds: [],
      target: '', mode: 'MERGE', mergeKey: [], checkpointColumn: null, where: '', fetchSize: null, commitSize: null, columns: []
    }, o);
  }

  function sampleJob() {
    return {
      format: JOB_FORMAT,
      version: VERSION,
      jobName: 'CUSTOMER_MIGRATION',
      description: '레거시 고객·주문 데이터를 NEXT_APP 스키마로 이관',
      // 접속은 마이그레이션 설정의 접속(profileId)을 가리키고, 이름·주소는 다른 PC에서 열 때 맞춰 보기 위한 사본
      source: { profileId: 'cn-legacy-prod', schema: 'LEGACY_APP' },
      target: { profileId: 'cn-next-prod', schema: 'NEXT_APP' },
      strategy: { mode: 'FULL', incrementalBy: 'PK', commitSize: 10000, fetchSize: 5000, errorPolicy: 'CONTINUE', errorTable: 'ERR$_', workers: 4 },
      mappings: [
        tableMapping({
          id: 'tm-customer', source: 'SRC_CUSTOMER', target: 'TB_MEMBER', mode: 'MERGE', mergeKey: ['MEMBER_ID'], checkpointColumn: 'CUSTOMER_ID',
          columns: [
            cm('MEMBER_ID', 'CUSTOMER_ID', '', 'REJECT'),
            cm('MEMBER_NAME', 'CUSTOMER_NM', 'TRIM(CUSTOMER_NM)', 'REJECT'),
            cm('MOBILE_NO', 'PHONE_NO', "REGEXP_REPLACE(PHONE_NO, '[^0-9]', '')", 'ALLOW'),
            cm('USE_YN', 'STATUS_CD', "CASE\n    WHEN STATUS_CD = 'A' THEN 'Y'\n    ELSE 'N'\nEND", 'DEFAULT', 'Y'),
            cm('MEMBER_GRADE', null, '', 'ALLOW'),
            cm('CREATED_AT', 'REG_DT', 'CAST(REG_DT AS TIMESTAMP)', 'SYSDATE'),
            cm('UPDATED_AT', 'MOD_DT', 'CAST(NVL(MOD_DT, REG_DT) AS TIMESTAMP)', 'ALLOW')
          ]
        }),
        tableMapping({
          id: 'tm-order', source: 'SRC_ORDER', target: 'TB_SALES_ORDER', mode: 'INSERT_ONLY', mergeKey: ['ORDER_NO'], checkpointColumn: 'ORDER_NO', where: "ORDER_STAT_CD <> '00'",
          columns: [
            cm('ORDER_NO', 'ORDER_NO', '', 'REJECT'),
            cm('MEMBER_ID', 'CUSTOMER_ID', '', 'REJECT'),
            cm('ORDERED_AT', 'ORDER_DT', 'CAST(ORDER_DT AS TIMESTAMP)', 'REJECT'),
            cm('TOTAL_AMT', 'ORDER_AMT', '', 'ALLOW'),
            cm('ORDER_STATUS', 'ORDER_STAT_CD', "DECODE(ORDER_STAT_CD,\n       '10', 'ORDERED',\n       '20', 'PAID',\n       '30', 'SHIPPED',\n       '90', 'CANCELED',\n       'UNKNOWN')", 'REJECT'),
            cm('CHANNEL_CD', null, '', 'REJECT'),
            cm('CREATED_AT', 'REG_DT', 'CAST(REG_DT AS TIMESTAMP)', 'ALLOW')
          ]
        }),
        tableMapping({
          id: 'tm-grade', source: 'SRC_CUSTOMER_GRADE', target: 'TB_MEMBER_GRADE', mode: 'TRUNCATE_INSERT', mergeKey: ['GRADE_CODE'],
          columns: [
            cm('GRADE_CODE', 'GRADE_CD', '', 'REJECT'),
            cm('GRADE_NAME', 'GRADE_NM', 'TRIM(GRADE_NM)', 'REJECT'),
            cm('DISPLAY_ORDER', 'SORT_SEQ', '', 'ALLOW')
          ]
        }),
        // SQL 원본: 고객 + 등급 JOIN → TB_MEMBER (등급 이름까지 채움). 같은 대상의 테이블 매핑과 겹치므로 기본은 사용 안 함
        sqlMapping({
          id: 'tm-sql-member', use: false, source: 'SQLMAP_MEMBER', sql: SAMPLE_SQL, target: 'TB_MEMBER', mode: 'MERGE', mergeKey: ['MEMBER_ID'],
          checkpointColumn: 'MEMBER_ID', fetchSize: 5000, commitSize: 10000,
          binds: [{ name: 'LAST_ID', type: 'NUMBER', value: '850000', fromCheckpoint: true }],
          columns: ['MEMBER_ID', 'MEMBER_NAME', 'MOBILE_NO', 'USE_YN', 'MEMBER_GRADE', 'CREATED_AT', 'UPDATED_AT'].map((c) => cm(c, c, '', c === 'MEMBER_ID' || c === 'MEMBER_NAME' ? 'REJECT' : c === 'USE_YN' ? 'DEFAULT' : 'ALLOW', c === 'USE_YN' ? 'Y' : ''))
        })
      ],
      // 지난 실행이 중간에 끊긴 상태(재개 예시)
      checkpoints: {
        'tm-customer': { column: 'CUSTOMER_ID', value: 850000, rows: 850000, total: 1240325, at: '2026-10-02 23:41:07', runId: 'R-20261002-234107', status: 'stopped' }
      }
    };
  }

  function blankJob(keep) {
    const j = sampleJob();
    j.jobName = 'NEW_MIGRATION';
    j.description = '';
    j.mappings = [];
    j.checkpoints = {};
    if (keep) {
      j.source = Object.assign({}, keep.source);
      j.target = Object.assign({}, keep.target);
      j.strategy = Object.assign({}, keep.strategy);
    }
    return j;
  }

  function newSqlMapping(n, o) {
    return sqlMapping(Object.assign({ source: 'SQLMAP_' + n }, o));
  }

  // ================= 저장 형식 =================
  /** 저장용 사본: 접속은 참조와 사본만(비밀번호 없음) */
  function toPlain(job, settings) {
    const conn = (c) => {
      const p = settings && MS.settingsModel.profile(settings, c.profileId);
      return p ? { profileId: c.profileId, schema: c.schema, name: p.name, kind: p.kind, host: p.host, port: p.port, service: p.service, user: p.user } : Object.assign({}, c);
    };
    return Object.assign({}, job, { source: conn(job.source), target: conn(job.target) });
  }

  function toJson(job, settings) {
    return JSON.stringify(toPlain(job, settings), null, 2);
  }

  /** 아주 작은 YAML 출력기(작업 파일 보기·저장용). 여러 줄 문자열은 | 블록으로 쓴다. */
  function toYaml(value, depth) {
    depth = depth || 0;
    const pad = '  '.repeat(depth);
    const scalar = (v) => {
      if (v == null) return 'null';
      if (typeof v === 'number' || typeof v === 'boolean') return String(v);
      const s = String(v);
      if (s === '' || /^[\s]|[\s]$|[:#\[\]{},&*!|>'"%@`]|^(true|false|null|yes|no|-?\d[\d.]*)$/i.test(s)) return "'" + s.replace(/'/g, "''") + "'";
      return s;
    };
    if (Array.isArray(value)) {
      if (!value.length) return ' []';
      return value.map((v) => {
        if (v && typeof v === 'object') {
          const inner = toYaml(v, depth + 1).replace(/^\n/, '');
          return '\n' + pad + '- ' + inner.trimStart();
        }
        return '\n' + pad + '- ' + scalar(v);
      }).join('');
    }
    if (value && typeof value === 'object') {
      const keys = Object.keys(value);
      if (!keys.length) return ' {}';
      return keys.map((k) => {
        const v = value[k];
        const key = /^[\w$.-]+$/.test(k) ? k : "'" + k + "'";
        if (typeof v === 'string' && v.includes('\n')) return '\n' + pad + key + ': |\n' + v.split('\n').map((l) => pad + '  ' + l).join('\n');
        if (v && typeof v === 'object') return '\n' + pad + key + ':' + toYaml(v, depth + 1);
        return '\n' + pad + key + ': ' + scalar(v);
      }).join('');
    }
    return ' ' + scalar(value);
  }

  function yamlOf(job, settings) {
    return '# Folderss Migration Studio 작업 파일\n' + toYaml(toPlain(job, settings)).replace(/^\n/, '') + '\n';
  }

  /** 버전 1(테이블 매핑 + SQL 매핑 따로) → 버전 2(매핑 하나에 원본 종류) */
  function upgradeV1(data) {
    const mappings = (data.tableMappings || []).map((m) => tableMapping(Object.assign({}, m, { sourceType: 'TABLE' })));
    for (const s of data.sqlMappings || []) {
      const columns = Object.entries(s.aliasMap || {}).map(([alias, target]) => cm(target, alias, '', 'ALLOW'));
      mappings.push(sqlMapping({ id: s.id, use: s.use, source: s.name, sql: s.sql, binds: s.binds || [], target: s.targetTable, mode: s.writeStrategy, mergeKey: s.mergeKey || [], checkpointColumn: s.checkpointColumn || null, fetchSize: s.fetchSize, commitSize: s.commitSize, columns }));
    }
    const conn = (c) => (c ? { profileId: null, schema: c.schema, name: c.name, kind: c.kind, host: c.host, port: c.port, service: c.service, user: c.user, color: c.color } : { profileId: null });
    return Object.assign({}, data, { version: VERSION, mappings, source: conn(data.source), target: conn(data.target) });
  }

  /** 작업 파일 읽기. 형식·버전을 확인하고 빠진 값은 기본값으로 채운다. */
  function parseJob(text) {
    let data = JSON.parse(text);
    if (data.format !== JOB_FORMAT) throw new Error('작업 파일이 아닙니다(format: ' + (data.format || '없음') + ')');
    if (data.version > VERSION) throw new Error('더 새 버전(' + data.version + ')의 작업 파일입니다');
    if (!data.version || data.version < 2) data = upgradeV1(data);
    const base = sampleJob();
    const job = Object.assign(blankJob(), data);
    delete job.tableMappings;
    delete job.sqlMappings;
    job.source = Object.assign({ profileId: null, schema: '' }, data.source);
    job.target = Object.assign({ profileId: null, schema: '' }, data.target);
    job.strategy = Object.assign({}, base.strategy, data.strategy);
    job.mappings = (data.mappings || []).map((m) => (m.sourceType === 'SQL' ? sqlMapping(m) : tableMapping(m)));
    job.checkpoints = data.checkpoints || {};
    return job;
  }

  function mappingTemplate(job, name) {
    return JSON.stringify({
      format: MAPPING_FORMAT,
      version: VERSION,
      name: name || job.jobName.toLowerCase() + '_mapping',
      sourceSchema: job.source.schema,
      targetSchema: job.target.schema,
      dictionary: { tables: MS.mapping.TABLE_DICT, columns: MS.mapping.COLUMN_DICT },
      mappings: job.mappings.map((m) => { const x = Object.assign({}, m); delete x.id; delete x.use; return x; })
    }, null, 2);
  }

  /** 매핑 템플릿 가져오기. 같은 원본→대상 매핑은 바꾸고 나머지는 더한다. → {added, replaced} */
  function applyTemplate(job, text) {
    let data = JSON.parse(text);
    if (data.format !== MAPPING_FORMAT) throw new Error('매핑 템플릿이 아닙니다(format: ' + (data.format || '없음') + ')');
    if (!data.version || data.version < 2) {
      data = Object.assign({}, data, { mappings: upgradeV1({ tableMappings: data.tableMappings, sqlMappings: data.sqlMappings }).mappings });
    }
    let added = 0;
    let replaced = 0;
    for (const m of data.mappings || []) {
      const next = m.sourceType === 'SQL' ? sqlMapping(Object.assign({}, m, { id: newId('tm-') })) : tableMapping(Object.assign({}, m, { id: newId('tm-') }));
      const at = job.mappings.findIndex((x) => x.sourceType === next.sourceType && x.source === next.source && x.target === next.target);
      if (at >= 0) { next.id = job.mappings[at].id; job.mappings[at] = next; replaced++; }
      else { job.mappings.push(next); added++; }
    }
    return { added, replaced };
  }

  MS.job = { JOB_FORMAT, MAPPING_FORMAT, VERSION, newId, sampleJob, blankJob, tableMapping, sqlMapping, newSqlMapping, toPlain, toJson, yamlOf, parseJob, mappingTemplate, applyTemplate, SAMPLE_SQL };
})();
