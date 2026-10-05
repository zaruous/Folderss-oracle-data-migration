/*
 * 이관 엔진(시뮬레이션). 실제 엔진과 같은 단계·같은 상태 전이를 흉내 낸다.
 *
 *   원본 읽기(Fetch 단위 스트리밍) → Transform(원본 SELECT 안에서 Oracle이 계산) → 컬럼 매핑(별칭 → 대상 열)
 *   → 배치 쓰기(배열 바인드, 커밋 크기만큼) → 커밋 → 체크포인트(마지막 커밋 키 저장)
 *
 * - 메모리에는 Fetch 버퍼와 쓰기 배치(작업자 수만큼)만 둔다. 전체 결과를 올리지 않는다.
 * - 일시정지: 진행 중 배치를 커밋한 뒤(커밋 경계) 멈춘다. 중지: 진행 중 배치를 롤백하고 마지막 커밋 키를 체크포인트로 남긴다.
 * - 재개: 체크포인트 키 다음부터 다시 읽는다(WHERE 키 > :LAST_ID ORDER BY 키).
 * - 오류 정책: 계속(행 오류는 오류 테이블, 일시 오류는 3회 재시도) · 오류 시 중지 · 3회 재시도 후 중지.
 * - 실행 위치: Folderss 프로세스가 아니라 플러그인이 띄운 하위 프로세스(MigrationAgent.exe). 팝업·Folderss를 닫아도 계속하고,
 *   다시 열면 파이프로 다시 붙는다. 이 시뮬레이션은 브라우저 안에서 같은 상태·이벤트를 흉내 낸다.
 * 시간은 시뮬레이션 배속(speed)만큼 빨리 흐른다. 표시하는 속도·경과 시간은 시뮬레이션 기준이다.
 */
