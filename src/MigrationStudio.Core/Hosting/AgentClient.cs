using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MigrationStudio.Core.Engine;

namespace MigrationStudio.Core.Hosting
{
    public sealed class AgentClient : IAgentClient
    {
        private readonly Process _process;
        private readonly string _dataDirectory;
        private readonly LaunchLogBuffer _launchLog = new LaunchLogBuffer();
        private NamedPipeClientStream _pipe;
        private StreamWriter _writer;
        private Stream _readerStream;
        private readonly StringBuilder _readBuffer = new StringBuilder();
        private readonly object _sendGate = new object();
        private CancellationTokenSource _readLoopCancel;
        private Task _readLoop;
        private bool _disposed;
        private RunSnapshot _lastSnapshot;
        private RunGuard _runGuard;
        private readonly ConcurrentQueue<AgentMessage> _inbound = new ConcurrentQueue<AgentMessage>();
        private readonly AutoResetEvent _inboundSignal = new AutoResetEvent(false);

        public event Action<RunSnapshot> Snapshot;
        public event Action<LogEntry> Log;
        public event Action<CheckpointRecord> Checkpoint;
        public event Action<RunSnapshot, string> Ended;
        public event Action<string> Disconnected;

        private AgentClient(Process process, string runId, string pipeName, string dataDirectory)
        {
            _process = process;
            RunId = runId;
            PipeName = pipeName;
            _dataDirectory = dataDirectory;
        }

        public int Pid { get { return _process != null && !_process.HasExited ? _process.Id : 0; } }
        public string RunId { get; private set; }
        public string PipeName { get; private set; }

        public static AgentClient Launch(AgentInstall install, string runId, string dataDirectory, int hostPid, bool killOnHostExit, ILaunchLog log)
        {
            if (install == null || string.IsNullOrEmpty(install.ExePath))
            {
                throw new AgentLaunchException("에이전트 실행 파일이 없습니다.");
            }

            if (!AgentPipeNames.IsValidRunId(runId))
            {
                throw new AgentLaunchException("RUN_ID 형식이 올바르지 않습니다.");
            }

            var pipe = AgentPipeNames.ForRun(runId);
            var args = "--run " + runId + " --pipe " + pipe + " --data \"" + dataDirectory + "\" --parent " + hostPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var start = new ProcessStartInfo
            {
                FileName = install.ExePath,
                Arguments = args,
                WorkingDirectory = install.Directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            Process process;
            try
            {
                process = Process.Start(start);
            }
            catch (Exception ex)
            {
                throw new AgentLaunchException("에이전트 프로세스를 시작하지 못했습니다: " + ex.Message, ex);
            }

            if (process == null)
            {
                throw new AgentLaunchException("에이전트 프로세스를 시작하지 못했습니다.");
            }

            var client = new AgentClient(process, runId, pipe, dataDirectory);
            client.PumpStdio(log ?? client._launchLog);
            if (killOnHostExit)
            {
                JobObject.TryAssignKillOnClose(process, line =>
                {
                    if (log != null) log.Append(line);
                });
            }

            return client;
        }

        public static AgentClient Attach(AgentRunInfo record, string dataDirectory)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            Process process;
            try
            {
                process = Process.GetProcessById(record.Pid);
            }
            catch
            {
                var last = !string.IsNullOrEmpty(dataDirectory)
                    ? AgentLogFormat.ReadLastLines(AgentRunFiles.LogFilePath(dataDirectory, record.RunId), 1)
                    : null;
                throw new AgentExitedException(-1, last);
            }

            return new AgentClient(process, record.RunId, record.Pipe, dataDirectory);
        }

        public async Task ConnectAsync(TimeSpan timeout, CancellationToken ct)
        {
            EnsureNotDisposed();
            if (_process != null && _process.HasExited)
            {
                throw CreateExitedException();
            }

            _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _pipe.ConnectAsync(50, ct).ConfigureAwait(false);
                    break;
                }
                catch (TimeoutException)
                {
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }

