/*
 * 화면 구성 요소: 버튼·선택·구간 선택·필드·배지·카드·진행 막대·탭·빈 상태·SQL 강조·SQL 편집기.
 * WPF로 옮길 때 각 함수가 ShellUi/Theme 도우미 하나에 대응한다(예: btn primary → ShellUi.PrimaryButton, dbBadge → Theme.DbBadge).
 */
(function () {
  'use strict';
  const MS = window.MS;
  const { h, cx, icon } = MS;

  function btn(label, opts) {
    opts = opts || {};
    return h('button', {
      type: 'button',
      class: cx('btn', opts.primary && 'primary', opts.kind, opts.sm && 'sm'),
      title: opts.title,
      disabled: opts.disabled,
      'aria-label': !label ? opts.title : null,
      onClick: opts.onClick
    }, opts.icon && icon(opts.icon), label);
  }

  /** 구간 선택(세그먼트). options: [{value, label, disabled, title}] */
  function seg(options, value, onChange, label) {
    const el = h('div.seg', { role: 'radiogroup', 'aria-label': label });
    for (const o of options) {
      el.appendChild(h('button', {
        type: 'button', role: 'radio', 'aria-checked': String(o.value === value),
        class: cx(o.value === value && 'on'), disabled: o.disabled, title: o.title,
        onClick: () => onChange(o.value)
      }, o.label));
    }
    return el;
  }

  /** options: 문자열 또는 {value, label, disabled}. 그룹은 {group, options} */
  function select(options, value, onChange, opts) {
    opts = opts || {};
    const el = h('select', { class: opts.cell ? 'cell-select' : 'select', title: opts.title, disabled: opts.disabled, 'aria-label': opts.label });
    const add = (parent, o) => {
      const item = typeof o === 'object' ? o : { value: o, label: o };
      parent.appendChild(h('option', { value: item.value, disabled: item.disabled, selected: item.value === value }, item.label));
    };
    for (const o of options) {
      if (o && o.group) {
        const g = h('optgroup', { label: o.group });
        o.options.forEach((x) => add(g, x));
        el.appendChild(g);
      } else add(el, o);
    }
    el.value = value == null ? '' : value;
    if (onChange) el.addEventListener('change', () => onChange(el.value));
    return el;
  }

  function input(value, onInput, opts) {
    opts = opts || {};
    const el = h('input', {
      class: cx(opts.cell ? 'cell-input' : 'input', opts.mono && 'mono'),
      type: opts.type || 'text', value: value == null ? '' : value, placeholder: opts.placeholder,
      spellcheck: false, autocomplete: 'off', 'aria-label': opts.label, disabled: opts.disabled, title: opts.title
    });
    if (onInput) el.addEventListener('input', () => onInput(el.value));
    if (opts.onChange) el.addEventListener('change', () => opts.onChange(el.value));
    return el;
  }

  function field(label, control, opts) {
    opts = opts || {};
    return h('label', { class: cx('field', opts.wide && 'wide') },
      h('span.field-label', label, opts.required && h('span.req', '*'), opts.extra),
      control,
      opts.hint && h('span.field-hint', opts.hint));
  }

  function check(label, checked, onChange, title) {
    const box = h('input', { type: 'checkbox', checked: !!checked });
    box.addEventListener('change', () => onChange(box.checked));
    return h('label.check', { title }, box, label);
  }

  const LEVEL = {
    PASS: { cls: 'pass', glyph: 'check' },
    WARN: { cls: 'warn', glyph: 'warn' },
    ERROR: { cls: 'error', glyph: 'error' },
    INFO: { cls: 'info', glyph: 'info' },
    SKIP: { cls: 'muted', glyph: 'info' }
  };
  function badge(level, text) {
    const l = LEVEL[level] || LEVEL.INFO;
    return h('span', { class: cx('badge', l.cls) }, text == null ? level : text);
  }

  /** DB 배지(DB Helper Theme.DbBadge와 같은 규칙): 색 표시가 있으면 그 색 바탕, 없으면 강조색 테두리 */
  function dbBadge(conn) {
    if (!conn || !conn.name) return h('span.dbb.none', '대상 없음');
    return h('span', { class: cx('dbb', conn.color || ''), title: conn.host ? conn.host + ':' + conn.port + '/' + conn.service : null }, conn.name);
  }

  function card(opts) {
    return h('section', { class: cx('card', opts.cls), 'aria-label': typeof opts.title === 'string' ? opts.title : null },
      (opts.title || opts.tools) && h('div.card-head',
        opts.title && h('div.card-title', opts.title),
        opts.sub && h('span.card-sub', opts.sub),
        opts.tools && h('div.card-tools', opts.tools)),
      opts.body != null && h('div', { class: cx('card-body', opts.flush && 'flush') }, opts.body),
      opts.raw,
      opts.foot && h('div.card-foot', opts.foot));
  }

  function bar(pct, state, thick) {
    const fill = h('i', { style: { width: (pct || 0).toFixed(2) + '%' } });
    const el = h('div', { class: cx('bar', state, thick && 'thick'), role: 'progressbar', 'aria-valuemin': '0', 'aria-valuemax': '100', 'aria-valuenow': Math.round(pct || 0) }, fill);
    el.set = (p, s) => {
      fill.style.width = (p || 0).toFixed(2) + '%';
      el.setAttribute('aria-valuenow', Math.round(p || 0));
      el.className = cx('bar', s, thick && 'thick');
    };
    return el;
  }

  /** items: [{key, label, count, disabled}] */
  function tabs(items, active, onChange) {
    return h('div.tabs', { role: 'tablist' }, items.map((t) =>
      h('button', {
        type: 'button', role: 'tab', 'aria-selected': String(t.key === active), class: cx('tab', t.key === active && 'on'),
        disabled: t.disabled, onClick: () => onChange(t.key)
      }, t.icon && icon(t.icon), t.label, t.count != null && h('span.count', t.count))));
  }

  function empty(glyph, title, desc, action) {
    return h('div.empty', icon(glyph), h('div.empty-title', title), desc && h('div', desc), action && h('div', { style: { marginTop: '6px' } }, action));
  }

  function notice(kind, glyph, ...content) {
    return h('div', { class: cx('notice', kind) }, icon(glyph), h('div.grow', content));
  }

  // ================= SQL 강조 =================
  const KEYWORDS = new Set(('SELECT FROM WHERE AND OR NOT NULL IS IN AS ON JOIN LEFT RIGHT FULL INNER OUTER CROSS GROUP BY ORDER HAVING ' +
    'UNION ALL DISTINCT CASE WHEN THEN ELSE END MERGE INTO USING MATCHED UPDATE SET INSERT VALUES DELETE TRUNCATE TABLE ' +
    'WITH LIKE BETWEEN EXISTS ASC DESC DUAL FETCH FIRST ROWS ONLY OFFSET COMMIT ROLLBACK PARTITION OVER').split(' '));
  const FUNCS = new Set(('TRIM LTRIM RTRIM UPPER LOWER INITCAP NVL NVL2 COALESCE DECODE CAST REGEXP_REPLACE REGEXP_SUBSTR REPLACE SUBSTR ' +
    'LENGTH LPAD RPAD TO_CHAR TO_DATE TO_NUMBER TO_TIMESTAMP SYSDATE SYSTIMESTAMP COUNT SUM MIN MAX AVG ROUND TRUNC ABS ' +
    'ORA_HASH STANDARD_HASH ROW_NUMBER LISTAGG NUMBER VARCHAR2 CHAR DATE TIMESTAMP').split(' '));
  const SQL_RX = /(--[^\n]*|\/\*[\s\S]*?(?:\*\/|$))|('(?:[^']|'')*'?)|(:[A-Za-z_][\w$#]*)|(\b\d+(?:\.\d+)?\b)|([A-Za-z_][\w$#]*)/g;

  function esc(s) {
    return s.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c]);
  }

  function highlight(sql) {
    let out = '';
    let last = 0;
    sql.replace(SQL_RX, (m, cmt, str, bind, num, word, at) => {
      out += esc(sql.slice(last, at));
      last = at + m.length;
      let cls = null;
      if (cmt) cls = 'cmt';
      else if (str) cls = 'str';
      else if (bind) cls = 'bind';
      else if (num) cls = 'num';
      else if (KEYWORDS.has(word.toUpperCase())) cls = 'kw';
      else if (FUNCS.has(word.toUpperCase())) cls = 'fn';
      out += cls ? '<span class="tok-' + cls + '">' + esc(m) + '</span>' : esc(m);
      return m;
    });
    return out + esc(sql.slice(last));
  }

  function code(sql, cls) {
    return h('pre', { class: cx('code', cls), innerHTML: highlight(sql) });
  }

  /**
   * SQL 편집기: textarea 위에 강조한 pre를 겹친다. Tab = 공백 4칸, Ctrl+Enter = onRun.
   * WPF 구현에서는 DB Helper처럼 TextBox(고정폭)로 시작하고, 강조가 필요하면 AvalonEdit를 검토한다.
   */
  function sqlEditor(opts) {
    const gutter = h('div.gutter', { 'aria-hidden': 'true' });
    const pre = h('pre', { 'aria-hidden': 'true' });
    const area = h('textarea', { spellcheck: false, 'aria-label': opts.label || 'SQL 편집기', wrap: 'off', value: opts.value || '' });
    const el = h('div.editor', { style: opts.height ? { height: opts.height + 'px' } : null }, gutter, h('div.surface', pre, area));
    let errLine = null;

    function paint() {
      const text = area.value;
      pre.innerHTML = highlight(text) + '\n';
      const lines = text.split('\n').length;
      let g = '';
      for (let i = 1; i <= lines; i++) g += '<div' + (i === errLine ? ' class="err"' : '') + '>' + i + '</div>';
      gutter.innerHTML = g;
      sync();
    }
    function sync() {
      pre.scrollTop = area.scrollTop;
      pre.scrollLeft = area.scrollLeft;
      gutter.scrollTop = area.scrollTop;
    }
    function caret() {
      const before = area.value.slice(0, area.selectionStart).split('\n');
      return { line: before.length, col: before[before.length - 1].length + 1 };
    }
    area.addEventListener('input', () => {
      paint();
      if (opts.onChange) opts.onChange(area.value);
    });
    area.addEventListener('scroll', sync);
    area.addEventListener('keydown', (e) => {
      if (e.key === 'Tab' && !e.ctrlKey && !e.altKey) {
        e.preventDefault();
        document.execCommand('insertText', false, '    ');
      } else if (e.key === 'Enter' && e.ctrlKey) {
        e.preventDefault();
        if (opts.onRun) opts.onRun();
      }
    });
    for (const ev of ['keyup', 'click', 'select']) area.addEventListener(ev, () => opts.onCaret && opts.onCaret(caret()));
    paint();
    return {
      el,
      area,
      get value() { return area.value; },
      set value(v) { area.value = v; paint(); },
      markLine(line) { errLine = line; paint(); },
      focus() { area.focus(); }
    };
  }

  MS.ui = { btn, seg, select, input, field, check, badge, dbBadge, card, bar, tabs, empty, notice, highlight, code, sqlEditor, LEVEL };
})();
