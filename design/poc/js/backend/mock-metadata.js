/*
 * Mock 메타데이터와 샘플 행. 실제 구현에서는 OracleAdapter가 ALL_TABLES·ALL_TAB_COLUMNS·ALL_CONSTRAINTS·ALL_TAB_STATISTICS에서 읽는다.
 * 열 통계(stats)는 검증 화면이 "실측 최대 길이·NULL 수"를 보여 주는 데 쓴다(실제로는 샘플링 조회 결과).
 */
(function () {
  'use strict';
  const MS = window.MS;

  function col(name, type, o) {
    o = o || {};
    return { name, type, nullable: !o.nn, pk: !!o.pk, defaultValue: o.def || null, comment: o.comment || '', stats: o.stats || {} };
  }

  const SOURCE = {
    schema: 'LEGACY_APP',
    tables: [
      {
        name: 'SRC_CUSTOMER', kind: 'TABLE', rows: 1240325, comment: '고객', avgRowLen: 96,
        columns: [
          col('CUSTOMER_ID', 'NUMBER(12)', { pk: 1, nn: 1, comment: '고객 번호', stats: { nulls: 0, distinct: 1240325, max: 1240325 } }),
          col('CUSTOMER_NM', 'VARCHAR2(100)', { comment: '고객명', stats: { nulls: 0, blanks: 37, maxLen: 42 } }),
          col('PHONE_NO', 'VARCHAR2(30)', { comment: '전화번호', stats: { nulls: 1204, maxLen: 18, digitsMaxLen: 13 } }),
          col('STATUS_CD', 'CHAR(1)', { nn: 1, comment: '상태(A 사용·I 휴면·D 탈퇴)', stats: { nulls: 0, distinct: 3 } }),
          col('GRADE_CD', 'VARCHAR2(2)', { comment: '등급 코드', stats: { nulls: 2210, distinct: 6 } }),
          col('REG_DT', 'DATE', { nn: 1, comment: '등록일', stats: { nulls: 0 } }),
          col('MOD_DT', 'DATE', { comment: '수정일', stats: { nulls: 182004 } })
        ]
      },
      {
        name: 'SRC_CUSTOMER_GRADE', kind: 'TABLE', rows: 6, comment: '고객 등급', avgRowLen: 40,
        columns: [
          col('GRADE_CD', 'VARCHAR2(2)', { pk: 1, nn: 1, comment: '등급 코드' }),
          col('GRADE_NM', 'VARCHAR2(50)', { nn: 1, comment: '등급 이름', stats: { maxLen: 12 } }),
          col('SORT_SEQ', 'NUMBER(3)', { comment: '정렬 순서' })
        ]
      },
      {
        name: 'SRC_ORDER', kind: 'TABLE', rows: 8420117, comment: '주문', avgRowLen: 74,
        columns: [
          col('ORDER_NO', 'VARCHAR2(20)', { pk: 1, nn: 1, comment: '주문 번호', stats: { maxLen: 17 } }),
          col('CUSTOMER_ID', 'NUMBER(12)', { nn: 1, comment: '고객 번호' }),
          col('ORDER_DT', 'DATE', { nn: 1, comment: '주문 일시' }),
          col('ORDER_AMT', 'NUMBER(15,2)', { comment: '주문 금액', stats: { max: 48200000 } }),
          col('ORDER_STAT_CD', 'VARCHAR2(2)', { nn: 1, comment: '주문 상태 코드' }),
          col('PAY_METHOD', 'VARCHAR2(10)', { comment: '결제 수단' }),
          col('REG_DT', 'DATE', { nn: 1, comment: '등록일' })
        ]
      },
      {
        name: 'SRC_ORDER_ITEM', kind: 'TABLE', rows: 21904551, comment: '주문 품목', avgRowLen: 52,
        columns: [
          col('ORDER_NO', 'VARCHAR2(20)', { pk: 1, nn: 1 }),
          col('ITEM_SEQ', 'NUMBER(4)', { pk: 1, nn: 1 }),
          col('PRODUCT_CD', 'VARCHAR2(20)', { nn: 1 }),
          col('QTY', 'NUMBER(7)', { nn: 1 }),
          col('UNIT_PRICE', 'NUMBER(12,2)')
        ]
      },
      {
        name: 'SRC_PRODUCT', kind: 'TABLE', rows: 48210, comment: '상품', avgRowLen: 120,
        columns: [
          col('PRODUCT_CD', 'VARCHAR2(20)', { pk: 1, nn: 1 }),
          col('PRODUCT_NM', 'VARCHAR2(200)', { nn: 1 }),
          col('CATEGORY_CD', 'VARCHAR2(10)'),
          col('PRICE', 'NUMBER(12,2)'),
          col('USE_FLAG', 'CHAR(1)')
        ]
      },
      {
        name: 'SRC_CODE_MST', kind: 'TABLE', rows: 1204, comment: '공통 코드', avgRowLen: 60,
        columns: [
          col('CODE_GRP', 'VARCHAR2(20)', { pk: 1, nn: 1 }),
          col('CODE', 'VARCHAR2(20)', { pk: 1, nn: 1 }),
          col('CODE_NM', 'VARCHAR2(100)', { nn: 1 })
        ]
      },
      {
        name: 'V_CUSTOMER_SUMMARY', kind: 'VIEW', rows: null, comment: '고객 요약 뷰',
        columns: [
          col('CUSTOMER_ID', 'NUMBER(12)'),
          col('ORDER_CNT', 'NUMBER'),
          col('TOTAL_AMT', 'NUMBER')
        ]
      }
    ]
  };

  const TARGET = {
    schema: 'NEXT_APP',
    tablespace: { name: 'NEXT_DATA', freeGb: 182.4, totalGb: 400 },
    tables: [
      {
        name: 'TB_MEMBER', kind: 'TABLE', rows: 58225, comment: '회원(시범 이관분 58,225건 있음)', avgRowLen: 110,
        columns: [
          col('MEMBER_ID', 'NUMBER(18)', { pk: 1, nn: 1, comment: '회원 ID' }),
          col('MEMBER_NAME', 'VARCHAR2(150)', { nn: 1, comment: '회원 이름' }),
          col('MOBILE_NO', 'VARCHAR2(20)', { comment: '휴대폰 번호(숫자만)' }),
          col('USE_YN', 'CHAR(1)', { nn: 1, def: "'Y'", comment: '사용 여부' }),
          col('MEMBER_GRADE', 'VARCHAR2(50)', { comment: '등급 이름' }),
          col('CREATED_AT', 'TIMESTAMP', { nn: 1, comment: '생성 일시' }),
          col('UPDATED_AT', 'TIMESTAMP', { comment: '수정 일시' })
        ]
      },
      {
        name: 'TB_MEMBER_GRADE', kind: 'TABLE', rows: 0, comment: '회원 등급', avgRowLen: 48,
        columns: [
          col('GRADE_CODE', 'VARCHAR2(10)', { pk: 1, nn: 1 }),
          col('GRADE_NAME', 'VARCHAR2(100)', { nn: 1 }),
          col('DISPLAY_ORDER', 'NUMBER(5)')
        ]
      },
      {
        name: 'TB_SALES_ORDER', kind: 'TABLE', rows: 0, comment: '판매 주문', avgRowLen: 90,
        fks: [{ columns: ['MEMBER_ID'], ref: 'TB_MEMBER', name: 'FK_SALES_ORDER_MEMBER' }],
        columns: [
          col('ORDER_NO', 'VARCHAR2(30)', { pk: 1, nn: 1 }),
          col('MEMBER_ID', 'NUMBER(18)', { nn: 1 }),
          col('ORDERED_AT', 'TIMESTAMP', { nn: 1 }),
          col('TOTAL_AMT', 'NUMBER(13,2)'),
          col('ORDER_STATUS', 'VARCHAR2(10)', { nn: 1 }),
          col('CHANNEL_CD', 'VARCHAR2(10)', { nn: 1, comment: '판매 채널(신규 컬럼)' }),
          col('CREATED_AT', 'TIMESTAMP')
        ]
      },
      {
        name: 'TB_SALES_ORDER_ITEM', kind: 'TABLE', rows: 0, comment: '판매 주문 품목', avgRowLen: 60,
        fks: [{ columns: ['ORDER_NO'], ref: 'TB_SALES_ORDER', name: 'FK_ORDER_ITEM_ORDER' }, { columns: ['PRODUCT_ID'], ref: 'TB_PRODUCT', name: 'FK_ORDER_ITEM_PRODUCT' }],
        columns: [
          col('ORDER_NO', 'VARCHAR2(30)', { pk: 1, nn: 1 }),
          col('LINE_NO', 'NUMBER(5)', { pk: 1, nn: 1 }),
          col('PRODUCT_ID', 'VARCHAR2(30)', { nn: 1 }),
          col('QUANTITY', 'NUMBER(9)', { nn: 1 }),
          col('UNIT_PRICE', 'NUMBER(14,2)')
        ]
      },
      {
        name: 'TB_PRODUCT', kind: 'TABLE', rows: 0, comment: '상품', avgRowLen: 140,
        columns: [
          col('PRODUCT_ID', 'VARCHAR2(30)', { pk: 1, nn: 1 }),
          col('PRODUCT_NAME', 'VARCHAR2(300)', { nn: 1 }),
          col('CATEGORY_ID', 'VARCHAR2(20)'),
          col('LIST_PRICE', 'NUMBER(14,2)'),
          col('USE_YN', 'CHAR(1)', { nn: 1, def: "'Y'" })
        ]
      },
      {
        name: 'TB_COMMON_CODE', kind: 'TABLE', rows: 0, comment: '공통 코드', avgRowLen: 70,
        columns: [
          col('GROUP_CODE', 'VARCHAR2(30)', { pk: 1, nn: 1 }),
          col('CODE', 'VARCHAR2(30)', { pk: 1, nn: 1 }),
          col('CODE_NAME', 'VARCHAR2(200)', { nn: 1 })
        ]
      }
    ]
  };

  // ================= 샘플 행 =================
  function rng(seed) {
    return function () {
      seed = (seed + 0x6d2b79f5) | 0;
      let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }
  const pick = (r, list) => list[Math.floor(r() * list.length)];
  const d = (s) => new Date(s.replace(' ', 'T'));
  // Oracle DATE는 초 단위까지만
  const sec = (ms) => new Date(Math.floor(ms / 1000) * 1000);

  const NAMES = ['김민준', '이서연', '박지호', '최수아', '정도윤', '강하은', '조예준', '윤지우', '장서준', '임하린', '오지민', '한유진', '신우진', '서지안', '권다은'];
  const GRADES = [
    { GRADE_CD: 'G1', GRADE_NM: '일반', SORT_SEQ: 1 },
    { GRADE_CD: 'G2', GRADE_NM: '실버', SORT_SEQ: 2 },
    { GRADE_CD: 'G3', GRADE_NM: '골드', SORT_SEQ: 3 },
    { GRADE_CD: 'G4', GRADE_NM: '플래티넘', SORT_SEQ: 4 },
    { GRADE_CD: 'VP', GRADE_NM: 'VIP', SORT_SEQ: 5 },
    { GRADE_CD: 'ZZ', GRADE_NM: '휴면', SORT_SEQ: 9 }
  ];

  // 맨 앞 행들은 변환식 효과(공백 이름·NULL 수정일·형식이 다른 전화번호)가 바로 보이게 고른다
  const CURATED_CUSTOMERS = [
    ['김민준', '010-1234-5678', 'A', 'G2', '2019-03-14 09:12:33', '2024-11-02 18:40:11'],
    ['  이서연 ', '010 2345 6789', 'A', 'G1', '2020-07-01 13:05:00', null],
    ['박지호', '(02)555-0101', 'I', 'G3', '2018-12-24 20:44:10', '2023-05-19 08:00:00'],
    ['   ', null, 'D', null, '2021-01-09 11:11:11', null],
    ['Choi Su-A', '+82-10-9876-5432', 'A', 'VP', '2017-06-30 07:30:00', '2025-02-14 10:10:10'],
    ['정도윤', '010.7777.1234', 'A', 'G4', '2022-09-03 16:20:45', '2026-01-08 09:00:00']
  ];

  function customer(id, i, r) {
    const c = CURATED_CUSTOMERS[i];
    if (c) return { CUSTOMER_ID: id, CUSTOMER_NM: c[0], PHONE_NO: c[1], STATUS_CD: c[2], GRADE_CD: c[3], REG_DT: d(c[4]), MOD_DT: c[5] && d(c[5]) };
    const reg = new Date(2015, 0, 1).getTime() + r() * 3.3e11;
    return {
      CUSTOMER_ID: id,
      CUSTOMER_NM: (r() < 0.12 ? ' ' : '') + pick(r, NAMES) + (r() < 0.1 ? '  ' : ''),
      PHONE_NO: r() < 0.06 ? null : '010-' + String(1000 + Math.floor(r() * 9000)) + '-' + String(1000 + Math.floor(r() * 9000)),
      STATUS_CD: r() < 0.88 ? 'A' : r() < 0.6 ? 'I' : 'D',
      GRADE_CD: r() < 0.03 ? null : pick(r, GRADES).GRADE_CD,
      REG_DT: sec(reg),
      MOD_DT: r() < 0.15 ? null : sec(reg + r() * 9e10)
    };
  }

  function order(i, r) {
    const dt = new Date(2023, 0, 1).getTime() + r() * 9e10;
    const day = sec(dt);
    const ymd = day.getFullYear() + String(day.getMonth() + 1).padStart(2, '0') + String(day.getDate()).padStart(2, '0');
    return {
      ORDER_NO: 'O' + ymd + String(100000 + i).slice(1),
      CUSTOMER_ID: 1 + Math.floor(r() * 1240325),
      ORDER_DT: day,
      ORDER_AMT: Math.round(r() * 48000000) / 100,
      ORDER_STAT_CD: pick(r, ['10', '20', '30', '90']),
      PAY_METHOD: pick(r, ['CARD', 'BANK', 'POINT', null]),
      REG_DT: sec(dt + 3e5)
    };
  }

  function generic(table, i, r) {
    const row = {};
    for (const c of table.columns) {
      const base = c.type.replace(/\(.*/, '');
      if (c.nullable && r() < 0.05) row[c.name] = null;
      else if (base === 'NUMBER') row[c.name] = c.pk ? i + 1 : Math.floor(r() * 10000) / (c.type.includes(',') ? 100 : 1);
      else if (base === 'DATE' || base === 'TIMESTAMP') row[c.name] = new Date(2020, 0, 1 + Math.floor(r() * 2000));
      else if (base === 'CHAR') row[c.name] = pick(r, ['Y', 'N']);
      else row[c.name] = c.name.slice(0, 4) + '-' + String(i + 1).padStart(5, '0');
    }
    return row;
  }

  /** 원본 테이블의 샘플 행. fromId가 있으면 숫자 PK를 그 값부터 매긴다(증분·체크포인트 미리보기). */
  function sampleRows(tableName, n, opts) {
    opts = opts || {};
    const r = rng(opts.seed || 7);
    const rows = [];
    if (tableName === 'SRC_CUSTOMER_GRADE') return GRADES.slice(0, n).map((g) => Object.assign({}, g));
    for (let i = 0; i < n; i++) {
      if (tableName === 'SRC_CUSTOMER') rows.push(customer((opts.fromId || 1) + i, i, r));
      else if (tableName === 'SRC_ORDER') rows.push(order(i, r));
      else {
        const t = SOURCE.tables.find((x) => x.name === tableName);
        if (!t) break;
        rows.push(generic(t, i, r));
      }
    }
    return rows;
  }

  MS.mockDb = { SOURCE, TARGET, GRADES, sampleRows };
})();
