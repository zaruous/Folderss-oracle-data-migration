using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MigrationStudio.Core.Hosting
{
    /// <summary>작업별 중복 실행 방지 뮤텍스 + 살아 있는 에이전트 2차 방어.</summary>
    public sealed class RunGuard : IDisposable
    {
        private readonly string _dataDirectory;
        private readonly int _maxConcurrent;
        private readonly string _jobName;
        private Mutex _mutex;
        private bool _ownsMutex;

        public RunGuard(string dataDirectory, string jobName, int maxConcurrent)
        {
            _dataDirectory = dataDirectory;
            _jobName = jobName ?? "";
            _maxConcurrent = Math.Max(1, maxConcurrent);
        }

        /// <summary>
        /// 동시 실행 한도에 세는 실행인가 — 끝난(done·stopped·failed·crashed) 실행은 에이전트가 결과 재접속을 위해
        /// --idle-exit초 더 살아 있어도 세지 않는다. 실제 Folderss에서 실패한 Dry Run 뒤 10분간 "동시 실행 한도(1)"로 막히던 원인.
        /// </summary>
        public static bool CountsTowardLimit(AgentRunInfo run)
        {
            if (run == null)
            {
                return false;
            }

            var state = run.State ?? "";
            return !(string.Equals(state, "done", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "stopped", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "crashed", StringComparison.OrdinalIgnoreCase));
        }

        public void Acquire()
        {
            var alive = AgentRuns.ListAlive(_dataDirectory).Where(CountsTowardLimit).ToList();
            if (alive.Count >= _maxConcurrent)
            {
                throw new ConcurrencyLimitException(_maxConcurrent);
            }

            var sameJob = alive.FirstOrDefault(r => string.Equals(r.JobName, _jobName, StringComparison.Ordinal));
            if (sameJob != null)
            {
                throw new AlreadyRunningException(sameJob.RunId, sameJob.Pid);
            }

            var name = MutexName(_jobName);
            _mutex = new Mutex(false, name, out _ownsMutex);
            if (!_ownsMutex)
            {
                var waited = _mutex.WaitOne(TimeSpan.FromMilliseconds(50));
                if (!waited)
                {
                    alive = AgentRuns.ListAlive(_dataDirectory).Where(CountsTowardLimit).ToList();
                    sameJob = alive.FirstOrDefault(r => string.Equals(r.JobName, _jobName, StringComparison.Ordinal));
                    if (sameJob != null)
                    {
                        throw new AlreadyRunningException(sameJob.RunId, sameJob.Pid);
                    }

                    throw new AlreadyRunningException("?", 0);
                }
            }
        }

        public void Dispose()
        {
            if (_mutex == null)
            {
                return;
            }

            try
            {
                if (_ownsMutex)
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch
            {
            }

            _mutex.Dispose();
            _mutex = null;
        }

        public static string MutexName(string jobName)
        {
            var safe = SanitizeJobName(jobName);
            return @"Local\folderss-migration-" + safe;
        }

        internal static string SanitizeJobName(string jobName)
        {
            if (string.IsNullOrEmpty(jobName))
            {
                return "job";
            }

            var sb = new StringBuilder();
            foreach (var ch in jobName)
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-')
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append('_');
                }
            }

            var text = sb.ToString();
            if (text.Length > 128)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobName))).Substring(0, 8).ToLowerInvariant();
                text = text.Substring(0, 120) + "-" + hash;
            }

            return text;
        }
    }
}
