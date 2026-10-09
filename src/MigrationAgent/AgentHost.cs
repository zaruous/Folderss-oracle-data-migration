using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Adapters.Oracle.Engine;
using MigrationStudio.Core.Engine;
using MigrationStudio.Core.Hosting;

namespace MigrationAgent
{
    internal sealed class AgentHost
    {
        private readonly string _runId;
        private readonly string _pipeName;
        private readonly string _dataDirectory;
        private readonly int _idleExitSeconds;
        private readonly int _startWaitSeconds;
        private AgentRunInfo _record;
        private StreamWriter _logWriter;
        private readonly List<LogEntry> _logRing = new List<LogEntry>();
        private readonly object _logGate = new object();
        private RunSnapshot _lastSnapshot;
        private MigrationEngine _engine;
        private Task _engineTask;
        private bool _startReceived;
        private bool _endedSent;
        private string _agentVersion;
        private string _engineState = "waiting";
        private DateTime _startedAt;
        private readonly ConcurrentQueue<AgentMessage> _outbound = new ConcurrentQueue<AgentMessage>();
        private readonly AutoResetEvent _outboundSignal = new AutoResetEvent(false);
        private Stream _activeStream;
        private readonly object _streamGate = new object();
        private CancellationTokenSource _hostCts = new CancellationTokenSource();
        // 끝난 뒤 클라이언트가 shutdown을 보냄 — 파이프를 닫지 않아도 바로 끝내라는 뜻
        private volatile bool _shutdownRequested;

        public AgentHost(string runId, string pipeName, string dataDirectory, int idleExitSeconds, int startWaitSeconds)
        {
            _runId = runId;
            _pipeName = pipeName;
            _dataDirectory = dataDirectory;
            _idleExitSeconds = idleExitSeconds;
            _startWaitSeconds = startWaitSeconds;
            _agentVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        }

        public async Task<int> RunAsync()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, __) => WriteCrash("process-exit");
            try
            {
                Directory.CreateDirectory(AgentRunFiles.RunsDirectory(_dataDirectory));
                Directory.CreateDirectory(AgentRunFiles.LogsDirectory(_dataDirectory));
                Directory.CreateDirectory(Path.Combine(_dataDirectory, "checkpoints"));

                _record = new AgentRunInfo
                {
                    RunId = _runId,
                    Pid = Process.GetCurrentProcess().Id,
                    Pipe = _pipeName,
                    StartedAt = DateTime.Now,
                    AgentVersion = _agentVersion,
                    State = "waiting",
                    JobName = ""
                };
                WriteRecord();

                _logWriter = new StreamWriter(new FileStream(AgentRunFiles.LogFilePath(_dataDirectory, _runId), FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                if (string.Equals(Environment.GetEnvironmentVariable("MIGRATION_AGENT_TEST_ADAPTER"), "memory", StringComparison.OrdinalIgnoreCase))
                {
                    WriteLog("WARN", "시험 어댑터로 실행 중");
                }

                var pump = Task.Run(() => OutboundPump(_hostCts.Token));
                var exit = await AcceptLoopAsync().ConfigureAwait(false);
                _hostCts.Cancel();
                await pump.ConfigureAwait(false);
                return exit;
            }
            catch (Exception ex)
            {
                WriteCrash(ex.Message);
                WriteLog("ERROR", ex.ToString());
                return 40;
            }
        }

        private async Task<int> AcceptLoopAsync()
        {
            var bootDeadline = DateTime.UtcNow.AddSeconds(_startWaitSeconds);
            while (!_hostCts.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                if (OperatingSystem.IsWindows())
                {
                    server = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, AgentPipeSecurity.CurrentUserOnly());
                }
                else
                {
                    server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                }

                using (server)
                {
                    var wait = server.WaitForConnectionAsync(_hostCts.Token);
                    if (!_startReceived)
                    {
                        var remaining = bootDeadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero)
                        {
                            return 30;
                        }

                        var completed = await Task.WhenAny(wait, Task.Delay(remaining)).ConfigureAwait(false);
                        if (completed != wait)
                        {
                            return 30;
                        }
                    }
                    else if (_endedSent)
                    {
                        // 끝난 뒤: --idle-exit초 안에 결과를 보러 다시 붙는 클라이언트가 없으면 나간다(결과·로그는 runs/·logs/에 남아 있다).
                        // 예전에는 클라이언트가 떠난 뒤 그냥 잠들어 그동안 아무도 붙을 수 없었다.
                        var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(_idleExitSeconds), _hostCts.Token)).ConfigureAwait(false);
                        if (completed != wait)
                        {
                            return ExitCode(_engineState);
                        }
                    }
                    else
                    {
                        await wait.ConfigureAwait(false);
                    }

