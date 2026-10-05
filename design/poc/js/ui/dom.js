/*
 * 화면 공용 도우미: 요소 만들기, 숫자·시간 형식, 아이콘 글리프, 대화상자·알림·파일 입출력.
 * 화면(ui)만 쓰고 백엔드(backend)는 이 파일을 모른다.
 */
(function () {
  'use strict';
  const MS = (window.MS = window.MS || {});

  /**
   * h('div.card#id', {onClick, title}, ...children)
   * attrs는 생략할 수 있다. on* → 이벤트, value·checked·disabled 등은 속성(property)으로 넣는다.
   */
  function h(sel, attrs, ...kids) {
    const m = /^([a-z0-9]*)(.*)$/i.exec(sel);
    const el = document.createElement(m[1] || 'div');
    for (const part of m[2].match(/[.#][\w-]+/g) || []) {
      if (part[0] === '.') el.classList.add(part.slice(1));
      else el.id = part.slice(1);
    }
    if (attrs != null && (typeof attrs !== 'object' || attrs instanceof Node || Array.isArray(attrs))) {
      kids.unshift(attrs);
      attrs = null;
    }
    for (const [k, v] of Object.entries(attrs || {})) {
      if (v == null || v === false) continue;
      if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
      else if (k === 'class') { for (const c of String(v).split(/\s+/)) if (c) el.classList.add(c); }
      else if (k === 'style' && typeof v === 'object') Object.assign(el.style, v);
      else if (k === 'dataset') Object.assign(el.dataset, v);
      else if (PROPS.has(k)) el[k] = v;
      else el.setAttribute(k, v === true ? '' : v);
    }
    append(el, kids);
    return el;
  }
  const PROPS = new Set(['value', 'checked', 'disabled', 'selected', 'innerHTML', 'textContent', 'tabIndex', 'htmlFor', 'multiple', 'readOnly', 'spellcheck']);

  function append(el, kids) {
    for (const kid of kids) {
      if (kid == null || kid === false || kid === true) continue;
      if (Array.isArray(kid)) append(el, kid);
      else el.appendChild(kid instanceof Node ? kid : document.createTextNode(String(kid)));
    }
    return el;
  }

  function cx(...parts) {
    return parts.filter(Boolean).join(' ');
  }

  // Segoe Fluent Icons(Windows 11)·Segoe MDL2 Assets(Windows 10) — DB Helper ShellMenu와 같은 글꼴
  const ICON = {
    newDoc: '', open: '', save: '', saveAs: '', importFile: '', exportFile: '',
    code: '', link: '', play: '', pause: '', stop: '', refresh: '',
    resume: '', check: '', close: '', add: '', del: '', arrow: '',
    back: '', chevR: '', chevD: '', info: '', warn: '', error: '',
    copy: '', search: '', setting: '', history: '', checklist: '', list: '',
    view: '', swap: '', help: '', key: '', sync: '', filter: '',
    flash: '', table: '', theme: '', clear: '', magic: '', page: ''
  };

  function icon(name, cls) {
    return h('i', { class: cx('ico', cls), 'aria-hidden': 'true' }, ICON[name] || name);
  }

  const fmt = {
    n(v) {
      return v == null || Number.isNaN(v) ? '—' : Math.round(v).toLocaleString('en-US');
    },
    /** 1,240,325 → 1.24M (좁은 칸) */
    short(v) {
      if (v == null) return '—';
      if (v >= 1e6) return (v / 1e6).toFixed(v >= 1e7 ? 1 : 2) + 'M';
      if (v >= 1e4) return (v / 1e3).toFixed(0) + 'K';
      return fmt.n(v);
    },
    pct(part, total) {
      if (!total) return 0;
      return Math.min(100, Math.max(0, (part / total) * 100));
    },
    dur(sec) {
      if (sec == null || !isFinite(sec)) return '—';
      sec = Math.max(0, Math.round(sec));
      const p = (x) => String(x).padStart(2, '0');
      return p(Math.floor(sec / 3600)) + ':' + p(Math.floor(sec / 60) % 60) + ':' + p(sec % 60);
    },
    time(d) {
      d = d || new Date();
      const p = (x) => String(x).padStart(2, '0');
      return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
    },
    stamp(d) {
      d = d || new Date();
      const p = (x) => String(x).padStart(2, '0');
      return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + ' ' + fmt.time(d);
    }
  };

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  // ================= 알림 =================
  function toast(message, kind) {
    const host = document.getElementById('toasts');
    const glyph = { ok: 'check', warn: 'warn', err: 'error' }[kind] || 'info';
    const el = h('div', { class: cx('toast', kind), role: 'status' }, icon(glyph), h('span', message));
    host.appendChild(el);
    setTimeout(() => el.remove(), kind === 'err' ? 5200 : 3200);
  }

  // ================= 대화상자 =================
  /**
   * modal({title, body, buttons:[{label, primary, kind, onClick → false면 안 닫음}], width})
   * 팝업 규칙: 위에 제목, 아래 오른쪽에 확인·닫기. Esc = 닫기, Enter = 주 버튼(입력칸 밖).
   */
  function modal(opts) {
    const back = h('div.modal-back');
    const close = () => {
      if (!back.isConnected) return;
      back.remove();
      document.removeEventListener('keydown', onKey, true);
      if (prev && prev.focus) prev.focus();
      if (opts.onClose) opts.onClose();
    };
    const buttons = (opts.buttons || [{ label: '닫기' }]).map((b) =>
      h('button', {
        class: cx('btn', b.primary && 'primary', b.kind),
        disabled: b.disabled,
        onClick: async () => {
          const keep = b.onClick ? await b.onClick() : undefined;
          if (keep !== false) close();
        }
      }, b.icon && icon(b.icon), b.label)
    );
    const box = h('div', { class: cx('modal', opts.cls), role: 'dialog', 'aria-modal': 'true', 'aria-label': opts.title, style: opts.width ? { width: 'min(' + opts.width + 'px, 100%)' } : null },
      h('div.modal-head', h('span.modal-title', opts.title), opts.headExtra, h('button.btn.ghost.sm.x', { title: '닫기 (Esc)', onClick: close }, icon('close'))),
      h('div.modal-body', opts.body),
      h('div.modal-foot', buttons)
    );
    back.appendChild(box);
    back.addEventListener('mousedown', (e) => { if (e.target === back) close(); });
    const prev = document.activeElement;
    const onKey = (e) => {
      // 대화상자가 겹치면 맨 위 것만 키를 받는다
      if (back !== document.getElementById('overlay').lastElementChild) return;
      if (e.key === 'Escape') { e.preventDefault(); close(); }
      if (e.key === 'Enter' && !e.ctrlKey && !/^(TEXTAREA|INPUT|SELECT|BUTTON)$/.test(e.target.tagName)) {
        const primary = buttons.find((b) => b.classList.contains('primary'));
        if (primary && !primary.disabled) { e.preventDefault(); primary.click(); }
      }
    };
    document.addEventListener('keydown', onKey, true);
    document.getElementById('overlay').appendChild(back);
    const first = box.querySelector('.modal-body input, .modal-body select, .modal-body textarea') || buttons[buttons.length - 1];
    if (first) first.focus();
    return { close, box };
  }

  function confirmBox(title, message, okLabel, kind) {
    return new Promise((resolve) => {
      let done = false;
      const m = modal({
        title,
        body: typeof message === 'string' ? h('p', { style: { margin: 0 } }, message) : message,
        buttons: [
          { label: '취소', onClick: () => { done = true; resolve(false); } },
          { label: okLabel || '확인', primary: true, kind, onClick: () => { done = true; resolve(true); } }
        ]
      });
      // Esc·바깥 누름으로 닫으면 취소
      const obs = new MutationObserver(() => {
        if (!m.box.isConnected) { obs.disconnect(); if (!done) resolve(false); }
      });
      obs.observe(document.getElementById('overlay'), { childList: true });
    });
  }

  // ================= 파일 =================
  function download(filename, text, mime) {
    const blob = new Blob([text], { type: (mime || 'application/json') + ';charset=utf-8' });
    const a = h('a', { href: URL.createObjectURL(blob), download: filename });
    document.body.appendChild(a);
    a.click();
    setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 0);
  }

  function pickFile(accept) {
    return new Promise((resolve) => {
      const input = h('input', { type: 'file', accept: accept || '.json', style: { display: 'none' } });
      input.addEventListener('change', async () => {
        const file = input.files && input.files[0];
        input.remove();
        resolve(file ? { name: file.name, text: await file.text() } : null);
      });
      document.body.appendChild(input);
      input.click();
    });
  }

  async function copyText(text) {
    try {
      await navigator.clipboard.writeText(text);
      toast('클립보드에 복사했습니다', 'ok');
    } catch (e) {
      toast('복사하지 못했습니다: ' + e.message, 'err');
    }
  }

  MS.h = h;
  MS.cx = cx;
  MS.icon = icon;
  MS.ICON = ICON;
  MS.fmt = fmt;
  MS.sleep = sleep;
  MS.toast = toast;
  MS.modal = modal;
  MS.confirmBox = confirmBox;
  MS.download = download;
  MS.pickFile = pickFile;
  MS.copyText = copyText;
})();
