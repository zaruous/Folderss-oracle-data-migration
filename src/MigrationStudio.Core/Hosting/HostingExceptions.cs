using System;

namespace MigrationStudio.Core.Hosting
{
    public enum LaunchStage
    {
        Prepare,
        Plan,
        Launch,
        Connect,
        Start
    }

    public sealed class LaunchFailedException : Exception
    {
        public LaunchFailedException(LaunchStage stage, string message, Exception inner)
            : base("[" + StageName(stage) + "] " + message, inner)
        {
            Stage = stage;
        }

        public LaunchFailedException(LaunchStage stage, string message)
            : base("[" + StageName(stage) + "] " + message)
        {
            Stage = stage;
        }

        public LaunchStage Stage { get; private set; }

        private static string StageName(LaunchStage stage)
        {
            switch (stage)
            {
                case LaunchStage.Prepare: return "Prepare";
                case LaunchStage.Plan: return "Plan";
                case LaunchStage.Launch: return "Launch";
                case LaunchStage.Connect: return "Connect";
                case LaunchStage.Start: return "Start";
                default: return stage.ToString();
            }
        }
    }

    public sealed class AgentMissingException : Exception
    {
        public AgentMissingException()
            : base("에이전트 파일이 없습니다 — 플러그인을 다시 설치하세요")
        {
        }
    }

    public sealed class AgentLaunchException : Exception
    {
        public AgentLaunchException(string message) : base(message) { }
        public AgentLaunchException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class AgentExitedException : Exception
    {
        public AgentExitedException(int exitCode, string lastLogLine)
            : base("에이전트가 종료되었습니다(코드 " + exitCode + ")." + (string.IsNullOrEmpty(lastLogLine) ? "" : " 마지막 로그: " + lastLogLine))
        {
            ExitCode = exitCode;
            LastLogLine = lastLogLine;
        }

        public int ExitCode { get; private set; }
        public string LastLogLine { get; private set; }
    }

    public sealed class AgentStartException : Exception
    {
        public AgentStartException(string message) : base(message) { }
    }

    public sealed class AlreadyRunningException : Exception
    {
        public AlreadyRunningException(string runId, int pid)
            : base("이 작업은 이미 실행 중입니다(" + runId + ", PID " + pid + "). 진행 화면에 다시 붙으려면 실행 화면을 여세요.")
        {
            RunId = runId;
            Pid = pid;
        }

        public string RunId { get; private set; }
        public int Pid { get; private set; }
    }

    public sealed class ConcurrencyLimitException : Exception
    {
        public ConcurrencyLimitException(int limit)
            : base("동시 실행 한도(" + limit + ")에 도달했습니다.")
        {
            Limit = limit;
        }

        public int Limit { get; private set; }
    }
}