                    lock (_streamGate)
                    {
                        _activeStream = server;
                    }

                    await SendAsync(new AgentMessage
                    {
                        Type = AgentMessageTypes.Welcome,
                        Protocol = AgentProtocol.Version,
                        Agent = _agentVersion,
                        Pid = Process.GetCurrentProcess().Id,
                        State = _engineState,
                        RunId = _runId,
                        StartedAt = (_startedAt == default ? _record.StartedAt : _startedAt).ToString("o", CultureInfo.InvariantCulture)
                    }).ConfigureAwait(false);

                    if (_lastSnapshot != null)
                    {
                        await SendAsync(new AgentMessage { Type = AgentMessageTypes.Snapshot, Snapshot = _lastSnapshot }).ConfigureAwait(false);
                    }

                    await HandleClientAsync(server).ConfigureAwait(false);
                    lock (_streamGate)
                    {
                        _activeStream = null;
                    }
                }

                if (_shutdownRequested)
                {
                    return ExitCode(_engineState);
                }

                // 클라이언트가 떠나면(창 닫힘 등) 다음 바퀴에서 새 파이프를 열어 다시 붙을 수 있게 한다.
                // 실행이 끝난 뒤라면 위에서 --idle-exit초만 기다린다.
            }

            return 0;
        }

        private async Task HandleClientAsync(Stream stream)
        {
            var buffer = new StringBuilder();
            while (true)
            {
                string line;
                if (!AgentMessageCodec.TryReadLine(stream, buffer, out line))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                AgentMessage message;
                try
                {
                    message = AgentMessageCodec.Deserialize(line);
                }
                catch
                {
                    await SendAsync(new AgentMessage { Type = AgentMessageTypes.Error, Code = "protocol", Message = "메시지 형식 오류" }).ConfigureAwait(false);
                    continue;
                }

                if (message == null)
                {
                    continue;
                }

                await HandleMessageAsync(message).ConfigureAwait(false);
                if (_shutdownRequested)
                {
                    return;
                }
            }
        }

        private async Task HandleMessageAsync(AgentMessage message)
        {
            switch (message.Type)
            {
                case AgentMessageTypes.Hello:
                    if (message.Protocol != AgentProtocol.Version)
                    {
                        await SendAsync(new AgentMessage { Type = AgentMessageTypes.Error, Code = "protocol", Message = "프로토콜 버전 불일치" }).ConfigureAwait(false);
                    }
                    break;
                case AgentMessageTypes.Start:
                    if (_startReceived)
                    {
                        await SendAsync(new AgentMessage { Type = AgentMessageTypes.Error, Code = "busy", Message = "이미 시작됨" }).ConfigureAwait(false);
                        break;
                    }

                    if (message.Spec == null)
                    {
                        await SendAsync(new AgentMessage { Type = AgentMessageTypes.Error, Code = "start-failed", Message = "실행 사양 없음" }).ConfigureAwait(false);
                        break;
                    }

                    _startReceived = true;
                    _record.JobName = message.Spec.Job != null ? message.Spec.Job.JobName : "";
                    _engineState = "running";
                    _record.State = "running";
                    _startedAt = DateTime.Now;
                    WriteRecord();
                    StartEngine(message.Spec);
                    break;
                case AgentMessageTypes.Attach:
                    ReplayLogs(message.LogTail > 0 ? message.LogTail : 2000);
                    break;
                case AgentMessageTypes.Pause:
                    _engine?.Pause();
                    break;
                case AgentMessageTypes.Resume:
                    _engine?.Resume();
                    break;
                case AgentMessageTypes.Stop:
                    _engine?.Stop();
                    break;
                case AgentMessageTypes.Ping:
                    await SendAsync(new AgentMessage { Type = AgentMessageTypes.Pong }).ConfigureAwait(false);
                    break;
                case AgentMessageTypes.Shutdown:
                    if (!_endedSent)
                    {
                        await SendAsync(new AgentMessage { Type = AgentMessageTypes.Error, Code = "busy", Message = "실행 중" }).ConfigureAwait(false);
                    }
                    else
                    {
                        _shutdownRequested = true;
                        _hostCts.Cancel();
                    }
                    break;
            }
        }

        private void StartEngine(RunSpec spec)
        {
            var listener = new AgentRunListener(this);
            ISourceFactory source;
            ITargetFactory target;
            ICheckpointStore store;
            IRunRecorder recorder;
            if (string.Equals(Environment.GetEnvironmentVariable("MIGRATION_AGENT_TEST_ADAPTER"), "memory", StringComparison.OrdinalIgnoreCase))
            {
                MemoryTestBootstrap.Create(spec, out source, out target, out store);
                // 시험 어댑터에는 Oracle이 없다 — 실행 기록(MIG_RUN)도 쓰지 않는다. 전에는 기록기가 빈 접속으로 연결 문자열을 만들다
                // "Value cannot be null"로 실행 전체가 실패했고, 시험이 끝남만 확인해서 눈에 띄지 않았다.
                recorder = null;
            }
            else
            {
                source = new OracleSourceFactory(spec.Source);
                target = new OracleTargetFactory(spec.Target, spec.ControlPrefix, spec.RunId);
                var local = string.Equals(spec.CheckpointStore, "LOCAL", StringComparison.OrdinalIgnoreCase);
                store = local
                    ? new LocalCheckpointStore(Path.Combine(spec.DataDirectory, "checkpoints"))
                    : (ICheckpointStore)new OracleCheckpointStore(spec.Target, spec.ControlPrefix);
                // 로컬 파일 체크포인트는 대상에 MIG_ 제어 테이블이 없거나 못 만들 때 고르는 것 — 실행 기록(MIG_RUN)도 대상에 쓰지 않는다
                recorder = local ? null : new OracleRunRecorder(spec.Target, spec.ControlPrefix);
            }

            _engine = new MigrationEngine(spec, source, target, store, listener, new SystemRunClock(), recorder);
            _engineTask = Task.Run(() => _engine.RunAsync(_hostCts.Token));
        }

        private void ReplayLogs(int tail)
        {
            List<LogEntry> copy;
            lock (_logGate)
            {
                copy = new List<LogEntry>(_logRing);
            }

            if (tail > 0 && copy.Count > tail)
            {
                copy = copy.GetRange(copy.Count - tail, tail);
            }

            foreach (var entry in copy)
            {
                Enqueue(new AgentMessage { Type = AgentMessageTypes.Log, Entry = entry });
            }
        }

        internal void OnSnapshot(RunSnapshot snapshot)
        {
            _lastSnapshot = snapshot;
            // 동기화는 몇 시간씩 돈다 — 주기가 끝날 때마다 기록 파일에 남겨 두면, 붙지 않은 상태에서도 "마지막 주기가 언제였나"를 알 수 있다.
            if (snapshot != null && snapshot.Sync != null && snapshot.Sync.LastCycleAt.HasValue && snapshot.Sync.Cycle != _record.Cycle)
            {
                _record.Cycle = snapshot.Sync.Cycle;
                _record.LastCycleAt = snapshot.Sync.LastCycleAt;
                WriteRecord();
            }
            EnqueueSnapshot(new AgentMessage { Type = AgentMessageTypes.Snapshot, Snapshot = snapshot });
        }

        internal void OnLog(LogEntry entry)
        {
            lock (_logGate)
            {
                _logRing.Add(entry);
                while (_logRing.Count > 2000)
                {
                    _logRing.RemoveAt(0);
                }
            }

            WriteLog(entry.Tag, entry.Text);
            Enqueue(new AgentMessage { Type = AgentMessageTypes.Log, Entry = entry });
        }

        internal void OnCheckpoint(CheckpointRecord record)
        {
            Enqueue(new AgentMessage { Type = AgentMessageTypes.Checkpoint, Record = record });
        }

        internal void OnEnd(RunSnapshot final)
        {
            _lastSnapshot = final;
            _engineState = MapExitReason(final != null ? final.State : _engine.State);
            _record.State = _engineState;
            _record.Message = _engineState;
            WriteRecord();
            _endedSent = true;
            Enqueue(new AgentMessage { Type = AgentMessageTypes.End, Snapshot = final, ExitReason = _engineState });
        }

        private void Enqueue(AgentMessage message)
        {
            _outbound.Enqueue(message);
            _outboundSignal.Set();
        }

        private void EnqueueSnapshot(AgentMessage message)
        {
            var filtered = new ConcurrentQueue<AgentMessage>();
            AgentMessage item;
            while (_outbound.TryDequeue(out item))
            {
                if (!string.Equals(item.Type, AgentMessageTypes.Snapshot, StringComparison.Ordinal))
                {
                    filtered.Enqueue(item);
                }
            }

            while (filtered.TryDequeue(out item))
            {
                _outbound.Enqueue(item);
            }

            _outbound.Enqueue(message);
            _outboundSignal.Set();
        }

        private async Task SendAsync(AgentMessage message)
        {
            Enqueue(message);
            await Task.Delay(10).ConfigureAwait(false);
        }

        private async Task OutboundPump(CancellationToken ct)
        {
            var buffer = new StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                AgentMessage message;
                if (!_outbound.TryDequeue(out message))
                {
                    _outboundSignal.WaitOne(100);
                    continue;
                }

                Stream stream;
                lock (_streamGate)
                {
                    stream = _activeStream;
                }

                if (stream == null)
                {
                    continue;
                }

                try
                {
                    var bytes = AgentMessageCodec.Serialize(message);
                    await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private static int ExitCode(string reason)
        {
            if (reason == "stopped") return 10;
            if (reason == "failed") return 20;
            return 0;
        }

        private static string MapExitReason(string engineState)
        {
            if (engineState == "stopped") return "stopped";
            if (engineState == "failed") return "failed";
            return "done";
        }

        private void WriteRecord()
        {
            AgentRunFiles.WriteAtomic(AgentRunFiles.RunFilePath(_dataDirectory, _runId), _record);
        }

        private void WriteCrash(string message)
        {
            try
            {
                // 끝난 뒤의 정상 종료(End를 보냈고 idle-exit·shutdown으로 나감)는 사고가 아니다 — 실제 Folderss에서 완료된 실행이
                // 기록에는 "crashed · process-exit"로 남던 원인. 끝나기 전에 프로세스가 사라질 때만 남긴다.
                if (_record == null || _endedSent)
                {
                    return;
                }

                _record.State = "crashed";
                _record.Message = message;
                WriteRecord();
            }
            catch
            {
            }
        }

        private void WriteLog(string tag, string text)
        {
            var entry = new LogEntry { At = DateTime.Now, Tag = tag, Text = text };
            _logWriter?.WriteLine(AgentLogFormat.FormatLine(entry));
        }

        private sealed class AgentRunListener : IRunListener
        {
            private readonly AgentHost _host;

            public AgentRunListener(AgentHost host)
            {
                _host = host;
            }

            public void OnSnapshot(RunSnapshot snapshot) { _host.OnSnapshot(snapshot); }
            public void OnLog(LogEntry entry) { _host.OnLog(entry); }
            public void OnCheckpoint(CheckpointRecord record) { _host.OnCheckpoint(record); }
            public void OnEnd(RunSnapshot final) { _host.OnEnd(final); }
        }
    }
}