                if (_process != null && _process.HasExited)
                {
                    throw CreateExitedException();
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new LaunchFailedException(LaunchStage.Connect, "에이전트 파이프 연결 시간 초과");
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            _readerStream = _pipe;
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            StartReadLoop();
            await SendAsync(new AgentMessage { Type = AgentMessageTypes.Hello, Protocol = AgentProtocol.Version, Client = "MigrationStudio 1.0.0" }, ct).ConfigureAwait(false);
            var welcome = await WaitForTypeAsync(AgentMessageTypes.Welcome, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (welcome == null || welcome.Protocol != AgentProtocol.Version)
            {
                throw new LaunchFailedException(LaunchStage.Connect, "에이전트 프로토콜이 맞지 않습니다.");
            }
        }

        public async Task StartAsync(RunSpec spec, CancellationToken ct)
        {
            EnsureNotDisposed();
            await SendAsync(new AgentMessage { Type = AgentMessageTypes.Start, Spec = spec }, ct).ConfigureAwait(false);
            var response = await WaitForAnyAsync(new[] { AgentMessageTypes.Snapshot, AgentMessageTypes.Error }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (response != null && response.Type == AgentMessageTypes.Error)
            {
                throw new AgentStartException(response.Message ?? response.Code ?? "start-failed");
            }
        }

        public async Task WaitFirstSnapshotAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (_lastSnapshot != null)
            {
                return;
            }

            var response = await WaitForTypeAsync(AgentMessageTypes.Snapshot, timeout, ct).ConfigureAwait(false);
            if (response == null)
            {
                throw new LaunchFailedException(LaunchStage.Start, "첫 진행 스냅숏을 받지 못했습니다.");
            }
        }

        public async Task AttachAsync(int logTail, CancellationToken ct)
        {
            EnsureNotDisposed();
            await SendAsync(new AgentMessage { Type = AgentMessageTypes.Attach, LogTail = logTail }, ct).ConfigureAwait(false);
        }

        public void Pause()
        {
            SendFireAndForget(new AgentMessage { Type = AgentMessageTypes.Pause });
        }

        public void Resume()
        {
            SendFireAndForget(new AgentMessage { Type = AgentMessageTypes.Resume });
        }

        public void Stop()
        {
            SendFireAndForget(new AgentMessage { Type = AgentMessageTypes.Stop });
        }

        public async Task ShutdownAsync()
        {
            EnsureNotDisposed();
            await SendAsync(new AgentMessage { Type = AgentMessageTypes.Shutdown }, CancellationToken.None).ConfigureAwait(false);
        }

        internal void BindRunGuard(RunGuard guard)
        {
            _runGuard = guard;
        }

        public void ReleaseRunGuard()
        {
            if (_runGuard == null)
            {
                return;
            }

            _runGuard.Dispose();
            _runGuard = null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopReadLoop();
            try
            {
                if (_writer != null)
                {
                    _writer.Dispose();
                }
            }
            catch
            {
            }

            try
            {
                if (_pipe != null)
                {
                    _pipe.Dispose();
                }
            }
            catch
            {
            }
        }

        private void PumpStdio(ILaunchLog log)
        {
            if (_process == null)
            {
                return;
            }

            _process.OutputDataReceived += (s, e) => { if (e.Data != null) log.Append(e.Data); };
            _process.ErrorDataReceived += (s, e) => { if (e.Data != null) log.Append(e.Data); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        private AgentExitedException CreateExitedException()
        {
            var code = _process != null ? _process.ExitCode : -1;
            var last = _dataDirectory != null ? AgentLogFormat.ReadLastLines(AgentRunFiles.LogFilePath(_dataDirectory, RunId), 1) : null;
            if (string.IsNullOrEmpty(last))
            {
                last = _launchLog.Tail();
            }

            return new AgentExitedException(code, last);
        }

        private void StartReadLoop()
        {
            _readLoopCancel = new CancellationTokenSource();
            _readLoop = Task.Run(() => ReadLoop(_readLoopCancel.Token));
        }

        private void StopReadLoop()
        {
            if (_readLoopCancel == null)
            {
                return;
            }

            try
            {
                _readLoopCancel.Cancel();
            }
            catch
            {
            }
        }

        private void ReadLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && _readerStream != null)
                {
                    string line;
                    if (!AgentMessageCodec.TryReadLine(_readerStream, _readBuffer, out line))
                    {
                        break;
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
                        continue;
                    }

                    if (message == null || string.IsNullOrEmpty(message.Type))
                    {
                        continue;
                    }

                    _inbound.Enqueue(message);
                    _inboundSignal.Set();
                    Dispatch(message);
                }
            }
            catch
            {
            }
            finally
            {
                var reason = "disconnected";
                if (_process != null)
                {
                    try
                    {
                        if (_process.HasExited)
                        {
                            reason = "exitCode=" + _process.ExitCode;
                        }
                    }
                    catch
                    {
                    }
                }

                RaiseDisconnected(reason);
            }
        }

        private void Dispatch(AgentMessage message)
        {
            switch (message.Type)
            {
                case AgentMessageTypes.Snapshot:
                    _lastSnapshot = message.Snapshot;
                    Raise(Snapshot, message.Snapshot);
                    break;
                case AgentMessageTypes.Log:
                    Raise(Log, message.Entry);
                    break;
                case AgentMessageTypes.Checkpoint:
                    Raise(Checkpoint, message.Record);
                    break;
                case AgentMessageTypes.End:
                    _lastSnapshot = message.Snapshot;
                    Raise(Ended, message.Snapshot, message.ExitReason);
                    break;
            }
        }

        private static void Raise<T>(Action<T> handler, T arg)
        {
            if (handler == null)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    handler(arg);
                }
                catch
                {
                }
            });
        }

        private static void Raise<T1, T2>(Action<T1, T2> handler, T1 arg1, T2 arg2)
        {
            if (handler == null)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    handler(arg1, arg2);
                }
                catch
                {
                }
            });
        }

        private void RaiseDisconnected(string reason)
        {
            var handler = Disconnected;
            if (handler == null)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    handler(reason);
                }
                catch
                {
                }
            });
        }

        private Task SendAsync(AgentMessage message, CancellationToken ct)
        {
            EnsureNotDisposed();
            var bytes = AgentMessageCodec.Serialize(message);
            lock (_sendGate)
            {
                if (_pipe == null)
                {
                    throw new InvalidOperationException("파이프가 연결되지 않았습니다.");
                }
            }

            return _pipe.WriteAsync(bytes, 0, bytes.Length, ct);
        }

        private void SendFireAndForget(AgentMessage message)
        {
            try
            {
                SendAsync(message, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        private Task<AgentMessage> WaitForTypeAsync(string type, TimeSpan timeout, CancellationToken ct)
        {
            return WaitForAnyAsync(new[] { type }, timeout, ct);
        }

        private Task<AgentMessage> WaitForAnyAsync(string[] types, TimeSpan timeout, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    AgentMessage message;
                    while (_inbound.TryDequeue(out message))
                    {
                        if (message != null && Array.IndexOf(types, message.Type) >= 0)
                        {
                            return message;
                        }
                    }

                    var wait = (int)Math.Min(200, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (wait <= 0)
                    {
                        break;
                    }

                    _inboundSignal.WaitOne(wait);
                }

                return (AgentMessage)null;
            }, ct);
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AgentClient));
            }
        }
    }
}