(function () {
  'use strict';
  const MS = window.MS;
  const { fmt } = MS;
  const V = MS.validation;
  const M = MS.mapping;

  const TICK_MS = 100;

  /** 실행 계획 만들기: 고른 작업을 실행 순서대로, 원본 범위·재개 위치·예상 거부를 계산한다. 원본이 테이블이든 SQL이든 같은 모양 */
  function buildPlan(job, meta, selected, runMode) {
    const planned = V.plannedMappings(Object.assign({}, job, {
      mappings: job.mappings.map((m) => Object.assign({}, m, { use: selected.has(m.id) }))
    }), meta.target);
    return planned.map((p) => {
      const m = p.m;
      const tgt = meta.target.tables.find((t) => t.name === p.target) || { rows: 0, avgRowLen: 100, columns: [] };
      const src = V.sourceOf(m, meta.source) || { rows: 0, avgRowLen: 100, columns: [] };
      const cp = job.checkpoints[m.id];
      const isSql = m.sourceType === 'SQL';
      const scope = src.rows || 0;
      const rej = V.expectedRejects(m, src, tgt);
      const broken = isSql && (!src.columns.length || MS.sqlMapping.validate(m, meta.source, meta.target).items.some((i) => i.level === 'ERROR' && ['SQL 구문', '원본 객체·열', '바인드 변수'].includes(i.check)));
      // SQL 원본의 체크포인트: SQL 안의 체크포인트 바인드 값(예: :LAST_ID = 850000)부터 1씩
      const cpBind = isSql ? (m.binds || []).find((b) => b.fromCheckpoint) : null;
      const from = cpBind && /^\d+$/.test(String(cpBind.value)) ? Number(cpBind.value) : 0;
      const item = {
        key: m.id, kind: isSql ? 'sql' : 'table', label: p.label, source: m.source, target: p.target, mode: m.mode,
        scopeTotal: scope, checkpointColumn: m.checkpointColumn,
        keyAt: isSql && m.checkpointColumn ? (n) => from + n : keyFn(m.checkpointColumn, src, 0, scope),
        // MERGE에서 갱신될 기존 행: 테이블은 겹치는 만큼, SQL은 체크포인트 이후 키 범위 비율만큼
        existing: m.mode !== 'MERGE' ? 0 : isSql ? Math.round((tgt.rows || 0) * scope / Math.max(1, scope + from)) : Math.min(tgt.rows || 0, scope),
        // SQL 원본의 NULL 통계는 주 테이블 전체 기준이라 결과 범위 비율만큼 줄인다
        rejectTotal: rej.all || broken ? scope : Math.round(rej.nulls * (isSql && src.baseRows ? scope / src.baseRows : 1)) + rej.dups, failAll: rej.all || broken,
        rejectReason: broken ? 'ORA-00904: 원본 SQL 오류 — SQL 원본 편집기에서 검증하세요'
          : rej.all ? 'ORA-01400: NULL을 ("' + job.target.schema + '"."' + p.target + '") NOT NULL 컬럼에 넣을 수 없음'
            : rej.dups ? 'ORA-00001: 무결성 제약 조건(PK_' + p.target + ')에 위배됩니다' : 'ORA-01400: NULL을 NOT NULL 컬럼에 넣을 수 없음(공백만 있는 이름 등)',
        rate: (4.6e6 / (src.avgRowLen || 100)) * (isSql && src.parsed && src.parsed.tables.length > 1 ? 0.7 : 1),
        transientAt: m.source === 'SRC_ORDER' ? 0.55 : null,
        nullChecks: V.nullChecks(m, src, tgt),
        columns: m.columns.filter((c) => M.valueSource(c)).length,
        sqlFrom: from
      };
      item.modeLabel = M.modeOf(item.mode).label;
      item.errorTable = job.strategy.errorPolicy === 'CONTINUE' ? job.target.schema + '.' + MS.sqlgen.errorTableFor(job.strategy, item.target) : null;
      item.base = runMode === 'RESUME' && cp && cp.status !== 'done' ? Math.min(cp.rows || 0, item.scopeTotal) : 0;
      item.resumeFrom = item.base ? cp : null;
      return item;
    });
  }

  /** 체크포인트 키: 숫자 PK는 진행 행 수로, 문자 키(ORDER_NO)는 날짜+일련번호 모양으로 만든다. */
  function keyFn(column, src, start, total) {
    if (!column) return () => null;
    const col = src.columns.find((c) => c.name === column);
    if (col && /^NUMBER/.test(col.type)) {
      const max = (col.stats && col.stats.max) || total;
      return (n) => start + Math.round(n * max / Math.max(1, total));
    }
    return (n) => {
      const d = new Date(2023, 0, 1 + Math.floor((n / Math.max(1, total)) * 1000));
      return 'O' + d.getFullYear() + String(d.getMonth() + 1).padStart(2, '0') + String(d.getDate()).padStart(2, '0') + String((n * 7919) % 100000).padStart(5, '0');
    };
  }

  function niceStep(total) {
    const target = Math.max(1, total / 8);
    const p = Math.pow(10, Math.floor(Math.log10(target)));
    return [1, 2, 5, 10].map((m) => m * p).reduce((a, b) => (Math.abs(b - target) < Math.abs(a - target) ? b : a));
  }

  class MigrationEngine {
    /** listener(type, snapshot, payload): 'update' · 'log' · 'checkpoint' · 'end' */
    constructor(listener) {
      this.listener = listener;
      this.snap = null;
      this.timer = null;
    }

    get state() {
      return this.snap ? this.snap.state : 'idle';
    }

    get active() {
      return ['running', 'pausing', 'paused'].includes(this.state);
    }

    start(plan, opts) {
      if (this.active) return;
      this.opts = opts;
      const now = new Date();
      const runId = 'R-' + now.getFullYear() + String(now.getMonth() + 1).padStart(2, '0') + String(now.getDate()).padStart(2, '0') + '-' + String(now.getHours()).padStart(2, '0') + String(now.getMinutes()).padStart(2, '0') + String(now.getSeconds()).padStart(2, '0');
      this.snap = {
        runId, state: 'running', mode: opts.runMode, dry: opts.runMode === 'DRY', elapsed: 0, rate: 0, current: 0,
        jobs: plan.map((p) => Object.assign({}, p, {
          status: 'wait', total: p.scopeTotal - p.base, read: 0, written: 0, pending: 0, inserted: 0, updated: 0, rejected: 0,
          commits: 0, checkpoint: p.base ? p.keyAt(p.base) : null, elapsed: 0, stall: 0, rateNow: 0, warnedReject: false
        })),
        pipeline: { buffer: 0, inflight: 0 },
        // 실제 구현: 플러그인이 MigrationAgent.exe를 하위 프로세스로 띄우고 이름 있는 파이프로 실행 사양을 넘긴 뒤 진행을 받는다
        agent: { exe: 'MigrationAgent.exe', pid: 10000 + Math.floor(Math.random() * 50000), pipe: 'folderss-migration-' + runId, onHostExit: opts.onHostExit || 'CONTINUE' },
        log: []
      };
      const modeText = { DRY: 'Dry Run(쓰기 없음)', EXECUTE: '이관 실행', RESUME: '체크포인트에서 재개' }[opts.runMode];
      const a = this.snap.agent;
      this.log('start', 'START', '실행 ' + runId + ' · ' + modeText + ' · 작업 ' + plan.length + '개\n' +
        '에이전트 ' + a.exe + ' 시작(PID ' + a.pid + ', 파이프 ' + a.pipe + ') · ' + (a.onHostExit === 'CONTINUE' ? '창·Folderss를 닫아도 계속' : 'Folderss를 닫으면 함께 중지') + '\n' +
        'Batch size=' + fmt.n(opts.commitSize) + ' · Fetch=' + fmt.n(opts.fetchSize) + ' · Workers=' + opts.workers + ' · 오류 정책=' + policyText(opts.errorPolicy) + ' · 체크포인트=' + (opts.checkpointStore || '대상 DB(MIG_CHECKPOINT)'));
      this.emit('update');
      this.timer = setInterval(() => this.safeTick(), TICK_MS);
    }

    safeTick() {
      try {
        this.tick();
      } catch (e) {
        // 화면 쪽 오류로 시뮬레이션이 멈추지 않게: 기록하고 실패로 끝낸다
        console.error(e);
        this.fail(this.snap.jobs[this.snap.current], '엔진 내부 오류: ' + e.message);
      }
    }

    tick() {
      const s = this.snap;
      if (!s || s.state === 'paused') return;
      const dt = (TICK_MS / 1000) * this.opts.speed;
      s.elapsed += dt;
      const j = s.jobs[s.current];
      if (!j) return this.finish();
      if (s.state === 'pausing') {
        this.commitPending(j);
        s.state = 'paused';
        if (j.status === 'run') j.status = 'paused';
        s.rate = 0;
        this.log('pause', 'PAUSE', '커밋 경계에서 일시 정지 · ' + j.label + (j.checkpoint != null ? ' · 마지막 커밋 ' + j.checkpointColumn + ' = ' + j.checkpoint : ''));
        this.emit('update');
        return;
      }
      if (j.status === 'wait') return this.beginJob(j);
      j.elapsed += dt;
      if (j.stall > 0) {
        j.stall--;
        j.rateNow = 0;
        s.rate = 0;
        this.emit('update');
        return;
      }
      const workers = this.opts.workers;
      const modeFactor = { MERGE: 0.82, INSERT_ONLY: 1, TRUNCATE_INSERT: 1.12, DELETE_INSERT: 0.6 }[j.mode] || 1;
      j.rateNow = j.rate * Math.pow(workers / 4, 0.85) * modeFactor * (s.dry ? 1.7 : 1) * (0.88 + Math.random() * 0.24);
      const n = Math.min(j.total - j.read, Math.max(1, Math.round(j.rateNow * dt)));
      j.read += n;
      j.pending += n;
      s.rate = s.rate ? s.rate * 0.7 + j.rateNow * 0.3 : j.rateNow;

      if (j.transientAt && !j.transientDone && !s.dry && (j.base + j.read) / j.scopeTotal >= j.transientAt) {
        if (!this.transient(j)) return;
      }
      const size = this.opts.commitSize;
      while (j.pending >= size || (j.read >= j.total && j.pending > 0)) {
        if (!this.commitBatch(j, Math.min(j.pending, size))) return;
      }
      if (j.read >= j.total && j.pending === 0) this.completeJob(j);
      s.pipeline = {
        buffer: s.state === 'running' ? 35 + Math.random() * 50 : 0,
        inflight: Math.min(workers, Math.max(1, Math.ceil(j.pending / Math.max(1, size / workers))))
      };
      this.emit('update');
    }

    beginJob(j) {
      const s = this.snap;
      j.status = 'run';
      const lines = [j.label + '  (' + j.modeLabel + (j.kind === 'sql' ? ' · SQL 매핑' : '') + ')'];
      if (j.base) lines.push('체크포인트에서 재개: ' + j.checkpointColumn + ' > ' + j.resumeFrom.value + ' · 남은 ' + fmt.n(j.total) + '행');
      else if (j.checkpointColumn) lines.push('범위: ' + j.checkpointColumn + ' 순서로 ' + fmt.n(j.scopeTotal) + '행' + (j.kind === 'sql' ? ' (:LAST_ID = ' + j.sqlFrom + ')' : ''));
      else lines.push('범위: ' + fmt.n(j.scopeTotal) + '행 (체크포인트 없음 — 중단하면 처음부터)');
      this.log(j.base ? 'resume' : 'start', j.base ? 'RESUME' : 'START', lines.join('\n'));
      if (j.mode === 'TRUNCATE_INSERT' && !s.dry) this.log('info', 'INFO', 'TRUNCATE TABLE ' + j.target + ' 완료');
      if (j.mode === 'MERGE') this.log('info', 'INFO', 'MERGE enabled · 대상 기존 ' + fmt.n(j.existing) + '행은 갱신 예상');
      if (s.dry) this.log('dry', 'DRY', '쓰기 없음 — 원본 읽기·변환·매핑만 하고 쓰기 문은 만들기만 함');
      this.emit('update');
    }

    commitBatch(j, c) {
      const s = this.snap;
      const doneBefore = j.base + j.written;
      const share = (total) => Math.round(total * (doneBefore + c) / j.scopeTotal) - Math.round(total * doneBefore / j.scopeTotal);
      const r = j.failAll ? c : Math.max(0, Math.min(c, share(j.rejectTotal)));
      if (r > 0 && !s.dry && this.opts.errorPolicy !== 'CONTINUE') {
        if (this.opts.errorPolicy === 'RETRY') for (let k = 1; k <= 3; k++) this.log('warn', 'WARN', '배치 오류 — 재시도 ' + k + '/3: ' + j.rejectReason);
        this.fail(j, j.rejectReason + '\n' + (this.opts.errorPolicy === 'RETRY' ? '데이터 오류는 재시도로 해결되지 않음 — ' : '') + '오류 시 중지 정책으로 멈춤 · 진행 중 배치 ' + fmt.n(j.pending) + '행 롤백');
        return false;
      }
      const u = Math.max(0, Math.min(c - r, share(j.existing)));
      j.written += c;
      j.pending -= c;
      j.rejected += r;
      j.updated += u;
      j.inserted += c - r - u;
      j.commits++;
      if (j.checkpointColumn) {
        j.checkpoint = j.keyAt(j.base + j.written);
        if (!s.dry) this.emit('checkpoint', { key: j.key, column: j.checkpointColumn, value: j.checkpoint, rows: j.base + j.written, total: j.scopeTotal, status: 'running', runId: s.runId });
      }
      if (r > 0 && !j.warnedReject) {
        j.warnedReject = true;
        this.log('warn', 'WARN', (s.dry ? '거부 예상 ' : '거부 ') + fmt.n(r) + '행' + (j.errorTable && !s.dry ? ' → ' + j.errorTable : '') + '\n' + j.rejectReason);
      }
      const step = niceStep(j.scopeTotal);
      const done = j.base + j.written;
      if (Math.floor(done / step) > Math.floor(doneBefore / step) && done < j.scopeTotal) {
        this.log(s.dry ? 'dry' : 'commit', s.dry ? 'DRY' : 'INFO', (s.dry ? fmt.n(done) + '행 읽기·변환' : 'Committed ' + fmt.n(done) + ' rows') + (j.checkpoint != null && !s.dry ? ' · 체크포인트 ' + j.checkpointColumn + ' = ' + j.checkpoint : ''));
      }
      return true;
    }

    /** 일시 오류(네트워크 끊김) 흉내: 진행 중 배치를 롤백하고 다시 연결해 체크포인트부터 이어 읽는다. */
    transient(j) {
      j.transientDone = true;
      const lost = j.pending;
      const msg = 'ORA-03113: 통신 채널에 EOF가 있습니다 · 진행 중 배치 ' + fmt.n(lost) + '행 롤백';
      if (this.opts.errorPolicy === 'STOP') {
        this.fail(j, msg + '\n오류 시 중지 정책으로 멈춤');
        return false;
      }
      j.read -= lost;
      j.pending = 0;
      j.stall = 8;
      this.log('warn', 'WARN', msg + '\n다시 연결해 재시도 1/3 · ' + j.checkpointColumn + " > '" + j.checkpoint + "'부터 이어 읽음");
      this.emit('update');
      return true;
    }

    commitPending(j) {
      if (j && j.status === 'run' && j.pending > 0) this.commitBatch(j, j.pending);
    }

    completeJob(j) {
      const s = this.snap;
      j.status = 'done';
      j.rateNow = 0;
      const lines = [j.label + '  (' + fmt.dur(j.elapsed) + ')',
        (s.dry ? '예상 ' : '') + 'Inserted : ' + fmt.n(j.inserted),
        (s.dry ? '예상 ' : '') + 'Updated  : ' + fmt.n(j.updated),
        (s.dry ? '예상 ' : '') + 'Rejected : ' + fmt.n(j.rejected) + (j.rejected && j.errorTable && !s.dry ? '  → ' + j.errorTable : '')];
      this.log(s.dry ? 'dry' : 'done', 'DONE', lines.join('\n'));
      if (j.checkpointColumn && !s.dry) this.emit('checkpoint', { key: j.key, column: j.checkpointColumn, value: j.checkpoint, rows: j.scopeTotal, total: j.scopeTotal, status: 'done', runId: s.runId });
      s.current++;
    }

    finish() {
      const s = this.snap;
      clearInterval(this.timer);
      s.state = 'done';
      s.rate = 0;
      const rows = s.jobs.reduce((a, j) => a + j.written, 0);
      const rej = s.jobs.reduce((a, j) => a + j.rejected, 0);
      this.log('done', 'DONE', '전체 ' + s.jobs.length + '개 작업 · ' + fmt.n(rows) + '행 · ' + fmt.dur(s.elapsed) + (rej ? ' · 거부 ' + fmt.n(rej) + '행' : '') + (s.dry ? ' · Dry Run(대상 변경 없음)' : ''));
      this.emit('update');
      this.emit('end');
    }

    fail(j, message) {
      const s = this.snap;
      clearInterval(this.timer);
      if (j) {
        j.read -= j.pending;
        j.pending = 0;
        j.status = 'failed';
        j.rateNow = 0;
        if (j.checkpointColumn && !s.dry) this.emit('checkpoint', { key: j.key, column: j.checkpointColumn, value: j.checkpoint, rows: j.base + j.written, total: j.scopeTotal, status: 'stopped', runId: s.runId });
      }
      s.state = 'failed';
      s.rate = 0;
      this.log('error', 'ERROR', message + (j && j.checkpoint != null ? '\n체크포인트: ' + j.checkpointColumn + ' = ' + j.checkpoint + ' (재개하면 여기부터)' : ''));
      this.emit('update');
      this.emit('end');
    }

    pause() {
      if (this.state === 'running') {
        this.snap.state = 'pausing';
        this.emit('update');
      }
    }

    resume() {
      if (this.state !== 'paused') return;
      const s = this.snap;
      s.state = 'running';
      const j = s.jobs[s.current];
      if (j && j.status === 'paused') j.status = 'run';
      this.log('resume', 'RESUME', '이어서 실행' + (j ? ' · ' + j.label : ''));
      this.emit('update');
    }

    stop() {
      if (!this.active) return;
      const s = this.snap;
      clearInterval(this.timer);
      const j = s.jobs[s.current];
      let rolled = 0;
      if (j && (j.status === 'run' || j.status === 'paused')) {
        rolled = j.pending;
        j.read -= j.pending;
        j.pending = 0;
        j.status = 'stopped';
        j.rateNow = 0;
        if (j.checkpointColumn && !s.dry) this.emit('checkpoint', { key: j.key, column: j.checkpointColumn, value: j.checkpoint, rows: j.base + j.written, total: j.scopeTotal, status: 'stopped', runId: s.runId });
      }
      s.state = 'stopped';
      s.rate = 0;
      this.log('stop', 'STOP', '사용자가 중지' + (rolled ? ' · 진행 중 배치 ' + fmt.n(rolled) + '행 롤백' : '') +
        (j && j.checkpoint != null && !s.dry ? '\n체크포인트 저장: ' + j.checkpointColumn + ' = ' + j.checkpoint + ' — [체크포인트에서 재개]로 이어서 실행' : ''));
      this.emit('update');
      this.emit('end');
    }

    /** 전체 진행: 원본 범위 대비 끝난 행(재개 전 행 포함) */
    totals() {
      const s = this.snap;
      if (!s) return { done: 0, total: 0, pct: 0, eta: null };
      const total = s.jobs.reduce((a, j) => a + j.scopeTotal, 0);
      const done = s.jobs.reduce((a, j) => a + j.base + j.written, 0);
      const remain = total - done;
      return { done, total, pct: fmt.pct(done, total), eta: s.rate > 0 ? remain / s.rate : null };
    }

    log(cls, tag, text) {
      const entry = { at: new Date(), cls, tag, text };
      this.snap.log.push(entry);
      if (this.snap.log.length > 2000) this.snap.log.shift();
      this.emit('log', entry);
    }

    emit(type, payload) {
      try {
        if (this.listener) this.listener(type, this.snap, payload);
      } catch (e) {
        console.error(e);
      }
    }
  }

  function policyText(p) {
    return { CONTINUE: '계속 + 오류 테이블', STOP: '오류 시 중지', RETRY: '3회 재시도' }[p] || p;
  }

  MS.engine = { buildPlan, MigrationEngine, policyText, niceStep };
})();
